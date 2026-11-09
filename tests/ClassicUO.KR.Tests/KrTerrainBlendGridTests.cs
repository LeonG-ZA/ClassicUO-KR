// SPDX-License-Identifier: BSD-2-Clause

using System;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class KrTerrainBlendGridTests
{
    [Fact]
    public void GridMatchesWeightEverywhereInTheTile()
    {
        Random rng = new(7);

        for (int trial = 0; trial < 200; trial++)
        {
            int[] block5 = new int[25];

            for (int i = 0; i < 25; i++)
            {
                block5[i] = rng.Next(3);
            }

            int terrain = rng.Next(3);
            float exponent = trial % 5 == 0 ? 0f : (float)(0.5 + rng.NextDouble() * 2.5);
            KrTerrainBlend.WeightGrid grid = new(block5, terrain, exponent);

            for (int k = 0; k < 40; k++)
            {
                float u = (float)rng.NextDouble();
                float v = (float)rng.NextDouble();

                Assert.Equal(KrTerrainBlend.Weight(block5, terrain, u, v, exponent), grid.At(u, v), 5);
            }
        }
    }
}
