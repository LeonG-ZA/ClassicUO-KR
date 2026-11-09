// SPDX-License-Identifier: BSD-2-Clause
//
// GPU terrain, phase 1: the layer textures of the KR terrains in view, packed
// into one atlas, and a table the shader's KR_TERRAIN mode reads to compose a
// land tile itself - per screen pixel, at the textures' own resolution -
// instead of the CPU baking a 44 px tile for every cell of every terrain's
// repeat (the cold-cell stutter, and the low resolution when zoomed in).
//
// Kept to what FNA runs everywhere (Direct3D, OpenGL, Vulkan; entry-level
// GPUs): one Color texture, 2048 square, no mipmaps, no texture arrays; the
// shader wraps each layer inside its cell itself.

using System;
using System.Collections.Generic;
using ClassicUO.KR.Textures;
using ClassicUO.KR.World;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Terrain
{
    public sealed class KrTerrainAtlas : IDisposable
    {
        public const int Size = 2048;
        public const int Cell = 256;
        public const int Columns = Size / Cell;
        /// <summary>
        /// Texture cells: the top six rows. The bottom two (512 x 2048 px)
        /// hold border tiles' records, 8 x 5 texels each: rows 0-3 two 4 x 4
        /// weight blocks (x 0-3 layers 1-4, x 4-7 layers 5-7, RGBA, at the
        /// texels -1..2 the interpolation reads, so the GPU's bilinear filter
        /// reproduces KrTerrainBlend.Weight in one fetch a block); row 4 the
        /// table entries as bytes (x 0 base and layers 1-3, x 1 layers 4-7).
        /// </summary>
        public const int MaxCells = Columns * 6;

        public const int WeightTop = 6 * Cell;
        public const int RecordWidth = 8, RecordHeight = 5;
        public const int RecordColumns = Size / RecordWidth;
        public const int MaxSlots = RecordColumns * ((Size - WeightTop) / RecordHeight);

        /// <summary>Layers a border tile can lay over its ground.</summary>
        public const int MaxLayers = 7;

        /// <summary>Entries in the shader's table (IsometricWorld.fx KrTerrainTable); 63 means none.</summary>
        public const int MaxEntries = 63;
        public const int NoEntry = 63;

        private readonly GraphicsDevice _device;
        private readonly Dictionary<DdsImage, int> _cells = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<int, int> _entries = [];
        private readonly int[] _periods = new int[MaxEntries];
        private readonly Dictionary<long, int> _slots = [];
        private readonly Stack<int> _freeSlots = new();
        private readonly uint[] _block = new uint[RecordWidth * RecordHeight];

        // The weight region's pixels on the CPU: blocks are written here and
        // the block rows touched since the last flush go up in one SetData
        // (a SetData per 4 x 4 block cost more than composing the tile).
        private readonly uint[] _weights = new uint[Size * (Size - WeightTop)];
        private int _dirtyFirst = int.MaxValue, _dirtyLast = -1;
        private int _nextSlot;

        public KrTerrainAtlas(GraphicsDevice device)
        {
            _device = device;
        }

        public Texture2D Texture { get; private set; }

        /// <summary>
        /// Per entry: (top cell, ground cell, mask cell, repeats packed as
        /// top + ground x 64 + mask x 4096). Uploaded to the effect each frame.
        /// </summary>
        public Vector4[] Table { get; } = new Vector4[MaxEntries + 1];

        /// <summary>
        /// The table entry for a composite terrain, adding its layers to the
        /// atlas on first use; -1 when the atlas or table is full, in which
        /// case the tile stays on the CPU path.
        /// </summary>
        public int Entry(int terrainId, KrTerrainArt.CompositeLayers layers)
        {
            if (_entries.TryGetValue(terrainId, out int entry))
            {
                return entry;
            }

            if (_entries.Count >= MaxEntries || layers.TopRepeat > 63 || layers.GroundRepeat > 63 || layers.MaskRepeat > 63)
            {
                return -1;
            }

            int top = CellFor(layers.Top), ground = CellFor(layers.Ground), mask = CellFor(layers.Mask);

            if (top < 0 || ground < 0 || mask < 0)
            {
                return -1;
            }

            entry = _entries.Count;
            _entries[terrainId] = entry;
            Table[entry] = new Vector4(top, ground, mask, layers.TopRepeat + (layers.GroundRepeat * 64) + (layers.MaskRepeat * 4096));
            _periods[entry] = Lcm(Lcm(layers.TopRepeat, layers.GroundRepeat), layers.MaskRepeat);

            return entry;
        }

        /// <summary>
        /// A tile's origin for the shader, wrapped to its terrain's full repeat
        /// (the least common multiple of the layer repeats, so the wrap is
        /// exact) and packed as x + y x 4096.
        /// </summary>
        public float PackOrigin(int entry, int tileX, int tileY) => PackOrigin(tileX, tileY, [entry]);

        /// <summary>
        /// As above for a border tile: wrapped to the common multiple of every
        /// terrain it shows, so each one's own repeat stays continuous with
        /// its neighbours' tiles.
        /// </summary>
        public float PackOrigin(int tileX, int tileY, ReadOnlySpan<int> entries)
        {
            int p = 1;

            foreach (int e in entries)
            {
                if (e >= 0 && e < MaxEntries)
                {
                    p = Lcm(p, _periods[e]);
                }
            }

            int x = ((tileX % p) + p) % p;
            int y = ((tileY % p) + p) % p;

            return x + (y * 4096f);
        }

        /// <summary>
        /// Writes a border tile's record - its base entry, up to seven layers'
        /// entries and their weights - and returns its slot, or -1. Slots are
        /// kept per tile position; when they run out, those of tiles far from
        /// this one (chunks long unloaded) are given back.
        /// </summary>
        public int TileRecord(int tileX, int tileY, int baseEntry, ReadOnlySpan<int> layerEntries, ReadOnlySpan<KrTerrainBlend.WeightGrid> grids)
        {
            if (Texture == null || layerEntries.Length > MaxLayers)
            {
                return -1;
            }

            long key = ((long)tileX << 32) | (uint)tileY;

            if (!_slots.TryGetValue(key, out int slot))
            {
                if (_freeSlots.Count == 0 && _nextSlot >= MaxSlots)
                {
                    Reclaim(tileX, tileY);
                }

                slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot < MaxSlots ? _nextSlot++ : -1;

                if (slot < 0)
                {
                    return -1;
                }

                _slots[key] = slot;
            }

            Array.Clear(_block);

            for (int ty = -1; ty <= 2; ty++)
            {
                for (int tx = -1; tx <= 2; tx++)
                {
                    for (int block = 0; block < 2; block++)
                    {
                        uint texel = 0;

                        for (int c = 0; c < 4; c++)
                        {
                            int layer = (block * 4) + c;

                            if (layer < grids.Length)
                            {
                                texel |= ToByte(grids[layer].TexelAt(tx, ty)) << (c * 8);
                            }
                        }

                        _block[((ty + 1) * RecordWidth) + (block * 4) + tx + 1] = texel;
                    }
                }
            }

            // Row 4: entries as bytes, 63 for none.
            Span<int> e = stackalloc int[8];
            e.Fill(NoEntry);
            e[0] = baseEntry;

            for (int i = 0; i < layerEntries.Length; i++)
            {
                e[i + 1] = layerEntries[i];
            }

            _block[4 * RecordWidth] = (uint)e[0] | ((uint)e[1] << 8) | ((uint)e[2] << 16) | ((uint)e[3] << 24);
            _block[(4 * RecordWidth) + 1] = (uint)e[4] | ((uint)e[5] << 8) | ((uint)e[6] << 16) | ((uint)e[7] << 24);

            int bx = (slot % RecordColumns) * RecordWidth, by = (slot / RecordColumns) * RecordHeight;

            for (int r = 0; r < RecordHeight; r++)
            {
                Array.Copy(_block, r * RecordWidth, _weights, ((by + r) * Size) + bx, RecordWidth);
            }

            _dirtyFirst = Math.Min(_dirtyFirst, by);
            _dirtyLast = Math.Max(_dirtyLast, by + RecordHeight - 1);

            return slot;
        }

        /// <summary>Uploads the weight rows written since the last call (once per mesh build).</summary>
        public void FlushWeights()
        {
            if (_dirtyLast < 0 || Texture == null)
            {
                return;
            }

            int rows = _dirtyLast - _dirtyFirst + 1;
            Texture.SetData(0, new Rectangle(0, WeightTop + _dirtyFirst, Size, rows), _weights, _dirtyFirst * Size, rows * Size);
            _dirtyFirst = int.MaxValue;
            _dirtyLast = -1;
        }

        private static uint ToByte(float w) => (uint)Math.Clamp((int)MathF.Round(w * 255f), 0, 255);

        /// <summary>
        /// Tiles whose slots <see cref="Reclaim"/> gave back since the last
        /// drain, packed (x &lt;&lt; 32 | y). A mesh built earlier still holds
        /// their old slot, which now carries another tile's record: those
        /// meshes must be rebuilt, or the tiles draw another tile's terrains
        /// (Britain at 1438,1587 came out a patchwork of planks, pavers and
        /// roofing after riding about town).
        /// </summary>
        public List<long> Reclaimed { get; } = [];

        private void Reclaim(int tileX, int tileY)
        {
            List<long> far = [];

            foreach (KeyValuePair<long, int> kv in _slots)
            {
                int x = (int)(kv.Key >> 32), y = (int)kv.Key;

                if (Math.Abs(x - tileX) > 64 || Math.Abs(y - tileY) > 64)
                {
                    far.Add(kv.Key);
                }
            }

            foreach (long k in far)
            {
                _freeSlots.Push(_slots[k]);
                _slots.Remove(k);
                Reclaimed.Add(k);
            }
        }

        private int CellFor(DdsImage image)
        {
            if (_cells.TryGetValue(image, out int cell))
            {
                return cell;
            }

            if (_cells.Count >= MaxCells)
            {
                return -1;
            }

            Texture ??= new Texture2D(_device, Size, Size, false, SurfaceFormat.Color);

            cell = _cells.Count;
            uint[] pixels = image.Width == Cell && image.Height == Cell
                ? image.Pixels
                : KrTerrainArt.Resample(image.Pixels, image.Width, image.Height, Cell, Cell);

            Texture.SetData(0, new Rectangle((cell % Columns) * Cell, (cell / Columns) * Cell, Cell, Cell), pixels, 0, pixels.Length);
            _cells[image] = cell;

            return cell;
        }

        private static int Lcm(int a, int b)
        {
            a = Math.Max(1, a);
            b = Math.Max(1, b);
            int x = a, y = b;

            while (y != 0)
            {
                (x, y) = (y, x % y);
            }

            long l = (long)a / x * b;

            return l > 4095 ? 4095 : (int)l;
        }

        public void Dispose()
        {
            Texture?.Dispose();
            Texture = null;
        }
    }
}
