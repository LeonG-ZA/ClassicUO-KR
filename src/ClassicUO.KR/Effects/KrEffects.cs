// SPDX-License-Identifier: BSD-2-Clause
//
// KR's effects from a local install: a definition id (what a 0xC7 packet
// carries) to the scene it plays, and the scripts and textures behind it.
//
//   EffectDefinitionCollection.uop  build/effectdefinitioncollection/{id:D8}.bin
//                                   u32 count, then 31-byte parts (Parts):
//                                   scene, height, scale, duration
//   EffectTexture.uop               data/effects/{name}  (.nif, .ems, .tga)
//
//

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.Effects;

/// <summary>
/// One scene a definition record plays: <paramref name="Height"/> in UO z
/// units above the feet, <paramref name="Scale"/> of the scene in world units
/// (a tile edge), <paramref name="Duration"/> in seconds (0: until it ends).
/// </summary>
public readonly record struct KrEffectPart(string Scene, float Height, float Scale, float Duration);

public sealed class KrEffects
{
    public const string DefinitionPackage = "EffectDefinitionCollection.uop";
    public const string ScenePackage = "EffectTexture.uop";

    private readonly KrAssetStore _store;
    private readonly KrStringDictionary _strings;
    private readonly Dictionary<int, string?> _sceneById = [];
    private readonly Dictionary<string, EmsScript?> _scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NifFile?> _scenes = new(StringComparer.OrdinalIgnoreCase);

    public KrEffects(KrAssetStore store)
    {
        _store = store;
        _strings = KrStringDictionary.Load(Path.Combine(store.RootPath, KrStringDictionary.PackageName));
    }

    /// <summary>
    /// Effects to fall back on for a scene or texture this store cannot
    /// supply - KR's, behind the Enhanced Client's, whose newer scenes use
    /// NIF blocks not read yet.
    /// </summary>
    public KrEffects? Fallback { get; init; }

    public static string DefinitionName(int id) => $"build/effectdefinitioncollection/{id:D8}.bin";

    public static string SceneEntryName(string scene) => "data/effects/" + scene;

    private Dictionary<int, string>? _sceneByNumber;

    /// <summary>
    /// The scene a server effect id plays: the scene whose file name is
    /// numbered one less (RunUO's Magic Arrow projectile 3006 →
    /// 00003005_Magic_arrow.ems, Weaken 5009 → 00005008_Weaken.nif). Measured
    /// over RunUO's spells: 40 of 58 effect ids name their own spell this
    /// way, against at most 2 for the id itself or the definition records.
    /// </summary>
    public string? SceneForServerEffect(int effectId) =>
        ScenesByNumber().TryGetValue(effectId - 1, out string? scene) ? scene : null;

    /// <summary>
    /// The scene KR plays for a moving effect, numbered by its 2D item
    /// graphic: Fireball travels as 0x36D4 = 14036, and KR draws
    /// 00014036_LargeFireball.nif (explosion 0x36BD = 14013,
    /// 00014013_Explosion2.nif). From KR casting Fireball
    ///.
    /// </summary>
    public string? SceneForGraphic(int graphic) =>
        ScenesByNumber().TryGetValue(graphic, out string? scene) ? scene : null;

    private Dictionary<int, string> ScenesByNumber()
    {
        if (_sceneByNumber is null)
        {
            _sceneByNumber = [];
            UopFile? package = _store.GetPackage(ScenePackage);

            for (int i = 0; i < _strings.Count && package is not null; i++)
            {
                string? s = _strings[i];

                if (s is { Length: > 9 } && (s[8] == '_' || s[8] == '.')
                    && int.TryParse(s.AsSpan(0, 8), out int number)
                    && (s.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || s.EndsWith(".ems", StringComparison.OrdinalIgnoreCase))
                    && package.Contains(SceneEntryName(s)))
                {
                    _sceneByNumber.TryAdd(number, s);
                }
            }
        }

        return _sceneByNumber;
    }

    private Dictionary<int, string>? _sceneByBody;
    private readonly Dictionary<int, bool> _hasFrames = [];

