// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers.Binary;
using ClassicUO.KR.Animations;
using Xunit;

namespace ClassicUO.KR.Tests;

public class SequenceTests
{
    /// <summary>Builds a synthetic AnimationSequence record set.</summary>
    private static byte[] Build(
        int bodyId,
        IReadOnlyList<(int FramesPerDirection, int Fallback, float Fps, (uint Frame, uint Type, uint Sound)[] Events)> actions)
    {
        List<byte> data = [];

        void U32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            data.AddRange(b);
        }

        U32((uint)bodyId);

        for (int i = 0; i < 4; i++)
        {
            U32(0);
        }

        U32((uint)actions.Count);

        for (int i = 0; i < actions.Count; i++)
        {
            var a = actions[i];

            U32((uint)i);
            U32((uint)a.FramesPerDirection);
            U32(unchecked((uint)a.Fallback));
            U32(unchecked((uint)-1));
            U32((uint)BitConverter.SingleToInt32Bits(a.Fps));

            for (int f = 0; f < 4; f++)
            {
                U32(a.FramesPerDirection > 0 ? 0x80808080u : 0u);
            }

            U32((uint)a.Events.Length);

            foreach (var e in a.Events)
            {
                U32(e.Frame);
                U32(1);
                U32(e.Type);

                // Sound events carry a 20-byte payload, type 6 carries 16.
                if (e.Type == 16)
                {
                    U32(1);
                    U32(1);
                    U32(1);
                    U32(e.Sound);
                    U32((uint)BitConverter.SingleToInt32Bits(1f));
                }
                else
                {
                    for (int p = 0; p < 4; p++)
                    {
                        U32(0);
                    }
                }
            }
        }

        // The undecoded trailer always opens with a count then 0x00 0xFF.
        U32(0);
        data.AddRange([0x00, 0xFF, 0x00, 0x00]);

        return [.. data];
    }

    [Fact]
    public void ParsesActionsFramesAndRates()
    {
        byte[] data = Build(400,
        [
            (10, -1, 24f, [(1u, 16u, 299u), (5u, 16u, 300u)]),
            (10, -1, 12f, [])
        ]);

        KrAnimationSequence sequence = KrAnimationSequence.Parse(data);

        Assert.Equal(400, sequence.BodyId);
        Assert.Equal(2, sequence.Actions.Count);

        KrAnimationAction walk = sequence.Actions[0];

        Assert.True(walk.HasArt);
        Assert.Equal(10, walk.FramesPerDirection);
        Assert.Equal(50, walk.TotalFrames);
        Assert.Equal(24f, walk.FramesPerSecond);

        Assert.Equal(2, walk.Events.Length);
        Assert.True(walk.Events[0].IsSound);
        Assert.Equal(1u, walk.Events[0].Frame);
        Assert.Equal(299u, walk.Events[0].SoundId);
        Assert.Equal(1f, walk.Events[0].Volume);
        Assert.Equal(300u, walk.Events[1].SoundId);
    }

    [Fact]
    public void ParsesVariableLengthEventRecords()
    {
        // Type 6 records are four bytes shorter than sound records; mixing them
        // is what forces the size to be read from the type.
        byte[] data = Build(1,
        [
            (10, -1, 9f, [(0u, 6u, 0u), (1u, 6u, 0u), (2u, 16u, 42u)]),
            (10, -1, 9f, [])
        ]);

        KrAnimationSequence sequence = KrAnimationSequence.Parse(data);

        Assert.Equal(3, sequence.Actions[0].Events.Length);
        Assert.Equal(KrAnimationEventType.PerFrameData, sequence.Actions[0].Events[0].Type);
        Assert.Equal(42u, sequence.Actions[0].Events[2].SoundId);

        // The second record must still line up after the mixed event list.
        Assert.Equal(1, sequence.Actions[1].Action);
        Assert.True(sequence.Actions[1].HasArt);
    }

    [Fact]
    public void FollowsFallbackChains()
    {
        byte[] data = Build(1,
        [
            (10, -1, 9f, []),   // 0: walk, has art
            (10, -1, 9f, []),   // 1: stand, has art
            (0, 1, 0f, []),     // 2: falls back to stand
            (0, 2, 0f, [])      // 3: falls back to 2, which falls back to stand
        ]);

        KrAnimationSequence sequence = KrAnimationSequence.Parse(data);

        Assert.Equal(0, sequence.ResolveAction(0));
        Assert.Equal(1, sequence.ResolveAction(2));
        Assert.Equal(1, sequence.ResolveAction(3));
    }

    [Fact]
    public void ReturnsMinusOneWhenNothingInTheChainHasArt()
    {
        byte[] data = Build(1,
        [
            (0, 1, 0f, []),
            (0, 0, 0f, [])      // a cycle back to 0
        ]);

        KrAnimationSequence sequence = KrAnimationSequence.Parse(data);

        Assert.Equal(-1, sequence.ResolveAction(0));
        Assert.Equal(-1, sequence.ResolveAction(99));
    }

    [Fact]
    public void RejectsUnknownEventTypes()
    {
        byte[] data = Build(1, [(10, -1, 9f, [(0u, 99u, 0u)])]);

        Assert.Throws<InvalidDataException>(() => KrAnimationSequence.Parse(data));
    }

    [Fact]
    public void RejectsTruncatedFiles()
    {
        Assert.Throws<InvalidDataException>(() => KrAnimationSequence.Parse(new byte[8]));
    }
}
