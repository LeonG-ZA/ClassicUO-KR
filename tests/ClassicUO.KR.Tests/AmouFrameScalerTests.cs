// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.KR.Animations;
using Xunit;

namespace ClassicUO.KR.Tests;

public class AmouFrameScalerTests
{
    private static uint Rgba(byte r, byte g, byte b, byte a) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

    private static byte A(uint p) => (byte)(p >> 24);

    private static byte R(uint p) => (byte)p;

    private static byte G(uint p) => (byte)(p >> 8);

    private static byte B(uint p) => (byte)(p >> 16);

    private static AmouFrameBitmap Solid(int width, int height, int centerX, int centerY, uint colour)
    {
        uint[] pixels = new uint[width * height];
        Array.Fill(pixels, colour);

        return new AmouFrameBitmap(width, height, centerX, centerY, pixels);
    }

    /// <summary>
    /// What the renderer actually uses: the frame's top and bottom edges
    /// relative to the anchor. MobileView draws the top edge at
    /// <c>y - (Height + FrameInfo.CenterY)</c>, so top is <c>-CenterY</c> and
    /// bottom is <c>Height - CenterY</c> in bitmap terms.
    /// </summary>
    private static (int Top, int Bottom, int Left, int Right) Bounds(AmouFrameBitmap frame) =>
        (-frame.CenterY, frame.Height - frame.CenterY, -frame.CenterX, frame.Width - frame.CenterX);

    [Fact]
    public void TileScaleIsTheReciprocalOfTheMeasuredHumanFactor()
    {
        Assert.Equal(2.0 / 3.0, AmouFrameScaler.TileScale);
    }

    [Fact]
    public void ScaleOfOneReturnsTheFrameUntouched()
    {
        AmouFrameBitmap frame = Solid(4, 4, 2, 4, Rgba(10, 20, 30, 255));

        Assert.Same(frame, AmouFrameScaler.Scale(frame, 1.0));
    }

    [Fact]
    public void HumanBodyLandsOnTheTwoDimensionalSize()
    {
        // Body 400 action 0 as measured through GetAnimationFrames: 61x96,
        // against 40x64 for the 2D human.
        AmouFrameBitmap frame = Solid(61, 96, 30, 95, Rgba(255, 255, 255, 255));

        AmouFrameBitmap scaled = AmouFrameScaler.Scale(frame);

        Assert.Equal(41, scaled.Width);
        Assert.Equal(64, scaled.Height);
    }

    [Fact]
    public void BoundsScaleTogetherSoTheAnchorStaysPut()
    {
        // Feet one pixel below the anchor, as KR stores a standing mobile.
        AmouFrameBitmap frame = Solid(61, 96, 30, 95, Rgba(255, 255, 255, 255));

        (int top, int bottom, int left, int right) = Bounds(frame);
        AmouFrameBitmap scaled = AmouFrameScaler.Scale(frame);
        (int sTop, int sBottom, int sLeft, int sRight) = Bounds(scaled);

        Assert.Equal((int)Math.Floor(top * AmouFrameScaler.TileScale + 0.5), sTop);
        Assert.Equal((int)Math.Floor(bottom * AmouFrameScaler.TileScale + 0.5), sBottom);
        Assert.Equal((int)Math.Floor(left * AmouFrameScaler.TileScale + 0.5), sLeft);
        Assert.Equal((int)Math.Floor(right * AmouFrameScaler.TileScale + 0.5), sRight);
    }

    [Fact]
    public void EveryEdgeStaysWithinHalfAPixelOfTheTrueScaledPosition()
    {
        // The failure this guards against is rounding Height and the anchor
        // offset independently, which lets the error on the two edges add up
        // and walks the feet off the tile.
        for (int height = 1; height <= 200; height++)
        {
            for (int centerY = 0; centerY <= height; centerY++)
            {
                AmouFrameBitmap frame = Solid(8, height, 4, centerY, Rgba(1, 2, 3, 255));
                AmouFrameBitmap scaled = AmouFrameScaler.Scale(frame);

                (int top, int bottom, _, _) = Bounds(frame);
                (int sTop, int sBottom, _, _) = Bounds(scaled);

                Assert.True(
                    Math.Abs(sTop - top * AmouFrameScaler.TileScale) <= 0.5,
                    $"top edge drifted: height {height}, centerY {centerY}");

                Assert.True(
                    Math.Abs(sBottom - bottom * AmouFrameScaler.TileScale) <= 0.5 || scaled.Height == 1,
                    $"bottom edge drifted: height {height}, centerY {centerY}");
            }
        }
    }

