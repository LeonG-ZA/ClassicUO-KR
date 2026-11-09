// SPDX-License-Identifier: BSD-2-Clause
// Builds small synthetic AMOU files so the decoder can be tested without the
// user's UOKR install.

using System.Buffers.Binary;

namespace ClassicUO.KR.Tests;

internal static class AmouBuilder
{
    public const int PaletteOffset = 0x28;
    public const int FrameTableOffset = 0x428;

    /// <summary>
    /// Builds a one-frame AMOU file. <paramref name="rows"/> holds the raw RLE
    /// token stream for each row.
    /// </summary>
    public static byte[] Build(
        uint bodyId,
        short x1,
        short y1,
        short x2,
        short y2,
        IReadOnlyList<byte[]> rows,
        IReadOnlyList<(byte R, byte G, byte B)> palette,
        ushort action = 0,
        ushort index = 1)
        => Build(bodyId, [(action, index, x1, y1, x2, y2, Concat(rows))], palette);

    /// <summary>Builds an AMOU file with several frames, each with its own pixel stream.</summary>
    public static byte[] Build(
        uint bodyId,
        IReadOnlyList<(ushort Action, ushort Index, short X1, short Y1, short X2, short Y2, byte[] Pixels)> frames,
        IReadOnlyList<(byte R, byte G, byte B)> palette)
    {
        int tableSize = frames.Count * 16;
        int pixelStart = FrameTableOffset + tableSize;
        int total = pixelStart + frames.Sum(f => f.Pixels.Length);

        byte[] data = new byte[total];
        Span<byte> s = data;

        "AMOU"u8.CopyTo(s);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x04), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x08), (uint)total);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x0C), bodyId);
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(0x10), frames.Min(f => f.X1));
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(0x12), frames.Min(f => f.Y1));
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(0x14), frames.Max(f => f.X2));
        BinaryPrimitives.WriteInt16LittleEndian(s.Slice(0x16), frames.Max(f => f.Y2));
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x18), 256);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x1C), PaletteOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x20), (uint)frames.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(0x24), FrameTableOffset);

        // Palette entries are stored R, G, B, unused.
        for (int i = 0; i < palette.Count && i < 256; i++)
        {
            int p = PaletteOffset + (i * 4);
            data[p] = palette[i].R;
            data[p + 1] = palette[i].G;
            data[p + 2] = palette[i].B;
        }

        int pixelCursor = pixelStart;

        for (int i = 0; i < frames.Count; i++)
        {
            var f = frames[i];
            int e = FrameTableOffset + (i * 16);

            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(e), f.Action);
            BinaryPrimitives.WriteUInt16LittleEndian(s.Slice(e + 0x02), f.Index);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(e + 0x04), f.X1);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(e + 0x06), f.Y1);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(e + 0x08), f.X2);
            BinaryPrimitives.WriteInt16LittleEndian(s.Slice(e + 0x0A), f.Y2);

            // The pixel offset is relative to the frame entry's own address.
            BinaryPrimitives.WriteUInt32LittleEndian(s.Slice(e + 0x0C), (uint)(pixelCursor - e));

            f.Pixels.CopyTo(data, pixelCursor);
            pixelCursor += f.Pixels.Length;
        }

        return data;
    }

    private static byte[] Concat(IReadOnlyList<byte[]> rows)
    {
        byte[] result = new byte[rows.Sum(r => r.Length)];
        int at = 0;

        foreach (byte[] row in rows)
        {
            row.CopyTo(result, at);
            at += row.Length;
        }

        return result;
    }

    /// <summary>A run of <paramref name="count"/> transparent pixels.</summary>
    public static byte[] Skip(int count) => [(byte)count];

    /// <summary>
    /// A run of opaque pixels, optionally with an anti-aliased pixel on each
    /// side. <paramref name="leading"/> and <paramref name="trailing"/> are
    /// 0-15 coverage nibbles; 0 means no edge pixel.
    /// </summary>
    public static byte[] Run(int leading, byte[] colours, int trailing, byte leadingColour = 0, byte trailingColour = 0)
    {
        List<byte> bytes =
        [
            (byte)(0x80 + colours.Length),
            (byte)(((leading & 0x0F) << 4) | (trailing & 0x0F))
        ];

        if (leading != 0)
        {
            bytes.Add(leadingColour);
        }

        bytes.AddRange(colours);

        if (trailing != 0)
        {
            bytes.Add(trailingColour);
        }

        return [.. bytes];
    }
}
