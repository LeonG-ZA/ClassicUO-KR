// SPDX-License-Identifier: BSD-2-Clause
// Entry point for reading assets out of a local Kingdom Reborn install.
//
// Nothing here ships with the client: the install path is a user setting and
// every byte is read from the user's own copy of UOKR.

using ClassicUO.KR.Animations;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR;

public sealed class KrAssetStore : IDisposable
{
    /// <summary>The animation frame packages, in probe order.</summary>
    public static readonly string[] AnimationFramePackages =
    [
        "AnimationFrame1.uop",
        "AnimationFrame2.uop",
        "AnimationFrame3.uop",
        "AnimationFrame4.uop",
        "AnimationFrame5.uop",
        "AnimationFrame6.uop"
    ];

    /// <summary>Package holding the per-body action metadata.</summary>
    public const string AnimationSequencePackage = "AnimationSequence.uop";

    /// <summary>Package holding the per-id animation definitions.</summary>
    public const string AnimationDefinitionPackage = "AnimationDefinition.uop";

    private readonly Dictionary<string, UopFile?> _packages = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, KrAnimationSequence?> _sequenceCache = [];
    private readonly Dictionary<long, LinkedListNode<CachedAnimation>> _animationCache = [];

    /// <summary>Most-recently-used first, so eviction takes from the tail.</summary>
    private readonly LinkedList<CachedAnimation> _animationOrder = new();

    private readonly object _sync = new();

    private UopFile?[]? _animationFrames;
    private long _animationCacheBytes;

    private sealed record CachedAnimation(long Key, AmouAnimation? Animation, int Bytes);

    /// <summary>
    /// How much inflated animation data to keep. Each file is a few hundred KB,
    /// and a direction is decoded per call, so a small budget still serves the
    /// five calls that make up one body/action without holding the whole 2 GB
    /// asset set in memory.
    /// </summary>
    public long AnimationCacheBudget { get; set; } = 64L * 1024 * 1024;

    public KrAssetStore(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("KR install path is empty.", nameof(rootPath));
        }

        RootPath = Path.GetFullPath(rootPath);

