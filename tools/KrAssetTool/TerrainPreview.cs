// SPDX-License-Identifier: BSD-2-Clause
// Side-by-side preview of what the client's land tiles become with KR terrain.
//
// Runs exactly the substitution ClassicUO's Art.Get does - the 2D land tile's
// own alpha, repainted with the KR ground texture - so the picture is what the
// world will look like rather than an idea of it.

using ClassicUO.KR;
using ClassicUO.KR.Util;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;

internal static class TerrainPreview
{
    private const int LandSize = 44;

    /// <summary>
    /// Minimal art.mul land reader: entry is a 44x44 diamond of u16 ARGB1555,
    /// rows widening 2,4,..44 then narrowing again.
    /// </summary>
    /// <summary>ReadLand, for the audit command.</summary>
    internal static uint[]? ReadLandPublic(string uoPath, int graphic) => ReadLand(uoPath, graphic);

    private static uint[]? ReadLand(string uoPath, int graphic)
    {
        string idxPath = Path.Combine(uoPath, "artidx.mul");
        string artPath = Path.Combine(uoPath, "art.mul");

        if (!File.Exists(idxPath) || !File.Exists(artPath))
        {
            return null;
        }

        using FileStream idx = File.OpenRead(idxPath);

        long entry = (long)graphic * 12;

        if (entry + 12 > idx.Length)
        {
            return null;
        }

        idx.Seek(entry, SeekOrigin.Begin);

        byte[] head = new byte[12];
        idx.ReadExactly(head);

        int lookup = BitConverter.ToInt32(head, 0);
        int length = BitConverter.ToInt32(head, 4);

        if (lookup < 0 || length <= 0)
        {
            return null;
        }

        using FileStream art = File.OpenRead(artPath);

        if (lookup + length > art.Length)
        {
            return null;
        }

        art.Seek(lookup, SeekOrigin.Begin);

        byte[] data = new byte[length];
        art.ReadExactly(data);

        uint[] pixels = new uint[LandSize * LandSize];
        int p = 0;

        for (int y = 0; y < LandSize; y++)
        {
            int count = y < 22 ? (y + 1) * 2 : (LandSize - y) * 2;
            int x0 = (LandSize - count) / 2;

            for (int i = 0; i < count; i++)
            {
                if (p + 2 > data.Length)
                {
                    return pixels;
                }

                ushort v = (ushort)(data[p] | (data[p + 1] << 8));
                p += 2;

                uint r = (uint)(((v >> 10) & 31) << 3);
                uint g = (uint)(((v >> 5) & 31) << 3);
                uint b = (uint)((v & 31) << 3);

                pixels[(y * LandSize) + x0 + i] = 0xFF000000u | (b << 16) | (g << 8) | r;
            }
        }

        return pixels;
    }

    public static int Run(string[] args, Func<string[], string, string?> getOption, Func<string, int> parseInt)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: terrain-preview <uokrPath> <uoPath> [-o <dir>] [--count <n>]");