    [Fact]
    public void FramesNeverVanish()
    {
        for (int size = 1; size <= 4; size++)
        {
            AmouFrameBitmap scaled = AmouFrameScaler.Scale(Solid(size, size, 0, size, Rgba(9, 9, 9, 255)), 0.1);

            Assert.True(scaled.Width >= 1);
            Assert.True(scaled.Height >= 1);
            Assert.Equal(scaled.Width * scaled.Height, scaled.Pixels.Length);
        }
    }

    [Fact]
    public void SolidInteriorKeepsItsColour()
    {
        uint colour = Rgba(200, 120, 40, 255);
        AmouFrameBitmap scaled = AmouFrameScaler.Scale(Solid(60, 60, 30, 59, colour));

        Assert.All(scaled.Pixels, p => Assert.Equal(colour, p));
    }

    [Fact]
    public void FullyTransparentStaysFullyTransparent()
    {
        // Including the colour channels: PixelPicker's hit mask counts a
        // pixel as transparent only when the whole word is zero, so alpha 0
        // with a leftover colour would leave the sprite clickable there.
        AmouFrameBitmap scaled = AmouFrameScaler.Scale(Solid(30, 30, 15, 29, Rgba(255, 0, 0, 0)));

        Assert.All(scaled.Pixels, p => Assert.Equal(0u, p));
    }

    [Fact]
    public void TransparentPixelsDoNotDragColourTowardsBlack()
    {
        // Half the source is opaque red, half is transparent black - the shape
        // of an anti-aliased KR edge. Alpha-weighting the colour has to leave
        // red red; a plain average would give (128, 0, 0).
        uint[] pixels =
        [
            Rgba(255, 0, 0, 255), Rgba(0, 0, 0, 0),
            Rgba(255, 0, 0, 255), Rgba(0, 0, 0, 0)
        ];

        AmouFrameBitmap scaled = AmouFrameScaler.Scale(new AmouFrameBitmap(2, 2, 1, 2, pixels), 0.5);

        Assert.Single(scaled.Pixels);

        uint p = scaled.Pixels[0];

        Assert.Equal(255, R(p));
        Assert.Equal(0, G(p));
        Assert.Equal(0, B(p));
        Assert.Equal(128, A(p));
    }

    [Fact]
    public void AreaFilterAveragesTheWholeFootprint()
    {
        // Nearest-neighbour would pick one source pixel and report 0 or 255;
        // a box filter over a 2x2 of two black and two white opaque pixels
        // gives the mean.
        uint[] pixels =
        [
            Rgba(0, 0, 0, 255), Rgba(255, 255, 255, 255),
            Rgba(255, 255, 255, 255), Rgba(0, 0, 0, 255)
        ];

        AmouFrameBitmap scaled = AmouFrameScaler.Scale(new AmouFrameBitmap(2, 2, 1, 2, pixels), 0.5);

        Assert.Equal(128, R(scaled.Pixels[0]));
        Assert.Equal(255, A(scaled.Pixels[0]));
    }

    [Fact]
    public void EdgePixelsAreNormalisedAgainstTheSourceThatExists()
    {
        // A 3x3 opaque square at 2/3 must stay fully opaque everywhere. The
        // footprint spans the whole source grid, so no destination pixel can
        // pick up phantom transparency from beyond the edge and fade the
        // outline.
        AmouFrameBitmap scaled = AmouFrameScaler.Scale(Solid(3, 3, 1, 3, Rgba(80, 90, 100, 255)));

        Assert.Equal(2, scaled.Width);
        Assert.Equal(2, scaled.Height);
        Assert.All(scaled.Pixels, p => Assert.Equal(255, A(p)));
    }

    [Fact]
    public void PartiallyTransparentSourceKeepsItsAverageAlpha()
    {
        uint[] pixels =
        [
            Rgba(10, 20, 30, 64), Rgba(10, 20, 30, 192),
            Rgba(10, 20, 30, 64), Rgba(10, 20, 30, 192)
        ];

        AmouFrameBitmap scaled = AmouFrameScaler.Scale(new AmouFrameBitmap(2, 2, 1, 2, pixels), 0.5);

        uint p = scaled.Pixels[0];

        Assert.Equal(128, A(p));
        Assert.Equal(10, R(p));
        Assert.Equal(20, G(p));
        Assert.Equal(30, B(p));
    }
}
