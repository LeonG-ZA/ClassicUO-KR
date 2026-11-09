// SPDX-License-Identifier: BSD-2-Clause
//
// The TGA textures KR's effects use (EffectTexture.uop, data/effects/*.tga).
// The 237 in the install are of four kinds, all stored bottom-up:
//
//   type 2  (true colour)  32 bpp  185     type 10 (RLE)  32 bpp    6
//   type 2  (true colour)  24 bpp   43     type 3  (grey)  8 bpp    3
//
// Pixels come out top-down as 0xAABBGGRR (byte order R, G, B, A), the layout
// SurfaceFormat.Color and DdsImage.Pixels use. A grey image is read as white
// with the grey as alpha - they are masks.

using System;
using System.Buffers.Binary;

namespace ClassicUO.KR.Effects;

public sealed class TgaImage
{
    /// <summary>An image made from pixels (R | G &lt;&lt; 8 | B &lt;&lt; 16 | A &lt;&lt; 24).</summary>
    public static TgaImage FromPixels(int width, int height, uint[] pixels) => new(width, height, pixels);

    private TgaImage(int width, int height, uint[] pixels)
    {
        Width = width;
        Height = height;
        Pixels = pixels;
    }

    public int Width { get; }

    public int Height { get; }

    public uint[] Pixels { get; }

    public static TgaImage? TryDecode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 18)
        {
            return null;
        }

        int idLength = data[0];
        int colourMapType = data[1];
        int type = data[2];
        int width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..]);
        int height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..]);
        int bpp = data[16];
        bool topDown = (data[17] & 0x20) != 0;

        if (colourMapType != 0 || width == 0 || height == 0 || type is not (2 or 3 or 10))
        {
            return null;
        }

        int bytes = bpp / 8;

        if (bytes is not (1 or 3 or 4) || (type == 3) != (bytes == 1))
        {
            return null;
        }

        uint[] pixels = new uint[width * height];
        int src = 18 + idLength;
        int count = width * height;
        int n = 0;

        uint Read(ReadOnlySpan<byte> d, int o) => bytes switch
        {
            1 => 0x00FFFFFFu | ((uint)d[o] << 24),
            3 => d[o + 2] | ((uint)d[o + 1] << 8) | ((uint)d[o] << 16) | 0xFF000000u,
            _ => d[o + 2] | ((uint)d[o + 1] << 8) | ((uint)d[o] << 16) | ((uint)d[o + 3] << 24),
        };

        if (type == 10)
        {
            while (n < count && src < data.Length)
            {
                int header = data[src++];
                int run = (header & 0x7F) + 1;

                if ((header & 0x80) != 0)
                {
                    if (src + bytes > data.Length)
                    {
                        return null;
                    }

                    uint p = Read(data, src);
                    src += bytes;

                    for (int i = 0; i < run && n < count; i++)
                    {
                        pixels[n++] = p;
                    }
                }
                else
                {
                    for (int i = 0; i < run && n < count; i++)
                    {
                        if (src + bytes > data.Length)
                        {
                            return null;
                        }

                        pixels[n++] = Read(data, src);
                        src += bytes;
                    }
                }
            }
        }
        else
        {
            if (src + (count * bytes) > data.Length)
            {
                return null;
            }

            for (; n < count; n++, src += bytes)
            {
                pixels[n] = Read(data, src);
            }
        }

        if (n < count)
        {
            return null;
        }

        if (!topDown)
        {
            for (int y = 0; y < height / 2; y++)
            {
                Span<uint> a = pixels.AsSpan(y * width, width);
                Span<uint> b = pixels.AsSpan((height - 1 - y) * width, width);

                for (int x = 0; x < width; x++)
                {
                    (a[x], b[x]) = (b[x], a[x]);
                }
            }
        }

        return new TgaImage(width, height, pixels);
    }
}
