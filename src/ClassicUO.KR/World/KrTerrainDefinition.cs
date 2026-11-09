// SPDX-License-Identifier: BSD-2-Clause
// TerrainDefinition.uop: one record per terrain type, and the link from a
// sector's terrain id to the art that paints it.
//
// Entries are named build/terraindefinition/{id}.bin - plain decimal, NOT
// zero-padded, unlike every other KR path pattern. The 2.53 assets hold 191
// records, ids 0 to 190, of 134 to 180 bytes each.
//
// A record is a terrain's shader layer set:
//
//   0x00 u32 name string index
//   0x04 u32 terrain id, which equals the id in the file name
//   0x08 u32 ?
//   0x0c f32 blend exponent: KR raises a terrain's blend weight to this
//            power. 0 means no blending - a hard edge. Zero in
//            94 of 191 records, every one a hard surface: water, lava, wood
//            planks, stone and marble floors, brick. The natural terrains are
//            nonzero (grass 3, forest 1, sand 1, cobble 0.5, shore 0.25).
//   0x10 u32 ?
//   0x14 u32 item count
//   0x18     item count * 12 bytes, undecoded (16 bytes in the Enhanced
//            Client's records: an extra u32)
//            u16 ?   (1 in every record)
//            u32 base texture string index
//            u8  layer count
//            layer count * 17 bytes:
//              +0x00 u32 texture string index
//              +0x04 u8  ?        (0 in every layer of every record)
//              +0x05 f32 scale
//              +0x09 u8  slot
//              +0x0a 7 bytes, zero in every layer of every record
//            40 bytes, undecoded
//
// All 191 records parse under this grammar and land on a 40-byte tail, across
// the five distinct record lengths the package holds - 134, 146, 151, 158 and
// 180 bytes, which are exactly 71 + 12*items + 17*layers for the item and
// layer counts each record declares.
//
// The string indexes go through string_dictionary.uop. A texture name that
// begins with eight digits, such as "02000010_Grass_A.tga", names an entry
// build/worldart/{those eight digits}.dds in Texture.uop. Across all 191 records there are 817 texture references: 668
// carry an eight-digit file name and ALL 668 resolve to an entry that is
// present in Texture.uop. The other 149 are human labels ("Swamp2 edges") or
// shader class names ("UODefaultTerrainLayer", "UOWaterTerrainLayer"), the.
//
// What is NOT established is which layer the renderer paints as the ground.
// What was measured: the base is 02000000_Black_Void_A.tga in 183 of 191
// records, slot 2 is 02000010_Grass_A.tga at scale 16 in 183 of 191, and
// slots 0 and 1 carry 85 and 90 distinct textures - the per-terrain pair a
// two-texture noise blend needs. Slot bytes ascend within a record and
// sometimes skip 3, so the byte is a stage enum rather than a counter.
//
//

using System;
using System.Buffers.Binary;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.World;

/// <summary>One layer of a terrain's shader layer set.</summary>
public readonly struct KrTerrainLayer
{
    public const int RecordSize = 17;

    /// <summary>Index into string_dictionary.uop naming this layer's texture.</summary>
    public readonly uint TextureStringIndex;

    /// <summary>Byte at +0x04; zero in every layer of every shipped record.</summary>
    public readonly byte Flag;

    /// <summary>Texture scale, 1 to 48 in the shipped data.</summary>
    public readonly float Scale;

    /// <summary>Stage this layer feeds, 0 to 4.</summary>
    public readonly byte Slot;

    public KrTerrainLayer(uint textureStringIndex, byte flag, float scale, byte slot)
    {
        TextureStringIndex = textureStringIndex;
        Flag = flag;
        Scale = scale;
        Slot = slot;
    }
}

public sealed class KrTerrainDefinition
{
    /// <summary>Bytes of a record that are neither items nor layers.</summary>
    public const int FixedSize = 71;

    /// <summary>Size of the undecoded block that follows the layers.</summary>
    public const int TailSize = 40;

    private const int ItemSize = 12;
    private const int EnhancedItemSize = 16;

    private KrTerrainDefinition(
        int id,
        uint nameStringIndex,
        uint baseTextureStringIndex,
        int itemCount,
        KrTerrainLayer[] layers,
        byte[] raw)
    {
        Id = id;
        NameStringIndex = nameStringIndex;
        BaseTextureStringIndex = baseTextureStringIndex;
        ItemCount = itemCount;
        Layers = layers;
        Raw = raw;
    }

    /// <summary>Terrain id, from the record itself.</summary>
    public int Id { get; }

