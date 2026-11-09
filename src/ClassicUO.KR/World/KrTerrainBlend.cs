// SPDX-License-Identifier: BSD-2-Clause
//
// How KR blends one terrain into its neighbours.
//
// A 64x64 RGBA texture is built per terrain for a
// 32x32 block of tiles - two texels per tile on each axis - and the alpha
// channel is that terrain's weight. For each texel:
//
//   * the texel's own tile is the terrain                      -> 1.0
//   * otherwise, three neighbours chosen by which quarter of
//     the tile the texel is in, if any of them is the terrain  -> 0.66
//   * otherwise, two more, if either is the terrain            -> 0.33
//   * otherwise                                                -> 0
//
// then raised to a per-terrain exponent, times 255 into the alpha byte. The GPU interpolates
// between texels, which is what makes the borders soft. The colour channels
// are a hash of the terrain id - a debug tint - and carry nothing.
//
// The neighbourhood is a 3x3 block indexed 0..8; the indices each quarter
// checks:
//
//   quarter        0.66 set     0.33 set
//   top-left       0, 1, 3      7, 5
//   top-right      1, 2, 5      7, 3
//   bottom-left    3, 6, 7      5, 1
//   bottom-right   5, 7, 8      3, 1
//
// The exponent is the terrain definition's float at +0x0C; 0 means the
// terrain does not blend at all.
//
// *Hypotheses,* judged against KR screenshots: that the block is row-major
// over (y, x) with the tile at 4; that "top" means lower world y; and the
// compositing order - blending terrains in descending id, hard (exponent 0)
// terrains last, on top.

using System;

namespace ClassicUO.KR.World;

public static class KrTerrainBlend
{
    public const float Near = 0.66f;
    public const float Far = 0.33f;

    private static readonly int[][] NearSets = [[0, 1, 3], [1, 2, 5], [3, 6, 7], [5, 7, 8]];
    private static readonly int[][] FarSets = [[7, 5], [7, 3], [5, 1], [3, 1]];

    /// <summary>
    /// The weight of <paramref name="terrain"/> at one texel of a tile.
    /// </summary>
    /// <param name="block">The 3x3 terrains around the tile, row-major, tile at 4.</param>
    /// <param name="qx">Which half of the tile across: 0 left, 1 right.</param>
    /// <param name="qy">Which half of the tile down: 0 top, 1 bottom.</param>
    public static float TexelWeight(ReadOnlySpan<int> block, int terrain, int qx, int qy, float exponent = 1f)
    {
        if (block[4] == terrain)
        {
            return 1f;
        }

        int quarter = (qy * 2) + qx;
        float w = 0f;

        foreach (int k in NearSets[quarter])
        {
            if (block[k] == terrain)
            {
                w = Near;
                break;
            }
        }

        if (w == 0f)
        {
            foreach (int k in FarSets[quarter])
            {
                if (block[k] == terrain)
                {
                    w = Far;
                    break;
                }
            }
        }

        return w == 0f || exponent == 1f ? w : MathF.Pow(w, exponent);
    }

    /// <summary>
    /// The weight of <paramref name="terrain"/> at a point inside the centre
    /// tile of a 5x5 block, interpolated between texels the way the GPU does.
    /// </summary>
    /// <param name="block5">The 5x5 terrains around the tile, row-major, tile at 12.</param>
    /// <param name="u">Across the tile, 0..1.</param>
    /// <param name="v">Down the tile, 0..1.</param>
    public static float Weight(ReadOnlySpan<int> block5, int terrain, float u, float v, float exponent = 1f)
    {
        // Exponent 0 is KR's "do not blend": the terrain covers its own tiles
        // and nothing else, with a hard edge. Floors, water, brick.
        if (exponent == 0f)
        {
            return block5[12] == terrain ? 1f : 0f;
        }

        // Texel centres sit at 0.25 and 0.75 of each tile; find the four
        // around this point in texel coordinates relative to the centre
        // tile's first texel.
        float gx = (u * 2f) - 0.5f;
        float gy = (v * 2f) - 0.5f;

        int x0 = (int)MathF.Floor(gx);
        int y0 = (int)MathF.Floor(gy);
        float fx = gx - x0;
        float fy = gy - y0;

        float w00 = Texel(block5, terrain, x0, y0, exponent);
        float w10 = Texel(block5, terrain, x0 + 1, y0, exponent);
        float w01 = Texel(block5, terrain, x0, y0 + 1, exponent);
        float w11 = Texel(block5, terrain, x0 + 1, y0 + 1, exponent);

        return (w00 * (1 - fx) * (1 - fy)) + (w10 * fx * (1 - fy)) + (w01 * (1 - fx) * fy) + (w11 * fx * fy);
    }

