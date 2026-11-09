// SPDX-License-Identifier: BSD-2-Clause
// Dumps the land records of a box of tiles: KR terrain, 2D land id and z, and
// any seams - the evidence for how KR blends one terrain into the next.

using ClassicUO.KR;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;

internal static class TerrainArea
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: terrain-area <uokrPath> --facet <n> --at <x>,<y> [--radius <r>]");

            return 1;
        }

        int facet = Program.ParseIntPublic(Program.GetOption(args, "--facet") ?? "0");
        string[] at = (Program.GetOption(args, "--at") ?? "0,0").Split(',');
        int cx = int.Parse(at[0]);
        int cy = int.Parse(at[1]);
        int radius = Program.ParseIntPublic(Program.GetOption(args, "--radius") ?? "6");

        string path = Path.Combine(args[0], KrFacetFile.PackageName(facet));

        using KrFacetFile file = KrFacetFile.Open(path, facet);

        Dictionary<int, KrSector> sectors = [];

        KrSector? SectorFor(int x, int y)
        {
            int s = file.SectorIndex(x / KrFacetFile.SectorSize, y / KrFacetFile.SectorSize);

            if (!sectors.TryGetValue(s, out KrSector? sector))
            {
                sector = file.TryParseSector(s, out KrSector parsed) ? parsed : null;

                if (sector is not null)
                {
                    sectors[s] = sector;
                }
            }

            return sector;
        }

        Console.WriteLine("terrain id grid (rows y, columns x), * = tile has seams:");
        Console.Write("        ");

        for (int x = cx - radius; x <= cx + radius; x++)
        {
            Console.Write($"{x % 1000,5}");
        }

        Console.WriteLine();

        List<string> seamLines = [];
        List<string> staticLines = [];
        List<string> landLines = [];

        for (int y = cy - radius; y <= cy + radius; y++)
        {
            Console.Write($"{y,6}  ");

            for (int x = cx - radius; x <= cx + radius; x++)
            {
                KrSector? sector = SectorFor(x, y);

                if (sector is null)
                {
                    Console.Write("    -");

                    continue;
                }

                int t = KrFacetFile.TileIndex(x % KrFacetFile.SectorSize, y % KrFacetFile.SectorSize);
                KrLandTile land = sector.Tiles[t];
                ReadOnlySpan<KrTerrainSeam> seams = sector.SeamsFor(t);

                Console.Write($"{land.TerrainId,4}{(seams.Length > 0 ? '*' : ' ')}");
                landLines.Add($"  ({x},{y}) terrain {land.TerrainId} land {land.LandTileId}");

                foreach (KrStatic st in sector.StaticsFor(t))
                {
                    staticLines.Add($"  ({x},{y}) static 0x{st.ArtId:X4} z {st.Z} hue {st.Hue} (land {land.LandTileId})");
                }

                if (seams.Length > 0)
                {
                    List<string> parts = [];

                    foreach (KrTerrainSeam seam in seams)
                    {
                        parts.Add($"terrain {seam.TerrainId} z {seam.Z} trailing {seam.Trailing}");
                    }

                    seamLines.Add(
                        $"  ({x},{y}) terrain {land.TerrainId} land {land.LandTileId} z {land.Z} flag {land.Flag}: "
                        + string.Join("; ", parts));
                }
            }

            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine($"tiles with seams: {seamLines.Count}");

        foreach (string line in seamLines)
        {
            Console.WriteLine(line);
        }

        if (args.Contains("--land"))
        {
            foreach (string line in landLines)
            {
                Console.WriteLine(line);
            }
        }

        Console.WriteLine($"statics: {staticLines.Count}");

        foreach (string line in staticLines)
        {
            Console.WriteLine(line);
        }

        return 0;
    }
}
