// SPDX-License-Identifier: BSD-2-Clause
// Prints every string-dictionary reference in a tileart.uop record: any u32
// past the fixed block that indexes a path-like dictionary string. Evidence
// for which textures and scenes a static names besides its own art.

using ClassicUO.KR;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;

internal static class TileArtStrings
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: tileart-strings <uokrPath> <graphic>[,<graphic>...]");

            return 1;
        }

        using UopFile package = UopFile.Open(Path.Combine(args[0], KrTileArt.PackageName));
        KrStringDictionary strings = KrStringDictionary.Load(Path.Combine(args[0], KrStringDictionary.PackageName));

        // "idx:<from>-<to>" lists dictionary strings by index.
        if (args[1].StartsWith("idx:", StringComparison.Ordinal))
        {
            string[] r = args[1][4..].Split('-');

            for (int i = int.Parse(r[0]); i <= int.Parse(r[1]) && i < strings.Count; i++)
            {
                Console.WriteLine($"  [{i}] {strings[i]}");
            }

            return 0;
        }

        // "grep:<text>" lists dictionary strings containing the text instead.
        if (args[1].StartsWith("grep:", StringComparison.Ordinal))
        {
            string needle = args[1][5..];

            for (int i = 0; i < strings.Count; i++)
            {
                if (strings[i] is { } s && s.Contains(needle, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"  [{i}] {s}");
                }
            }

            return 0;
        }

        foreach (string part in args[1].Split(','))
        {
            int graphic = Program.ParseIntPublic(part);

            if (!package.TryGetEntry(KrTileArt.EntryName(graphic), out UopEntry entry))
            {
                Console.WriteLine($"{graphic} (0x{graphic:X4}): no record");

                continue;
            }

            byte[] data = package.Read(entry);
            Console.WriteLine($"{graphic} (0x{graphic:X4}): {data.Length} bytes");

            if (KrTileArt.Parse(data) is { } art)
            {
                Console.WriteLine(
                    $"  region ({art.RegionX},{art.RegionY})-({art.CornerX},{art.CornerY}) offset ({art.OffsetX},{art.OffsetY}) 2D {art.Width2D}x{art.Height2D}"
                    + $" source {KrTileArt.SourceGraphic(data, graphic, strings)} refs [{string.Join(",", KrTileArt.References(data, graphic, strings))}]");
            }

            // A shader definition block, as the terrain parser reads it
            // (0x7bebf1): u8 present, u8 flag, u32 class, u8 count, then
            // count x (u32 texture, u8 type, f32 scale, u32 a, u32 b).
            for (int o = 0; o + 7 <= data.Length; o++)
            {
                if (data[o] != 1)
                {
                    continue;
                }

                uint cls = BitConverter.ToUInt32(data, o + 2);
                int count = data[o + 6];

                if (cls == 0 || cls >= (uint)strings.Count || count is 0 or > 8 || o + 7 + (count * 17) > data.Length)
                {
                    continue;
                }

                List<string> layers = [];
                bool ok = true;

                for (int l = 0; l < count && ok; l++)
                {
                    int p = o + 7 + (l * 17);
                    uint tex = BitConverter.ToUInt32(data, p);
                    float scale = BitConverter.ToSingle(data, p + 5);

                    ok = tex > 0 && tex < (uint)strings.Count && scale > 0 && scale <= 64;
                    layers.Add($"{strings[(int)tex]} x{scale} type {data[p + 4]} a {BitConverter.ToUInt32(data, p + 9)} b {BitConverter.ToUInt32(data, p + 13)}");
                }

                if (ok)
                {
                    Console.WriteLine($"  shader @+0x{o:X3} flag {data[o + 1]} class {strings[(int)cls]}");

                    foreach (string l in layers)
                    {
                        Console.WriteLine($"    {l}");
                    }
                }
            }

            if (Program.GetOption(args, "--at") is { } at)
            {
                int start = Convert.ToInt32(at, 16);

                for (int o = start; o + 4 <= data.Length && o < start + 32; o += 4)
                {
                    Console.WriteLine($"  +0x{o:X3} {BitConverter.ToSingle(data, o)} ({BitConverter.ToInt32(data, o)})");
                }
            }

            if (args.Contains("--floats"))
            {
                // Plausible floats at every byte offset, to find a field.
                for (int o = 0; o + 4 <= data.Length; o++)
                {
                    float f = BitConverter.ToSingle(data, o);

                    if (float.IsFinite(f) && MathF.Abs(f) is > 0.01f and < 100f && MathF.Abs(f * 1000f - MathF.Round(f * 1000f)) < 1e-3f)
                    {
                        Console.WriteLine($"  float +0x{o:X3} {f}");
                    }
                }
            }

            if (args.Contains("--brief"))
            {
                continue;
            }

            for (int o = 2; o + 4 <= data.Length; o++)
            {
                uint index = BitConverter.ToUInt32(data, o);

                if (index >= (uint)strings.Count)
                {
                    continue;
                }

                string? s = strings[(int)index];

                if (!string.IsNullOrEmpty(s) && (s.Contains('\\') || s.Contains('.')))
                {
                    Console.WriteLine($"  +0x{o:X3}  [{index}] {s}");
                }
            }
        }

        return 0;
    }
}
