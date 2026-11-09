// SPDX-License-Identifier: BSD-2-Clause
//
// Tests that read a real UOKR install. They no-op when one is not present, so
// the suite still runs on a machine without the assets. Point the environment
// variable UOKR_PATH at the install to run them somewhere else.

using ClassicUO.KR.Animations;
using ClassicUO.KR.Uop;
using Xunit;

namespace ClassicUO.KR.Tests;

public class LiveInstallTests
{
    private static string? FindInstall()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("UOKR_PATH")
        ];

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && KrAssetStore.LooksLikeKrInstall(c!));
    }

    [Fact]
    public void Body7Action0IsTheArmouredOrcWalk()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        Assert.True(store.TryFindAnimationFrame(7, 0, out UopFile package, out UopEntry entry));
        Assert.Equal("AnimationFrame1.uop", Path.GetFileName(package.FilePath));
        Assert.True(entry.IsCompressed);

        AmouAnimation animation = store.LoadAnimation(7, 0)!;

        Assert.Equal(1u, animation.Version);
        Assert.Equal(7u, animation.BodyId);
        Assert.Equal(0, animation.Action);
        Assert.Equal(55, animation.Frames.Count);
        Assert.Equal(11, animation.FramesPerDirection);

        AmouFrameBitmap first = animation.Decode(0, 0)!;

        Assert.Equal(105, first.Width);
        Assert.Equal(112, first.Height);
        Assert.Equal(65, first.CenterX);
        Assert.Equal(83, first.CenterY);

        // A walk frame is mostly figure, so plenty of pixels must be opaque.
        Assert.Contains(first.Pixels, p => (p >> 24) == 255);
    }

    [Fact]
    public void EveryDirectionOfAWalkDecodes()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        AmouAnimation animation = store.LoadAnimation(400, 0)!;

        Assert.Equal(AmouAnimation.StoredDirectionCount * animation.FramesPerDirection, animation.Frames.Count);

        for (int direction = 0; direction < AmouAnimation.StoredDirectionCount; direction++)
        {
            for (int frame = 0; frame < animation.FramesPerDirection; frame++)
            {
                AmouFrameBitmap bitmap = animation.Decode(direction, frame)!;

                Assert.True(bitmap.Width > 0);
                Assert.True(bitmap.Height > 0);
                Assert.Equal(bitmap.Width * bitmap.Height, bitmap.Pixels.Length);
            }
        }
    }

    [Fact]
    public void HumanSequenceCarriesTheExpectedFootsteps()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrAnimationSequence sequence = store.LoadSequence(400)!;

        Assert.Equal(400, sequence.BodyId);
        Assert.Equal(52, sequence.Actions.Count);

        // Action numbering matches ClassicUO's PeopleAnimationGroup: walking
        // actions carry footstep sounds, and the two mounted gaits differ.
        Assert.Equal([299u, 300u], sequence.Actions[0].Events.Select(e => e.SoundId));
        Assert.Equal([299u, 300u], sequence.Actions[15].Events.Select(e => e.SoundId));
        Assert.Equal([297u, 298u, 297u, 298u], sequence.Actions[24].Events.Select(e => e.SoundId));

        Assert.Equal(24f, sequence.Actions[0].FramesPerSecond);
        Assert.Equal(10, sequence.Actions[0].FramesPerDirection);
    }

    [Fact]
    public void MonsterSequenceFallsBackSensibly()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrAnimationSequence sequence = store.LoadSequence(1)!;

        // These match ClassicUO's HighAnimationGroup: GetHit3 -> GetHit2, both
        // fidgets -> Stand, Fly -> Walk.
        Assert.Equal(15, sequence.ResolveAction(16));
        Assert.Equal(1, sequence.ResolveAction(17));
        Assert.Equal(1, sequence.ResolveAction(18));
        Assert.Equal(0, sequence.ResolveAction(19));
    }

    [Fact]
    public void TexturePackagesHoldDds()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        UopFile textures = store.GetPackage("LegacyTexture.uop")!;

        Assert.NotNull(textures);
        Assert.NotEmpty(textures.Entries);

        byte[] payload = textures.Read(textures.Entries[0]);

        Assert.Equal((byte)'D', payload[0]);
        Assert.Equal((byte)'D', payload[1]);
        Assert.Equal((byte)'S', payload[2]);
        Assert.Equal((byte)' ', payload[3]);
    }

    [Fact]
    public void MissingBodiesReportAbsenceRatherThanThrowing()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        Assert.False(store.HasAnimation(60000, 0));
        Assert.Null(store.LoadAnimation(60000, 0));
        Assert.Null(store.LoadSequence(60000));
    }

    /// <summary>
    /// The AMOU palette is stored R, G, B, unused. Reading it as B, G, R
    /// decodes every creature in its complementary colour, and the mistake is
    /// invisible on the human bodies because their skin is grey - so pin the
    /// orientation against a body that is unambiguously warm-coloured.
    /// </summary>
    [Fact]
    public void PaletteChannelsAreNotSwapped()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        // Body 12 is a dragon; the KR client draws it copper.
        AmouAnimation animation = store.LoadAnimation(12, 1)!;
        Assert.True(animation.TryGetFrameInfo(2, 0, out AmouFrameInfo info));

        AmouFrameBitmap bitmap = animation.Decode(info);

        long red = 0, blue = 0, opaque = 0;

        foreach (uint p in bitmap.Pixels)
        {
            if (p >> 24 < 250) continue;
            opaque++;
            red += p & 0xFF;
            blue += (p >> 16) & 0xFF;
        }

        Assert.True(opaque > 100, $"expected a decoded frame, got {opaque} opaque pixels");
        Assert.True(red > blue, $"dragon should decode warm: mean red {red / (double)opaque:F1} vs blue {blue / (double)opaque:F1}");
    }
}
