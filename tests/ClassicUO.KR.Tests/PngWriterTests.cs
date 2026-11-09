// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers.Binary;
using ClassicUO.KR.Util;
using Xunit;

namespace ClassicUO.KR.Tests;

public class PngWriterTests
{
    [Fact]
    public void WritesAReadablePngHeader()
    {
        uint[] pixels = new uint[4 * 2];
        Array.Fill(pixels, 0xFF112233u);

        using MemoryStream stream = new();
        PngWriter.Write(stream, 4, 2, pixels);

        byte[] png = stream.ToArray();

        Assert.Equal<byte>([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);

        // IHDR: length 13, then the chunk name, width, height, depth, colour type.
        Assert.Equal(13, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(8)));
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);

        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public void RejectsBadArguments()
    {
        using MemoryStream stream = new();

        Assert.Throws<ArgumentException>(() => PngWriter.Write(stream, 0, 1, new uint[1]));
        Assert.Throws<ArgumentException>(() => PngWriter.Write(stream, 4, 4, new uint[4]));
    }
}
