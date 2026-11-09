// SPDX-License-Identifier: BSD-2-Clause
//
// Statics KR draws as a pair on one tile.
//
// KR builds a tree from a trunk and a separate crown of leaves; the 2D map
// holds only one of the two, so drawing KR's art for that id alone gives half
// a tree.

using ClassicUO.KR;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class StaticPairTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    [Fact]
    public void FindsTheTrunkAndLeavesOfATree()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        KrStaticPairs pairs = KrStaticPairs.Build(path, [0]);

        // Measured over facet 0: 3286 red_cedar_TRUNK is on 14,863 tiles and
        // 3287 red_cedar_LEAVES is on every one of them.
        Assert.True(pairs.TryGet(3286, out ushort leaves));
        Assert.Equal(3287, leaves);

        Assert.True(pairs.TryGet(3287, out ushort trunk));
        Assert.Equal(3286, trunk);

        Assert.True(pairs.TryGet(3296, out ushort walnut));
        Assert.Equal(3297, walnut);
    }

    [Fact]
    public void PairingIsTheExceptionNotTheRule()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        KrStaticPairs pairs = KrStaticPairs.Build(path, [0]);

        // If this ever ran into the thousands the threshold would be matching
        // things that merely share crowded tiles, and statics would start
        // acquiring pieces of their neighbours.
        Assert.True(pairs.Paired > 0);
        Assert.True(pairs.Paired < 500, $"{pairs.Paired} art ids were paired, which is too many");
        Assert.True(pairs.Placements > 100_000);
    }

    [Fact]
    public void AWallHasNoCompanion()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        KrStaticPairs pairs = KrStaticPairs.Build(path, [0]);

        // 53 is a grey brick wall: common, and drawn on its own.
        Assert.False(pairs.TryGet(53, out _));
    }

    [Fact]
    public void OutOfRangeGraphicsHaveNoCompanion()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        KrStaticPairs pairs = KrStaticPairs.Build(path, [0]);

        Assert.False(pairs.TryGet(-1, out _));
        Assert.False(pairs.TryGet(KrStaticPairs.GraphicCount, out _));
    }

    [Fact]
    public void MergingTheCompanionMakesABiggerSpriteThanEitherHalf()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store, 1.0);

        KrStaticSprite? trunk = art.SpriteWith(3286, -1, 0, 0);
        KrStaticSprite? merged = art.SpriteWith(3286, 3287, 0, 0);

        if (trunk is null || merged is null)
        {
            return;
        }

        // The crown is wider than the trunk, so the merged sprite has to grow
        // sideways; if it did not, the companion was not drawn.
        Assert.True(merged.Width >= trunk.Width);
        Assert.True(merged.Height >= trunk.Height);
        Assert.Equal(merged.Width * merged.Height, merged.Pixels.Length);

        int trunkVisible = trunk.Pixels.Count(p => (p >> 24) > 8);
        int mergedVisible = merged.Pixels.Count(p => (p >> 24) > 8);

        Assert.True(
            mergedVisible > trunkVisible,
            $"merged has {mergedVisible} visible pixels, trunk alone has {trunkVisible}");
    }

    [Fact]
    public void MergingStillHonoursTheRequestedFootprint()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? merged = art.SpriteWith(3286, 3287, 60, 90);

        if (merged is null)
        {
            return;
        }

        Assert.Equal(60, merged.Width);
        Assert.Equal(90, merged.Height);
    }
}
