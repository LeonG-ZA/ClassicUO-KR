// SPDX-License-Identifier: BSD-2-Clause
// facet0-4.uop: the KR world maps.
//
// A facet package holds one unnamed 8-byte entry - the facet header - plus one
// entry per sector, named build/sectors/facet_{facet:D2}/{sector:D8}.bin.
//
// Header (8 bytes): u32 height, u32 width, both in tiles. These match the 2D
// client's map sizes exactly:
//
//     facet 0, 1   7168 x 4096   Felucca / Trammel
//     facet 2      2304 x 1600   Ilshenar
//     facet 3      2560 x 2048   Malas
//     facet 4      1448 x 1448   Tokuno
//
// A sector is 64x64 tiles, and the sector grid is ceil(width/64) by
// ceil(height/64) - which gives exactly the entry count of every package
// (112*64 = 7168, 36*25 = 900, 40*32 = 1280, 23*23 = 529). Both the sector
// grid and the tiles within a sector are stored COLUMN-major: index = x * 64 + y.
//
// The facet dimensions: Felucca and Trammel 7168x4096,
// Ilshenar 2304x1600, Malas 2560x2048, Tokuno 1448x1448, then Gargoyle and
// Test, which the 2.53 assets do not ship.
//
// Sector payload:
//
//   header, 3 bytes
//     0x00 u8  the facet this sector belongs to
//     0x01 u16 the sector's own index
//   Both are self-checks the client itself makes - it carries the errors
//   "Fetched incorrect sector ID (bad .sector file?)" and "Fetched sector from
//   a foreign facet (bad .sector file?)" - and both hold in the shipped data.
//
//   then 4096 tiles, each:
//
//     land record, 8 bytes
//       0x00 s8  z
//       0x01 u16 KR terrain id, 0..190 - indexes build/terraindefinition/N.bin
//       0x03 u8  flag, only ever 0 or 1
//       0x04 u16 the classic 2D land tile id
//       0x06 u8  number of terrain seams that follow
//       0x07 u8  trailing byte; the static count when there are no seams
//
//     then `seam count` seam records, 6 bytes each
//       0x00 s8  z
//       0x01 u32 KR terrain id, 0..190
//       0x05 u8  trailing byte; the static count on the LAST seam
//
//     then `static count` static records, 9 bytes each
//       0x00 u32 art id
//       0x04 s8  z
//       0x05 u32 hue
//
// The static count is the trailing byte of the last record read before the
// static list - the land record's byte 7 when the seam list is empty, else the
// last seam's byte 5. On a non-final seam that byte is never 0 (1,071,346
// samples) and on a final one it is the count, so it reads as an overloaded
// "another seam follows" marker; its value on non-final seams is not decoded.
//
// How this was checked, against a 2.53 install and a 2D client:
//
//   * All 17,045 sectors of all five facets parse to exactly their last byte.
//   * Land layer cross-checked against map0.mul over 557,056 tiles sampled
//     across facet 0: the u16 at 0x04 equals the 2D land tile id and the s8 at
//     0x00 equals the 2D z for EVERY tile, 100%. That also pins the
//     column-major tile order - the row-major reading transposes and fails.
//   * Static records cross-checked against statics0.mul: on 3,989 tiles the
//     (art id, z) lists match exactly, with no disagreement, so the 9-byte
//     record is the same static the 2D client draws.
//   * Seams really are seams: over 167,343 of them, every single one sits on a
//     sector edge and not one in the interior. Statics are the opposite -
//     242,998 interior against 16,648 on an edge.
//
//

using System.Buffers.Binary;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.World;