    /// <summary>
    /// The particle scene KR draws for a creature it has no animation for,
    /// or null. Scenes numbered 10,000,000 + body are a creature's own:
    /// 10000164_EnergyVortex.nif (body 164), 10000790_SandVortex.nif (790),
    /// ten in all; some of those bodies are also animated (the elementals
    /// 14-16), where the scene is presumably drawn with the animation, so it
    /// replaces the sprite only when the body has no frames. The wisp (58)
    /// is not numbered that way: the only reference to 00008448_wisp.nif is
    /// tileart record 8448 (0x2100), the 2D wisp statuette, so it is written
    /// out here.
    /// </summary>
    public string? SceneForBody(int body, bool withFrames = false)
    {
        // A creature KR animates can still carry a scene: the Fire
        // Elemental's 10000015_Elemental_Fire.nif is the fireball under it.
        // KR shows only the Fire and Water Elementals' (15, 16); the Earth,
        // Acid, Blood, Poison and Snow Elementals have scenes it does not play.
        // The Water Elemental's is off until it matches KR's (parked
        // 2026-11-04: ours is not KR's bucket shape round the feet).
        if (HasFrames(body) && (!withFrames || body != 15))
        {
            return null;
        }

        if (body == 58)
        {
            return "00008448_wisp.nif";
        }

        if (_sceneByBody is null)
        {
            _sceneByBody = [];
            UopFile? package = _store.GetPackage(ScenePackage);

            for (int i = 0; i < _strings.Count && package is not null; i++)
            {
                string? s = _strings[i];

                if (s is { Length: > 9 } && s[0] == '1' && (s[8] == '_' || s[8] == '.')
                    && int.TryParse(s.AsSpan(1, 7), out int b)
                    && s.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
                    && package.Contains(SceneEntryName(s)))
                {
                    _sceneByBody.TryAdd(b, s);
                }
            }
        }

        return _sceneByBody.TryGetValue(body, out string? scene) ? scene : null;
    }

    /// <summary>Whether any AnimationFrame package holds frames for <paramref name="body"/>.</summary>
    public bool HasFrames(int body)
    {
        if (_hasFrames.TryGetValue(body, out bool cached))
        {
            return cached;
        }

        bool found = false;

        foreach (string name in KrAssetStore.AnimationFramePackages)
        {
            UopFile? package = _store.GetPackage(name);

            for (int action = 0; action < 40 && package is not null && !found; action++)
            {
                found = package.Contains(KrAssetStore.AnimationFrameName(body, action));
            }

            if (found)
            {
                break;
            }
        }

        _hasFrames[body] = found;

        return found;
    }

