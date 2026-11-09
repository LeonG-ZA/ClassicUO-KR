// coverage <uokrPath> <ecPath>: which land tiles and statics on the
// Enhanced Client's facets (the newest map data) KR has art for.
//
// Land: KR's land-to-terrain map is the sweep of its own facets 0-4
// (KrTerrainMap.Build), as the client builds it; a land id outside it is
// drawn from 2D art. Statics: KR art is build/worldart/{id}.dds in
// Texture.uop, for the graphic or the source its tileart record names.

using System.Globalization;
using ClassicUO.KR;
using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;

internal static class Coverage
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: coverage <uokrPath> <ecPath>");

            return 1;
        }

        string kr = args[0], ec = args[1];

        KrLegacyTerrainMap legacy = KrLegacyTerrainMap.Load(Path.Combine(ec, KrLegacyTerrainMap.PackageName));
        KrTerrainMap krLand = KrTerrainMap.Build(kr, [0, 1, 2, 3, 4]);

        using UopFile krTex = UopFile.Open(Path.Combine(kr, "Texture.uop"));
        using UopFile ecTex = UopFile.Open(Path.Combine(ec, "Texture.uop"));
        using UopFile ecLegacy = UopFile.Open(Path.Combine(ec, "LegacyTexture.uop"));
        string? artIdx = args.Length > 3 ? args[3] : null;
        byte[] idx = artIdx is not null && File.Exists(artIdx) ? File.ReadAllBytes(artIdx) : [];
        bool Has2D(int id, bool isLand)
        {
            int e = isLand ? id : id + 0x4000;
            if ((e + 1) * 12 > idx.Length) return false;
            int off = BitConverter.ToInt32(idx, e * 12), len = BitConverter.ToInt32(idx, e * 12 + 4);
            return off >= 0 && len > 0 && off != -1;
        }
        bool EcLegacy(int id) => ecLegacy.Contains(string.Create(CultureInfo.InvariantCulture, $"build/tileartlegacy/{id:D8}.dds"));
        KrAssetStore krStore = new(kr);
        KrAssetStore ecStore = new(ec);
        KrStringDictionary krStrings = KrStringDictionary.Load(Path.Combine(kr, "string_dictionary.uop"));

        // facet -> id -> tiles
        var land = new SortedDictionary<int, Dictionary<int, long>>();
        var statics = new SortedDictionary<int, Dictionary<int, long>>();
        var where = new Dictionary<(bool Static, int Id), (int Facet, int X, int Y)>();

        string[] names = ["facet0", "facet1", "facet2", "facet3", "facet4", "facet5", "facet0x", "facet1x", "facet2x", "facet5x"];

        for (int n = 0; n < names.Length; n++)
        {
            string path = Path.Combine(ec, names[n] + ".uop");

            if (!File.Exists(path))
            {
                continue;
            }

            int facet = names[n][5] - '0';
            int key = n; // index into names
            var l = land[key] = [];
            var s = statics[key] = [];

            using KrFacetFile file = KrFacetFile.Open(path, facet, legacy);

            for (int sector = 0; sector < file.SectorCount; sector++)
            {
                if (!file.TryParseSector(sector, out KrSector parsed))
                {
                    continue;
                }

                for (int t = 0; t < parsed.Tiles.Length; t++)
                {
                    int id = parsed.Tiles[t].LandTileId;
                    l[id] = l.GetValueOrDefault(id) + 1;

                    if (!where.ContainsKey((false, id)))
                    {
                        (int x, int y) = file.WorldCoordinates(sector, t);
                        where[(false, id)] = (facet, x, y);
                    }

                    foreach (KrStatic st in parsed.StaticsFor(t))
                    {
                        int sid = (int)st.ArtId;
                        s[sid] = s.GetValueOrDefault(sid) + 1;

                        if (!where.ContainsKey((true, sid)))
                        {
                            (int x, int y) = file.WorldCoordinates(sector, t);
                            where[(true, sid)] = (facet, x, y);
                        }
                    }
                }
            }

            Console.WriteLine($"{names[n]}: {file.Width}x{file.Height}, {l.Count} land ids, {s.Count} static ids");
        }

        bool KrStaticArt(int id)
        {
            if (krTex.Contains(KrTexturePackages.WorldArtEntryName(id)))
            {
                return true;
            }

            byte[]? raw = KrTileArt.ReadRaw(krStore, id);

            if (raw is null)
            {
                return false;
            }

            if (KrTileArt.SourceGraphic(raw, id, krStrings) is int src && src != id
                && krTex.Contains(KrTexturePackages.WorldArtEntryName(src)))
            {
                return true;
            }

            foreach (int r in KrTileArt.References(raw, id, krStrings))
            {
                if (krTex.Contains(KrTexturePackages.WorldArtEntryName(r)))
                {
                    return true;
                }
            }

            return false;
        }

        bool EcStaticArt(int id) => ecTex.Contains(KrTexturePackages.WorldArtEntryName(id));

        Console.WriteLine();
        Console.WriteLine("facet    | land ids | no KR terrain (tiles)     | static ids | no KR art (placements)");

        var missingLand = new Dictionary<int, long>();
        var missingStatic = new Dictionary<int, long>();
        var staticFacets = new Dictionary<int, SortedSet<string>>();
        var landFacets = new Dictionary<int, SortedSet<string>>();

        foreach (int key in land.Keys)
        {
            var l = land[key];
            var s = statics[key];
            long landTiles = l.Values.Sum(), staticCount = s.Values.Sum();
            var ml = l.Where(p => !krLand.TryGet(p.Key, out _)).ToList();
            var ms = s.Where(p => !KrStaticArt(p.Key)).ToList();

            foreach (var p in ml)
            {
                missingLand[p.Key] = missingLand.GetValueOrDefault(p.Key) + p.Value;
                (landFacets.TryGetValue(p.Key, out var set) ? set : landFacets[p.Key] = []).Add(names[key]);
            }

            foreach (var p in ms)
            {
                missingStatic[p.Key] = missingStatic.GetValueOrDefault(p.Key) + p.Value;
                (staticFacets.TryGetValue(p.Key, out var set) ? set : staticFacets[p.Key] = []).Add(names[key]);
            }

            long mlTiles = ml.Sum(p => p.Value), msCount = ms.Sum(p => p.Value);
            var gap = ms.Where(p => KrTileArt.ReadRaw(krStore, p.Key) is null).ToList();
            long gapCount = gap.Sum(p => p.Value);
            long gapLegacy = gap.Where(p => EcLegacy(p.Key)).Sum(p => p.Value);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  {names[key]}: statics with no KR record {gap.Count} ids, {gapCount:N0} of {staticCount:N0} placements ({100.0 * gapCount / staticCount:F2}%); EC legacy art for {gapLegacy:N0} of them"));
            Console.WriteLine(
                $"{names[key],-8} | {l.Count,8} | {ml.Count,4} ids, {mlTiles,9:N0} ({100.0 * mlTiles / landTiles,5:F2}%) | {s.Count,10} | {ms.Count,4} ids, {msCount,8:N0} ({100.0 * msCount / staticCount,5:F2}%)");
        }

        string outDir = args.Length > 2 ? args[2] : ".";
        Directory.CreateDirectory(outDir);

        using (StreamWriter w = new(Path.Combine(outDir, "missing-land.csv")))
        {
            w.WriteLine("land_id,hex,tiles,csv_terrain,ec_has_terrain_def,art_2d,facets,first_seen");

            foreach (var p in missingLand.OrderByDescending(p => p.Value))
            {
                int t = legacy.TerrainFor(p.Key);
                var at = where[(false, p.Key)];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{p.Key},0x{p.Key:X4},{p.Value},{t},{(t >= 0 && t < 2001 ? "yes" : "no")},{(Has2D(p.Key, true) ? "yes" : "no")},{string.Join(' ', landFacets[p.Key])},{at.Facet}:{at.X}:{at.Y}"));
            }
        }

        int ecHas = 0;

        using (StreamWriter w = new(Path.Combine(outDir, "missing-statics.csv")))
        {
            w.WriteLine("static_id,hex,placements,kr_tileart,ec_worldart,ec_tileart,ec_legacy,art_2d,above_2d_range,facets,first_seen");

            foreach (var p in missingStatic.OrderByDescending(p => p.Value))
            {
                bool e = EcStaticArt(p.Key);
                ecHas += e ? 1 : 0;
                var at = where[(true, p.Key)];
                w.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{p.Key},0x{p.Key:X4},{p.Value},{(KrTileArt.ReadRaw(krStore, p.Key) is null ? "no" : "yes")},{(e ? "yes" : "no")},{(KrTileArt.ReadRaw(ecStore, p.Key) is null ? "no" : "yes")},{(EcLegacy(p.Key) ? "yes" : "no")},{(Has2D(p.Key, false) ? "yes" : "no")},{(p.Key >= 0x4000 ? "yes" : "no")},{string.Join(' ', staticFacets[p.Key])},{at.Facet}:{at.X}:{at.Y}"));
            }
        }

        Console.WriteLine();
        Console.WriteLine($"all facets: {missingLand.Count} land ids without a KR terrain, {missingStatic.Count} static ids without KR art ({ecHas} of them have EC world art).");
        Console.WriteLine($"wrote {Path.Combine(outDir, "missing-land.csv")} and missing-statics.csv");

        return 0;
    }
}
