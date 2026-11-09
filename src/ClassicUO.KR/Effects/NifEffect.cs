// SPDX-License-Identifier: BSD-2-Clause
//
// Runs the particle systems of a parsed NIF effect scene (NifFile). This follows what
// the blocks say, and where the behaviour is not certain the choice is a
// *hypothesis*:
//
//   * node transforms compose child-to-root; a node with a
//     NiTransformController takes its rotation (quaternion or XYZ Euler keys),
//     translation and scale from the keys, clamped to the controller's range;
//   * a particle system emits while its NiPSysEmitterCtlr's visibility keys
//     say 1, at the controller's birth rate per second; NiPSysEmitterSpeedCtlr
//     overrides the emitter's speed;
//   * an emitter places a particle in its box/sphere/cylinder in the emitter
//     object's frame, and sends it along that frame's +Z tilted by the
//     declination and turned by the planar angle (radians);
//   * grow/fade scale the size over the first "grow" and last "fade" seconds,
//     the colour modifier's keys run over the particle's life (0..1), and
//     gravity pulls along an axis (force type 0) or toward a node (1);
//   * the whole system is scaled by its node's scale - 0.0859 in Magic
//     Arrow's fire trail - which brings the scene to roughly tile units.

using System;
using System.Collections.Generic;
using System.Numerics;

namespace ClassicUO.KR.Effects;

public interface IKrEffect
{
    /// <summary>Where the effect stands in the world now, in pixels; set before each update.</summary>
    Vector2 Origin { get; set; }

    bool Finished { get; }

    void Update(float seconds);

    IEnumerable<EmsSprite> Sprites();
}

public sealed class NifEffect : IKrEffect
{
    private const float MaxDuration = 15f;

    /// <summary>
    /// Scene positions to tile units. A particle system's own node scale
    /// (0.086 in the fire trail, 0.17-0.37 in the vortex, 1 in the wisp) is
    /// applied to its local quantities - size, launch speed, gravity - but
    /// not to where particles are born: these systems are world-space, born
    /// at the emitter's world position. Scaling positions too collapsed the
    /// fire trail's ring of orbiting emitters (radius 2.75) to about 2 px.
    /// 0.4 keeps the wisp a compact ball and gives the trail's ring about
    /// 34 px of radius; judged against KR recordings (0.5 was a little
    /// larger than KR's ring).
    /// </summary>
    public const float PositionScale = 0.4f;

    public Vector2 Origin { get; set; }

    /// <summary>
    /// Scene space to the client's tile space, through the scene's own
    /// camera. Every effect scene carries a "Camera_Temp" node - the camera
    /// it was authored with: in the energy vortex and the fire trail it sits
    /// on -Y looking toward +Y with world Z up, so the screen's right is x,
    /// its up is z, and y is depth. The look-down angle is built into the
    /// scenes (the fire trail's Dummy02 is tilted 40 degrees, the vortex's
    /// Rendergroup 30), which is what makes the trail's orbiting flames an
    /// ellipse round the caster. A scene without the node is read the same
    /// way. The client projects tile space as (x - y) across and (x + y) - z
    /// down at 22 px a unit, so a screen offset (sx, sy) is tile ((sx + sy) /
    /// 44, (sy - sx) / 44, 0); one scene unit is 31 px. Depth is dropped.
    /// </summary>
    private Vector3 Facing(Vector3 v)
    {
        const float k = 31f / 44f;

        Vector3 c = Vector3.TransformNormal(v, _view);
        float sx = c.X;
        float sy = -c.Y;

        return new Vector3((sx + sy) * k, (sy - sx) * k, 0f);
    }

    /// <summary>Scene to camera axes: x right, y up, z toward the viewer.</summary>
    private readonly Matrix4x4 _view;

