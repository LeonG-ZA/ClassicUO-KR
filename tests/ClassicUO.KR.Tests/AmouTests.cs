// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.KR.Animations;
using Xunit;

namespace ClassicUO.KR.Tests;

public class AmouTests
{
    private static readonly (byte R, byte G, byte B)[] Palette =
    [
        (0, 0, 0),
        (255, 0, 0),
        (0, 255, 0),
        (0, 0, 255)
    ];

    private static uint Rgba(byte r, byte g, byte b, byte a) =>
        ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

    [Fact]
    public void ParsesHeaderAndFrameTable()
    {
        byte[] data = AmouBuilder.Build(
            bodyId: 7,
            x1: -2,
            y1: -3,
            x2: 2,
            y2: 1,
            rows: [AmouBuilder.Skip(4), AmouBuilder.Skip(4), AmouBuilder.Skip(4), AmouBuilder.Skip(4)],
            palette: Palette);

        AmouAnimation animation = AmouAnimation.Parse(data);

        Assert.Equal(1u, animation.Version);
        Assert.Equal(7u, animation.BodyId);
        Assert.Single(animation.Frames);
        Assert.Equal(-2, animation.BoundsX1);
        Assert.Equal(1, animation.BoundsY2);

        AmouFrameInfo info = animation.Frames[0];

        Assert.Equal(4, info.Width);
        Assert.Equal(4, info.Height);

        // The anchor is the mobile's feet: the negated top-left corner.
        Assert.Equal(2, info.CenterX);
        Assert.Equal(3, info.CenterY);
    }

    [Fact]
    public void RejectsFilesWithoutTheMagic()
    {
        byte[] data = new byte[64];

        Assert.False(AmouAnimation.IsAmou(data));
        Assert.Throws<InvalidDataException>(() => AmouAnimation.Parse(data));
    }

    [Fact]
    public void DecodesTransparentRunsAsZero()
    {
        byte[] data = AmouBuilder.Build(
            bodyId: 1,
            x1: 0,
            y1: 0,
            x2: 4,
            y2: 1,
            rows: [AmouBuilder.Skip(4)],
            palette: Palette);

        AmouAnimation animation = AmouAnimation.Parse(data);
        AmouFrameBitmap bitmap = animation.Decode(animation.Frames[0]);

        Assert.Equal(4, bitmap.Width);
        Assert.Equal(1, bitmap.Height);
        Assert.All(bitmap.Pixels, p => Assert.Equal(0u, p));
    }

    [Fact]
    public void DecodesOpaqueRunsFromThePalette()
    {
        // One row: skip 1, then two opaque pixels (red, green), then skip 1.
        byte[] row = [.. AmouBuilder.Skip(1), .. AmouBuilder.Run(0, [1, 2], 0), .. AmouBuilder.Skip(1)];

        byte[] data = AmouBuilder.Build(1, 0, 0, 4, 1, [row], Palette);
        AmouAnimation animation = AmouAnimation.Parse(data);
        AmouFrameBitmap bitmap = animation.Decode(animation.Frames[0]);

        Assert.Equal(0u, bitmap.Pixels[0]);
        Assert.Equal(Rgba(255, 0, 0, 255), bitmap.Pixels[1]);
        Assert.Equal(Rgba(0, 255, 0, 255), bitmap.Pixels[2]);
        Assert.Equal(0u, bitmap.Pixels[3]);
    }

    [Fact]
    public void AppliesEdgeCoverageNibblesAsAlpha()
    {
        // Leading nibble 8 and trailing nibble 4 become alpha 8*17 and 4*17.
        byte[] row = AmouBuilder.Run(8, [1], 4, leadingColour: 2, trailingColour: 3);

        byte[] data = AmouBuilder.Build(1, 0, 0, 3, 1, [row], Palette);
        AmouAnimation animation = AmouAnimation.Parse(data);
        AmouFrameBitmap bitmap = animation.Decode(animation.Frames[0]);

        Assert.Equal(Rgba(0, 255, 0, 8 * 17), bitmap.Pixels[0]);
        Assert.Equal(Rgba(255, 0, 0, 255), bitmap.Pixels[1]);
        Assert.Equal(Rgba(0, 0, 255, 4 * 17), bitmap.Pixels[2]);
    }

    [Fact]
    public void SplitsFramesIntoFiveDirections()
    {
        // Ten frames: five directions of two frames each.
        var frames = new List<(ushort, ushort, short, short, short, short, byte[])>();

        for (ushort i = 1; i <= 10; i++)
        {
            frames.Add((3, i, 0, 0, 1, 1, AmouBuilder.Run(0, [(byte)(i % 4)], 0)));
        }

        AmouAnimation animation = AmouAnimation.Parse(AmouBuilder.Build(1, frames, Palette));

        Assert.Equal(3, animation.Action);
        Assert.True(animation.HasRegularDirections);
        Assert.Equal(2, animation.FramesPerDirection);

        // Direction 2, frame 1 is the sixth stored frame, i.e. index 6.
        Assert.True(animation.TryGetFrameInfo(2, 1, out AmouFrameInfo info));
        Assert.Equal(6, info.Index);

        Assert.False(animation.TryGetFrameInfo(5, 0, out _));
        Assert.False(animation.TryGetFrameInfo(0, 2, out _));
    }

    [Fact]
    public void ReportsIrregularDirectionSplits()
    {
        var frames = new List<(ushort, ushort, short, short, short, short, byte[])>();

        for (ushort i = 1; i <= 7; i++)
        {
            frames.Add((0, i, 0, 0, 1, 1, AmouBuilder.Run(0, [1], 0)));
        }

        AmouAnimation animation = AmouAnimation.Parse(AmouBuilder.Build(1, frames, Palette));

        Assert.False(animation.HasRegularDirections);
        Assert.Equal(0, animation.FramesPerDirection);
        Assert.False(animation.TryGetFrameInfo(0, 0, out _));
    }

    [Fact]
    public void ThrowsWhenThePixelStreamIsTruncated()
    {
        // Claims a four-pixel row but supplies only a one-pixel run.
        byte[] data = AmouBuilder.Build(1, 0, 0, 4, 1, [AmouBuilder.Run(0, [1], 0)], Palette);

        AmouAnimation animation = AmouAnimation.Parse(data);

        Assert.Throws<InvalidDataException>(() => animation.Decode(animation.Frames[0]));
    }
}
