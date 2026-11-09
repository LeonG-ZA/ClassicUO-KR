// SPDX-License-Identifier: BSD-2-Clause
// RLE decoder for AMOU frame pixel data.
//
// Rows are decoded top to bottom; within a row, tokens are read until `width`
// pixels have been produced:
//
//   c = next byte
//   if c < 0x80:            skip c transparent pixels
//   else:
//       n = c - 0x80        run of opaque pixels
//       f = next byte       edge flags
//       if (f & 0xF0) != 0: one leading pixel  = palette[next], alpha = (f >> 4) * 17
//       repeat n:           one opaque pixel   = palette[next], alpha = 255
//       if (f & 0x0F) != 0: one trailing pixel = palette[next], alpha = (f & 0xF) * 17
//   when x reaches width: next row (x -= width)
//
// The leading/trailing nibbles are the anti-aliased edge coverage of the run.

namespace ClassicUO.KR.Animations;

public static class AmouDecoder
{
    /// <summary>
    /// Decodes one frame into straight (non-premultiplied) RGBA8888.
    /// </summary>
    /// <param name="data">The inflated AMOU file.</param>
    /// <param name="info">Frame table entry, with an absolute <see cref="AmouFrameInfo.PixelOffset"/>.</param>
    /// <param name="palette">Palette entries packed as (B &lt;&lt; 16) | (G &lt;&lt; 8) | R.</param>
    public static AmouFrameBitmap Decode(ReadOnlySpan<byte> data, in AmouFrameInfo info, ReadOnlySpan<uint> palette)
    {
        int width = info.Width;
        int height = info.Height;

        if (width <= 0 || height <= 0)
        {
            return new AmouFrameBitmap(Math.Max(width, 0), Math.Max(height, 0), info.CenterX, info.CenterY, []);
        }

        uint[] pixels = new uint[width * height];

        if (info.PixelOffset < 0 || info.PixelOffset >= data.Length)
        {
            throw new InvalidDataException($"AMOU frame pixel offset {info.PixelOffset} is outside the file ({data.Length} bytes).");
        }

        int q = info.PixelOffset;
        int x = 0;
        int y = 0;

        // The writes below use a flat index, so a run that overshoots the row
        // carries into the next one — matching the `x -= width` row wrap.
        while (y < height)
        {
            while (x < width)
            {
                if (q >= data.Length)
                {
                    throw new InvalidDataException($"AMOU pixel stream ran out at row {y} of {height}.");
                }

                byte c = data[q++];

                if (c < 0x80)
                {
                    // A run of transparent pixels; the buffer is already zeroed.
                    x += c;

                    continue;
                }

                int n = c - 0x80;

                if (q >= data.Length)
                {
                    throw new InvalidDataException($"AMOU pixel stream ran out at row {y} of {height}.");
                }

                byte f = data[q++];

                if ((f & 0xF0) != 0)
                {
                    Put(pixels, (y * width) + x, palette, data[q++], (uint)((f >> 4) * 17));
                    x++;
                }

                for (int i = 0; i < n; i++)
                {
                    Put(pixels, (y * width) + x, palette, data[q++], 255);
                    x++;
                }

                if ((f & 0x0F) != 0)
                {
                    Put(pixels, (y * width) + x, palette, data[q++], (uint)((f & 0x0F) * 17));
                    x++;
                }
            }

            x -= width;
            y++;
        }

        return new AmouFrameBitmap(width, height, info.CenterX, info.CenterY, pixels);
    }

    private static void Put(uint[] pixels, int index, ReadOnlySpan<uint> palette, byte colour, uint alpha)
    {
        if ((uint)index >= (uint)pixels.Length || colour >= palette.Length)
        {
            return;
        }

        uint bgr = palette[colour];

        // (A << 24) | (B << 16) | (G << 8) | R
        pixels[index] = (alpha << 24) | bgr;
    }
}
