// SPDX-License-Identifier: BSD-2-Clause
// Reader for the Mythic Package container (.uop) used by Kingdom Reborn.
//
// Layout (little-endian):
//   Header: "MYP\0" u32 version(=4) u32 signature i64 firstTableOffset
//           u32 tableCapacity u32 fileCount
//   Table:  u32 entryCount i64 nextTableOffset, then entryCount * 34-byte entries
//   Entry:  i64 dataOffset u32 headerLength u32 compressedSize u32 decompressedSize
//           u64 nameHash u32 headerAdler32 u16 flags      (flags 1 = zlib)
//
// Payload starts at dataOffset + headerLength. headerLength is 12 in every KR
// package seen; headerAdler32 is the Adler-32 of those 12 bytes.

using System.Buffers.Binary;
using System.IO.Compression;
using Microsoft.Win32.SafeHandles;

namespace ClassicUO.KR.Uop;

public readonly struct UopEntry
{
    public const int RecordSize = 34;

    public readonly long DataOffset;
    public readonly int HeaderLength;
    public readonly int CompressedSize;
    public readonly int DecompressedSize;
    public readonly ulong NameHash;
    public readonly uint HeaderAdler32;
    public readonly ushort Flags;

    public UopEntry(
        long dataOffset,
        int headerLength,
        int compressedSize,
        int decompressedSize,
        ulong nameHash,
        uint headerAdler32,
        ushort flags)
    {
        DataOffset = dataOffset;
        HeaderLength = headerLength;
        CompressedSize = compressedSize;
        DecompressedSize = decompressedSize;
        NameHash = nameHash;
        HeaderAdler32 = headerAdler32;
        Flags = flags;
    }

    public bool IsEmpty => DataOffset == 0 || CompressedSize == 0;

    public bool IsCompressed => (Flags & 1) != 0;

    /// <summary>Payload size once inflated (or the stored size, when uncompressed).</summary>
    public int PayloadSize => DecompressedSize != 0 ? DecompressedSize : CompressedSize;
}

public sealed class UopFile : IDisposable
{
    private const uint Magic = 0x0050594D; // "MYP\0"

    private readonly SafeFileHandle _handle;
    private readonly UopEntry[] _entries;
    private readonly Dictionary<ulong, int> _byHash;

    public string FilePath { get; }

    public uint Version { get; }

    public uint Signature { get; }

    public IReadOnlyList<UopEntry> Entries => _entries;

    private UopFile(string path, SafeFileHandle handle, uint version, uint signature, UopEntry[] entries)
    {
        FilePath = path;
        _handle = handle;
        Version = version;
        Signature = signature;
        _entries = entries;
        _byHash = new Dictionary<ulong, int>(entries.Length);

        for (int i = 0; i < entries.Length; i++)
        {
            // Duplicate hashes should not occur; first one wins if they do.
            _byHash.TryAdd(entries[i].NameHash, i);
        }
    }

    public static UopFile Open(string path)
    {
        SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

        try
        {
            Span<byte> header = stackalloc byte[28];
            ReadExact(handle, header, 0);

            uint magic = BinaryPrimitives.ReadUInt32LittleEndian(header);

            if (magic != Magic)
            {
                throw new InvalidDataException($"'{path}' is not a Mythic Package (magic 0x{magic:X8}).");
            }

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(4));
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(8));
            long nextTable = BinaryPrimitives.ReadInt64LittleEndian(header.Slice(12));
            uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice(24));

            List<UopEntry> entries = new((int)Math.Min(fileCount, 1u << 20));
            Span<byte> tableHeader = stackalloc byte[12];

            while (nextTable != 0)
            {
                ReadExact(handle, tableHeader, nextTable);

                int entryCount = BinaryPrimitives.ReadInt32LittleEndian(tableHeader);
                long following = BinaryPrimitives.ReadInt64LittleEndian(tableHeader.Slice(4));

                if (entryCount < 0)
                {
                    throw new InvalidDataException($"'{path}': bad table entry count {entryCount}.");
                }

                byte[] block = new byte[entryCount * UopEntry.RecordSize];
                ReadExact(handle, block, nextTable + 12);

                for (int i = 0; i < entryCount; i++)
                {
                    ReadOnlySpan<byte> e = block.AsSpan(i * UopEntry.RecordSize, UopEntry.RecordSize);

                    UopEntry entry = new(
                        BinaryPrimitives.ReadInt64LittleEndian(e),
                        BinaryPrimitives.ReadInt32LittleEndian(e.Slice(8)),
                        BinaryPrimitives.ReadInt32LittleEndian(e.Slice(12)),
                        BinaryPrimitives.ReadInt32LittleEndian(e.Slice(16)),
                        BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(20)),
                        BinaryPrimitives.ReadUInt32LittleEndian(e.Slice(28)),
                        BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(32)));

                    if (!entry.IsEmpty)
                    {
                        entries.Add(entry);
                    }
                }

                nextTable = following;
            }

            return new UopFile(path, handle, version, signature, entries.ToArray());
        }
        catch
        {
            handle.Dispose();

            throw;
        }
    }

    public bool TryGetEntry(string name, out UopEntry entry) => TryGetEntry(UopHash.Create(name), out entry);

    public bool TryGetEntry(ulong hash, out UopEntry entry)
    {
        if (_byHash.TryGetValue(hash, out int index))
        {
            entry = _entries[index];

            return true;
        }

        entry = default;

        return false;
    }

    public bool Contains(string name) => _byHash.ContainsKey(UopHash.Create(name));

    public bool Contains(ulong hash) => _byHash.ContainsKey(hash);

    /// <summary>Reads an entry and inflates it when the zlib flag is set.</summary>
    public byte[] Read(in UopEntry entry)
    {
        byte[] raw = ReadRaw(entry);

        if (!entry.IsCompressed)
        {
            return raw;
        }

        byte[] output = new byte[entry.DecompressedSize];

        using MemoryStream input = new(raw, writable: false);
        using ZLibStream zlib = new(input, CompressionMode.Decompress);

        int read = 0;

        while (read < output.Length)
        {
            int n = zlib.Read(output, read, output.Length - read);

            if (n <= 0)
            {
                throw new InvalidDataException($"zlib stream ended after {read} of {output.Length} bytes.");
            }

            read += n;
        }

        return output;
    }

    /// <summary>Reads an entry's payload without inflating it.</summary>
    public byte[] ReadRaw(in UopEntry entry)
    {
        byte[] buffer = new byte[entry.CompressedSize];
        ReadExact(_handle, buffer, entry.DataOffset + entry.HeaderLength);

        return buffer;
    }

    /// <summary>Reads the small per-entry header preceding the payload (12 bytes in KR files).</summary>
    public byte[] ReadEntryHeader(in UopEntry entry)
    {
        byte[] buffer = new byte[entry.HeaderLength];

        if (buffer.Length != 0)
        {
            ReadExact(_handle, buffer, entry.DataOffset);
        }

        return buffer;
    }

    public byte[]? Read(string name) => TryGetEntry(name, out UopEntry entry) ? Read(entry) : null;

    private static void ReadExact(SafeFileHandle handle, Span<byte> buffer, long offset)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int n = RandomAccess.Read(handle, buffer.Slice(total), offset + total);

            if (n == 0)
            {
                throw new EndOfStreamException($"unexpected EOF at {offset + total} ({buffer.Length - total} bytes short).");
            }

            total += n;
        }
    }

    public void Dispose() => _handle.Dispose();
}
