// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers.Binary;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class FacetTests
{
    private sealed record TileSpec(sbyte Z, ushort TerrainId, ushort LandTileId, int Seams, int Statics);

    /// <summary>
    /// Builds a sector payload the way the shipped files are laid out: a 3-byte
    /// header, then 4096 tiles of land record + seams + statics, where the
    /// static count lives in the trailing byte of the last record before the
    /// static list.
    /// </summary>
    private static byte[] BuildSector(ushort index, Func<int, TileSpec> spec)
    {
        List<byte> data = [0, 0, 0];
        BinaryPrimitives.WriteUInt16LittleEndian(
            System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data).Slice(1),
            index);

        for (int i = 0; i < 64 * 64; i++)
        {
            TileSpec t = spec(i);

            byte[] land = new byte[KrLandTile.RecordSize];
            land[0] = (byte)t.Z;
            BinaryPrimitives.WriteUInt16LittleEndian(land.AsSpan(1), t.TerrainId);
            land[3] = 0;
            BinaryPrimitives.WriteUInt16LittleEndian(land.AsSpan(4), t.LandTileId);
            land[6] = (byte)t.Seams;
            // Only the land record's trailing byte counts the statics when
            // there is no seam to carry it.
            land[7] = t.Seams == 0 ? (byte)t.Statics : (byte)0;
            data.AddRange(land);

            for (int s = 0; s < t.Seams; s++)
            {
                byte[] seam = new byte[KrTerrainSeam.RecordSize];
                seam[0] = 7;
                BinaryPrimitives.WriteUInt32LittleEndian(seam.AsSpan(1), 42u);
                // Non-final seams carry a non-zero marker; the last one carries
                // the static count.
                seam[5] = s == t.Seams - 1 ? (byte)t.Statics : (byte)3;
                data.AddRange(seam);
            }

            for (int s = 0; s < t.Statics; s++)
            {
                byte[] st = new byte[KrStatic.RecordSize];
                BinaryPrimitives.WriteUInt32LittleEndian(st, (uint)(6000 + s));
                st[4] = unchecked((byte)-5);
                BinaryPrimitives.WriteUInt32LittleEndian(st.AsSpan(5), 1641u);
                data.AddRange(st);
            }
        }

        return [.. data];
    }

    private static TileSpec Plain => new(0, 5, 1531, 0, 0);

    [Fact]
    public void ParsesASectorOfPlainLand()
    {
        byte[] payload = BuildSector(7, _ => new TileSpec(-5, 5, 168, 0, 0));

        Assert.True(KrFacetFile.TryParseSector(payload, out KrSector sector));

        Assert.Equal(0, sector.Facet);
        Assert.Equal(7, sector.Index);
        Assert.Equal(64 * 64, sector.Tiles.Length);
        Assert.Empty(sector.Seams);
        Assert.Empty(sector.Statics);

        Assert.Equal(-5, sector.Tiles[0].Z);
        Assert.Equal(5, sector.Tiles[0].TerrainId);
        Assert.Equal(168, sector.Tiles[0].LandTileId);
    }

    [Fact]
    public void ReadsSeamsAndStaticsPerTile()
    {
        // Tile 3 carries two seams and one static; tile 9 carries no seam and
        // two statics, so the count has to come off the land record instead.
        byte[] payload = BuildSector(0, i => i switch
        {
            3 => new TileSpec(0, 5, 1531, 2, 1),
            9 => new TileSpec(0, 5, 1531, 0, 2),
            _ => Plain
        });

        Assert.True(KrFacetFile.TryParseSector(payload, out KrSector sector));

        Assert.Equal(2, sector.Seams.Length);
        Assert.Equal(3, sector.Statics.Length);

        Assert.Equal(2, sector.Tiles[3].SeamCount);
        Assert.Equal(1, sector.Tiles[3].StaticCount);
        Assert.Equal(0, sector.Tiles[9].SeamCount);
        Assert.Equal(2, sector.Tiles[9].StaticCount);

        ReadOnlySpan<KrTerrainSeam> seams = sector.SeamsFor(3);
        Assert.Equal(2, seams.Length);
        Assert.Equal(7, seams[0].Z);
        Assert.Equal(42u, seams[0].TerrainId);

        ReadOnlySpan<KrStatic> statics = sector.StaticsFor(9);
        Assert.Equal(2, statics.Length);
        Assert.Equal(6000u, statics[0].ArtId);
        Assert.Equal(-5, statics[0].Z);
        Assert.Equal(1641u, statics[0].Hue);

        // Nothing else carries anything.
        Assert.True(sector.StaticsFor(0).IsEmpty);
        Assert.True(sector.SeamsFor(0).IsEmpty);
    }

    [Fact]
    public void RejectsASectorThatDoesNotConsumeExactly()
    {
        byte[] payload = [.. BuildSector(0, _ => Plain), 0xFF];

        Assert.False(KrFacetFile.TryParseSector(payload, out _));
    }

    [Fact]
    public void RejectsATruncatedSector()
    {
        byte[] full = BuildSector(0, _ => Plain);

        Assert.False(KrFacetFile.TryParseSector(full.AsSpan(0, full.Length - 8).ToArray(), out _));
        Assert.False(KrFacetFile.TryParseSector([0, 0], out _));
    }

    [Theory]
    // The sector grid is ceil(size / 64), and these are the real KR facets.
    [InlineData(7168, 4096, 112, 64, 7168)]
    [InlineData(2304, 1600, 36, 25, 900)]
    [InlineData(2560, 2048, 40, 32, 1280)]
    [InlineData(1448, 1448, 23, 23, 529)]
    public void SectorGridMatchesTheMapSize(int width, int height, int sx, int sy, int count)
    {
        Assert.Equal(sx, (width + KrFacetFile.SectorSize - 1) / KrFacetFile.SectorSize);
        Assert.Equal(sy, (height + KrFacetFile.SectorSize - 1) / KrFacetFile.SectorSize);
        Assert.Equal(count, sx * sy);
    }

    [Fact]
    public void TilesWithinASectorAreOrderedColumnMajor()
    {
        Assert.Equal(0, KrFacetFile.TileIndex(0, 0));
        Assert.Equal(1, KrFacetFile.TileIndex(0, 1));
        Assert.Equal(64, KrFacetFile.TileIndex(1, 0));
        Assert.Equal(4095, KrFacetFile.TileIndex(63, 63));

        Assert.Equal((0, 1), KrFacetFile.TileCoordinates(1));
        Assert.Equal((1, 0), KrFacetFile.TileCoordinates(64));

        for (int i = 0; i < 4096; i++)
        {
            (int x, int y) = KrFacetFile.TileCoordinates(i);
            Assert.Equal(i, KrFacetFile.TileIndex(x, y));
        }
    }

    [Fact]
    public void SectorsAreOrderedColumnMajor()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrFacetFile file = KrFacetFile.Open(Path.Combine(root, KrFacetFile.PackageName(0)), 0);

        // Felucca: 7168 x 4096, so 112 sectors across and 64 down. The client
        // divides the index by the count down the height, so stepping the
        // index by one moves south, not east.
        Assert.Equal(112, file.SectorsX);
        Assert.Equal(64, file.SectorsY);

        Assert.Equal(0, file.SectorIndex(0, 0));
        Assert.Equal(1, file.SectorIndex(0, 1));
        Assert.Equal(64, file.SectorIndex(1, 0));
        Assert.Equal(file.SectorCount - 1, file.SectorIndex(file.SectorsX - 1, file.SectorsY - 1));

        Assert.Equal((0, 1), file.SectorCoordinates(1));
        Assert.Equal((1, 0), file.SectorCoordinates(64));

        for (int sector = 0; sector < file.SectorCount; sector += 997)
        {
            (int x, int y) = file.SectorCoordinates(sector);
            Assert.Equal(sector, file.SectorIndex(x, y));
        }

        // Sector 333 begins at (320, 832): 333 / 64 = 5 across, 333 % 64 = 13 down.
        Assert.Equal((320, 832), file.WorldCoordinates(333, 0));
        Assert.Equal((321, 832), file.WorldCoordinates(333, KrFacetFile.TileIndex(1, 0)));
    }

    [Fact]
    public void BuildsTheDocumentedNamePatterns()
    {
        Assert.Equal("build/sectors/facet_00/00000000.bin", KrFacetFile.SectorName(0, 0));
        Assert.Equal("build/sectors/facet_03/00001280.bin", KrFacetFile.SectorName(3, 1280));
        Assert.Equal("facet0.uop", KrFacetFile.PackageName(0));

        // Terrain definitions are the one pattern that is not zero-padded.
        Assert.Equal("build/terraindefinition/0.bin", KrTerrainDefinition.EntryName(0));
        Assert.Equal("build/terraindefinition/190.bin", KrTerrainDefinition.EntryName(190));
    }

    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, "facet0.uop")));
    }

    private static string? Find2DInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UO_PATH")];

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, "map0.mul")));
    }

    [Fact]
    public void RealFacetHeadersMatchTheMapSizes()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        (int Facet, int Width, int Height, int Count)[] expected =
        [
            (0, 7168, 4096, 7168),
            (1, 7168, 4096, 7168),
            (2, 2304, 1600, 900),
            (3, 2560, 2048, 1280),
            (4, 1448, 1448, 529)
        ];

        foreach ((int facet, int width, int height, int count) in expected)
        {
            using KrFacetFile file = KrFacetFile.Open(Path.Combine(root, KrFacetFile.PackageName(facet)), facet);

            Assert.Equal(width, file.Width);
            Assert.Equal(height, file.Height);
            Assert.Equal(count, file.SectorCount);
        }
    }

    /// <summary>
    /// Every sector consumes to exactly its last byte. Facets 2-4 are swept in
    /// full; the two 7,168-sector maps are sampled, since kr-asset-tool's
    /// verify-facets does the exhaustive run.
    /// </summary>
    [Fact]
    public void RealSectorsParseExactly()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        int parsed = 0;

        for (int facet = 0; facet <= 4; facet++)
        {
            using KrFacetFile file = KrFacetFile.Open(Path.Combine(root, KrFacetFile.PackageName(facet)), facet);

            int step = file.SectorCount > 3000 ? 11 : 1;

            for (int sector = 0; sector < file.SectorCount; sector += step)
            {
                byte[]? data = file.ReadSector(sector);

                Assert.NotNull(data);
                Assert.True(
                    KrFacetFile.TryParseSector(data, out KrSector result),
                    $"facet {facet} sector {sector} did not parse");

                Assert.Equal(facet, result.Facet);
                Assert.Equal(sector, result.Index);

                parsed++;
            }
        }

        Assert.True(parsed > 3000, $"only {parsed} sectors were read");
    }

    /// <summary>
    /// Terrain seams only ever sit on a sector edge - that is what makes them
    /// seams - while statics are spread through the interior.
    /// </summary>
    [Fact]
    public void SeamsSitOnSectorEdgesAndStaticsDoNot()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrFacetFile file = KrFacetFile.Open(Path.Combine(root, KrFacetFile.PackageName(0)), 0);

        int seamsOnEdge = 0;
        int seamsInside = 0;
        int staticsInside = 0;

        for (int sector = 0; sector < file.SectorCount; sector += 101)
        {
            if (!file.TryParseSector(sector, out KrSector result))
            {
                continue;
            }

            for (int i = 0; i < result.Tiles.Length; i++)
            {
                (int x, int y) = KrFacetFile.TileCoordinates(i);

                bool onEdge = x == 0 || y == 0 ||
                    x == KrFacetFile.SectorSize - 1 || y == KrFacetFile.SectorSize - 1;

                if (onEdge)
                {
                    seamsOnEdge += result.Tiles[i].SeamCount;
                }
                else
                {
                    seamsInside += result.Tiles[i].SeamCount;
                    staticsInside += result.Tiles[i].StaticCount;
                }
            }
        }

        Assert.True(seamsOnEdge > 0);
        Assert.Equal(0, seamsInside);
        Assert.True(staticsInside > 0, "statics should not be confined to sector edges");
    }

    /// <summary>
    /// The decisive check on the land record and on both column-major orderings:
    /// the land tile id and z of every tile must equal the 2D client's. Reading
    /// the tiles row-major transposes each sector and this fails.
    /// </summary>
    [Fact]
    public void LandLayerMatchesThe2DMap()
    {
        string? root = FindInstall();
        string? uo = Find2DInstall();

        if (root is null || uo is null)
        {
            return;
        }

        using KrFacetFile file = KrFacetFile.Open(Path.Combine(root, KrFacetFile.PackageName(0)), 0);
        using FileStream map = File.OpenRead(Path.Combine(uo, "map0.mul"));

        int blocksDown = file.Height / 8;
        byte[] block = new byte[192];
        int compared = 0;

        for (int sector = 0; sector < file.SectorCount; sector += 331)
        {
            Assert.True(file.TryParseSector(sector, out KrSector result));

            for (int i = 0; i < result.Tiles.Length; i += 7)
            {
                (int x, int y) = file.WorldCoordinates(sector, i);

                long blockIndex = ((x / 8) * (long)blocksDown) + (y / 8);
                map.Seek((blockIndex * 196) + 4, SeekOrigin.Begin);
                map.ReadExactly(block);

                int cell = (((y % 8) * 8) + (x % 8)) * 3;

                ushort landTileId = BinaryPrimitives.ReadUInt16LittleEndian(block.AsSpan(cell));
                sbyte z = (sbyte)block[cell + 2];

                Assert.Equal(landTileId, result.Tiles[i].LandTileId);
                Assert.Equal(z, result.Tiles[i].Z);

                compared++;
            }
        }

        Assert.True(compared > 5000, $"only {compared} tiles were compared");
    }

    /// <summary>
    /// The 9-byte records are the same statics the 2D client draws: the
    /// (art id, z) list of a tile matches statics0.mul.
    /// </summary>
    [Fact]
    public void StaticsMatchThe2DStatics()
    {
        string? root = FindInstall();
        string? uo = Find2DInstall();

        if (root is null || uo is null || !File.Exists(Path.Combine(uo, "statics0.mul")))
        {
            return;
        }

        using KrFacetFile file = KrFacetFile.Open(Path.Combine(root, KrFacetFile.PackageName(0)), 0);
        using FileStream index = File.OpenRead(Path.Combine(uo, "staidx0.mul"));
        using FileStream statics = File.OpenRead(Path.Combine(uo, "statics0.mul"));

        int blocksDown = file.Height / 8;
        byte[] entry = new byte[12];
        int tilesChecked = 0;

        for (int sector = 0; sector < file.SectorCount; sector += 331)
        {
            Assert.True(file.TryParseSector(sector, out KrSector result));

            for (int i = 0; i < result.Tiles.Length; i++)
            {
                if (result.Tiles[i].StaticCount == 0)
                {
                    continue;
                }

                (int x, int y) = file.WorldCoordinates(sector, i);

                long blockIndex = ((x / 8) * (long)blocksDown) + (y / 8);
                index.Seek(blockIndex * 12, SeekOrigin.Begin);
                index.ReadExactly(entry);

                int lookup = BinaryPrimitives.ReadInt32LittleEndian(entry);
                int length = BinaryPrimitives.ReadInt32LittleEndian(entry.AsSpan(4));

                if (lookup < 0 || length <= 0)
                {
                    continue;
                }

                byte[] data = new byte[length];
                statics.Seek(lookup, SeekOrigin.Begin);
                statics.ReadExactly(data);

                List<(ushort Id, sbyte Z)> expected = [];

                for (int o = 0; o + 7 <= length; o += 7)
                {
                    if (data[o + 2] == x % 8 && data[o + 3] == y % 8)
                    {
                        expected.Add((BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(o)), (sbyte)data[o + 4]));
                    }
                }

                List<(ushort Id, sbyte Z)> actual = [];

                foreach (KrStatic s in result.StaticsFor(i))
                {
                    actual.Add(((ushort)s.ArtId, s.Z));
                }

                Assert.Equal(expected.Order(), actual.Order());

                tilesChecked++;
            }
        }

        Assert.True(tilesChecked > 100, $"only {tilesChecked} tiles carried statics");
    }
}