/// <summary>One tile of a sector's land layer.</summary>
public readonly struct KrLandTile
{
    public const int RecordSize = 8;

    /// <summary>Height, and equal to the 2D client's z for the same tile.</summary>
    public readonly sbyte Z;

    /// <summary>KR terrain id, 0..190; indexes <c>build/terraindefinition/N.bin</c>.</summary>
    public readonly ushort TerrainId;

    /// <summary>Only ever 0 or 1 in the shipped data; meaning not established.</summary>
    public readonly byte Flag;

    /// <summary>The classic 2D land tile id, identical to map0-4.mul.</summary>
    public readonly ushort LandTileId;

    /// <summary>How many terrain seams this tile carries.</summary>
    public readonly byte SeamCount;

    /// <summary>How many statics this tile carries.</summary>
    public readonly byte StaticCount;

    public KrLandTile(sbyte z, ushort terrainId, byte flag, ushort landTileId, byte seamCount, byte staticCount)
    {
        Z = z;
        TerrainId = terrainId;
        Flag = flag;
        LandTileId = landTileId;
        SeamCount = seamCount;
        StaticCount = staticCount;
    }
}

/// <summary>
/// A neighbouring terrain carried on a sector edge, so the renderer can blend
/// across the seam without loading the adjacent sector.
/// </summary>
public readonly struct KrTerrainSeam
{
    public const int RecordSize = 6;

    public readonly sbyte Z;
    public readonly uint TerrainId;

    /// <summary>
    /// Byte 5. On the last seam of a tile this is the static count; on any
    /// earlier one it is non-zero and not decoded.
    /// </summary>
    public readonly byte Trailing;

    public KrTerrainSeam(sbyte z, uint terrainId, byte trailing)
    {
        Z = z;
        TerrainId = terrainId;
        Trailing = trailing;
    }
}

/// <summary>A static item, the same one the 2D client reads from statics*.mul.</summary>
public readonly struct KrStatic
{
    public const int RecordSize = 9;

    public readonly uint ArtId;
    public readonly sbyte Z;
    public readonly uint Hue;

    public KrStatic(uint artId, sbyte z, uint hue)
    {
        ArtId = artId;
        Z = z;
        Hue = hue;
    }
}

public sealed class KrSector
{
    private readonly int[] _seamStart;
    private readonly int[] _staticStart;

    internal KrSector(
        byte facet,
        ushort index,
        KrLandTile[] tiles,
        KrTerrainSeam[] seams,
        KrStatic[] statics,
        int[] seamStart,
        int[] staticStart)
    {
        Facet = facet;
        Index = index;
        Tiles = tiles;
        Seams = seams;
        Statics = statics;
        _seamStart = seamStart;
        _staticStart = staticStart;
    }

    /// <summary>The facet the payload says it belongs to.</summary>
    public byte Facet { get; }

    /// <summary>The sector index the payload carries; should equal the one asked for.</summary>
    public ushort Index { get; }

    /// <summary>4096 land tiles, column-major within the 64x64 sector.</summary>
    public KrLandTile[] Tiles { get; }

    /// <summary>Every seam in the sector, in tile order.</summary>
    public KrTerrainSeam[] Seams { get; }

    /// <summary>Every static in the sector, in tile order.</summary>
    public KrStatic[] Statics { get; }

    public ReadOnlySpan<KrTerrainSeam> SeamsFor(int tileIndex) =>
        Seams.AsSpan(_seamStart[tileIndex], Tiles[tileIndex].SeamCount);

    public ReadOnlySpan<KrStatic> StaticsFor(int tileIndex) =>
        Statics.AsSpan(_staticStart[tileIndex], Tiles[tileIndex].StaticCount);
}

public sealed class KrFacetFile : IDisposable
{
    /// <summary>A sector is 64x64 tiles.</summary>
    public const int SectorSize = 64;

    private const int HeaderSize = 3;
    private const int TilesPerSector = SectorSize * SectorSize;

    /// <summary>
    /// An Enhanced Client land tile: s8 z, u16 land id, u8 seam count, u8
    /// trailing byte. EC dropped KR's terrain id and flag; its seams carry a
    /// land id where KR's carry a terrain id.
    /// </summary>
    private const int EnhancedLandRecordSize = 5;

    private readonly UopFile _file;
    private readonly KrLegacyTerrainMap? _enhanced;

    private KrFacetFile(UopFile file, int facet, int width, int height, KrLegacyTerrainMap? enhanced)
    {
        _file = file;
        _enhanced = enhanced;
        Facet = facet;
        Width = width;
        Height = height;
    }

