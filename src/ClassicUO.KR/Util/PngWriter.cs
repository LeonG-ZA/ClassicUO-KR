// SPDX-License-Identifier: BSD-2-Clause
// Minimal RGBA8 PNG encoder, so the export tool needs no image library.

using System.Buffers.Binary;
using System.IO.Compression;

namespace ClassicUO.KR.Util;

public static class PngWriter
{
    private static readonly uint[] CrcTable = BuildCrcTable();

    /// <summary>Writes straight (non-premultiplied) RGBA pixels packed as (A &lt;&lt; 24) | (B &lt;&lt; 16) | (G &lt;&lt; 8) | R.</summary>
    public static void Write(string path, int width, int height, ReadOnlySpan<uint> pixels)
    {
        using FileStream stream = File.Create(path);
        Write(stream, width, height, pixels);
    }

    public static void Write(Stream stream, int width, int height, ReadOnlySpan<uint> pixels)
    {
        if (width <= 0 || height <= 0)
        {
            throw new ArgumentException($"bad image size {width}x{height}.");
        }

        if (pixels.Length < width * height)
        {
            throw new ArgumentException($"need {width * height} pixels, got {pixels.Length}.");
        }

        stream.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);

        byte[] ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // colour type: truecolour with alpha
        ihdr[10] = 0; // deflate
        ihdr[11] = 0; // adaptive filtering
        ihdr[12] = 0; // no interlace

        WriteChunk(stream, "IHDR", ihdr);

        // Raw scanlines: one filter byte (0 = none) then RGBA per pixel.
        byte[] raw = new byte[height * ((width * 4) + 1)];
        int w = 0;

        for (int y = 0; y < height; y++)
        {
            raw[w++] = 0;

            for (int x = 0; x < width; x++)
            {
                uint p = pixels[(y * width) + x];

                raw[w++] = (byte)p;         // R
                raw[w++] = (byte)(p >> 8);  // G
                raw[w++] = (byte)(p >> 16); // B
                raw[w++] = (byte)(p >> 24); // A
            }
        }

        using MemoryStream compressed = new();

        using (ZLibStream zlib = new(compressed, CompressionLevel.Optimal, leaveOpen: true))
        {
            zlib.Write(raw);
        }

        WriteChunk(stream, "IDAT", compressed.GetBuffer().AsSpan(0, (int)compressed.Length));
        WriteChunk(stream, "IEND", []);
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);

        Span<byte> name = stackalloc byte[4];

        for (int i = 0; i < 4; i++)
        {
            name[i] = (byte)type[i];
        }

        stream.Write(name);
        stream.Write(data);

        uint crc = Crc(0xFFFFFFFF, name);
        crc = Crc(crc, data) ^ 0xFFFFFFFF;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint Crc(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return crc;
    }

    private static uint[] BuildCrcTable()
    {
        uint[] table = new uint[256];

        for (uint n = 0; n < 256; n++)
        {
            uint c = n;

            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }
}
