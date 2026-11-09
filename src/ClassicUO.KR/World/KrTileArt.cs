// SPDX-License-Identifier: BSD-2-Clause
//
// The fixed part of a tileart.uop record: where KR puts a static's art.
//
// Records are version 3 and variable length, but a fixed block near the start
// is the same in every one. Decoded by lining fields up against the art they
// describe, and checked across the whole file:
//
//   +0x00  u16  version, always 3
//   +0x06  u32  graphic id, equal to the record's own id in all 16,384
//   +0x43  i32  x of the near corner of the region this static draws
//   +0x47  i32  y of the near corner
//   +0x4B  i32  x of the far corner of KR's art in its texture
//   +0x4F  i32  y of the far corner - exact in 1,781 of 2,201 sampled,
//               within 2 px in 1,886
//   +0x53  i32  x of the texture's left edge, relative to the tile's left
//               corner, in KR pixels
//   +0x57  i32  y of the art's bottom, relative to the tile's bottom corner,
//               in KR pixels
//   +0x63  i32  the 2D sprite's width  } equal to art.mul's size in 13,599
//   +0x67  i32  the 2D sprite's height } of 13,606 records
//
// A static draws only the REGION (+0x43..+0x4F, inclusive) of a SOURCE
// texture. Usually the source is its own art, but a large object split over
// several tiles - a statue - is one texture shared between its slices, each
// drawing its own strip. The source is named by a string-dictionary reference
// later in the record (see SourceGraphic). Drawing the whole texture on every
// slice's tile, or 2D art on slices with no texture of their own, is what put
// two statues in one place.
//
// The Enhanced Client's records are version 4: the same fixed block with
// every field after the graphic id 10 bytes later (+0x4D for the region,
// +0x6D/+0x71 for the 2D size), and texture names under TileArtEnhanced// where KR's say TileArtLegacy\. Lined up against KR over 1,500 records,
// the 2D size matches in 97%.
//
// The two offsets are what KR anchors a static by. Scaled by 2/3 - the same
// factor found independently for KR mobiles - they put KR's art where the 2D
// client puts the same object to within about 2 px on both axes, over 2,867
// graphics compared.

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.World;