    public int Facet { get; }

    /// <summary>Map width in tiles.</summary>
    public int Width { get; }

    /// <summary>Map height in tiles.</summary>
    public int Height { get; }

    public int SectorsX => (Width + SectorSize - 1) / SectorSize;

    public int SectorsY => (Height + SectorSize - 1) / SectorSize;

    public int SectorCount => SectorsX * SectorsY;

    /// <summary>
    /// Sector ordering is column-major: the client divides the sector index by
    /// the number of sectors down the HEIGHT, taking the quotient as the first
    /// coordinate and the remainder as the second.
    /// </summary>
    /// <remarks>
    /// The divisor comes from the height, not the width, which is what makes
    /// the obvious row-major reading wrong. Confirmed by the map0.mul
    /// cross-check described at the top of this file.
    /// </remarks>
    public int SectorIndex(int sectorX, int sectorY) => (sectorX * SectorsY) + sectorY;

    public (int X, int Y) SectorCoordinates(int sector) => (sector / SectorsY, sector % SectorsY);

    /// <summary>
    /// Tiles within a sector are column-major too: index = x * 64 + y. Pinned
    /// by the map0.mul cross-check - reading them row-major transposes every
    /// sector.
    /// </summary>
    public static int TileIndex(int x, int y) => (x * SectorSize) + y;

    public static (int X, int Y) TileCoordinates(int tileIndex) =>
        (tileIndex / SectorSize, tileIndex % SectorSize);

    /// <summary>Maps a sector and a tile within it to world tile coordinates.</summary>
    public (int X, int Y) WorldCoordinates(int sector, int tileIndex)
    {
        (int sx, int sy) = SectorCoordinates(sector);
        (int tx, int ty) = TileCoordinates(tileIndex);

        return ((sx * SectorSize) + tx, (sy * SectorSize) + ty);
    }

