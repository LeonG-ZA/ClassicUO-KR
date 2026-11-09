// SPDX-License-Identifier: BSD-2-Clause
//
// Placing KR art where the 2D sprite's own art sits inside its box.
//
// Fitting to the 2D sprite's full size is not enough, because a 2D sprite
// usually carries transparent margin. The fallen log, graphic 3319, is 44x40
// but its art is only 32x30 at (6, 1) - nine empty rows below it. KR art is
// trimmed to its own bounds, so stretching it across the full box dropped the
// object nine pixels into the ground.

using ClassicUO.KR;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class StaticBoxTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    [Fact]
    public void TheSpriteFillsTheBoxItWasGiven()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        // The log's own numbers: a 44x40 box with the art at (6, 1), 32x30.
        KrStaticSprite? sprite = art.SpriteInBox(3319, -1, 44, 40, 6, 1, 32, 30);

        if (sprite is null)
        {
            return;
        }

        Assert.Equal(44, sprite.Width);
        Assert.Equal(40, sprite.Height);
        Assert.Equal(44 * 40, sprite.Pixels.Length);
    }

    [Fact]
    public void NothingIsDrawnInTheMargin()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? sprite = art.SpriteInBox(3319, -1, 44, 40, 6, 1, 32, 30);

        if (sprite is null)
        {
            return;
        }

        // The nine rows below the art are what the object used to be pushed
        // into. They have to stay empty, or the baseline is wrong again.
        for (int y = 31; y < 40; y++)
        {
            for (int x = 0; x < 44; x++)
            {
                Assert.Equal(0u, sprite.Pixels[(y * 44) + x] >> 24);
            }
        }

        // And so do the left and right margins.
        for (int y = 0; y < 40; y++)
        {
            Assert.Equal(0u, sprite.Pixels[(y * 44) + 0] >> 24);
            Assert.Equal(0u, sprite.Pixels[(y * 44) + 43] >> 24);
        }
    }

    [Fact]
    public void TheArtLandsInsideTheGivenBounds()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? sprite = art.SpriteInBox(3319, -1, 44, 40, 6, 1, 32, 30);

        if (sprite is null)
        {
            return;
        }

        Assert.True(
            KrStaticArt.TryGetBounds(sprite.Pixels, 44, 40, out int x0, out int y0, out int w, out int h),
            "the boxed sprite is empty");

        Assert.True(x0 >= 6, $"art starts at x={x0}, left of the 2D art's own bounds");
        Assert.True(y0 >= 1, $"art starts at y={y0}, above the 2D art's own bounds");
        Assert.True(x0 + w <= 6 + 32, "art runs past the right of the 2D art's bounds");
        Assert.True(y0 + h <= 1 + 30, "art runs past the bottom of the 2D art's bounds");
    }

    [Fact]
    public void BoxingMovesTheArtUpComparedWithFillingTheWholeBox()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? stretched = art.Sprite(3319, 44, 40);
        KrStaticSprite? boxed = art.SpriteInBox(3319, -1, 44, 40, 6, 1, 32, 30);

        if (stretched is null || boxed is null)
        {
            return;
        }

        KrStaticArt.TryGetBounds(stretched.Pixels, 44, 40, out _, out _, out _, out int stretchedHeight);
        KrStaticArt.TryGetBounds(boxed.Pixels, 44, 40, out _, out _, out _, out int boxedHeight);

        // The whole point: the old behaviour reached further down the box.
        Assert.True(
            boxedHeight < stretchedHeight,
            $"boxed art is {boxedHeight} tall, stretched was {stretchedHeight}");
    }

    [Fact]
    public void ADegenerateBoxFallsBackToPlainFitting()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? zero = art.SpriteInBox(3319, -1, 44, 40, 0, 0, 0, 0);
        KrStaticSprite? plain = art.Sprite(3319, 44, 40);

        if (plain is null)
        {
            return;
        }

        Assert.NotNull(zero);
        Assert.Equal(plain.Width, zero!.Width);
        Assert.Equal(plain.Height, zero.Height);
    }
}
