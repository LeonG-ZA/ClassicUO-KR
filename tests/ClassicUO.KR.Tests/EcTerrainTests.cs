// SPDX-License-Identifier: BSD-2-Clause
//
// The Enhanced Client's terrain: its facet layout, its TerrainDefinition
// records and legacyterrainmap.csv. The live tests
// no-op without an EC install; point EC_PATH at one to run them elsewhere.

using System.Buffers.Binary;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class EcTerrainTests
{
    private const string Csv = "legacyId,newId,newSubType\r\n1,0,1\r\n3,1,0\r\n168,5,0\r\n";

    private static string? FindEc()
    {
        string?[] candidates =
        [
            Environment.GetEnvironmentVariable("EC_PATH")
        ];

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, "facet0.uop")));
    }

    private static string? FindKr()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, "facet0.uop")));
    }

    [Fact]
    public void LegacyTerrainMapReadsRows()
    {
        KrLegacyTerrainMap map = KrLegacyTerrainMap.Parse(Csv);

        Assert.Equal(3, map.Count);
        Assert.Equal(1, map.TerrainFor(3));
        Assert.Equal(5, map.TerrainFor(168));
        Assert.Equal(1, map.SubTypeFor(1));
        Assert.Equal(-1, map.TerrainFor(2));
    }

    [Fact]
    public void EnhancedSectorMapsLandIdsToTerrains()
    {
        // 4096 five-byte land records; tile 0 has one seam on land 168 and
        // one static, every other tile is land 3 with nothing on it.
        List<byte> data = [0, 7, 0];

        for (int i = 0; i < 64 * 64; i++)
        {
            data.Add(unchecked((byte)-5));
            data.AddRange(BitConverter.GetBytes((ushort)3));
            data.Add(i == 0 ? (byte)1 : (byte)0);
            data.Add(0);

            if (i == 0)
            {
                data.Add(2);
                data.AddRange(BitConverter.GetBytes(168u));
                data.Add(1); // the last seam carries the static count

                byte[] st = new byte[KrStatic.RecordSize];
                BinaryPrimitives.WriteUInt32LittleEndian(st, 6000u);
                data.AddRange(st);
            }
        }

        KrLegacyTerrainMap map = KrLegacyTerrainMap.Parse(Csv);

        Assert.True(KrFacetFile.TryParseSector(data.ToArray(), out KrSector sector, map));
        Assert.Equal(7, sector.Index);
        Assert.Equal(-5, sector.Tiles[0].Z);
        Assert.Equal(3, sector.Tiles[0].LandTileId);
        Assert.Equal(1, sector.Tiles[0].TerrainId);
        Assert.Equal(5u, sector.SeamsFor(0)[0].TerrainId);
        Assert.Equal(6000u, sector.StaticsFor(0)[0].ArtId);

        // The same bytes are not a KR sector.
        Assert.False(KrFacetFile.TryParseSector(data.ToArray(), out _));
    }

    [Fact]
    public void EnhancedTerrainDefinitionsAllParse()
    {
        string? ec = FindEc();

        if (ec is null)
        {
            return;
        }

        using UopFile file = UopFile.Open(Path.Combine(ec, "TerrainDefinition.uop"));

        List<KrTerrainDefinition> all = KrTerrainDefinition.LoadAll(file, enhanced: true);

        Assert.Equal(212, all.Count);
        Assert.All(all, d => Assert.InRange(d.Id, 0, 211));
    }

    [Fact]
    public void EnhancedGroundTilesBuild()
    {
        string? ec = FindEc();

        if (ec is null)
        {
            return;
        }

        KrTerrainMap map = KrTerrainMap.FromLegacy(
            KrLegacyTerrainMap.Load(Path.Combine(ec, KrLegacyTerrainMap.PackageName)));

        using KrAssetStore store = new(ec);
        KrTerrainArt art = new(store) { Enhanced = true };

        // Every terrain the table uses that has a definition must yield a
        // ground tile from EC's own strings and textures.
        HashSet<int> terrains = [];

        for (int g = 0; g < KrTerrainMap.LandGraphicCount; g++)
        {
            if (map.TryGet(g, out ushort t) && art.Definition(t) is not null)
            {
                terrains.Add(t);
            }
        }

        Assert.True(terrains.Count > 150, $"only {terrains.Count} terrains have definitions");

        List<int> missing = terrains.Where(t => art.GroundTile(t, 44) is null).Order().ToList();

        Assert.True(missing.Count == 0, $"no ground tile for terrains {string.Join(", ", missing)}");
    }

    [Fact]
    public void EnhancedFacetMatchesKrTerrain()
    {
        string? ec = FindEc();
        string? kr = FindKr();

        if (ec is null || kr is null)
        {
            return;
        }

        KrLegacyTerrainMap map = KrLegacyTerrainMap.Load(Path.Combine(ec, KrLegacyTerrainMap.PackageName));

        Assert.True(map.Count > 4000);

        using KrFacetFile ecFacet = KrFacetFile.Open(Path.Combine(ec, "facet0.uop"), 0, map);
        using KrFacetFile krFacet = KrFacetFile.Open(Path.Combine(kr, "facet0.uop"), 0);

        Assert.Equal(krFacet.Width, ecFacet.Width);
        Assert.Equal(krFacet.Height, ecFacet.Height);

        long same = 0, agree = 0, unmapped = 0;
        int parsed = 0;

        // Every 7th sector keeps the test quick while crossing the whole map.
        for (int s = 0; s < ecFacet.SectorCount; s += 7)
        {
            if (!ecFacet.TryParseSector(s, out KrSector e))
            {
                continue;
            }

            parsed++;

            if (!krFacet.TryParseSector(s, out KrSector k))
            {
                continue;
            }

            for (int i = 0; i < e.Tiles.Length; i++)
            {
                if (map.TerrainFor(e.Tiles[i].LandTileId) < 0)
                {
                    unmapped++;
                }

                if (e.Tiles[i].LandTileId == k.Tiles[i].LandTileId)
                {
                    same++;
                    agree += e.Tiles[i].TerrainId == k.Tiles[i].TerrainId ? 1 : 0;
                }
            }
        }

        Assert.True(parsed > 300, $"only {parsed} EC sectors parsed");
        Assert.Equal(0, unmapped);
        // The rest are EC's regroupings (cave transitions, ornate tiles): 96.7%
        // on this sample, 98.7% over the first 3,000 sectors.
        Assert.True(agree > same * 0.95, $"{agree} of {same} tiles agree with KR's terrain");
    }
}
