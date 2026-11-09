// SPDX-License-Identifier: BSD-2-Clause
// Catalogues KR's spell effects: every Gamebryo NIF scene in
// EffectTexture.uop, what kinds of blocks it holds, and which textures it
// names - the survey that says how much of the format a renderer needs.
//
// Entries are named "data/effects/" + a file name from the string dictionary
// (e.g. 00001002_Fireworks02.nif); effect definitions in
// EffectDefinitionCollection.uop, "build/effectdefinitioncollection/{id:D8}.bin",
// hold that name as a string index at +8.

using System.Text.RegularExpressions;
using ClassicUO.KR.Effects;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.Tool;

internal static partial class EffectCatalog
{
    public const string TexturePackage = "EffectTexture.uop";
    public const string DefinitionPackage = "EffectDefinitionCollection.uop";

    [GeneratedRegex(@"[\w\-]+\.(?:tga|dds|bmp)", RegexOptions.IgnoreCase)]
    private static partial Regex TextureName();

    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: effects <uokrPath> [--name <substring>] [--blocks]");

            return 1;
        }

        string root = args[0];
        string? filter = Program.GetOption(args, "--name");
        bool listBlocks = args.Contains("--blocks");

        using UopFile package = UopFile.Open(Path.Combine(root, TexturePackage));
        KrStringDictionary strings = KrStringDictionary.Load(Path.Combine(root, KrStringDictionary.PackageName));

        // Names: any dictionary string that hashes to an entry under data/effects/.
        Dictionary<ulong, string> names = [];

        for (int i = 0; i < strings.Count; i++)
        {
            string? s = strings[i];

            if (!string.IsNullOrEmpty(s) && package.TryGetEntry("data/effects/" + s, out UopEntry e))
            {
                names[e.NameHash] = s;
            }
        }

        // Textures are named by the NIFs and scripts that use them.
        HashSet<string> textureNames = [];

        foreach (UopEntry entry in package.Entries)
        {
            if (!entry.IsEmpty)
            {
                foreach (Match m in TextureName().Matches(System.Text.Encoding.ASCII.GetString(package.Read(entry))))
                {
                    textureNames.Add(m.Value);
                }
            }
        }

        foreach (string t in textureNames)
        {
            if (package.TryGetEntry("data/effects/" + t, out UopEntry e))
            {
                names.TryAdd(e.NameHash, t);
            }
        }

        Dictionary<string, int> kinds = [];
        int parsed = 0, failed = 0;
        Dictionary<string, int> typeFiles = [];
        Dictionary<string, int> typeBlocks = [];
        Dictionary<string, int> textures = [];
        Dictionary<string, int> versions = [];

        foreach (UopEntry entry in package.Entries)
        {
            if (entry.IsEmpty)
            {
                continue;
            }

            byte[] data = package.Read(entry);
            string name = names.TryGetValue(entry.NameHash, out string? n) ? n : $"0x{entry.NameHash:X16}";
            string kind = Kind(data);
            Bump(kinds, kind);

            if (kind != "nif")
            {
                continue;
            }

            NifHeader? header = NifHeader.TryParse(data);

            if (header is null)
            {
                failed++;
                Console.WriteLine($"  not parsed: {name} ({data.Length} bytes)");

                continue;
            }

            parsed++;
            Bump(versions, header.VersionText);

            foreach (string type in header.BlockTypes)
            {
                Bump(typeFiles, type);
            }

            for (int i = 0; i < header.BlockCount; i++)
            {
                Bump(typeBlocks, header.TypeOf(i));
            }

            HashSet<string> own = [];

            foreach (Match m in TextureName().Matches(System.Text.Encoding.ASCII.GetString(data)))
            {
                own.Add(m.Value.ToLowerInvariant());
            }

            foreach (string t in own)
            {
                Bump(textures, t);
            }

            if (filter is not null && name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{name}: {data.Length} bytes, {header.BlockCount} blocks, textures: {string.Join(", ", own)}");

                if (listBlocks)
                {
                    for (int i = 0; i < header.BlockCount; i++)
                    {
                        Console.WriteLine($"  [{i,3}] {header.TypeOf(i)}");
                    }
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"{package.Entries.Count(e => !e.IsEmpty)} entries, {names.Count} named; NIFs: {parsed} parsed, {failed} not");
        Console.WriteLine($"kinds: {string.Join(", ", kinds.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
        Console.WriteLine($"versions: {string.Join(", ", versions.Select(kv => $"{kv.Key} x{kv.Value}"))}");
        Console.WriteLine();
        Console.WriteLine($"{typeFiles.Count} block types (files using it, blocks in all):");

        foreach ((string type, int files) in typeFiles.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key))
        {
            Console.WriteLine($"  {type,-34} {files,5} {typeBlocks.GetValueOrDefault(type),7}");
        }

        Console.WriteLine();
        Console.WriteLine($"{textures.Count} distinct texture names; most used:");

        foreach ((string tex, int count) in textures.OrderByDescending(kv => kv.Value).Take(25))
        {
            Console.WriteLine($"  {tex,-40} {count,4}");
        }

        return failed == 0 ? 0 : 2;
    }

    /// <summary>What an entry is, from its first bytes.</summary>
    public static string Kind(ReadOnlySpan<byte> data)
    {
        if (NifHeader.LooksLikeNif(data))
            return "nif";

        if (data.Length >= 4 && data[0] == 'p' && data[1] == 'a' && data[2] == 'r' && data[3] == 't')
            return "particle script";

        if (data.Length >= 4 && data[0] == '8' && data[1] == 'B' && data[2] == 'P' && data[3] == 'S')
            return "psd";

        // TGA: no id, no colour map, image type 2 (true colour) or 10 (RLE) or 3 (grey).
        if (data.Length >= 18 && data[0] == 0 && data[1] == 0 && data[2] is 2 or 3 or 10)
            return "tga";

        return "other";
    }

    private static void Bump(Dictionary<string, int> d, string key) =>
        d[key] = d.GetValueOrDefault(key) + 1;
}
