// SPDX-License-Identifier: BSD-2-Clause
//
// The header of a Gamebryo NIF scene, as KR's spell effects store them in
// EffectTexture.uop. Every one of the
// 749 entries is version 20.0.0.4:
//
//   "Gamebryo File Format, Version 20.0.0.4\n"
//   u32  version          0x14000004
//   u8   endian           1 = little
//   u32  user version     0
//   u32  block count
//   u16  block type count
//   per type:  u32 length, ASCII name     ("NiNode", "NiParticleSystem", ...)
//   per block: u16 index into the types
//   u32  group count, then the groups
//
// 20.0.0.4 predates the block-size table (20.2.0.7) and the shared string
// table (20.1.0.3), so the blocks themselves can only be walked by parsing
// each type. This reads the header only: what kinds of blocks a scene holds,
// in what order, and where they start.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace ClassicUO.KR.Effects;

public sealed class NifHeader
{
    public const string Magic = "Gamebryo File Format, Version ";

    private NifHeader(string versionText, uint version, uint userVersion,
        string[] blockTypes, ushort[] blockTypeIndex, int blocksOffset)
    {
        VersionText = versionText;
        Version = version;
        UserVersion = userVersion;
        BlockTypes = blockTypes;
        BlockTypeIndex = blockTypeIndex;
        BlocksOffset = blocksOffset;
    }

    /// <summary>The version as the header line spells it, e.g. "20.0.0.4".</summary>
    public string VersionText { get; }

    public uint Version { get; }

    public uint UserVersion { get; }

    /// <summary>The distinct block type names, in the file's order.</summary>
    public IReadOnlyList<string> BlockTypes { get; }

    /// <summary>For each block, its index into <see cref="BlockTypes"/>.</summary>
    public IReadOnlyList<ushort> BlockTypeIndex { get; }

    public int BlockCount => BlockTypeIndex.Count;

    /// <summary>Where the first block starts.</summary>
    public int BlocksOffset { get; }

    /// <summary>The type name of block <paramref name="i"/>.</summary>
    public string TypeOf(int i) => BlockTypes[BlockTypeIndex[i]];

    public static bool LooksLikeNif(ReadOnlySpan<byte> data) =>
        data.Length > Magic.Length && data[..Magic.Length].SequenceEqual(Encoding.ASCII.GetBytes(Magic));

    /// <summary>Parses the header, or returns null when it is not a version this reads.</summary>
    public static NifHeader? TryParse(ReadOnlySpan<byte> data)
    {
        if (!LooksLikeNif(data))
        {
            return null;
        }

        int newline = data.IndexOf((byte)'\n');

        if (newline < 0)
        {
            return null;
        }

        string versionText = Encoding.ASCII.GetString(data[Magic.Length..newline]);
        int o = newline + 1;

        if (o + 13 > data.Length)
        {
            return null;
        }

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);
        byte endian = data[o + 4];
        uint userVersion = BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 5)..]);
        int blockCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(o + 9)..]);
        o += 13;

        // Only the layout above: 20.0.0.4, little-endian, no Bethesda header.
        if (version != 0x14000004 || endian != 1 || userVersion != 0 || blockCount < 0 || o + 2 > data.Length)
        {
            return null;
        }

        int typeCount = BinaryPrimitives.ReadUInt16LittleEndian(data[o..]);
        o += 2;

        string[] types = new string[typeCount];

        for (int i = 0; i < typeCount; i++)
        {
            if (o + 4 > data.Length)
            {
                return null;
            }

            int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);
            o += 4;

            if (length < 0 || length > 256 || o + length > data.Length)
            {
                return null;
            }

            types[i] = Encoding.ASCII.GetString(data.Slice(o, length));
            o += length;
        }

        if (o + (2 * blockCount) + 4 > data.Length)
        {
            return null;
        }

        ushort[] index = new ushort[blockCount];

        for (int i = 0; i < blockCount; i++)
        {
            index[i] = BinaryPrimitives.ReadUInt16LittleEndian(data[(o + (2 * i))..]);

            if (index[i] >= typeCount)
            {
                return null;
            }
        }

        o += 2 * blockCount;

        int groups = (int)BinaryPrimitives.ReadUInt32LittleEndian(data[o..]);
        o += 4 + (4 * groups);

        return new NifHeader(versionText, version, userVersion, types, index, o);
    }
}
