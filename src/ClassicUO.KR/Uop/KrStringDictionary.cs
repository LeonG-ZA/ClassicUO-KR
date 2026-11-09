// SPDX-License-Identifier: BSD-2-Clause
// string_dictionary.uop: one entry holding every string the KR data refers to
// by index - texture file names, terrain names, shader class names.
//
// The package holds a single unnamed entry. Its payload is:
//
//   0x00 u64 ?            (looks like a FILETIME; not used)
//   0x08 u32 count        one MORE than the number of strings present
//   0x0c u32 ?            59 in the 2.53 data
//   0x10      strings, each a u16 byte length followed by that many bytes
//
// The 2.53 file holds 30,220 strings and consumes to exactly its last byte,
// while the count at 0x08 reads 30,221: index 0 is an implicit empty string
// and the stored strings are 1..30,220. (An earlier reading pinned base 0 from
// art ids matching terrain ids; neighbouring strings often name similar ids,
// and the terrain layers only line up with base 1.)
//
// Strings are stored as single bytes. The 2.53 data is plain ASCII apart from
// a handful of entries, so Latin-1 is used: it never throws and round-trips
// every byte.

using System.Buffers.Binary;
using System.Text;

namespace ClassicUO.KR.Uop;

public sealed class KrStringDictionary
{
    private const int HeaderSize = 16;

    private readonly string[] _strings;

    private KrStringDictionary(string[] strings, uint declaredCount)
    {
        _strings = strings;
        DeclaredCount = declaredCount;
    }

    /// <summary>How many strings the file actually holds.</summary>
    public int Count => _strings.Length;

    /// <summary>The count field at 0x08, which is one more than <see cref="Count"/>.</summary>
    public uint DeclaredCount { get; }

    /// <summary>The string at an index, or null when the index is out of range.</summary>
    public string? this[long index] =>
        index >= 0 && index < _strings.Length ? _strings[(int)index] : null;

    public static KrStringDictionary Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException($"string dictionary truncated ({data.Length} bytes).");
        }

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(8));

        // KR's indexes are 1-based: string 0 is an implicit empty entry, which
        // is why the declared count is one more than the strings stored. An
        // the KR client settled it - the grass terrain's
        // layers bind Grass_A, Grass_B and noise_alpha, each one entry before
        // what a 0-based read gives.
        List<string> strings = [string.Empty];
        int offset = HeaderSize;

        Encoding latin1 = Encoding.Latin1;

        while (offset + 2 <= data.Length)
        {
            int length = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
            offset += 2;

            if (offset + length > data.Length)
            {
                throw new InvalidDataException(
                    $"string dictionary entry {strings.Count} runs past the end of the payload.");
            }

            strings.Add(latin1.GetString(data.Slice(offset, length)));
            offset += length;
        }

        return new KrStringDictionary([.. strings], declared);
    }

    /// <summary>Reads the single entry out of string_dictionary.uop.</summary>
    public static KrStringDictionary Load(string path)
    {
        using UopFile file = UopFile.Open(path);

        foreach (UopEntry entry in file.Entries)
        {
            return Parse(file.Read(entry));
        }

        throw new InvalidDataException($"'{path}' holds no entries.");
    }

    public const string PackageName = "string_dictionary.uop";
}
