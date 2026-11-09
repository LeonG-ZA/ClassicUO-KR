// SPDX-License-Identifier: BSD-2-Clause
// Reader for the Mythic Package Editor name dictionary (Dictionary.dic).
//
// UOP entries are keyed by a hash of their path, so a package cannot be
// enumerated by name on its own. The Mythic Package Editor EC ships a
// dictionary of recovered names, which turns a package listing into readable
// paths.
//
// Nothing here ships with this project: the file is the editor's, and the
// caller supplies a path to their own copy.
//   https://github.com/pincoide/Mythic-Package-Editor-EC-
//   (Mythic.Package.Editor/Dictionary.dic on the master branch)
//
// Format:
//   "DIC\0" u8 version, then per entry:
//     u64 hash
//     u8  hasName
//     when hasName is non-zero: a 7-bit encoded length then that many UTF-8 bytes
//
// Entries with hasName clear are hashes whose name was never recovered; the
// copy used here holds 160,172 named entries and 23,789 unnamed ones, and
// every named entry re-hashes to its own key.

using System.Buffers.Binary;
using System.Text;

namespace ClassicUO.KR.Uop;

public sealed class UopNameDictionary
{
    private readonly Dictionary<ulong, string> _names;

    private UopNameDictionary(Dictionary<ulong, string> names, int unnamedCount)
    {
        _names = names;
        UnnamedCount = unnamedCount;
    }

    public int Count => _names.Count;

    /// <summary>Hashes present in the file with no recovered name.</summary>
    public int UnnamedCount { get; }

    public IReadOnlyDictionary<ulong, string> Names => _names;

    public static UopNameDictionary Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);

        if (data.Length < 5 || data[0] != 'D' || data[1] != 'I' || data[2] != 'C' || data[3] != 0)
        {
            throw new InvalidDataException($"'{path}' is not a Mythic Package name dictionary.");
        }

        Dictionary<ulong, string> names = [];
        int unnamed = 0;
        int offset = 5;

        while (offset + 9 <= data.Length)
        {
            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(offset));
            offset += 8;

            byte hasName = data[offset++];

            if (hasName == 0)
            {
                unnamed++;

                continue;
            }

            int length = 0;
            int shift = 0;

            while (true)
            {
                if (offset >= data.Length)
                {
                    throw new InvalidDataException($"'{path}': name length runs past the end of the file.");
                }

                byte b = data[offset++];
                length |= (b & 0x7F) << shift;

                if ((b & 0x80) == 0)
                {
                    break;
                }

                shift += 7;

                if (shift > 28)
                {
                    throw new InvalidDataException($"'{path}': malformed name length.");
                }
            }

            if (length < 0 || offset + length > data.Length)
            {
                throw new InvalidDataException($"'{path}': name runs past the end of the file.");
            }

            names[hash] = Encoding.UTF8.GetString(data, offset, length);
            offset += length;
        }

        return new UopNameDictionary(names, unnamed);
    }

    public bool TryGetName(ulong hash, out string name) => _names.TryGetValue(hash, out name!);

    /// <summary>Resolves the names of a package's entries, where the dictionary knows them.</summary>
    public List<string> ResolveEntries(UopFile file)
    {
        List<string> resolved = [];

        foreach (UopEntry entry in file.Entries)
        {
            if (_names.TryGetValue(entry.NameHash, out string? name))
            {
                resolved.Add(name);
            }
        }

        resolved.Sort(StringComparer.Ordinal);

        return resolved;
    }
}
