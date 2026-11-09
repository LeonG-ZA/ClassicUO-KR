// SPDX-License-Identifier: BSD-2-Clause
// Builds a single opaque ground tile for a KR terrain id.
//
// A terrain definition is a shader layer set, not one texture (see
// KrTerrainDefinition), and the client feeds
// every layer to the terrain shader in file order - no field says "this one is
// the ground". Painting one square per tile on ClassicUO's 2D grid needs a
// single image, so one is built here and the choice is measured rather than
// asserted.
//
// WHAT IS BUILT: the base texture, with the terrain's own A texture composited
// over it, flattened opaque.
//
// Finding the A texture. Layer slot 0 is the per-terrain texture, and in 68 of
// the 74 terrains that have one its art id ends in '1' - it is the B half of
// an A/B pair that KR blends through a noise mask. The A half is the same id
// ending in '0', and it exists in Texture.uop for all 68. So slot 0's art id
// with its last digit set to '0' is tried first, then slot 0 itself, then the
// record's name, then the base.
//
// Why composite over the base. Some A textures are themselves soft-alpha
// blend layers rather than solid ground - 02000010_Grass_A averages alpha 141
// and is fully opaque over only 40% of its texels - so used alone they leave
// holes that would show as black. The base (02000000_Black_Void_A in 183 of
// 191 records) is opaque, and putting the A texture over it is the bottom two
// layers of KR's own set.
//
// How the choice was measured. For each terrain, average the colour of the 2D
// client's land art (art.mul) over the tiles carrying that terrain, and
// compare with the average colour of the candidate. Over 78 terrains and
// 794,622 tiles of facet 0, tile-weighted distance:
//
//   base then A texture (this)        27.68   0 terrains left non-opaque
//   A texture alone                   29.43   1
//   slot0 -> name -> base             34.91   3
//   base + name + slot0               34.42   0
//   base then slot0                   37.13   0
//   whole layer set composited        50.98   -  slot 2 is grass everywhere
//                                                and greens the whole world
//   name -> slot0 -> base             63.11   -
//
// That comparison is a sanity check, not the goal: the point is to show KR's
// art, not to match the 2D client's colours. It is here because it is the only
// ground truth available, and because it catches a candidate that is plainly
// the wrong texture.

using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.World;

public sealed class KrTerrainArt
{
    private readonly KrAssetStore _store;
    private readonly Dictionary<int, KrTerrainDefinition?> _definitions = [];
    private readonly Dictionary<(int Terrain, int Size, int Vx, int Vy), uint[]?> _tiles = [];
    private readonly Dictionary<string, DdsImage?> _textures = [];
    private readonly Dictionary<string, bool> _placeholders = [];

    private KrStringDictionary? _strings;
    private bool _stringsLoaded;

    public KrTerrainArt(KrAssetStore store)
    {
        _store = store;
    }

    /// <summary>The store is an Enhanced Client install: read its terrain definitions in EC's layout.</summary>
    public bool Enhanced { get; init; }

    public KrStringDictionary? Strings
    {
        get
        {
            if (!_stringsLoaded)
            {
                _stringsLoaded = true;

                string path = Path.Combine(_store.RootPath, KrStringDictionary.PackageName);

                if (File.Exists(path))
                {
                    _strings = KrStringDictionary.Load(path);
                }
            }

            return _strings;
        }
    }

    public KrTerrainDefinition? Definition(int terrainId)
    {
        if (_definitions.TryGetValue(terrainId, out KrTerrainDefinition? cached))
        {
            return cached;
        }

        UopFile? package = _store.GetPackage(KrTerrainDefinition.PackageName);
        byte[]? raw = package?.Read(KrTerrainDefinition.EntryName(terrainId));

        KrTerrainDefinition? definition = null;

        if (raw is not null)
        {
            try
            {
                definition = KrTerrainDefinition.Parse(raw, Enhanced);
            }
            catch (InvalidDataException)
            {
                definition = null;
            }
        }

        _definitions[terrainId] = definition;

        return definition;
    }

