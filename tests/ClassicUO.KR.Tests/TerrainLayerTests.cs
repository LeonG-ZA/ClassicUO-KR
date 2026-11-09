// SPDX-License-Identifier: BSD-2-Clause
//
// The terrain layer stack: how a layer's scale becomes a tile period, and
// what GroundStack puts in the stack.
//
// Layer blending is OFF by default and these tests pin that, because it is a
// deliberate decision rather than an oversight: measured over the whole map it
// is worse, and the reason is in KR's own data. See KrTerrainArt.BlendLayers.

using ClassicUO.KR;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class TerrainLayerTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    // ---- scale to period --------------------------------------------------

    [Theory]
    // 64 / scale tiles, snapped to the nearest power of two. Grass slot 0 is
    // x10 (6.4 tiles -> 8) and slot 2 is x24 (2.7 -> 2); forest slot 0 is x5
    // (12.8, capped at 8) and slot 2 is x16 (4.0 -> 4).
    [InlineData(10f, 8, 8)]
    [InlineData(24f, 8, 2)]
    [InlineData(5f, 8, 8)]
    [InlineData(16f, 8, 4)]
    [InlineData(64f, 8, 1)]
    public void TurnsALayerScaleIntoATilePeriod(float scale, int max, int expected)
    {
        Assert.Equal(expected, KrTerrainArt.LayerPeriod(scale, max));
    }

    [Fact]
    public void NeverExceedsTheTerrainsOwnPeriod()
    {
        // A very coarse layer would want far more tiles than the terrain's
        // period allows; it has to be capped or it would not tile within it.
        Assert.Equal(4, KrTerrainArt.LayerPeriod(1f, 4));
        Assert.Equal(1, KrTerrainArt.LayerPeriod(1f, 1));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(-3f)]
    [InlineData(float.NaN)]
    public void FallsBackToTheWholePeriodForAnUnusableScale(float scale)
    {
        Assert.Equal(8, KrTerrainArt.LayerPeriod(scale, 8));
    }

    [Fact]
    public void EveryLayerPeriodDividesTheCommonOne()
    {
        // This is the property the whole scheme rests on: a layer whose period
        // did not divide the terrain's would not tile within it, and the ground
        // would show a seam every few tiles.
        for (int max = 1; max <= 8; max *= 2)
        {
            for (float scale = 1; scale <= 48; scale++)
            {
                int period = KrTerrainArt.LayerPeriod(scale, max);

                Assert.True(period >= 1 && period <= max, $"scale {scale} max {max} -> {period}");
                Assert.Equal(0, max % period);
            }
        }
    }

    // ---- the stack --------------------------------------------------------

    [Fact]
    public void DrawsOneTextureUnlessLayerBlendingIsAskedFor()
    {
        string? path = FindInstall();
        if (path is null)
        {
            return;
        }


        using KrAssetStore store = new(path!);

        KrTerrainArt art = new(store);

        // Terrain 4 is forest, which has two layers with textures besides the
        // one the chain picks, so it would grow if blending were on.
        Assert.Single(art.GroundStack(4));

        art.BlendLayers = true;
        art.Clear();

        Assert.True(art.GroundStack(4).Length > 1);
    }



    [Fact]
    public void NeverPutsTheBlackVoidBaseInTheStack()
    {
        string? path = FindInstall();
        if (path is null)
        {
            return;
        }


        using KrAssetStore store = new(path!);

        KrTerrainArt art = new(store) { BlendLayers = true };

        // 02000000_Black_Void_A is the base of terrain after terrain and is an
        // opaque near-black placeholder; painting it is what put black blotches
        // through grass, so it must never reach the stack.
        foreach (int terrain in (int[])[1, 4, 5, 52, 146, 170])
        {
            Assert.DoesNotContain(art.GroundStack(terrain), e => e.Id == "02000000");
        }
    }

    [Fact]
    public void TheStackIsOrderedBottomFirstAndStartsWithTheChosenGround()
    {
        string? path = FindInstall();
        if (path is null)
        {
            return;
        }


        using KrAssetStore store = new(path!);

        KrTerrainArt art = new(store) { BlendLayers = true };

        foreach (int terrain in (int[])[1, 4, 146])
        {
            (string Id, int Period)[] stack = art.GroundStack(terrain);

            if (stack.Length == 0)
            {
                continue;
            }


            Assert.Equal(art.GroundArtId(terrain), stack[0].Id);
            Assert.Equal(stack.Select(e => e.Id).Distinct().Count(), stack.Length);
        }
    }

    [Fact]
    public void APeriodIsAlwaysAPowerOfTwo()
    {
        string? path = FindInstall();
        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path!);

        KrTerrainArt art = new(store);

        for (int terrain = 0; terrain < 191; terrain++)
        {
            int period = art.PeriodFor(terrain);

            Assert.True(period >= 1 && period <= KrTerrainArt.MaxCompositeRepeat);
            Assert.Equal(0, KrTerrainArt.MaxCompositeRepeat % period);
        }
    }

    [Fact]
    public void GrassIsCompositedTheWayTheCaptureShowed()
    {
        string? path = FindInstall();
        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path!);

        KrTerrainArt art = new(store);

        // As the KR client draws it: Grass_A x10 over Grass_B x5 through
        // noise_alpha x24, snapped to powers of two for the sprite cache.
        KrTerrainArt.CompositeLayers? grass = art.Composite(1);

        Assert.NotNull(grass);
        Assert.Same(art.Texture("02000010"), grass!.Top);
        Assert.Same(art.Texture("02000011"), grass.Ground);
        Assert.Same(art.Texture("01000003"), grass.Mask);
        Assert.Equal((8, 4, 32), (grass.TopRepeat, grass.GroundRepeat, grass.MaskRepeat));
        Assert.Equal(32, art.PeriodFor(1));

        // Water is its own shader class, not composited.
        Assert.Null(art.Composite(5));

        // A composite tile repeats on its period.
        Assert.Equal(art.GroundDiamond(1, 44, 3, 5), art.GroundDiamond(1, 44, 3 + 32, 5 - 32));
    }

    // ---- what blending actually does --------------------------------------


    [Fact]
    public void EveryTileIsOpaqueWhicheverWayItIsBuilt()
    {
        string? path = FindInstall();
        if (path is null)
        {
            return;
        }


        using KrAssetStore store = new(path!);

        foreach (bool blend in (bool[])[false, true])
        {
            KrTerrainArt art = new(store) { BlendLayers = blend };

            foreach (int terrain in (int[])[1, 4, 5, 146, 170])
            {
                uint[]? tile = art.GroundTile(terrain, 44);

                if (tile is null)
                {
                    continue;
                }

                // The ground is the bottom of the world; nothing shows through.
                Assert.All(tile, p => Assert.Equal(0xFFu, p >> 24));
            }
        }
    }
}
