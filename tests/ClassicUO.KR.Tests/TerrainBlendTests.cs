// SPDX-License-Identifier: BSD-2-Clause
//
// KR's terrain blend weights.
//

using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class TerrainBlendTests
{
    private const int T = 7;
    private const int O = 1;

    private static int[] Block3(params int[] cells) => cells;

    [Fact]
    public void TheTilesOwnTerrainWeighsOne()
    {
        int[] block = Block3(O, O, O, O, T, O, O, O, O);

        for (int q = 0; q < 4; q++)
        {
            Assert.Equal(1f, KrTerrainBlend.TexelWeight(block, T, q % 2, q / 2));
        }
    }

    [Theory]
    // top-left texel looks at NW, N, W for 0.66
    [InlineData(0, 0, 0, 0.66f)]
    [InlineData(1, 0, 0, 0.66f)]
    [InlineData(3, 0, 0, 0.66f)]
    // ...and at the S and E entries (7, 5) for 0.33
    [InlineData(7, 0, 0, 0.33f)]
    [InlineData(5, 0, 0, 0.33f)]
    // bottom-right texel: E, S, SE for 0.66; W, N for 0.33
    [InlineData(8, 1, 1, 0.66f)]
    [InlineData(3, 1, 1, 0.33f)]
    [InlineData(1, 1, 1, 0.33f)]
    // a neighbour in neither set gives nothing
    [InlineData(8, 0, 0, 0f)]
    [InlineData(0, 1, 1, 0f)]
    public void ANeighbourGivesTheDecodedWeight(int neighbour, int qx, int qy, float expected)
    {
        int[] block = Block3(O, O, O, O, O, O, O, O, O);
        block[neighbour] = T;

        Assert.Equal(expected, KrTerrainBlend.TexelWeight(block, T, qx, qy));
    }

    [Fact]
    public void TheNearSetWinsOverTheFarSet()
    {
        // NW and S are both T; top-left checks NW first, so 0.66.
        int[] block = Block3(T, O, O, O, O, O, O, T, O);

        Assert.Equal(0.66f, KrTerrainBlend.TexelWeight(block, T, 0, 0));
    }

    [Fact]
    public void AnExponentIsApplied()
    {
        int[] block = Block3(T, O, O, O, O, O, O, O, O);

        Assert.Equal(0.66f * 0.66f, KrTerrainBlend.TexelWeight(block, T, 0, 0, 2f), 4);
    }

    [Fact]
    public void AUniformBlockIsSolidEverywhere()
    {
        int[] block5 = Enumerable.Repeat(T, 25).ToArray();

        foreach (float u in (float[])[0f, 0.3f, 0.5f, 0.9f, 1f])
        {
            Assert.Equal(1f, KrTerrainBlend.Weight(block5, T, u, 0.5f), 4);
        }
    }

    [Fact]
    public void ABorderFadesRatherThanStepping()
    {
        // Left two columns are T, the rest O: T's weight across the centre
        // tile (an O tile) must fall smoothly from its left edge.
        int[] block5 = new int[25];

        for (int i = 0; i < 25; i++)
        {
            block5[i] = (i % 5) < 2 ? T : O;
        }

        float left = KrTerrainBlend.Weight(block5, T, 0.0f, 0.5f);
        float middle = KrTerrainBlend.Weight(block5, T, 0.5f, 0.5f);
        float right = KrTerrainBlend.Weight(block5, T, 1.0f, 0.5f);

        Assert.True(left > middle && middle >= right, $"{left} {middle} {right}");
        Assert.InRange(left, 0.01f, 0.99f);
    }

    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    [Fact]
    public void ExponentZeroIsAHardEdge()
    {
        // A floor (exponent 0) next to the centre tile must not reach into it
        // at all, however close the point is to the shared edge.
        int[] block5 = Enumerable.Repeat(O, 25).ToArray();
        block5[11] = T;   // the tile to the centre's left

        Assert.Equal(0f, KrTerrainBlend.Weight(block5, T, 0.01f, 0.5f, exponent: 0f));

        block5[12] = T;
        Assert.Equal(1f, KrTerrainBlend.Weight(block5, T, 0.99f, 0.5f, exponent: 0f));
    }

    [Fact]
    public void DefinitionsCarryTheExponent()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using ClassicUO.KR.KrAssetStore store = new(path);

        KrTerrainArt art = new(store);

        // grass 3, forest 1, cobble 0.5, wood floor 0 (hard).
        Assert.Equal(3f, art.Exponent(1));
        Assert.Equal(1f, art.Exponent(4));
        Assert.Equal(0.5f, art.Exponent(9));
        Assert.Equal(0f, art.Exponent(11));
    }

    [Fact]
    public void AFlatColourIsNotAPlaceholder()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using ClassicUO.KR.KrAssetStore store = new(path);

        KrTerrainArt art = new(store);

        // Terrain 52's uniform tan is the beach's base; the black stand-ins
        // are not usable.
        Assert.False(art.IsPlaceholder("02000520"));
        Assert.True(art.IsPlaceholder("02001700"));
        Assert.Equal("02000520", art.GroundArtId(52));
    }

    [Fact]
    public void ResamplingFullyTransparentPixelsKeepsTheirColour()
    {
        // Two transparent green pixels averaged into one: the result is still
        // transparent, but green, not black - black is what showed through
        // once stretched ground was forced opaque.
        uint green = 0x00106020;
        uint[] result = KrTerrainArt.Resample([green, green], 2, 1, 1, 1);

        Assert.Equal(0u, result[0] >> 24);
        Assert.Equal(green & 0xFFFFFF, result[0] & 0xFFFFFF);
    }
}
