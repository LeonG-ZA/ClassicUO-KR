// SPDX-License-Identifier: BSD-2-Clause
// data/gamedata/legacyterrainmap.csv, inside the Enhanced Client's
// GameData.uop: the table that turns a 2D land tile id into a terrain
// definition id.
//
// KR stores the terrain id in every facet record. EC dropped it and stores
// only the land tile id (see KrFacetFile), so this table is what gives an EC
// sector its terrains.
//
// Text, one header line "legacyId,newId,newSubType", then 4,162 rows. newId
// is the TerrainDefinition id. newSubType is 0, 1 or 2; its meaning is not
// established and it is kept but unused.
//
// Checked against KR's own facets: for tiles where both maps carry the same
// land id, newId equals KR's terrain id for 98.7% of tiles and seams. The
// rest are EC regroupings (KR's 13 Cave_Transition terrains become one, the
// blue slate ornate tiles become 89).

using System.Globalization;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.World;

public sealed class KrLegacyTerrainMap
{
    public const string PackageName = "GameData.uop";
    public const string EntryName = "data/gamedata/legacyterrainmap.csv";

    private readonly Dictionary<int, (int Terrain, int SubType)> _rows;

    private KrLegacyTerrainMap(Dictionary<int, (int Terrain, int SubType)> rows) => _rows = rows;

    /// <summary>How many land ids the table maps.</summary>
    public int Count => _rows.Count;

    /// <summary>The terrain definition id for a land tile id, or -1 when the table has no row for it.</summary>
    public int TerrainFor(int landTileId) => _rows.TryGetValue(landTileId, out var row) ? row.Terrain : -1;

    /// <summary>The row's newSubType, or -1 when the table has no row for it.</summary>
    public int SubTypeFor(int landTileId) => _rows.TryGetValue(landTileId, out var row) ? row.SubType : -1;

    public static KrLegacyTerrainMap Parse(string text)
    {
        Dictionary<int, (int, int)> rows = [];

        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();

            if (line.Length == 0 || !char.IsAsciiDigit(line[0]))
            {
                continue; // header, blank line
            }

            string[] f = line.Split(',');

            if (f.Length < 2
                || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int legacy)
                || !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int terrain))
            {
                throw new InvalidDataException($"legacyterrainmap.csv: bad row '{line}'.");
            }

            int subType = f.Length > 2 && int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int st)
                ? st
                : 0;

            rows[legacy] = (terrain, subType);
        }

        return new KrLegacyTerrainMap(rows);
    }

    /// <summary>Reads the table out of an Enhanced Client's GameData.uop.</summary>
    public static KrLegacyTerrainMap Load(string gameDataPath)
    {
        using UopFile file = UopFile.Open(gameDataPath);

        byte[] data = file.Read(EntryName)
            ?? throw new InvalidDataException($"'{gameDataPath}' has no {EntryName}.");

        return Parse(System.Text.Encoding.Latin1.GetString(data));
    }
}
