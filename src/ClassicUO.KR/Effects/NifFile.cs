// SPDX-License-Identifier: BSD-2-Clause
//
// Reads the blocks of a Gamebryo NIF 20.0.0.4 scene - KR's spell effects.
// 20.0.0.4 stores no block sizes, so each type's layout must be exact for the
// next block to be found; the layouts follow the NifTools description of the
// format for this version. Every file this reads must end exactly where the
// last block and the root list end, which is checked: of the 451 scenes in
// the install, 444 parse that way, NiGeomMorpherController included (vertex
// morphs are parsed, not yet played). The other 7 use NiPixelData or
// NiTextureEffect, which are not read yet.
//
// A block is its type and a dictionary of named fields - enough to drive the
// particle simulation (NifEffect) without a class per type. References are
// block indices, -1 for none.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using System.Text;

namespace ClassicUO.KR.Effects;

public sealed class NifBlock
{
    public NifBlock(string type) => Type = type;

    public string Type { get; }

    public Dictionary<string, object> Fields { get; } = new(StringComparer.Ordinal);

    public T Get<T>(string name, T fallback = default!) =>
        Fields.TryGetValue(name, out object? v) && v is T t ? t : fallback;

    public int Ref(string name) => Get(name, -1);

    public float Float(string name, float fallback = 0f) => Get(name, fallback);
}

/// <summary>Keys of one animated value: time and value, with the interpolation type.</summary>
/// <summary>Keys; quadratic ones (interpolation 2) also carry each key's forward and backward tangents.</summary>
public sealed record NifKeys<T>(uint Interpolation, (float Time, T Value)[] Keys, (T Forward, T Backward)[]? Tangents = null);

public sealed class NifFile
{
    private NifFile(NifHeader header, List<NifBlock> blocks, int[] roots)
    {
        Header = header;
        Blocks = blocks;
        Roots = roots;
    }

    public NifHeader Header { get; }

    public IReadOnlyList<NifBlock> Blocks { get; }

    public IReadOnlyList<int> Roots { get; }

    public NifBlock? this[int i] => i >= 0 && i < Blocks.Count ? Blocks[i] : null;

