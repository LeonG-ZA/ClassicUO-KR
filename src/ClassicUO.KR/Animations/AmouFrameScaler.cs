// SPDX-License-Identifier: BSD-2-Clause
//
// Resamples a decoded KR animation frame onto the 2D client's scale.
//
// NOT in the renderer's path any more. Frames now go into the texture atlas at
// KR's native resolution and are scaled when they are drawn, so that the
// camera's zoom and the KR scale compose into one sampling step - see
// SpriteInfo. What is still live here is
// TileScale, the one constant the whole thing is built on, plus the area
// filter, kept because it is the reference a future filtered draw path has to
// match (the world batch samples with PointClamp today).
//
// KR drew its mobiles larger than the 2D art, and not by a constant: across
// the 179 bodies that could be compared against their 2D counterparts the
// height ratio runs 1.22-2.62, falls as the creature gets bigger, and does not
// preserve each body's aspect ratio. The only
// ratio anchored to anything real is the human body - 400 is 61x96 against
// 40x64 and 401 is 57x94 against 40x63, both 1.5 - so that is the number used
// here. It puts a KR human at the scale of the 44x44 tile grid it stands on
// and leaves KR's own creature proportions alone, which is art direction
// rather than a defect to undo.
//
// The anchor is the delicate part. A frame's bounds are stored relative to the
// mobile's feet, so scaling the *bounds* and deriving the size from them keeps
// both edges within half a pixel of the true scaled position; scaling the size
// and the anchor offset independently would let the feet drift off the tile.
// See ScaleBound and the Scale overload below.

namespace ClassicUO.KR.Animations;

public static class AmouFrameScaler
{
    /// <summary>
    /// Factor KR animation frames are resampled by on their way into the
    /// renderer: the reciprocal of the measured KR-to-2D human ratio of 1.5.
    /// </summary>
    /// <remarks>
    /// The world is still 2D art - terrain, statics and gumps are all drawn
    /// for the 44x44 tile grid - so the mobiles have to meet that grid. If KR
    /// terrain ever lands and the whole renderer moves to KR's scale, this is
    /// the one line to change: set it to 1.0 to pass KR art through untouched.
    /// </remarks>
    public const double TileScale = 2.0 / 3.0;

    /// <summary>
    /// Resamples a frame to <see cref="TileScale"/>. Returns the frame
    /// unchanged when the scale is 1.
    /// </summary>
    public static AmouFrameBitmap Scale(AmouFrameBitmap source) => Scale(source, TileScale);

