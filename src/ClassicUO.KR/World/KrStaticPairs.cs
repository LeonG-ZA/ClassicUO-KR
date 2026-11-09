// SPDX-License-Identifier: BSD-2-Clause
//
// Statics that Kingdom Reborn always draws together.
//
// KR composes a tree from two statics on the same tile: 3286 red_cedar_TRUNK
// is a bare trunk with branches, 3287 red_cedar_LEAVES is the crown, and laid
// over each other they make one tree. The 2D map carries only one static per
// tree tile, because in art.mul each of those ids is already a whole tree, so
// drawing KR's art for whichever id the 2D map happens to hold gives half of
// KR's tree - and when that half is the trunk, the tree has no leaves.
//
// The pairing is not guessed at. Sweeping a facet and counting which art ids
// share a tile shows it outright: over facet 0, art 3286 appears on 14,863
// tiles and 3287 is on every one of them, while the next most frequent
// companion of either is under 1 %.

using System;
using System.Collections.Generic;

namespace ClassicUO.KR.World;

public sealed class KrStaticPairs
{
    /// <summary>Art ids run to the same limit as the 2D static graphics.</summary>
    public const int GraphicCount = 0x4000;

    /// <summary>No companion.</summary>
    public const ushort None = ushort.MaxValue;

    /// <summary>
    /// How much of an art's tiles a companion must share before the two count
    /// as one object. The real pairs sit at 99.9 % and above and the next
    /// candidate is under 1 %, so there is nothing near this line.
    /// </summary>
    public const double ShareThreshold = 0.9;

    /// <summary>
    /// How many placements an art needs before it is considered at all. A
    /// handful of co-located statics somewhere on the map is a coincidence,
    /// not a composed object.
    /// </summary>
    public const int MinimumPlacements = 100;

    private readonly ushort[] _companion;

    private KrStaticPairs(ushort[] companion, int paired, long placements)
    {
        _companion = companion;
        Paired = paired;
        Placements = placements;
    }

    /// <summary>How many art ids have a companion.</summary>
    public int Paired { get; }

    /// <summary>How many static placements were swept.</summary>
    public long Placements { get; }

    /// <summary>The art KR always draws with this one, or false when there is none.</summary>
    public bool TryGet(int graphic, out ushort companion)
    {
        companion = None;

        if (graphic < 0 || graphic >= GraphicCount)
        {
            return false;
        }

        companion = _companion[graphic];

        return companion != None;
    }

    /// <summary>Sweeps the facets counting which art ids share a tile.</summary>
    public static KrStaticPairs Build(string krPath, ReadOnlySpan<int> facets, KrLegacyTerrainMap? enhanced = null)
    {
        int[] tiles = new int[GraphicCount];

        // Only pairs matter, and a tile rarely holds many statics, so the
        // counts are kept sparsely rather than as a 16k x 16k matrix.
        Dictionary<(ushort A, ushort B), int> shared = [];

        long placements = 0;

        List<ushort> present = [];

        foreach (int facet in facets)
        {
            string path = System.IO.Path.Combine(krPath, KrFacetFile.PackageName(facet));

            if (!System.IO.File.Exists(path))
            {
                continue;
            }

            using KrFacetFile file = KrFacetFile.Open(path, facet, enhanced);

            for (int sector = 0; sector < file.SectorCount; sector++)
            {
                if (!file.TryParseSector(sector, out KrSector data))
                {
                    continue;
                }

                for (int tile = 0; tile < 4096; tile++)
                {
                    ReadOnlySpan<KrStatic> statics = data.StaticsFor(tile);

                    if (statics.Length == 0)
                    {
                        continue;
                    }

                    present.Clear();

                    foreach (KrStatic s in statics)
                    {
                        if (s.ArtId >= GraphicCount)
                        {
                            continue;
                        }

                        ushort art = (ushort)s.ArtId;

                        if (!present.Contains(art))
                        {
                            present.Add(art);
                        }
                    }

                    foreach (ushort art in present)
                    {
                        tiles[art]++;
                        placements++;
                    }

                    for (int i = 0; i < present.Count; i++)
                    {
                        for (int j = 0; j < present.Count; j++)
                        {
                            if (i == j)
                            {
                                continue;
                            }

                            (ushort, ushort) key = (present[i], present[j]);

                            shared.TryGetValue(key, out int had);
                            shared[key] = had + 1;
                        }
                    }
                }
            }
        }

        ushort[] companion = new ushort[GraphicCount];
        Array.Fill(companion, None);

        int[] best = new int[GraphicCount];

        foreach (((ushort a, ushort b), int count) in shared)
        {
            if (tiles[a] < MinimumPlacements || count <= best[a])
            {
                continue;
            }

            if (count < tiles[a] * ShareThreshold)
            {
                continue;
            }

            best[a] = count;
            companion[a] = b;
        }

        int paired = 0;

        for (int i = 0; i < GraphicCount; i++)
        {
            if (companion[i] != None)
            {
                paired++;
            }
        }

        return new KrStaticPairs(companion, paired, placements);
    }
}
