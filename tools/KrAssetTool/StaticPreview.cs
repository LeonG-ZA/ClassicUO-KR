// SPDX-License-Identifier: BSD-2-Clause
// Renders KR static art beside the 2D original, so the two can be compared
// before anything is changed in the client.

using ClassicUO.KR;
using ClassicUO.KR.Util;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;


internal static class StaticPreview
{
    /// <summary>
    /// Minimal art.mul static reader: entry 0x4000 + graphic, a u16 width and
    /// height then a run-length encoded body of u16 ARGB1555.
    /// </summary>
    private static uint[]? ReadStatic(string uoPath, int graphic, out int width, out int height)
    {
        width = height = 0;

        string idxPath = Path.Combine(uoPath, "artidx.mul");
        string artPath = Path.Combine(uoPath, "art.mul");

        if (!File.Exists(idxPath) || !File.Exists(artPath))
        {
            return null;
        }

        using FileStream idx = File.OpenRead(idxPath);

        long entry = (long)(0x4000 + graphic) * 12;

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

        width = BitConverter.ToUInt16(data, 4);
        height = BitConverter.ToUInt16(data, 6);

        if (width <= 0 || width > 1024 || height <= 0 || height > 1024)
        {
            return null;
        }

        uint[] pixels = new uint[width * height];

        // Row offsets follow the header, then each row is a run of
        // (offset, count) pairs terminated by a zero pair.
        int lookupTable = 8;
        int dataStart = lookupTable + (height * 2);

        for (int y = 0; y < height; y++)
        {
            int rowOffset = BitConverter.ToUInt16(data, lookupTable + (y * 2)) * 2;
            int pos = dataStart + rowOffset;
            int x = 0;

            while (pos + 4 <= data.Length)
            {
                int xOffset = BitConverter.ToUInt16(data, pos);
                int run = BitConverter.ToUInt16(data, pos + 2);

                pos += 4;

                if (xOffset == 0 && run == 0)
                {
                    break;
                }

                x += xOffset;

                for (int i = 0; i < run && pos + 2 <= data.Length; i++, pos += 2, x++)
                {
                    ushort c = BitConverter.ToUInt16(data, pos);

                    if (x < 0 || x >= width)
                    {
                        continue;
                    }

                    uint r = (uint)((c >> 10) & 0x1F) * 255 / 31;
                    uint g = (uint)((c >> 5) & 0x1F) * 255 / 31;
                    uint b = (uint)(c & 0x1F) * 255 / 31;

                    pixels[(y * width) + x] = 0xFF000000u | (b << 16) | (g << 8) | r;
                }
            }
        }

        return pixels;
    }

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine(
                "usage: static-preview <uokrPath> [<uoPath>] --graphic <n>[,<n>...] [-o <dir>]");

            return 1;
        }

        string krPath = args[0];
        string? uoPath = args.Length > 1 && !args[1].StartsWith('-') ? args[1] : null;

        string list = Program.GetOption(args, "--graphic") ?? "3278";
        string outDir = Program.GetOption(args, "-o") ?? "out/statics";

        Directory.CreateDirectory(outDir);

        using KrAssetStore store = new(krPath);

        KrStaticArt art = new(store);

        // --pairs composites the companion static KR draws with this one.
        KrStaticPairs? pairs = Program.GetOption(args, "--pairs") is not null
            || Array.IndexOf(args, "--pairs") >= 0
                ? KrStaticPairs.Build(krPath, [0])
                : null;

        if (pairs is not null)
        {
            Console.WriteLine($"{pairs.Paired} art ids have a companion");
        }

        foreach (string part in list.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part.Trim(), out int graphic))
            {
                continue;
            }

            int companion = -1;

            if (pairs is not null && pairs.TryGet(graphic, out ushort mate))
            {
                companion = mate;
            }

            KrStaticSprite? sprite = art.SpriteWith(graphic, companion, 0, 0);

            if (sprite is null)
            {
                Console.WriteLine($"static {graphic}: KR has no art");

                continue;
            }

            uint[]? twoD = uoPath is null
                ? null
                : ReadStatic(uoPath, graphic, out int tw, out int th) is { } p
                    ? Pad(p, tw, th, out tw, out th)
                    : null;

            int gap = 8;
            int width = sprite.Width + (twoD is null ? 0 : gap + _lastWidth);
            int height = Math.Max(sprite.Height, twoD is null ? 0 : _lastHeight);

            uint[] sheet = new uint[width * height];

            Blit(sheet, width, sprite.Pixels, sprite.Width, sprite.Height, 0, height - sprite.Height);

            if (twoD is not null)
            {
                Blit(sheet, width, twoD, _lastWidth, _lastHeight,
                    sprite.Width + gap, height - _lastHeight);
            }

            string path = Path.Combine(outDir, $"static-{graphic}.png");

            PngWriter.Write(path, width, height, sheet);

            Console.WriteLine(
                $"static {graphic}: KR {sprite.Width}x{sprite.Height}"
                + (companion >= 0 ? $" +{companion}" : "")
                + (twoD is null ? "" : $", 2D {_lastWidth}x{_lastHeight}")
                + $" -> {path}");
        }

        return 0;
    }

    private static int _lastWidth;
    private static int _lastHeight;

    private static uint[] Pad(uint[] pixels, int w, int h, out int outW, out int outH)
    {
        _lastWidth = outW = w;
        _lastHeight = outH = h;

        return pixels;
    }

    private static void Blit(
        uint[] dst, int dstWidth, uint[] src, int srcWidth, int srcHeight, int x0, int y0)
    {
        for (int y = 0; y < srcHeight; y++)
        {
            for (int x = 0; x < srcWidth; x++)
            {
                dst[((y0 + y) * dstWidth) + x0 + x] = src[(y * srcWidth) + x];
            }
        }
    }
}
