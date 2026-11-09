// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.IO;
using ClassicUO.Renderer;
using ClassicUO.Renderer.Terrain;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using static ClassicUO.Renderer.UltimaBatcher2D;

namespace ClassicUO.Game.Map
{
    /// <summary>
    /// Tracks per-texture sprite counts and write cursors for bucket-insert building.
    /// Avoids dictionary overhead by using linear search (typically &lt; 20 unique textures per chunk).
    /// </summary>
    internal struct TextureBucketTracker
    {
        private Texture2D[] _textures;
        private int[] _counts;
        private int[] _cursors;
        private int _bucketCount;

        public TextureBucketTracker(int initialCapacity)
        {
            _textures = new Texture2D[initialCapacity];
            _counts = new int[initialCapacity];
            _cursors = new int[initialCapacity];
            _bucketCount = 0;
        }

        public void Clear()
        {
            if (_bucketCount > 0)
                Array.Clear(_textures, 0, _bucketCount);
            _bucketCount = 0;
        }

        /// <summary>
        /// Registers a texture during pass 1 (counting phase).
        /// </summary>
        public void Count(Texture2D texture)
        {
            for (int i = 0; i < _bucketCount; i++)
            {
                if (_textures[i] == texture)
                {
                    _counts[i]++;
                    return;
                }
            }

            if (_bucketCount >= _textures.Length)
            {
                int newSize = _textures.Length * 2;
                Array.Resize(ref _textures, newSize);
                Array.Resize(ref _counts, newSize);
                Array.Resize(ref _cursors, newSize);
            }

            _textures[_bucketCount] = texture;
            _counts[_bucketCount] = 1;
            _bucketCount++;
        }

        /// <summary>
        /// Computes prefix-sum offsets from the counts. Returns total sprite count.
        /// After this, GetNextIndex() can be called during pass 2.
        /// </summary>
        public int ComputeOffsets()
        {
            int offset = 0;
            for (int i = 0; i < _bucketCount; i++)
            {
                _cursors[i] = offset;
                offset += _counts[i];
            }
            return offset;
        }

        /// <summary>
        /// Returns the next write index for this texture and advances its cursor.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetNextIndex(Texture2D texture)
        {
            for (int i = 0; i < _bucketCount; i++)
            {
                if (_textures[i] == texture)
                    return _cursors[i]++;
            }

            // Should never happen if pass 1 counted correctly
            return -1;
        }
    }

    internal sealed class ChunkMesh
    {
        public readonly MeshLayer Land = new();
        public readonly MeshLayer Statics = new();

        public bool IsDirty = true;

        private bool _animatedWaterEffect;
        private TextureBucketTracker _landBuckets = new(16);
        private TextureBucketTracker _staticsBuckets = new(32);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void MarkDirtyIfNeeded(GameObject obj)
        {
            if (!IsDirty && obj is GameObjects.Land or Static or Multi)
                IsDirty = true;
        }

        // The world the current build reads neighbouring land from, for KR
        // terrain blending. Set at the top of Build; builds run on one thread.
        private static World s_world;

