// SPDX-License-Identifier: BSD-2-Clause
//
// Fitting a KR static to the 2D sprite's footprint.
//
// Anything built out of repeated tiles - a roof, a floor - needs this.
// ClassicUO anchors each sprite from its own width and height, so a sprite
// even slightly off its 2D footprint staggers against its neighbours. In game
// that showed up as roof tiles sliding into the building beneath them.

using ClassicUO.KR;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class StaticFitTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    [Fact]
    public void FittingGivesExactlyTheRequestedFootprint()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        foreach (int graphic in (int[])[2, 53, 3278])
        {
            KrStaticSprite? sprite = art.Sprite(graphic, 44, 44);

            if (sprite is null)
            {
                continue;
            }

            // Exactly, not approximately: a roof tile one pixel out still
            // staggers.
            Assert.Equal(44, sprite.Width);
            Assert.Equal(44, sprite.Height);
            Assert.Equal(44 * 44, sprite.Pixels.Length);
        }
    }

    [Fact]
    public void FittingIsIndependentOfTheProvidersOwnScale()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        // A fitted sprite must come out the same size whatever the scale is
        // set to, or the setting would quietly break tiling surfaces again.
        KrStaticSprite? a = new KrStaticArt(store, 0.25).Sprite(3278, 40, 60);
        KrStaticSprite? b = new KrStaticArt(store, 4.0).Sprite(3278, 40, 60);

        if (a is null || b is null)
        {
            return;
        }

        Assert.Equal(40, a.Width);
        Assert.Equal(60, a.Height);
        Assert.Equal(a.Width, b.Width);
        Assert.Equal(a.Height, b.Height);
    }

    [Fact]
    public void AskingForNoFitFallsBackToTheScale()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store, 1.0);

        KrStaticSprite? unfitted = art.Sprite(3278);
        KrStaticSprite? fitted = art.Sprite(3278, 40, 60);

        if (unfitted is null || fitted is null)
        {
            return;
        }

        Assert.NotEqual(40, unfitted.Width);
        Assert.Equal(40, fitted.Width);
    }

    [Fact]
    public void FittedAndUnfittedSpritesAreCachedApart()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? fitted = art.Sprite(3278, 44, 44);
        KrStaticSprite? other = art.Sprite(3278, 80, 80);

        if (fitted is null || other is null)
        {
            return;
        }

        // One cache entry per footprint, or the first size asked for would be
        // handed to every caller after it.
        Assert.NotSame(fitted, other);
        Assert.Equal(44, fitted.Width);
        Assert.Equal(80, other.Width);
        Assert.Same(fitted, art.Sprite(3278, 44, 44));
    }
}