    public static string SectorName(int facet, int sector) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"build/sectors/facet_{facet:D2}/{sector:D8}.bin");

    public static string PackageName(int facet) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"facet{facet}.uop");

    /// <summary>Opens a facet package and reads its header.</summary>
    /// <summary>
    /// Opens a facet package. Pass <paramref name="enhanced"/> for an
    /// Enhanced Client package: its sectors are then read in EC's layout and
    /// their land ids turned into terrain ids through that table.
    /// </summary>
    public static KrFacetFile Open(string path, int facet, KrLegacyTerrainMap? enhanced = null)
    {
        UopFile file = UopFile.Open(path);

        try
        {
            // The header is the one entry with no name; find it by elimination
            // rather than by hash, so no name dictionary is needed.
            foreach (UopEntry entry in file.Entries)
            {
                if (entry.PayloadSize != 8)
                {
                    continue;
                }

                byte[] header = file.Read(entry);

                int height = BinaryPrimitives.ReadInt32LittleEndian(header);
                int width = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));

                if (width > 0 && height > 0 && width <= 1 << 20 && height <= 1 << 20)
                {
                    return new KrFacetFile(file, facet, width, height, enhanced);
                }
            }

            throw new InvalidDataException($"'{path}' has no facet header entry.");
        }
        catch
        {
            file.Dispose();

            throw;
        }
    }

    /// <summary>Reads a sector's raw, inflated payload, or null when absent.</summary>
    public byte[]? ReadSector(int sector) => _file.Read(SectorName(Facet, sector));

    /// <summary>
    /// Parses a sector. Returns false only for a payload that is truncated or
    /// does not consume to exactly its last byte; every sector in the shipped
    /// assets parses.
    /// </summary>
    public bool TryParseSector(int sector, out KrSector result)
    {
        result = null!;

        byte[]? data = ReadSector(sector);

        return data is not null && TryParseSector(data, out result, _enhanced);
    }

    /// <summary>
    /// Parses a sector. With <paramref name="enhanced"/> the payload is read in
    /// the Enhanced Client's layout, and every tile's and seam's land id is
    /// mapped to a terrain id; a land id the table lacks gets terrain 0.
    /// </summary>
    public static bool TryParseSector(ReadOnlySpan<byte> data, out KrSector result, KrLegacyTerrainMap? enhanced = null)
    {
        result = null!;

        if (data.Length < HeaderSize)
        {
            return false;
        }

        byte facet = data[0];
        ushort index = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(1));

        KrLandTile[] tiles = new KrLandTile[TilesPerSector];
        int[] seamStart = new int[TilesPerSector];
        int[] staticStart = new int[TilesPerSector];

        // One pass to lay out the tiles and count, a second to fill the flat
        // seam and static arrays once their sizes are known.
        int offset = HeaderSize;
        int seamTotal = 0;
        int staticTotal = 0;
        int landSize = enhanced is null ? KrLandTile.RecordSize : EnhancedLandRecordSize;

        for (int i = 0; i < TilesPerSector; i++)
        {
            if (offset + landSize > data.Length)
            {
                return false;
            }

            ReadOnlySpan<byte> r = data.Slice(offset);

            byte seams = r[landSize - 2];

            // The static count is the trailing byte of the last record before
            // the static list: the land record's when there is no seam.
            byte trailing = r[landSize - 1];

            offset += landSize;

            if (offset + (seams * KrTerrainSeam.RecordSize) > data.Length)
            {
                return false;
            }

            for (int s = 0; s < seams; s++)
            {
                trailing = data[offset + 5];
                offset += KrTerrainSeam.RecordSize;
            }

            if (offset + (trailing * KrStatic.RecordSize) > data.Length)
            {
                return false;
            }

            offset += trailing * KrStatic.RecordSize;

            if (enhanced is null)
            {
                tiles[i] = new KrLandTile(
                    (sbyte)r[0],
                    BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(1)),
                    r[3],
                    BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(4)),
                    seams,
                    trailing);
            }
            else
            {
                ushort land = BinaryPrimitives.ReadUInt16LittleEndian(r.Slice(1));

                tiles[i] = new KrLandTile((sbyte)r[0], EnhancedTerrain(enhanced, land), 0, land, seams, trailing);
            }

            seamStart[i] = seamTotal;
            staticStart[i] = staticTotal;
            seamTotal += seams;
            staticTotal += trailing;
        }

        // Landing exactly on the last byte is the check that the whole model
        // held for this sector.
        if (offset != data.Length)
        {
            return false;
        }

        KrTerrainSeam[] allSeams = new KrTerrainSeam[seamTotal];
        KrStatic[] allStatics = new KrStatic[staticTotal];

        offset = HeaderSize;

        for (int i = 0; i < TilesPerSector; i++)
        {
            offset += landSize;

            for (int s = 0; s < tiles[i].SeamCount; s++)
            {
                ReadOnlySpan<byte> r = data.Slice(offset);

                uint terrain = BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(1));

                if (enhanced is not null)
                {
                    terrain = EnhancedTerrain(enhanced, (int)Math.Min(terrain, int.MaxValue));
                }

                allSeams[seamStart[i] + s] = new KrTerrainSeam((sbyte)r[0], terrain, r[5]);

                offset += KrTerrainSeam.RecordSize;
            }

            for (int s = 0; s < tiles[i].StaticCount; s++)
            {
                ReadOnlySpan<byte> r = data.Slice(offset);

                allStatics[staticStart[i] + s] = new KrStatic(
                    BinaryPrimitives.ReadUInt32LittleEndian(r),
                    (sbyte)r[4],
                    BinaryPrimitives.ReadUInt32LittleEndian(r.Slice(5)));

                offset += KrStatic.RecordSize;
            }
        }

        result = new KrSector(facet, index, tiles, allSeams, allStatics, seamStart, staticStart);

        return true;
    }

    private static ushort EnhancedTerrain(KrLegacyTerrainMap map, int landTileId)
    {
        int terrain = map.TerrainFor(landTileId);

        return terrain is >= 0 and <= ushort.MaxValue ? (ushort)terrain : (ushort)0;
    }

    public void Dispose() => _file.Dispose();
}