        if (!Directory.Exists(RootPath))
        {
            throw new DirectoryNotFoundException($"KR install folder not found: {RootPath}");
        }
    }

    public string RootPath { get; }

    /// <summary>True when the folder looks like a UOKR install we can read animations from.</summary>
    public static bool LooksLikeKrInstall(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath))
        {
            return false;
        }

        return File.Exists(Path.Combine(rootPath, AnimationFramePackages[0]));
    }

    public static string AnimationFrameName(int body, int action) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"build/animationframe/{body:D6}/{action:D2}.bin");

    public static string AnimationDefinitionName(int id) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"build/animationdefinition/{id:D8}.bin");

    public static string AnimationSequenceName(int body) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"build/animationsequence/{body:D8}.bin");

    /// <summary>Per-body action metadata, cached. Null when KR ships no sequence for the body.</summary>
    public KrAnimationSequence? LoadSequence(int body)
    {
        lock (_sync)
        {
            if (_sequenceCache.TryGetValue(body, out KrAnimationSequence? cached))
            {
                return cached;
            }
        }

        UopFile? package = GetPackage(AnimationSequencePackage);
        KrAnimationSequence? sequence = null;

        if (package is not null)
        {
            byte[]? raw;

            lock (_sync)
            {
                raw = package.Read(AnimationSequenceName(body));
            }

            if (raw is not null)
            {
                sequence = KrAnimationSequence.Parse(raw);
            }
        }

        lock (_sync)
        {
            _sequenceCache[body] = sequence;
        }

        return sequence;
    }

    /// <summary>Probes which body ids have an animation sequence record.</summary>
    public List<int> FindSequenceBodies(int maxBody = 2048)
    {
        List<int> bodies = [];
        UopFile? package = GetPackage(AnimationSequencePackage);

        if (package is null)
        {
            return bodies;
        }

        for (int body = 0; body <= maxBody; body++)
        {
            if (package.Contains(AnimationSequenceName(body)))
            {
                bodies.Add(body);
            }
        }

        return bodies;
    }

    /// <summary>Opens a package by file name, caching it. Returns null when the file is absent.</summary>
    public UopFile? GetPackage(string fileName)
    {
        lock (_sync)
        {
            if (_packages.TryGetValue(fileName, out UopFile? cached))
            {
                return cached;
            }

            string full = Path.Combine(RootPath, fileName);
            UopFile? file = null;

            if (File.Exists(full))
            {
                file = UopFile.Open(full);
            }

            _packages[fileName] = file;

            return file;
        }
    }

    private UopFile?[] AnimationFrames()
    {
        lock (_sync)
        {
            if (_animationFrames is null)
            {
                _animationFrames = new UopFile?[AnimationFramePackages.Length];

                for (int i = 0; i < AnimationFramePackages.Length; i++)
                {
                    _animationFrames[i] = GetPackage(AnimationFramePackages[i]);
                }
            }

            return _animationFrames;
        }
    }

    /// <summary>
    /// Finds the raw (still compressed) entry for a body/action across the six
    /// animation packages, and reports which package held it.
    /// </summary>
    public bool TryFindAnimationFrame(int body, int action, out UopFile package, out UopEntry entry)
    {
        ulong hash = UopHash.Create(AnimationFrameName(body, action));

        foreach (UopFile? file in AnimationFrames())
        {
            if (file is not null && file.TryGetEntry(hash, out entry))
            {
                package = file;

                return true;
            }
        }

        package = null!;
        entry = default;

        return false;
    }

    /// <summary>Reads and inflates one body/action animation file, or null when it does not exist.</summary>
    public byte[]? ReadAnimationFrameFile(int body, int action)
    {
        if (!TryFindAnimationFrame(body, action, out UopFile package, out UopEntry entry))
        {
            return null;
        }

        lock (_sync)
        {
            return package.Read(entry);
        }
    }

    /// <summary>Loads and parses one body/action animation, cached. Null when KR has no such file.</summary>
    public AmouAnimation? LoadAnimation(int body, int action)
    {
        long key = ((long)body << 32) | (uint)action;

        lock (_sync)
        {
            if (_animationCache.TryGetValue(key, out LinkedListNode<CachedAnimation>? hit))
            {
                _animationOrder.Remove(hit);
                _animationOrder.AddFirst(hit);

                return hit.Value.Animation;
            }
        }

        byte[]? data = ReadAnimationFrameFile(body, action);
        AmouAnimation? animation = data is null ? null : AmouAnimation.Parse(data);

        lock (_sync)
        {
            // Another thread may have loaded it while we were inflating.
            if (_animationCache.TryGetValue(key, out LinkedListNode<CachedAnimation>? raced))
            {
                _animationOrder.Remove(raced);
                _animationOrder.AddFirst(raced);

                return raced.Value.Animation;
            }

            int bytes = data?.Length ?? 0;
            LinkedListNode<CachedAnimation> node = _animationOrder.AddFirst(new CachedAnimation(key, animation, bytes));
            _animationCache[key] = node;
            _animationCacheBytes += bytes;

            Trim();
        }

        return animation;
    }

    /// <summary>
    /// Drops every cached animation. Needed when something that changes how a
    /// frame is produced is altered at runtime, such as the frame scale: the
    /// cache holds parsed files rather than decoded bitmaps, but callers key
    /// their own decoded results off these, so they are invalidated together.
    /// </summary>
    public void ClearAnimationCache()
    {
        lock (_sync)
        {
            _animationCache.Clear();
            _animationOrder.Clear();
            _animationCacheBytes = 0;
        }
    }

    /// <summary>Drops least-recently-used animations until the cache fits its budget.</summary>
    private void Trim()
    {
        while (_animationCacheBytes > AnimationCacheBudget && _animationOrder.Last is { } last)
        {
            // Keep at least one entry so a single oversized file still caches.
            if (_animationOrder.Count == 1)
            {
                break;
            }

            _animationOrder.RemoveLast();
            _animationCache.Remove(last.Value.Key);
            _animationCacheBytes -= last.Value.Bytes;
        }
    }

    public bool HasAnimation(int body, int action) => TryFindAnimationFrame(body, action, out _, out _);

    /// <summary>
    /// Probes which action files exist for a body. Names are hashed, so the only
    /// way to enumerate is to try each candidate.
    /// </summary>
    public List<int> FindActions(int body, int maxAction = 63)
    {
        List<int> actions = [];

        for (int action = 0; action <= maxAction; action++)
        {
            if (HasAnimation(body, action))
            {
                actions.Add(action);
            }
        }

        return actions;
    }

    /// <summary>Probes which body ids have at least one action file.</summary>
    public List<int> FindBodies(int maxBody = 2048, int maxAction = 63)
    {
        List<int> bodies = [];

        for (int body = 0; body <= maxBody; body++)
        {
            for (int action = 0; action <= maxAction; action++)
            {
                if (HasAnimation(body, action))
                {
                    bodies.Add(body);

                    break;
                }
            }
        }

        return bodies;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (UopFile? file in _packages.Values)
            {
                file?.Dispose();
            }

            _packages.Clear();
            _animationCache.Clear();
            _animationOrder.Clear();
            _animationCacheBytes = 0;
            _sequenceCache.Clear();
            _animationFrames = null;
        }
    }
}