public readonly record struct KrTileArt(
    int Graphic,
    int RegionX,
    int RegionY,
    int CornerX,
    int CornerY,
    int OffsetX,
    int OffsetY,
    int Width2D,
    int Height2D)
{
    public const string PackageName = "tileart.uop";

    public const int MinimumLength = 0x6B;

    /// <summary>How much later the fixed fields sit in an Enhanced Client (version 4) record.</summary>
    public const int EnhancedShift = 10;

    /// <summary>Where the variable part of a record begins: past the fixed block, which is longer in version 4.</summary>
    public static int VariableStart(ReadOnlySpan<byte> data) =>
        data.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(data) == 4 ? MinimumLength + EnhancedShift : MinimumLength;

    public static string EntryName(int graphic) => $"build/tileart/{graphic:D8}.bin";

    /// <summary>
    /// Whether the fixed block describes this static's art at all.
    /// </summary>
    /// <remarks>
    /// 5,593 of 16,384 records give a region one pixel wide or tall - (0,0) to
    /// (0,0), or (0,0) to (0,59). They are mostly flat pieces: carpet, floor,
    /// water, lava, swamp, stone roof. KR evidently draws those another way;
    /// carpets share large textures (2749 and 2750 both draw from
    /// 00002750_carpet, 256x256), which suggests they are laid across the floor
    /// like terrain. Drawing the region literally gave a 1 px sliver per tile.
    /// </remarks>
    public bool HasRegion => CornerX - RegionX >= 2 && CornerY - RegionY >= 2;

    /// <summary>
    /// Parses the fixed block of a KR (version 3) or Enhanced Client (version
    /// 4) record, or returns null when the record is too short or another version.
    /// </summary>
    public static KrTileArt? Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < 2)
        {
            return null;
        }

        int k = BinaryPrimitives.ReadUInt16LittleEndian(data) switch
        {
            3 => 0,
            4 => EnhancedShift,
            _ => -1
        };

        if (k < 0 || data.Length < MinimumLength + k)
        {
            return null;
        }

        return new KrTileArt(
            BinaryPrimitives.ReadInt32LittleEndian(data[0x06..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x43 + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x47 + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x4B + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x4F + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x53 + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x57 + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x63 + k)..]),
            BinaryPrimitives.ReadInt32LittleEndian(data[(0x67 + k)..]));
    }

    /// <summary>
    /// The graphic whose texture this record draws from.
    /// </summary>
    /// <remarks>
    /// *Located, not parsed.* The variable part of the record is not decoded,
    /// so the reference is found as the first u32 after the fixed block that
    /// indexes a <c>TileArtLegacy\N</c> or <c>WorldArt\N_...</c> string with
    /// N within 64 of the record's own graphic. That finds one in 15,958 of
    /// 16,384 records; where the regions it gives were checked against the
    /// named texture, 1,094 of 1,109 lay inside it. Null when none is found.
    /// </remarks>
    public static int? SourceGraphic(ReadOnlySpan<byte> data, int graphic, KrStringDictionary strings)
    {
        // A record that names its own texture draws it, wherever it is named.
        // The first reference is sometimes another object entirely: 37 (a
        // stone wall) names 2, an ankh, first; 322 and 331 name 386, a
        // sandstone block. 336 records name themselves after something else,
        // and every one's region fits its own texture.
        // The KR sprite shader's first layer names the texture drawn.
        if (KrShader(data, strings) is { Class: "UOSpriteShader" } sprite
            && ArtNumber(sprite.Layers[0].Texture) is { } drawn && drawn < 0x10000)
        {
            return drawn;
        }

        List<int> all = References(data, graphic, strings);

        if (all.Contains(graphic))
        {
            return graphic;
        }

        for (int o = VariableStart(data); o + 4 <= data.Length; o++)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);

            if (index >= (uint)strings.Count)
            {
                continue;
            }

            int? n = TextureNumber(strings[(int)index]);

            if (n is { } v && Math.Abs(v - graphic) <= 64)
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>
    /// Every texture the record names, in order, without repeats - the same
    /// located reference as <see cref="SourceGraphic"/>, collected rather than
    /// stopping at the first.
    /// </summary>
    public static List<int> References(ReadOnlySpan<byte> data, int graphic, KrStringDictionary strings)
    {
        List<int> found = [];

        for (int o = VariableStart(data); o + 4 <= data.Length; o++)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);

            if (index >= (uint)strings.Count)
            {
                continue;
            }

            if (TextureNumber(strings[(int)index]) is { } v
                && Math.Abs(v - graphic) <= 64
                && !found.Contains(v))
            {
                found.Add(v);
            }
        }

        return found;
    }

    private static int? TextureNumber(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        foreach (string folder in (ReadOnlySpan<string>)["TileArtLegacy\\", "TileArtEnhanced\\", "WorldArt\\"])
        {
            int at = name.IndexOf(folder, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
            {
                continue;
            }

            int start = at + folder.Length;
            int end = start;

            while (end < name.Length && char.IsAsciiDigit(name[end]))
            {
                end++;
            }

            if (end > start && int.TryParse(name.AsSpan(start, end - start), out int value))
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>
    /// The KR terrain texture (a 0200xxxx art id, as a number) a floor piece
    /// is drawn from, or null. *Located, not parsed.*
    /// </summary>
    /// <remarks>
    /// Stone pavers 1309-1312 have no world art of their own; KR lays them as
    /// ground with 02000260_Stone_Medium_A, blue-grey square pavers. Record
    /// 1311 names two 0200 textures when scanned byte by byte: Cave_Floor_A
    /// (dictionary index 1941) and Stone_Medium_B (2433). Only the second
    /// sits beside the record's own source string (TileArtLegacy\1309, 2434),
    /// so the one nearest the source's string, within
    /// <see cref="TerrainStringReach"/>, is taken. The caller prefers its A
    /// sibling.
    /// </remarks>
    public static int? TerrainTexture(ReadOnlySpan<byte> data, int source, KrStringDictionary strings)
    {
        // The record's own name (+0x02) is a terrain texture for some floors:
        // cave floor 1339-1353 names 02000131_Cave_Floor_B there.
        if (data.Length >= 6 && TerrainArt(strings, BinaryPrimitives.ReadUInt32LittleEndian(data[2..])) is { } named)
        {
            return named;
        }

        int? sourceIndex = null;
        List<(int Index, int ArtId)> found = [];

        for (int o = VariableStart(data); o + 4 <= data.Length; o++)
        {
            uint index = BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);

            if (index >= (uint)strings.Count || strings[(int)index] is not { } name)
            {
                continue;
            }

            if (sourceIndex is null && TextureNumber(name) == source)
            {
                sourceIndex = (int)index;
            }

            int at = name.IndexOf("WorldArt\\0200", StringComparison.OrdinalIgnoreCase);

            if (at >= 0 && int.TryParse(name.AsSpan(at + 9, 8), out int art))
            {
                found.Add(((int)index, art));
            }
        }

        if (sourceIndex is not { } s)
        {
            return null;
        }

        int? best = null;
        int bestDistance = TerrainStringReach + 1;

        foreach ((int index, int art) in found)
        {
            int distance = Math.Abs(index - s);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = art;
            }
        }

        return best;
    }

    private static int? TerrainArt(KrStringDictionary strings, uint index)
    {
        if (index >= (uint)strings.Count || strings[(int)index] is not { } name)
        {
            return null;
        }

        int at = name.IndexOf("WorldArt\\0200", StringComparison.OrdinalIgnoreCase);

        return at >= 0 && int.TryParse(name.AsSpan(at + 9, 8), out int art) ? art : null;
    }

    /// <summary>How far, in dictionary indices, a floor's terrain name may sit from its source's.</summary>
    public const int TerrainStringReach = 8;

    /// <summary>One layer of a shader definition: a texture name and how many tiles it repeats over.</summary>
    public readonly record struct ShaderLayer(string Texture, float Scale, uint A, uint B);

    /// <summary>A shader definition: the shader class and its layers, in order.</summary>
    public sealed record ShaderDefinition(string Class, IReadOnlyList<ShaderLayer> Layers);

    /// <summary>
    /// The KR shader definition a record carries - the one whose layers name
    /// <c>WorldArt</c> textures; null when there is none.
    /// </summary>
    /// <remarks>
    /// *Verified.* A record holds two shader definitions in the layout the
    /// terrain parser reads: u8 present (1), u8 flag,
    /// u32 class string, u8 layer count, then per layer u32 texture string,
    /// u8 type, f32 scale, u32 a, u32 b. The first names KR art, the second
    /// the legacy art. A sprite is <c>UOSpriteShader</c> [texture x1,
    /// 01000045_noise x16] - the first layer is the texture it draws, which
    /// for a slice of a large object is the owner's (3416 names
    /// 00003417_Jungle_Tree_trunk, 4772 names 00004770_CatFamilyStatue). A
    /// floor is <c>UOStaticTerrainShader</c> with terrain-style layers
    /// (1311: Stone_Medium_A x4, Stone_Medium_B x4, noise_alpha x24).
    /// </remarks>
    public static ShaderDefinition? KrShader(ReadOnlySpan<byte> data, KrStringDictionary strings)
    {
        foreach (ShaderDefinition def in Shaders(data, strings))
        {
            if (def.Layers.Count > 0 && def.Layers[0].Texture.Contains("WorldArt\\", StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }

        return null;
    }

    /// <summary>Every shader definition block found in a record, in order.</summary>
    public static List<ShaderDefinition> Shaders(ReadOnlySpan<byte> data, KrStringDictionary strings)
    {
        List<ShaderDefinition> found = [];

        for (int o = 0; o + 7 <= data.Length; o++)
        {
            if (data[o] != 1)
            {
                continue;
            }

            uint cls = BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 2)..]);
            int count = data[o + 6];

            if (cls == 0 || cls >= (uint)strings.Count || count is 0 or > 8 || o + 7 + (count * 17) > data.Length
                || strings[(int)cls] is not { } className || !className.StartsWith("UO", StringComparison.Ordinal))
            {
                continue;
            }

            List<ShaderLayer> layers = [];
            bool ok = true;

            for (int l = 0; l < count && ok; l++)
            {
                ReadOnlySpan<byte> p = data[(o + 7 + (l * 17))..];
                uint tex = BinaryPrimitives.ReadUInt32LittleEndian(p);
                float scale = BinaryPrimitives.ReadSingleLittleEndian(p[5..]);

                ok = tex > 0 && tex < (uint)strings.Count && scale > 0 && scale <= 64 && strings[(int)tex] is { Length: > 0 };

                if (ok)
                {
                    layers.Add(new ShaderLayer(
                        strings[(int)tex]!, scale,
                        BinaryPrimitives.ReadUInt32LittleEndian(p[9..]),
                        BinaryPrimitives.ReadUInt32LittleEndian(p[13..])));
                }
            }

            if (ok)
            {
                found.Add(new ShaderDefinition(className, layers));
                o += 6 + (count * 17);
            }
        }

        return found;
    }

    /// <summary>The 8-digit art id in a texture name, as a number (02000260 -> 2000260); null if none.</summary>
    public static int? ArtNumber(string name)
    {
        int at = name.LastIndexOf('\\');
        ReadOnlySpan<char> file = name.AsSpan(at + 1);

        return file.Length >= 8 && int.TryParse(file[..8], out int n) ? n : null;
    }

    /// <summary>How far past a group's source <see cref="GroupOwner"/> looks.</summary>
    public const int GroupSpan = 32;

    /// <summary>
    /// The graphic whose texture a group of slices really draws from, when the
    /// source they all name has no texture of its own; null when none holds
    /// the region.
    /// </summary>
    /// <remarks>
    /// A large tree is a row of slices that all name one source first: the
    /// jungle tree's trunk slices 3415-3419 name 3415, its leaves 3420-3426
    /// name 3420. Neither 3415 nor 3420 has world art; the trunk texture is on
    /// 3417 (512x1024) and the canopy on 3423 (512x512), both members of their
    /// group. A slice's second reference is only the next slice, so following
    /// it found the canopy for some slices, nothing for others and another
    /// tree's canopy (3430, of group 3427) for 3426.
    /// </remarks>
    public static int? GroupOwner(
        int group, Func<int, byte[]?> raw, KrStringDictionary strings, Func<int, bool> holds)
    {
        for (int g = group; g < group + GroupSpan && g < 0x4000; g++)
        {
            if (!holds(g) || raw(g) is not { } data)
            {
                continue;
            }

            List<int> refs = References(data, g, strings);

            if (refs.Count > 0 && refs[0] == group)
            {
                return g;
            }
        }

        return null;
    }

    /// <summary>One graphic's raw record, or null when there is none.</summary>
    public static byte[]? ReadRaw(KrAssetStore store, int graphic) =>
        store.GetPackage(PackageName)?.Read(EntryName(graphic));

    /// <summary>Reads one graphic's record from the store, or null when there is none.</summary>
    public static KrTileArt? Read(KrAssetStore store, int graphic)
    {
        byte[]? raw = ReadRaw(store, graphic);

        return raw is null ? null : Parse(raw);
    }
}