    /// <summary>
    /// What a definition record plays: u32 count, then count entries of 31
    /// bytes - u32 0, u32 scene string index, f32 x2 (0), f32 height (UO z
    /// units above the feet: Heal 5, Reactive Armor 10), f32 scale, u8 1,
    /// f32 duration (s), u8, u8 (0/1 - most scenes are listed twice, once
    /// each). KR drew both copies, at the record's scale - one
    /// scene unit a world unit (a tile edge) times it - and stopped at its
    /// duration. Record 5029 (Greater Heal) lists 00005029_GreaterHeal.nif
    /// and 00005004_heal.nif; KR drew only Heal's particles.
    /// </summary>
    public IReadOnlyList<KrEffectPart> Parts(int definitionId)
    {
        if (_parts.TryGetValue(definitionId, out IReadOnlyList<KrEffectPart>? cached))
        {
            return cached;
        }

        List<KrEffectPart> parts = [];
        byte[]? data = _store.GetPackage(DefinitionPackage)?.Read(DefinitionName(definitionId));

        if (data is { Length: >= 4 })
        {
            int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(data);

            for (int i = 0, o = 4; i < count && o + 31 <= data.Length; i++, o += 31)
            {
                uint index = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(o + 4));

                if (index < (uint)_strings.Count && _strings[(int)index] is { Length: > 0 } scene)
                {
                    parts.Add(new KrEffectPart(
                        scene,
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o + 16)),
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o + 20)),
                        BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(o + 25))));
                }
            }
        }

        _parts[definitionId] = parts;

        return parts;
    }

    /// <summary>The parts a server effect id plays: definition record id - 1, as <see cref="SceneForServerEffect"/>.</summary>
    public IReadOnlyList<KrEffectPart> PartsForServerEffect(int effectId) => Parts(effectId - 1);

    private readonly Dictionary<int, IReadOnlyList<KrEffectPart>> _parts = [];

    /// <summary>The scene definition record <paramref name="definitionId"/> names, or null.</summary>
    public string? SceneFor(int definitionId)
    {
        if (_sceneById.TryGetValue(definitionId, out string? cached))
        {
            return cached;
        }

        string? scene = null;
        byte[]? data = _store.GetPackage(DefinitionPackage)?.Read(DefinitionName(definitionId));

        // EC's 9919 is a record of another layout whose +8 indexes nothing.
        if (data is { Length: >= 12 } && BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8)) is var index
            && index < (uint)_strings.Count)
        {
            scene = _strings[(int)index];
        }

        _sceneById[definitionId] = scene;

        return scene;
    }

    /// <summary>The parsed script for an .ems scene, cached; null for anything else or a missing entry.</summary>
    public EmsScript? Script(string scene)
    {
        if (!scene.EndsWith(".ems", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (_scripts.TryGetValue(scene, out EmsScript? cached))
        {
            return cached;
        }

        byte[]? data = _store.GetPackage(ScenePackage)?.Read(SceneEntryName(scene));
        string? text = data is null ? null : Encoding.Latin1.GetString(data);

        // The Enhanced Client emptied its candle and torch flames (a
        // particlesystem "Empty" with one invisible particle). KR's script
        // when KR is behind it, else our own (Resources/flames).
        if (text != null && text.Contains("particlesystem \"Empty\"", StringComparison.OrdinalIgnoreCase))
        {
            EmsScript? replacement = Fallback?.Script(scene) ?? OwnScript(scene);

            if (replacement != null)
            {
                _scripts[scene] = replacement;

                return replacement;
            }
        }

        EmsScript? script = text is null ? null : EmsScript.Parse(text);
        _scripts[scene] = script;

        return script;
    }

    /// <summary>Our own script for an emptied EC scene (Resources/flames), or null.</summary>
    private static EmsScript? OwnScript(string scene)
    {
        using System.IO.Stream? res = typeof(KrEffects).Assembly.GetManifestResourceStream(
            "ClassicUO.KR.Resources.flames." + scene.ToLowerInvariant());

        if (res is null)
        {
            return null;
        }

        using System.IO.StreamReader reader = new(res, Encoding.Latin1);

        return EmsScript.Parse(reader.ReadToEnd());
    }

    /// <summary>A parsed NIF scene, cached; null when missing or not readable yet.</summary>
    public NifFile? Scene(string scene)
    {
        if (_scenes.TryGetValue(scene, out NifFile? cached))
        {
            return cached;
        }

        byte[]? data = scene.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)
            ? _store.GetPackage(ScenePackage)?.Read(SceneEntryName(scene))
            : null;
        NifFile? nif = data is null ? null : NifFile.TryParse(data, out _);
        _scenes[scene] = nif;

        return nif;
    }

    /// <summary>A running effect for a scene - an .ems script or a NIF - or null when neither can run.</summary>
    public IKrEffect? Create(string scene, int seed)
    {
        if (Script(scene) is { Sequences.Count: > 0 } script)
        {
            return new EmsEffect(script, seed);
        }

        return Scene(scene) is { } nif ? new NifEffect(nif, seed) : Fallback?.Create(scene, seed);
    }

    /// <summary>A decoded effect texture by its name in a script (e.g. "03000005_fire.tga").</summary>
    public TgaImage? Texture(string name)
    {
        if (name.Contains('|'))
        {
            return Composite(name);
        }

        byte[]? data = _store.GetPackage(ScenePackage)?.Read(SceneEntryName(name));

        return (data is null ? null : TgaImage.TryDecode(data)) ?? Fallback?.Texture(name);
    }

    private readonly Dictionary<string, TgaImage?> _composites = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A material's base, dark and detail maps in one (NifEffect.Look names
    /// it "base:a.tga|dark:b.tga|detail:c.tga", any of them): Gamebryo's
    /// fixed stages multiply them - base and dark as they are, detail times
    /// two (MODULATE2X, as KR drew Cunning). The vertex colour, applied by
    /// the caller, multiplies the result as it does the base alone. Sampled
    /// at the first map's size.
    /// </summary>
    private TgaImage? Composite(string name)
    {
        if (_composites.TryGetValue(name, out TgaImage? cached))
        {
            return cached;
        }

        List<(string Slot, TgaImage Image)> maps = [];

        foreach (string part in name.Split('|'))
        {
            int colon = part.IndexOf(':');

            if (colon > 0 && Texture(part[(colon + 1)..]) is { } image)
            {
                maps.Add((part[..colon], image));
            }
        }

        TgaImage? result = null;

        if (maps.Count > 0)
        {
            int w = maps[0].Image.Width, h = maps[0].Image.Height;
            uint[] pixels = new uint[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float r = 1f, g = 1f, b = 1f, a = 1f;

                    foreach ((string slot, TgaImage image) in maps)
                    {
                        uint p = image.Pixels[((y * image.Height / h) * image.Width) + (x * image.Width / w)];
                        float k = slot == "detail" ? 2f / 255f : 1f / 255f;
                        r *= (p & 0xFF) * k;
                        g *= ((p >> 8) & 0xFF) * k;
                        b *= ((p >> 16) & 0xFF) * k;

                        if (slot != "dark")
                        {
                            a *= (p >> 24) * k;
                        }
                    }

                    pixels[(y * w) + x] = (uint)(Math.Clamp(r, 0f, 1f) * 255f)
                        | ((uint)(Math.Clamp(g, 0f, 1f) * 255f) << 8)
                        | ((uint)(Math.Clamp(b, 0f, 1f) * 255f) << 16)
                        | ((uint)(Math.Clamp(a, 0f, 1f) * 255f) << 24);
                }
            }

            result = TgaImage.FromPixels(w, h, pixels);
        }

        _composites[name] = result;

        return result;
    }
}
