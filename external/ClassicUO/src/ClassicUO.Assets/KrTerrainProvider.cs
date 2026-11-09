// SPDX-License-Identifier: BSD-2-Clause
//
// Alternate land-tile art backed by a local Kingdom Reborn install.
//
// This sits beside the MUL readers the way KrAnimationProvider does: when the
// user has pointed the client at a UOKR folder, a land tile's pixels come from
// KR's terrain textures instead of art.mul and texmaps.mul; anything KR has no
// terrain for falls through to the 2D art. No KR asset ships with the client.
//
// How a 2D land tile id finds KR art:
//
//   land tile id -> KR terrain id      KrTerrainMap, swept out of the facets
//   terrain id   -> ground texture     KrTerrainArt, the slot0/name/base chain
//
// The first step works because a KR sector's land record carries both ids, and
// the mapping is very nearly a function: over 69.8 million tiles across all
// five facets, 1,557 land tile ids map to a terrain and only 2 of them ever
// carry more than one, which is what lets the renderer pick KR art knowing
// nothing but the graphic. The sweep takes about 2.4 seconds.
//
// WHAT THIS IS NOT. KR draws terrain as a mesh with one large texture tiled
// across it, blending a whole layer set per terrain. Here each tile takes the
// crop of that texture at (x % period, y % period), so the pattern runs across
// the ground rather than repeating in every diamond, but it still repeats on a
// period of at most 8 tiles, and only one layer of KR's set is used. It is
// KR's art on ClassicUO's grid, not KR's terrain renderer.
//

