// mapdiff <uokrPath> <uo2dPath> [facets]: compares the 2D map files
// (map{n}.mul, staidx{n}.mul, statics{n}.mul) with KR's own facets tile by
// tile: land id, land z, and the statics on each tile (id and z, in any
// order). mapdif patches are not applied.

using ClassicUO.KR.World;

internal static class MapDiff
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: mapdiff <uokrPath> <uo2dPath> [facet,...]");

            return 1;
        }

        int[] facets = args.Length > 2 ? args[2].Split(',').Select(int.Parse).ToArray() : [0, 1, 2, 3, 4];

        foreach (int facet in facets)
        {
            string map = Path.Combine(args[1], $"map{facet}.mul");
            string idx = Path.Combine(args[1], $"staidx{facet}.mul");
            string sta = Path.Combine(args[1], $"statics{facet}.mul");
            string kr = Path.Combine(args[0], KrFacetFile.PackageName(facet));

            if (!File.Exists(map) || !File.Exists(kr))
            {
                Console.WriteLine($"facet {facet}: missing files");
                continue;
            }

            byte[] m = File.ReadAllBytes(map), ix = File.ReadAllBytes(idx), st = File.ReadAllBytes(sta);
            using KrFacetFile file = KrFacetFile.Open(kr, facet);
            int w = file.Width, h = file.Height, bh = h / 8;

            long tiles = 0, landId = 0, landZ = 0, staticTiles = 0, staticDiff = 0, krOnly = 0, twoDOnly = 0, unparsed = 0;
            var landPairs = new Dictionary<(int, int), long>();
            var staticOnly = new Dictionary<(bool Kr, int Id), long>();
            var example = new Dictionary<string, (int X, int Y)>();

            void Note(string kind, int x, int y) => example.TryAdd(kind, (x, y));

            for (int sector = 0; sector < file.SectorCount; sector++)
            {
                if (!file.TryParseSector(sector, out KrSector s))
                {
                    unparsed++;
                    continue;
                }

                for (int t = 0; t < s.Tiles.Length; t++)
                {
                    (int x, int y) = file.WorldCoordinates(sector, t);

                    if (x >= w || y >= h)
                    {
                        continue;
                    }

                    tiles++;
                    int block = (x / 8) * bh + (y / 8);
                    int cell = (y % 8) * 8 + (x % 8);
                    int o = block * 196 + 4 + cell * 3;
                    int id2 = BitConverter.ToUInt16(m, o);
                    sbyte z2 = (sbyte)m[o + 2];
                    KrLandTile lt = s.Tiles[t];

                    if (lt.LandTileId != id2)
                    {
                        landId++;
                        landPairs[(id2, lt.LandTileId)] = landPairs.GetValueOrDefault((id2, lt.LandTileId)) + 1;
                        Note("land id", x, y);
                    }
                    else if (lt.Z != z2)
                    {
                        landZ++;
                        Note("land z", x, y);
                    }

                    // 2D statics on this tile.
                    var two = new List<(int, int)>();
                    int io = block * 12;

                    if (io + 12 <= ix.Length)
                    {
                        int off = BitConverter.ToInt32(ix, io), len = BitConverter.ToInt32(ix, io + 4);

                        if (off >= 0 && len > 0)
                        {
                            for (int k = off; k + 7 <= off + len && k + 7 <= st.Length; k += 7)
                            {
                                if (st[k + 2] == x % 8 && st[k + 3] == y % 8)
                                {
                                    two.Add((BitConverter.ToUInt16(st, k), (sbyte)st[k + 4]));
                                }
                            }
                        }
                    }

                    var krs = new List<(int, int)>();

                    foreach (KrStatic ks in s.StaticsFor(t))
                    {
                        krs.Add(((int)ks.ArtId, ks.Z));
                    }

                    if (two.Count == 0 && krs.Count == 0)
                    {
                        continue;
                    }

                    staticTiles++;
                    two.Sort();
                    krs.Sort();

                    if (!two.SequenceEqual(krs))
                    {
                        staticDiff++;
                        Note("statics", x, y);

                        foreach (var p in two.Except(krs))
                        {
                            twoDOnly++;
                            staticOnly[(false, p.Item1)] = staticOnly.GetValueOrDefault((false, p.Item1)) + 1;
                        }

                        foreach (var p in krs.Except(two))
                        {
                            krOnly++;
                            staticOnly[(true, p.Item1)] = staticOnly.GetValueOrDefault((true, p.Item1)) + 1;
                        }
                    }
                }
            }

            Console.WriteLine($"facet {facet} ({w}x{h}): {tiles:N0} tiles, {unparsed} unparsed sectors");
            Console.WriteLine($"  land id differs {landId:N0} ({100.0 * landId / tiles:F3}%), same id but z differs {landZ:N0} ({100.0 * landZ / tiles:F3}%)");
            Console.WriteLine($"  tiles with statics {staticTiles:N0}, differing {staticDiff:N0} ({(staticTiles > 0 ? 100.0 * staticDiff / staticTiles : 0):F3}%): {twoDOnly:N0} only in 2D, {krOnly:N0} only in KR");

            foreach (var e in example)
            {
                Console.WriteLine($"  first {e.Key} difference at {e.Value.X},{e.Value.Y}");
            }

            foreach (var p in landPairs.OrderByDescending(p => p.Value).Take(5))
            {
                Console.WriteLine($"  land 2D 0x{p.Key.Item1:X4} vs KR 0x{p.Key.Item2:X4}: {p.Value:N0}");
            }

            foreach (var p in staticOnly.OrderByDescending(p => p.Value).Take(6))
            {
                Console.WriteLine($"  static 0x{p.Key.Id:X4} only in {(p.Key.Kr ? "KR" : "2D")}: {p.Value:N0}");
            }
        }

        return 0;
    }
}