    /// <summary>
    /// <see cref="Weight"/> for one tile and terrain, ready for every pixel:
    /// inside the centre tile the GPU-style interpolation only ever reads the
    /// 4x4 texels -1..2, so those are worked out once. Composing a blended
    /// tile called Weight for each pixel - rebuilding a 3x3 block for four
    /// texels each time - and was most of a chunk mesh's build (about 1 ms a
    /// tile, a second's stutter for each step into new ground).
    /// </summary>
    public readonly struct WeightGrid
    {
        private readonly float[]? _texels;
        private readonly float _hard;

        public WeightGrid(ReadOnlySpan<int> block5, int terrain, float exponent)
        {
            if (exponent == 0f)
            {
                _texels = null;
                _hard = block5[12] == terrain ? 1f : 0f;

                return;
            }

            _hard = 0f;
            _texels = new float[16];

            for (int ty = -1; ty <= 2; ty++)
            {
                for (int tx = -1; tx <= 2; tx++)
                {
                    _texels[((ty + 1) * 4) + tx + 1] = Texel(block5, terrain, tx, ty, exponent);
                }
            }
        }

        /// <summary>The weight at texel (tx, ty), each -1..2, the 4x4 the interpolation reads.</summary>
        public float TexelAt(int tx, int ty) => _texels is null ? _hard : _texels[((ty + 1) * 4) + tx + 1];

        /// <summary>Zero everywhere: nothing of the terrain reaches this tile.</summary>
        public bool IsEmpty
        {
            get
            {
                if (_texels is null)
                {
                    return _hard == 0f;
                }

                foreach (float t in _texels)
                {
                    if (t != 0f)
                    {
                        return false;
                    }
                }

                return true;
            }
        }

        /// <summary>The weight at (u, v) inside the tile, as <see cref="Weight"/> gives it.</summary>
        public float At(float u, float v)
        {
            if (_texels is null)
            {
                return _hard;
            }

            float gx = (u * 2f) - 0.5f;
            float gy = (v * 2f) - 0.5f;
            int x0 = Math.Clamp((int)MathF.Floor(gx), -1, 1);
            int y0 = Math.Clamp((int)MathF.Floor(gy), -1, 1);
            float fx = gx - x0;
            float fy = gy - y0;
            int i = ((y0 + 1) * 4) + x0 + 1;

            return (_texels[i] * (1 - fx) * (1 - fy)) + (_texels[i + 1] * fx * (1 - fy))
                + (_texels[i + 4] * (1 - fx) * fy) + (_texels[i + 5] * fx * fy);
        }
    }

    /// <summary>A texel's weight, by texel index relative to the centre tile (-2..3).</summary>
    private static float Texel(ReadOnlySpan<int> block5, int terrain, int tx, int ty, float exponent)
    {
        int tileX = FloorDiv(tx, 2);
        int tileY = FloorDiv(ty, 2);

        // 3x3 around that tile, out of the 5x5 around the centre one.
        Span<int> block = stackalloc int[9];

        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int bx = 2 + tileX + dx;
                int by = 2 + tileY + dy;

                block[((dy + 1) * 3) + dx + 1] =
                    bx is >= 0 and < 5 && by is >= 0 and < 5 ? block5[(by * 5) + bx] : -1;
            }
        }

        return TexelWeight(block, terrain, tx - (tileX * 2), ty - (tileY * 2), exponent);
    }

    private static int FloorDiv(int a, int b) => (int)MathF.Floor(a / (float)b);
}