    /// <summary>
    /// Resamples a frame by <paramref name="scale"/> with an area (box) filter
    /// over each destination pixel's source footprint, scaling the frame's
    /// bounds so the mobile's anchor point stays where it was.
    /// </summary>
    public static AmouFrameBitmap Scale(AmouFrameBitmap source, double scale)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (scale <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "scale must be positive.");
        }

        if (scale == 1.0 || source.Width <= 0 || source.Height <= 0)
        {
            return source;
        }

        // Bounds relative to the anchor, as the frame table stores them.
        int x1 = -source.CenterX;
        int y1 = -source.CenterY;
        int x2 = x1 + source.Width;
        int y2 = y1 + source.Height;

        int sx1 = ScaleBound(x1, scale);
        int sy1 = ScaleBound(y1, scale);
        int sx2 = ScaleBound(x2, scale);
        int sy2 = ScaleBound(y2, scale);

        // A frame thinner than one destination pixel still has to exist; push
        // the far edge out rather than the anchor-bearing near edge.
        if (sx2 <= sx1)
        {
            sx2 = sx1 + 1;
        }

        if (sy2 <= sy1)
        {
            sy2 = sy1 + 1;
        }

        int width = sx2 - sx1;
        int height = sy2 - sy1;

        uint[] pixels = Resample(source, width, height);

        return new AmouFrameBitmap(width, height, -sx1, -sy1, pixels);
    }

    /// <summary>
    /// Rounds a scaled bound to the destination pixel grid, half up. Monotonic
    /// across the sign change, so a frame's bounds can never cross over.
    /// </summary>
    private static int ScaleBound(int bound, double scale) => (int)Math.Floor(bound * scale + 0.5);

    private static uint[] Resample(AmouFrameBitmap source, int width, int height)
    {
        (int First, double[] Weights)[] columns = BuildWeights(source.Width, width);
        (int First, double[] Weights)[] rows = BuildWeights(source.Height, height);

        uint[] src = source.Pixels;
        uint[] dst = new uint[width * height];

        for (int dy = 0; dy < height; dy++)
        {
            (int firstRow, double[] rowWeights) = rows[dy];

            for (int dx = 0; dx < width; dx++)
            {
                (int firstColumn, double[] columnWeights) = columns[dx];

                double coverage = 0.0;
                double alphaSum = 0.0;
                double r = 0.0;
                double g = 0.0;
                double b = 0.0;

                for (int j = 0; j < rowWeights.Length; j++)
                {
                    int rowOffset = (firstRow + j) * source.Width;
                    double wy = rowWeights[j];

                    for (int i = 0; i < columnWeights.Length; i++)
                    {
                        double area = wy * columnWeights[i];

                        if (area <= 0.0)
                        {
                            continue;
                        }

                        uint pixel = src[rowOffset + firstColumn + i];

                        coverage += area;

                        // Weight colour by alpha so that the transparent
                        // pixels KR leaves beside an anti-aliased edge - which
                        // carry no useful colour - cannot drag the edge
                        // towards black.
                        double alpha = area * ((pixel >> 24) & 0xFF);

                        if (alpha <= 0.0)
                        {
                            continue;
                        }

                        alphaSum += alpha;
                        r += alpha * (pixel & 0xFF);
                        g += alpha * ((pixel >> 8) & 0xFF);
                        b += alpha * ((pixel >> 16) & 0xFF);
                    }
                }

                // Fully transparent stays fully transparent, colour channels
                // included: PixelPicker's hit mask treats a pixel as
                // transparent only when the whole word is zero, so a pixel
                // with alpha 0 and a leftover colour would still be clickable.
                if (alphaSum <= 0.0 || coverage <= 0.0)
                {
                    continue;
                }

                uint a = ToByte(alphaSum / coverage);

                dst[dy * width + dx] =
                    (a << 24) | (ToByte(b / alphaSum) << 16) | (ToByte(g / alphaSum) << 8) | ToByte(r / alphaSum);
            }
        }

        return dst;
    }

    /// <summary>
    /// For each destination pixel along one axis, the first source pixel it
    /// overlaps and that pixel run's coverage weights.
    /// </summary>
    /// <remarks>
    /// The footprint is mapped proportionally across the frame, so the
    /// destination pixel grid spans exactly the source pixel grid:
    /// <c>d</c> covers <c>[d * srcLength / dstLength, (d + 1) * srcLength / dstLength]</c>.
    /// Rounding the bounds to whole pixels has already moved the frame box by
    /// up to half a pixel, and taking the footprint from the rounded box's
    /// absolute coordinates instead would push that half pixel of the
    /// silhouette off the edge of the frame. The cost is that the effective
    /// factor is <c>dstLength / srcLength</c> rather than the exact scale -
    /// 41/61 against 2/3 for a human body, under one percent.
    /// </remarks>
    private static (int First, double[] Weights)[] BuildWeights(int srcLength, int dstLength)
    {
        (int First, double[] Weights)[] result = new (int, double[])[dstLength];

        for (int d = 0; d < dstLength; d++)
        {
            double u0 = (double)d * srcLength / dstLength;
            double u1 = (double)(d + 1) * srcLength / dstLength;

            int first = Math.Clamp((int)Math.Floor(u0), 0, srcLength - 1);
            int last = Math.Clamp((int)Math.Ceiling(u1) - 1, first, srcLength - 1);
            double[] weights = new double[last - first + 1];

            for (int i = 0; i < weights.Length; i++)
            {
                int s = first + i;
                weights[i] = Math.Min(s + 1, u1) - Math.Max(s, u0);
            }

            result[d] = (first, weights);
        }

        return result;
    }

    private static uint ToByte(double value) => (uint)Math.Clamp((int)(value + 0.5), 0, 255);
}
