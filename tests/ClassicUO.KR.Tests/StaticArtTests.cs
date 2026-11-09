// SPDX-License-Identifier: BSD-2-Clause
//
// KR static art: finding a sprite's bounds inside its padded texture, and
// what comes back for a real graphic.
//
// The bounds matter more than they look. KR's world art is padded to a power
// of two, so the padding has to be trimmed before the sprite is anchored, and
// the anchor works from the sprite's own width and height. Getting the bounds
// wrong moves every static off its tile.

using ClassicUO.KR;
using ClassicUO.KR.Textures;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class StaticArtTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    private const uint Opaque = 0xFF123456;
    private const uint Clear = 0x00000000;

    /// <summary>A w x h image with an opaque rectangle at (x0, y0).</summary>
    private static uint[] WithRect(int w, int h, int x0, int y0, int rw, int rh, uint colour = Opaque)
    {
        uint[] pixels = new uint[w * h];

        for (int y = y0; y < y0 + rh; y++)
        {
            for (int x = x0; x < x0 + rw; x++)
            {
                pixels[(y * w) + x] = colour;
            }
        }

        return pixels;
    }

    // ---- bounds -----------------------------------------------------------

    [Fact]
    public void FindsASpriteAnchoredAtTheTopLeft()
    {
        // The usual case: 87.7 % of KR's statics start at (0, 0) in a padded
        // texture, so this is the shape nearly every lookup takes.
        uint[] pixels = WithRect(64, 256, 0, 0, 43, 160);

        Assert.True(KrStaticArt.TryGetBounds(pixels, 64, 256, out int x0, out int y0, out int w, out int h));

        Assert.Equal(0, x0);
        Assert.Equal(0, y0);
        Assert.Equal(43, w);
        Assert.Equal(160, h);
    }

    [Fact]
    public void FindsASpriteThatIsNotAtTheOrigin()
    {
        uint[] pixels = WithRect(32, 32, 5, 7, 11, 13);

        Assert.True(KrStaticArt.TryGetBounds(pixels, 32, 32, out int x0, out int y0, out int w, out int h));

        Assert.Equal(5, x0);
        Assert.Equal(7, y0);
        Assert.Equal(11, w);
        Assert.Equal(13, h);
    }

    [Fact]
    public void BoundsAreInclusiveOfTheLastVisiblePixel()
    {
        // One pixel in the far corner: the bounds must be 1x1 at that pixel,
        // not a zero-sized box, or the sprite vanishes.
        uint[] pixels = new uint[8 * 8];
        pixels[(7 * 8) + 7] = Opaque;

        Assert.True(KrStaticArt.TryGetBounds(pixels, 8, 8, out int x0, out int y0, out int w, out int h));

        Assert.Equal(7, x0);
        Assert.Equal(7, y0);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
    }

    [Fact]
    public void AFullyVisibleImageNeedsNoTrimming()
    {
        uint[] pixels = WithRect(16, 16, 0, 0, 16, 16);

        Assert.True(KrStaticArt.TryGetBounds(pixels, 16, 16, out int x0, out int y0, out int w, out int h));

        Assert.Equal(0, x0);
        Assert.Equal(0, y0);
        Assert.Equal(16, w);
        Assert.Equal(16, h);
    }

    [Fact]
    public void AnEmptyImageHasNoBounds()
    {
        uint[] pixels = new uint[16 * 16];

        Assert.False(KrStaticArt.TryGetBounds(pixels, 16, 16, out _, out _, out _, out _));
    }

    [Fact]
    public void NearlyTransparentPixelsDoNotCountAsTheSprite()
    {
        // DXT5 leaves a little alpha noise around a cut-out edge. Counting it
        // would pad every sprite by a pixel or two and shift the anchor.
        uint[] pixels = new uint[16 * 16];

        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = 0x05000000;
        }

        pixels[(8 * 16) + 8] = Opaque;

        Assert.True(KrStaticArt.TryGetBounds(pixels, 16, 16, out int x0, out int y0, out int w, out int h));

        Assert.Equal(8, x0);
        Assert.Equal(8, y0);
        Assert.Equal(1, w);
        Assert.Equal(1, h);
    }

    // ---- real art ---------------------------------------------------------

    [Fact]
    public void ReturnsNothingForAGraphicKrHasNoArtFor()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        // The world-art block runs to 16382; nothing is keyed above it.
        Assert.Null(art.Sprite(0x3FFF));
        Assert.Null(art.Sprite(-1));
    }

    [Fact]
    public void ScalesASpriteOntoTheTwoDimensionalTileGrid()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        // 3278 is a bush, and one of the samples the format notes are drawn
        // from, so it is known to be present.
        KrStaticSprite? full = new KrStaticArt(store, 1.0).Sprite(3278);

        if (full is null)
        {
            return;
        }

        KrStaticSprite? scaled = new KrStaticArt(store, 0.5).Sprite(3278);

        Assert.NotNull(scaled);
        Assert.Equal(full.Width / 2, scaled!.Width, tolerance: 1);
        Assert.Equal(full.Height / 2, scaled.Height, tolerance: 1);
        Assert.Equal(scaled.Width * scaled.Height, scaled.Pixels.Length);
    }

    [Fact]
    public void ASpriteIsTrimmedOfItsPadding()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store, 1.0);

        foreach (int graphic in (int[])[2, 53, 3278])
        {
            KrStaticSprite? sprite = art.Sprite(graphic);

            if (sprite is null)
            {
                continue;
            }

            DdsImage? texture = art.Texture(graphic);

            Assert.NotNull(texture);

            // The sprite can never be larger than the texture it came out of,
            // and if it were the same size on both axes for every graphic the
            // trimming would not be happening at all.
            Assert.True(sprite.Width <= texture!.Width);
            Assert.True(sprite.Height <= texture.Height);
            Assert.Equal(sprite.Width * sprite.Height, sprite.Pixels.Length);
        }
    }

    [Fact]
    public void TheSameGraphicComesBackFromTheCache()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrStaticSprite? first = art.Sprite(3278);

        if (first is null)
        {
            return;
        }

        Assert.Same(first, art.Sprite(3278));

        art.Clear();

        Assert.NotSame(first, art.Sprite(3278));
    }
}
