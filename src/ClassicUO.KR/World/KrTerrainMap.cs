// SPDX-License-Identifier: BSD-2-Clause
// Which KR terrain id goes with a classic 2D land tile id.
//
// A sector's land record carries both (see KrFacetFile), so the table is built
// by sweeping the facet packages once and tallying the pairs.
//
// The mapping is very nearly a function. Over 5,382,144 tiles sampled across
// all five facets, 1,058 of the 1,059 land tile ids seen map to exactly ONE
// terrain id, and 99.9984% of tiles carry their land tile id's most common
// terrain. The single exception is land tile 2446, which is terrain 60 on 134
// tiles and terrain 47 on 84. So a client that only knows the 2D land tile id
// - which is all ClassicUO's renderer has at the point it draws a tile - can
// pick the right KR terrain for all but a rounding error of the world.
//
// Only the land record's first eight bytes are touched here; seams and statics
// are stepped over rather than parsed, which keeps the sweep to one pass over
// the inflated bytes with no allocation per tile.

using System.Buffers.Binary;

namespace ClassicUO.KR.World;

public sealed class KrTerrainMap
{
    /// <summary>Land tile ids run 0..0x3FFF in the 2D client.</summary>
    public const int LandGraphicCount = 0x4000;

    /// <summary>Returned for a land tile id the sweep never saw.</summary>
    public const ushort Unknown = ushort.MaxValue;

    private readonly ushort[] _terrain;
    private readonly int[] _count;

    private KrTerrainMap(ushort[] terrain, int[] count, int known, int ambiguous, long tiles)
    {
        _terrain = terrain;
        _count = count;
        Known = known;
        Ambiguous = ambiguous;
        TilesScanned = tiles;
    }

    /// <summary>How many land tile ids the sweep found a terrain for.</summary>
    public int Known { get; }

    /// <summary>How many land tile ids carried more than one terrain id.</summary>
    public int Ambiguous { get; }

    public long TilesScanned { get; }

    /// <summary>The KR terrain id for a land tile id, or <see cref="Unknown"/>.</summary>
    public ushort this[int landGraphic] =>
        (uint)landGraphic < LandGraphicCount ? _terrain[landGraphic] : Unknown;

    /// <summary>How many tiles of this land tile id the sweep saw.</summary>
    public int TileCount(int landGraphic) =>
        (uint)landGraphic < LandGraphicCount ? _count[landGraphic] : 0;

    public bool TryGet(int landGraphic, out ushort terrainId)
    {
        terrainId = this[landGraphic];

        return terrainId != Unknown;
    }

    /// <summary>
    /// The Enhanced Client's table: its facets carry no terrain id to sweep,
    /// so every land id the CSV lists is taken as is. Tile counts are 0.
    /// </summary>
    public static KrTerrainMap FromLegacy(KrLegacyTerrainMap legacy)
    {
        ushort[] terrain = new ushort[LandGraphicCount];
        Array.Fill(terrain, Unknown);
        int known = 0;

        for (int graphic = 0; graphic < LandGraphicCount; graphic++)
        {
            int t = legacy.TerrainFor(graphic);

            if (t is >= 0 and < Unknown)
            {
                terrain[graphic] = (ushort)t;
                known++;
            }
        }

        return new KrTerrainMap(terrain, new int[LandGraphicCount], known, 0, 0);
    }

    /// <summary>
    /// Sweeps the facet packages and tallies land tile id against terrain id.
    /// Facets that are not present are skipped.
    /// </summary>
    public static KrTerrainMap Build(string krPath, ReadOnlySpan<int> facets)
    {
        // Top two terrains per land tile id. Nothing in the shipped data needs
        // a third, and a third would only cost the tie-break, not correctness.
        ushort[] first = new ushort[LandGraphicCount];
        ushort[] second = new ushort[LandGraphicCount];
        int[] firstCount = new int[LandGraphicCount];
        int[] secondCount = new int[LandGraphicCount];

        Array.Fill(first, Unknown);
        Array.Fill(second, Unknown);

        long tiles = 0;

        foreach (int facet in facets)
        {
            string path = Path.Combine(krPath, KrFacetFile.PackageName(facet));

            if (!File.Exists(path))
            {
                continue;
            }

            using KrFacetFile file = KrFacetFile.Open(path, facet);

            for (int sector = 0; sector < file.SectorCount; sector++)
            {
                byte[]? data = file.ReadSector(sector);

                if (data is null)
                {
                    continue;
                }

                tiles += Tally(data, first, second, firstCount, secondCount);
            }
        }

        ushort[] terrain = new ushort[LandGraphicCount];
        int[] count = new int[LandGraphicCount];
        Array.Fill(terrain, Unknown);

        int known = 0;
        int ambiguous = 0;

        for (int i = 0; i < LandGraphicCount; i++)
        {
            if (first[i] == Unknown)
            {
                continue;
            }

            known++;

            if (second[i] != Unknown)
            {
                ambiguous++;
            }

            terrain[i] = secondCount[i] > firstCount[i] ? second[i] : first[i];
            count[i] = firstCount[i] + secondCount[i];
        }

        return new KrTerrainMap(terrain, count, known, ambiguous, tiles);
    }

    private static int Tally(
        ReadOnlySpan<byte> data,
        ushort[] first,
        ushort[] second,
        int[] firstCount,
        int[] secondCount)
    {
        int offset = 3;
        int seen = 0;

        for (int i = 0; i < 64 * 64; i++)
        {
            if (offset + KrLandTile.RecordSize > data.Length)
            {
                break;
            }

            ushort terrain = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 1));
            ushort graphic = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset + 4));

            int seams = data[offset + 6];
            int statics = data[offset + 7];

            offset += KrLandTile.RecordSize;

            for (int s = 0; s < seams; s++)
            {
                if (offset + KrTerrainSeam.RecordSize > data.Length)
                {
                    return seen;
                }

                statics = data[offset + 5];
                offset += KrTerrainSeam.RecordSize;
            }

            offset += statics * KrStatic.RecordSize;

            if (offset > data.Length)
            {
                return seen;
            }

            if (graphic >= LandGraphicCount)
            {
                continue;
            }

            seen++;

            if (first[graphic] == terrain)
            {
                firstCount[graphic]++;
            }
            else if (first[graphic] == Unknown)
            {
                first[graphic] = terrain;
                firstCount[graphic] = 1;
            }
            else if (second[graphic] == terrain)
            {
                secondCount[graphic]++;
            }
            else if (second[graphic] == Unknown)
            {
                second[graphic] = terrain;
                secondCount[graphic] = 1;
            }
        }

        return seen;
    }
}