using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using ClassicUO.KR;
using ClassicUO.KR.World;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Assets
{
    public sealed class KrTerrainProvider : IDisposable
    {
        private readonly KrAssetStore _store;
        private readonly KrTerrainMap _map;
        private readonly KrTerrainArt _art;
        private readonly ushort[] _terrainByTexId;
        private readonly bool[] _suppressed;

        private KrTerrainProvider(
            KrAssetStore store, KrTerrainMap map, ushort[] terrainByTexId, bool[] suppressed, bool enhanced)
        {
            _store = store;
            _map = map;
            _art = new KrTerrainArt(store) { Enhanced = enhanced };
            IsEnhanced = enhanced;
            _terrainByTexId = terrainByTexId;
            _suppressed = suppressed;
        }

        public static KrTerrainProvider Instance { get; private set; }

        public static bool IsActive => Instance != null;

        /// <summary>Whether the ground comes from an Enhanced Client install (ec_terrain).</summary>
        public bool IsEnhanced { get; }

        /// <summary>Whether uniform composite land is composed on the GPU (kr_gpu_terrain).</summary>
        public static bool GpuTerrain { get; set; }

        /// <summary>
        /// The composite layers the GPU path draws a land graphic's terrain
        /// with, or null to keep it on the CPU: water-class terrains have
        /// their own modes, and terrains that are not a plain three-layer
        /// composite (stacked or single layers) are not handled yet.
        /// </summary>
        /// <summary>The composite layers for a terrain id, or null when the GPU path cannot draw it.</summary>
        public KrTerrainArt.CompositeLayers GpuCompositeTerrain(int t) =>
            !GpuTerrain || t < 0 || _art.IsWaterSurface(t) || _art.IsStars(t) || _art.IsLava(t) ? null : _art.Composite(t);

        /// <summary>
        /// The terrains laid over a border tile's ground, in KR's order (the
        /// CPU composite's own, KrTerrainArt.BlendOrder), with their weights;
        /// those that reach no part of the tile are left out.
        /// </summary>
        public void GpuBlend(int[] block5, List<int> layers, List<KrTerrainBlend.WeightGrid> grids)
        {
            layers.Clear();
            grids.Clear();

            foreach (int t in _art.BlendOrder(block5, ascending: false))
            {
                KrTerrainBlend.WeightGrid g = new(block5, t, _art.Exponent(t));

                if (!g.IsEmpty)
                {
                    layers.Add(t);
                    grids.Add(g);
                }
            }
        }

        public KrTerrainArt.CompositeLayers GpuComposite(ushort graphic, out int terrainId)
        {
            terrainId = -1;

            if (!GpuTerrain || !TryGetTerrain(graphic, out ushort t)
                || _art.IsWaterSurface(t) || _art.IsStars(t) || _art.IsLava(t))
            {
                return null;
            }

            terrainId = t;

            return _art.Composite(t);
        }

        /// <summary>
        /// Whether water - land tiles and water statics - is drawn with KR art.
        /// Off until there is a water shader: KR draws water with one
        /// (UOWaterShader), and its plain texture alone gave jagged, mismatched
        /// shorelines. Off, all water is 2D, so it at least matches itself.
        /// </summary>
        public const bool KrWater = false;

        /// <summary>How many land tile ids resolved to a KR terrain.</summary>
        public int MappedLandTiles => _map.Known;

        /// <summary>
        /// Builds the land tile to terrain table by sweeping the facet
        /// packages, and the texture-id table from the client's own tile data
        /// so the stretched-land path can be served too.
        /// </summary>
        /// <remarks>
        /// With <paramref name="ecPath"/> set to an Enhanced Client install,
        /// the ground is drawn from it instead: its land-to-terrain table
        /// (GameData.uop) replaces the facet sweep, and its terrain definitions
        /// and textures replace KR's. When that install cannot be read, KR is
        /// used as usual.
        /// </remarks>
        public static bool Initialize(string krPath, LandTiles[] landData, string ecPath = null)
        {
            Shutdown();

            bool enhanced = !string.IsNullOrWhiteSpace(ecPath)
                && File.Exists(Path.Combine(ecPath, KrLegacyTerrainMap.PackageName))
                && KrAssetStore.LooksLikeKrInstall(ecPath);

            if (!string.IsNullOrWhiteSpace(ecPath) && !enhanced)
            {
                Log.Warn($"EC terrain: '{ecPath}' is not an Enhanced Client install; using KR's terrain.");
            }

            string root = enhanced ? ecPath : krPath;

            if (string.IsNullOrWhiteSpace(root) || !KrAssetStore.LooksLikeKrInstall(root))
            {
                return false;
            }

            try
            {
                KrTerrainMap map = enhanced
                    ? KrTerrainMap.FromLegacy(KrLegacyTerrainMap.Load(Path.Combine(ecPath, KrLegacyTerrainMap.PackageName)))
                    : KrTerrainMap.Build(krPath, [0, 1, 2, 3, 4]);

                if (map.Known == 0)
                {
                    Log.Warn("KR terrain: the facet sweep found no land tiles; staying on 2D art.");

                    return false;
                }

                // texmaps are keyed by the land tile's TexID, not its graphic,
                // so carry the mapping across once here.
                ushort[] byTexId = new ushort[0x4000];
                Array.Fill(byTexId, KrTerrainMap.Unknown);

                if (landData != null)
                {
                    int limit = Math.Min(landData.Length, KrTerrainMap.LandGraphicCount);

                    for (int graphic = 0; graphic < limit; graphic++)
                    {
                        ushort texId = landData[graphic].TexID;

                        if (texId == 0 || texId >= byTexId.Length)
                        {
                            continue;
                        }

                        if (map.TryGet(graphic, out ushort terrain) && byTexId[texId] == KrTerrainMap.Unknown)
                        {
                            byTexId[texId] = terrain;
                        }
                    }
                }

                bool[] suppressed = FindDisagreeingTiles(map, landData);

                Instance = new KrTerrainProvider(
                    new KrAssetStore(root), map, byTexId, suppressed, enhanced);

                Log.Trace(
                    $"{(enhanced ? "EC" : "KR")} terrain: {map.Known} land tile ids mapped from {map.TilesScanned:N0} tiles " +
                    $"({map.Ambiguous} ambiguous).");

                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"KR terrain unavailable ({ex.Message}); staying on 2D art.");
                Instance = null;

                return false;
            }
        }

        // DIAGNOSTIC. The draw path falls through to the 2D art whenever any
        // link in the chain comes up empty, and it does so silently, which
        // made an in-game report of "only 2D ground" impossible to place.
        // The first few calls through KrTerrain.GetLand record what happened
        // and get written next to the client, so one run says which link
        // broke. Remove once the path is trusted.
        private static readonly System.Collections.Generic.List<string> _trace = [];
        private static bool _traceWritten;

        // DIAGNOSTIC. A histogram of what every land tile actually resolved
        // to, so the shapes on screen can be matched to land tile ids instead
        // of guessed at from a screenshot.
        private static readonly System.Collections.Generic.Dictionary<(int Graphic, string Outcome), int>
            _landTally = [];

        // DIAGNOSTIC. The same histogram for statics. Trees are the only
        // statics the chunk mesh excludes, so they take a different draw path
        // from everything else, and this says which path each graphic took.
        private static readonly System.Collections.Generic.Dictionary<(int Graphic, string Outcome), int>
            _staticTally = [];

        public static void NoteStatic(int graphic, string outcome)
        {
            lock (_staticTally)
            {
                _staticTally.TryGetValue((graphic, outcome), out int had);
                _staticTally[(graphic, outcome)] = had + 1;
            }
        }

        private static System.Collections.Generic.List<string> StaticTallyLines()
        {
            System.Collections.Generic.List<string> lines = [];

            lock (_staticTally)
            {
                if (_staticTally.Count == 0)
                {
                    return lines;
                }

                lines.Add("");
                lines.Add("statics drawn, by graphic and outcome (most drawn first):");
                lines.Add("   graphic  outcome                       drawn");

                foreach (var e in _staticTally.OrderByDescending(e => e.Value).Take(120))
                {
                    lines.Add($"   {e.Key.Graphic,7}  {e.Key.Outcome,-28} {e.Value,7}");
                }
            }

            return lines;
        }

        private static int _landNotes;

        public static void NoteLand(int graphic, string outcome)
        {
            bool dump;

            lock (_landTally)
            {
                _landTally.TryGetValue((graphic, outcome), out int had);
                _landTally[(graphic, outcome)] = had + 1;

                // Rewritten as the player moves. Land tiles resolve when a
                // chunk mesh is built, not every frame, so this counts in
                // hundreds rather than the hundred thousand a per-frame path
                // would need.
                dump = ++_landNotes % 500 == 0;
            }

            if (dump)
            {
                WriteTrace();
            }
        }

        private static System.Collections.Generic.List<string> LandTallyLines()
        {
            System.Collections.Generic.List<string> lines = [];

            lock (_landTally)
            {
                if (_landTally.Count == 0)
                {
                    return lines;
                }

                lines.Add("");
                lines.Add("land tiles drawn, by id and outcome (most drawn first):");
                lines.Add("   land   terrain  art        outcome             drawn");

                foreach (var e in _landTally.OrderByDescending(e => e.Value).Take(120))
                {
                    int graphic = e.Key.Graphic;

                    string terrain = "-";
                    string art = "-";

                    if (Instance != null && Instance._map.TryGet(graphic, out ushort t))
                    {
                        terrain = t.ToString();
                        art = Instance._art.GroundArtId(t) ?? "none";
                    }

                    lines.Add(
                        $"   {graphic,5}   {terrain,7}  {art,-10} {e.Key.Outcome,-18} {e.Value,7}");
                }
            }

            return lines;
        }

        public static void Note(string line)
        {
            if (_traceWritten)
            {
                return;
            }

            lock (_trace)
            {
                if (_trace.Count < 40)
                {
                    _trace.Add(line);

                    return;
                }

                _traceWritten = true;
            }

            WriteTrace();
        }

        public static void WriteTrace()
        {
            try
            {
                string path = System.IO.Path.Combine(AppContext.BaseDirectory, "kr-terrain-status.txt");

                System.Collections.Generic.List<string> lines =
                [
                    $"kr terrain active: {IsActive}",
                    $"land tile ids mapped: {(IsActive ? Instance.MappedLandTiles : 0)}",
                    "",
                    "first calls through the draw path:",
                ];

                lock (_trace)
                {
                    lines.AddRange(_trace);
                }

                lines.AddRange(LandTallyLines());
                lines.AddRange(StaticTallyLines());

                System.IO.File.WriteAllLines(path, lines);
                Log.Trace($"KR terrain: wrote {path}");
            }
            catch (Exception ex)
            {
                Log.Warn($"KR terrain: could not write the status file ({ex.Message}).");
            }
        }

        /// <summary>
        /// Land tile ids whose own wetness disagrees with the terrain they map
        /// to, which are left on the 2D art.
        /// </summary>
        /// <remarks>
        /// The facet sweep takes the commonest terrain for each land tile id,
        /// and for a handful of filler ids that lands on something the tile is
        /// plainly not. Land 16368 and 16370 are dry but map to terrain 5,
        /// whose other six ids are all named water and flagged wet, so KR's
        /// water texture was being painted inland.
        ///
        /// Deciding it by majority keeps this from being a list of magic ids:
        /// a terrain counts as wet when most of the land ids mapping to it
        /// are, and any id disagreeing with its own terrain is left alone.
        /// </remarks>
        private static bool[] FindDisagreeingTiles(KrTerrainMap map, LandTiles[] landData)
        {
            bool[] suppressed = new bool[KrTerrainMap.LandGraphicCount];

            if (landData == null)
            {
                return suppressed;
            }

            int limit = Math.Min(landData.Length, KrTerrainMap.LandGraphicCount);

            Dictionary<ushort, (int Wet, int Dry)> tally = [];

            for (int graphic = 0; graphic < limit; graphic++)
            {
                if (!map.TryGet(graphic, out ushort terrain))
                {
                    continue;
                }

                tally.TryGetValue(terrain, out (int Wet, int Dry) had);

                tally[terrain] = landData[graphic].IsWet
                    ? (had.Wet + 1, had.Dry)
                    : (had.Wet, had.Dry + 1);
            }

            int count = 0;

            for (int graphic = 0; graphic < limit; graphic++)
            {
                if (!map.TryGet(graphic, out ushort terrain)
                    || !tally.TryGetValue(terrain, out (int Wet, int Dry) t))
                {
                    continue;
                }

                if ((t.Wet > t.Dry) != landData[graphic].IsWet)
                {
                    suppressed[graphic] = true;
                    count++;
                }
            }

            if (count > 0)
            {
                Log.Trace(
                    $"KR terrain: {count} land tile ids disagree with their terrain's wetness "
                    + "and stay on the 2D art.");
            }

            return suppressed;
        }

        public static void Shutdown()
        {
            Instance?.Dispose();
            Instance = null;
        }

        /// <summary>The KR terrain for a land tile id.</summary>
        public bool TryGetTerrain(uint graphic, out ushort terrain)
        {
            terrain = KrTerrainMap.Unknown;

            if (graphic < (uint)_suppressed.Length && _suppressed[graphic])
            {
                return false;
            }

            return _map.TryGet((int)graphic, out terrain);
        }

        /// <summary>The KR terrain a stretched tile's texture id belongs to.</summary>
        public bool TryGetTerrainByTexId(uint texId, out ushort terrain)
        {
            terrain = KrTerrainMap.Unknown;

            if (texId >= (uint)_terrainByTexId.Length)
            {
                return false;
            }

            terrain = _terrainByTexId[texId];

            return terrain != KrTerrainMap.Unknown;
        }

        /// <summary>
        /// How many tiles this terrain's texture is spread across before it
        /// repeats. Tile (x, y) takes the crop at (x % period, y % period).
        /// </summary>
        /// <remarks>
        /// With variation on, the ground only repeats on the noise mask's
        /// period, so callers must cache and wrap on that instead.
        /// </remarks>
        public int PeriodFor(ushort terrain) =>
            _art.PeriodFor(terrain);

        /// <summary>A KR world-art texture by art id (e.g. "02000051"), as RGBA pixels.</summary>
        /// <summary>Whether KR draws this land graphic as water (see KrTerrainArt.IsWaterSurface).</summary>
        public bool IsWaterLand(ushort graphic) =>
            TryGetTerrain(graphic, out ushort terrain) && (_art.IsWaterSurface(terrain) || _art.IsStars(terrain) || _art.IsLava(terrain));

        /// <summary>Whether KR draws this land graphic as lava (terrain 10).</summary>
        public bool IsLavaLand(ushort graphic) =>
            TryGetTerrain(graphic, out ushort terrain) && _art.IsLava(terrain);

        /// <summary>Whether KR draws this land graphic as its star field (terrain 53).</summary>
        public bool IsStarsLand(ushort graphic) =>
            TryGetTerrain(graphic, out ushort terrain) && _art.IsStars(terrain);

        /// <summary>Whether KR draws this land graphic as swamp water (terrain 70).</summary>
        public bool IsSwampLand(ushort graphic) =>
            TryGetTerrain(graphic, out ushort terrain) && _art.IsSwampWater(terrain);

        /// <summary>
        /// Blends KR swamp water into a land tile that borders it: the water's
        /// blend weight (terrain 70 has exponent 1, so KR fades it into its
        /// neighbours) goes into the sprite's alpha as 255 * (1 - weight),
        /// and the shader's KR_LAND mode mixes the flowing water in by it.
        /// <paramref name="pixels"/> is a square tile, or the 44 px diamond.
        /// </summary>
        public void ApplyWaterEdge(int[] block5, uint[] pixels, int size, bool diamond)
        {
            if (block5 == null || pixels == null || block5[12] >= 0 && _art.IsSwampWater(block5[12]))
            {
                return;
            }

            List<int> water = [];

            foreach (int t in block5)
            {
                if (t >= 0 && !water.Contains(t) && _art.IsSwampWater(t))
                {
                    water.Add(t);
                }
            }

            if (water.Count == 0)
            {
                return;
            }

            float half = size / 2f;
            KrTerrainBlend.WeightGrid[] grids = [.. water.Select(t => new KrTerrainBlend.WeightGrid(block5, t, _art.Exponent(t)))];

            for (int py = 0; py < size; py++)
            {
                for (int px = 0; px < size; px++)
                {
                    int i = (py * size) + px;

                    if ((pixels[i] >> 24) == 0)
                    {
                        continue;
                    }

                    float u = diamond ? (px - half + py + 0.5f) / size : (px + 0.5f) / size;
                    float v = diamond ? (py - px + half + 0.5f) / size : (py + 0.5f) / size;
                    float w = 0f;

                    foreach (KrTerrainBlend.WeightGrid g in grids)
                    {
                        w += g.At(u, v);
                    }

                    uint a = (uint)Math.Clamp(MathF.Round(255f * (1f - Math.Min(w, 1f))), 1f, 255f);
                    pixels[i] = (pixels[i] & 0x00FFFFFF) | (a << 24);
                }
            }
        }

        public bool TryGetTexture(string artId, out uint[] pixels, out int width, out int height)
        {
            var image = _art.Texture(artId);
            pixels = image?.Pixels;
            width = image?.Width ?? 0;
            height = image?.Height ?? 0;

            return image is not null;
        }

        /// <summary>
        /// One tile of KR ground, resampled to <paramref name="size"/> and
        /// opaque. The caller supplies the shape: for the flat path that means
        /// masking with the land tile's diamond.
        /// </summary>
        public bool TryGetGroundTile(ushort terrain, int size, int vx, int vy, out uint[] pixels)
        {
            pixels = terrain == KrTerrainMap.Unknown ? null : _art.GroundTile(terrain, size, vx, vy);

            return pixels != null;
        }

        /// <summary>
        /// One land diamond, sampled in world space so the texture runs across
        /// the ground instead of restarting in every tile. Already masked to
        /// the diamond and opaque inside it.
        /// </summary>
        public bool TryGetGroundDiamond(ushort terrain, int size, int tileX, int tileY, out uint[] pixels)
        {
            pixels = terrain == KrTerrainMap.Unknown ? null : _art.GroundDiamond(terrain, size, tileX, tileY);

            return pixels != null;
        }

        /// <summary>
        /// KR terrain ids of the 5x5 tiles around one, from their land tile
        /// ids; -1 where unknown. Row-major over (y, x), the tile at 12.
        /// </summary>
        public int[] Block5(Func<int, int, int> landGraphicAt, int x, int y)
        {
            int[] block = new int[25];

            for (int dy = -2; dy <= 2; dy++)
            {
                for (int dx = -2; dx <= 2; dx++)
                {
                    int g = landGraphicAt(x + dx, y + dy);

                    block[((dy + 2) * 5) + dx + 2] =
                        g >= 0 && TryGetTerrain((uint)g, out ushort t) ? t : -1;
                }
            }

            return block;
        }

        /// <summary>Whether every known tile in the block is the centre's terrain.</summary>
        public static bool IsUniform(int[] block)
        {
            foreach (int t in block)
            {
                if (t >= 0 && t != block[12])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// A land diamond blended with its neighbours the way KR blends
        /// terrain; lower terrain ids drawn over higher ones, which is the
        /// order that matched the KR client.
        /// </summary>
        public bool TryGetBlendedDiamond(int[] block5, int size, int x, int y, out uint[] pixels)
        {
            pixels = _art.BlendedDiamond(block5, size, x, y, ascending: false);

            return pixels != null;
        }

        /// <summary>The stretched-path square version of <see cref="TryGetBlendedDiamond"/>.</summary>
        public bool TryGetBlendedTile(int[] block5, int size, int x, int y, out uint[] pixels)
        {
            pixels = _art.BlendedTile(block5, size, x, y, ascending: false);

            return pixels != null;
        }

        public void Dispose()
        {
            _art.Clear();
            _store.Dispose();
        }
    }
}
