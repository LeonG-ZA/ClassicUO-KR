// SPDX-License-Identifier: BSD-2-Clause
//
// Sprite cache for Kingdom Reborn land tiles.
//
// This mirrors Arts and Texmaps - an atlas plus a lazily filled table of
// SpriteInfo - but is keyed by (land tile id, position within the texture's
// period) rather than by graphic alone, because a KR terrain texture is spread
// across several tiles instead of being repeated in each one.
//
// Variants are built on demand, so only the terrains actually on screen cost
// anything: a handful of terrains times at most 64 crops each.

using System;
using ClassicUO.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Terrain
{
    public sealed class KrTerrain
    {
        /// <summary>ClassicUO's land art is a 44x44 diamond.</summary>
        public const int LandSize = 44;

        private const int LandGraphicCount = 0x4000;

        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[][] _land = new SpriteInfo[LandGraphicCount][];
        private readonly SpriteInfo[][] _stretched = new SpriteInfo[LandGraphicCount][];
        private readonly int[] _period = new int[LandGraphicCount];

        private readonly GraphicsDevice _device;
        private bool _waterTried;

        /// <summary>The GPU terrain path's layer atlas and table.</summary>
        public KrTerrainAtlas Atlas { get; }

        public KrTerrain(GraphicsDevice device)
        {
            _device = device;
            Atlas = new KrTerrainAtlas(device);
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
        }

        /// <summary>
        /// KR's water terrain (definition 5): slot 0 02000051_water is the
        /// colour, its base 01000012_lava_normal a normal map, and slot 1
        /// 01000017_cube3 the sky it reflects - dark navy with bright specks,
        /// which is where KR's glints come from. Null until loaded, or when
        /// KR terrain is not available.
        /// </summary>
        public Texture2D WaterColor { get; private set; }
        public Texture2D WaterNormal { get; private set; }
        public Texture2D WaterReflect { get; private set; }

        /// <summary>02000700_Swamp_Water_A, for swamp and sewer water statics.</summary>
        public Texture2D WaterSwamp { get; private set; }

        /// <summary>Terrain 70's bump (02000703_Swamp_Water_D) and reflection (02000702_Swamp_Water_C).</summary>
        public Texture2D SwampBump { get; private set; }
        public Texture2D SwampReflect { get; private set; }

        /// <summary>Lava's normal map (01000012_lava_normal) and colour (02000100_Lava_A).</summary>
        public Texture2D LavaNormal { get; private set; }
        public Texture2D Lava { get; private set; }

        /// <summary>Terrain 53's layers: bump Stars_C x24, reflection Stars_A x0.5, Stars_B x24.</summary>
        public Texture2D StarsBump { get; private set; }
        public Texture2D StarsReflect { get; private set; }
        public Texture2D StarsField { get; private set; }

        private bool _grungeTried;
        private Texture2D _grunge;

        /// <summary>
        /// KR's grunge, world art 1000045, or
        /// null when KR terrain is not available.
        /// </summary>
        public Texture2D Grunge
        {
            get
            {
                if (!_grungeTried)
                {
                    _grungeTried = true;

                    if (KrTerrainProvider.Instance is { } provider)
                    {
                        _grunge = Load(provider, "01000045");
                    }
                }

                return _grunge;
            }
        }

        public bool EnsureWater()
        {
            if (!_waterTried)
            {
                _waterTried = true;
                KrTerrainProvider provider = KrTerrainProvider.Instance;

                if (provider != null)
                {
                    WaterColor = Load(provider, "02000051");
                    WaterNormal = Load(provider, "01000013"); // the bump KR binds (section 31)
                    WaterReflect = Load(provider, "01000017");
                    WaterSwamp = Load(provider, "02000700");
                    SwampBump = Load(provider, "02000703");
                    SwampReflect = Load(provider, "02000702");
                    LavaNormal = Load(provider, "01000012");
                    Lava = Load(provider, "02000100");
                    StarsBump = Load(provider, "02000532");
                    StarsReflect = Load(provider, "02000530");
                    StarsField = Load(provider, "02000531");
                }
            }

            bool ok = WaterColor != null && WaterNormal != null && WaterReflect != null;

            return ok;
        }

        private Texture2D Load(KrTerrainProvider provider, string artId)
        {
            if (!provider.TryGetTexture(artId, out uint[] pixels, out int w, out int h))
                return null;

            var texture = new Texture2D(_device, w, h, false, SurfaceFormat.Color);
            texture.SetData(pixels);

            return texture;
        }

        /// <summary>
        /// The KR sprite for a flat land tile, or an empty one when KR has no
        /// terrain for it. <paramref name="x"/> and <paramref name="y"/> are
        /// world tile coordinates.
        /// </summary>
        /// <summary>Land graphic 0xA8, plain water.</summary>
        public const ushort WaterLand = 0xA8;

        /// <summary>
        /// KR's water at a tile, for water statics: the same sprite the land
        /// water beside them gets, so the two match.
        /// </summary>
        public SpriteInfo GetWater(int x, int y) => GetLand(WaterLand, x, y);

        private readonly System.Collections.Generic.Dictionary<ushort, SpriteInfo> _shore = [];

        /// <summary>
        /// A shore water static drawn from its KR edge mask (the fourth layer
        /// of its UOWaterShader): mask alpha for the WATER shader's pulsing
        /// alpha test, mask rgb for the foam the shader adds. The water colour
        /// itself comes from the shader, so no water texture is needed, and
        /// this works with <see cref="KrTerrainProvider.KrWater"/> off.
        /// Empty when the static has no mask (open water).
        /// </summary>
        public SpriteInfo GetShoreWater(ushort graphic)
        {
            if (_shore.TryGetValue(graphic, out SpriteInfo cached))
            {
                return cached;
            }

            SpriteInfo info = SpriteInfo.Empty;
            string mask = KrStaticProvider.Instance?.WaterMask(graphic);
            KrTerrainProvider provider = KrTerrainProvider.Instance;

            if (mask != null && provider != null
                && provider.TryGetTexture(mask, out uint[] mp, out int mw, out int mh))
            {
                uint[] pixels = new uint[LandSize * LandSize];
                float half = LandSize / 2f;

                for (int py = 0; py < LandSize; py++)
                {
                    int count = py < LandSize / 2 ? (py + 1) * 2 : (LandSize - py) * 2;
                    int start = (LandSize - count) / 2;

                    for (int px = start; px < start + count; px++)
                    {
                        // The mask covers the tile; the diamond is that square
                        // turned 45 degrees, as the ground textures are.
                        float u = (px - half + py + 0.5f) / LandSize;
                        float v = (py - px + half + 0.5f) / LandSize;

                        // Bilinear: the pulse moves the edge along the mask's
                        // gradient, so it needs the smooth values.
                        uint m = Bilinear(mp, mw, mh, (u * mw) - 0.5f, (v * mh) - 0.5f);

                        // Fully opaque mask is open water: no foam there. A
                        // pixel of alpha 0 is never drawn.
                        pixels[(py * LandSize) + px] = m >> 24 >= 255 ? 0xFF000000 : m;
                    }
                }

                info.Texture = _atlas.AddSprite(pixels, LandSize, LandSize, out Rectangle source);
                info.SetSource(source);
            }
            else if (mask == null && provider != null)
            {
                // Open water: an opaque black diamond, as a shore piece's open
                // part; KR's water colour comes from the shader.
                info = _openWater ??= OpenWaterDiamond();
            }

            _shore[graphic] = info;

            return info;
        }

        private SpriteInfo? _openWater;

        private SpriteInfo OpenWaterDiamond()
        {
            uint[] pixels = new uint[LandSize * LandSize];

            for (int py = 0; py < LandSize; py++)
            {
                int count = py < LandSize / 2 ? (py + 1) * 2 : (LandSize - py) * 2;
                // A pixel wider each side: scaled, the filtered rim fell below
                // the shader's pulsing alpha test, and the floor under the
                // water showed through as dashes on every tile edge. The
                // overlap lands on the neighbouring water.
                int start = Math.Max((LandSize - count) / 2 - 1, 0);
                int end = Math.Min((LandSize + count) / 2 + 1, LandSize);
                Array.Fill(pixels, 0xFF000000, (py * LandSize) + start, end - start);
            }

            SpriteInfo info = SpriteInfo.Empty;
            info.Texture = _atlas.AddSprite(pixels, LandSize, LandSize, out Rectangle source);
            info.SetSource(source);

            return info;
        }

        public SpriteInfo GetLand(ushort graphic, int x, int y)
        {
            KrTerrainProvider provider = KrTerrainProvider.Instance;

            if (provider == null || graphic >= LandGraphicCount)
            {
                KrTerrainProvider.Note($"land {graphic}: provider null or graphic out of range");

                return SpriteInfo.Empty;
            }

            if (!provider.TryGetTerrain(graphic, out ushort terrain))
            {
                KrTerrainProvider.Note($"land {graphic}: no terrain for this graphic");

                return SpriteInfo.Empty;
            }

            int period = PeriodOf(graphic, terrain, provider);
            int vx = Wrap(x, period);
            int vy = Wrap(y, period);

            SpriteInfo[] slots = _land[graphic] ??= new SpriteInfo[period * period];

            ref SpriteInfo info = ref slots[(vy * period) + vx];

            if (info.Texture == null)
            {
                if (!provider.TryGetGroundDiamond(terrain, LandSize, vx, vy, out uint[] pixels))
                {
                    KrTerrainProvider.Note(
                        $"land {graphic}: terrain {terrain} resolved but no ground diamond");

                    return SpriteInfo.Empty;
                }

                DebugTint(pixels, false);
                info.Texture = _atlas.AddSprite(pixels, LandSize, LandSize, out Rectangle source);
                info.SetSource(source);

                KrTerrainProvider.Note(
                    $"land {graphic}: terrain {terrain}, period {period}, drew KR art at ({vx},{vy})");
            }

            return info;
        }

        /// <summary>
        /// The KR sprite for a stretched land tile, keyed by the land tile's
        /// texture id. Texmaps are opaque squares, so no mask is applied.
        /// </summary>
        public SpriteInfo GetStretched(ushort graphic, uint texId, int size, int x, int y)
        {
            KrTerrainProvider provider = KrTerrainProvider.Instance;

            if (provider == null || graphic >= LandGraphicCount || size <= 0)
            {
                KrTerrainProvider.Note(
                    $"stretched {graphic}: provider null, graphic out of range, or size {size}");

                return SpriteInfo.Empty;
            }

            // Prefer the tile's own graphic; the texture id is the fallback for
            // land whose graphic the facet sweep never saw.
            if (!provider.TryGetTerrain(graphic, out ushort terrain)
                && !provider.TryGetTerrainByTexId(texId, out terrain))
            {
                KrTerrainProvider.Note($"stretched {graphic}: no terrain (texId {texId})");

                return SpriteInfo.Empty;
            }

            int period = PeriodOf(graphic, terrain, provider);
            int vx = Wrap(x, period);
            int vy = Wrap(y, period);

            SpriteInfo[] slots = _stretched[graphic] ??= new SpriteInfo[period * period];

            ref SpriteInfo info = ref slots[(vy * period) + vx];

            if (info.Texture == null)
            {
                if (!provider.TryGetGroundTile(terrain, size, vx, vy, out uint[] pixels))
                {
                    KrTerrainProvider.Note(
                        $"stretched {graphic}: terrain {terrain} resolved but no ground tile (size {size})");

                    return SpriteInfo.Empty;
                }

                DebugTint(pixels, true);
                info.Texture = _atlas.AddSprite(pixels, size, size, out Rectangle source);
                info.SetSource(source);

                KrTerrainProvider.Note(
                    $"stretched {graphic}: terrain {terrain}, size {size}, period {period}, drew KR art");
            }

            return info;
        }

        private readonly System.Collections.Generic.Dictionary<long, SpriteInfo> _blended = [];

        /// <summary>
        /// A flat land tile blended with its neighbours. Tiles whose 5x5
        /// neighbourhood is all one terrain - most of them - take the shared
        /// sprite from <see cref="GetLand"/>; only border tiles get their own.
        /// </summary>
        public SpriteInfo GetLandBlended(ushort graphic, int x, int y, int[] block5)
        {
            KrTerrainProvider provider = KrTerrainProvider.Instance;

            if (provider == null || block5 == null || KrTerrainProvider.IsUniform(block5))
            {
                return GetLand(graphic, x, y);
            }

            long key = Key(x, y, 0);

            if (_blended.TryGetValue(key, out SpriteInfo cached))
            {
                return cached;
            }

            SpriteInfo info = default;

            if (provider.TryGetBlendedDiamond(block5, LandSize, x, y, out uint[] pixels))
            {
                provider.ApplyWaterEdge(block5, pixels, LandSize, diamond: true);
                DebugTint(pixels, false);
                info.Texture = _atlas.AddSprite(pixels, LandSize, LandSize, out Rectangle source);
                info.SetSource(source);
            }
            else
            {
                info = GetLand(graphic, x, y);
            }

            _blended[key] = info;

            return info;
        }

        /// <summary>The stretched-path version of <see cref="GetLandBlended"/>.</summary>
        public SpriteInfo GetStretchedBlended(ushort graphic, uint texId, int size, int x, int y, int[] block5)
        {
            KrTerrainProvider provider = KrTerrainProvider.Instance;

            if (provider == null || block5 == null || size <= 0 || KrTerrainProvider.IsUniform(block5))
            {
                return GetStretched(graphic, texId, size, x, y);
            }

            long key = Key(x, y, size);

            if (_blended.TryGetValue(key, out SpriteInfo cached))
            {
                return cached;
            }

            SpriteInfo info = default;

            if (provider.TryGetBlendedTile(block5, size, x, y, out uint[] pixels))
            {
                provider.ApplyWaterEdge(block5, pixels, size, diamond: false);
                DebugTint(pixels, true);
                info.Texture = _atlas.AddSprite(pixels, size, size, out Rectangle source);
                info.SetSource(source);
            }
            else
            {
                info = GetStretched(graphic, texId, size, x, y);
            }

            _blended[key] = info;

            return info;
        }

        private static long Key(int x, int y, int size) =>
            ((long)(ushort)x << 32) | ((long)(ushort)y << 16) | (ushort)size;

        private int PeriodOf(ushort graphic, ushort terrain, KrTerrainProvider provider)
        {
            int period = _period[graphic];

            if (period == 0)
            {
                period = Math.Max(1, provider.PeriodFor(terrain));
                _period[graphic] = period;
            }

            return period;
        }

        private static uint Bilinear(uint[] p, int w, int h, float x, float y)
        {
            int x0 = Math.Clamp((int)MathF.Floor(x), 0, w - 1), y0 = Math.Clamp((int)MathF.Floor(y), 0, h - 1);
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float fx = Math.Clamp(x - x0, 0f, 1f), fy = Math.Clamp(y - y0, 0f, 1f);
            uint result = 0;

            for (int s = 0; s < 32; s += 8)
            {
                float c00 = (p[(y0 * w) + x0] >> s) & 0xFF, c10 = (p[(y0 * w) + x1] >> s) & 0xFF;
                float c01 = (p[(y1 * w) + x0] >> s) & 0xFF, c11 = (p[(y1 * w) + x1] >> s) & 0xFF;
                float c = ((c00 * (1 - fx)) + (c10 * fx)) * (1 - fy) + ((c01 * (1 - fx)) + (c11 * fx)) * fy;
                result |= (uint)Math.Clamp(MathF.Round(c), 0f, 255f) << s;
            }

            return result;
        }

        private static int Wrap(int value, int period) => ((value % period) + period) % period;

        // Diagnostic: with KR_DEBUG_LAND=1 set, stretched KR land sprites are
        // tinted red and flat ones blue, to see which path draws which tile.
        private static readonly bool _debugLand = Environment.GetEnvironmentVariable("KR_DEBUG_LAND") == "1";

        private static void DebugTint(uint[] pixels, bool stretched)
        {
            if (!_debugLand || pixels == null)
            {
                return;
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                uint p = pixels[i];

                if ((p >> 24) == 0)
                {
                    continue;
                }

                uint r = p & 0xFF, g = (p >> 8) & 0xFF, b = (p >> 16) & 0xFF;
                pixels[i] = stretched
                    ? (p & 0xFF000000) | (b / 2 << 16) | (g / 2 << 8) | Math.Min(255u, r + 80)
                    : (p & 0xFF000000) | (Math.Min(255u, b + 80) << 16) | (g / 2 << 8) | (r / 2);
            }
        }
    }
}