        public void Build(Chunk chunk, World world, GraphicsDevice graphicsDevice)
        {
            s_world = world;

            Land.Reset();
            Statics.Reset();
            IsDirty = false;

            var profile = ProfileManager.CurrentProfile;
            if (profile == null)
                return;

            _animatedWaterEffect = profile.AnimatedWaterEffect;

            // Pass 1: count sprites per texture
            _landBuckets.Clear();
            _staticsBuckets.Clear();

            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (var obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                    {
                        obj.InChunkMesh = false;
                        obj.MeshSpriteIndex = -1;

                        switch (obj)
                        {
                            case GameObjects.Land land:
                                { double probeT = Managers.FrameProbe.Now; CountLand(land); Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshLand, probeT); }
                                break;

                            case Static staticObj:
                                { double probeT = Managers.FrameProbe.Now; CountStatic(staticObj); Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshStatic, probeT); }
                                break;

                            case Multi multi:
                                { double probeT = Managers.FrameProbe.Now; CountMulti(multi); Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshMulti, probeT); }
                                break;
                        }
                    }
                }
            }

            int landTotal = _landBuckets.ComputeOffsets();
            int staticsTotal = _staticsBuckets.ComputeOffsets();

            Land.EnsureCapacity(landTotal);
            Land.Count = landTotal;
            Statics.EnsureCapacity(staticsTotal);
            Statics.Count = staticsTotal;

            // Pass 2: write sprites at their final sorted positions
            for (int x = 0; x < 8; x++)
            {
                for (int y = 0; y < 8; y++)
                {
                    for (var obj = chunk.GetHeadObject(x, y); obj != null; obj = obj.TNext)
                    {
                        switch (obj)
                        {
                            case GameObjects.Land land:
                                { double probeT = Managers.FrameProbe.Now; TryAddLand(land); Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshLand, probeT); }
                                break;

                            case Static staticObj:
                                { double probeT = Managers.FrameProbe.Now; TryAddStatic(staticObj); Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshStatic, probeT); }
                                break;

                            case Multi multi:
                                { double probeT = Managers.FrameProbe.Now; TryAddMulti(multi); Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshMulti, probeT); }
                                break;
                        }
                    }
                }
            }

            double probeUp = Managers.FrameProbe.Now;
            _gpuDecided.Clear();

            if (KrTerrainProvider.IsActive && KrTerrainProvider.GpuTerrain)
            {
                var atlas = Client.Game.UO.KrTerrain.Atlas;
                atlas.FlushWeights();

                // Slots taken back from far tiles: their chunks' meshes still
                // point at the old slots, so rebuild them (when they are back
                // in view) to get fresh records.
                if (atlas.Reclaimed.Count > 0)
                {
                    foreach (long key in atlas.Reclaimed)
                    {
                        Chunk stale = world.Map.GetChunk((int)(key >> 32), (int)key, load: false);

                        if (stale != null && stale != chunk && stale.Mesh != null)
                        {
                            stale.Mesh.IsDirty = true;
                        }
                    }

                    atlas.Reclaimed.Clear();
                }
            }

            Land.UploadVertexBuffer(graphicsDevice);
            Statics.UploadVertexBuffer(graphicsDevice);
            Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshUpload, probeUp);
        }

        public void Clear()
        {
            Land.Reset();
            Land.Dispose();
            Statics.Reset();
            Statics.Dispose();
            IsDirty = true;
        }

        /// <summary>
        /// Resets mesh state for pool reuse. Keeps GPU buffers and arrays allocated.
        /// </summary>
        public void SoftClear()
        {
            Land.SoftReset();
            Statics.SoftReset();
            _landBuckets.Clear();
            _staticsBuckets.Clear();
            IsDirty = true;
        }

        #region Pass 1: Texture counting

        private void CountLand(GameObjects.Land land)
        {
            if (!land.AllowedToDraw || land.IsDestroyed)
                return;

            if (_animatedWaterEffect && land.TileData.IsWet)
                return;

            // GPU terrain first, exactly as TryAddLand decides it: its quads go
            // in the atlas texture's bucket, which must be counted here too.
            // Decided once per build and kept for TryAddLand: the decision
            // writes the tile's weight block, which should not happen twice.
            double probeGpu = Managers.FrameProbe.Now;
            KrTerrainAtlas atlas = null;
            float gpuHueX = 0, gpuOrigin = 0;
            bool gpu = land.Hue == 0 && TryGpuTerrain(land, out atlas, out gpuHueX, out gpuOrigin);
            Managers.FrameProbe.Add(Managers.FrameProbe.Section.GpuDecide, probeGpu);

            if (gpu)
            {
                _gpuDecided[land] = (gpuHueX, gpuOrigin);
                _landBuckets.Count(atlas.Texture);
                Managers.FrameProbe.LandTile(gpu: true);

                return;
            }

            Managers.FrameProbe.LandTile(gpu: false);

            double probeCpu = Managers.FrameProbe.Now;

            if (TryResolveLandSprite(land, out Texture2D texture, out _, out _))
                _landBuckets.Count(texture);

            Managers.FrameProbe.Add(Managers.FrameProbe.Section.CpuResolve, probeCpu);
        }

        /// <summary>
        /// The texture and source rectangle a land tile draws with, and
        /// whether it uses the stretched geometry.
        /// </summary>
        /// <remarks>
        /// Both passes of the build MUST agree on this. Pass one counts
        /// sprites per texture and the vertex array is sized from those
        /// counts; pass two writes at the index that count reserved. Picking
        /// Kingdom Reborn art in pass two alone left the KR atlas with no
        /// counted slot, so the cursor ran past the end of the array and the
        /// client crashed on the first frame after login. Resolving it in one
        /// place is what keeps the two passes in step.
        ///
        /// The KR lookups are cached per terrain and position, so being
        /// called once per pass costs a lookup, not a resample.
        /// </remarks>
        /// <summary>
        /// KR terrain ids of the 5x5 tiles around a land tile, for blending
        /// one terrain into the next the way KR does. Neighbours in chunks that are not loaded count as
        /// unknown and simply do not blend.
        /// </summary>
        private static int[] Block5(GameObjects.Land land)
        {
            World world = s_world;

            if (world?.Map == null || KrTerrainProvider.Instance == null)
            {
                return null;
            }

            // From the map file, not the loaded tiles: a neighbour across a
            // chunk edge may not be loaded when this chunk's mesh is built,
            // and the blend (sewer water beside x = 6031) was lost for good.
            return KrTerrainProvider.Instance.Block5(world.Map.GetLandGraphic, land.X, land.Y);
        }

        private static bool TryResolveLandSprite(
            GameObjects.Land land,
            out Texture2D texture,
            out Rectangle source,
            out bool stretchedGeometry)
        {
            texture = null;
            source = default;
            stretchedGeometry = false;

            if (land.IsStretched)
            {
                ushort texId = Client.Game.UO.FileManager.TileData.LandData[land.Graphic].TexID;

                ref readonly var texmapInfo = ref Client.Game.UO.Texmaps.GetTexmap(texId);

                if (texmapInfo.Texture != null)
                {
                    stretchedGeometry = true;

                    if (KrTerrainProvider.IsActive && (KrTerrainProvider.KrWater || !IsWaterLand(land)))
                    {
                        var kr = Client.Game.UO.KrTerrain.GetStretchedBlended(
                            land.Graphic, texId, texmapInfo.Source.Width, land.X, land.Y, Block5(land));

                        if (kr.Texture != null)
                        {
                            texture = kr.Texture;
                            source = kr.Source;

                            KrTerrainProvider.NoteLand(land.Graphic, "kr stretched");

                            return true;
                        }
                    }

                    texture = texmapInfo.Texture;
                    source = texmapInfo.Source;

                    KrTerrainProvider.NoteLand(land.Graphic, "2d texmap");

                    return true;
                }
            }

            if (KrTerrainProvider.IsActive && (KrTerrainProvider.KrWater || !IsWaterLand(land)))
            {
                var krLand = Client.Game.UO.KrTerrain.GetLandBlended(land.Graphic, land.X, land.Y, Block5(land));

                if (krLand.Texture != null)
                {
                    texture = krLand.Texture;
                    source = krLand.Source;

                    KrTerrainProvider.NoteLand(land.Graphic, "kr flat");

                    return true;
                }
            }

            ref readonly var artInfo = ref Client.Game.UO.Arts.GetLand(land.Graphic);

            if (artInfo.Texture == null)
            {
                KrTerrainProvider.NoteLand(land.Graphic, "NOTHING DRAWN");

                return false;
            }

            texture = artInfo.Texture;
            source = artInfo.Source;

            KrTerrainProvider.NoteLand(land.Graphic, "2d land art");

            return true;
        }

        private void CountStatic(Static staticObj)
        {
            if (!staticObj.AllowedToDraw || staticObj.IsDestroyed || LavaOnLavaLand(staticObj))
                return;

            CountStaticLike(staticObj, ref staticObj.ItemData, staticObj.Graphic, staticObj.X, staticObj.Y);
        }

        private void CountMulti(Multi multi)
        {
            if (!multi.AllowedToDraw || multi.IsDestroyed)
                return;

            if (multi.State != 0)
                return;

            CountStaticLike(multi, ref multi.ItemData, multi.Graphic, multi.X, multi.Y);
        }

        private void CountStaticLike(GameObject obj, ref StaticTiles itemData, ushort graphic, ushort x, ushort y)
        {
            if (IsStaticExcludedFromMesh(graphic, ref itemData))
                return;

            if (!TryResolveStaticSprite(
                    graphic, itemData.IsWet && itemData.Height <= 1 && !KrNotWater(graphic), LiesFlat(ref itemData), x, y, out Texture2D texture, out _, out _, out _))
                return;

            _staticsBuckets.Count(texture);
        }

        #endregion

        #region Pass 2: Vertex building

        private void TryAddLand(GameObjects.Land land)
        {
            if (!land.AllowedToDraw || land.IsDestroyed)
                return;

            if (_animatedWaterEffect && land.TileData.IsWet)
                return;

            ushort hue = land.Hue;
            Vector3 hueVec;
            if (hue != 0)
            {
                hueVec.X = hue - 1;
                hueVec.Y = land.IsStretched
                    ? ShaderHueTranslator.SHADER_LAND_HUED
                    : ShaderHueTranslator.SHADER_HUED;
            }
            else
            {
                hueVec.X = 0;
                hueVec.Y = land.IsStretched
                    ? ShaderHueTranslator.SHADER_LAND
                    : ShaderHueTranslator.SHADER_NONE;
            }
            hueVec.Z = 1f;

            if (hue == 0 && ShaderHueTranslator.WaterShader && IsWaterLand(land))
                hueVec.Y = LandWaterMode(land);

            float depth = land.CalculateDepthZ() + 0.5f;
            int baseX = (land.X - land.Y) * 22 - 22;
            int baseY = (land.X + land.Y) * 22 - (land.Z << 2) - 22;

            land.KrTerrainGpu = false;

            if (land.Hue == 0 && _gpuDecided.TryGetValue(land, out (float HueX, float Origin) decided))
            {
                KrTerrainAtlas atlas = Client.Game.UO.KrTerrain.Atlas;
                float packed = decided.HueX;
                float origin = decided.Origin;
                int slot = 0;
                int gpuIdx = _landBuckets.GetNextIndex(atlas.Texture);
                land.MeshSpriteIndex = gpuIdx;
                land.KrTerrainGpu = true;
                land.KrTerrainHue = packed;
                Vector3 gpuHue = new(packed, ShaderHueTranslator.SHADER_KR_TERRAIN, 1f);

                if (land.IsStretched)
                {
                    WriteStretchedTerrain(gpuIdx, atlas.Texture, baseX, baseY + (land.Z << 2), ref land.YOffsets,
                        ref land.NormalTop, ref land.NormalRight, ref land.NormalLeft, ref land.NormalBottom,
                        origin, slot, gpuHue, depth);
                }
                else
                {
                    Land.WriteTerrainQuad(gpuIdx, atlas.Texture, baseX, baseY, origin, gpuHue, depth, slot);
                }
                land.InChunkMesh = true;

                return;
            }

            if (!TryResolveLandSprite(land, out Texture2D texture, out Rectangle source, out bool stretched))
                return;

            int idx = _landBuckets.GetNextIndex(texture);
            land.MeshSpriteIndex = idx;

            // KR lights terrain by slope, the way ClassicUO lights stretched
            // land: the KR client gives flat ground 0.856 and
            // slopes 0.64-0.96 by facing, and
            // ClassicUO's flat value is 0.854. So KR land is drawn lit
            // everywhere - flat tiles too (their quads carry the flat normal),
            // or they come out brighter than their stretched neighbours.
            // Must agree with GameSceneDrawingSorting.ApplyMeshHue.
            if (hue == 0 && hueVec.Y is ShaderHueTranslator.SHADER_NONE or ShaderHueTranslator.SHADER_LAND && IsKrLand(land))
            {
                hueVec.Y = ShaderHueTranslator.SHADER_KR_LAND;
            }

            if (stretched)
            {
                WriteStretchedLand(
                    idx,
                    texture,
                    source,
                    baseX,
                    baseY + (land.Z << 2),
                    ref land.YOffsets,
                    ref land.NormalTop,
                    ref land.NormalRight,
                    ref land.NormalLeft,
                    ref land.NormalBottom,
                    hueVec,
                    depth
                );
            }
            else
            {
                // A stretched tile with no texmap still sits at the stretched
                // height, even though it draws as a flat quad.
                int quadY = land.IsStretched ? baseY + (land.Z << 2) : baseY;

                Land.WriteQuadAt(idx, texture, source, baseX, quadY, hueVec, depth);
            }

            land.InChunkMesh = true;
        }

        /// <summary>
        /// The texture and source rectangle a static draws with, plus the size
        /// to anchor it by.
        /// </summary>
        /// <remarks>
        /// As with land, BOTH passes of the build must agree on this: pass one
        /// counts sprites per texture and the vertex array is sized from those
        /// counts, pass two writes at the index that count reserved. Resolving
        /// it in one place is what keeps them in step.
        ///
        /// KR art is a different size from the 2D sprite it replaces - the
        /// content box runs to a median 1.11x its width and 1.38x its height -
        /// so the size is reported back rather than assumed.
        /// </remarks>
        private static bool TryResolveStaticSprite(
            ushort graphic,
            bool keepFit,
            bool flat,
            int tileX,
            int tileY,
            out Texture2D texture,
            out Rectangle source,
            out int width,
            out int height)
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            // Shore water: KR's edge mask, so the shader can pulse the edge.
            if (keepFit && KrTerrainProvider.IsActive && artInfo.Source.Width == 44)
            {
                SpriteInfo shore = Client.Game.UO.KrTerrain.GetShoreWater(graphic);

                if (shore.Texture != null)
                {
                    texture = shore.Texture;
                    source = shore.Source;
                    width = source.Width;
                    height = source.Height;

                    return true;
                }
            }

            if (keepFit && IsWaterDiamond(artInfo.Source.Width, artInfo.Source.Height))
            {
                SpriteInfo water = Client.Game.UO.KrTerrain.GetWater(tileX, tileY);

                if (water.Texture != null)
                {
                    texture = water.Texture;
                    source = water.Source;
                    width = source.Width;
                    height = source.Height;

                    return true;
                }
            }

            // KR art is fitted to the 2D sprite's footprint, so that anything
            // built out of repeated tiles still lines up.
            // kr_light_sources_2d: light sources keep their 2D art.
            if (KrStaticProvider.IsActive && artInfo.Texture != null && !LightAs2D(graphic))
            {
                ref readonly var krInfo = ref Client.Game.UO.KrStatics.GetArt(
                    graphic, artInfo.Source.Width, artInfo.Source.Height, keepFit, tileX, tileY, flat);

                if (krInfo.Texture != null)
                {
                    texture = krInfo.Texture;
                    source = krInfo.Source;
                    // Drawn at its display size; the atlas may hold more detail.
                    width = krInfo.Width;
                    height = krInfo.Height;

                    KrTerrainProvider.NoteStatic(graphic, "mesh: kr");

                    return true;
                }

                KrTerrainProvider.NoteStatic(graphic, "mesh: kr missing, 2d");
            }

            if (artInfo.Texture == null)
            {
                texture = null;
                source = default;
                width = height = 0;

                KrTerrainProvider.NoteStatic(graphic, "mesh: NOTHING DRAWN");

                return false;
            }

            texture = artInfo.Texture;
            source = artInfo.Source;
            width = artInfo.Source.Width;
            height = artInfo.Source.Height;

            return true;
        }

        /// <summary>
        /// Checks if a static/multi should be excluded from the chunk mesh.
        /// </summary>
        /// <summary>
        /// Lies on the floor: height 0, or height 1 when it is also a
        /// background surface. Stone roof 1372 is height 1; drawn as a box it
        /// became an opaque square that covered the walls behind it. The
        /// extra flags keep benches, logs and food standing (1,521 statics
        /// are height 1; 26 of them are background surfaces, 21 stone roofs).
        /// </summary>
        /// <summary>
        /// A wet static that is one floor diamond: shore and river water. With
        /// KR terrain on it draws KR's land water, so it matches the open sea
        /// (land tiles) beside it.
        /// </summary>
        /// <summary>
        /// kr_light_sources_2d: a light source whose KR art needs a separate
        /// flame (its tileart names a scene: torchflame.ems on a wall torch)
        /// keeps its 2D art and animation. Light sources KR draws in one
        /// piece keep their KR art.
        /// </summary>
        internal static bool LightAs2D(ushort graphic) =>
            Configuration.Settings.GlobalSettings.KrLightSources2D
            && Client.Game.UO.FileManager.TileData.StaticData[graphic].IsLight
            && KrStaticProvider.Instance?.SceneFor(graphic, out _) != null;

        internal static bool IsWaterDiamond(int width, int height) =>
            KrTerrainProvider.KrWater && KrTerrainProvider.IsActive && width == 44 && height > 0 && height <= 44;

        /// <summary>
        /// Land drawn as water: wet in tiledata, or KR terrain that KR draws
        /// with its water shader (sewer channels). Must agree with ApplyMeshHue
        /// and LandView.
        /// </summary>
        internal static byte LandWaterMode(GameObjects.Land land) =>
            KrTerrainProvider.Instance?.IsSwampLand(land.Graphic) == true
                ? ShaderHueTranslator.SHADER_SWAMP_WATER
                : KrTerrainProvider.Instance?.IsStarsLand(land.Graphic) == true
                    ? ShaderHueTranslator.SHADER_STARS
                    : KrTerrainProvider.Instance?.IsLavaLand(land.Graphic) == true
                        ? ShaderHueTranslator.SHADER_LAVA
                        : ShaderHueTranslator.SHADER_WATER;

        internal static bool IsWaterLand(GameObjects.Land land) =>
            land.TileData.IsWet || KrTerrainProvider.Instance?.IsWaterLand(land.Graphic) == true;

        /// <summary>Land drawn with KR terrain art (water stays 2D).</summary>
        internal static bool IsKrLand(GameObjects.Land land) =>
            KrTerrainProvider.IsActive && (KrTerrainProvider.KrWater || !IsWaterLand(land));

        /// <summary>
        /// A wet, flat static KR draws as no water: its water shader names no
        /// water texture (blood, 0x122A-0x122F). Must agree with View and
        /// GameSceneDrawingSorting.IsWater.
        /// </summary>
        public static bool KrNotWater(ushort graphic) =>
            KrStaticProvider.Instance?.IsNotWater(graphic) == true;

        /// <summary>A static KR draws as swamp water. Must agree with ApplyMeshHue and View.</summary>
        private static readonly System.Collections.Generic.Dictionary<GameObjects.Land, (float HueX, float Origin)> _gpuDecided = [];
        private static readonly System.Collections.Generic.List<int> _gpuLayers = [];
        private static readonly System.Collections.Generic.List<KR.World.KrTerrainBlend.WeightGrid> _gpuGrids = [];

        /// <summary>
        /// Whether a land tile goes the GPU terrain way: KR terrain on, a flat
        /// tile, not water, and every terrain it shows a plain three-layer
        /// composite the atlas can hold. A uniform tile is its terrain alone;
        /// a border tile is its centre terrain with up to three others laid
        /// over it in KR's order (KrTerrainArt.BlendOrder), their weights in
        /// the atlas at <paramref name="slot"/>. More layers stay on the CPU.
        /// </summary>
        private static bool TryGpuTerrain(GameObjects.Land land, out KrTerrainAtlas atlas, out float hueX, out float origin)
        {
            atlas = null;
            hueX = 0;
            origin = 0;

            if (!KrTerrainProvider.IsActive || !KrTerrainProvider.GpuTerrain || IsWaterLand(land))
            {
                return false;
            }

            KrTerrainProvider provider = KrTerrainProvider.Instance;
            int[] block5 = Block5(land);

            if (block5 == null || block5[12] < 0 || provider.GpuCompositeTerrain(block5[12]) is not { } baseLayers)
            {
                return false;
            }

            atlas = Client.Game.UO.KrTerrain.Atlas;
            int baseEntry = atlas.Entry(block5[12], baseLayers);

            if (baseEntry < 0 || atlas.Texture == null)
            {
                return false;
            }

            // A uniform tile: its entry, negated, needs no record.
            if (KrTerrainProvider.IsUniform(block5))
            {
                hueX = -1 - baseEntry;
                origin = atlas.PackOrigin(baseEntry, land.X, land.Y);

                return true;
            }

            provider.GpuBlend(block5, _gpuLayers, _gpuGrids);

            if (_gpuLayers.Count > KrTerrainAtlas.MaxLayers)
            {
                return false;
            }

            Span<int> entries = stackalloc int[KrTerrainAtlas.MaxLayers + 1];
            entries[0] = baseEntry;

            for (int i = 0; i < _gpuLayers.Count; i++)
            {
                if (provider.GpuCompositeTerrain(_gpuLayers[i]) is not { } layers
                    || (entries[i + 1] = atlas.Entry(_gpuLayers[i], layers)) < 0)
                {
                    return false;
                }
            }

            int slot = atlas.TileRecord(land.X, land.Y, baseEntry, entries.Slice(1, _gpuLayers.Count),
                System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_gpuGrids));

            if (slot < 0)
            {
                return false;
            }

            hueX = slot;
            origin = atlas.PackOrigin(land.X, land.Y, entries.Slice(0, _gpuLayers.Count + 1));

            return true;
        }

        internal static bool IsLava(ushort graphic) =>
            KrStaticProvider.Instance?.IsLava(graphic) == true;

        /// <summary>
        /// Flat lava lying on lava land (0x3450-0x3459 and 0x3548 in the lava
        /// at Trammel 5685,2995): the land already draws KR's lava, and the
        /// static's 2D diamond showed as a patterned tile in it.
        /// </summary>
        internal static bool LavaOnLavaLand(Static s)
        {
            if (!KrStaticProvider.IsActive || s.ItemData.Height > 1 || !IsLava(s.Graphic))
                return false;

            for (GameObject o = s.World.Map.GetTile(s.X, s.Y, false); o != null; o = o.TNext)
            {
                if (o is GameObjects.Land land)
                {
                    return Math.Abs(land.Z - s.Z) <= 1 && KrTerrainProvider.Instance?.IsLavaLand(land.Graphic) == true;
                }
            }

            return false;
        }

        internal static bool IsSwamp(ushort graphic) =>
            KrStaticProvider.Instance?.IsSwampWater(graphic) == true;

        internal static bool LiesFlat(ref StaticTiles itemData) =>
            itemData.Height == 0 || (itemData.Height == 1 && itemData.IsSurface && itemData.IsBackground);

        /// <summary>
        /// Whether a static has to meet its neighbours exactly. Roofs, floors
        /// and walls are built from repeated pieces; everything else stands on
        /// its own and keeps KR's proportions.
        /// </summary>
        private static bool Tiles(ref StaticTiles itemData) =>
            itemData.IsRoof || itemData.IsSurface || itemData.IsWall;

        private static bool IsStaticExcludedFromMesh(ushort graphic, ref StaticTiles itemData)
        {
            if (itemData.IsInternal)
                return true;

            if (itemData.IsAnimated)
                return true;

            // A static carrying a KR scene (0x350E's 00013579_waterfall) is
            // drawn per object, where StaticView keeps the scene running;
            // in the mesh it never started. A blank one without a scene too:
            // StaticView skips it, as KR draws nothing there - in the mesh,
            // a waterfall's edge pieces (0x34FB-0x34FE, 00005703_blank) left
            // a black notch in the falls.
            if (KrStaticProvider.Instance is { } krStatics
                && (krStatics.SceneFor(graphic, out bool blank) != null || blank))
                return true;

            if (itemData.IsFoliage)
                return true;

            if (StaticFilters.IsTree(graphic, out _))
                return true;

            if (StaticFilters.IsRock(graphic))
                return true;

            return false;
        }

        private void TryAddStatic(Static staticObj)
        {
            if (!staticObj.AllowedToDraw || staticObj.IsDestroyed || LavaOnLavaLand(staticObj))
                return;

            TryAddStaticLike(staticObj, ref staticObj.ItemData, staticObj.Graphic, staticObj.Hue);
        }

        private void TryAddMulti(Multi multi)
        {
            if (!multi.AllowedToDraw || multi.IsDestroyed)
                return;

            if (multi.State != 0)
                return;

            TryAddStaticLike(multi, ref multi.ItemData, multi.Graphic, multi.Hue);
        }

        private void TryAddStaticLike(GameObject obj, ref StaticTiles itemData, ushort graphic, ushort hue)
        {
            if (IsStaticExcludedFromMesh(graphic, ref itemData))
                return;

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, itemData.IsPartialHue, 1f);

            // Only flat water is water; a trough is wet but 6 high.
            bool openWater = itemData.IsWet && itemData.Height <= 1 && !KrNotWater(graphic);

            // Swamp before water: wet, flat swamp statics are KR swamp texture.
            if (hue == 0 && ShaderHueTranslator.WaterShader && IsSwamp(graphic))
                hueVec.Y = ShaderHueTranslator.SHADER_SWAMP;
            else if (hue == 0 && ShaderHueTranslator.WaterShader && openWater)
                hueVec.Y = ShaderHueTranslator.SHADER_WATER;

            float depth = obj.CalculateDepthZ() + 0.5f;

            // Flat pieces at one height on one tile share a depth exactly - a
            // rug (0x0AFA), the boards under it (0x04A9) and a second carpet
            // (0x0ACF), all z 20 at Trammel 1804,2704 - and the tie went to
            // whichever texture page drew first, so the rug came and went with
            // where the player had been. The file's first piece stays on top,
            // as KR shows it; 0.002 a step stays below one z (0.01).
            if (obj is Static ordered)
                depth -= Math.Min((int)ordered.TileOrder, 4) * 0.002f;

            int baseX = (obj.X - obj.Y) * 22 - 22;
            int baseY = (obj.X + obj.Y) * 22 - (obj.Z << 2) - 22;

            if (!TryResolveStaticSprite(
                    graphic, openWater, LiesFlat(ref itemData), obj.X, obj.Y,
                    out Texture2D texture, out Rectangle source, out int width, out int height))
                return;

            // The anchor puts the sprite's bottom centre on the tile, and works
            // from whatever size the sprite is, so KR art of a different size
            // sits correctly without any special case. Writing the index entry
            // from the drawn sprite is what keeps picking lined up with it.
            ref var artIndex = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
            artIndex.Width = (short)((width >> 1) - 22);
            artIndex.Height = (short)(height - 44);

            int posX = baseX - artIndex.Width;
            int posY = baseY - artIndex.Height;

            int idx = _staticsBuckets.GetNextIndex(texture);
            obj.MeshSpriteIndex = idx;
            Statics.WriteQuadAt(idx, texture, source, posX, posY, width, height, hueVec, depth);
            obj.InChunkMesh = true;
        }

        /// <summary>
        /// A sloped tile on the GPU terrain path: the stretched geometry and
        /// normals of <see cref="WriteStretchedLand"/>, with the texture
        /// coordinates the tile's own (u, v) - top (0,0), right (1,0), left
        /// (0,1), bottom (1,1), the square the CPU's BlendedTile is mapped by -
        /// x plus twice the weight slot, y plus 2 to mark it square-mapped.
        /// </summary>
        private void WriteStretchedTerrain(
            int index, Texture2D texture, int posX, int posY, ref YOffsets yOffsets,
            ref Vector3 normalTop, ref Vector3 normalRight, ref Vector3 normalLeft, ref Vector3 normalBottom,
            float origin, int slot, Vector3 hue, float depth)
        {
            ref var vertex = ref Land.Vertices[index];
            float s = slot * 2f;

            vertex.TextureCoordinate0 = new Vector3(s, 2f, origin);
            vertex.TextureCoordinate1 = new Vector3(s + 1f, 2f, origin);
            vertex.TextureCoordinate2 = new Vector3(s, 3f, origin);
            vertex.TextureCoordinate3 = new Vector3(s + 1f, 3f, origin);

            vertex.Normal0 = normalTop;
            vertex.Normal1 = normalRight;
            vertex.Normal2 = normalLeft;
            vertex.Normal3 = normalBottom;

            vertex.Position0 = new Vector3(posX + 22, posY - yOffsets.Top, depth);
            vertex.Position1 = new Vector3(posX + 44, posY + (22 - yOffsets.Right), depth);
            vertex.Position2 = new Vector3(posX, posY + (22 - yOffsets.Left), depth);
            vertex.Position3 = new Vector3(posX + 22, posY + (44 - yOffsets.Bottom), depth);

            vertex.Hue0 = vertex.Hue1 = vertex.Hue2 = vertex.Hue3 = hue;

            Land.Textures[index] = texture;
        }

        private void WriteStretchedLand(
            int index,
            Texture2D texture, Rectangle sourceRect,
            int posX, int posY,
            ref YOffsets yOffsets,
            ref Vector3 normalTop, ref Vector3 normalRight,
            ref Vector3 normalLeft, ref Vector3 normalBottom,
            Vector3 hue, float depth)
        {
            ref var vertex = ref Land.Vertices[index];

            // Half-pixel inset to prevent texture bleeding at tile seams
            float sourceX = (sourceRect.X + 0.5f) / (float)texture.Width;
            float sourceY = (sourceRect.Y + 0.5f) / (float)texture.Height;
            float sourceW = (sourceRect.Width - 1f) / (float)texture.Width;
            float sourceH = (sourceRect.Height - 1f) / (float)texture.Height;

            vertex.TextureCoordinate0.X = sourceX;
            vertex.TextureCoordinate0.Y = sourceY;
            vertex.TextureCoordinate0.Z = 0;
            vertex.TextureCoordinate1.X = sourceW + sourceX;
            vertex.TextureCoordinate1.Y = sourceY;
            vertex.TextureCoordinate1.Z = 0;
            vertex.TextureCoordinate2.X = sourceX;
            vertex.TextureCoordinate2.Y = sourceH + sourceY;
            vertex.TextureCoordinate2.Z = 0;
            vertex.TextureCoordinate3.X = sourceW + sourceX;
            vertex.TextureCoordinate3.Y = sourceH + sourceY;
            vertex.TextureCoordinate3.Z = 0;

            vertex.Normal0 = normalTop;
            vertex.Normal1 = normalRight;
            vertex.Normal2 = normalLeft;
            vertex.Normal3 = normalBottom;

            vertex.Position0.X = posX + 22;
            vertex.Position0.Y = posY - yOffsets.Top;
            vertex.Position1.X = posX + 44;
            vertex.Position1.Y = posY + (22 - yOffsets.Right);
            vertex.Position2.X = posX;
            vertex.Position2.Y = posY + (22 - yOffsets.Left);
            vertex.Position3.X = posX + 22;
            vertex.Position3.Y = posY + (44 - yOffsets.Bottom);

            vertex.Position0.Z = vertex.Position1.Z = vertex.Position2.Z = vertex.Position3.Z = depth;

            vertex.Hue0 = vertex.Hue1 = vertex.Hue2 = vertex.Hue3 = hue;

            Land.Textures[index] = texture;
        }

        #endregion
    }
}
