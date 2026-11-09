// SPDX-License-Identifier: BSD-2-Clause
// Renders a box of KR ground the way the client's flat path draws it, beside
// the same tiles in 2D art, so a screenshot from the KR client can be compared
// with what we would draw at the same place.

using ClassicUO.KR;
using ClassicUO.KR.Util;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;

internal static class TerrainRender
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: terrain-render <uokrPath> <uoPath> --facet <n> --at <x>,<y> [--radius <r>] [-o <file>]");

            return 1;
        }

        int facet = Program.ParseIntPublic(Program.GetOption(args, "--facet") ?? "0");
        string[] at = (Program.GetOption(args, "--at") ?? "0,0").Split(',');
        int cx = int.Parse(at[0]);
        int cy = int.Parse(at[1]);
        int r = Program.ParseIntPublic(Program.GetOption(args, "--radius") ?? "8");
        string output = Program.GetOption(args, "-o") ?? "terrain-render.png";
        string blend = Program.GetOption(args, "--blend") ?? "none";

        using KrAssetStore store = new(args[0]);
        using KrFacetFile file = KrFacetFile.Open(Path.Combine(args[0], KrFacetFile.PackageName(facet)), facet);

        KrTerrainArt art = new(store);

        int span = (r * 2) + 1;
        int w = (span * 44) + 44;
        int h = (span * 44) + 88;

        if (args.Contains("--classify"))
        {
            // Why land would stay off the GPU terrain path, over the area.
            Dictionary<int, KrSector?> sec = [];
            Dictionary<string, int> why = [];
            Dictionary<int, int> nonComposite = [];

            for (int y = cy - r; y <= cy + r; y++)
            {
                for (int x = cx - r; x <= cx + r; x++)
                {
                    int[] b5 = new int[25];

                    for (int by = -2; by <= 2; by++)
                    {
                        for (int bx = -2; bx <= 2; bx++)
                        {
                            b5[((by + 2) * 5) + bx + 2] = TerrainAt(file, sec, x + bx, y + by);
                        }
                    }

                    string reason;
                    int c = b5[12];

                    if (c < 0) reason = "no terrain";
                    else if (art.IsWaterSurface(c) || art.IsStars(c) || art.IsLava(c)) reason = "water-class";
                    else if (art.Composite(c) is null) { reason = "centre not composite"; nonComposite[c] = nonComposite.GetValueOrDefault(c) + 1; }
                    else
                    {
                        List<int> order = art.BlendOrder(b5, false).Where(t => !new KrTerrainBlend.WeightGrid(b5, t, art.Exponent(t)).IsEmpty).ToList();
                        int bad = order.FirstOrDefault(t => art.IsWaterSurface(t) || art.IsStars(t) || art.IsLava(t) || art.Composite(t) is null, -1);

                        if (b5.All(t => t == c)) reason = "gpu uniform";
                        else if (order.Count > 3) reason = $"{order.Count} layers";
                        else if (bad >= 0) { reason = "layer not composite"; nonComposite[bad] = nonComposite.GetValueOrDefault(bad) + 1; }
                        else reason = "gpu border";
                    }

                    why[reason] = why.GetValueOrDefault(reason) + 1;
                }
            }

            foreach (var kv in why.OrderByDescending(k => k.Value)) Console.WriteLine($"{kv.Key,-22} {kv.Value}");
            foreach (var kv in nonComposite.OrderByDescending(k => k.Value).Take(10)) Console.WriteLine($"  terrain {kv.Key} not composite: {kv.Value}");

            return 0;
        }

        if (args.Contains("--bench"))
        {
            // Times BlendedDiamond over the area: first pass cold (textures
            // and ground tiles decoded), second warm.
            Dictionary<int, KrSector?> sec = [];
            List<int[]> blocks = [];
            List<(int, int)> where = [];

            for (int y = cy - r; y <= cy + r; y++)
            {
                for (int x = cx - r; x <= cx + r; x++)
                {
                    int[] b5 = new int[25];

                    for (int by = -2; by <= 2; by++)
                    {
                        for (int bx = -2; bx <= 2; bx++)
                        {
                            b5[((by + 2) * 5) + bx + 2] = TerrainAt(file, sec, x + bx, y + by);
                        }
                    }

                    blocks.Add(b5);
                    where.Add((x, y));
                }
            }

            int border = blocks.Count(b => b.Any(t => t != b[12]));

            for (int pass = 0; pass < 3; pass++)
            {
                // Pass 2: the same terrain at positions shifted by 7 tiles -
                // textures decoded, ground cells mostly not.
                int shift = pass == 2 ? 7 : 0;
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();

                for (int i = 0; i < blocks.Count; i++)
                {
                    art.BlendedDiamond(blocks[i], 44, where[i].Item1 + shift, where[i].Item2 + shift, ascending: false);
                }

                Console.WriteLine($"pass {pass}: {blocks.Count} tiles ({border} border) in {sw.Elapsed.TotalMilliseconds:0.0} ms, {sw.Elapsed.TotalMilliseconds / blocks.Count:0.000} ms a tile");
            }

            // Pass 3: fresh cells again (shift 13), ground cells alone, then
            // the blend over them once they are cached.
            {
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                int cells = 0;

                for (int i = 0; i < blocks.Count; i++)
                {
                    foreach (int t in blocks[i].Distinct())
                    {
                        if (t >= 0)
                        {
                            art.GroundDiamond(t, 44, where[i].Item1 + 13, where[i].Item2 + 13);
                            cells++;
                        }
                    }
                }

                double ground = sw.Elapsed.TotalMilliseconds;
                sw.Restart();

                for (int i = 0; i < blocks.Count; i++)
                {
                    art.BlendedDiamond(blocks[i], 44, where[i].Item1 + 13, where[i].Item2 + 13, ascending: false);
                }

                Console.WriteLine($"pass 3: ground cells {ground:0.0} ms for {cells} lookups, blend {sw.Elapsed.TotalMilliseconds:0.0} ms");
            }

            return 0;
        }

        uint[] kr = new uint[w * h];
        uint[] twoD = new uint[w * h];

        Dictionary<int, KrSector?> sectors = [];

        for (int y = cy - r; y <= cy + r; y++)
        {
            for (int x = cx - r; x <= cx + r; x++)
            {
                int s = file.SectorIndex(x / KrFacetFile.SectorSize, y / KrFacetFile.SectorSize);

                if (!sectors.TryGetValue(s, out KrSector? sector))
                {
                    sector = file.TryParseSector(s, out KrSector parsed) ? parsed : null;
                    sectors[s] = sector;
                }

                if (sector is null)
                {
                    continue;
                }

                KrLandTile land = sector.Tiles[KrFacetFile.TileIndex(x % KrFacetFile.SectorSize, y % KrFacetFile.SectorSize)];

                int dx = x - cx;
                int dy = y - cy;
                int left = (w / 2) + ((dx - dy) * 22) - 22;
                int top = (h / 2) + ((dx + dy) * 22) - 22 - (land.Z * 4);

                uint[]? tile;

                if (blend == "none")
                {
                    tile = art.GroundDiamond(land.TerrainId, 44, x, y);
                }
                else
                {
                    int[] block5 = new int[25];

                    for (int by = -2; by <= 2; by++)
                    {
                        for (int bx = -2; bx <= 2; bx++)
                        {
                            block5[((by + 2) * 5) + bx + 2] = TerrainAt(file, sectors, x + bx, y + by);
                        }
                    }

                    if (Program.GetOption(args, "--square") is not null || args.Contains("--square"))
                    {
                        // The stretched path: a 64 px square, shown as the
                        // diamond the shader would map it onto.
                        uint[]? sq = art.BlendedTile(block5, 64, x, y, ascending: false);
                        tile = sq is null ? null : SquareToDiamond(sq, 64);
                    }
                    else
                    {
                        tile = art.BlendedDiamond(block5, 44, x, y, ascending: blend == "asc");
                    }
                }

                if (tile is not null)
                {
                    Blit(kr, w, h, tile, 44, 44, left, top);
                }

                uint[]? two = TerrainPreview.ReadLandPublic(args[1], land.LandTileId);

                if (two is not null)
                {
                    Blit(twoD, w, h, two, 44, 44, left, top);
                }
            }
        }

        uint[] sheet = new uint[w * 2 * h];

        for (int y = 0; y < h; y++)
        {
            Array.Copy(twoD, y * w, sheet, y * w * 2, w);
            Array.Copy(kr, y * w, sheet, (y * w * 2) + w, w);
        }

        PngWriter.Write(output, w * 2, h, sheet);
        Console.WriteLine($"{span}x{span} tiles around {cx},{cy} -> {output} (2D left, KR right)");

        return 0;
    }

    private static uint[] SquareToDiamond(uint[] sq, int size)
    {
        uint[] d = new uint[44 * 44];

        for (int py = 0; py < 44; py++)
        {
            int count = py < 22 ? (py + 1) * 2 : (44 - py) * 2;
            int start = (44 - count) / 2;

            for (int px = start; px < start + count; px++)
            {
                float u = (px - 22f + py) / 44f;
                float v = (py - px + 22f) / 44f;
                int sx = Math.Clamp((int)(u * size), 0, size - 1);
                int sy = Math.Clamp((int)(v * size), 0, size - 1);
                d[(py * 44) + px] = sq[(sy * size) + sx];
            }
        }

        return d;
    }

    private static int TerrainAt(KrFacetFile file, Dictionary<int, KrSector?> sectors, int x, int y)
    {
        if (x < 0 || y < 0)
        {
            return -1;
        }

        int s = file.SectorIndex(x / KrFacetFile.SectorSize, y / KrFacetFile.SectorSize);

        if (!sectors.TryGetValue(s, out KrSector? sector))
        {
            sector = file.TryParseSector(s, out KrSector parsed) ? parsed : null;
            sectors[s] = sector;
        }

        return sector is null
            ? -1
            : sector.Tiles[KrFacetFile.TileIndex(x % KrFacetFile.SectorSize, y % KrFacetFile.SectorSize)].TerrainId;
    }

    private static void Blit(uint[] dst, int dw, int dh, uint[] src, int sw, int sh, int x0, int y0)
    {
        for (int y = 0; y < sh; y++)
        {
            int ty = y0 + y;

            if (ty < 0 || ty >= dh)
            {
                continue;
            }

            for (int x = 0; x < sw; x++)
            {
                int tx = x0 + x;
                uint p = src[(y * sw) + x];

                if (tx >= 0 && tx < dw && (p >> 24) > 0)
                {
                    dst[(ty * dw) + tx] = p;
                }
            }
        }
    }
}