    /// <summary>Parses a scene, or returns null with the reason in <paramref name="error"/>.</summary>
    public static NifFile? TryParse(byte[] data, out string? error)
    {
        error = null;
        NifHeader? header = NifHeader.TryParse(data);

        if (header is null)
        {
            error = "not a 20.0.0.4 NIF";

            return null;
        }

        Reader r = new(data, header.BlocksOffset);
        List<NifBlock> blocks = new(header.BlockCount);

        for (int i = 0; i < header.BlockCount; i++)
        {
            string type = header.TypeOf(i);
            NifBlock block = new(type);
            int at = r.Pos;

            try
            {
                if (!Read(r, block))
                {
                    error = $"block {i}: no reader for {type}";

                    return null;
                }
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidOperationException)
            {
                error = $"block {i} {type} at {at}: {ex.Message}";

                return null;
            }

            blocks.Add(block);
        }

        try
        {
            int[] roots = r.Refs();

            if (r.Pos != data.Length)
            {
                error = $"ended at {r.Pos} of {data.Length}";

                return null;
            }

            return new NifFile(header, blocks, roots);
        }
        catch (ArgumentOutOfRangeException)
        {
            error = "truncated root list";

            return null;
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _b;

        public Reader(byte[] b, int pos)
        {
            _b = b;
            Pos = pos;
        }

        public int Pos { get; private set; }

        private ReadOnlySpan<byte> Take(int n)
        {
            if (n < 0 || Pos + n > _b.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(n), $"read of {n} at {Pos} past the end");
            }

            ReadOnlySpan<byte> s = _b.AsSpan(Pos, n);
            Pos += n;

            return s;
        }

        public void Skip(int n) => Take(n);

        public byte U8() => Take(1)[0];

        public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));

        public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));

        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

        public float F() => BinaryPrimitives.ReadSingleLittleEndian(Take(4));

        public Vector2 V2() => new(F(), F());

        public Vector3 V3() => new(F(), F(), F());

        public Vector4 V4() => new(F(), F(), F(), F());

        public Quaternion Quat()
        {
            float w = F(), x = F(), y = F(), z = F();

            return new Quaternion(x, y, z, w);
        }

        public string Str()
        {
            uint n = U32();

            if (n > 4096)
            {
                throw new InvalidOperationException($"string length {n} at {Pos - 4}");
            }

            return Encoding.Latin1.GetString(Take((int)n));
        }

        public int[] Refs()
        {
            uint n = U32();

            if (n > 100_000)
            {
                throw new InvalidOperationException($"list length {n} at {Pos - 4}");
            }

            int[] r = new int[n];

            for (int i = 0; i < r.Length; i++)
            {
                r[i] = I32();
            }

            return r;
        }

        public NifKeys<T> Keys<T>(Func<Reader, T> value)
        {
            uint n = U32();

            if (n == 0)
            {
                return new NifKeys<T>(0, []);
            }

            if (n > 100_000)
            {
                throw new InvalidOperationException($"key count {n} at {Pos - 4}");
            }

            uint interpolation = U32();
            (float, T)[] keys = new (float, T)[n];
            (T, T)[]? tangents = interpolation == 2 ? new (T, T)[n] : null;

            for (int i = 0; i < keys.Length; i++)
            {
                float t = F();
                T v = value(this);

                if (tangents is not null)
                {
                    // Quadratic: forward and backward tangents.
                    T forward = value(this);
                    tangents[i] = (forward, value(this));
                }
                else if (interpolation == 3)
                {
                    Skip(12);   // tension, bias, continuity
                }

                keys[i] = (t, v);
            }

            return new NifKeys<T>(interpolation, keys, tangents);
        }
    }

    // ---- shared parts ------------------------------------------------------

    private static void ObjectNet(Reader r, NifBlock b)
    {
        b.Fields["name"] = r.Str();
        b.Fields["extra"] = r.Refs();
        b.Fields["controller"] = r.I32();
    }

    private static void AvObject(Reader r, NifBlock b)
    {
        ObjectNet(r, b);
        b.Fields["flags"] = (int)r.U16();
        b.Fields["translation"] = r.V3();

        // Row-major 3x3 rotation.
        float[] m = new float[9];

        for (int i = 0; i < 9; i++)
        {
            m[i] = r.F();
        }

        b.Fields["rotation"] = new Matrix4x4(m[0], m[1], m[2], 0, m[3], m[4], m[5], 0, m[6], m[7], m[8], 0, 0, 0, 0, 1);
        b.Fields["scale"] = r.F();
        b.Fields["properties"] = r.Refs();
        b.Fields["collision"] = r.I32();
    }

    private static void Node(Reader r, NifBlock b)
    {
        AvObject(r, b);
        b.Fields["children"] = r.Refs();
        b.Fields["effects"] = r.Refs();
    }

    private static void TimeController(Reader r, NifBlock b)
    {
        b.Fields["next"] = r.I32();
        b.Fields["flags"] = (int)r.U16();
        b.Fields["frequency"] = r.F();
        b.Fields["phase"] = r.F();
        b.Fields["start"] = r.F();
        b.Fields["stop"] = r.F();
        b.Fields["target"] = r.I32();
    }

    private static void SingleInterp(Reader r, NifBlock b)
    {
        TimeController(r, b);
        b.Fields["interpolator"] = r.I32();
    }

    private static void Modifier(Reader r, NifBlock b)
    {
        b.Fields["name"] = r.Str();
        b.Fields["order"] = (int)r.U32();
        b.Fields["target"] = r.I32();
        b.Fields["active"] = r.U8() != 0;
    }

    private static void Emitter(Reader r, NifBlock b)
    {
        Modifier(r, b);

        foreach (string f in (ReadOnlySpan<string>)["speed", "speed_var", "declination", "declination_var", "planar", "planar_var"])
        {
            b.Fields[f] = r.F();
        }

        b.Fields["color"] = r.V4();

        foreach (string f in (ReadOnlySpan<string>)["radius", "radius_var", "life", "life_var"])
        {
            b.Fields[f] = r.F();
        }
    }

    private static void VolumeEmitter(Reader r, NifBlock b)
    {
        Emitter(r, b);
        b.Fields["emitter_object"] = r.I32();
    }

    private static Dictionary<string, object>? TexDesc(Reader r)
    {
        Dictionary<string, object> t = new()
        {
            ["source"] = r.I32(),
            ["clamp"] = r.U32(),
            ["filter"] = r.U32(),
            ["uv_set"] = r.U32(),
        };

        if (r.U8() != 0)
        {
            t["uv_translation"] = r.V2();
            t["uv_scale"] = r.V2();
            t["uv_rotation"] = r.F();
            t["uv_method"] = r.U32();
            t["uv_center"] = r.V2();
        }

        return t;
    }

    private static int GeometryData(Reader r, NifBlock b)
    {
        r.I32();                        // group id
        int n = r.U16();
        b.Fields["vertex_count"] = n;
        r.U8();                         // keep flags
        r.U8();                         // compress flags

        if (r.U8() != 0)
        {
            if (b.Type == "NiTriShapeData")
            {
                Vector3[] vertices = new Vector3[n];

                for (int i = 0; i < n; i++)
                {
                    vertices[i] = r.V3();
                }

                b.Fields["vertices"] = vertices;
            }
            else
            {
                r.Skip(12 * n);         // particle data vertices: preallocated, unused
            }
        }

        int vectorFlags = r.U16();

        if (r.U8() != 0)
        {
            r.Skip(12 * n);             // normals

            if ((vectorFlags & 4096) != 0)
            {
                r.Skip(24 * n);         // tangents, bitangents
            }
        }

        r.V3();                         // centre
        r.F();                          // radius

        if (r.U8() != 0)
        {
            if (b.Type == "NiTriShapeData")
            {
                Vector4[] colors = new Vector4[n];

                for (int i = 0; i < n; i++)
                {
                    colors[i] = new Vector4(r.F(), r.F(), r.F(), r.F());
                }

                b.Fields["colors"] = colors;
            }
            else
            {
                r.Skip(16 * n);         // vertex colours
            }
        }

        int uvSets = vectorFlags & 63;

        if (b.Type == "NiTriShapeData" && uvSets > 0)
        {
            Vector2[] uvs = new Vector2[n];

            for (int i = 0; i < n; i++)
            {
                uvs[i] = r.V2();
            }

            b.Fields["uvs"] = uvs;
            uvSets--;
        }

        r.Skip(8 * n * uvSets);         // further UV sets
        r.U16();                        // consistency
        r.I32();                        // additional data

        return n;
    }

    private static void Geometry(Reader r, NifBlock b)
    {
        AvObject(r, b);
        b.Fields["data"] = r.I32();
        b.Fields["skin"] = r.I32();

        if (r.U8() != 0)
        {
            b.Fields["shader"] = r.Str();
            r.I32();
        }
    }

    // ---- block readers -----------------------------------------------------

    private static bool Read(Reader r, NifBlock b)
    {
        switch (b.Type)
        {
            case "NiNode":
                Node(r, b);

                return true;

            case "NiBillboardNode":
                Node(r, b);
                b.Fields["billboard_mode"] = (int)r.U16();

                return true;

            case "NiZBufferProperty":
                ObjectNet(r, b);
                b.Fields["flags"] = (int)r.U16();
                b.Fields["function"] = (int)r.U32();

                return true;

            case "NiVertexColorProperty":
                ObjectNet(r, b);
                b.Fields["flags"] = (int)r.U16();
                b.Fields["vertex_mode"] = (int)r.U32();
                b.Fields["lighting_mode"] = (int)r.U32();

                return true;

            case "NiAlphaProperty":
                ObjectNet(r, b);
                b.Fields["flags"] = (int)r.U16();
                b.Fields["threshold"] = (int)r.U8();

                return true;

            case "NiDitherProperty":
            case "NiSpecularProperty":
                ObjectNet(r, b);
                b.Fields["flags"] = (int)r.U16();

                return true;

            case "NiStencilProperty":
                ObjectNet(r, b);
                r.U8();
                r.Skip(4 * 7);

                return true;

            case "NiMaterialProperty":
                ObjectNet(r, b);
                b.Fields["ambient"] = r.V3();
                b.Fields["diffuse"] = r.V3();
                b.Fields["specular"] = r.V3();
                b.Fields["emissive"] = r.V3();
                b.Fields["glossiness"] = r.F();
                b.Fields["alpha"] = r.F();

                return true;

            case "NiTexturingProperty":
            {
                ObjectNet(r, b);
                b.Fields["apply_mode"] = (int)r.U32();
                int count = (int)r.U32();

                foreach (string slot in (ReadOnlySpan<string>)["base", "dark", "detail", "gloss", "glow"])
                {
                    if (r.U8() != 0)
                    {
                        b.Fields[slot] = TexDesc(r)!;
                    }
                }

                if (r.U8() != 0)
                {
                    b.Fields["bump"] = TexDesc(r)!;
                    r.Skip(4 * 6);      // luma scale, offset, 2x2 matrix
                }

                // Decal slots: one for each texture past the first six.
                for (int i = 0; i < count - 6; i++)
                {
                    if (r.U8() != 0)
                    {
                        b.Fields["decal" + i] = TexDesc(r)!;
                    }
                }

                uint shaders = r.U32();

                for (int i = 0; i < shaders; i++)
                {
                    if (r.U8() != 0)
                    {
                        TexDesc(r);
                        r.U32();
                    }
                }

                return true;
            }

            case "NiSourceTexture":
                ObjectNet(r, b);
                r.U8();                 // external
                b.Fields["file"] = r.Str();
                b.Fields["pixel_data"] = r.I32();
                b.Fields["pixel_layout"] = (int)r.U32();
                b.Fields["mipmaps"] = (int)r.U32();
                b.Fields["alpha_format"] = (int)r.U32();
                r.U8();                 // static
                r.U8();                 // direct render

                return true;

            case "NiStringExtraData":
                b.Fields["name"] = r.Str();
                b.Fields["string"] = r.Str();

                return true;

            case "NiTransformController":
            case "NiAlphaController":
            case "NiVisController":
                SingleInterp(r, b);

                return true;

            case "NiMaterialColorController":
                SingleInterp(r, b);
                b.Fields["target_color"] = (int)r.U16();

                return true;

            case "NiTextureTransformController":
                SingleInterp(r, b);
                r.U8();
                b.Fields["slot"] = (int)r.U32();
                b.Fields["operation"] = (int)r.U32();

                return true;

            case "NiTransformInterpolator":
                b.Fields["translation"] = r.V3();
                b.Fields["rotation"] = r.Quat();
                b.Fields["scale"] = r.F();
                b.Fields["data"] = r.I32();

                return true;

            case "NiTransformData":
            {
                uint n = r.U32();

                if (n > 0)
                {
                    uint type = r.U32();
                    b.Fields["rotation_type"] = (int)type;

                    if (type == 4)
                    {
                        b.Fields["rot_x"] = r.Keys(x => x.F());
                        b.Fields["rot_y"] = r.Keys(x => x.F());
                        b.Fields["rot_z"] = r.Keys(x => x.F());
                    }
                    else
                    {
                        (float, Quaternion)[] keys = new (float, Quaternion)[n];

                        for (int i = 0; i < keys.Length; i++)
                        {
                            keys[i] = (r.F(), r.Quat());

                            if (type == 3)
                            {
                                r.Skip(12);
                            }
                        }

                        b.Fields["rotations"] = new NifKeys<Quaternion>(type, keys);
                    }
                }

                b.Fields["translations"] = r.Keys(x => x.V3());
                b.Fields["scales"] = r.Keys(x => x.F());

                return true;
            }

            case "NiFloatInterpolator":
                b.Fields["value"] = r.F();
                b.Fields["data"] = r.I32();

                return true;

            case "NiBoolInterpolator":
                b.Fields["value"] = (int)r.U8();
                b.Fields["data"] = r.I32();

                return true;

            case "NiPoint3Interpolator":
                b.Fields["value"] = r.V3();
                b.Fields["data"] = r.I32();

                return true;

            case "NiPathInterpolator":
                r.U16();
                r.I32();
                r.F();
                r.F();
                r.U16();
                b.Fields["path_data"] = r.I32();
                b.Fields["percent_data"] = r.I32();

                return true;

            case "NiFloatData":
                b.Fields["keys"] = r.Keys(x => x.F());

                return true;

            case "NiBoolData":
                b.Fields["keys"] = r.Keys(x => (int)x.U8());

                return true;

            case "NiPosData":
                b.Fields["keys"] = r.Keys(x => x.V3());

                return true;

            case "NiColorData":
                b.Fields["keys"] = r.Keys(x => x.V4());

                return true;

            case "NiParticleSystem":
                Geometry(r, b);
                b.Fields["world_space"] = r.U8() != 0;
                b.Fields["modifiers"] = r.Refs();

                return true;

            case "NiTriShape":
                Geometry(r, b);

                return true;

            case "NiTriShapeData":
            {
                GeometryData(r, b);
                int triangles = r.U16();
                r.U32();

                if (r.U8() != 0)
                {
                    ushort[] indices = new ushort[3 * triangles];

                    for (int i = 0; i < indices.Length; i++)
                    {
                        indices[i] = (ushort)r.U16();
                    }

                    b.Fields["triangles"] = indices;
                }

                int groups = r.U16();

                for (int i = 0; i < groups; i++)
                {
                    r.Skip(2 * r.U16());
                }

                return true;
            }

            case "NiPSysData":
            {
                int n = GeometryData(r, b);

                if (r.U8() != 0)
                {
                    r.Skip(4 * n);      // radii
                }

                r.U16();                // active count

                foreach (int size in (ReadOnlySpan<int>)[4, 16, 4, 12])   // sizes, rotations, angles, axes
                {
                    if (r.U8() != 0)
                    {
                        r.Skip(size * n);
                    }
                }

                r.Skip(28 * n);         // particle info: velocity, age, life span, last update, generation, code

                if (r.U8() != 0)
                {
                    r.Skip(4 * n);      // rotation speeds
                }

                r.U16();                // added particles
                r.U16();                // added particles base

                return true;
            }

            case "NiPSysAgeDeathModifier":
                Modifier(r, b);
                b.Fields["spawn_on_death"] = r.U8() != 0;
                b.Fields["spawn_modifier"] = r.I32();

                return true;

            case "NiPSysBoxEmitter":
                VolumeEmitter(r, b);
                b.Fields["width"] = r.F();
                b.Fields["height"] = r.F();
                b.Fields["depth"] = r.F();

                return true;

            case "NiPSysSphereEmitter":
                VolumeEmitter(r, b);
                b.Fields["sphere_radius"] = r.F();

                return true;

            case "NiPSysCylinderEmitter":
                VolumeEmitter(r, b);
                b.Fields["cylinder_radius"] = r.F();
                b.Fields["cylinder_height"] = r.F();

                return true;

            case "NiPSysMeshEmitter":
                Emitter(r, b);
                b.Fields["meshes"] = r.Refs();
                b.Fields["velocity_type"] = (int)r.U32();
                b.Fields["emission_type"] = (int)r.U32();
                b.Fields["emission_axis"] = r.V3();

                return true;

            case "NiPSysSpawnModifier":
                Modifier(r, b);
                b.Fields["generations"] = (int)r.U16();
                b.Fields["percent"] = r.F();
                b.Fields["min"] = (int)r.U16();
                b.Fields["max"] = (int)r.U16();
                b.Fields["speed_var"] = r.F();
                b.Fields["direction_var"] = r.F();
                b.Fields["life"] = r.F();
                b.Fields["life_var"] = r.F();

                return true;

            case "NiPSysGrowFadeModifier":
                Modifier(r, b);
                b.Fields["grow"] = r.F();
                b.Fields["grow_generation"] = (int)r.U16();
                b.Fields["fade"] = r.F();
                b.Fields["fade_generation"] = (int)r.U16();

                return true;

            case "NiPSysColorModifier":
                Modifier(r, b);
                b.Fields["data"] = r.I32();

                return true;

            case "NiPSysGravityModifier":
                Modifier(r, b);
                b.Fields["gravity_object"] = r.I32();
                b.Fields["axis"] = r.V3();
                b.Fields["decay"] = r.F();
                b.Fields["strength"] = r.F();
                b.Fields["force_type"] = (int)r.U32();
                b.Fields["turbulence"] = r.F();
                b.Fields["turbulence_scale"] = r.F();

                return true;

            case "NiPSysPositionModifier":
                Modifier(r, b);

                return true;

            case "NiPSysBoundUpdateModifier":
                Modifier(r, b);
                r.U16();

                return true;

            case "NiPSysRotationModifier":
                Modifier(r, b);
                b.Fields["rotation_speed"] = r.F();
                b.Fields["rotation_speed_var"] = r.F();
                b.Fields["rotation_angle"] = r.F();
                b.Fields["rotation_angle_var"] = r.F();
                b.Fields["random_sign"] = r.U8() != 0;
                b.Fields["random_axis"] = r.U8() != 0;
                b.Fields["axis"] = r.V3();

                return true;

            case "NiPSysUpdateCtlr":
            case "NiPSysResetOnLoopCtlr":
                TimeController(r, b);

                return true;

            case "NiPSysEmitterCtlr":
                SingleInterp(r, b);
                b.Fields["modifier"] = r.Str();
                b.Fields["visibility"] = r.I32();

                return true;

            case "NiPSysEmitterSpeedCtlr":
            case "NiPSysEmitterLifeSpanCtlr":
            case "NiPSysEmitterInitialRadiusCtlr":
            case "NiPSysGravityStrengthCtlr":
            case "NiPSysInitialRotSpeedCtlr":
            case "NiPSysInitialRotSpeedVarCtlr":
                SingleInterp(r, b);
                b.Fields["modifier"] = r.Str();

                return true;

            // Vertex morphing (00014138_sparkle's flare): interpolator refs,
            // not the later weighted list, and - read from the file, against
            // the NifTools description - no unknown-ints list after them in
            // 20.0.0.4. The morph keys live in the interpolators.
            case "NiGeomMorpherController":
            {
                TimeController(r, b);
                r.U16();                // extra flags
                b.Fields["data"] = r.I32();
                r.U8();                 // always update
                b.Fields["interpolators"] = r.Refs();

                return true;
            }

            case "NiMorphData":
            {
                int morphs = (int)r.U32();
                int vertices = (int)r.U32();
                b.Fields["relative"] = r.U8() != 0;
                Vector3[][] targets = new Vector3[morphs][];

                for (int m = 0; m < morphs; m++)
                {
                    r.Str();            // frame name
                    r.F();              // legacy weight
                    targets[m] = new Vector3[vertices];

                    for (int v = 0; v < vertices; v++)
                    {
                        targets[m][v] = r.V3();
                    }
                }

                b.Fields["morphs"] = targets;

                return true;
            }

            default:
                return false;
        }
    }
}