    /// <summary>Index into the string dictionary holding this terrain's name.</summary>
    public uint NameStringIndex { get; }

    /// <summary>Index into the string dictionary naming the base texture.</summary>
    public uint BaseTextureStringIndex { get; }

    /// <summary>How many 12-byte undecoded items the record carries.</summary>
    public int ItemCount { get; }

    public KrTerrainLayer[] Layers { get; }

    /// <summary>The whole record, including the parts not decoded.</summary>
    public byte[] Raw { get; }

    /// <summary>
    /// The power KR raises this terrain's blend weight to; 0 for a hard edge.
    ///
    /// </summary>
    public float BlendExponent => Raw.Length >= 0x10 ? BitConverter.ToSingle(Raw, 0x0C) : 0f;

    public static string EntryName(int id) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"build/terraindefinition/{id}.bin");

    public const string PackageName = "TerrainDefinition.uop";

    /// <summary>
    /// Turns a texture name from the string dictionary into the package entry
    /// that holds it, or null when the name is a label rather than a file
    /// name. "Data\WorldArt\02000010_Grass_A.tga" and "02000010_Grass_A.tga"
    /// both give "build/worldart/02000010.dds".
    /// </summary>
    public static string? WorldArtEntryName(string? textureName)
    {
        if (string.IsNullOrEmpty(textureName))
        {
            return null;
        }

        int slash = textureName.LastIndexOfAny(['\\', '/']);
        ReadOnlySpan<char> file = textureName.AsSpan(slash + 1);

        if (file.Length < 9 || file[8] != '_')
        {
            return null;
        }

        for (int i = 0; i < 8; i++)
        {
            if (!char.IsAsciiDigit(file[i]))
            {
                return null;
            }
        }

        return string.Concat("build/worldart/", file[..8], ".dds");
    }

    /// <summary>
    /// Parses a record. <paramref name="enhanced"/> reads the Enhanced
    /// Client's layout, whose items are 16 bytes rather than 12.
    /// </summary>
    public static KrTerrainDefinition Parse(byte[] data, bool enhanced = false)
    {
        if (data.Length < FixedSize)
        {
            throw new InvalidDataException($"terrain definition truncated ({data.Length} bytes).");
        }

        ReadOnlySpan<byte> span = data;

        uint nameIndex = BinaryPrimitives.ReadUInt32LittleEndian(span);
        int id = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(4));
        int itemCount = BinaryPrimitives.ReadInt32LittleEndian(span.Slice(0x14));

        if (itemCount < 0 || itemCount > 64)
        {
            throw new InvalidDataException($"terrain definition {id} declares {itemCount} items.");
        }

        int offset = 0x18 + (itemCount * (enhanced ? EnhancedItemSize : ItemSize));

        if (offset + 7 > data.Length)
        {
            throw new InvalidDataException($"terrain definition {id} runs past its end at the layer list.");
        }

        // u16 that is 1 in every shipped record, then the base texture.
        offset += 2;

        uint baseTexture = BinaryPrimitives.ReadUInt32LittleEndian(span.Slice(offset));
        offset += 4;

        int layerCount = span[offset];
        offset += 1;

        if (offset + (layerCount * KrTerrainLayer.RecordSize) + TailSize != data.Length)
        {
            throw new InvalidDataException(
                $"terrain definition {id} is {data.Length} bytes, which does not match " +
                $"{itemCount} item(s) and {layerCount} layer(s).");
        }

        KrTerrainLayer[] layers = new KrTerrainLayer[layerCount];

        for (int i = 0; i < layerCount; i++)
        {
            ReadOnlySpan<byte> r = span.Slice(offset);

            layers[i] = new KrTerrainLayer(
                BinaryPrimitives.ReadUInt32LittleEndian(r),
                r[4],
                BinaryPrimitives.ReadSingleLittleEndian(r.Slice(5)),
                r[9]);

            offset += KrTerrainLayer.RecordSize;
        }

        return new KrTerrainDefinition(id, nameIndex, baseTexture, itemCount, layers, data);
    }

    /// <summary>Reads every terrain definition in the package, in id order.</summary>
    public static List<KrTerrainDefinition> LoadAll(UopFile file, int maxId = 1024, bool enhanced = false)
    {
        List<KrTerrainDefinition> result = [];

        for (int id = 0; id <= maxId; id++)
        {
            byte[]? data = file.Read(EntryName(id));

            if (data is not null)
            {
                result.Add(Parse(data, enhanced));
            }
        }

        return result;
    }
}
