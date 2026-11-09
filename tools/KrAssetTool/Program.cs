// SPDX-License-Identifier: BSD-2-Clause
// Command-line probe/export tool for Kingdom Reborn assets.

using System.Globalization;
using ClassicUO.KR;
using ClassicUO.KR.Animations;
using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;
using ClassicUO.KR.Util;
using ClassicUO.KR.World;

namespace ClassicUO.KR.Tool;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Usage();

            return 1;
        }

        // `kr-anim-export <uokrPath> <body> <action>` compatibility: a leading
        // path means the export command.
        string command = args[0];

        if (Directory.Exists(command) || File.Exists(command))
        {
            command = Path.GetExtension(args[0]).Equals(".uop", StringComparison.OrdinalIgnoreCase) ? "list" : "export";
            args = ["--", .. args];
        }

        string[] rest = args[1..];

        try
        {
            switch (command.ToLowerInvariant().TrimStart('-'))
            {
                case "export": return Export(rest);
                case "info": return Info(rest);
                case "actions": return Actions(rest);
                case "bodies": return Bodies(rest);
                case "verify": return Verify(rest);
                case "facet": return Facet(rest);
                case "verifyfacets" or "verify-facets": return VerifyFacets(rest);
                case "terrain": return Terrain(rest);
                case "verifyterrain" or "verify-terrain": return VerifyTerrain(rest);
                case "terrainmap" or "terrain-map": return TerrainMap(rest);
                case "terrainclassify" or "terrain-classify": return TerrainClassify(rest);
                case "terrainaudit" or "terrain-audit": return TerrainAudit(rest);
                case "terrainrules" or "terrain-rules": return TerrainRules(rest);
                case "staticpreview" or "static-preview": return StaticPreview.Run(rest);
                case "staticpairs" or "static-pairs": return StaticPairs(rest);
                case "staticdiff" or "static-diff": return StaticDiff.Run(rest);
                case "effects": return EffectCatalog.Run(rest);
                case "land-probe":
                {
                    using KrAssetStore st = new(rest[0]);
                    KrTerrainArt a = new(st);
                    int t = int.Parse(rest[1]);
                    foreach ((int x, int y) in new[] { (4530, 1365), (4531, 1366), (4529, 1370), (4533, 1362) })
                    {
                        static double Mean(uint[]? px) { double s = 0; int n = 0; foreach (uint p in px ?? []) { if ((p >> 24) == 0) continue; s += ((p & 0xFF) + ((p >> 8) & 0xFF) + ((p >> 16) & 0xFF)) / 3.0; n++; } return n == 0 ? -1 : s / n; }
                        Console.WriteLine($"({x},{y}) diamond {Mean(a.GroundDiamond(t, 44, x, y)):0.0}  square64 {Mean(a.GroundTile(t, 64, x, y)):0.0}  square128 {Mean(a.GroundTile(t, 128, x, y)):0.0}");
                    }
                    return 0;
                }
                case "tileartstrings" or "tileart-strings": return TileArtStrings.Run(rest);
                case "effect-sim": return EffectSim.Run(rest);
                case "effect-render": return EffectRender.Run(rest);
                case "effect-dump": return EffectDump.Run(rest);
                case "coverage": return Coverage.Run(rest);
                case "mapdiff": return MapDiff.Run(rest);
                case "held-lights": return HeldLightsCheck.Run(rest);
                case "terrainarea" or "terrain-area": return TerrainArea.Run(rest);
                case "terrainrender" or "terrain-render": return TerrainRender.Run(rest);
                case "terrainfloats" or "terrain-floats": return TerrainFloats(rest);
                case "terrainpreview" or "terrain-preview":
                    return TerrainPreview.Run(rest, GetOption, ParseInt);
                case "terrainpatch" or "terrain-patch":
                    return TerrainPreview.RunPatch(rest, GetOption, ParseInt);
                case "exporttexture" or "export-texture": return ExportTexture(rest);
                case "names": return Names(rest);
                case "sequence": return Sequence(rest);
                case "verifysequences" or "verify-sequences": return VerifySequences(rest);
                case "hash": return Hash(rest);
                case "list": return List(rest);
                case "probe": return Probe(rest);
                case "extract": return Extract(rest);
                case "help": Usage(); return 0;
                default:
                    Console.Error.WriteLine($"unknown command '{command}'.");
                    Usage();

                    return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");

            return 2;
        }
    }

    private static void Usage()
    {
        Console.WriteLine(
            """
            kr-asset-tool - read assets from a local Kingdom Reborn install

              export  <uokrPath> <body> <action> [-o <dir>] [-d <direction>] [--sheet] [--no-frames]
                      Decode an AMOU animation to PNG. Writes one PNG per frame and,
                      with --sheet, an anchor-aligned contact sheet per direction.

              info    <uokrPath> <body> <action>
                      Header, palette and frame-table summary for one animation.

              effects <uokrPath> [--name <substring>] [--blocks]
                      Catalogue the spell-effect NIF scenes: block types, textures.

              actions <uokrPath> <body> [--max <n>]      Which action files exist for a body.
              bodies  <uokrPath> [--max <n>]             Which body ids have animations.

              facet   <uokrPath> <facet> [--sector <n>]  Map size, sector grid, and one sector.
              verify-facets <uokrPath>                  Sweep every sector of every facet.
              terrain <uokrPath> [--id <n>] [--limit <n>]
                      Terrain definitions, with each texture reference resolved.
              verify-terrain <uokrPath>                 Follow every texture reference into Texture.uop.
              terrain-map <uokrPath>                    Land tile id -> terrain id table, from the facets.
              terrain-preview <uokrPath> <uoPath> [-o <dir>] [--count <n>]
                      2D land art beside what KR terrain repaints it to.
              terrain-patch <uokrPath> [--land <graphic>] [--tiles <n>] [-o <dir>]
                      A patch of ground laid out on the isometric grid.
              export-texture <uokrPath> <terraintexture | worldart <artId> | terrain <terrainId>> [-o <dir>]
                      Decode DDS textures to PNG.
              names   <Dictionary.dic> <uop>            Resolve entry hashes to real paths.

              list    <uop> [--limit <n>]                Entries in a package.
              probe   <uop> [--limit <n>]                Entries plus a guess at each payload's format.
              extract <uop> <hash|0xHASH|name> <out>     Write one entry's inflated payload to a file.
              hash    <name>                             lookup3 hash of a package-internal path.

            `kr-asset-tool <uokrPath> <body> <action>` is shorthand for `export`.
            """);
    }

    private static int Export(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: export <uokrPath> <body> <action> [-o <dir>] [-d <direction>] [--sheet]");

            return 1;
        }

        string root = args[0];
        int body = ParseInt(args[1]);
        int action = ParseInt(args[2]);

        string outDir = GetOption(args, "-o") ?? GetOption(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), $"kr-anim-{body:D6}-{action:D2}");

        string? directionOption = GetOption(args, "-d") ?? GetOption(args, "--direction");
        bool sheet = HasFlag(args, "--sheet");
        bool frames = !HasFlag(args, "--no-frames");

        using KrAssetStore store = new(root);

        AmouAnimation? animation = store.LoadAnimation(body, action);

        if (animation is null)
        {
            Console.Error.WriteLine($"no KR animation for body {body} action {action}.");

            return 3;
        }

        Directory.CreateDirectory(outDir);

        if (!animation.HasRegularDirections)
        {
            Console.Error.WriteLine(
                $"warning: {animation.Frames.Count} frames do not divide into {AmouAnimation.StoredDirectionCount} " +
                "directions; exporting in stored order instead.");

            return ExportRaw(animation, outDir, sheet);
        }

        int[] directions = directionOption is null
            ? Enumerable.Range(0, AmouAnimation.StoredDirectionCount).ToArray()
            : [ParseInt(directionOption)];

        int perDirection = animation.FramesPerDirection;
        int written = 0;

        // Every frame's size and anchor, for lining frames up outside the tool.
        using StreamWriter anchors = new(Path.Combine(outDir, "anchors.txt"));

        foreach (int direction in directions)
        {
            List<AmouFrameBitmap> decoded = [];

            for (int frame = 0; frame < perDirection; frame++)
            {
                if (!animation.TryGetFrameInfo(direction, frame, out AmouFrameInfo info) || info.IsEmpty)
                {
                    continue;
                }

                AmouFrameBitmap bitmap = animation.Decode(info);
                decoded.Add(bitmap);
                anchors.WriteLine($"d{direction}_f{frame:D3} {bitmap.Width} {bitmap.Height} {bitmap.CenterX} {bitmap.CenterY}");

                if (frames)
                {
                    string path = Path.Combine(outDir, $"d{direction}_f{frame:D3}.png");
                    PngWriter.Write(path, bitmap.Width, bitmap.Height, bitmap.Pixels);
                    written++;
                }
            }

            if (sheet && decoded.Count != 0)
            {
                string path = Path.Combine(outDir, $"d{direction}_sheet.png");
                WriteSheet(path, decoded);
                written++;
            }

            Console.WriteLine($"direction {direction}: {decoded.Count} frames");
        }

        Console.WriteLine($"wrote {written} file(s) to {outDir}");

        return 0;
    }

    /// <summary>Fallback for a file whose frame table does not split into five directions.</summary>
    private static int ExportRaw(AmouAnimation animation, string outDir, bool sheet)
    {
        List<AmouFrameBitmap> decoded = [];

        foreach (AmouFrameInfo info in animation.Frames)
        {
            if (info.IsEmpty)
            {
                continue;
            }

            AmouFrameBitmap bitmap = animation.Decode(info);
            decoded.Add(bitmap);
            PngWriter.Write(Path.Combine(outDir, $"raw_{info.Index:D3}.png"), bitmap.Width, bitmap.Height, bitmap.Pixels);
        }

        if (sheet && decoded.Count != 0)
        {
            WriteSheet(Path.Combine(outDir, "raw_sheet.png"), decoded);
        }

        Console.WriteLine($"wrote {decoded.Count} frame(s) in stored order to {outDir}");

        return 0;
    }

    /// <summary>Lays frames out in a row, aligned on the mobile's anchor point.</summary>
    private static void WriteSheet(string path, List<AmouFrameBitmap> frames)
    {
        int left = 0;
        int right = 0;
        int top = 0;
        int bottom = 0;

        foreach (AmouFrameBitmap f in frames)
        {
            left = Math.Max(left, f.CenterX);
            right = Math.Max(right, f.Width - f.CenterX);
            top = Math.Max(top, f.CenterY);
            bottom = Math.Max(bottom, f.Height - f.CenterY);
        }

        int cellWidth = left + right;
        int cellHeight = top + bottom;
        int width = cellWidth * frames.Count;
        uint[] pixels = new uint[width * cellHeight];

        for (int i = 0; i < frames.Count; i++)
        {
            AmouFrameBitmap f = frames[i];
            int originX = (i * cellWidth) + left - f.CenterX;
            int originY = top - f.CenterY;

            for (int y = 0; y < f.Height; y++)
            {
                for (int x = 0; x < f.Width; x++)
                {
                    pixels[((originY + y) * width) + originX + x] = f.Pixels[(y * f.Width) + x];
                }
            }
        }

        PngWriter.Write(path, width, cellHeight, pixels);
    }

    private static int Info(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: info <uokrPath> <body> <action>");

            return 1;
        }

        using KrAssetStore store = new(args[0]);

        int body = ParseInt(args[1]);
        int action = ParseInt(args[2]);
        string name = KrAssetStore.AnimationFrameName(body, action);

        Console.WriteLine($"name      {name}");
        Console.WriteLine($"hash      0x{UopHash.Create(name):X16}");

        if (!store.TryFindAnimationFrame(body, action, out UopFile package, out UopEntry entry))
        {
            Console.WriteLine("not present in any AnimationFrame package.");

            return 3;
        }

        Console.WriteLine($"package   {Path.GetFileName(package.FilePath)}");
        Console.WriteLine($"offset    {entry.DataOffset} (+{entry.HeaderLength} header)");
        Console.WriteLine($"size      {entry.CompressedSize} -> {entry.DecompressedSize} (flags {entry.Flags})");

        AmouAnimation animation = AmouAnimation.Parse(package.Read(entry));

        Console.WriteLine($"version   {animation.Version}");
        Console.WriteLine($"body id   {animation.BodyId}");
        Console.WriteLine($"bounds    ({animation.BoundsX1}, {animation.BoundsY1}) .. ({animation.BoundsX2}, {animation.BoundsY2})");
        Console.WriteLine($"action id {animation.Action}");
        Console.WriteLine($"frames    {animation.Frames.Count}");

        if (!animation.HasRegularDirections)
        {
            Console.WriteLine($"          does NOT divide into {AmouAnimation.StoredDirectionCount} directions");

            return 0;
        }

        Console.WriteLine($"          {AmouAnimation.StoredDirectionCount} directions x {animation.FramesPerDirection} frames");

        for (int direction = 0; direction < AmouAnimation.StoredDirectionCount; direction++)
        {
            Console.Write($"  dir {direction}:");

            if (animation.TryGetFrameInfo(direction, 0, out AmouFrameInfo first))
            {
                Console.Write($" first {first.Width}x{first.Height} anchor ({first.CenterX}, {first.CenterY}) index {first.Index}");
            }

            Console.WriteLine();
        }

        return 0;
    }

    private static int Actions(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: actions <uokrPath> <body> [--max <n>]");

            return 1;
        }

        using KrAssetStore store = new(args[0]);

        int body = ParseInt(args[1]);
        int max = ParseInt(GetOption(args, "--max") ?? "63");
        List<int> actions = store.FindActions(body, max);

        Console.WriteLine($"body {body}: {actions.Count} action(s): {string.Join(", ", actions)}");

        return actions.Count == 0 ? 3 : 0;
    }

    private static int Bodies(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: bodies <uokrPath> [--max <n>]");

            return 1;
        }

        using KrAssetStore store = new(args[0]);

        int max = ParseInt(GetOption(args, "--max") ?? "2048");
        List<int> bodies = store.FindBodies(max);

        Console.WriteLine($"{bodies.Count} bodies with KR animations (0..{max}):");
        Console.WriteLine(string.Join(", ", bodies));

        return 0;
    }

    private static int Sequence(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: sequence <uokrPath> <body>");

            return 1;
        }

        using KrAssetStore store = new(args[0]);

        int body = ParseInt(args[1]);
        KrAnimationSequence? sequence = store.LoadSequence(body);

        if (sequence is null)
        {
            Console.Error.WriteLine($"no animation sequence for body {body}.");

            return 3;
        }

        Console.WriteLine($"body {sequence.BodyId}: {sequence.Actions.Count} actions, {sequence.TrailingData.Length} trailing bytes (undecoded)");

        foreach (KrAnimationAction a in sequence.Actions)
        {
            if (a.HasArt)
            {
                Console.Write($"  action {a.Action,2}: {a.FramesPerDirection,3} frames/dir ({a.TotalFrames,3} total) at {a.FramesPerSecond,5:0.##} fps");
            }
            else
            {
                int resolved = sequence.ResolveAction(a.Action);
                Console.Write($"  action {a.Action,2}: no art, falls back to {a.FallbackAction} (resolves to {resolved})");
            }

            if (a.Events.Length != 0)
            {
                Console.Write("  events: " + string.Join(", ", a.Events.Select(e => $"frame {e.Frame} sound {e.SoundId}")));
            }

            Console.WriteLine();
        }

        return 0;
    }

    /// <summary>
    /// Cross-checks AnimationSequence.uop against the actual frame files: an
    /// action the sequence claims art for must exist and have exactly
    /// framesPerDirection * 5 frames, and one it does not claim must be absent.
    /// </summary>
    private static int VerifySequences(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: verify-sequences <uokrPath> [--max-body <n>]");

            return 1;
        }

        using KrAssetStore store = new(args[0]);

        int maxBody = ParseInt(GetOption(args, "--max-body") ?? "2048");
        List<int> bodies = store.FindSequenceBodies(maxBody);

        int checkedActions = 0;
        int missingArt = 0;
        int frameMismatch = 0;
        int unexpectedArt = 0;

        foreach (int body in bodies)
        {
            KrAnimationSequence? sequence = store.LoadSequence(body);

            if (sequence is null)
            {
                continue;
            }

            foreach (KrAnimationAction a in sequence.Actions)
            {
                checkedActions++;

                byte[]? raw = store.ReadAnimationFrameFile(body, a.Action);

                if (a.HasArt)
                {
                    if (raw is null)
                    {
                        missingArt++;
                        Console.WriteLine($"  body {body} action {a.Action}: sequence claims art, no frame file");

                        continue;
                    }

                    AmouAnimation animation = AmouAnimation.Parse(raw);

                    if (animation.Frames.Count != a.TotalFrames)
                    {
                        frameMismatch++;
                        Console.WriteLine(
                            $"  body {body} action {a.Action}: sequence says {a.TotalFrames} frames, file has {animation.Frames.Count}");
                    }
                }
                else if (raw is not null)
                {
                    unexpectedArt++;
                    Console.WriteLine($"  body {body} action {a.Action}: sequence says no art, but a frame file exists");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine($"bodies with sequences  : {bodies.Count} (scanned 0..{maxBody})");
        Console.WriteLine($"actions checked        : {checkedActions}");
        Console.WriteLine($"claimed art missing    : {missingArt}");
        Console.WriteLine($"frame-count mismatches : {frameMismatch}");
        Console.WriteLine($"unexpected art present : {unexpectedArt}");

        return missingArt + frameMismatch + unexpectedArt == 0 ? 0 : 4;
    }

    /// <summary>
    /// Sweeps the animation packages and checks the format assumptions: the
    /// frame-table field at 0x00 equals the action number, frame counts divide
    /// by five, and every frame decodes without running off the end.
    /// </summary>
    private static int Verify(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: verify <uokrPath> [--max-body <n>] [--max-action <n>] [--decode]");

            return 1;
        }

        using KrAssetStore store = new(args[0]);

        int maxBody = ParseInt(GetOption(args, "--max-body") ?? "1200");
        int maxAction = ParseInt(GetOption(args, "--max-action") ?? "63");
        bool decode = HasFlag(args, "--decode");

        int files = 0;
        int bodies = 0;
        int badAction = 0;
        int badSplit = 0;
        int badDecode = 0;
        long framesDecoded = 0;
        SortedDictionary<int, int> perDirectionCounts = [];

        for (int body = 0; body <= maxBody; body++)
        {
            bool any = false;

            for (int action = 0; action <= maxAction; action++)
            {
                byte[]? raw = store.ReadAnimationFrameFile(body, action);

                if (raw is null)
                {
                    continue;
                }

                any = true;
                files++;

                AmouAnimation animation;

                try
                {
                    animation = AmouAnimation.Parse(raw);
                }
                catch (Exception ex)
                {
                    badDecode++;
                    Console.WriteLine($"  body {body} action {action}: parse failed - {ex.Message}");

                    continue;
                }

                if (animation.Action != action)
                {
                    badAction++;
                    Console.WriteLine($"  body {body} action {action}: field 0x00 is {animation.Action}");
                }

                if (!animation.HasRegularDirections)
                {
                    badSplit++;
                    Console.WriteLine($"  body {body} action {action}: {animation.Frames.Count} frames, not a multiple of {AmouAnimation.StoredDirectionCount}");

                    continue;
                }

                perDirectionCounts[animation.FramesPerDirection] =
                    perDirectionCounts.GetValueOrDefault(animation.FramesPerDirection) + 1;

                if (!decode)
                {
                    continue;
                }

                foreach (AmouFrameInfo info in animation.Frames)
                {
                    if (info.IsEmpty)
                    {
                        continue;
                    }

                    try
                    {
                        animation.Decode(info);
                        framesDecoded++;
                    }
                    catch (Exception ex)
                    {
                        badDecode++;
                        Console.WriteLine($"  body {body} action {action} frame {info.Index}: {ex.Message}");
                    }
                }
            }

            if (any)
            {
                bodies++;
            }
        }

        Console.WriteLine();
        Console.WriteLine($"bodies with animations : {bodies} (scanned 0..{maxBody})");
        Console.WriteLine($"animation files        : {files}");
        Console.WriteLine($"field 0x00 != action   : {badAction}");
        Console.WriteLine($"frames not divisible   : {badSplit}");
        Console.WriteLine($"decode failures        : {badDecode}");

        if (decode)
        {
            Console.WriteLine($"frames decoded         : {framesDecoded}");
        }

        Console.WriteLine("frames per direction   : " + string.Join(", ", perDirectionCounts.Select(kv => $"{kv.Key}x{kv.Value}")));

        return badAction + badSplit + badDecode == 0 ? 0 : 4;
    }

    private static int Facet(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: facet <uokrPath> <facet> [--sector <n>]");

            return 1;
        }

        int facet = ParseInt(args[1]);
        string path = Path.Combine(args[0], KrFacetFile.PackageName(facet));

        using KrFacetFile file = KrFacetFile.Open(path, facet);

        Console.WriteLine($"facet {facet}: {file.Width} x {file.Height} tiles");
        Console.WriteLine($"sectors  {file.SectorsX} x {file.SectorsY} = {file.SectorCount} ({KrFacetFile.SectorSize}x{KrFacetFile.SectorSize} tiles each)");

        string? sectorOption = GetOption(args, "--sector");

        if (sectorOption is null)
        {
            return 0;
        }

        int sector = ParseInt(sectorOption);

        if (!file.TryParseSector(sector, out KrSector parsed))
        {
            Console.WriteLine($"sector {sector}: did not parse");

            return 4;
        }

        (int originX, int originY) = file.WorldCoordinates(sector, 0);

        Console.WriteLine(
            $"sector {sector}: facet {parsed.Facet}, stored index {parsed.Index}, " +
            $"origin ({originX},{originY}), {parsed.Seams.Length} seam(s), {parsed.Statics.Length} static(s)");

        Dictionary<ushort, int> tiles = [];

        foreach (KrLandTile t in parsed.Tiles)
        {
            tiles[t.LandTileId] = tiles.GetValueOrDefault(t.LandTileId) + 1;
        }

        Console.WriteLine("  most common land tiles: " +
            string.Join(", ", tiles.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key}x{k.Value}")));

        Dictionary<ushort, int> terrains = [];

        foreach (KrLandTile t in parsed.Tiles)
        {
            terrains[t.TerrainId] = terrains.GetValueOrDefault(t.TerrainId) + 1;
        }

        Console.WriteLine("  most common terrain ids: " +
            string.Join(", ", terrains.OrderByDescending(k => k.Value).Take(5).Select(k => $"{k.Key}x{k.Value}")));

        return 0;
    }

    /// <summary>
    /// Sweeps every sector of every facet and reports how many parse to exactly
    /// their last byte, which is the check that the land-record model held.
    /// </summary>
    private static int VerifyFacets(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: verify-facets <uokrPath>");

            return 1;
        }

        int totalOk = 0;
        int totalBad = 0;

        for (int facet = 0; facet <= 4; facet++)
        {
            string path = Path.Combine(args[0], KrFacetFile.PackageName(facet));

            if (!File.Exists(path))
            {
                continue;
            }

            using KrFacetFile file = KrFacetFile.Open(path, facet);

            int ok = 0;
            int bad = 0;
            int missing = 0;
            int indexMismatch = 0;

            for (int sector = 0; sector < file.SectorCount; sector++)
            {
                byte[]? data = file.ReadSector(sector);

                if (data is null)
                {
                    missing++;

                    continue;
                }

                if (KrFacetFile.TryParseSector(data, out KrSector parsed))
                {
                    ok++;

                    if (parsed.Index != sector)
                    {
                        indexMismatch++;
                    }
                }
                else
                {
                    bad++;
                }
            }

            Console.WriteLine(
                $"facet {facet}: {file.Width}x{file.Height}, {file.SectorsX}x{file.SectorsY}={file.SectorCount} sectors | " +
                $"{ok} parsed, {bad} failed, {missing} missing, {indexMismatch} index mismatches");

            totalOk += ok;
            totalBad += bad;
        }

        Console.WriteLine();
        Console.WriteLine($"total: {totalOk} sectors parsed exactly, {totalBad} failed");

        return 0;
    }

    private static int Terrain(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: terrain <uokrPath> [--id <n>] [--limit <n>]");

            return 1;
        }

        using UopFile file = UopFile.Open(Path.Combine(args[0], KrTerrainDefinition.PackageName));
        using UopFile art = UopFile.Open(Path.Combine(args[0], KrTexturePackages.WorldArtPackage));

        KrStringDictionary strings =
            KrStringDictionary.Load(Path.Combine(args[0], KrStringDictionary.PackageName));

        List<KrTerrainDefinition> all = KrTerrainDefinition.LoadAll(file);

        string Describe(uint index)
        {
            string? name = strings[index];
            string? entry = KrTerrainDefinition.WorldArtEntryName(name);

            if (entry is null)
            {
                return $"{name ?? "<out of range>"}  (a label, not a file name)";
            }

            return $"{name}  -> {entry} {(art.Read(entry) is null ? "MISSING" : "present")}";
        }

        string? idOption = GetOption(args, "--id");

        if (idOption is not null)
        {
            int wanted = ParseInt(idOption);
            KrTerrainDefinition? one = all.FirstOrDefault(t => t.Id == wanted);

            if (one is null)
            {
                Console.Error.WriteLine($"no terrain definition with id {wanted}.");

                return 4;
            }

            Console.WriteLine($"terrain {one.Id} ({one.Raw.Length} bytes, {one.ItemCount} item(s), {one.Layers.Length} layer(s))");
            Console.WriteLine($"  name  {Describe(one.NameStringIndex)}");
            Console.WriteLine($"  base  {Describe(one.BaseTextureStringIndex)}");

            foreach (KrTerrainLayer layer in one.Layers)
            {
                Console.WriteLine($"  slot {layer.Slot} x{layer.Scale,-7:0.###} {Describe(layer.TextureStringIndex)}");
            }

            if (args.Contains("--raw"))
            {
                Console.WriteLine("  raw: " + Convert.ToHexString(one.Raw));

                for (int o = 0; o + 4 <= one.Raw.Length; o += 1)
                {
                    uint v = BitConverter.ToUInt32(one.Raw, o);

                    if (v >= 1000 && v < (uint)strings.Count)
                    {
                        Console.WriteLine($"  +0x{o:X2} u32 {v} {strings[(int)v]}");
                    }
                }
            }

            return 0;
        }

        int limit = ParseInt(GetOption(args, "--limit") ?? "10");

        Console.WriteLine($"{all.Count} terrain definitions (package holds {file.Entries.Count} entries)");
        Console.WriteLine($"string dictionary: {strings.Count} strings");

        foreach (KrTerrainDefinition t in all.Take(limit))
        {
            Console.WriteLine($"  id {t.Id,3}: {strings[t.NameStringIndex]} ({t.Layers.Length} layers, {t.Raw.Length} bytes, exponent {t.BlendExponent})");
        }

        return 0;
    }

    /// <summary>
    /// Parses every terrain definition and follows each texture reference into
    /// Texture.uop, which is the check that the record grammar and the
    /// name-to-entry rule both hold.
    /// </summary>
    private static int VerifyTerrain(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: verify-terrain <uokrPath>");

            return 1;
        }

        using UopFile file = UopFile.Open(Path.Combine(args[0], KrTerrainDefinition.PackageName));
        using UopFile art = UopFile.Open(Path.Combine(args[0], KrTexturePackages.WorldArtPackage));

        KrStringDictionary strings =
            KrStringDictionary.Load(Path.Combine(args[0], KrStringDictionary.PackageName));

        List<KrTerrainDefinition> all = KrTerrainDefinition.LoadAll(file);

        int refs = 0;
        int named = 0;
        int present = 0;
        int missing = 0;
        Dictionary<int, int> layerCounts = [];

        foreach (KrTerrainDefinition t in all)
        {
            layerCounts[t.Layers.Length] = layerCounts.GetValueOrDefault(t.Layers.Length) + 1;

            List<uint> indexes = [t.BaseTextureStringIndex];
            indexes.AddRange(t.Layers.Select(l => l.TextureStringIndex));

            foreach (uint index in indexes)
            {
                refs++;

                string? entry = KrTerrainDefinition.WorldArtEntryName(strings[index]);

                if (entry is null)
                {
                    continue;
                }

                named++;

                if (art.Read(entry) is not null)
                {
                    present++;
                }
                else
                {
                    missing++;
                    Console.WriteLine($"  terrain {t.Id}: {strings[index]} -> {entry} is absent");
                }
            }
        }

        Console.WriteLine($"{all.Count} terrain definitions parsed, all landing on a {KrTerrainDefinition.TailSize}-byte tail");
        Console.WriteLine($"  layer counts: {string.Join(", ", layerCounts.OrderBy(k => k.Key).Select(k => $"{k.Key} layers x{k.Value}"))}");
        Console.WriteLine($"  texture references: {refs}");
        Console.WriteLine($"    with an 8-digit file name: {named}");
        Console.WriteLine($"    present in {KrTexturePackages.WorldArtPackage}: {present}");
        Console.WriteLine($"    absent: {missing}");
        Console.WriteLine($"    labels or shader class names: {refs - named}");

        return missing == 0 ? 0 : 4;
    }

    /// <summary>
    /// Builds the land tile id to terrain id table and reports how well the
    /// mapping behaves, which is what the renderer relies on.
    /// </summary>
    // Which block of world art each painted terrain ends up drawing from.
    // The ids split into three ranges: 0000xxxx is art keyed by a classic
    // graphic id, 0200xxxx (and 0100xxxx) are KR's own blocks. A terrain that
    // resolves into the 0000xxxx range is being painted with the art for some
    // static, which is why a few of them looked like the 2D client's art.
    // Compares the KR ground each land tile id gets against the 2D art for
    // the same id. They are different renderings of the same ground, so the
    // mean colour should be close; a large gap means the chain picked a
    // different material, which is what "the wrong tile ids" looks like on
    // screen. Weighted by how much of the map each id covers.
    // Scores candidate ground rules against the 2D art for the same land tile
    // id. The two renderings differ, so the absolute number means little; what
    // matters is which rule is closest, tile-weighted.
    // What else KR puts on a tile that carries a given static. If KR drew a
    // tree as a trunk plus a separate crown of leaves, the two would show up
    // here as near-constant companions.
    private static int StaticPairs(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: static-pairs <uokrPath> --art <n>[,<n>...] [--facet <n>]");

            return 1;
        }

        int facet = ParseInt(GetOption(args, "--facet") ?? "0");

        HashSet<uint> wanted = [];

        foreach (string part in (GetOption(args, "--art") ?? "3286").Split(
            ',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out int v))
            {
                wanted.Add((uint)v);
            }
        }

        string path = Path.Combine(args[0], KrFacetFile.PackageName(facet));

        using KrFacetFile file = KrFacetFile.Open(path, facet);

        Dictionary<uint, int> alone = [];
        Dictionary<(uint Art, uint With), int> together = [];

        int tiles = 0;

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

                foreach (KrStatic st in statics)
                {
                    if (!wanted.Contains(st.ArtId))
                    {
                        continue;
                    }

                    tiles++;
                    alone.TryGetValue(st.ArtId, out int had);
                    alone[st.ArtId] = had + 1;

                    foreach (KrStatic other in statics)
                    {
                        if (other.ArtId == st.ArtId)
                        {
                            continue;
                        }

                        together.TryGetValue((st.ArtId, other.ArtId), out int h2);
                        together[(st.ArtId, other.ArtId)] = h2 + 1;
                    }
                }
            }
        }

        Console.WriteLine($"facet {facet}: {tiles:N0} placements of the requested art");

        foreach ((uint art, int count) in alone.OrderByDescending(e => e.Value))
        {
            Console.WriteLine();
            Console.WriteLine($"art {art} appears on {count:N0} tiles, sharing them with:");

            var mates = together
                .Where(e => e.Key.Art == art)
                .OrderByDescending(e => e.Value)
                .Take(8);

            bool any = false;

            foreach (var m in mates)
            {
                any = true;

                Console.WriteLine(
                    $"    art {m.Key.With,6}  on {m.Value,7:N0} of them  ({(double)m.Value / count:P1})");
            }

            if (!any)
            {
                Console.WriteLine("    nothing - it is always alone on its tile");
            }
        }

        return 0;
    }

    // Every aligned float in a terrain definition that looks like a small
    // parameter, so terrains can be compared field by field.
    private static int TerrainFloats(string[] args)
    {
        using KrAssetStore store = new(args[0]);
        KrTerrainArt art = new(store);

        foreach (string a in args.Skip(1))
        {
            if (!int.TryParse(a, out int id) || art.Definition(id) is not { } d)
            {
                continue;
            }

            byte[] b = d.Raw;

            if (args.Contains("--exp"))
            {
                Console.WriteLine($"{id,3} {BitConverter.ToSingle(b, 0x0C),8:0.###}  {art.Strings?[d.NameStringIndex]}");

                continue;
            }

            List<string> parts = [];

            for (int o = 0; o + 4 <= b.Length; o++)
            {
                float f = BitConverter.ToSingle(b, o);

                if (f != 0 && !float.IsNaN(f) && Math.Abs(f) >= 0.01f && Math.Abs(f) <= 64f
                    && Math.Abs(f - MathF.Round(f * 100) / 100) < 1e-4)
                {
                    parts.Add($"+{o:X2}:{f:0.###}");
                }
            }

            Console.WriteLine($"terrain {id,3} len {b.Length}: {string.Join(" ", parts)}");
        }

        return 0;
    }

    private static int TerrainRules(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: terrain-rules <uokrPath> <uoPath>");

            return 1;
        }

        KrTerrainMap map = KrTerrainMap.Build(args[0], [0, 1, 2, 3, 4]);

        using KrAssetStore store = new(args[0]);

        KrTerrainArt art = new(store);

        string[] names =
        [
            "single layer (default)",
            "single layer, whole texture",
            "base only",
            "most opaque layer, no base",
            "name first, then chain",
            "layered set (BlendLayers)"
        ];
        double[] weighted = new double[names.Length];
        double[] tones = new double[names.Length];
        long[] totals = new long[names.Length];

        for (int graphic = 0; graphic < KrTerrainMap.LandGraphicCount; graphic++)
        {
            if (!map.TryGet(graphic, out ushort terrain))
            {
                continue;
            }

            uint[]? twoD = TerrainPreview.ReadLandPublic(args[1], graphic);

            if (twoD is null)
            {
                continue;
            }

            long tiles = map.TileCount(graphic);

            (string? groundId, _) = art.GroundArtChoice(terrain);
            string? baseId = art.BaseArtId(terrain);

            uint[]?[] candidates =
            [
                art.GroundTile(terrain, 44),
                Flat(art, groundId),
                Flat(art, baseId),
                Flat(art, art.MostOpaqueArtId(terrain)),
                Flat(art, art.NamedArtId(terrain) ?? groundId),
                Blended(art, terrain)
            ];

            for (int i = 0; i < candidates.Length; i++)
            {
                if (candidates[i] is null)
                {
                    continue;
                }

                weighted[i] += MeanDistance(twoD, candidates[i]!) * tiles;
                tones[i] += ToneDistance(twoD, candidates[i]!) * tiles;
                totals[i] += tiles;
            }
        }

        Console.WriteLine($"{"rule",-30} {"per-pixel",12} {"tone",12}");

        for (int i = 0; i < names.Length; i++)
        {
            double score = totals[i] == 0 ? 0 : weighted[i] / totals[i];
            double tone = totals[i] == 0 ? 0 : tones[i] / totals[i];

            Console.WriteLine($"{names[i],-30} {score,12:0.00} {tone,12:0.00}");
        }

        return 0;
    }

    /// <summary>One tile with the whole layer set composited.</summary>
    private static uint[]? Blended(KrTerrainArt art, ushort terrain)
    {
        art.BlendLayers = true;
        art.Clear();

        uint[]? tile = art.GroundTile(terrain, 44);

        art.BlendLayers = false;
        art.Clear();

        return tile;
    }

    /// <summary>One art id resampled to a 44x44 tile, opaque, nothing under it.</summary>
    private static uint[]? Flat(KrTerrainArt art, string? artId)
    {
        if (artId is null)
        {
            return null;
        }

        DdsImage? image = art.Texture(artId);

        if (image is null)
        {
            return null;
        }

        uint[] tile = KrTerrainArt.Resample(image.Pixels, image.Width, image.Height, 44, 44);

        for (int i = 0; i < tile.Length; i++)
        {
            tile[i] |= 0xFF000000;
        }

        return tile;
    }

    private static int TerrainAudit(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: terrain-audit <uokrPath> <uoPath> [--top <n>]");

            return 1;
        }

        KrTerrainMap map = KrTerrainMap.Build(args[0], [0, 1, 2, 3, 4]);

        using KrAssetStore store = new(args[0]);

        KrTerrainArt art = new(store);

        List<(int Graphic, ushort Terrain, string Id, string Step, double Dist, long Tiles)> rows = [];

        Dictionary<string, (int Count, long Tiles)> steps = [];

        for (int graphic = 0; graphic < KrTerrainMap.LandGraphicCount; graphic++)
        {
            if (!map.TryGet(graphic, out ushort terrain))
            {
                continue;
            }

            (string? id, string step) = art.GroundArtChoice(terrain);

            if (id is null)
            {
                continue;
            }

            long tiles = map.TileCount(graphic);

            steps.TryGetValue(step, out (int Count, long Tiles) had);
            steps[step] = (had.Count + 1, had.Tiles + tiles);

            uint[]? twoD = TerrainPreview.ReadLandPublic(args[1], graphic);
            uint[]? kr = art.GroundTile(terrain, 44);

            if (twoD is null || kr is null)
            {
                continue;
            }

            rows.Add((graphic, terrain, id, step, MeanDistance(twoD, kr), tiles));
        }

        Console.WriteLine($"audited {rows.Count} land tile ids");
        Console.WriteLine();
        Console.WriteLine($"{"chain step",-12} {"ids",6} {"tiles",14}");

        foreach ((string step, (int count, long tiles)) in steps.OrderByDescending(e => e.Value.Tiles))
        {
            Console.WriteLine($"{step,-12} {count,6} {tiles,14:N0}");
        }

        int top = ParseInt(GetOption(args, "--top") ?? "25");

        Console.WriteLine();
        Console.WriteLine("worst colour mismatches, by tiles covered:");
        Console.WriteLine($"{"land",6} {"terrain",8} {"art",10} {"step",-11} {"dist",7} {"tiles",12}");

        foreach (var r in rows.Where(r => r.Dist > 40).OrderByDescending(r => r.Tiles).Take(top))
        {
            Console.WriteLine(
                $"{r.Graphic,6} {r.Terrain,8} {r.Id,10} {r.Step,-11} {r.Dist,7:0.0} {r.Tiles,12:N0}");
        }

        return 0;
    }

    /// <summary>
    /// Distance between the two tiles' MEAN colours. Per-pixel distance
    /// punishes a tile for having detail where the 2D art is flat, which is
    /// exactly what compositing the layer set adds, so it cannot say whether
    /// the ground is the right material. Comparing the average tone can.
    /// </summary>
    private static double ToneDistance(uint[] a, uint[] b)
    {
        double ar = 0, ag = 0, ab = 0, br = 0, bg = 0, bb = 0;
        int n = 0;

        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            if ((a[i] >> 24) < 128)
            {
                continue;
            }

            ar += a[i] & 0xFF;
            ag += (a[i] >> 8) & 0xFF;
            ab += (a[i] >> 16) & 0xFF;

            br += b[i] & 0xFF;
            bg += (b[i] >> 8) & 0xFF;
            bb += (b[i] >> 16) & 0xFF;

            n++;
        }

        if (n == 0)
        {
            return 0;
        }

        double dr = (ar - br) / n;
        double dg = (ag - bg) / n;
        double db = (ab - bb) / n;

        return Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
    }

    private static double MeanDistance(uint[] a, uint[] b)
    {
        double total = 0;
        int n = 0;

        for (int i = 0; i < a.Length && i < b.Length; i++)
        {
            // the 2D diamond has transparent corners; only compare inside it
            if ((a[i] >> 24) < 128)
            {
                continue;
            }

            double dr = (double)(a[i] & 0xFF) - (b[i] & 0xFF);
            double dg = (double)((a[i] >> 8) & 0xFF) - ((b[i] >> 8) & 0xFF);
            double db = (double)((a[i] >> 16) & 0xFF) - ((b[i] >> 16) & 0xFF);

            total += Math.Sqrt((dr * dr) + (dg * dg) + (db * db));
            n++;
        }

        return n == 0 ? 0 : total / n;
    }

    private static int TerrainClassify(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: terrain-classify <uokrPath>");

            return 1;
        }

        KrTerrainMap map = KrTerrainMap.Build(args[0], [0, 1, 2, 3, 4]);

        using KrAssetStore store = new(args[0]);

        KrTerrainArt art = new(store);

        Dictionary<ushort, long> tiles = [];

        for (int graphic = 0; graphic < KrTerrainMap.LandGraphicCount; graphic++)
        {
            if (map.TryGet(graphic, out ushort terrain))
            {
                tiles.TryGetValue(terrain, out long had);
                tiles[terrain] = had + map.TileCount(graphic);
            }
        }

        long total = 0;

        foreach (long count in tiles.Values)
        {
            total += count;
        }

        Dictionary<string, (int Terrains, long Tiles)> blocks = [];

        List<(ushort Terrain, string Id, long Tiles)> legacy = [];

        foreach ((ushort terrain, long count) in tiles)
        {
            string? id = art.GroundArtId(terrain);
            string block = id is null ? "no texture" : BlockOf(id);

            blocks.TryGetValue(block, out (int Terrains, long Tiles) had);
            blocks[block] = (had.Terrains + 1, had.Tiles + count);

            if (id is not null && block == "0000xxxx")
            {
                legacy.Add((terrain, id, count));
            }
        }

        Console.WriteLine($"{tiles.Count} terrains are painted, covering {total:N0} tiles");
        Console.WriteLine();
        Console.WriteLine($"{"block",-12} {"terrains",8} {"tiles",14}  share");

        foreach ((string block, (int terrains, long count)) in blocks.OrderByDescending(e => e.Value.Tiles))
        {
            Console.WriteLine($"{block,-12} {terrains,8} {count,14:N0}  {(double)count / total:P2}");
        }

        Console.WriteLine();
        Console.WriteLine("terrains drawing from the classic-graphic block, worst first:");

        foreach ((ushort terrain, string id, long count) in legacy.OrderByDescending(e => e.Tiles).Take(20))
        {
            Console.WriteLine($"  terrain {terrain,3}  art {id}  {count,12:N0} tiles");
        }

        return 0;
    }

    private static string BlockOf(string artId) =>
        artId.StartsWith("02", StringComparison.Ordinal) ? "0200xxxx"
        : artId.StartsWith("01", StringComparison.Ordinal) ? "0100xxxx"
        : "0000xxxx";

    private static int TerrainMap(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: terrain-map <uokrPath> [--limit <n>]");

            return 1;
        }

        System.Diagnostics.Stopwatch watch = System.Diagnostics.Stopwatch.StartNew();

        KrTerrainMap map = KrTerrainMap.Build(args[0], [0, 1, 2, 3, 4]);

        watch.Stop();

        Console.WriteLine(
            $"swept {map.TilesScanned:N0} tiles in {watch.Elapsed.TotalSeconds:0.00}s: " +
            $"{map.Known} land tile ids mapped, {map.Ambiguous} of them ambiguous");

        using KrAssetStore store = new(args[0]);

        KrTerrainArt art = new(store);

        int withTexture = 0;
        int withoutTexture = 0;
        int limit = ParseInt(GetOption(args, "--limit") ?? "12");
        int shown = 0;

        for (int graphic = 0; graphic < KrTerrainMap.LandGraphicCount; graphic++)
        {
            if (!map.TryGet(graphic, out ushort terrain))
            {
                continue;
            }

            string? name = art.GroundArtId(terrain);

            if (name is null)
            {
                withoutTexture++;

                continue;
            }

            withTexture++;

            if (shown < limit)
            {
                Console.WriteLine($"  land {graphic,5} -> terrain {terrain,3} -> {name}");
                shown++;
            }
        }

        Console.WriteLine($"{withTexture} land tile ids resolve to a texture, {withoutTexture} do not");

        return withoutTexture == 0 ? 0 : 4;
    }

    /// <summary>Decodes DDS textures to PNG, so the terrain mapping can be eyeballed.</summary>
    private static int ExportTexture(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: export-texture <uokrPath> <terraintexture | worldart <artId> | terrain <terrainId>> [-o <dir>]");

            return 1;
        }

        string root = args[0];
        string what = args[1].ToLowerInvariant();
        string outDir = GetOption(args, "-o") ?? GetOption(args, "--out")
            ?? Path.Combine(Directory.GetCurrentDirectory(), "kr-textures");

        Directory.CreateDirectory(outDir);

        int written = 0;

        void WriteOne(UopFile package, string entry, string label)
        {
            byte[]? data = package.Read(entry);

            if (data is null)
            {
                Console.Error.WriteLine($"  {label}: {entry} is absent");

                return;
            }

            DdsImage image = DdsImage.Decode(data);
            string path = Path.Combine(outDir, $"{label}.png");

            PngWriter.Write(path, image.Width, image.Height, image.Pixels);
            Console.WriteLine($"  {label}  {image.Width}x{image.Height} {image.Format} -> {Path.GetFileName(path)}");
            written++;
        }

        if (what is "terraintexture" or "sky")
        {
            using UopFile package = UopFile.Open(Path.Combine(root, KrTexturePackages.TerrainTexturePackage));

            Console.WriteLine($"{KrTexturePackages.TerrainTexturePackage}: {package.Entries.Count} entries");
            Console.WriteLine("(despite the package name these are sky and celestial textures)");

            for (int id = 0; id <= 64; id++)
            {
                string entry = KrTexturePackages.TerrainTextureEntryName(id);

                if (package.Read(entry) is not null)
                {
                    WriteOne(package, entry, $"terraintexture_{id:D8}");
                }
            }
        }
        else if (what == "worldart")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: export-texture <uokrPath> worldart <artId> [-o <dir>]");

                return 1;
            }

            using UopFile package = UopFile.Open(Path.Combine(root, KrTexturePackages.WorldArtPackage));

            int artId = ParseInt(args[2]);
            WriteOne(package, KrTexturePackages.WorldArtEntryName(artId), $"worldart_{artId:D8}");
        }
        else if (what == "terrain")
        {
            if (args.Length < 3)
            {
                Console.Error.WriteLine("usage: export-texture <uokrPath> terrain <terrainId> [-o <dir>]");

                return 1;
            }

            int terrainId = ParseInt(args[2]);

            using UopFile defs = UopFile.Open(Path.Combine(root, KrTerrainDefinition.PackageName));
            using UopFile package = UopFile.Open(Path.Combine(root, KrTexturePackages.WorldArtPackage));

            KrStringDictionary strings =
                KrStringDictionary.Load(Path.Combine(root, KrStringDictionary.PackageName));

            byte[]? raw = defs.Read(KrTerrainDefinition.EntryName(terrainId));

            if (raw is null)
            {
                Console.Error.WriteLine($"no terrain definition with id {terrainId}.");

                return 4;
            }

            KrTerrainDefinition definition = KrTerrainDefinition.Parse(raw);

            Console.WriteLine($"terrain {terrainId}: {strings[definition.NameStringIndex]}");

            List<(string Label, uint Index)> wanted = [("base", definition.BaseTextureStringIndex)];
            wanted.AddRange(definition.Layers.Select((l, i) => ($"slot{l.Slot}_{i}", l.TextureStringIndex)));

            foreach ((string label, uint index) in wanted)
            {
                string? entry = KrTerrainDefinition.WorldArtEntryName(strings[index]);

                if (entry is null)
                {
                    Console.WriteLine($"  {label}: {strings[index]} is a label, no texture to export");

                    continue;
                }

                WriteOne(package, entry, $"terrain{terrainId:D3}_{label}");
            }
        }
        else
        {
            Console.Error.WriteLine($"unknown texture set '{args[1]}'.");

            return 1;
        }

        Console.WriteLine($"{written} PNG(s) in {outDir}");

        return 0;
    }

    /// <summary>Resolves a package's entry hashes through a Mythic Package Editor name dictionary.</summary>
    private static int Names(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: names <Dictionary.dic> <uop> [--limit <n>]");

            return 1;
        }

        UopNameDictionary dictionary = UopNameDictionary.Load(args[0]);

        using UopFile file = UopFile.Open(args[1]);

        List<string> resolved = dictionary.ResolveEntries(file);
        int limit = ParseInt(GetOption(args, "--limit") ?? "20");

        Console.WriteLine($"dictionary: {dictionary.Count} names ({dictionary.UnnamedCount} unnamed hashes)");
        Console.WriteLine($"{Path.GetFileName(file.FilePath)}: {resolved.Count} of {file.Entries.Count} entries resolved");

        foreach (string name in resolved.Take(limit))
        {
            Console.WriteLine($"  {name}");
        }

        return 0;
    }

    private static int Hash(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: hash <name>");

            return 1;
        }

        foreach (string name in args)
        {
            Console.WriteLine($"0x{UopHash.Create(name):X16}  {name}");
        }

        return 0;
    }

    private static int List(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: list <uop> [--limit <n>]");

            return 1;
        }

        using UopFile file = UopFile.Open(args[0]);

        int limit = ParseInt(GetOption(args, "--limit") ?? "25");

        Console.WriteLine($"{Path.GetFileName(file.FilePath)}: version {file.Version}, signature 0x{file.Signature:X8}, {file.Entries.Count} entries");

        for (int i = 0; i < Math.Min(limit, file.Entries.Count); i++)
        {
            UopEntry e = file.Entries[i];
            Console.WriteLine($"  [{i,5}] hash 0x{e.NameHash:X16} off {e.DataOffset,12} {e.CompressedSize,9} -> {e.DecompressedSize,9} flags {e.Flags}");
        }

        return 0;
    }

    private static int Probe(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: probe <uop> [--limit <n>]");

            return 1;
        }

        using UopFile file = UopFile.Open(args[0]);

        int limit = ParseInt(GetOption(args, "--limit") ?? "20");
        Dictionary<string, int> kinds = new(StringComparer.Ordinal);

        Console.WriteLine($"{Path.GetFileName(file.FilePath)}: {file.Entries.Count} entries");

        for (int i = 0; i < file.Entries.Count; i++)
        {
            UopEntry e = file.Entries[i];
            byte[] payload;

            try
            {
                payload = file.Read(e);
            }
            catch (Exception ex)
            {
                kinds[$"<error: {ex.GetType().Name}>"] = kinds.GetValueOrDefault($"<error: {ex.GetType().Name}>") + 1;

                continue;
            }

            string kind = Identify(payload);
            kinds[kind] = kinds.GetValueOrDefault(kind) + 1;

            if (i < limit)
            {
                Console.WriteLine($"  [{i,5}] hash 0x{e.NameHash:X16} {e.DecompressedSize,9} bytes  {kind}  {Hex(payload, 24)}");
            }
        }

        Console.WriteLine("summary:");

        foreach (KeyValuePair<string, int> kv in kinds.OrderByDescending(k => k.Value))
        {
            Console.WriteLine($"  {kv.Value,7}  {kv.Key}");
        }

        return 0;
    }

    private static int Extract(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: extract <uop> <hash|0xHASH|name> <out>");

            return 1;
        }

        using UopFile file = UopFile.Open(args[0]);

        string key = args[1];
        ulong hash = key.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(key[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : UopHash.Create(key);

        if (!file.TryGetEntry(hash, out UopEntry entry))
        {
            Console.Error.WriteLine($"0x{hash:X16} not found in {Path.GetFileName(file.FilePath)}.");

            return 3;
        }

        byte[] payload = file.Read(entry);
        File.WriteAllBytes(args[2], payload);

        Console.WriteLine($"wrote {payload.Length} bytes to {args[2]} ({Identify(payload)})");

        return 0;
    }

    /// <summary>Best-effort format guess from a payload's first bytes.</summary>
    internal static string Identify(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 4)
        {
            if (data[0] == 'D' && data[1] == 'D' && data[2] == 'S' && data[3] == ' ') return "DDS";
            if (data[0] == 'A' && data[1] == 'M' && data[2] == 'O' && data[3] == 'U') return "AMOU";
            if (data[0] == 0x89 && data[1] == 'P' && data[2] == 'N' && data[3] == 'G') return "PNG";
            if (data[0] == 'B' && data[1] == 'M') return "BMP";
            if (data[0] == 'M' && data[1] == 'Y' && data[2] == 'P') return "MYP";
            if (data[0] == 0xFF && data[1] == 0xD8) return "JPEG";
        }

        if (LooksLikeText(data))
        {
            return data.Length > 5 && data[0] == '<' ? "XML/text" : "text";
        }

        return "binary";
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> data)
    {
        int sample = Math.Min(data.Length, 256);

        if (sample == 0)
        {
            return false;
        }

        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];

            if (b is not (9 or 10 or 13) && (b < 32 || b > 126))
            {
                return false;
            }
        }

        return true;
    }

    private static string Hex(ReadOnlySpan<byte> data, int count)
    {
        int n = Math.Min(count, data.Length);
        return Convert.ToHexString(data[..n]);
    }

    internal static int ParseIntPublic(string value) => ParseInt(value);

    private static int ParseInt(string value)
    {
        return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : int.Parse(value, CultureInfo.InvariantCulture);
    }

    internal static string? GetOption(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
