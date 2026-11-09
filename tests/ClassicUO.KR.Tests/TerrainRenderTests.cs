// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.KR;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class TerrainRenderTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    // ---- art id handling --------------------------------------------------

    [Theory]
    [InlineData(@"Data\WorldArt\02000010_Grass_A.tga", "02000010")]
    [InlineData("02000011_Grass_B.tga", "02000011")]
    [InlineData("Swamp2 edges", null)]
    [InlineData("UODefaultTerrainLayer", null)]
    [InlineData(null, null)]
    public void ReadsTheArtIdOutOfATextureName(string? name, string? expected)
    {
        Assert.Equal(expected, KrTerrainArt.ArtId(name));
    }

    [Theory]
    // The B half of a pair resolves to the A half.
    [InlineData("02000011", "02000010")]
    [InlineData("02000041", "02000040")]
    // Already the A half, so there is no sibling to go to.
    [InlineData("02000010", null)]
    [InlineData(null, null)]
    [InlineData("0200001", null)]
    public void FindsTheASiblingOfATexturePair(string? artId, string? expected)
    {
        Assert.Equal(expected, KrTerrainArt.ASibling(artId));
    }

    // ---- compositing and resampling ---------------------------------------

    [Fact]
    public void CompositesSourceOverDestination()
    {
        // (A << 24) | (B << 16) | (G << 8) | R
        uint[] dst = [0xFF0000FFu, 0xFF0000FFu, 0xFF0000FFu];
        uint[] src = [0x00FF0000u, 0xFFFF0000u, 0x80FF0000u];

        KrTerrainArt.Over(dst, src);

        // transparent source leaves the destination alone
        Assert.Equal(0xFF0000FFu, dst[0]);
        // opaque source replaces it
        Assert.Equal(0xFFFF0000u, dst[1]);
        // half alpha mixes: red 255 -> 127, blue 0 -> 128
        Assert.Equal(127u, dst[2] & 0xFF);
        Assert.Equal(128u, (dst[2] >> 16) & 0xFF);
        Assert.Equal(255u, dst[2] >> 24);
    }

    [Fact]
    public void ResamplesByAveragingAreas()
    {
        // 2x2 of four opaque colours down to 1x1 is their mean.
        uint[] source =
        [
            0xFF000000u | 100u, 0xFF000000u | 200u,
            0xFF000000u | 40u, 0xFF000000u | 60u
        ];

        uint[] result = KrTerrainArt.Resample(source, 2, 2, 1, 1);

        Assert.Single(result);
        Assert.Equal(100u, result[0] & 0xFF);
        Assert.Equal(255u, result[0] >> 24);
    }

    [Fact]
    public void ResampleIgnoresTransparentTexelsWhenAveragingColour()
    {
        // A transparent black texel must not drag the colour down.
        uint[] source =
        [
            0xFF0000FFu, 0x00000000u,
            0xFF0000FFu, 0x00000000u
        ];

        uint[] result = KrTerrainArt.Resample(source, 2, 2, 1, 1);

        Assert.Equal(255u, result[0] & 0xFF);
        Assert.Equal(127u, result[0] >> 24);
    }

    [Fact]
    public void ResampleRejectsBadSizes()
    {
        Assert.Throws<ArgumentException>(() => KrTerrainArt.Resample(new uint[4], 2, 2, 0, 1));
        Assert.Throws<ArgumentException>(() => KrTerrainArt.Resample(new uint[4], 0, 2, 1, 1));
    }

    // ---- the land tile to terrain table -----------------------------------

    [Fact]
    public void RealInstallMapsLandTilesToTerrains()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        KrTerrainMap map = KrTerrainMap.Build(root, [0, 1, 2, 3, 4]);

        // The whole world: 17,045 sectors of 4,096 tiles.
        Assert.Equal(17045L * 4096, map.TilesScanned);

        Assert.True(map.Known > 1500, $"only {map.Known} land tile ids mapped");

        // Nearly a function: hardly any land tile id carries two terrains.
        Assert.True(map.Ambiguous <= 4, $"{map.Ambiguous} land tile ids were ambiguous");

        // The ones everyone sees: water, grass and forest.
        Assert.True(map.TryGet(168, out ushort water));
        Assert.True(map.TryGet(3, out ushort grass));
        Assert.True(map.TryGet(196, out ushort forest));

        Assert.NotEqual(water, grass);
        Assert.NotEqual(grass, forest);

        // Land tile ids that do not exist in the world stay unknown.
        Assert.False(map.TryGet(0x3FFF, out _));
    }

    [Fact]
    public void EveryMappedLandTileYieldsAnOpaqueGroundTile()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        KrTerrainMap map = KrTerrainMap.Build(root, [0]);

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        int checked_ = 0;
        int missing = 0;
        List<int> emptyGraphics = [];

        for (int graphic = 0; graphic < KrTerrainMap.LandGraphicCount; graphic++)
        {
            if (!map.TryGet(graphic, out ushort terrain))
            {
                continue;
            }

            uint[]? tile = art.GroundTile(terrain, 44);

            if (tile is null)
            {
                missing++;
                emptyGraphics.Add(graphic);

                continue;
            }

            Assert.Equal(44 * 44, tile.Length);

            // The ground is the bottom of the world: nothing may show through.
            foreach (uint pixel in tile)
            {
                Assert.Equal(255u, pixel >> 24);
            }

            checked_++;
        }

        Assert.True(checked_ > 1000, $"only {checked_} land tile ids produced a tile");

        // Not zero any more, and deliberately so. A terrain whose every ground
        // candidate is one of KR's uniformly black placeholders now resolves
        // to nothing and the tile stays on the 2D art, which is what stopped
        // black diamonds appearing along shorelines. Those ids are the only
        // ones allowed to come back empty, and each one is checked below
        // rather than the count simply being relaxed.
        Assert.True(missing < 50, $"{missing} land tile ids produced no tile");

        foreach (int graphic in emptyGraphics)
        {
            map.TryGet(graphic, out ushort terrain);

            Assert.Null(art.GroundArtId(terrain));

            // Every texture the CHAIN considers has to be a placeholder, or
            // something other than the placeholder rule is dropping tiles.
            // The chain is slot 0, its A sibling and the record's name - not
            // every layer, since the later slots are detail layers that are
            // never drawn on their own (section 13).
            KrTerrainDefinition? definition = art.Definition(terrain);

            Assert.NotNull(definition);

            string? slot0 = null;

            foreach (KrTerrainLayer layer in definition!.Layers)
            {
                if (layer.Slot != 0)
                {
                    continue;
                }

                slot0 = KrTerrainArt.ArtId(art.Strings?[layer.TextureStringIndex]);

                if (slot0 is not null)
                {
                    break;
                }
            }

            string?[] candidates =
            [
                KrTerrainArt.ASibling(slot0),
                slot0,
                KrTerrainArt.ArtId(art.Strings?[definition.NameStringIndex])
            ];

            foreach (string? id in candidates)
            {
                if (id is not null && art.Texture(id) is not null)
                {
                    Assert.True(
                        art.IsPlaceholder(id),
                        $"land {graphic}, terrain {terrain}: {id} is real art but was dropped");
                }
            }
        }
    }


    [Fact]
    public void GroundTilesAreCachedPerSize()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        uint[]? a = art.GroundTile(1, 44);
        uint[]? b = art.GroundTile(1, 44);
        uint[]? c = art.GroundTile(1, 64);

        Assert.NotNull(a);
        Assert.Same(a, b);
        Assert.NotSame(a, c);
        Assert.Equal(64 * 64, c!.Length);
    }

    /// <summary>
    /// The point of the positional crops is that neighbouring tiles continue
    /// the texture. With a crop that needs no rescaling the assembly has to
    /// come back bit for bit, which pins the region maths exactly.
    /// </summary>
    [Fact]
    public void RegionCropsReassembleIntoTheSourceExactly()
    {
        const int side = 64;
        const int period = 4;
        const int size = side / period;

        uint[] source = new uint[side * side];

        for (int y = 0; y < side; y++)
        {
            for (int x = 0; x < side; x++)
            {
                // Distinct per pixel, so any mis-indexing shows up.
                source[(y * side) + x] = 0xFF000000u | (uint)((y * side) + x);
            }
        }

        uint[] assembled = new uint[side * side];

        for (int vy = 0; vy < period; vy++)
        {
            for (int vx = 0; vx < period; vx++)
            {
                uint[] crop = KrTerrainArt.ResampleRegion(source, side, side, vx, vy, period, size);

                Assert.Equal(size * size, crop.Length);

                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        assembled[(((vy * size) + y) * side) + (vx * size) + x] = crop[(y * size) + x];
                    }
                }
            }
        }

        Assert.Equal(source, assembled);
    }

    [Fact]
    public void RegionCropsOfOnePeriodAreTheWholeSource()
    {
        uint[] source = [1u, 2u, 3u, 4u];

        Assert.Equal(
            KrTerrainArt.Resample(source, 2, 2, 2, 2),
            KrTerrainArt.ResampleRegion(source, 2, 2, 0, 0, 1, 2));
    }

    /// <summary>Neighbouring tiles must not be handed the same pixels.</summary>
    [Fact]
    public void NeighbouringTilesGetDifferentCrops()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        uint[]? a = art.GroundTile(1, 44, 0, 0);
        uint[]? b = art.GroundTile(1, 44, 1, 0);
        uint[]? c = art.GroundTile(1, 44, 0, 1);

        Assert.NotNull(a);
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }

    [Fact]
    public void PeriodFollowsTheTextureResolution()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        for (int terrain = 0; terrain <= 190; terrain++)
        {
            Assert.InRange(art.PeriodFor(terrain), 1, KrTerrainArt.MaxCompositeRepeat);
        }

        // Composited terrains repeat on their largest layer repeat: grass's
        // noise at 24 -> 32, the flagstone road's at 16.
        Assert.Equal(32, art.PeriodFor(1));
        Assert.Equal(16, art.PeriodFor(42));
    }

    [Fact]
    public void CropsWrapForNegativeAndLargeCoordinates()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        int period = art.PeriodFor(1);

        Assert.Same(art.GroundTile(1, 16, 1, 2), art.GroundTile(1, 16, 1 + period, 2 + period));
        Assert.Same(art.GroundTile(1, 16, 1, 2), art.GroundTile(1, 16, 1 - period, 2 - (2 * period)));
    }

    /// <summary>
    /// The diamond is sampled in world space, so a tile a whole period away
    /// must come back identical and its immediate neighbour must not.
    /// </summary>
    [Fact]
    public void DiamondsWrapOnThePeriodAndDifferBetweenNeighbours()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        int period = art.PeriodFor(1);

        uint[]? a = art.GroundDiamond(1, 44, 3, 5);

        Assert.NotNull(a);
        Assert.Same(a, art.GroundDiamond(1, 44, 3 + period, 5 + period));
        Assert.Same(a, art.GroundDiamond(1, 44, 3 - period, 5 - period));
        Assert.NotEqual(a, art.GroundDiamond(1, 44, 4, 5));
    }

    [Fact]
    public void DiamondsAreMaskedAndOpaqueInside()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);

        KrTerrainArt art = new(store);

        uint[]? tile = art.GroundDiamond(1, 44, 0, 0);

        Assert.NotNull(tile);
        Assert.Equal(44 * 44, tile!.Length);

        int inside = 0;

        for (int y = 0; y < 44; y++)
        {
            int count = y < 22 ? (y + 1) * 2 : (44 - y) * 2;
            int start = (44 - count) / 2;

            for (int x = 0; x < 44; x++)
            {
                uint alpha = tile[(y * 44) + x] >> 24;

                if (x >= start && x < start + count)
                {
                    Assert.Equal(255u, alpha);
                    inside++;
                }
                else
                {
                    Assert.Equal(0u, alpha);
                }
            }
        }

        // The UO land diamond covers 2 + 4 + .. + 44 + .. + 2 pixels.
        Assert.Equal(2 * (22 * 23), inside);
    }
}