    private Matrix4x4 FindView()
    {
        for (int i = 0; i < _nif.Blocks.Count; i++)
        {
            NifBlock b = _nif.Blocks[i];

            if (b.Type == "NiNode" && string.Equals(b.Get("name", ""), "Camera_Temp", StringComparison.OrdinalIgnoreCase))
            {
                // The camera's rotation in the world, without its position
                // or scale; its inverse takes the scene into camera axes,
                // where the camera looks along -z with y up.
                Matrix4x4 world = World(i, 0f);
                Vector3 right = Vector3.Normalize(new Vector3(world.M11, world.M12, world.M13));
                Vector3 up = Vector3.Normalize(new Vector3(world.M21, world.M22, world.M23));
                Vector3 back = Vector3.Normalize(new Vector3(world.M31, world.M32, world.M33));
                Matrix4x4 rotation = new(right.X, right.Y, right.Z, 0, up.X, up.Y, up.Z, 0, back.X, back.Y, back.Z, 0, 0, 0, 0, 1);

                return Matrix4x4.Transpose(rotation);
            }
        }

        // x right, z up, y away from the viewer.
        return new Matrix4x4(1, 0, 0, 0, 0, 0, -1, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    }

    /// <summary>
    /// Planes drawn as sprites are half their edge across - judged against
    /// the KR vortex, whose ball is about half the size full edges gave.
    /// </summary>
    private const float PlaneSize = 0.5f;

    private readonly NifFile _nif;
    private readonly Random _rng;
    private readonly int[] _parent;
    private readonly List<System_> _systems = [];
    private readonly float _duration;
    private float _time;
    private readonly float _emitLength;

    /// <summary>
    /// The period a looping scene restarts on: when it has finished, its
    /// last particle dead (emission's end + the longest life). The Crystal
    /// Cave barrier emits until 0.7 s and its particles live 0.33 s: KR
    /// repeats it every 1.1 s (a recording: 33 frames at 30 fps, 12 of them
    /// dark). Wrapped at 0.7, each cycle began before the last one's
    /// particles died and it never went dark.
    /// </summary>
    private readonly float _loopLength;

    public NifEffect(NifFile nif, int seed)
    {
        _nif = nif;
        _rng = new Random(seed);
        _parent = new int[nif.Blocks.Count];
        Array.Fill(_parent, -1);

        for (int i = 0; i < nif.Blocks.Count; i++)
        {
            foreach (int c in nif.Blocks[i].Get<int[]>("children", []))
            {
                if (c >= 0 && c < _parent.Length)
                {
                    _parent[c] = i;
                }
            }
        }

        float duration = 0f;

        for (int i = 0; i < nif.Blocks.Count; i++)
        {
            if (nif.Blocks[i].Type == "NiParticleSystem")
            {
                System_ s = new(this, i);
                _systems.Add(s);
                duration = Math.Max(duration, s.EmitUntil + s.MaxLife);
            }
        }

        // Meshes only (Magic Reflection): their controllers' end, or the
        // scene ended on its first frame.
        if (_systems.Count == 0)
        {
            foreach (NifBlock b in nif.Blocks)
            {
                if (b.Type.EndsWith("Controller") && b.Fields.ContainsKey("stop"))
                {
                    duration = Math.Max(duration, b.Float("stop"));
                }
            }
        }

        // A looping emitter has no end of its own: the part's duration (or
        // the cap) ends the scene.
        _duration = _systems.Exists(s => s.LoopsEmitter) ? MaxDuration : Math.Min(MaxDuration, duration);
        _view = FindView();
        FindPlanes();

        foreach (System_ s in _systems)
        {
            _emitLength = Math.Max(_emitLength, s.EmitUntil);
        }

        _loopLength = _emitLength;

        foreach (System_ s in _systems)
        {
            _loopLength = Math.Max(_loopLength, s.EmitUntil + s.MaxLife);
        }
    }

    /// <summary>
    /// Restart the scene's clock when it runs out, keeping live particles -
    /// for scenes that stand in for a creature (the wisp) rather than play once.
    /// </summary>
    public bool Loop { get; set; }

    /// <summary>The period a looping scene restarts on (its emitters' length), 0 if none.</summary>
    public float LoopLength => RestartWhenFinished ? _loopLength : _emitLength;

    /// <summary>
    /// With <see cref="Loop"/>: restart only once the scene has finished, as
    /// KR plays a static's scene, rather than as its emission ends keeping
    /// live particles (a creature's scene, the wisp).
    /// </summary>
    public bool RestartWhenFinished { get; set; }

    public bool Finished
    {
        get
        {
            if (Loop || _time < _duration)
            {
                return false;
            }

            foreach (System_ s in _systems)
            {
                if (s.Live > 0)
                {
                    return false;
                }
            }

            return true;
        }
    }

    public void Update(float seconds)
    {
        // Small steps, so fast emitters and strong gravity stay stable.
        while (seconds > 0f)
        {
            float dt = Math.Min(seconds, 1f / 60f);
            seconds -= dt;
            _time += dt;

            if (Loop && _time >= LoopLength && LoopLength > 0f)
            {
                _time -= LoopLength;
            }

            foreach (System_ s in _systems)
            {
                s.Update(_time, dt);
            }
        }
    }

    /// <summary>
    /// Whether 4-vertex shapes are drawn as camera-facing sprites (spell
    /// effects). A static's scene draws them as meshes instead, placed and
    /// scaled by tileart: as sprites the Tokuno oven's coals plane
    /// (00002347_Oven.nif, scale 0.1) became a 90 px animated square.
    /// </summary>
    public bool PlanesAsSprites { get; set; } = true;

    /// <summary>
    /// Draw only the first textured shape of the meshes: a script's mesh
    /// particle (lightning.ems's lightning.nif has three bolt quads; KR drew
    /// one a frame, KR casting Lightning).
    /// </summary>
    public bool FirstShapeOnly { get; set; }

    public IEnumerable<EmsSprite> Sprites()
    {
        foreach (EmsSprite sprite in PlanesAsSprites ? PlaneSprites() : [])
        {
            yield return sprite;
        }

        foreach (System_ s in _systems)
        {
            foreach (EmsSprite sprite in s.Sprites())
            {
                yield return sprite;
            }
        }
    }

    /// <summary>A texture slot's file name, or "".</summary>
    private string Slot(NifBlock texturing, string slot) =>
        texturing.Get<Dictionary<string, object>>(slot) is { } desc
        && desc.TryGetValue("source", out object? src)
        && _nif[(int)src] is { } tex
            ? tex.Get("file", "")
            : "";

    /// <summary>
    /// The glow map added over a shape's other maps (Gamebryo's ADD stage:
    /// KR drew Cunning's blue-tinted detail with 03004057_Green_dot.tga added,
    /// the green its dots show), or "" - also when the glow is the shape's
    /// only map, which Look uses as the texture itself.
    /// </summary>
    /// <summary>
    /// Whether a shape's only map is its glow: added to the lit colour, not
    /// multiplied by it, so it shows untinted - Agility's sparks
    /// (03004001_SparkBlue.tga as glow, emissive black) were invisible tinted.
    /// </summary>
    private bool GlowOnly(NifBlock shape)
    {
        foreach (int p in shape.Get<int[]>("properties", []))
        {
            if (_nif[p] is { Type: "NiTexturingProperty" } prop)
            {
                return Slot(prop, "glow").Length > 0 && Slot(prop, "base").Length == 0
                    && Slot(prop, "dark").Length == 0 && Slot(prop, "detail").Length == 0;
            }
        }

        return false;
    }

    private string GlowOf(NifBlock shape)
    {
        foreach (int p in shape.Get<int[]>("properties", []))
        {
            if (_nif[p] is { Type: "NiTexturingProperty" } prop
                && (Slot(prop, "base").Length > 0 || Slot(prop, "dark").Length > 0 || Slot(prop, "detail").Length > 0))
            {
                return Slot(prop, "glow");
            }
        }

        return "";
    }

    /// <summary>
    /// A shape's texture and blend from its properties. The texture is the
    /// base, dark and detail maps multiplied as Gamebryo's fixed stages do -
    /// a composite KrEffects.Texture builds ("base:a.tga|detail:b.tga") when
    /// there is more than the base, the base's own name when it is alone -
    /// else the glow map: the energy vortex's GLOW system has only a detail
    /// texture (03004004_bluflair.tga), drawn at twice its value as KR's
    /// MODULATE2X detail stage does. The glow map, added on top, is GlowOf's.
    /// </summary>
    private (string Texture, KrBlend Blend) Look(NifBlock shape)
    {
        string texture = "";
        KrBlend blend = KrBlend.Alpha;

        foreach (int p in shape.Get<int[]>("properties", []))
        {
            if (_nif[p] is not { } prop)
            {
                continue;
            }

            if (prop.Type == "NiTexturingProperty")
            {
                string baseMap = Slot(prop, "base"), dark = Slot(prop, "dark"), detail = Slot(prop, "detail");

                texture = dark.Length == 0 && detail.Length == 0 ? baseMap
                    : string.Join('|', new[] { ("base", baseMap), ("dark", dark), ("detail", detail) }
                        .Where(m => m.Item2.Length > 0).Select(m => m.Item1 + ":" + m.Item2));

                if (texture.Length == 0)
                {
                    texture = Slot(prop, "glow");
                }
            }
            else if (prop.Type == "NiAlphaProperty")
            {
                // Flags: bit 0 blend on, bits 1-4 source, 5-8 destination,
                // Gamebryo order: 0 ONE, 1 ZERO, 2 SRCCOLOR, 3 INVSRCCOLOR,
                // 4 DESTCOLOR, 5 INVDESTCOLOR, 6 SRCALPHA, 7 INVSRCALPHA.
                // 4097: ONE, ONE - additive (fire trail). 4163: ZERO,
                // SRCCOLOR - multiply (the wisp's dark halo).
                int flags = prop.Get("flags", 0);
                int srcBlend = (flags >> 1) & 15;
                int dstBlend = (flags >> 5) & 15;

                // 4363: INVDESTCOLOR, DESTALPHA - a screen (KR's trace
                // of the Moonglow fountain); as alpha its dark sheet drew
                // black over the water.
                blend = srcBlend == 5 ? KrBlend.Screen
                    : dstBlend == 0 ? KrBlend.Additive
                    : (srcBlend == 1 && dstBlend == 2) || (srcBlend == 4 && dstBlend == 1) ? KrBlend.Multiply
                    : KrBlend.Alpha;
            }
        }

        return (texture, blend);
    }

    // ---- planes ------------------------------------------------------------

    /// <summary>
    /// A textured 4-vertex NiTriShape, drawn as a sprite at its node's
    /// animated world position, sized by its longer edge and turned by how
    /// its local X axis lands on the screen. The energy vortex's bright swirl
    /// is three such planes (03004012_Galaxy.tga, additive). Larger meshes
    /// are not drawn yet. *Hypothesis:* a billboard is close enough for a
    /// plane seen from KR's fixed camera.
    /// </summary>
    private sealed record Plane(int Node, Vector3 Centre, float HalfEdge, string Texture, KrBlend Blend);

    private readonly List<Plane> _planes = [];

    private void FindPlanes()
    {
        for (int i = 0; i < _nif.Blocks.Count; i++)
        {
            NifBlock shape = _nif.Blocks[i];

            if (shape.Type != "NiTriShape" || _nif[shape.Ref("data")]?.Get<Vector3[]>("vertices") is not { Length: 4 } v)
            {
                continue;
            }

            (string texture, KrBlend blend) = Look(shape);

            if (texture.Length == 0)
            {
                continue;
            }

            Vector3 centre = (v[0] + v[1] + v[2] + v[3]) / 4f;
            float edge = 0f;

            for (int k = 0; k < 4; k++)
            {
                edge = Math.Max(edge, Vector3.Distance(v[k], v[(k + 1) % 4]));
            }

            _planes.Add(new Plane(i, centre, edge / 2f, texture, blend));
        }
    }

    private IEnumerable<EmsSprite> PlaneSprites()
    {
        foreach (Plane p in _planes)
        {
            Matrix4x4 world = World(p.Node, _time);
            Vector3 position = Vector3.Transform(p.Centre, world);
            Vector3 axis = Vector3.TransformNormal(Vector3.UnitX, world);

            // Turned by where the plane's x axis lands in camera axes.
            Vector3 c = Vector3.TransformNormal(axis, _view);
            float angle = MathF.Atan2(-c.Y, c.X);

            yield return new EmsSprite(
                p.Texture, Facing(position) * PositionScale, p.HalfEdge * world.GetScale() * PlaneSize, angle * (180f / MathF.PI),
                Vector4.One, p.Blend, 0, 1, 1);
        }
    }

    // ---- transforms --------------------------------------------------------

    /// <summary>A node's world matrix at time <paramref name="t"/> (row-vector convention).</summary>
    private Matrix4x4 World(int node, float t)
    {
        Matrix4x4 m = Matrix4x4.Identity;

        for (int n = node; n >= 0; n = _parent[n])
        {
            m *= Local(n, t);
        }

        return m;
    }

    private Matrix4x4 Local(int index, float t)
    {
        NifBlock b = _nif.Blocks[index];
        Vector3 translation = b.Get("translation", Vector3.Zero);
        // NIF stores the rotation for column vectors; transpose for v * M.
        Matrix4x4 rotation = Matrix4x4.Transpose(b.Get("rotation", Matrix4x4.Identity));
        float scale = b.Get("scale", 1f);

        for (int c = b.Ref("controller"); _nif[c] is { } ctl; c = ctl.Ref("next"))
        {
            if (ctl.Type != "NiTransformController" || _nif[ctl.Ref("interpolator")] is not { } interp)
            {
                continue;
            }

            float ct = ControllerTime(ctl, t);

            // A node moved along a path (Heal's sparkles climb a helix,
            // 00005004_heal.nif): the path's keys are timed 0..1 along it, and
            // the percent keys say how far along it is at time ct. Past 1 it
            // goes round again - Clumsy's runs 0.19 to 5.19, five laps.
            if (interp.Type == "NiPathInterpolator")
            {
                if (_nif[interp.Ref("path_data")]?.Get<NifKeys<Vector3>>("keys") is { Keys.Length: > 0 } path
                    && _nif[interp.Ref("percent_data")]?.Get<NifKeys<float>>("keys") is { Keys.Length: > 0 } percent)
                {
                    float along = Sample(percent, ct, 0f);
                    along -= MathF.Floor(along);
                    float first = path.Keys[0].Time, last = path.Keys[^1].Time;
                    translation = Sample(path, first + (along * (last - first)));
                }

                continue;
            }

            NifBlock? data = _nif[interp.Ref("data")];

            if (data is not null)
            {
                if (data.Fields.TryGetValue("rot_x", out object? rx))
                {
                    float x = Sample((NifKeys<float>)rx, ct, 0f);
                    float y = Sample(data.Get<NifKeys<float>>("rot_y")!, ct, 0f);
                    float z = Sample(data.Get<NifKeys<float>>("rot_z")!, ct, 0f);
                    rotation = Matrix4x4.CreateRotationX(x) * Matrix4x4.CreateRotationY(y) * Matrix4x4.CreateRotationZ(z);
                }
                else if (data.Get<NifKeys<Quaternion>>("rotations") is { Keys.Length: > 0 } q)
                {
                    rotation = Matrix4x4.CreateFromQuaternion(SampleQuat(q, ct));
                }

                if (data.Get<NifKeys<Vector3>>("translations") is { Keys.Length: > 0 } tk)
                {
                    translation = Sample(tk, ct);
                }

                if (data.Get<NifKeys<float>>("scales") is { Keys.Length: > 0 } sk)
                {
                    scale = Sample(sk, ct, scale);
                }
            }
        }

        return Matrix4x4.CreateScale(scale) * rotation * Matrix4x4.CreateTranslation(translation);
    }

    /// <summary>
    /// A controller's own time at scene time <paramref name="t"/>, by its
    /// cycle type (flags bits 1-2: 0 loop, 1 reverse, 2 clamp). Cunning's
    /// emitter (flags 8, stop 2, on for its first 0.1 s) loops: KR fired its
    /// burst again 2 s in, which clamped it never did.
    /// </summary>
    private static float ControllerTime(NifBlock ctl, float t)
    {
        float ct = (t * ctl.Float("frequency", 1f)) + ctl.Float("phase");
        float start = ctl.Float("start");
        float stop = ctl.Float("stop");

        if (stop <= start)
        {
            return ct;
        }

        float span = stop - start;

        switch (Cycle(ctl))
        {
            case 0:
                return ct <= start ? Math.Max(ct, start) : start + (((ct - start) % span + span) % span);

            case 1:
            {
                float u = ct <= start ? 0f : ((ct - start) % (2f * span) + 2f * span) % (2f * span);

                return start + (u <= span ? u : (2f * span) - u);
            }

            default:
                return Math.Clamp(ct, start, stop);
        }
    }

    private static int Cycle(NifBlock ctl) => (ctl.Get("flags", 0) >> 1) & 3;

    // ---- textured meshes ----------------------------------------------------

    /// <summary>Whether the scene has textured triangle meshes (a waterfall's sheet of water).</summary>
    public bool HasMeshes
    {
        get
        {
            for (int i = 0; i < _nif.Blocks.Count; i++)
            {
                if (_nif.Blocks[i].Type == "NiTriShape" && _nif[_nif.Blocks[i].Ref("data")] is { } d
                    && d.Get<ushort[]>("triangles", []).Length > 3 && d.Get<Vector2[]>("uvs", []).Length > 0)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Draws the scene's textured meshes at the current time into a
    /// premultiplied-alpha image,
    /// through <paramref name="project"/> (scene world position to pixels).
    /// A mesh's texture moves with its NiTextureTransformController
    /// (operation 0 slides u, 1 slides v; 00013549_waterfall's v runs 0 to
    /// -1 a second), and is looped: the water keeps falling. Vertex colours
    /// tint it; triangles are laid in file order, alpha-blended.
    /// <paramref name="origin"/> is where pixel (0, 0) lies in projected space.
    /// </summary>
    /// <summary>
    /// Whether every shape the last <see cref="RenderMeshes"/> drew was
    /// additive (ONE, ONE): its image is then added to the screen, not laid
    /// over it. KR adds a waterfall's sheets; laid over with alpha, they
    /// dimmed the water behind them and came out darker than KR's.
    /// </summary>
    public bool LastMeshesAdditive { get; private set; }

    /// <summary>Whether every shape the last <see cref="RenderMeshes"/> drew was a screen (<see cref="KrBlend.Screen"/>).</summary>
    public bool LastMeshesScreen { get; private set; }

    public uint[]? RenderMeshes(Func<string, TgaImage?> texture, Func<Vector3, Vector2> project,
        out int width, out int height, out Vector2 origin, bool rawVertices = false)
    {
        width = height = 0;
        origin = default;

        List<(Vector2[] Screen, NifBlock Data, TgaImage Image, TgaImage? Glow, Vector2 Offset, bool Additive, bool Lighten, Vector4 Shade)> shapes = [];
        Vector2 lo = new(float.MaxValue), hi = new(float.MinValue);

        for (int i = 0; i < _nif.Blocks.Count; i++)
        {
            NifBlock shape = _nif.Blocks[i];

            if (shape.Type != "NiTriShape" || _nif[shape.Ref("data")] is not { } data
                || data.Get<Vector3[]>("vertices") is not { Length: > 0 } verts
                || data.Get<Vector2[]>("uvs", []).Length != verts.Length
                || data.Get<ushort[]>("triangles", []).Length < 3)
            {
                continue;
            }

            (string name, KrBlend blend) = Look(shape);

            if (name.Length == 0 || texture(name) is not { } image)
            {
                continue;
            }

            // A spell effect's meshes (PlanesAsSprites): not its 4-vertex
            // planes, drawn as sprites; not a hidden shape - Reactive
            // Armor's first ring is flagged hidden (17) and KR does not draw it.
            Vector4 shade = Vector4.One;

            // A static's scene too: the Crystal Cave barrier's
            // 00014713_field_of_paralysis shows one arc at a time by its
            // NiVisController; ignored, every arc drew at once. Its colour
            // stays the texture's (waterfalls keep their look); only an
            // NiAlphaController fades it.
            if (!PlanesAsSprites)
            {
                if (!Visible(i, _time))
                {
                    continue;
                }

                shade.W = AlphaOnly(shape, _time);

                if (shade.W <= 0.001f && blend != KrBlend.Additive)
                {
                    continue;
                }
            }

            if (PlanesAsSprites)
            {
                if (verts.Length == 4 || !Visible(i, _time))
                {
                    continue;
                }

                shade = Shade(shape, _time);

                // Faded out - unless drawn ONE, ONE, which takes no alpha:
                // Disarm's swirl rings have alpha 0 and KR draws them.
                if (shade.W <= 0.001f && blend != KrBlend.Additive)
                {
                    continue;
                }
            }

            // Raw: the vertices as the file holds them, for a caller that has
            // KR's own world matrix, which already carries the node chain.
            verts = Morphed(shape, verts);

            Matrix4x4 world = rawVertices ? Matrix4x4.Identity : World(i, _time);
            Vector2[] screen = new Vector2[verts.Length];

            for (int v = 0; v < verts.Length; v++)
            {
                screen[v] = project(Vector3.Transform(verts[v], world));
                lo = Vector2.Min(lo, screen[v]);
                hi = Vector2.Max(hi, screen[v]);
            }

            // The glow map, added on top as KR's fixed stages do: the paralysis
            // field's arc is bigarc3 with bigarc2 as glow, which widens and
            // brightens the ribbon; the base alone drew a thin line.
            TgaImage? glow = GlowOf(shape) is { Length: > 0 } glowName ? texture(glowName) : null;

            // A screen is composed as added here; the whole image is screened.
            shapes.Add((screen, data, image, glow, UvOffset(shape), blend is KrBlend.Additive or KrBlend.Screen, blend == KrBlend.Screen, shade));

            if (FirstShapeOnly)
            {
                break;
            }
        }

        if (shapes.Count == 0)
        {
            return null;
        }

        LastMeshesScreen = shapes.TrueForAll(sh => sh.Lighten);
        LastMeshesAdditive = !LastMeshesScreen && shapes.TrueForAll(sh => sh.Additive);

        lo = new Vector2(MathF.Floor(lo.X), MathF.Floor(lo.Y));
        width = Math.Clamp((int)MathF.Ceiling(hi.X - lo.X) + 1, 1, 512);
        height = Math.Clamp((int)MathF.Ceiling(hi.Y - lo.Y) + 1, 1, 512);
        origin = lo;
        uint[] pixels = new uint[width * height];

        foreach ((Vector2[] screen, NifBlock data, TgaImage image, TgaImage? glow, Vector2 offset, bool additive, bool _, Vector4 shade) in shapes)
        {
            Vector2[] uvs = data.Get<Vector2[]>("uvs", []);
            Vector4[] colors = data.Get<Vector4[]>("colors", []);
            ushort[] tris = data.Get<ushort[]>("triangles", []);

            for (int k = 0; k + 2 < tris.Length; k += 3)
            {
                int a = tris[k], b = tris[k + 1], c = tris[k + 2];
                Vector2 pa = screen[a] - lo, pb = screen[b] - lo, pc = screen[c] - lo;
                float area = ((pb.X - pa.X) * (pc.Y - pa.Y)) - ((pb.Y - pa.Y) * (pc.X - pa.X));

                if (MathF.Abs(area) < 1e-6f)
                {
                    continue;
                }

                // Texture filtering, as KR's GPU does it: how many texels a
                // screen pixel spans here sets an n x n grid of bilinear taps
                // over the pixel. The paralysis field's arc is a 3-texel
                // strand on a 7 px ribbon; one point sample a pixel broke it
                // into dashes where KR shows a soft, continuous glow.
                Vector2 UvAt(Vector2 q)
                {
                    float qa = (((pb.X - q.X) * (pc.Y - q.Y)) - ((pb.Y - q.Y) * (pc.X - q.X))) / area;
                    float qb = (((pc.X - q.X) * (pa.Y - q.Y)) - ((pc.Y - q.Y) * (pa.X - q.X))) / area;

                    return (uvs[a] * qa) + (uvs[b] * qb) + (uvs[c] * (1f - qa - qb));
                }

                Vector2 du = UvAt(pa + Vector2.UnitX) - UvAt(pa), dv = UvAt(pa + Vector2.UnitY) - UvAt(pa);
                Vector2 texels = new(image.Width, image.Height);
                float span = MathF.Max((du * texels).Length(), (dv * texels).Length());

                // A shrunk texture is read from its mip level, as the GPU does,
                // with at most 2 x 2 taps there. Up to 4 x 4 taps of the full
                // image a pixel made hide's 20 parts cost ~300 ms a frame.
                int level = span > 2f ? Math.Min((int)MathF.Log2(span), 6) : 0;
                TgaImage tapImage = Mip(image, level);
                TgaImage? tapGlow = glow is null ? null : Mip(glow, level);
                int taps = span / (1 << level) > 1.5f ? 2 : 1;

                int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.X, MathF.Min(pb.X, pc.X))));
                int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(pa.X, MathF.Max(pb.X, pc.X))));
                int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(pa.Y, MathF.Min(pb.Y, pc.Y))));
                int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(pa.Y, MathF.Max(pb.Y, pc.Y))));

                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        Vector2 q = new(x + 0.5f, y + 0.5f);
                        float wa = (((pb.X - q.X) * (pc.Y - q.Y)) - ((pb.Y - q.Y) * (pc.X - q.X))) / area;
                        float wb = (((pc.X - q.X) * (pa.Y - q.Y)) - ((pc.Y - q.Y) * (pa.X - q.X))) / area;
                        float wc = 1f - wa - wb;

                        if (wa < 0f || wb < 0f || wc < 0f)
                        {
                            continue;
                        }

                        Vector2 uv = (uvs[a] * wa) + (uvs[b] * wb) + (uvs[c] * wc) + offset;
                        Vector4 tint = colors.Length == uvs.Length
                            ? (colors[a] * wa) + (colors[b] * wb) + (colors[c] * wc)
                            : Vector4.One;

                        Vector4 texel = default, glowTexel = default;
                        Vector2 glowUv = uv - offset;

                        for (int sy = 0; sy < taps; sy++)
                        {
                            for (int sx = 0; sx < taps; sx++)
                            {
                                Vector2 step = (du * (((sx + 0.5f) / taps) - 0.5f)) + (dv * (((sy + 0.5f) / taps) - 0.5f));
                                texel += SampleBilinear(tapImage, uv + step);

                                if (tapGlow != null)
                                {
                                    glowTexel += SampleBilinear(tapGlow, glowUv + step);
                                }
                            }
                        }

                        Vector4 colour = texel / (taps * taps) * tint * shade;

                        if (glow != null)
                        {
                            colour += glowTexel / (taps * taps) * new Vector4(shade.W);
                        }

                        Blend(ref pixels[(y * width) + x], colour, additive);
                    }
                }
            }
        }

        return pixels;
    }

    /// <summary>
    /// Scene space to the screen in scene units, as particles are drawn:
    /// through the scene's camera (Facing), x right, y down.
    /// </summary>
    public Vector2 ScreenUnits(Vector3 v)
    {
        Vector3 c = Vector3.TransformNormal(v, _view);

        return new Vector2(c.X, -c.Y);
    }

    /// <summary>
    /// Whether a node is drawn at time <paramref name="t"/>: neither it nor
    /// an ancestor is hidden (flag bit 0) or switched off by a NiVisController.
    /// </summary>
    private bool Visible(int node, float t)
    {
        for (int n = node; n >= 0; n = _parent[n])
        {
            NifBlock b = _nif.Blocks[n];

            // A NiVisController sets the hidden bit as it plays, so its keys,
            // not the stored flag, decide: Disarm's swirl rings sit under
            // nodes flagged hidden (23) that it shows from 0.13 s.
            bool controlled = false;

            for (int c = b.Ref("controller"); _nif[c] is { } ctl; c = ctl.Ref("next"))
            {
                if (ctl.Type != "NiVisController" || _nif[ctl.Ref("interpolator")] is not { } interp)
                {
                    continue;
                }

                controlled = true;
                int on = interp.Get("value", 1) == 0 ? 0 : 1;

                if (_nif[interp.Ref("data")]?.Get<NifKeys<int>>("keys") is { Keys.Length: > 0 } keys)
                {
                    float ct = ControllerTime(ctl, t);
                    on = keys.Keys[0].Value;

                    foreach ((float time, int value) in keys.Keys)
                    {
                        if (time <= ct)
                        {
                            on = value;
                        }
                    }
                }

                if (on == 0)
                {
                    return false;
                }
            }

            if (!controlled && (b.Get("flags", 0) & 1) != 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A spell effect mesh's colour and opacity at time <paramref name="t"/>:
    /// its material's alpha, animated by a NiAlphaController, and colour by a
    /// NiMaterialColorController, on the shape or its material. Reactive
    /// Armor's rings fade in over 1 s, hold, and are gone at 5 s.
    /// </summary>
    /// <summary>A shape's alpha from NiAlphaControllers alone (on it or its material), 1 without one.</summary>
    private float AlphaOnly(NifBlock shape, float t)
    {
        float alpha = 1f;
        List<NifBlock> owners = [shape];

        foreach (int p in shape.Get<int[]>("properties", []))
        {
            if (_nif[p] is { Type: "NiMaterialProperty" } material)
            {
                owners.Add(material);
            }
        }

        foreach (NifBlock owner in owners)
        {
            for (int c = owner.Ref("controller"); _nif[c] is { } ctl; c = ctl.Ref("next"))
            {
                if (ctl.Type == "NiAlphaController")
                {
                    alpha = FloatAt(ctl.Ref("interpolator"), ControllerTime(ctl, t), alpha);
                }
            }
        }

        return alpha;
    }

    private Vector4 Shade(NifBlock shape, float t)
    {
        Vector3 color = Vector3.One;
        float alpha = 1f;
        List<NifBlock> owners = [shape];

        foreach (int p in shape.Get<int[]>("properties", []))
        {
            if (_nif[p] is { Type: "NiMaterialProperty" } material)
            {
                // Lit with no lights and no ambient (as KR): the
                // material's emissive colour, unless a controller animates it.
                // A black one is drawn with the texture's colour: Teleport's
                // tall glow (5023; emissive 0, alpha 0.25)
                // goes through a pixel shader in KR, a faint blue beam - as
                // black it was a dark column.
                color = material.Get("emissive", Vector3.One);
                alpha = material.Get("alpha", 1f);

                if (color.X + color.Y + color.Z <= 0.001f)
                {
                    color = Vector3.One;
                }
                owners.Add(material);
            }
        }

        foreach (NifBlock owner in owners)
        {
            for (int c = owner.Ref("controller"); _nif[c] is { } ctl; c = ctl.Ref("next"))
            {
                float ct = ControllerTime(ctl, t);

                if (ctl.Type == "NiAlphaController")
                {
                    alpha = FloatAt(ctl.Ref("interpolator"), ct, alpha);
                }
                else if (ctl.Type == "NiMaterialColorController" && ctl.Get("target_color", 3) == 3 && _nif[ctl.Ref("interpolator")] is { } interp)
                {
                    color = _nif[interp.Ref("data")]?.Get<NifKeys<Vector3>>("keys") is { Keys.Length: > 0 } keys
                        ? Sample(keys, ct)
                        : interp.Get("value", color);
                }
            }
        }

        return new Vector4(Vector3.Clamp(color, Vector3.Zero, Vector3.One), Math.Clamp(alpha, 0f, 1f));
    }

    /// <summary>
    /// A shape's vertices with its NiGeomMorpherController applied:
    /// 00014138_sparkle's flare planes are short strips whose two relative
    /// morph targets, weighted -0.25 to 1.4 over a 2.67 s loop, stretch them
    /// into the long rays KR shows; unmorphed they drew as a dot. Relative
    /// morphs add weight x target to the base (morph 0 is the base itself).
    /// </summary>
    private Vector3[] Morphed(NifBlock shape, Vector3[] verts)
    {
        for (int c = shape.Ref("controller"); _nif[c] is { } ctl; c = ctl.Ref("next"))
        {
            if (ctl.Type != "NiGeomMorpherController" || _nif[ctl.Ref("data")] is not { } data
                || data.Get<Vector3[][]>("morphs") is not { Length: > 1 } morphs)
            {
                continue;
            }

            int[] interps = ctl.Get<int[]>("interpolators", []);
            float start = ctl.Float("start"), stop = ctl.Float("stop");
            float ct = (_time * ctl.Float("frequency", 1f)) + ctl.Float("phase");

            if (stop > start)
            {
                float span = stop - start;
                ct = start + ((((ct - start) % span) + span) % span);
            }

            bool relative = data.Get("relative", true);
            Vector3[] result = relative ? (Vector3[])verts.Clone() : new Vector3[verts.Length];

            for (int m = relative ? 1 : 0; m < morphs.Length && m < interps.Length; m++)
            {
                if (morphs[m].Length != verts.Length)
                {
                    continue;
                }

                float w = FloatAt(interps[m], ct, 0f);

                if (!float.IsFinite(w) || MathF.Abs(w) > 1000f)
                {
                    w = 0f;
                }

                for (int v = 0; v < verts.Length; v++)
                {
                    result[v] += morphs[m][v] * w;
                }
            }

            return result;
        }

        return verts;
    }

    private Vector2 UvOffset(NifBlock shape)
    {
        Vector2 offset = default;

        foreach (int p in shape.Get<int[]>("properties", []))
        {
            if (_nif[p] is not { Type: "NiTexturingProperty" } prop)
            {
                continue;
            }

            for (int c = prop.Ref("controller"); _nif[c] is { } ctl; c = ctl.Ref("next"))
            {
                // Only the base texture's transform moves what is drawn: the
                // oven's coals scroll their detail texture (slot 1).
                if (ctl.Type != "NiTextureTransformController" || ctl.Get("slot", 0) != 0)
                {
                    continue;
                }

                // Looped over the controller's range rather than clamped.
                float start = ctl.Float("start"), stop = ctl.Float("stop");
                float ct = (_time * ctl.Float("frequency", 1f)) + ctl.Float("phase");

                if (stop > start)
                {
                    float span = stop - start;
                    ct = start + ((((ct - start) % span) + span) % span);
                }

                float value = FloatAt(ctl.Ref("interpolator"), ct, 0f);

                switch (ctl.Get("operation", 0))
                {
                    case 0: offset.X += value; break;
                    case 1: offset.Y += value; break;
                }
            }
        }

        return offset;
    }

    /// <summary>A wrapped bilinear sample, as the GPU filters a texture.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TgaImage, List<TgaImage>> _mips = new();

    /// <summary>The image halved <paramref name="level"/> times (2 x 2 box), cached per image.</summary>
    private static TgaImage Mip(TgaImage image, int level)
    {
        if (level <= 0)
        {
            return image;
        }

        List<TgaImage> chain = _mips.GetValue(image, i => [i]);

        lock (chain)
        {
            while (chain.Count <= level && chain[^1] is { Width: > 1 } or { Height: > 1 })
            {
                TgaImage src = chain[^1];
                int w = Math.Max(1, src.Width / 2), h = Math.Max(1, src.Height / 2);
                uint[] px = new uint[w * h];

                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int sx = Math.Min(x * 2, src.Width - 1), sy = Math.Min(y * 2, src.Height - 1);
                        int sx1 = Math.Min(sx + 1, src.Width - 1), sy1 = Math.Min(sy + 1, src.Height - 1);
                        uint p0 = src.Pixels[(sy * src.Width) + sx], p1 = src.Pixels[(sy * src.Width) + sx1];
                        uint p2 = src.Pixels[(sy1 * src.Width) + sx], p3 = src.Pixels[(sy1 * src.Width) + sx1];
                        uint r = 0;

                        for (int shift = 0; shift < 32; shift += 8)
                        {
                            uint sum = ((p0 >> shift) & 0xFF) + ((p1 >> shift) & 0xFF) + ((p2 >> shift) & 0xFF) + ((p3 >> shift) & 0xFF);
                            r |= ((sum + 2) / 4) << shift;
                        }

                        px[(y * w) + x] = r;
                    }
                }

                chain.Add(TgaImage.FromPixels(w, h, px));
            }

            return chain[Math.Min(level, chain.Count - 1)];
        }
    }

    private static Vector4 SampleBilinear(TgaImage image, Vector2 uv)
    {
        float fx = (uv.X * image.Width) - 0.5f, fy = (uv.Y * image.Height) - 0.5f;
        int x = (int)MathF.Floor(fx), y = (int)MathF.Floor(fy);
        float tx = fx - x, ty = fy - y;

        Vector4 At(int px, int py)
        {
            px = ((px % image.Width) + image.Width) % image.Width;
            py = ((py % image.Height) + image.Height) % image.Height;
            uint p = image.Pixels[(py * image.Width) + px];

            return new Vector4(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, p >> 24) / 255f;
        }

        return Vector4.Lerp(Vector4.Lerp(At(x, y), At(x + 1, y), tx), Vector4.Lerp(At(x, y + 1), At(x + 1, y + 1), tx), ty);
    }

    private static Vector4 SampleWrap(TgaImage image, Vector2 uv)
    {
        float u = uv.X - MathF.Floor(uv.X), v = uv.Y - MathF.Floor(uv.Y);
        int x = Math.Min(image.Width - 1, (int)(u * image.Width));
        int y = Math.Min(image.Height - 1, (int)(v * image.Height));
        uint p = image.Pixels[(y * image.Width) + x];

        return new Vector4(p & 0xFF, (p >> 8) & 0xFF, (p >> 16) & 0xFF, p >> 24) / 255f;
    }

    /// <summary>
    /// Lays a texel on the image, which holds premultiplied colour: additive
    /// (ONE/ONE, the waterfall and the sparkle flares) adds its colour
    /// whatever its alpha - weighting by alpha lost the flares' faint-alpha
    /// rays - with its brightness as coverage; alpha blending (the oven's
    /// coals) lays colour times alpha over what is there.
    /// </summary>
    private static void Blend(ref uint dst, Vector4 src, bool additive)
    {
        Vector4 d = new Vector4(dst & 0xFF, (dst >> 8) & 0xFF, (dst >> 16) & 0xFF, dst >> 24) / 255f;
        float a = Math.Clamp(src.W, 0f, 1f);
        Vector3 rgb = additive
            ? new Vector3(src.X, src.Y, src.Z) + new Vector3(d.X, d.Y, d.Z)
            : (new Vector3(src.X, src.Y, src.Z) * a) + (new Vector3(d.X, d.Y, d.Z) * (1f - a));
        float outA = additive
            ? Math.Max(d.W, Math.Min(1f, Math.Max(rgb.X, Math.Max(rgb.Y, rgb.Z))))
            : a + (d.W * (1f - a));

        dst = (uint)(Math.Clamp(rgb.X, 0f, 1f) * 255f)
            | ((uint)(Math.Clamp(rgb.Y, 0f, 1f) * 255f) << 8)
            | ((uint)(Math.Clamp(rgb.Z, 0f, 1f) * 255f) << 16)
            | ((uint)(Math.Clamp(outA, 0f, 1f) * 255f) << 24);
    }

    // ---- key sampling (linear; quadratic and TBC keys are read as linear) ----

    private static float Sample(NifKeys<float> k, float t, float fallback)
    {
        if (k.Keys.Length == 0)
        {
            return fallback;
        }

        if (t <= k.Keys[0].Time)
        {
            return k.Keys[0].Value;
        }

        for (int i = 1; i < k.Keys.Length; i++)
        {
            if (t <= k.Keys[i].Time)
            {
                if (k.Interpolation == 5)
                {
                    return k.Keys[i - 1].Value;     // step
                }

                float f = (t - k.Keys[i - 1].Time) / Math.Max(1e-6f, k.Keys[i].Time - k.Keys[i - 1].Time);

                return k.Keys[i - 1].Value + ((k.Keys[i].Value - k.Keys[i - 1].Value) * f);
            }
        }

        return k.Keys[^1].Value;
    }

    private static Vector3 Sample(NifKeys<Vector3> k, float t)
    {
        if (t <= k.Keys[0].Time)
        {
            return k.Keys[0].Value;
        }

        for (int i = 1; i < k.Keys.Length; i++)
        {
            if (t <= k.Keys[i].Time)
            {
                float f = (t - k.Keys[i - 1].Time) / Math.Max(1e-6f, k.Keys[i].Time - k.Keys[i - 1].Time);

                // Quadratic keys: a Hermite curve through them, leaving each
                // by its forward tangent and arriving by the next one's
                // backward. Clumsy's ring path is 5 keys; straight between
                // them it was a diamond where KR draws a circle.
                if (k.Tangents is { } tan)
                {
                    float f2 = f * f, f3 = f2 * f;

                    return (k.Keys[i - 1].Value * ((2 * f3) - (3 * f2) + 1))
                        + (tan[i - 1].Forward * (f3 - (2 * f2) + f))
                        + (k.Keys[i].Value * ((-2 * f3) + (3 * f2)))
                        + (tan[i].Backward * (f3 - f2));
                }

                return Vector3.Lerp(k.Keys[i - 1].Value, k.Keys[i].Value, f);
            }
        }

        return k.Keys[^1].Value;
    }

    private static Vector4 Sample(NifKeys<Vector4> k, float t)
    {
        if (k.Keys.Length == 0)
        {
            return Vector4.One;
        }

        if (t <= k.Keys[0].Time)
        {
            return k.Keys[0].Value;
        }

        for (int i = 1; i < k.Keys.Length; i++)
        {
            if (t <= k.Keys[i].Time)
            {
                float f = (t - k.Keys[i - 1].Time) / Math.Max(1e-6f, k.Keys[i].Time - k.Keys[i - 1].Time);

                return Vector4.Lerp(k.Keys[i - 1].Value, k.Keys[i].Value, f);
            }
        }

        return k.Keys[^1].Value;
    }

    private static Quaternion SampleQuat(NifKeys<Quaternion> k, float t)
    {
        if (t <= k.Keys[0].Time)
        {
            return k.Keys[0].Value;
        }

        for (int i = 1; i < k.Keys.Length; i++)
        {
            if (t <= k.Keys[i].Time)
            {
                float f = (t - k.Keys[i - 1].Time) / Math.Max(1e-6f, k.Keys[i].Time - k.Keys[i - 1].Time);

                return Quaternion.Slerp(k.Keys[i - 1].Value, k.Keys[i].Value, f);
            }
        }

        return k.Keys[^1].Value;
    }

    /// <summary>A float interpolator's value at <paramref name="t"/>: its keys, else its constant.</summary>
    private float FloatAt(int interpolator, float t, float fallback)
    {
        if (_nif[interpolator] is not { } interp)
        {
            return fallback;
        }

        float value = interp.Get("value", fallback);

        return _nif[interp.Ref("data")]?.Get<NifKeys<float>>("keys") is { Keys.Length: > 0 } keys
            ? Sample(keys, t, value)
            : value;
    }

    // ---- one particle system ----------------------------------------------

    private struct Particle
    {
        public Vector3 Position;
        public Vector3 Velocity;
        public float Age;
        public float Life;
        public float Radius;
        public float Rotation;
        public float Spin;
        public Vector2 Origin;
        public Vector3 Emitter;

        /// <summary>The system node's world at emission, inverted (local systems).</summary>
        public Matrix4x4 Unworld;
    }

    private sealed class System_
    {
        private readonly NifEffect _fx;
        private readonly NifBlock _ps;
        private readonly int _psIndex;
        private readonly List<Particle> _live = [];
        private readonly NifBlock? _emitter;
        private readonly NifBlock? _emitterCtl;
        private readonly int _speedInterp = -1;
        private readonly NifBlock? _growFade;
        private readonly NifKeys<Vector4>? _colors;
        private readonly List<NifBlock> _gravity = [];
        private readonly NifBlock? _rotation;
        private readonly float _scale;
        private readonly string _texture = "";
        private readonly string _glow = "";
        private readonly bool _glowOnly;
        private readonly KrBlend _blend;
        private readonly bool _worldSpace;
        private readonly Vector3? _emissive;
        private readonly NifBlock? _emissiveCtl;
        private float _owed;

        // Emitter settings and gravity strengths animated over the effect's
        // time. Arch Protection's gravity (2040) is ramped by one over 1.17 s;
        // held at full, its sparkles flew 150 units off the screen.
        private readonly Dictionary<string, NifBlock> _animated = [];
        private readonly Dictionary<string, NifBlock> _gravityStrength = [];

        private Vector3 EmissiveAt(Vector3 fallback)
        {
            if (_emissiveCtl is null || _fx._nif[_emissiveCtl.Ref("interpolator")] is not { } interp)
            {
                return fallback;
            }

            float ct = ControllerTime(_emissiveCtl, _fx._time);

            return _fx._nif[interp.Ref("data")]?.Get<NifKeys<Vector3>>("keys") is { Keys.Length: > 0 } keys
                ? Sample(keys, ct)
                : interp.Get("value", fallback);
        }

        private float Animated(string controller, float t, float fallback) =>
            _animated.TryGetValue(controller, out NifBlock? ctl)
                ? _fx.FloatAt(ctl.Ref("interpolator"), ControllerTime(ctl, t), fallback)
                : fallback;

        public System_(NifEffect fx, int index)
        {
            _fx = fx;
            _psIndex = index;
            _ps = fx._nif.Blocks[index];

            foreach (int m in _ps.Get<int[]>("modifiers", []))
            {
                if (fx._nif[m] is not { } mod || !mod.Get("active", true))
                {
                    continue;
                }

                switch (mod.Type)
                {
                    case "NiPSysBoxEmitter":
                    case "NiPSysSphereEmitter":
                    case "NiPSysCylinderEmitter":
                    case "NiPSysMeshEmitter":
                        _emitter ??= mod;

                        break;

                    case "NiPSysGrowFadeModifier":
                        _growFade = mod;

                        break;

                    case "NiPSysColorModifier":
                        _colors = fx._nif[mod.Ref("data")]?.Get<NifKeys<Vector4>>("keys");

                        break;

                    case "NiPSysGravityModifier":
                        _gravity.Add(mod);

                        break;

                    case "NiPSysRotationModifier":
                        _rotation = mod;

                        break;
                }
            }

            for (int c = _ps.Ref("controller"); fx._nif[c] is { } ctl; c = ctl.Ref("next"))
            {
                if (ctl.Type == "NiPSysEmitterCtlr")
                {
                    _emitterCtl = ctl;
                }
                else if (ctl.Type == "NiPSysEmitterSpeedCtlr")
                {
                    _speedInterp = ctl.Ref("interpolator");
                }
                else if (ctl.Type is "NiPSysEmitterLifeSpanCtlr" or "NiPSysEmitterInitialRadiusCtlr"
                    or "NiPSysInitialRotSpeedCtlr" or "NiPSysInitialRotSpeedVarCtlr")
                {
                    _animated[ctl.Type] = ctl;
                }
                else if (ctl.Type == "NiPSysGravityStrengthCtlr")
                {
                    _gravityStrength[ctl.Get("modifier", "")] = ctl;
                }
            }

            // Scale: the system's own node scale and its ancestors'.
            _scale = fx.World(index, 0f).GetScale();

            (_texture, _blend) = fx.Look(_ps);

            // Sprites have no screen pass: added, it lightens alike.
            if (_blend == KrBlend.Screen)
            {
                _blend = KrBlend.Additive;
            }
            _glow = fx.GlowOf(_ps);
            _glowOnly = fx.GlowOnly(_ps);

            // Vertex colours ignored (NiVertexColorProperty vertex mode 0):
            // the particles take the material's emissive colour. KR drew
            // Heal's sparkles lit, emissive (0.78, 0.84, 0.95).
            NifBlock? material = null;
            bool ignoreVertex = false;

            foreach (int p in _ps.Get<int[]>("properties", []))
            {
                // Lit with vertex mode 2 too: the vertex colour is then the
                // ambient and diffuse, which KR's lights (none, ambient 0)
                // leave black - the emissive colour alone shows. A waterfall's
                // drops (03013556_waterfall_downlong, emissive 0.35-0.4) were
                // drawn white from their colour keys; KR's are faint.
                // Vertex mode 1 (lighting off) keeps its vertex colours:
                // Clumsy's sparkles (mode 1, emissive white) are KR's purple
                // from their colour keys - taking the emissive drew them white.
                if (fx._nif[p] is { Type: "NiVertexColorProperty" } vc)
                {
                    int mode = vc.Get("vertex_mode", 1);
                    ignoreVertex = mode == 0 || (mode == 2 && vc.Get("lighting_mode", 1) == 1);
                }
                else if (fx._nif[p] is { Type: "NiMaterialProperty" } m)
                {
                    material = m;
                }
            }

            if (ignoreVertex && material is not null)
            {
                _emissive = material.Get("emissive", Vector3.One);

                // Its emissive colour animated (NiMaterialColorController,
                // target 3, self-illumination): Agility's is black but for
                // its controller, which KR plays - unread, it added nothing.
                for (int c = material.Ref("controller"); fx._nif[c] is { } ctl; c = ctl.Ref("next"))
                {
                    if (ctl.Type == "NiMaterialColorController" && ctl.Get("target_color", 0) == 3)
                    {
                        _emissiveCtl = ctl;
                    }
                }
            }

            _worldSpace = _ps.Get("world_space", false);

            // NiPSysData's vertex count is how many particles the system has
            // room for - 1 for each bubble in 00012865_Bubble_Swamp, whose
            // emitter's 99864/s would otherwise put ~1,660 on one spot a frame.
            // *Hypothesis:* Gamebryo emits no more than that many live.
            int room = fx._nif[_ps.Ref("data")]?.Get("vertex_count", 0) ?? 0;
            _maxLive = room > 0 ? Math.Min(room, 2000) : 2000;
            EmitUntil = EmitEnd();
            MaxLife = (_emitter?.Float("life") ?? 0f) + ((_emitter?.Float("life_var") ?? 0f) / 2f);
        }

        private readonly int _maxLive;

        public float EmitUntil { get; }

        /// <summary>Its emitter controller loops (cycle type 0, a span to loop over).</summary>
        public bool LoopsEmitter =>
            _emitterCtl is not null && Cycle(_emitterCtl) == 0 && _emitterCtl.Float("stop") > _emitterCtl.Float("start");

        public float MaxLife { get; }

        public int Live => _live.Count;

        private float EmitEnd()
        {
            if (_emitterCtl is null)
            {
                return 0f;
            }

            float end = _emitterCtl.Float("stop");

            if (_fx._nif[_emitterCtl.Ref("visibility")] is { } vis
                && _fx._nif[vis.Ref("data")]?.Get<NifKeys<int>>("keys") is { Keys.Length: > 0 } keys)
            {
                end = 0f;

                for (int i = 0; i < keys.Keys.Length; i++)
                {
                    if (keys.Keys[i].Value != 0)
                    {
                        end = i + 1 < keys.Keys.Length ? keys.Keys[i + 1].Time : _emitterCtl.Float("stop");
                    }
                }
            }

            return end;
        }

        private bool Emitting(float t, float dt)
        {
            if (_emitterCtl is null)
            {
                return false;
            }

            float ct = ControllerTime(_emitterCtl, t);

            // A looping emitter (Cunning's) fires again each cycle, up to the
            // scene's end; one that clamps stops at its last "on" key.
            bool looping = LoopsEmitter && t <= _fx._duration;

            if (_fx._nif[_emitterCtl.Ref("visibility")] is { } vis)
            {
                if (_fx._nif[vis.Ref("data")]?.Get<NifKeys<int>>("keys") is { Keys.Length: > 0 } keys)
                {
                    int on = keys.Keys[0].Value;
                    float previous = ct - dt;

                    foreach ((float time, int value) in keys.Keys)
                    {
                        if (time <= ct)
                        {
                            on = value;
                        }

                        // An "on" key straight followed by "off" at the same
                        // time is a one-frame burst (00012865_Bubble_Swamp:
                        // 0:0 1.13:1 1.13:0, birth rate 99864/s); taking the
                        // last key at or before now would never see it on.
                        // *Hypothesis:* Gamebryo emits for the frame that
                        // crosses such a key.
                        if (value != 0 && time > previous && time <= ct)
                        {
                            return looping || t <= EmitUntil + dt;
                        }
                    }

                    return on != 0 && (looping || t <= EmitUntil);
                }

                return vis.Get("value", 1) != 0;
            }

            return t <= EmitUntil;
        }

        // A system not in world space keeps its particles in its node's
        // frame: KR draws Blade Spirits' (5034) through
        // each emitter's current world matrix, so they ride Dummy01's spin
        // as compact bursts. Left where they were emitted, they strung out
        // in lines (Curse) and scattered (Blade Spirits).
        private Matrix4x4 _nodeNow = Matrix4x4.Identity;

        private Vector3 Placed(in Particle p) =>
            _worldSpace ? p.Position : Vector3.Transform(Vector3.Transform(p.Position, p.Unworld), _nodeNow);

        private float Rand(float variation) => variation * ((float)(_fx._rng.NextDouble() * 2.0) - 1f);

        public void Update(float t, float dt)
        {
            _nodeNow = _fx.World(_psIndex, t);

            if (_emitter is not null && Emitting(t, dt))
            {
                float rate = _fx.FloatAt(_emitterCtl!.Ref("interpolator"), ControllerTime(_emitterCtl, t), 0f);
                _owed += rate * dt;

                while (_owed >= 1f && _live.Count < _maxLive)
                {
                    _owed -= 1f;
                    Emit(t);
                }

                // A full system drops what it could not emit, rather than
                // bursting it out once particles die.
                if (_live.Count >= _maxLive)
                {
                    _owed = 0f;
                }
            }

            for (int i = _live.Count - 1; i >= 0; i--)
            {
                Particle p = _live[i];
                p.Age += dt;

                if (p.Age >= p.Life)
                {
                    _live.RemoveAt(i);

                    continue;
                }

                foreach (NifBlock g in _gravity)
                {
                    float strength = _gravityStrength.TryGetValue(g.Get("name", ""), out NifBlock? ctl)
                        ? _fx.FloatAt(ctl.Ref("interpolator"), ControllerTime(ctl, t), g.Float("strength"))
                        : g.Float("strength");
                    Matrix4x4 frame = _fx._nif[g.Ref("gravity_object")] is not null
                        ? _fx.World(g.Ref("gravity_object"), t)
                        : Matrix4x4.Identity;

                    Vector3 pull = g.Get("force_type", 0) == 0
                        ? Vector3.Normalize(Vector3.TransformNormal(g.Get("axis", Vector3.UnitZ), frame))
                        : SafeNormalize(frame.Translation - p.Position);

                    // Decay: weaker with distance from the gravity object,
                    // e^(-decay * distance) with the distance in the system's
                    // own units (before its node scale). Arch Protection's
                    // systems are scaled 0.367: with the distance unscaled
                    // its sparkles spread 3.1 units high where the KR client's own
                    // KR spread them 0.3; in its own units, 0.33.
                    float decay = g.Float("decay");

                    if (decay > 0f)
                    {
                        strength *= MathF.Exp(-decay * Vector3.Distance(frame.Translation, p.Position) / Math.Max(_scale, 1e-4f));
                    }

                    p.Velocity += pull * strength * _scale * dt;
                }

                p.Position += p.Velocity * dt;
                p.Rotation += p.Spin * dt;
                _live[i] = p;
            }
        }

        private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 1e-8f ? Vector3.Normalize(v) : Vector3.Zero;

        private void Emit(float t)
        {
            NifBlock e = _emitter!;
            int frameNode = e.Ref("emitter_object");
            Matrix4x4 frame = _fx._nif[frameNode] is not null ? _fx.World(frameNode, t) : _fx.World(_psIndex, t);

            Vector3 local = e.Type switch
            {
                "NiPSysBoxEmitter" => new Vector3(
                    Rand(e.Float("width") / 2f), Rand(e.Float("height") / 2f), Rand(e.Float("depth") / 2f)),
                "NiPSysSphereEmitter" => RandomInSphere() * e.Float("sphere_radius"),
                "NiPSysCylinderEmitter" => CylinderPoint(e.Float("cylinder_radius"), e.Float("cylinder_height")),
                _ => Vector3.Zero,
            };

            // A mesh emitter: a random point on one of its meshes' faces, in
            // that mesh's frame. The Moonglow fountain's splashes rise from
            // the cylinder round its basin; all at the node's origin, they
            // were one blob at the spout's foot.
            if (e.Type == "NiPSysMeshEmitter" && e.Get<int[]>("meshes", []) is { Length: > 0 } meshes)
            {
                int mesh = meshes[_fx._rng.Next(meshes.Length)];

                if (_fx._nif[mesh] is { } shape && _fx._nif[shape.Ref("data")] is { } data
                    && data.Get<Vector3[]>("vertices") is { Length: > 0 } verts)
                {
                    ushort[] tris = data.Get<ushort[]>("triangles", []);
                    frame = _fx.World(mesh, t);

                    if (tris.Length >= 3)
                    {
                        int k = _fx._rng.Next(tris.Length / 3) * 3;
                        float u = (float)_fx._rng.NextDouble(), v = (float)_fx._rng.NextDouble();

                        if (u + v > 1f)
                        {
                            (u, v) = (1f - u, 1f - v);
                        }

                        local = verts[tris[k]] + ((verts[tris[k + 1]] - verts[tris[k]]) * u) + ((verts[tris[k + 2]] - verts[tris[k]]) * v);
                    }
                    else
                    {
                        local = verts[_fx._rng.Next(verts.Length)];
                    }
                }
            }

            float declination = e.Float("declination") + Rand(e.Float("declination_var"));
            float planar = e.Float("planar") + Rand(e.Float("planar_var"));
            Vector3 dir = new(
                MathF.Sin(declination) * MathF.Cos(planar),
                MathF.Sin(declination) * MathF.Sin(planar),
                MathF.Cos(declination));

            float speed = (_speedInterp >= 0 ? _fx.FloatAt(_speedInterp, t, e.Float("speed")) : e.Float("speed"))
                + Rand(e.Float("speed_var") / 2f);   // half each side, as life

            Particle p = new()
            {
                Position = Vector3.Transform(local, frame),
                Emitter = frame.Translation,
                Origin = _fx.Origin,
                Unworld = Matrix4x4.Invert(_nodeNow, out Matrix4x4 unworld) ? unworld : Matrix4x4.Identity,
                Velocity = SafeNormalize(Vector3.TransformNormal(dir, frame)) * speed * _scale,
                // Life varies by half its variation each side: Curse (5028) had KR's count at 1 s and was gone by 1.5 s only so.
                Life = Math.Max(0.01f, Animated("NiPSysEmitterLifeSpanCtlr", t, e.Float("life")) + Rand(e.Float("life_var") / 2f)),
                Radius = Math.Max(0f, Animated("NiPSysEmitterInitialRadiusCtlr", t, e.Float("radius")) + Rand(e.Float("radius_var"))),
            };

            if (_rotation is not null)
            {
                p.Rotation = _rotation.Float("rotation_angle") + Rand(_rotation.Float("rotation_angle_var"));
                p.Spin = Animated("NiPSysInitialRotSpeedCtlr", t, _rotation.Float("rotation_speed"))
                    + Rand(Animated("NiPSysInitialRotSpeedVarCtlr", t, _rotation.Float("rotation_speed_var")));

                if (_rotation.Get("random_sign", false) && _fx._rng.Next(2) == 0)
                {
                    p.Spin = -p.Spin;
                }
            }

            _live.Add(p);
        }

        private Vector3 RandomInSphere()
        {
            Vector3 v;

            do
            {
                v = new Vector3(Rand(1f), Rand(1f), Rand(1f));
            }
            while (v.LengthSquared() > 1f);

            return v;
        }

        private Vector3 CylinderPoint(float radius, float height)
        {
            Vector3 v = RandomInSphere();

            // Base to top, not centred: Vengeful Spirit's first particles sit a
            // unit above the feet in KR.
            return new Vector3(v.X * radius, v.Y * radius, height / 2f + Rand(height / 2f));
        }

        public IEnumerable<EmsSprite> Sprites()
        {
            foreach (Particle p in _live)
            {
                float size = p.Radius;

                if (_growFade is not null)
                {
                    float grow = _growFade.Float("grow");
                    float fade = _growFade.Float("fade");

                    if (grow > 0f && p.Age < grow)
                    {
                        size *= p.Age / grow;
                    }

                    if (fade > 0f && p.Life - p.Age < fade)
                    {
                        size *= (p.Life - p.Age) / fade;
                    }
                }

                Vector4 color = _emitter?.Get("color", Vector4.One) ?? Vector4.One;

                if (_colors is { Keys.Length: > 0 })
                {
                    color *= Sample(_colors, p.Age / p.Life);
                }

                // The material's colour in place of the vertex colour; the
                // keys' alpha still fades it.
                if (_emissive is { } emissive)
                {
                    color = new Vector4(EmissiveAt(emissive), color.W);
                }

                // A glow-only map is added over the colour: the texture as it is.
                if (_glowOnly)
                {
                    color = new Vector4(Vector3.One, color.W);
                }

                Vector3 placed = Placed(p);

                yield return new EmsSprite(
                    _texture, _fx.Facing(placed) * PositionScale, size * _scale, p.Rotation * (180f / MathF.PI), color,
                    _blend, 0, 1, 1, p.Origin, _worldSpace, placed, Emitter: p.Emitter, Glow: _glow.Length > 0 ? _glow : null);
            }
        }
    }
}

internal static class MatrixScale
{
    /// <summary>The uniform scale of a transform (length of its first row).</summary>
    public static float GetScale(this Matrix4x4 m) => new Vector3(m.M11, m.M12, m.M13).Length();
}
