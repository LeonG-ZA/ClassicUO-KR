// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.KR.Uop;
using Xunit;

namespace ClassicUO.KR.Tests;

public class UopHashTests
{
    [Theory]
    // The reference vector from the handover brief: body 7, action 0.
    [InlineData("build/animationframe/000007/00.bin", 0x6244CDD1C9A844A1UL)]
    public void MatchesKnownVectors(string name, ulong expected)
    {
        Assert.Equal(expected, UopHash.Create(name));
    }

    [Fact]
    public void LowerCasesTheNameBeforeHashing()
    {
        Assert.Equal(
            UopHash.Create("build/animationframe/000007/00.bin"),
            UopHash.Create("BUILD/AnimationFrame/000007/00.BIN"));
    }

    [Fact]
    public void IsSensitiveToEveryPathSegment()
    {
        ulong baseline = UopHash.Create("build/animationframe/000007/00.bin");

        Assert.NotEqual(baseline, UopHash.Create("build/animationframe/000007/01.bin"));
        Assert.NotEqual(baseline, UopHash.Create("build/animationframe/000008/00.bin"));
        Assert.NotEqual(baseline, UopHash.Create("build/animationframe/000007/00.bi"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("abcdefghijk")]      // one byte short of a full 12-byte block
    [InlineData("abcdefghijkl")]     // exactly one block
    [InlineData("abcdefghijklm")]    // one byte over
    public void HandlesEveryBlockBoundary(string name)
    {
        // The block loop and the tail switch are the easy places to get this
        // wrong; the point here is simply that no length throws.
        UopHash.Create(name);
    }

    [Fact]
    public void BuildsTheDocumentedNamePatterns()
    {
        Assert.Equal("build/animationframe/000007/00.bin", KrAssetStore.AnimationFrameName(7, 0));
        Assert.Equal("build/animationframe/000400/23.bin", KrAssetStore.AnimationFrameName(400, 23));
        Assert.Equal("build/animationsequence/00000400.bin", KrAssetStore.AnimationSequenceName(400));
        Assert.Equal("build/animationdefinition/00000060.bin", KrAssetStore.AnimationDefinitionName(60));
    }
}