    /// <summary>
    /// The eight-digit art id in a texture name, or null when the name is a
    /// label rather than a file name.
    /// </summary>
    public static string? ArtId(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName))
        {
            return null;
        }

        int slash = textureName.LastIndexOfAny(['\\', '/']);
        ReadOnlySpan<char> file = textureName.AsSpan(slash + 1);

        if (file.Length < 9 || file[8] != '_')
        {
            return null;
        }

        for (int i = 0; i < 8; i++)
        {
            if (!char.IsAsciiDigit(file[i]))
            {
                return null;
            }
        }

        return new string(file[..8]);
    }

    /// <summary>The A half of an A/B texture pair: the same art id ending in 0.</summary>
    public static string? ASibling(string? artId) =>
        artId is { Length: 8 } && artId[7] != '0' ? string.Concat(artId.AsSpan(0, 7), "0") : null;

    /// <summary>
    /// The art id this terrain's ground is drawn from: slot 0's A sibling,
    /// then slot 0, then the record's name, then the base.
    /// </summary>
    /// <summary>
    /// How much of a texture is fully opaque, 0..1. The ground layer of a
    /// terrain is the one that actually covers it, so this is what tells a
    /// ground texture from a sparse detail layer.
    /// </summary>
    public double Opacity(string artId)
    {
        DdsImage? image = Texture(artId);

        if (image is null)
        {
            return 0;
        }

        int opaque = 0;

        foreach (uint pixel in image.Pixels)
        {
            if ((pixel >> 24) >= 250)
            {
                opaque++;
            }
        }

        return image.Pixels.Length == 0 ? 0 : (double)opaque / image.Pixels.Length;
    }

    /// <summary>
    /// The most opaque texture among the terrain's layers and its name,
    /// ignoring the base. Null when the terrain references no texture.
    /// </summary>
    public string? MostOpaqueArtId(int terrainId)
    {
        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        if (definition is null || strings is null)
        {
            return null;
        }

        string? best = null;
        double bestOpacity = -1;

        foreach (KrTerrainLayer layer in definition.Layers)
        {
            string? id = ArtId(strings[layer.TextureStringIndex]);

            if (id is null || Texture(id) is null)
            {
                continue;
            }

            double opacity = Opacity(id);

            if (opacity > bestOpacity)
            {
                bestOpacity = opacity;
                best = id;
            }
        }

        string? named = ArtId(strings[definition.NameStringIndex]);

        if (named is not null && Texture(named) is not null && Opacity(named) > bestOpacity)
        {
            best = named;
        }

        return best;
    }

    /// <summary>
    /// The art id named by the terrain record itself, which for the KR-authored
    /// terrains is its canonical texture: terrain 4 names 02000040_Forest_A,
    /// terrain 146 names 02001460_Rock_Dark_A.
    /// </summary>
    /// <summary>
    /// Whether a texture is a flat, featureless placeholder rather than real
    /// ground art.
    /// </summary>
    /// <remarks>
    /// KR ships solid-black stand-ins for terrains it never finished, and
    /// painting one leaves a black hole in the middle of the world - in game
    /// they showed up as black diamonds along a shoreline. Of the 82 ground
    /// textures the map actually paints, exactly four are uniformly black
    /// (02000440, 02000450, 02001550 and 02001700), each with a luminance mean
    /// and standard deviation of 0.00. The next lowest deviation of any other
    /// texture is 7.67, so the threshold below has an enormous margin and is
    /// not a tuned number.
    /// </remarks>
    public bool IsPlaceholder(string artId)
    {
        if (_placeholders.TryGetValue(artId, out bool cached))
        {
            return cached;
        }

        DdsImage? image = Texture(artId);

        bool flat = false;

        if (image is not null && image.Pixels.Length > 0)
        {
            double sum = 0;
            double sumSquares = 0;

            foreach (uint pixel in image.Pixels)
            {
                double luminance =
                    (0.299 * (pixel & 0xFF))
                    + (0.587 * ((pixel >> 8) & 0xFF))
                    + (0.114 * ((pixel >> 16) & 0xFF));

                sum += luminance;
                sumSquares += luminance * luminance;
            }

            double mean = sum / image.Pixels.Length;
            double variance = Math.Max(0, (sumSquares / image.Pixels.Length) - (mean * mean));

            // Flat AND dark. The stand-ins are uniform black (mean 0.00); a flat
            // colour is not one - terrain 52's 02000520_Tan_No_Name is a
            // uniform tan (142, 126, 104) and is the beach's real base.
            flat = Math.Sqrt(variance) < PlaceholderDeviation && mean < PlaceholderMaxLuminance;
        }

        _placeholders[artId] = flat;

        return flat;
    }

    /// <summary>
    /// Luminance deviation below which a texture counts as featureless. The
    /// placeholders measure 0.00 and the next real texture 7.67.
    /// </summary>
    public const double PlaceholderDeviation = 3.0;

    /// <summary>Mean luminance at or above which a flat texture is a real colour, not a stand-in.</summary>
    public const double PlaceholderMaxLuminance = 16.0;

    public string? NamedArtId(int terrainId)
    {
        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        if (definition is null || strings is null)
        {
            return null;
        }

        string? id = ArtId(strings[definition.NameStringIndex]);

        return id is not null && Texture(id) is not null ? id : null;
    }

    public string? GroundArtId(int terrainId) => GroundArtChoice(terrainId).Id;

    /// <summary>
    /// The ground art id and which step of the chain supplied it. The step
    /// matters when auditing: the A-sibling rule rewrites the last digit of
    /// the id, so a wrong guess there hands the terrain a different
    /// material's texture rather than merely a different variant of its own.
    /// </summary>
    public (string? Id, string Step) GroundArtChoice(int terrainId)
    {
        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        if (definition is null || strings is null)
        {
            return (null, "none");
        }

        string? slot0 = null;

        foreach (KrTerrainLayer layer in definition.Layers)
        {
            if (layer.Slot == 0)
            {
                slot0 = ArtId(strings[layer.TextureStringIndex]);

                if (slot0 is not null)
                {
                    break;
                }
            }
        }

        // The record's own name, when it is a KR terrain texture (0200xxxx),
        // comes first. For most terrains it is the same texture the A sibling
        // gives (Forest_A, Cobblestone_A, Rock_Dark_A); where it is not, slot 0
        // points somewhere else - terrain 52 names 02000520_Tan_No_Name, a
        // tan sand, but its slot 0 is 02000532_Stars_C, whose A sibling is a
        // near-black texture that put black patches along a beach.
        string? named = ArtId(strings[definition.NameStringIndex]);

        if (named is not null && named.StartsWith("0200", StringComparison.Ordinal)
            && Texture(named) is not null && !IsPlaceholder(named))
        {
            return (named, "name");
        }

        foreach ((string? candidate, string step) in ((string?, string)[])
        [
            (ASibling(slot0), "a-sibling"),
            (slot0, "slot0"),
            (ArtId(strings[definition.NameStringIndex]), "name")

            // The base is NOT a candidate. It is not ground - for most
            // terrains it is 02000000_Black_Void_A, a dark placeholder - and
            // over all 1,557 mapped land tile ids it never once won the chain
            // (slot0 59, a-sibling 1429, name 69), so dropping it costs
            // nothing. It was only reachable at all once the uniformly black
            // textures above it started being rejected, and it put the black
            // straight back.
        ])
        {
            if (candidate is not null && Texture(candidate) is not null && !IsPlaceholder(candidate))
            {
                return (candidate, step);
            }
        }

        return (null, "none");
    }

    private string? GroundArtIdLegacy(int terrainId)
    {
        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        if (definition is null || strings is null)
        {
            return null;
        }

        string? slot0 = null;

        foreach (KrTerrainLayer layer in definition.Layers)
        {
            if (layer.Slot == 0)
            {
                slot0 = ArtId(strings[layer.TextureStringIndex]);

                if (slot0 is not null)
                {
                    break;
                }
            }
        }

        foreach (string? candidate in (string?[])
        [
            ASibling(slot0),
            slot0,
            ArtId(strings[definition.NameStringIndex]),
            ArtId(strings[definition.BaseTextureStringIndex])
        ])
        {
            if (candidate is not null && Texture(candidate) is not null)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The art id of this terrain's base texture, the bottom layer.</summary>
    public string? BaseArtId(int terrainId)
    {
        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        if (definition is null || strings is null)
        {
            return null;
        }

        string? id = ArtId(strings[definition.BaseTextureStringIndex]);

        return id is not null && Texture(id) is not null ? id : null;
    }

    /// <summary>A decoded world-art texture by art id, cached.</summary>
    public DdsImage? Texture(string artId)
    {
        string entry = string.Concat("build/worldart/", artId, ".dds");

        if (_textures.TryGetValue(entry, out DdsImage? cached))
        {
            return cached;
        }

        UopFile? package = _store.GetPackage(KrTexturePackages.WorldArtPackage);
        byte[]? raw = package?.Read(entry);

        DdsImage? image = null;

        if (raw is not null && DdsImage.IsDds(raw))
        {
            try
            {
                image = DdsImage.Decode(raw);
            }
            catch (InvalidDataException)
            {
                image = null;
            }
        }

        _textures[entry] = image;

        return image;
    }

    /// <summary>
    /// How many tiles the texture is spread across before it repeats.
    /// </summary>
    /// <remarks>
    /// A single tile showing the whole texture is what makes the ground look
    /// quilted: every tile carries the same blotches in the same place. Spread
    /// over a period, tile (x, y) takes the crop at (x % period, y % period)
    /// and the pattern runs across the ground instead.
    ///
    /// The period is bounded by resolution, not by KR's own per-layer scale.
    /// KR renders the texture at screen resolution with filtering; here each
    /// tile is a 44-pixel diamond, so a crop smaller than about 32 source
    /// pixels is being magnified and goes soft. 32 pixels per tile gives
    /// 2 tiles for a 64-pixel texture up to 8 for 256 and above.
    /// </remarks>
    public int PeriodFor(int terrainId)
    {
        if (Composite(terrainId) is { } c)
        {
            return c.Period;
        }

        string? groundId = GroundArtId(terrainId);
        DdsImage? ground = groundId is null ? null : Texture(groundId);

        if (ground is null)
        {
            return 1;
        }

        // 64 source px per tile, the span KR gives floor textures (section
        // 19) and what the graveyard road at Trammel 1372,1488 shows: at 32
        // px per tile our cobbles came out twice KR's size (section 26).
        int raw = Math.Clamp(Math.Min(ground.Width, ground.Height) / PixelsPerTile, 1, MaxPeriod);

        // Snapped to a power of two so that every layer's own period divides
        // it; a layer whose period did not divide the common one would not
        // tile.
        return LargestPowerOfTwoAtMost(raw);
    }

    private static int LargestPowerOfTwoAtMost(int value)
    {
        int p = 1;

        while (p * 2 <= value)
        {
            p *= 2;
        }

        return p;
    }

    /// <summary>The tile period a layer repeats on, from its own scale.</summary>
    /// <remarks>
    /// *Hypothesis.* A layer's Scale is 1 to 48 in the shipped data and reads
    /// as how often the texture repeats across a sector, which is 64 tiles
    /// square, so the period in tiles is 64 / Scale: grass slot 0 is x10 (6.4
    /// tiles) and slot 2 is x24 (2.7); forest slot 0 is x5 (12.8) and slot 2
    /// is x16 (4.0). Nothing here verifies the 64. What the renderer actually
    /// needs is the RATIO between layers of one terrain, and any constant
    /// gives that equally, so the constant only sets the overall coarseness.
    /// The result is snapped to a power of two and capped at the terrain's own
    /// period so that every layer tiles within it.
    /// </remarks>
    public static int LayerPeriod(float scale, int max)
    {
        if (!(scale > 0))
        {
            return max;
        }

        double tiles = 64.0 / scale;

        int best = 1;
        double bestError = double.MaxValue;

        for (int p = 1; p <= max; p *= 2)
        {
            double error = Math.Abs(Math.Log2(p) - Math.Log2(tiles));

            if (error < bestError)
            {
                bestError = error;
                best = p;
            }
        }

        return best;
    }

    /// <summary>
    /// Composite a terrain's whole layer set rather than its single chosen
    /// texture. **Off by default, because measured over the whole map it is
    /// worse**, and it is kept only as the groundwork for doing this properly
    /// once KR's noise mask is decoded.
    /// </summary>
    /// <remarks>
    /// Blending the set unconditionally is wrong, and the data says so
    /// plainly. Slot 2 carries 02000010_Grass_A on terrain after terrain,
    /// including ones where grass must never appear: terrain 170 is
    /// 02001700_Black, a cave floor, and its only layer with a texture is that
    /// Grass_A; terrain 5 is water and its slot 1 is 01000017_cube3. KR must
    /// therefore gate each layer through the noise mask, which is not decoded,
    /// so there is nothing here to say where a layer applies.
    ///
    /// What it costs and buys, as the tile-weighted mean distance to the 2D
    /// art for the same land tile id (`kr-asset-tool terrain-rules`), per
    /// pixel and by mean tone:
    ///
    ///   single layer   33.69 / 27.72
    ///   layered set    55.02 / 51.25
    ///
    /// Per terrain it is not uniform. Forest improves a lot - land 196 goes
    /// from 70.8 to 51.6, and the patch render stops being raw leaf litter and
    /// reads as forest floor. The loss is in the two biggest terrains on the
    /// map: terrain 170 over 11.5 million tiles, and terrain 5 over about 31
    /// million, both of which get a layer painted on them that does not belong.
    /// </remarks>
    public bool BlendLayers { get; set; }

    /// <summary>
    /// Replace a chain pick that is not one of the terrain's layers with the
    /// layer set itself. **Off**: tried for terrain 4 (it turned leaf litter
    /// into grass with leaves) but it is too crude in general - terrain 9's
    /// layers are Cobblestone_B, 01000004_noise_alpha_sharp and
    /// 00001261_blue_tiles, and taking the most opaque gave a road of blue
    /// slabs. Slot 1 being a noise texture suggests KR mixes slots 0 and 2
    /// through slot 1; that is not decoded.
    /// </summary>
    public bool UseLayerSet { get; set; }

    /// <summary>
    /// The textures a terrain's ground is built from, bottom first, each with
    /// the tile period it repeats on.
    /// </summary>
    /// <remarks>
    /// KR blends a terrain's whole layer set through a noise mask. Painting
    /// one layer flat is what left forest as raw leaf litter beside clean
    /// grass: terrain 4's own texture is 02000040_Forest_A, and KR covers it
    /// with Forest_B and, at slot 2, the same 02000010_Grass_A that nearly
    /// every terrain carries. The mask is not decoded, so this composites in
    /// slot order using each layer's own alpha, which is an approximation and
    /// not a reading of the format.
    ///
    /// The bottom is the single texture the chain already chose, forced
    /// opaque, because no layer is a solid ground texture on its own: grass
    /// slot 0 is 0.8 % opaque and slot 2 is 39.6 %.
    /// </remarks>
    public (string Id, int Period)[] GroundStack(int terrainId)
    {
        string? groundId = GroundArtId(terrainId);

        if (groundId is null)
        {
            return [];
        }

        int period = PeriodFor(terrainId);

        List<(string Id, int Period)> stack = [(groundId, period)];

        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        // When the chain's pick is not one of the terrain's own layers, KR
        // never draws it: terrain 4's chain gives 02000040_Forest_A (solid
        // leaf litter, the A sibling of slot 0) but its layers are
        // 02000041_Forest_B, 23 % opaque, and 02000010_Grass_A, 40 % opaque,
        // and KR draws that ground as grass with scattered leaves. Use the
        // layer set instead: the most opaque layer as the solid bottom, the
        // rest over it in slot order, all on the terrain's period so that a
        // layer shared with a neighbouring terrain - Grass_A, here - runs on
        // across the border. Terrains whose pick is one of their layers
        // (grass, water) are left exactly as they were; section 13 showed
        // what blending every terrain's whole set unconditionally does.
        if (UseLayerSet && !BlendLayers && definition is not null && strings is not null)
        {
            List<string> layers = [];

            foreach (KrTerrainLayer layer in definition.Layers.OrderBy(l => l.Slot))
            {
                string? id = ArtId(strings[layer.TextureStringIndex]);

                if (id is not null && !layers.Contains(id) && Texture(id) is not null && !IsPlaceholder(id))
                {
                    layers.Add(id);
                }
            }

            if (layers.Count > 0 && !layers.Contains(groundId))
            {
                string bottom = layers.OrderByDescending(Opacity).First();

                stack = [(bottom, period)];

                foreach (string id in layers)
                {
                    if (id != bottom)
                    {
                        stack.Add((id, period));
                    }
                }

                return stack.ToArray();
            }
        }

        if (BlendLayers && definition is not null && strings is not null)
        {
            foreach (KrTerrainLayer layer in definition.Layers.OrderBy(l => l.Slot))
            {
                string? id = ArtId(strings[layer.TextureStringIndex]);

                if (id is null || id == groundId || Texture(id) is null)
                {
                    continue;
                }

                stack.Add((id, LayerPeriod(layer.Scale, period)));
            }
        }

        return stack.ToArray();
    }

    /// <summary>Composites one source pixel over one destination pixel.</summary>
    private static uint OverPixel(uint dst, uint src)
    {
        uint sa = (src >> 24) & 0xFF;

        if (sa == 0)
        {
            return dst;
        }

        if (sa == 0xFF)
        {
            return src;
        }

        uint inv = 255 - sa;

        uint r = (((src & 0xFF) * sa) + ((dst & 0xFF) * inv)) / 255;
        uint g = ((((src >> 8) & 0xFF) * sa) + (((dst >> 8) & 0xFF) * inv)) / 255;
        uint b = ((((src >> 16) & 0xFF) * sa) + (((dst >> 16) & 0xFF) * inv)) / 255;

        return 0xFF000000u | (b << 16) | (g << 8) | r;
    }

    /// <summary>The largest period <see cref="PeriodFor"/> will return.</summary>
    public const int MaxPeriod = 8;

    /// <summary>Source pixels a ground texture spends on one tile.</summary>
    public const int PixelsPerTile = 64;

    /// <summary>
    /// One tile of the terrain's ground: the crop at (<paramref name="vx"/>,
    /// <paramref name="vy"/>) of the texture spread over
    /// <see cref="PeriodFor"/> tiles, as an opaque square. The base texture is
    /// composited under it. Null when the terrain references no texture.
    /// </summary>
    public uint[]? GroundTile(int terrainId, int size, int vx = 0, int vy = 0) =>
        CompositeTile(terrainId, size, vx, vy, diamond: false)
        ?? GroundTileCore(terrainId, size, vx, vy);

    private uint[]? GroundTileCore(int terrainId, int size, int vx, int vy)
    {
        int period = PeriodFor(terrainId);

        vx = ((vx % period) + period) % period;
        vy = ((vy % period) + period) % period;

        (int, int, int, int) key = (terrainId, size, vx, vy);

        if (_tiles.TryGetValue(key, out uint[]? cached))
        {
            return cached;
        }

        (string Id, int Period)[] stack = GroundStack(terrainId);
        uint[]? tile = null;

        if (stack.Length > 0)
        {
            DdsImage ground = Texture(stack[0].Id)!;
            int bottom = stack[0].Period;

            tile = ResampleRegion(
                ground.Pixels, ground.Width, ground.Height,
                vx % bottom, vy % bottom, bottom, size);

            // NOTHING is composited underneath. The definition's "base" is not
            // a backdrop: for grass it is 02000000_Black_Void_A, an opaque
            // near-black placeholder (28, 27, 16). KR covers it with the layer
            // set blended through a noise mask, but flattening one layer onto
            // it shows the void through every gap - grass is only 39.6% opaque,
            // so in-game it came out blotched with black. Scored against the 2D
            // art over every mapped land tile id, dropping the base improves the
            // tile-weighted mean distance from 35.94 to 33.69. Picking the most
            // opaque layer instead was worse again, at 40.22.
            //
            // The ground is the bottom of the world; nothing shows through it.
            // Forcing alpha opaque keeps the layer's own colour in the gaps,
            // which is what makes grass read as grass.
            for (int i = 0; i < tile.Length; i++)
            {
                tile[i] |= 0xFF000000;
            }

            // The rest of the layer set, in slot order, each on its own period.
            for (int layer = 1; layer < stack.Length; layer++)
            {
                DdsImage image = Texture(stack[layer].Id)!;
                int p = stack[layer].Period;

                uint[] over = ResampleRegion(
                    image.Pixels, image.Width, image.Height, vx % p, vy % p, p, size);

                Over(tile, over);
            }
        }

        _tiles[key] = tile;

        return tile;
    }

    /// <summary>
    /// One land tile as a 44x44 diamond, sampled in WORLD space.
    /// </summary>
    /// <remarks>
    /// Taking an axis-aligned crop per tile and masking it to a diamond makes
    /// the texture staircase: inside each tile it runs with the screen, but
    /// the tiles themselves advance diagonally, so the pattern breaks at every
    /// edge. The ground is a surface in world space, and KR maps the texture
    /// onto it there, so this does the same - a tile's four corners take the
    /// texture at (x, y), (x+1, y), (x+1, y+1) and (x, y+1) and the diamond
    /// interpolates between them. On screen the texture ends up rotated 45
    /// degrees, which is what it looks like in KR.
    ///
    /// Screen position of a point (u, v) inside tile (x, y), with the tile
    /// 44 wide and 44 tall:
    ///     px = (u - v) * 22 + 22
    ///     py = (u + v) * 22
    /// which inverts to
    ///     u = (px - 22 + py) / 44
    ///     v = (py - px + 22) / 44
    /// </remarks>
    public uint[]? GroundDiamond(int terrainId, int size, int tileX, int tileY) =>
        CompositeTile(terrainId, size, tileX, tileY, diamond: true)
        ?? GroundDiamondCore(terrainId, size, tileX, tileY);

    private uint[]? GroundDiamondCore(int terrainId, int size, int tileX, int tileY)
    {
        int period = PeriodFor(terrainId);

        int vx = ((tileX % period) + period) % period;
        int vy = ((tileY % period) + period) % period;

        (int, int, int, int) key = (terrainId, -size, vx, vy);

        if (_tiles.TryGetValue(key, out uint[]? cached))
        {
            return cached;
        }

        (string Id, int Period)[] stack = GroundStack(terrainId);
        uint[]? tile = null;

        if (stack.Length > 0)
        {
            DdsImage[] images = new DdsImage[stack.Length];

            for (int i = 0; i < stack.Length; i++)
            {
                images[i] = Texture(stack[i].Id)!;
            }

            // No backdrop; see GroundTile for why the definition's base is not
            // one.
            tile = new uint[size * size];

            float half = size / 2f;

            for (int py = 0; py < size; py++)
            {
                int count = py < size / 2 ? (py + 1) * 2 : (size - py) * 2;
                int start = (size - count) / 2;

                for (int px = start; px < start + count; px++)
                {
                    float u = (px - half + py) / size;
                    float v = (py - px + half) / size;

                    uint pixel = Sample(
                        images[0], (vx + u) / stack[0].Period, (vy + v) / stack[0].Period)
                        | 0xFF000000;

                    for (int layer = 1; layer < stack.Length; layer++)
                    {
                        int p = stack[layer].Period;

                        pixel = OverPixel(pixel, Sample(images[layer], (vx + u) / p, (vy + v) / p));
                    }

                    tile[(py * size) + px] = pixel;
                }
            }
        }

        _tiles[key] = tile;

        return tile;
    }

    /// <summary>
    /// The terrains of a 5x5 block in the order they are laid over the centre
    /// tile's ground - shared by the CPU composite (BlendedDiamond) and the
    /// GPU path, so the two cannot drift apart.
    /// </summary>
    public List<int> BlendOrder(ReadOnlySpan<int> block5, bool ascending)
    {
        List<int> terrains = [];

        foreach (int t in block5)
        {
            if (t >= 0 && !terrains.Contains(t))
            {
                terrains.Add(t);
            }
        }

        // Blending terrains first, in id order; hard ones (exponent 0) last so
        // that a floor fully covers whatever bled onto its tiles.
        terrains.Sort((a, b) =>
        {
            // Placeholder terrains go underneath everything.
            bool pa = IsPlaceholder(a), pb = IsPlaceholder(b);

            if (pa != pb)
            {
                return pa ? -1 : 1;
            }

            bool ha = Exponent(a) == 0f, hb = Exponent(b) == 0f;

            if (ha != hb)
            {
                return ha ? 1 : -1;
            }

            // Among blending terrains the higher exponent goes on top.
            // *Hypothesis* fitting two KR shots: grass (3) over forest (1) in
            // the garden, rocky dirt (2) over dirt (1) on a road at Trammel
            // 1325,3739. Id order alone gets one of them wrong.
            int byExponent = Exponent(a).CompareTo(Exponent(b));

            if (byExponent != 0)
            {
                return byExponent;
            }

            return ascending ? a.CompareTo(b) : b.CompareTo(a);
        });

        return terrains;
    }

    /// <summary>
    /// A land diamond blended with its neighbours the way KR blends terrain
    /// (see <see cref="KrTerrainBlend"/>).
    /// </summary>
    /// <param name="block5">Terrain ids of the 5x5 tiles around this one,
    /// row-major over (y, x), this tile at index 12.</param>
    /// <param name="ascending">Composite terrains in ascending id order
    /// (later ones on top); false for descending. Which KR uses is not known.</param>
    public uint[]? BlendedDiamond(ReadOnlySpan<int> block5, int size, int tileX, int tileY, bool ascending = true)
    {
        int centre = block5[12];

        uint[]? baseTile = centre < 0 ? null : GroundDiamond(centre, size, tileX, tileY);

        if (baseTile is null)
        {
            return null;
        }

        List<int> terrains = BlendOrder(block5, ascending);

        if (terrains.Count == 1)
        {
            return baseTile;
        }

        uint[] tile = (uint[])baseTile.Clone();
        float half = size / 2f;

        foreach (int t in terrains)
        {
            KrTerrainBlend.WeightGrid grid = new(block5, t, Exponent(t));

            if (grid.IsEmpty)
            {
                continue;
            }

            uint[]? ground = GroundDiamond(t, size, tileX, tileY);

            if (ground is null)
            {
                continue;
            }

            for (int py = 0; py < size; py++)
            {
                int count = py < size / 2 ? (py + 1) * 2 : (size - py) * 2;
                int start = (size - count) / 2;

                for (int px = start; px < start + count; px++)
                {
                    float u = (px - half + py) / size;
                    float v = (py - px + half) / size;

                    float w = grid.At(u, v);

                    if (w <= 0f)
                    {
                        continue;
                    }

                    int i = (py * size) + px;
                    tile[i] = Lerp(tile[i], ground[i], w);
                }
            }
        }

        return tile;
    }

    /// <summary>
    /// The square version of <see cref="BlendedDiamond"/>, for ClassicUO's
    /// stretched land path: its shader maps a square texture onto the tile
    /// with x across and y down, the same axes <see cref="GroundTile"/> crops
    /// in, so the same weights apply at (x, y) = (u, v).
    /// </summary>
    public uint[]? BlendedTile(ReadOnlySpan<int> block5, int size, int tileX, int tileY, bool ascending = true)
    {
        int centre = block5[12];

        uint[]? baseTile = centre < 0 ? null : GroundTile(centre, size, tileX, tileY);

        if (baseTile is null)
        {
            return null;
        }

        List<int> terrains = [];

        foreach (int t in block5)
        {
            if (t >= 0 && !terrains.Contains(t))
            {
                terrains.Add(t);
            }
        }

        if (terrains.Count == 1)
        {
            return baseTile;
        }

        // Blending terrains first, in id order; hard ones (exponent 0) last so
        // that a floor fully covers whatever bled onto its tiles.
        terrains.Sort((a, b) =>
        {
            // Placeholder terrains go underneath everything.
            bool pa = IsPlaceholder(a), pb = IsPlaceholder(b);

            if (pa != pb)
            {
                return pa ? -1 : 1;
            }

            bool ha = Exponent(a) == 0f, hb = Exponent(b) == 0f;

            if (ha != hb)
            {
                return ha ? 1 : -1;
            }

            // Among blending terrains the higher exponent goes on top.
            // *Hypothesis* fitting two KR shots: grass (3) over forest (1) in
            // the garden, rocky dirt (2) over dirt (1) on a road at Trammel
            // 1325,3739. Id order alone gets one of them wrong.
            int byExponent = Exponent(a).CompareTo(Exponent(b));

            if (byExponent != 0)
            {
                return byExponent;
            }

            return ascending ? a.CompareTo(b) : b.CompareTo(a);
        });

        uint[] tile = (uint[])baseTile.Clone();

        foreach (int t in terrains)
        {
            KrTerrainBlend.WeightGrid grid = new(block5, t, Exponent(t));

            if (grid.IsEmpty)
            {
                continue;
            }

            uint[]? ground = GroundTile(t, size, tileX, tileY);

            if (ground is null)
            {
                continue;
            }

            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float w = grid.At((x + 0.5f) / size, (y + 0.5f) / size);

                    if (w > 0f)
                    {
                        int i = (y * size) + x;
                        tile[i] = Lerp(tile[i], ground[i], w);
                    }
                }
            }
        }

        return tile;
    }

    /// <summary>
    /// A terrain drawn the way KR's default terrain layer draws it, read from
    /// the KR client: layers
    /// 0, 1 and 2 of the definition, with
    ///     ground = layer0 x layer2.alpha + layer1 x (1 - layer2.alpha)
    /// and each layer repeating every Scale tiles in world space.
    /// </summary>
    public sealed record CompositeLayers(
        DdsImage Top, int TopRepeat, DdsImage Ground, int GroundRepeat, DdsImage Mask, int MaskRepeat)
    {
        /// <summary>
        /// The mask is one picture for one tile (a lava rim's 0000NNNN_mask_lava),
        /// not a pattern laid across the world: KR clamps it to the tile, and
        /// its rows run with +y. Read as terrain (-y, wrapped), the Trammel
        /// 4598,3617 pit's rim came out in steps that did not join.
        /// </summary>
        public bool PieceMask { get; init; }

        /// <summary>Tiles after which the whole composite repeats.</summary>
        public int Period => Math.Max(TopRepeat, Math.Max(GroundRepeat, MaskRepeat));
    }

    /// <summary>Largest repeat a composite layer is given; also the cache period.</summary>
    public const int MaxCompositeRepeat = 32;

    /// <summary>A layer's repeat in tiles: its Scale, snapped to a power of two.</summary>
    /// <remarks>
    /// *Our approximation, not KR's.* KR repeats each layer every Scale tiles
    /// exactly (grass: 10, 5, 24), but the client caches one sprite per tile
    /// position within a period, and exact scales would need periods of 120
    /// tiles. Snapped to powers of two up to 32, every layer divides the
    /// largest, so the ground tiles seamlessly on a period of at most 32.
    /// </remarks>
    public static int CompositeRepeat(float scale)
    {
        if (!(scale > 0))
        {
            return 1;
        }

        int best = 1;

        for (int p = 1; p <= MaxCompositeRepeat; p *= 2)
        {
            if (Math.Abs(Math.Log2(p) - Math.Log2(scale)) < Math.Abs(Math.Log2(best) - Math.Log2(scale)))
            {
                best = p;
            }
        }

        return best;
    }

    private readonly Dictionary<int, CompositeLayers?> _composites = [];

    /// <summary>
    /// The terrain's three composite layers, or null when it is not drawn that
    /// way (another shader class, fewer than three layers, a texture missing).
    /// </summary>
    public CompositeLayers? Composite(int terrainId)
    {
        if (_composites.TryGetValue(terrainId, out CompositeLayers? cached))
        {
            return cached;
        }

        CompositeLayers? result = null;
        KrTerrainDefinition? definition = Definition(terrainId);
        KrStringDictionary? strings = Strings;

        // The record's "base" string is the shader class (section 29).
        string? cls = definition is null || strings is null ? null : strings[definition.BaseTextureStringIndex];

        if (definition is not null && strings is not null && definition.Layers.Length >= 3
            && cls is "UODefaultTerrainLayer" or "UOBumpMapTerrainLayer")
        {
            result = CompositeFrom(
                [.. definition.Layers.Take(3).Select(l => (ArtId(strings[l.TextureStringIndex]), l.Scale))]);
        }

        _composites[terrainId] = result;

        return result;
    }

    /// <summary>Builds composite layers from three (art id, scale) pairs: top, ground, mask.</summary>
    public CompositeLayers? CompositeFrom(IReadOnlyList<(string? ArtId, float Scale)> layers)
    {
        if (layers.Count < 3)
        {
            return null;
        }

        DdsImage?[] images = [.. layers.Take(3).Select(l => l.ArtId is null ? null : Texture(l.ArtId))];

        if (images[0] is null || images[1] is null || images[2] is null)
        {
            return null;
        }

        return new CompositeLayers(
            images[0]!, CompositeRepeat(layers[0].Scale),
            images[1]!, CompositeRepeat(layers[1].Scale),
            images[2]!, CompositeRepeat(layers[2].Scale));
    }

    private readonly Dictionary<(int Terrain, int Size, int Vx, int Vy), uint[]?> _compositeTiles = [];

    private uint[]? CompositeTile(int terrainId, int size, int tileX, int tileY, bool diamond)
    {
        if (Composite(terrainId) is not { } c)
        {
            return null;
        }

        int p = c.Period;
        int vx = ((tileX % p) + p) % p;
        int vy = ((tileY % p) + p) % p;
        (int, int, int, int) key = (terrainId, diamond ? -size : size, vx, vy);

        if (!_compositeTiles.TryGetValue(key, out uint[]? tile))
        {
            tile = ComposeTile(c, size, vx, vy, diamond);
            _compositeTiles[key] = tile;
        }

        return tile;
    }

    /// <summary>
    /// One tile of a composite, as a <paramref name="size"/> square or (with
    /// <paramref name="diamond"/>) a land diamond, sampled at world tile
    /// (<paramref name="tileX"/>, <paramref name="tileY"/>). Texture
    /// coordinates are KR's: (x, -y) in tiles over each layer's repeat.
    /// </summary>
    public static uint[] ComposeTile(CompositeLayers c, int size, int tileX, int tileY, bool diamond)
    {
        uint[] tile = new uint[size * size];
        float half = size / 2f;

        for (int py = 0; py < size; py++)
        {
            int start = 0, count = size;

            if (diamond)
            {
                count = py < size / 2 ? (py + 1) * 2 : (size - py) * 2;
                start = (size - count) / 2;
            }

            for (int px = start; px < start + count; px++)
            {
                float u = diamond ? (px - half + py) / size : (px + 0.5f) / size;
                float v = diamond ? (py - px + half) / size : (py + 0.5f) / size;
                float wx = tileX + u, wy = tileY + v;

                uint top = Sample(c.Top, wx / c.TopRepeat, -wy / c.TopRepeat);
                uint ground = Sample(c.Ground, wx / c.GroundRepeat, -wy / c.GroundRepeat);
                float a = ((c.PieceMask
                    ? Sample(c.Mask, Math.Clamp(u, 0f, 0.999f), Math.Clamp(v, 0f, 0.999f))
                    : Sample(c.Mask, wx / c.MaskRepeat, -wy / c.MaskRepeat)) >> 24 & 0xFF) / 255f;

                tile[(py * size) + px] = Lerp(ground, top, a) | 0xFF000000;
            }
        }

        return tile;
    }

    /// <summary>
    /// A flat-colour stand-in such as terrain 52 "tan no-name" (one 64x64
    /// colour in both slots). The facets put it under water below shore
    /// cliffs, and KR never shows it over a neighbour: at Trammel 2680,2188
    /// KR's cliff tiles (59, 60) show their own dark dirt where id order put
    /// the tan over them. *Hypothesis:* KR keeps such terrains underneath.
    /// </summary>
    public bool IsPlaceholder(int terrainId) => (Kind(terrainId) & KindPlaceholder) != 0;

    private const int KindPlaceholder = 1, KindWater = 2, KindStars = 4, KindLava = 8, KindSwamp = 16;

    private readonly Dictionary<int, int> _kinds = [];

    /// <summary>
    /// A terrain's class, worked out once from its definition's strings: the
    /// checks below run for every land tile of every mesh build (and inside
    /// the blend-order sort), and redoing the string tests each time was a
    /// large part of the GPU terrain path's cost.
    /// </summary>
    private int Kind(int terrainId)
    {
        if (_kinds.TryGetValue(terrainId, out int kind))
        {
            return kind;
        }

        kind = 0;

        if (Definition(terrainId) is { } d && Strings is { } strings)
        {
            string name = strings[d.NameStringIndex] ?? "";
            bool waterClass = strings[d.BaseTextureStringIndex] == "UOWaterTerrainLayer";

            if (name.Contains("no-name", StringComparison.OrdinalIgnoreCase))
            {
                kind |= KindPlaceholder;
            }

            if (waterClass && name.Contains("water", StringComparison.OrdinalIgnoreCase))
            {
                kind |= KindWater;

                if (name.Contains("swamp", StringComparison.OrdinalIgnoreCase))
                {
                    kind |= KindSwamp;
                }
            }

            if (waterClass && name.Equals("stars", StringComparison.OrdinalIgnoreCase))
            {
                kind |= KindStars;
            }

            if (waterClass && name.Equals("lava", StringComparison.OrdinalIgnoreCase))
            {
                kind |= KindLava;
            }
        }

        _kinds[terrainId] = kind;

        return kind;
    }

    /// <summary>
    /// A terrain KR draws with its water shader: class UOWaterTerrainLayer
    /// and named water - sea (5) and swamp water (70, the sewer channels on
    /// land 15849-15852, which tiledata does not flag wet). Lava, acid and
    /// stars share the class but are not water.
    /// </summary>
    public bool IsWaterSurface(int terrainId) => (Kind(terrainId) & KindWater) != 0;

    /// <summary>
    /// The star field (terrain 53, the void around the shrine rooms): water
    /// class, drawn with the sea's bump-and-reflect pass on star textures.
    /// </summary>
    public bool IsStars(int terrainId) => (Kind(terrainId) & KindStars) != 0;

    /// <summary>
    /// Lava (terrain 10): water class, drawn with the bump pass of
    /// Composited as ground, its first slot -
    /// the blue normal map 01000012_lava_normal - came out as purple land.
    /// </summary>
    public bool IsLava(int terrainId) => (Kind(terrainId) & KindLava) != 0;

    /// <summary>Swamp water (terrain 70): bump and reflection of its own, and a flowing bump.</summary>
    public bool IsSwampWater(int terrainId) => (Kind(terrainId) & KindSwamp) != 0;

    /// <summary>A terrain's blend exponent from its definition; 0 when unknown.</summary>
    public float Exponent(int terrainId) => Definition(terrainId)?.BlendExponent ?? 0f;

    public static uint Lerp(uint a, uint b, float t)
    {
        uint Channel(int shift)
        {
            float x = ((a >> shift) & 0xFF) * (1 - t) + ((b >> shift) & 0xFF) * t;
            return (uint)Math.Clamp((int)(x + 0.5f), 0, 255) << shift;
        }

        return Channel(0) | Channel(8) | Channel(16) | (Math.Max(a >> 24, b >> 24) << 24);
    }

    /// <summary>Bilinear sample of a texture at wrapped normalised coordinates.</summary>
    public static uint Sample(DdsImage image, float u, float v)
    {
        float fx = (u - MathF.Floor(u)) * image.Width;
        float fy = (v - MathF.Floor(v)) * image.Height;

        int x0 = (int)fx;
        int y0 = (int)fy;
        int x1 = (x0 + 1) % image.Width;
        int y1 = (y0 + 1) % image.Height;

        x0 %= image.Width;
        y0 %= image.Height;

        float tx = fx - MathF.Floor(fx);
        float ty = fy - MathF.Floor(fy);

        uint p00 = image.Pixels[(y0 * image.Width) + x0];
        uint p10 = image.Pixels[(y0 * image.Width) + x1];
        uint p01 = image.Pixels[(y1 * image.Width) + x0];
        uint p11 = image.Pixels[(y1 * image.Width) + x1];

        uint result = 0;

        for (int shift = 0; shift < 32; shift += 8)
        {
            float a = ((p00 >> shift) & 0xFF) * (1 - tx) + ((p10 >> shift) & 0xFF) * tx;
            float b = ((p01 >> shift) & 0xFF) * (1 - tx) + ((p11 >> shift) & 0xFF) * tx;
            uint c = (uint)Math.Clamp((a * (1 - ty)) + (b * ty), 0, 255);

            result |= c << shift;
        }

        return result;
    }

    /// <summary>
    /// Resamples the (<paramref name="vx"/>, <paramref name="vy"/>) cell of a
    /// <paramref name="period"/> by <paramref name="period"/> division of the
    /// source into a <paramref name="size"/> square.
    /// </summary>
    public static uint[] ResampleRegion(
        ReadOnlySpan<uint> source,
        int width,
        int height,
        int vx,
        int vy,
        int period,
        int size)
    {
        if (period <= 1)
        {
            return Resample(source, width, height, size, size);
        }

        int x0 = (int)((long)vx * width / period);
        int x1 = (int)(((long)vx + 1) * width / period);
        int y0 = (int)((long)vy * height / period);
        int y1 = (int)(((long)vy + 1) * height / period);

        int w = Math.Max(1, x1 - x0);
        int h = Math.Max(1, y1 - y0);

        uint[] crop = new uint[w * h];

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                crop[(y * w) + x] = source[((y0 + y) * width) + x0 + x];
            }
        }

        return Resample(crop, w, h, size, size);
    }

    public void Clear()
    {
        _tiles.Clear();
        _composites.Clear();
        _compositeTiles.Clear();
        _textures.Clear();
        _definitions.Clear();
    }

    /// <summary>Source-over composite of <paramref name="src"/> onto <paramref name="dst"/>.</summary>
    public static void Over(uint[] dst, ReadOnlySpan<uint> src)
    {
        for (int i = 0; i < dst.Length; i++)
        {
            uint s = src[i];
            uint sa = (s >> 24) & 0xFF;

            if (sa == 0)
            {
                continue;
            }

            if (sa == 0xFF)
            {
                dst[i] = s;

                continue;
            }

            uint d = dst[i];
            uint inv = 255 - sa;

            uint r = (((s & 0xFF) * sa) + ((d & 0xFF) * inv)) / 255;
            uint g = ((((s >> 8) & 0xFF) * sa) + (((d >> 8) & 0xFF) * inv)) / 255;
            uint b = ((((s >> 16) & 0xFF) * sa) + (((d >> 16) & 0xFF) * inv)) / 255;
            uint a = Math.Max((d >> 24) & 0xFF, sa);

            dst[i] = (a << 24) | (b << 16) | (g << 8) | r;
        }
    }

    /// <summary>
    /// Box-filter resample, weighting colour by alpha so transparent texels do
    /// not drag the colour towards whatever happens to sit under them.
    /// </summary>
    public static uint[] Resample(ReadOnlySpan<uint> source, int width, int height, int newWidth, int newHeight)
    {
        if (width <= 0 || height <= 0 || newWidth <= 0 || newHeight <= 0)
        {
            throw new ArgumentException("bad resample size.");
        }

        uint[] result = new uint[newWidth * newHeight];

        for (int y = 0; y < newHeight; y++)
        {
            int y0 = (int)((long)y * height / newHeight);
            int y1 = (int)(((long)y + 1) * height / newHeight);

            if (y1 <= y0)
            {
                y1 = y0 + 1;
            }

            for (int x = 0; x < newWidth; x++)
            {
                int x0 = (int)((long)x * width / newWidth);
                int x1 = (int)(((long)x + 1) * width / newWidth);

                if (x1 <= x0)
                {
                    x1 = x0 + 1;
                }

                long r = 0;
                long g = 0;
                long b = 0;
                long a = 0;
                long weight = 0;
                int count = 0;
                long pr = 0, pg = 0, pb = 0;

                for (int sy = y0; sy < y1; sy++)
                {
                    for (int sx = x0; sx < x1; sx++)
                    {
                        uint p = source[(sy * width) + sx];
                        long pa = (p >> 24) & 0xFF;

                        pr += p & 0xFF;
                        pg += (p >> 8) & 0xFF;
                        pb += (p >> 16) & 0xFF;
                        r += (long)(p & 0xFF) * pa;
                        g += (long)((p >> 8) & 0xFF) * pa;
                        b += (long)((p >> 16) & 0xFF) * pa;
                        a += pa;
                        weight += pa;
                        count++;
                    }
                }

                // A box with no alpha at all keeps its plain average colour,
                // still fully transparent. Returning 0 here made it black, and
                // ground that is forced opaque afterwards - a transparent patch
                // of Grass_A on ClassicUO's stretched land path - then showed
                // as black blobs along a shoreline.
                result[(y * newWidth) + x] = weight == 0
                    ? (uint)(pb / count) << 16 | (uint)(pg / count) << 8 | (uint)(pr / count)
                    : (uint)(a / count) << 24
                        | (uint)(b / weight) << 16
                        | (uint)(g / weight) << 8
                        | (uint)(r / weight);
            }
        }

        return result;
    }
}