            return 1;
        }

        string krPath = args[0];
        string uoPath = args[1];
        string outDir = getOption(args, "-o") ?? getOption(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "kr-terrain-preview");
        int count = parseInt(getOption(args, "--count") ?? "16");

        Directory.CreateDirectory(outDir);

        KrTerrainMap map = KrTerrainMap.Build(krPath, [0, 1, 2, 3, 4]);

        using KrAssetStore store = new(krPath);

        KrTerrainArt art = new(store);

        // The land tile ids that cover the most ground, so the preview shows
        // what a player actually walks on.
        List<int> graphics = [];

        for (int g = 0; g < KrTerrainMap.LandGraphicCount; g++)
        {
            if (map.TileCount(g) > 0)
            {
                graphics.Add(g);
            }
        }

        graphics.Sort((a, b) => map.TileCount(b).CompareTo(map.TileCount(a)));

        int rows = Math.Min(count, graphics.Count);

        // Each row: 2D art, KR repaint, KR source texture.
        const int cell = LandSize;
        int width = cell * 3;
        int height = rows * cell;

        uint[] sheet = new uint[width * height];
        int written = 0;

        for (int row = 0; row < rows; row++)
        {
            int graphic = graphics[row];

            uint[]? twoD = ReadLand(uoPath, graphic);

            if (twoD is null)
            {
                continue;
            }

            map.TryGet(graphic, out ushort terrain);

            uint[]? kr = art.GroundTile(terrain, LandSize);

            void Blit(uint[] src, int column)
            {
                for (int y = 0; y < cell; y++)
                {
                    for (int x = 0; x < cell; x++)
                    {
                        sheet[((row * cell) + y) * width + (column * cell) + x] = src[(y * cell) + x];
                    }
                }
            }

            Blit(twoD, 0);

            if (kr is not null)
            {
                uint[] repainted = new uint[twoD.Length];

                for (int i = 0; i < twoD.Length; i++)
                {
                    uint src = twoD[i];
                    repainted[i] = src == 0 ? 0u : (src & 0xFF000000) | (kr[i] & 0x00FFFFFF);
                }

                Blit(repainted, 1);

                uint[] square = new uint[cell * cell];

                for (int i = 0; i < square.Length; i++)
                {
                    square[i] = kr[i] | 0xFF000000;
                }

                Blit(square, 2);
            }

            string name = art.GroundArtId(terrain) ?? "<none>";

            Console.WriteLine($"  land {graphic,5} ({map.TileCount(graphic),8:N0} tiles) -> terrain {terrain,3} -> {name}");
            written++;
        }

        string path = Path.Combine(outDir, "terrain-preview.png");
        PngWriter.Write(path, width, height, sheet);

        Console.WriteLine($"{written} row(s): 2D art | KR repaint | KR texture -> {path}");

        return 0;
    }

    /// <summary>
    /// Lays out a patch of ground the way the game does - diamonds on the
    /// isometric grid - so the tiling can be judged rather than guessed at.
    /// </summary>
    public static int RunPatch(string[] args, Func<string[], string, string?> getOption, Func<string, int> parseInt)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: terrain-patch <uokrPath> [--land <graphic>] [--tiles <n>] [-o <dir>]");

            return 1;
        }

        string krPath = args[0];
        int graphic = parseInt(getOption(args, "--land") ?? "3");
        int tiles = Math.Clamp(parseInt(getOption(args, "--tiles") ?? "12"), 2, 40);
        string outDir = getOption(args, "-o") ?? getOption(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "kr-terrain-preview");

        Directory.CreateDirectory(outDir);

        KrTerrainMap map = KrTerrainMap.Build(krPath, [0, 1, 2, 3, 4]);

        if (!map.TryGet(graphic, out ushort terrain))
        {
            Console.Error.WriteLine($"land tile {graphic} has no KR terrain.");

            return 4;
        }

        using KrAssetStore store = new(krPath);

        KrTerrainArt art = new(store);

        int period = art.PeriodFor(terrain);

        // Screen layout: a tile at (x, y) sits at ((x - y) * 22, (x + y) * 22).
        int width = (tiles * 2 * 22) + LandSize;
        int height = (tiles * 2 * 22) + LandSize;
        uint[] sheet = new uint[width * height];

        int originX = (tiles * 22);

        for (int y = 0; y < tiles; y++)
        {
            for (int x = 0; x < tiles; x++)
            {
                uint[]? tile = art.GroundDiamond(terrain, LandSize, x, y);

                if (tile is null)
                {
                    continue;
                }

                int sx = originX + ((x - y) * 22);
                int sy = (x + y) * 22;

                for (int py = 0; py < LandSize; py++)
                {
                    for (int px = 0; px < LandSize; px++)
                    {
                        uint pixel = tile[(py * LandSize) + px];

                        if ((pixel >> 24) == 0)
                        {
                            continue;
                        }

                        int tx = sx + px;
                        int ty = sy + py;

                        if ((uint)tx >= (uint)width || (uint)ty >= (uint)height)
                        {
                            continue;
                        }

                        sheet[(ty * width) + tx] = pixel;
                    }
                }
            }
        }

        string path = Path.Combine(outDir, $"terrain-patch-{graphic}.png");
        PngWriter.Write(path, width, height, sheet);

        Console.WriteLine(
            $"land {graphic} -> terrain {terrain} -> {art.GroundArtId(terrain)}, " +
            $"period {period} tiles, {tiles}x{tiles} patch -> {path}");

        return 0;
    }
}
