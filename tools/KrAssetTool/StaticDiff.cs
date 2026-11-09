// SPDX-License-Identifier: BSD-2-Clause
// Compares the statics in a KR facet against the 2D client's statics0.mul for
// the same tiles, to see whether KR's map carries objects the 2D map does not.

using ClassicUO.KR;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;

internal static class StaticDiff
{
    /// <summary>
    /// Minimal statics reader. staidx is 12 bytes a block (lookup, length,
    /// extra); each static is 7 bytes: u16 graphic, u8 x, u8 y, s8 z, u16 hue.
    /// Blocks are indexed blockX * blocksDown + blockY.
    /// </summary>
    private sealed class Statics2D : IDisposable
    {
        private readonly FileStream _idx;
        private readonly FileStream _mul;
        private readonly int _blocksDown;

        public Statics2D(string uoPath, int map, int blocksDown)
        {
            _idx = File.OpenRead(Path.Combine(uoPath, $"staidx{map}.mul"));
            _mul = File.OpenRead(Path.Combine(uoPath, $"statics{map}.mul"));
            _blocksDown = blocksDown;
        }

        public List<(ushort Graphic, int X, int Y)> Block(int blockX, int blockY)
        {
            List<(ushort, int, int)> result = [];

            long entry = ((long)blockX * _blocksDown + blockY) * 12;

            if (entry + 12 > _idx.Length)
            {
                return result;
            }

            _idx.Seek(entry, SeekOrigin.Begin);

            byte[] head = new byte[12];
            _idx.ReadExactly(head);

            int lookup = BitConverter.ToInt32(head, 0);
            int length = BitConverter.ToInt32(head, 4);

            if (lookup < 0 || length <= 0 || lookup + length > _mul.Length)
            {
                return result;
            }

            _mul.Seek(lookup, SeekOrigin.Begin);

            byte[] data = new byte[length];
            _mul.ReadExactly(data);

            for (int o = 0; o + 7 <= data.Length; o += 7)
            {
                result.Add((
                    BitConverter.ToUInt16(data, o),
                    data[o + 2],
                    data[o + 3]));
            }

            return result;
        }

        public void Dispose()
        {
            _idx.Dispose();
            _mul.Dispose();
        }
    }

    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: static-diff <uokrPath> <uoPath> [--facet <n>] [--sectors <n>]");

            return 1;
        }

        int facet = Program.ParseIntPublic(Program.GetOption(args, "--facet") ?? "0");
        int limit = Program.ParseIntPublic(Program.GetOption(args, "--sectors") ?? "400");

        string path = Path.Combine(args[0], KrFacetFile.PackageName(facet));

        using KrFacetFile file = KrFacetFile.Open(path, facet);

        using Statics2D twoD = new(args[1], facet, file.Height / 8);

        Dictionary<ushort, int> krOnly = [];
        Dictionary<ushort, int> bothSeen = [];

        long krTotal = 0;
        long matched = 0;
        long twoDTotal = 0;

        Dictionary<ushort, int> twoDOnly = [];

        int sectors = Math.Min(limit, file.SectorCount);

        for (int sector = 0; sector < sectors; sector++)
        {
            if (!file.TryParseSector(sector, out KrSector data))
            {
                continue;
            }

            (int sx, int sy) = file.SectorCoordinates(sector);

            for (int tile = 0; tile < 4096; tile++)
            {
                ReadOnlySpan<KrStatic> statics = data.StaticsFor(tile);

                if (statics.Length == 0)
                {
                    continue;
                }

                (int tx, int ty) = KrFacetFile.TileCoordinates(tile);

                int worldX = (sx * KrFacetFile.SectorSize) + tx;
                int worldY = (sy * KrFacetFile.SectorSize) + ty;

                List<(ushort Graphic, int X, int Y)> block = twoD.Block(worldX / 8, worldY / 8);

                HashSet<ushort> here = [];

                foreach ((ushort g, int bx, int by) in block)
                {
                    if (bx == worldX % 8 && by == worldY % 8)
                    {
                        here.Add(g);
                    }
                }

                HashSet<ushort> krHere = [];

                foreach (KrStatic s in statics)
                {
                    krHere.Add((ushort)s.ArtId);
                }

                foreach (ushort g in here)
                {
                    twoDTotal++;

                    if (!krHere.Contains(g))
                    {
                        twoDOnly.TryGetValue(g, out int had2);
                        twoDOnly[g] = had2 + 1;
                    }
                }

                foreach (KrStatic s in statics)
                {
                    krTotal++;

                    ushort art = (ushort)s.ArtId;

                    if (here.Contains(art))
                    {
                        matched++;
                        bothSeen.TryGetValue(art, out int had);
                        bothSeen[art] = had + 1;
                    }
                    else
                    {
                        krOnly.TryGetValue(art, out int had);
                        krOnly[art] = had + 1;
                    }
                }
            }
        }

        Console.WriteLine($"facet {facet}, first {sectors} sectors");
        Console.WriteLine($"  KR static placements : {krTotal:N0}");
        Console.WriteLine($"  also in statics{facet}.mul: {matched:N0} ({(double)matched / Math.Max(krTotal, 1):P1})");
        Console.WriteLine($"  KR only              : {krTotal - matched:N0}");

        Console.WriteLine();
        Console.WriteLine("art ids KR places that the 2D map does not, most common first:");

        foreach ((ushort art, int count) in krOnly.OrderByDescending(e => e.Value).Take(25))
        {
            bothSeen.TryGetValue(art, out int also);

            Console.WriteLine($"    art {art,6}  KR-only {count,7:N0}  shared {also,7:N0}");
        }

        long twoDOnlyTotal = 0;

        foreach (int c in twoDOnly.Values)
        {
            twoDOnlyTotal += c;
        }

        Console.WriteLine();
        Console.WriteLine($"  2D static placements : {twoDTotal:N0}");
        Console.WriteLine($"  2D only (KR's map does not have them): {twoDOnlyTotal:N0}");
        Console.WriteLine();
        Console.WriteLine("art ids the 2D map places that KR does not, most common first:");

        foreach ((ushort art, int count) in twoDOnly.OrderByDescending(e => e.Value).Take(25))
        {
            Console.WriteLine($"    art {art,6}  2D-only {count,7:N0}");
        }

        return 0;
    }
}
