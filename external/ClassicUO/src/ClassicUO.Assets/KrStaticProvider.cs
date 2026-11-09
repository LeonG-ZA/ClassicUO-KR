// SPDX-License-Identifier: BSD-2-Clause
//
// Alternate static art backed by a local Kingdom Reborn install.
//
// This sits beside KrTerrainProvider and works the same way: when the user has
// pointed the client at a UOKR folder, a static's pixels come from KR's world
// art instead of art.mul, and anything KR has no art for falls through to the
// 2D sprite. No KR asset ships with the client.
//
// Unlike the terrain, no table has to be built first. Texture.uop's 0000xxxx
// world-art block is keyed directly by classic graphic id, so the lookup is
// immediate and startup costs nothing.

using System;
using System.Collections.Generic;
using System.Linq;
using ClassicUO.KR;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Assets
{
    public sealed class KrStaticProvider : IDisposable
    {
        private readonly KrAssetStore _store;

        // KR's install behind EC's statics, for the scenes EC's tileart no
        // longer names (candles, sconces, fireplaces, ovens); null otherwise.
        private readonly KrAssetStore _krScenes;
        private KrStringDictionary _krStrings;
        private readonly KrStaticArt _art;
        private readonly KrTerrainArt _terrainArt;
        private readonly Dictionary<ushort, KrTerrainArt.CompositeLayers> _floorComposite = [];
        private readonly KrStaticPairs _pairs;
        private readonly ArtLoader _arts;
        private readonly Dictionary<ushort, (int X, int Y, int W, int H)?> _bounds2D = [];
        private readonly Dictionary<ushort, KrTileArt?> _tileArt = [];
        private readonly Dictionary<ushort, int> _source = [];
        private readonly Dictionary<ushort, List<int>> _refs = [];
        private KrStringDictionary _strings;
        private bool _stringsLoaded;

        private KrStringDictionary Strings
        {
            get
            {
                if (!_stringsLoaded)
                {
                    _stringsLoaded = true;

                    string path = System.IO.Path.Combine(_store.RootPath, KrStringDictionary.PackageName);

                    if (System.IO.File.Exists(path))
                    {
                        _strings = KrStringDictionary.Load(path);
                    }
                }

                return _strings;
            }
        }

        /// <summary>Whether sprites keep KR's native resolution (kr_native_detail).</summary>
        public static bool NativeDetail { get; set; }

        private KrStaticProvider(
            KrAssetStore store, double scale, KrStaticPairs pairs, ArtLoader arts, bool enhanced, KrAssetStore krScenes = null)
        {
            _store = store;
            _krScenes = krScenes;
            IsEnhanced = enhanced;
            // Native detail keeps KR's own pixels: 1 / scale per display pixel.
            _art = new KrStaticArt(store, scale) { Detail = NativeDetail && scale > 0 ? 1.0 / scale : 1.0 };
            _terrainArt = new KrTerrainArt(store) { Enhanced = enhanced };
            _pairs = pairs;
            _arts = arts;
        }

        /// <summary>How many art ids KR always draws with a second one.</summary>
        public int PairedGraphics => _pairs.Paired;

        public static KrStaticProvider Instance { get; private set; }

        public static bool IsActive => Instance != null;

        /// <summary>Whether statics come from an Enhanced Client install (ec_statics).</summary>
        public bool IsEnhanced { get; }

        /// <summary>How much KR art is shrunk to sit on the 2D tile grid.</summary>
        public double Scale => _art.Scale;

        /// <remarks>
        /// With <paramref name="ecPath"/> set to an Enhanced Client install,
        /// statics are drawn from it instead: its tileart (version 4), world
        /// art, strings and facets. When that install cannot be read, KR is
        /// used as usual.
        /// </remarks>
        public static bool Initialize(string krPath, double scale, ArtLoader arts, string ecPath = null)
        {
            Shutdown();

            bool enhanced = !string.IsNullOrWhiteSpace(ecPath)
                && System.IO.File.Exists(System.IO.Path.Combine(ecPath, KrLegacyTerrainMap.PackageName))
                && KrAssetStore.LooksLikeKrInstall(ecPath);

            if (!string.IsNullOrWhiteSpace(ecPath) && !enhanced)
            {
                Log.Warn($"EC statics: '{ecPath}' is not an Enhanced Client install; using KR's statics.");
            }

            string root = enhanced ? ecPath : krPath;

            if (string.IsNullOrWhiteSpace(root) || !KrAssetStore.LooksLikeKrInstall(root))
            {
                return false;
            }

            try
            {
                KrStaticPairs pairs = enhanced
                    ? KrStaticPairs.Build(root, [0, 1, 2, 3, 4],
                        KrLegacyTerrainMap.Load(System.IO.Path.Combine(root, KrLegacyTerrainMap.PackageName)))
                    : KrStaticPairs.Build(root, [0, 1, 2, 3, 4]);

                // EC's tileart dropped the flame or glow from 41 statics -
                // candles, sconces, fireplaces, ovens - though EC still ships
                // the scenes; KR's tileart supplies them when KR is installed
                //.
                KrAssetStore krScenes = enhanced && !string.IsNullOrWhiteSpace(krPath)
                    && KrAssetStore.LooksLikeKrInstall(krPath) ? new KrAssetStore(krPath) : null;

                Instance = new KrStaticProvider(new KrAssetStore(root), scale, pairs, arts, enhanced, krScenes);

                Log.Trace(
                    $"{(enhanced ? "EC" : "KR")} statics: on, drawing at {scale:0.###} of KR's own size; "
                    + $"{pairs.Paired} art ids are drawn with a companion "
                    + $"(from {pairs.Placements:N0} placements).");

                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"KR statics unavailable ({ex.Message}); staying on 2D art.");
                Instance = null;

                return false;
            }
        }

        public static void Shutdown()
        {
            Instance?.Dispose();
            Instance = null;
        }

        /// <summary>
        /// The KR sprite for a classic static graphic, already cropped to its
        /// own art and scaled to the 2D tile grid.
        /// </summary>
        /// <param name="keepFit">
        /// True to skip KR's own anchor and fit the art to the 2D sprite's
        /// bounds instead. Used for wet statics: KR's water textures are large
        /// and, placed by KR's anchor, spread across the coastline.
        /// </param>
        public bool TryGetSprite(
            ushort graphic, int fitWidth, int fitHeight, bool keepFit,
            out uint[] pixels, out int width, out int height) =>
            TryGetSprite(graphic, fitWidth, fitHeight, keepFit, false, 0, 0, out pixels, out width, out height, out _, out _);

        /// <summary>As the main overload, without the display size.</summary>
        public bool TryGetSprite(
            ushort graphic, int fitWidth, int fitHeight, bool keepFit, bool flat, int tileX, int tileY,
            out uint[] pixels, out int width, out int height) =>
            TryGetSprite(graphic, fitWidth, fitHeight, keepFit, flat, tileX, tileY, out pixels, out width, out height, out _, out _);

        /// <summary>
        /// Whether this static's sprite depends on where it is: a flat piece
        /// whose texture runs on across several tiles. The renderer keeps one
        /// sprite per position within the span for these.
        /// </summary>
        public bool IsPositional(ushort graphic, int fitWidth, int fitHeight, bool keepFit, bool flat) =>
            !keepFit && IsFlatPiece(fitWidth, fitHeight, flat || LaidLikeTerrain(graphic));

        /// <summary>
        /// A static KR draws as terrain (UOStaticTerrainShader) lies flat
        /// whatever tiledata says: the lava rim 0x3286-0x3295 is 1 high and not
        /// a surface, so it was drawn upright as its 64 px mask texture -
        /// brown stepped squares - where KR lays Lava_Grey cut by that mask.
        /// </summary>
        private bool LaidLikeTerrain(ushort graphic) => FloorComposite(graphic) is not null;

        /// <summary>
        /// A piece that lies on the floor: tiledata height 0 and a 2D sprite
        /// that is one floor diamond. Decided from tiledata, not from the
        /// tileart region: carpet corner 2788 is the one piece in its set with
        /// a full region, (0,0)-(63,63), and was drawn standing up because of
        /// it. Carpets are height 0; a bench (2917) and a fallen log (3319),
        /// also 44 px sprites, are height 1.
        /// </summary>
        /// <remarks>
        /// A 2D sprite only a few pixels tall is not a floor: KR's cave walls
        /// (0x2F50-0x2F55, 0x31B3-0x31B8) are height 0 with blank 44x1 2D art
        /// and real KR art placed by tileart, and were being laid flat as
        /// diamonds. Floor pieces are 29-44 px tall.
        /// </remarks>
        /// <summary>
        /// Whether a record's KR art fills less of its texture than a whole
        /// floor texture does (carpet 2788: region 63 of 64 px) - a sprite
        /// drawn in the corner of a larger texture.
        /// </summary>
        /// <remarks>
        /// A region that fills the piece's own texture is not a sprite, whatever
        /// source it was moved to: terracotta roof edges 0x0563-0x0569 give a
        /// 65 px region on their own 64 px coping texture, which sent them to
        /// the 256 px roof texture as bare squares that stuck out past the wall.
        /// </remarks>
        private bool IsSpriteArt(KrTileArt? tileArt, int source, int graphic) =>
            tileArt is { HasRegion: true } t && _art.Texture(source) is { } texture
            && t.CornerX - t.RegionX + 2 < texture.Width
            && !(source != graphic && _art.Texture(graphic) is { } own && t.CornerX - t.RegionX + 2 >= own.Width);

        private static bool IsFlatPiece(int fitWidth, int fitHeight, bool flat) =>
            flat && fitWidth == 44 && fitHeight >= MinFlatHeight && fitHeight <= 44;

        private const int MinFlatHeight = 8;

        /// <param name="displayWidth">The size it is drawn at before zoom; the
        /// pixels are larger when they keep KR's native detail.</param>
        public bool TryGetSprite(
            ushort graphic, int fitWidth, int fitHeight, bool keepFit, bool flat, int tileX, int tileY,
            out uint[] pixels, out int width, out int height, out int displayWidth, out int displayHeight)
        {
            bool found = TryGetSpriteCore(graphic, fitWidth, fitHeight, keepFit, flat, tileX, tileY,
                out pixels, out width, out height, out KrStaticSprite sprite);

            displayWidth = sprite?.DisplayWidth ?? width;
            displayHeight = sprite?.DisplayHeight ?? height;

            return found;
        }

        private bool TryGetSpriteCore(
            ushort graphic, int fitWidth, int fitHeight, bool keepFit, bool flat, int tileX, int tileY,
            out uint[] pixels, out int width, out int height, out KrStaticSprite spriteOut)
        {
            pixels = null;
            width = height = 0;
            spriteOut = null;

            flat |= LaidLikeTerrain(graphic);

            // NOT merged with its companion, deliberately.
            //
            // KR does place a tree as a trunk static plus a leaves static on
            // one tile - that part is real and measured - but so does the 2D
            // map, and ClassicUO already draws both. Compositing the companion
            // in as well forced summer foliage onto trees the client was
            // drawing correctly: the world in question was Felucca, where the
            // trees are bare, and the KR client that looked "right" renders
            // Trammel whichever facet you are in, so it was never a valid
            // reference.
            //
            // KrStaticPairs is kept because the pairing it measures is sound
            // and the table is wanted for the analysis tools.
            // Place KR's art where the 2D sprite's own art sits inside its box,
            // rather than stretching it across the whole box: a 2D sprite often
            // carries transparent margin, and filling that pushed objects down
            // into the ground.
            (int X, int Y, int W, int H)? bounds = Bounds2D(graphic);

            KrStaticSprite sprite;

            // Every static is fitted to the 2D art's own bounds inside the 2D
            // box. Drawing non-tiling statics at KR's own size (section 18) put
            // KR's large water textures over the coastline as rectangles, so it
            // is off until KR's real sizing rule is known. `tiles` is kept for
            // when it is.
            // KR's own anchor, read from tileart.uop (section 19), when the
            // record has one. This is what KR does, and it lines up with the
            // 2D client to about 2 px.
            int source = graphic;
            KrTileArt? tileArt = keepFit ? null : TileArt(graphic, out source);

            // Flat water stays 2D for now. Only 6039 of the water set
            // 6039-6044 has KR art (a 256x256 texture spanning four tiles;
            // 6038, 6061 and 6062 are shoreline masks), so drawing it put dark
            // patches among the 2D tiles - grey shapes squeezed into the box.
            // KR draws water with its own shader (UOWaterShader), not as art.
            // Water is animated, so it is drawn per object (View.cs), which
            // does not pass the flat flag: decide from the sprite alone.
            if (keepFit && IsFlatPiece(fitWidth, fitHeight, true))
            {
                return false;
            }

            // A piece lying on the floor is laid onto the tile like terrain
            // (section 19), whatever its region says.
            // A floor whose KR shader is UOStaticTerrainShader is drawn like
            // terrain: its three layers composited in world space (section 30).
            // A floor piece whose KR art is a ready-drawn diamond sprite, not a
            // floor texture, draws as a sprite: teleporter tiles 6122-6157 are
            // a 69 px diamond in the corner of a 128 px texture, which the flat
            // path read as a two-tile floor and showed a quarter of per tile.
            bool spriteArt = IsSpriteArt(tileArt, source, graphic);

            // A piece drawn from its own art over a base (roof edges) is laid
            // tile by tile; the world-space composite below flips its art.
            if (!keepFit && IsFlatPiece(fitWidth, fitHeight, flat) && !spriteArt && OwnArtOverBase(graphic) is { } decal
                && _art.FlatDiamond(decal.Art, decal.Base, tileX, tileY) is { } laidDecal)
            {
                pixels = laidDecal.Pixels;
                width = laidDecal.Width;
                height = laidDecal.Height;

                return true;
            }

            if (!keepFit && IsFlatPiece(fitWidth, fitHeight, flat) && FloorComposite(graphic) is { } floor && !spriteArt)
            {
                pixels = KrTerrainArt.ComposeTile(floor, 44, tileX, tileY, diamond: true);
                width = height = 44;

                // Seam overlap, as FlatDiamond's (KrStaticArt.ExtendDiamond).
                KrStaticArt.ExtendDiamond(pixels, 44);

                // A snow edge (6085) is shaped by the art its fourth layer
                // names (00006085_snow), a lava rim 0x355B by another piece's
                // (00012963_mask_lava): that art's alpha cuts the composite.
                // A centre piece (6077) names a ground texture with no static
                // art there and covers the tile. Drawn from their own art,
                // snow centres came out bright and edges bluish.
                if (FloorMask(graphic) is int maskArt && _art.FlatDiamond(maskArt, maskArt, tileX, tileY) is { Width: 44, Height: 44 } shape)
                {
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        pixels[i] = (pixels[i] & 0x00FFFFFF) | (shape.Pixels[i] & 0xFF000000);
                    }
                }

                return true;
            }

            if (!keepFit && IsFlatPiece(fitWidth, fitHeight, flat) && !spriteArt)
            {
                int under = FlatBase(graphic);

                // No world art at all (stone pavers 1309-1312): KR lays these
                // as ground with a terrain texture the record names.
                if (_art.Texture(under) is null && _art.Texture(graphic) is null)
                {
                    under = FloorTerrain(graphic) ?? under;
                }

                KrStaticSprite laid = _art.FlatDiamond(graphic, under, tileX, tileY);

                if (laid is not null)
                {
                    pixels = laid.Pixels;
                    width = laid.Width;
                    height = laid.Height;

                    return true;
                }
            }

            // A record whose region is a sliver does not describe the art;
            // fit the static's own texture into the 2D box instead.
            if (tileArt is { HasRegion: false })
            {
                tileArt = null;
                source = graphic;
            }

            if (tileArt is { } t)
            {
                // A slice of a large object draws its strip of the owner's
                // texture, placed by its own offsets; the strips meet up.
                sprite = _art.SpriteOnTile(graphic, t, source, null);

                // The record says what KR draws here. If that is nothing - a
                // region outside its texture, or a source with no KR art -
                // drawing the 2D art instead would put a second copy of a
                // statue beside the KR one, so the static draws nothing.
                // An empty sprite would make the callers fall back to 2D, so a
                // slice that draws nothing gets a transparent pixel instead.
                if (sprite is null && source != graphic)
                {
                    pixels = new uint[2];
                    width = 2;
                    height = 1;

                    return true;
                }
            }
            else if (bounds is { } b && fitWidth > 0 && fitHeight > 0)
            {
                sprite = _art.SpriteInBox(graphic, -1, fitWidth, fitHeight, b.X, b.Y, b.W, b.H);
            }
            else
            {
                sprite = _art.SpriteWith(graphic, -1, 0, 0);
            }

            if (sprite is null)
            {
                return false;
            }

            pixels = sprite.Pixels;
            width = sprite.Width;
            height = sprite.Height;
            spriteOut = sprite;

            return true;
        }

        /// <summary>
        /// Where the 2D sprite's own art sits inside its width and height, or
        /// null when it has none.
        /// </summary>
        private KrTileArt? TileArt(ushort graphic, out int source)
        {
            if (_tileArt.TryGetValue(graphic, out KrTileArt? cached))
            {
                source = _source.TryGetValue(graphic, out int s) ? s : graphic;

                return cached;
            }

            byte[] raw = KrTileArt.ReadRaw(_store, graphic);
            KrTileArt? record = raw is null ? null : KrTileArt.Parse(raw);

            source = graphic;

            if (record is not null && raw is not null && Strings is { } strings)
            {
                source = KrTileArt.SourceGraphic(raw, graphic, strings) ?? graphic;
            }

            _refs[graphic] = record is not null && raw is not null && Strings is { } names
                ? KrTileArt.References(raw, graphic, names)
                : [];

            // The region must fit inside the texture it is cut from. Where the
            // pick is missing or too small, the first reference that holds it
            // is the source: 130, a tall stone wall (region 65x194), names 120,
            // a 64x64 tan block, before 128 (128x256). 347 records change.
            // A group of slices (a large tree) names a source with no texture;
            // the texture is on another member of the group (section 19).
            if (record is { } grp && grp.HasRegion && !Holds(source, grp) && Strings is { } groupNames
                && KrTileArt.GroupOwner(source, g => KrTileArt.ReadRaw(_store, g), groupNames, g => Holds(g, grp)) is { } owner)
            {
                source = owner;
            }

            if (record is { } fit && fit.HasRegion && !Holds(source, fit))
            {
                foreach (int candidate in _refs[graphic])
                {
                    if (Holds(candidate, fit))
                    {
                        source = candidate;
                        break;
                    }
                }
            }

            _source[graphic] = source;

            // A record for some other graphic, or a corner that no texture
            // could have, means the fixed block does not apply here.
            if (record is { } r && (r.Graphic != graphic || r.CornerX < 0 || r.CornerY < 0))
            {
                record = null;
            }

            _tileArt[graphic] = record;

            return record;
        }

        private bool Holds(int texture, KrTileArt region) =>
            _art.TextureSize(texture) is { } size && region.CornerX < size.Width + 1 && region.CornerY < size.Height + 1;

        /// <summary>
        /// What a flat piece is laid over: the KR base its record leads to,
        /// or itself when it needs none.
        /// </summary>
        /// <remarks>
        /// *Inferred, not read from the format*. A carpet's motif pieces are transparent decals meant to
        /// lie on an opaque base (2758 on 2760). The record names the base
        /// sometimes first, sometimes later, sometimes only through another
        /// piece (2751 names 2749, which names 2750), so every reference is
        /// tried, then references of references. A base whose average colour
        /// is far from the 2D tile's is rejected - edge pieces otherwise reach
        /// the other carpet family's base and pick up its colour. Checked by
        /// rendering a real carpet from the map.
        /// </remarks>
        private readonly Dictionary<ushort, int?> _floorTerrain = [];

        /// <summary>
        /// The terrain texture a floor piece with no art of its own is laid
        /// with: the record's 0200 name, as its A sibling when that exists
        /// (Stone_Medium_B -> 02000260_Stone_Medium_A, KR's blue-grey pavers).
        /// </summary>
        private int? FloorTerrain(ushort graphic)
        {
            if (_floorTerrain.TryGetValue(graphic, out int? cached))
            {
                return cached;
            }

            int? result = null;
            TileArt(graphic, out int source);

            if (KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
                && KrTileArt.TerrainTexture(raw, source, strings) is { } art)
            {
                int a = art - (art % 10);
                result = _art.Texture(a) is not null ? a : _art.Texture(art) is not null ? art : null;
            }

            _floorTerrain[graphic] = result;

            return result;
        }

        /// <summary>
        /// The terrain-style composite a floor static is drawn with, from its
        /// tileart record's KR shader (UOStaticTerrainShader with three
        /// layers: stone pavers, cave floor); null for anything else.
        /// </summary>
        private KrTerrainArt.CompositeLayers FloorComposite(ushort graphic)
        {
            if (_floorComposite.TryGetValue(graphic, out KrTerrainArt.CompositeLayers cached))
            {
                return cached;
            }

            KrTerrainArt.CompositeLayers result = null;

            if (KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
                && KrTileArt.KrShader(raw, strings) is { Class: "UOStaticTerrainShader", Layers.Count: 3 or 4 } shader)
            {
                // Four layers (snow 6077-6092): the first three are the
                // terrain composite (Snow_A, Snow_B, noise) and the fourth
                // only shapes the piece - see FloorMask.
                result = _terrainArt.CompositeFrom(
                    [.. shader.Layers.Take(3).Select(l => (KrTerrainArt.ArtId(l.Texture), l.Scale))]);

                // A piece's own mask (00004894_mask_lava), one per tile.
                if (result is not null && shader.Layers[2].Texture.Contains("_mask", StringComparison.OrdinalIgnoreCase))
                {
                    result = result with { PieceMask = true };
                }
            }

            _floorComposite[graphic] = result;

            return result;
        }

        /// <summary>
        /// A 4-layer floor shader whose first and third layers are the same art,
        /// which is the piece's own image over the base its second layer names.
        /// </summary>
        private (int Art, int Base)? OwnArtOverBase(ushort graphic) =>
            KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
            && KrTileArt.KrShader(raw, strings) is { Class: "UOStaticTerrainShader", Layers.Count: 4 } shader
            && KrTerrainArt.ArtId(shader.Layers[0].Texture) is { } own
            && own == KrTerrainArt.ArtId(shader.Layers[2].Texture)
            && int.TryParse(own, out int art)
            && int.TryParse(KrTerrainArt.ArtId(shader.Layers[1].Texture), out int under)
            && _art.Texture(art) is not null && _art.Texture(under) is not null
                ? (art, under)
                : null;

        /// <summary>The static art id a 4-layer floor shader's last layer names, if it is one.</summary>
        private int? FloorMask(ushort graphic) =>
            KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
            && KrTileArt.KrShader(raw, strings) is { Class: "UOStaticTerrainShader", Layers.Count: 4 } shader
            && int.TryParse(KrTerrainArt.ArtId(shader.Layers[3].Texture), out int id)
            && id is > 0 and < 0x10000
                ? id
                : null;

        private readonly Dictionary<ushort, (string Scene, bool Blank, float X, float Y, float Height, float Scale)> _scenes = [];

        /// <summary>
        /// The KR effect scene a static's tileart record names (swamp bubbles
        /// 0x322C name 00012844_Bubble_Swamp.nif), or null; and whether its KR
        /// shader draws 00005703_blank, i.e. KR shows only the scene.
        /// </summary>
        /// <summary>
        /// Whether a scene name at <paramref name=o/> is followed by a real
        /// placement - three plain floats and a scale. The scan reads every
        /// byte offset, and noise matches: grey brick wall 53's second shader
        /// decodes as lightning_hit.ems (its "scale" 2.8e-40), which ran on
        /// every such wall and filled Britain with smoke.
        /// </summary>
        private static bool LooksLikeScenePlacement(byte[] raw, int o)
        {
            if (o + 20 > raw.Length)
            {
                return false;
            }

            for (int k = 4; k <= 12; k += 4)
            {
                float v = BitConverter.ToSingle(raw, o + k);

                if (!float.IsFinite(v) || (v != 0f && (MathF.Abs(v) < 0.001f || MathF.Abs(v) > 1000f)))
                {
                    return false;
                }
            }

            float scale = BitConverter.ToSingle(raw, o + 16);

            return float.IsFinite(scale) && scale is >= 0.001f and <= 100f;
        }

        /// <summary>The height offset (z units) and scale tileart gives a static's scene.</summary>
        public void ScenePlacement(ushort graphic, out float height, out float scale) =>
            ScenePlacement(graphic, out _, out _, out height, out scale);

        /// <summary>
        /// The placement after a static's scene name: an offset (x, y) along
        /// the tile axes, a height and a scale. Candles carry x: 0x142C -0.3,
        /// 0x1434 0.25, the candelabra 0xB1D -0.27.
        /// </summary>
        public void ScenePlacement(ushort graphic, out float x, out float y, out float height, out float scale)
        {
            SceneFor(graphic, out _);
            (string _, bool _, x, y, height, scale) = _scenes[graphic];
        }

        /// <summary>One scene a static plays, with its placement (tileart).</summary>
        public readonly record struct SceneRecord(string Scene, float X, float Y, float Height, float Scale, float Size = 1f);

        private readonly Dictionary<ushort, List<SceneRecord>> _sceneSets = [];

        /// <summary>
        /// Every scene a static plays, the first being <see cref="SceneFor"/>'s.
        /// Tileart keeps them in groups by hue - a u32 hue and a u32 count, then
        /// count 31-byte records (name, x, y, height, scale, flags); KR plays
        /// the whole default (hue 0) group. A waterfall 0x34F3 is
        /// 00013555_waterfall, its highlights twice and its downlong drops;
        /// 0x34FF is 00013567_waterfall, splash01.ems and 03013567_waterfall
        /// (splat and stars: the mist) twice. The groups after it are the
        /// blood (0x21), swamp (0x34, 0x98) and acid (0x163) falls.
        /// </summary>
        public IReadOnlyList<SceneRecord> ScenesFor(ushort graphic)
        {
            SceneFor(graphic, out _);

            return _sceneSets.TryGetValue(graphic, out List<SceneRecord> set) ? set : [];
        }

        /// <summary>
        /// Scenes KR's tileart gives these statics that EC's does not (candles,
        /// fireplaces, an oven, a campfire, lava sparks): names and placements
        /// as KR's tileart lists them, used only for EC records with no scene
        /// of their own and no KR install to fall back to.
        /// </summary>
        private static readonly Dictionary<ushort, SceneRecord[]> EcMissingScenes = new()
        {
            [0x03AE] = [new("00000942_PostWater.nif", 0f, 0f, 0f, 1f)],
            [0x0461] = [new("fireplaceGlow02.nif", 1f, 0f, 0f, 0.75f)],
            [0x046F] = [new("fireplaceGlow01.nif", -0.3f, 0.5f, 0f, 0.75f)],
            [0x0930] = [new("00002352_Oven.nif", 0f, 0f, 11f, 0.1f), new("fireplaceGlow01.nif", -0.1f, 0.5f, 0f, 0.5f)],
            [0x09FD] = [new("candleflame01.ems", -0.1f, 0f, -2f, 0.5f)],
            [0x0A02] = [new("candleflame01.ems", 0.1f, 0f, -1.5f, 0.75f)],
            [0x0A0F] = [new("candleflame01.ems", 0f, 0f, -7f, 0.5f)],
            [0x0B1A] = [new("candleflame01.ems", 0f, 0f, -2f, 0.5f)],
            [0x0B1B] = [new("candleflame01.ems", 0f, 0f, -2f, 0.5f)],
            [0x0B1C] = [new("candleflame01.ems", 0f, 0f, -2f, 0.5f)],
            [0x0B1D] = [new("candleflame01.ems", -0.27f, 0f, -3.35f, 0.45f), new("candleflame01.ems", 0.02f, 0f, -1f, 0.45f), new("candleflame01.ems", 0.3f, 0f, -0.1f, 0.45f)],
            [0x0B26] = [new("candleflame01.ems", -0.2f, 0f, 10.5f, 0.45f), new("candleflame01.ems", 0.05f, 0f, 13f, 0.45f), new("candleflame01.ems", 0.34f, 0f, 13.9f, 0.45f)],
            [0x0FAC] = [new("00003555_Fire.nif", 0f, 0f, 0f, 1f), new("00003555_Fire.nif", 0f, 0f, 0f, 1f)],
            [0x142C] = [new("candleflame01.ems", -0.3f, 0f, -6f, 0.5f)],
            [0x1430] = [new("candleflame01.ems", 0f, 0f, -5f, 0.5f)],
            [0x1434] = [new("candleflame01.ems", 0.25f, 0f, -2.5f, 0.5f)],
            [0x1854] = [new("candleflame01.ems", 0f, 0f, -3f, 0.45f)],
            [0x1858] = [new("candleflame01.ems", 0.05f, 0f, -1f, 0.45f)],
            [0x1C14] = [new("candleflame01.ems", -0.05f, 0f, -1.5f, 0.5f)],
            [0x236E] = [new("candleflame01.ems", 0f, 0f, 8f, 0.5f)],
            [0x2371] = [new("candleflame01.ems", 0.05f, 0f, 8f, 0.5f)],
            [0x3BB0] = [new("00015080_Lava_Sparks.nif", 0f, 0f, 0f, 0.5f), new("00015080_Lava_Sparks.nif", 0f, 0f, 0f, 0.5f)],
        };

        private static bool IsOurFlame(string scene) =>
            scene.StartsWith("torchflame", StringComparison.OrdinalIgnoreCase)
            || scene.StartsWith("candleflame", StringComparison.OrdinalIgnoreCase);

        /// <summary>Resources/flames/flame-placements.txt, written by tools/flame-tuner.</summary>
        private static readonly Lazy<Dictionary<ushort, List<SceneRecord>>> OurFlamePlacements = new(() =>
        {
            Dictionary<ushort, List<SceneRecord>> result = [];

            using System.IO.Stream stream = typeof(ClassicUO.KR.Effects.KrEffects).Assembly
                .GetManifestResourceStream("ClassicUO.KR.Resources.flames.flame-placements.txt");

            if (stream == null)
            {
                return result;
            }

            using System.IO.StreamReader reader = new(stream);

            while (reader.ReadLine() is { } line)
            {
                string[] p = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (p.Length < 6 || line[0] == '#')
                {
                    continue;
                }

                ushort graphic = Convert.ToUInt16(p[0], 16);

                if (!result.TryGetValue(graphic, out List<SceneRecord> flames))
                {
                    result[graphic] = flames = [];
                }

                System.Globalization.CultureInfo c = System.Globalization.CultureInfo.InvariantCulture;
                flames.Add(new SceneRecord(p[1], float.Parse(p[2], c), float.Parse(p[3], c), float.Parse(p[4], c), float.Parse(p[5], c),
                    p.Length > 6 ? float.Parse(p[6], c) : 1f));
            }

            return result;
        });

        public string SceneFor(ushort graphic, out bool blank)
        {
            if (!_scenes.TryGetValue(graphic, out (string Scene, bool Blank, float X, float Y, float Height, float Scale) found))
            {
                found = (null, false, 0f, 0f, 0f, 1f);

                if (KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings)
                {
                    List<SceneRecord> set = [];
                    FindScene(raw, strings, ref found, set);

                    if (found.Scene is null && _krScenes != null && KrTileArt.ReadRaw(_krScenes, graphic) is { } krRaw
                        && (_krStrings ??= KrStringDictionary.Load(
                            System.IO.Path.Combine(_krScenes.RootPath, KrStringDictionary.PackageName))) is { } krStrings)
                    {
                        FindScene(krRaw, krStrings, ref found, set);
                    }

                    // EC's record for this one candle names the torch's flame, which
                    // stands far too big and high on it. Placed by eye on the wick.
                    if (graphic == 0x0A28 && raw.Length >= 2 && BitConverter.ToUInt16(raw, 0) == 4
                        && string.Equals(found.Scene, "torchflame.ems", StringComparison.OrdinalIgnoreCase))
                    {
                        found = ("candleflame01.ems", found.Blank, 0.05f, -0.05f, -6.75f, 0.5f);
                        set.Clear();
                        set.Add(new SceneRecord(found.Scene, found.X, found.Y, found.Height, found.Scale));
                    }

                    // Still none, and the record is EC's (version 4): the flames
                    // and glows KR's tileart names for these statics and EC's does
                    // not. EC ships every one of these scenes itself.
                    if (found.Scene is null && raw.Length >= 2 && BitConverter.ToUInt16(raw, 0) == 4
                        && EcMissingScenes.TryGetValue(graphic, out SceneRecord[] missing))
                    {
                        SceneRecord first = missing[0];
                        found = (first.Scene, found.Blank, first.X, first.Y, first.Height, first.Scale);
                        set.AddRange(missing);
                    }

                    // EC's waterfall bottoms list no mist; KR's play splash01.ems
                    // (which EC still ships) there. Placement as KR's tileart gives it.
                    if (raw.Length >= 2 && BitConverter.ToUInt16(raw, 0) == 4 && set.Count > 0
                        && !set.Exists(r => r.Scene.Contains("splash", StringComparison.OrdinalIgnoreCase)))
                    {
                        float mistX = set[0].Scene.Equals("00013567_waterfall.nif", StringComparison.OrdinalIgnoreCase) ? -0.2f
                            : set[0].Scene.Equals("00013597_waterfall.nif", StringComparison.OrdinalIgnoreCase) ? 0.2f
                            : float.NaN;

                        if (!float.IsNaN(mistX))
                        {
                            set.Insert(1, new SceneRecord("splash01.ems", mistX, 0f, 0f, 0.5f));
                        }
                    }

                    // With only EC installed, our flames sit where the flame tuner put them.
                    if (_krScenes == null && raw.Length >= 2 && BitConverter.ToUInt16(raw, 0) == 4
                        && OurFlamePlacements.Value.TryGetValue(graphic, out List<SceneRecord> placed))
                    {
                        set.RemoveAll(r => IsOurFlame(r.Scene));
                        set.InsertRange(0, placed);
                        SceneRecord first = set[0];
                        found = (first.Scene, found.Blank, first.X, first.Y, first.Height, first.Scale);
                    }

                    _sceneSets[graphic] = set;

                    // 00014662_InvisibleWall draws nothing either: the
                    // paralysis fields (0x3967) - KR shows only their scene,
                    // where the 2D field art stood lit behind it here.
                    found.Blank = KrTileArt.KrShader(raw, strings) is { Layers.Count: > 0 } shader
                        && (shader.Layers[0].Texture.Contains("_blank", StringComparison.OrdinalIgnoreCase)
                            || shader.Layers[0].Texture.Contains("InvisibleWall", StringComparison.OrdinalIgnoreCase));
                }

                _scenes[graphic] = found;
            }

            blank = found.Blank;

            return found.Scene;
        }

        /// <summary>The first scene name in a record that is followed by a real placement, with that placement.</summary>
        private static void FindScene(
            byte[] raw, KrStringDictionary strings,
            ref (string Scene, bool Blank, float X, float Y, float Height, float Scale) found, List<SceneRecord> set)
        {
            for (int o = KrTileArt.VariableStart(raw); o + 4 <= raw.Length && found.Scene is null; o++)
            {
                uint index = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(o));

                // A NIF scene, or an .ems particle script (the bees
                // 0x91B over a beehive name bees.ems).
                if (index < (uint)strings.Count && strings[(int)index] is { } name
                    && (name.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                        || name.EndsWith(".ems", StringComparison.OrdinalIgnoreCase))
                    && LooksLikeScenePlacement(raw, o))
                {
                    found.Scene = name;

                    // After the name: an offset (x, y, height) and a
                    // scale - 0x34ED (0, 0, -15) and 0.3, 0x350E
                    // (0, 0, -9) and 0.25, the sparkles 0 and 1
                    //.
                    if (o + 20 <= raw.Length)
                    {
                        float px = BitConverter.ToSingle(raw, o + 4);
                        float py = BitConverter.ToSingle(raw, o + 8);
                        found.X = float.IsFinite(px) && MathF.Abs(px) < 16f ? px : 0f;
                        found.Y = float.IsFinite(py) && MathF.Abs(py) < 16f ? py : 0f;
                        float height = BitConverter.ToSingle(raw, o + 12);
                        float scale = BitConverter.ToSingle(raw, o + 16);
                        found.Height = float.IsFinite(height) && MathF.Abs(height) < 1000f ? height : 0f;
                        found.Scale = float.IsFinite(scale) && scale is > 0.001f and < 100f ? scale : 1f;
                    }

                    set.Add(new SceneRecord(name, found.X, found.Y, found.Height, found.Scale));

                    // The rest of its group: the count sits 8 bytes before the
                    // first name, the records follow 31 bytes apart (32 in the
                    // Enhanced Client's version 4 records).
                    int count = o >= 8 ? BitConverter.ToInt32(raw, o - 8) : 1;
                    int stride = raw.Length >= 2 && BitConverter.ToUInt16(raw, 0) == 4 ? 32 : 31;

                    for (int k = 1; k < count && k < 16; k++)
                    {
                        int r = o + (stride * k);

                        if (r + 20 > raw.Length
                            || System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(r)) is var next && next >= (uint)strings.Count
                            || strings[(int)next] is not { } nextName
                            || !(nextName.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || nextName.EndsWith(".ems", StringComparison.OrdinalIgnoreCase))
                            || !LooksLikeScenePlacement(raw, r))
                        {
                            break;
                        }

                        float nx = BitConverter.ToSingle(raw, r + 4), ny = BitConverter.ToSingle(raw, r + 8);
                        float nh = BitConverter.ToSingle(raw, r + 12), ns = BitConverter.ToSingle(raw, r + 16);
                        set.Add(new SceneRecord(nextName,
                            float.IsFinite(nx) && MathF.Abs(nx) < 16f ? nx : 0f,
                            float.IsFinite(ny) && MathF.Abs(ny) < 16f ? ny : 0f,
                            float.IsFinite(nh) && MathF.Abs(nh) < 1000f ? nh : 0f,
                            float.IsFinite(ns) && ns is > 0.001f and < 100f ? ns : 1f));
                    }
                }
            }
        }

        private readonly Dictionary<ushort, bool> _swamp = [];

        /// <summary>
        /// Whether KR draws this static as swamp water: its KR shader's first
        /// layer is a Swamp_Water terrain texture (swamp 0x326D, used for the
        /// sewer channels as well).
        /// </summary>
        public bool IsSwampWater(ushort graphic)
        {
            if (!_swamp.TryGetValue(graphic, out bool result))
            {
                result = KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
                    && KrTileArt.KrShader(raw, strings) is { Layers.Count: > 0 } shader
                    && shader.Layers[0].Texture.Contains("Swamp_Water", StringComparison.OrdinalIgnoreCase);
                _swamp[graphic] = result;
            }

            return result;
        }

        /// <summary>Whether KR has a tileart record for this static.</summary>
        public bool HasTileArt(ushort graphic) => KrTileArt.ReadRaw(_store, graphic) is not null;

        private readonly Dictionary<ushort, bool> _legacy = [];

        /// <summary>
        /// Whether KR draws this static with its 2D art: a sprite shader
        /// naming TileArtLegacy\&lt;graphic&gt;.tga, as the death vortex
        /// (0x3798, Trammel 5673,325) does past a blank first sprite. Such a
        /// static keeps its 2D animation.
        /// </summary>
        public bool UsesLegacyArt(ushort graphic)
        {
            if (!_legacy.TryGetValue(graphic, out bool result))
            {
                string legacy = "TileArtLegacy\\" + graphic + ".tga";
                // And only past a blank sprite: a forge's bellows (0x197E)
                // names its 2D art too, after its own KR model, and drawing
                // its 2D frames as well spilled fire and smoke over the KR
                // forge (Tokuno 766,1260).
                result = KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
                    && KrTileArt.Shaders(raw, strings).ToList() is { } shaders
                    && shaders.Any(d => d.Class == "UOSpriteShader"
                        && d.Layers.Count > 0 && d.Layers[0].Texture.EndsWith(legacy, StringComparison.OrdinalIgnoreCase))
                    && shaders.FirstOrDefault(d => d.Class == "UOSpriteShader" && d.Layers.Count > 0) is { } first
                    && first.Layers[0].Texture.Contains("_blank", StringComparison.OrdinalIgnoreCase)
                    // And no scene: a waterfall piece (0x34F3) has the same
                    // blank and legacy pair, but KR shows its scene, and its
                    // 2D frames poured a dashed stream over the Tokuno bridge
                    // at 804,1253.
                    && SceneFor(graphic, out _) == null;
                _legacy[graphic] = result;
            }

            return result;
        }

        /// <summary>Whether KR draws this static as a terrain-style floor composite.</summary>
        public bool HasFloorComposite(ushort graphic) => FloorComposite(graphic) is not null;

        private readonly Dictionary<ushort, bool> _lava = [];

        /// <summary>
        /// Lava KR draws with its water shader (UOWaterShader with
        /// 01000012_lava_normal and 02000100_Lava_A: 0x12F4-0x1318), not with
        /// the animated 2D art.
        /// </summary>
        public bool IsLava(ushort graphic)
        {
            if (!_lava.TryGetValue(graphic, out bool result))
            {
                result = KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
                    && KrTileArt.KrShader(raw, strings) is { Class: "UOWaterShader", Layers.Count: > 1 } shader
                    && shader.Layers[0].Texture.Contains("lava_normal", StringComparison.OrdinalIgnoreCase);
                _lava[graphic] = result;
            }

            return result;
        }

        private readonly Dictionary<ushort, bool> _notWater = [];

        /// <summary>
        /// A wet, flat static KR does not draw as water: its UOWaterShader
        /// names no water texture. Blood (0x122A-0x122F) is wet in tiledata
        /// and KR draws it with that shader, but over 00004651_blood and
        /// friends - a still pool. Drawn as shore water here, its own soft
        /// alpha met the shoreline's pulsing test and the pool blinked.
        /// </summary>
        public bool IsNotWater(ushort graphic)
        {
            if (!_notWater.TryGetValue(graphic, out bool result))
            {
                result = KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings
                    && KrTileArt.Shaders(raw, strings).FirstOrDefault(d => d.Class == "UOWaterShader") is { } shader
                    && !shader.Layers.Any(l => l.Texture.Contains("water", StringComparison.OrdinalIgnoreCase)
                        || l.Texture.Contains("lava", StringComparison.OrdinalIgnoreCase));
                _notWater[graphic] = result;
            }

            return result;
        }

        private readonly Dictionary<ushort, string> _waterMask = [];

        /// <summary>
        /// The edge mask a shore water static is drawn with: the fourth layer
        /// (a = 3) of its <c>UOWaterShader</c>, e.g. 02100013_water_mask. KR
        /// binds it as stage 2 and alpha-tests against a pulsing reference,
        /// which is the shoreline animation.
        /// Null for open water, whose shader has no mask.
        /// </summary>
        public string WaterMask(ushort graphic)
        {
            if (_waterMask.TryGetValue(graphic, out string cached))
            {
                return cached;
            }

            string result = null;

            if (KrTileArt.ReadRaw(_store, graphic) is { } raw && Strings is { } strings)
            {
                foreach (KrTileArt.ShaderDefinition def in KrTileArt.Shaders(raw, strings))
                {
                    if (def.Class == "UOWaterShader")
                    {
                        result = def.Layers.Where(l => l.A == 3).Select(l => KrTerrainArt.ArtId(l.Texture)).FirstOrDefault();
                        break;
                    }
                }
            }

            _waterMask[graphic] = result;

            return result;
        }

        private int FlatBase(ushort graphic)
        {
            if (_art.IsOpaque(graphic))
            {
                return graphic;
            }

            TileArt(graphic, out _);

            List<int> hop = _refs.TryGetValue(graphic, out List<int> r) ? r : [];
            List<int> candidates = [];

            foreach (int c in hop)
            {
                if (c != graphic && _art.IsOpaque(c))
                {
                    candidates.Add(c);
                }
            }

            if (candidates.Count == 0)
            {
                foreach (int c in hop)
                {
                    if (c == graphic || c < 0 || c >= 0x4000)
                    {
                        continue;
                    }

                    TileArt((ushort)c, out _);

                    if (_refs.TryGetValue((ushort)c, out List<int> next))
                    {
                        foreach (int n in next)
                        {
                            if (n != graphic && _art.IsOpaque(n) && !candidates.Contains(n))
                            {
                                candidates.Add(n);
                            }
                        }
                    }
                }
            }

            (double R, double G, double B)? target = null;

            try
            {
                ArtInfo info = _arts.GetArt((uint)(graphic + 0x4000));
                target = KrStaticArt.MeanColour(info.Pixels);
            }
            catch (Exception)
            {
                target = null;
            }

            int best = graphic;
            double bestDistance = MaxBaseColourDistance;

            foreach (int c in candidates)
            {
                if (target is not { } t || _art.TextureColour(c) is not { } m)
                {
                    continue;
                }

                double d = Math.Sqrt(
                    ((m.R - t.R) * (m.R - t.R)) + ((m.G - t.G) * (m.G - t.G)) + ((m.B - t.B) * (m.B - t.B)));

                if (d <= bestDistance)
                {
                    bestDistance = d;
                    best = c;
                }
            }

            return best;
        }

        /// <summary>
        /// The furthest a base's average colour may be from the 2D tile's. The
        /// right red base sits about 27 from its tiles; the other family's blue
        /// is far beyond this.
        /// </summary>
        private const double MaxBaseColourDistance = 50;

        private (int X, int Y, int W, int H)? Bounds2D(ushort graphic)
        {
            if (_bounds2D.TryGetValue(graphic, out (int X, int Y, int W, int H)? cached))
            {
                return cached;
            }

            (int X, int Y, int W, int H)? bounds = null;

            try
            {
                ArtInfo info = _arts.GetArt((uint)(graphic + 0x4000));

                if (info.Width > 0 && info.Height > 0
                    && KrStaticArt.TryGetBounds(
                        info.Pixels, info.Width, info.Height,
                        out int x0, out int y0, out int w, out int h))
                {
                    bounds = (x0, y0, w, h);
                }
            }
            catch (Exception)
            {
                bounds = null;
            }

            _bounds2D[graphic] = bounds;

            return bounds;
        }

        public void Dispose()
        {
            _art.Clear();
            _store.Dispose();
            _krScenes?.Dispose();
        }
    }
}
