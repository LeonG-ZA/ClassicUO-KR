// SPDX-License-Identifier: BSD-2-Clause
// Minimal DDS reader for the DXT1 and DXT5 textures KR ships.
//
// Every entry in Texture.uop, LegacyTexture.uop and TerrainTexture.uop that
// has been looked at is a bare DDS with a 128-byte header, no mipmaps, and a
// DXT1 or DXT5 payload. Nothing here tries to be a general DDS reader: it
// handles what the KR packages hold and rejects the rest loudly.
//
// Header fields used (little-endian):
//   0x00 "DDS "
//   0x0c u32 height
//   0x10 u32 width
//   0x1c u32 mipmap count
//   0x54 FOURCC, "DXT1" or "DXT5"
//
// Pixels come out as straight (non-premultiplied) RGBA packed
// (A << 24) | (B << 16) | (G << 8) | R, which is what PngWriter and the
// animation decoder already use.

using System.Buffers.Binary;

namespace ClassicUO.KR.Textures;

public enum DdsFormat
{
    Dxt1,
    Dxt5
}

public sealed class DdsImage
{
    public const int HeaderSize = 128;

    private DdsImage(int width, int height, DdsFormat format, uint[] pixels)
    {
        Width = width;
        Height = height;
        Format = format;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public DdsFormat Format { get; }

    /// <summary>Width * Height pixels, (A &lt;&lt; 24) | (B &lt;&lt; 16) | (G &lt;&lt; 8) | R.</summary>
    public uint[] Pixels { get; }

    public static bool IsDds(ReadOnlySpan<byte> data) =>
        data.Length >= HeaderSize && data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ';

    /// <summary>An image from decoded pixels (tests, composites).</summary>
    public static DdsImage FromPixels(int width, int height, uint[] pixels) =>
        new(width, height, DdsFormat.Dxt5, pixels);

    public static DdsImage Decode(ReadOnlySpan<byte> data)
    {
        if (!IsDds(data))
        {
            throw new InvalidDataException("not a DDS file.");
        }

        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x0C));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(0x10));

        string fourCc = $"{(char)data[0x54]}{(char)data[0x55]}{(char)data[0x56]}{(char)data[0x57]}";

        DdsFormat format = fourCc switch
        {
            "DXT1" => DdsFormat.Dxt1,
            "DXT5" => DdsFormat.Dxt5,
            _ => throw new InvalidDataException($"unsupported DDS format '{fourCc}'.")
        };

        if (width <= 0 || height <= 0 || width > 1 << 14 || height > 1 << 14)
        {
            throw new InvalidDataException($"bad DDS size {width}x{height}.");
        }

        int blocksX = (width + 3) / 4;
        int blocksY = (height + 3) / 4;
        int blockSize = format == DdsFormat.Dxt1 ? 8 : 16;
        int needed = blocksX * blocksY * blockSize;

        ReadOnlySpan<byte> payload = data.Slice(HeaderSize);

        if (payload.Length < needed)
        {
            throw new InvalidDataException(
                $"DDS payload is {payload.Length} bytes, need {needed} for {width}x{height} {fourCc}.");
        }

        uint[] pixels = new uint[width * height];

        Span<uint> colours = stackalloc uint[4];
        Span<byte> alphas = stackalloc byte[8];

        for (int by = 0; by < blocksY; by++)
        {
            for (int bx = 0; bx < blocksX; bx++)
            {
                ReadOnlySpan<byte> block = payload.Slice(((by * blocksX) + bx) * blockSize, blockSize);

                int colourOffset = format == DdsFormat.Dxt5 ? 8 : 0;

                if (format == DdsFormat.Dxt5)
                {
                    DecodeAlpha(block, alphas);
                }

                ReadOnlySpan<byte> c = block.Slice(colourOffset);

                ushort c0 = BinaryPrimitives.ReadUInt16LittleEndian(c);
                ushort c1 = BinaryPrimitives.ReadUInt16LittleEndian(c.Slice(2));
                uint bits = BinaryPrimitives.ReadUInt32LittleEndian(c.Slice(4));

                // DXT5 always uses the four-colour mode; DXT1 switches to a
                // three-colour mode with a transparent slot when c0 <= c1.
                bool fourColour = format == DdsFormat.Dxt5 || c0 > c1;

                colours[0] = FromRgb565(c0);
                colours[1] = FromRgb565(c1);

                if (fourColour)
                {
                    // Index 2 is (2*c0 + c1)/3 and index 3 is (c0 + 2*c1)/3,
                    // in that order - getting them the wrong way round still
                    // produces a plausible image, so it is worth being exact.
                    colours[2] = Lerp(colours[0], colours[1], 2, 1);
                    colours[3] = Lerp(colours[0], colours[1], 1, 2);
                }
                else
                {
                    colours[2] = Lerp(colours[0], colours[1], 1, 1);
                    colours[3] = 0;
                }

                for (int py = 0; py < 4; py++)
                {
                    int y = (by * 4) + py;

                    if (y >= height)
                    {
                        break;
                    }

                    for (int px = 0; px < 4; px++)
                    {
                        int x = (bx * 4) + px;

                        if (x >= width)
                        {
                            break;
                        }

                        int index = (int)((bits >> (((py * 4) + px) * 2)) & 3);
                        uint rgba = colours[index];

                        if (format == DdsFormat.Dxt5)
                        {
                            int a = AlphaIndex(block, (py * 4) + px);
                            rgba = (rgba & 0x00FFFFFF) | ((uint)alphas[a] << 24);
                        }

                        pixels[(y * width) + x] = rgba;
                    }
                }
            }
        }

        return new DdsImage(width, height, format, pixels);
    }

    private static void DecodeAlpha(ReadOnlySpan<byte> block, Span<byte> alphas)
    {
        byte a0 = block[0];
        byte a1 = block[1];

        alphas[0] = a0;
        alphas[1] = a1;

        if (a0 > a1)
        {
            for (int i = 1; i <= 6; i++)
            {
                alphas[i + 1] = (byte)((((7 - i) * a0) + (i * a1)) / 7);
            }
        }
        else
        {
            for (int i = 1; i <= 4; i++)
            {
                alphas[i + 1] = (byte)((((5 - i) * a0) + (i * a1)) / 5);
            }

            alphas[6] = 0;
            alphas[7] = 255;
        }
    }

    private static int AlphaIndex(ReadOnlySpan<byte> block, int pixel)
    {
        // Six bytes of three-bit indexes, little-endian, starting at byte 2.
        int bit = pixel * 3;
        int offset = 2 + (bit >> 3);

        int value = block[offset] | (block[offset + 1] << 8);

        return (value >> (bit & 7)) & 7;
    }

    private static uint FromRgb565(ushort value)
    {
        int r = (value >> 11) & 0x1F;
        int g = (value >> 5) & 0x3F;
        int b = value & 0x1F;

        r = (r << 3) | (r >> 2);
        g = (g << 2) | (g >> 4);
        b = (b << 3) | (b >> 2);

        return 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | (uint)r;
    }

    private static uint Lerp(uint a, uint b, int wa, int wb)
    {
        int total = wa + wb;

        int r = ((int)(a & 0xFF) * wa + (int)(b & 0xFF) * wb) / total;
        int g = ((int)((a >> 8) & 0xFF) * wa + (int)((b >> 8) & 0xFF) * wb) / total;
        int bl = ((int)((a >> 16) & 0xFF) * wa + (int)((b >> 16) & 0xFF) * wb) / total;

        return 0xFF000000u | ((uint)bl << 16) | ((uint)g << 8) | (uint)r;
    }
}
