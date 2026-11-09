// SPDX-License-Identifier: BSD-2-Clause
// Draws KR effects headless, the way the client's KrParticles draws a spell
// effect on a mobile, at the moments the client's -krfxsheet captured them -
// to compare with the KR client's own frames
// while the placement constants are tuned, without running the game.

using System.Globalization;
using System.Numerics;
using System.Text.Json;
using ClassicUO.KR.Effects;
using ClassicUO.KR.Util;

namespace ClassicUO.KR.Tool;

internal static class EffectRender
{
    private const int Size = 280;

    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine(
                "usage: effect-render <uokrPath> <EffectSheet dir> <out dir> [--ids 4,8] [--pos-px 12.4] [--size-px 20]\n" +
                "       [--lift-k 1] [--anchor 146,162] [--bg <dir>]   (bg: NNN_bg.rgba, 280x280 RGBA, drawn over)\n" +
                "       [--parts --zoom 1.7]   (the definition record's parts, as KrParticles.SpawnParts plays them)");

            return 1;
        }

        using KrAssetStore store = new(args[0]);
        KrEffects effects = new(store);
        string sheet = args[1], outDir = args[2];
        Directory.CreateDirectory(outDir);

        float posPx = Float(args, "--pos-px", 31f * NifEffect.PositionScale);
        float sizePx = Float(args, "--size-px", 20f);
        float liftK = Float(args, "--lift-k", 1f);
        string[] anchorText = (Program.GetOption(args, "--anchor") ?? "146,162").Split(',');
        Vector2 feet = new(float.Parse(anchorText[0], CultureInfo.InvariantCulture), float.Parse(anchorText[1], CultureInfo.InvariantCulture));
        string? bgDir = Program.GetOption(args, "--bg");
        HashSet<int>? ids = Program.GetOption(args, "--ids")?.Split(',').Select(int.Parse).ToHashSet();
        Dictionary<string, TgaImage?> textures = new(StringComparer.OrdinalIgnoreCase);
        bool parts = args.Contains("--parts");
        float zoom = Float(args, "--zoom", 1f);
        // --times 0.1,0.3,...: the moments to draw, seconds, in place of six over msKr.
        float[]? times = Program.GetOption(args, "--times")?.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
        const float krUnitPx = 22f / 0.7063993f;

        foreach (string path in Directory.GetFiles(sheet, "???.json").Order())
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            JsonElement e = doc.RootElement;
            int n = e.GetProperty("index").GetInt32();

            if (ids != null && !ids.Contains(n) || e.GetProperty("scene").ValueKind != JsonValueKind.String)
            {
                continue;
            }

            // (effect, anchor, position px a unit as posPx means it, size px, duration)
            List<(IKrEffect Fx, Vector2 Anchor, float Pos, float Size, float Until)> running = [];

            if (parts)
            {
                int seed = 7919 * (n + 1);

                IReadOnlyList<KrEffectPart> list = effects.PartsForServerEffect(e.GetProperty("effect").GetInt32());

                foreach (KrEffectPart part in list.Count > 0 ? list : effects.Parts(0))
                {
                    if (part.Scale > 0f && effects.Create(part.Scene, seed++) is { } pfx)
                    {
                        // A script's units at the part's scale, as KrParticles
                        // spreads them (posPx: 31 * PositionScale a unit = 1x).
                        float pos = pfx is EmsEffect ? 31f * NifEffect.PositionScale * part.Scale / zoom : krUnitPx * part.Scale / zoom;
                        running.Add((pfx, feet - new Vector2(0, part.Height * 4f / zoom), pos, 2f * krUnitPx * part.Scale / zoom, part.Duration));
                    }
                }
            }
            else if (effects.Create(e.GetProperty("scene").GetString()!, 7919 * (n + 1)) is { } fx)
            {
                // --static: as KrParticles plays a static's scene.
                if (args.Contains("--static") && fx is NifEffect sn)
                {
                    sn.Loop = true;
                    sn.RestartWhenFinished = true;
                    sn.PlanesAsSprites = false;
                }
                else if (args.Contains("--static") && fx is EmsEffect script)
                {
                    script.Continuous = true;
                }

                // As KrParticles' LayerLift: how far above the feet the effect is anchored.
                float lift = e.GetProperty("layer").GetInt32() switch { 0 => 60f, 1 or 2 => 35f, 3 => 30f, _ => 0f } * liftK;
                running.Add((fx, feet - new Vector2(0, lift), posPx, sizePx, 0f));
            }

            if (running.Count == 0)
            {
                continue;
            }

            float msKr = e.GetProperty("msKr").GetSingle();
            float time = 0f;

            for (int k = 0; k < (times?.Length ?? 6); k++)
            {
                float at = times != null ? times[k] : k / 5f * msKr / 1000f;

                while (time < at)
                {
                    float dt = Math.Min(1f / 60f, at - time);

                    foreach (var r in running)
                    {
                        r.Fx.Update(dt);
                    }

                    time += dt;
                }

                uint[] canvas = bgDir != null && File.Exists(Path.Combine(bgDir, $"{n:D3}_bg.rgba"))
                    ? Rgba(File.ReadAllBytes(Path.Combine(bgDir, $"{n:D3}_bg.rgba")))
                    : new uint[Size * Size];

                Vector4[] px = canvas.Select(Unpack).ToArray();

                // As KrParticles: alpha first, then additive on top; multiply not drawn.
                foreach (KrBlend blend in (KrBlend[])[KrBlend.Alpha, KrBlend.Additive])
                {
                    foreach (var r in running)
                    {
                        if (r.Until > 0f && time >= r.Until)
                        {
                            continue;
                        }

                        foreach (EmsSprite s in args.Contains("--meshonly") ? [] : r.Fx.Sprites())
                        {
                            // --krview: placed as KrParticles.StaticScenePoint, the
                            // whole system at the tileart scale (KR draws
                            // a static's particles through world = scale x I).
                            float kv = Float(args, "--krview", 0f);
                            Vector2? spriteAt = kv > 0f ? r.Anchor + KrScreen(Vector3.Transform(s.Scene * kv, KrFacing), Float(args, "--krpx", 22f / 0.7063993f)) : null;
                            float spritePx = kv > 0f ? 2f * Float(args, "--krpx", 22f / 0.7063993f) * kv : r.Size;

                            if (s.Blend == blend)
                            {
                                Draw(px, s, r.Anchor, r.Pos, spritePx, Texture(effects, textures, s.Texture), spriteAt);
                            }

                            // As KrParticles: a glow map added, untinted, with the additive sprites.
                            if (blend == KrBlend.Additive && s.Glow != null)
                            {
                                Draw(px, s with { Color = new Vector4(1f, 1f, 1f, s.Color.W), Blend = KrBlend.Additive }, r.Anchor, r.Pos, spritePx,
                                    Texture(effects, textures, s.Glow), spriteAt);
                            }
                        }

                        // As KrParticles.DrawEffectMesh: the meshes after the alpha sprites.
                        // --krview S: as KrParticles.DrawMesh places a static's
                        // meshes - KR's facing and camera, at tileart scale S.
                        float krView = Float(args, "--krview", 0f);

                        if (blend == KrBlend.Alpha && r.Fx is NifEffect { HasMeshes: true } nif
                            && nif.RenderMeshes(t => Texture(effects, textures, t),
                                v => krView > 0f ? KrScreen(Vector3.Transform(v, KrFacing * Matrix4x4.CreateScale(krView)), Float(args, "--krpx", 22f / 0.7063993f)) : nif.ScreenUnits(v) * r.Pos,
                                out int mw, out int mh, out Vector2 origin) is { } mesh)
                        {
                            for (int y = 0; y < mh; y++)
                            {
                                for (int x = 0; x < mw; x++)
                                {
                                    int cx = (int)(r.Anchor.X + origin.X) + x, cy = (int)(r.Anchor.Y + origin.Y) + y;

                                    if (cx < 0 || cy < 0 || cx >= Size || cy >= Size)
                                    {
                                        continue;
                                    }

                                    // Premultiplied, as RenderMeshes makes it.
                                    Vector4 m = Unpack(mesh[(y * mw) + x]);
                                    ref Vector4 d = ref px[(cy * Size) + cx];
                                    d = new Vector4(Vector3.Min(Vector3.One, new Vector3(m.X, m.Y, m.Z) + (new Vector3(d.X, d.Y, d.Z) * (1f - m.W))), 1f);
                                }
                            }
                        }
                    }
                }

                PngWriter.Write(Path.Combine(outDir, $"{n:D3}_sim_{k}.png"), Size, Size, px.Select(Pack).ToArray());
            }
        }

        return 0;
    }

    private static void Draw(Vector4[] px, in EmsSprite s, Vector2 anchor, float posPx, float sizePx, TgaImage? tex, Vector2? at = null)
    {
        if (tex == null || s.Size <= 0f || s.Color.W <= 0f)
        {
            return;
        }

        // NifEffect's positions are tile units from Facing: (x - y) and (x + y)
        // at 22 px, which is the scene's screen axes at 31 px a unit; posPx
        // stands in for 31 * PositionScale.
        float scale = posPx / (31f * NifEffect.PositionScale);
        Vector2 centre = at ?? anchor + scale * new Vector2(
            (s.Position.X - s.Position.Y) * 22f,
            ((s.Position.X + s.Position.Y) * 22f) - (s.Position.Z * 32f));
        float width = s.Size * sizePx;
        float half = width * 0.75f;
        float angle = s.RotationDegrees * MathF.PI / 180f;
        float cos = MathF.Cos(-angle), sin = MathF.Sin(-angle);
        float alpha = Math.Clamp(s.Color.W, 0f, 1f);

        int x0 = Math.Max(0, (int)(centre.X - half)), x1 = Math.Min(Size - 1, (int)(centre.X + half));
        int y0 = Math.Max(0, (int)(centre.Y - half)), y1 = Math.Min(Size - 1, (int)(centre.Y + half));

        for (int y = y0; y <= y1; y++)
        {
            for (int x = x0; x <= x1; x++)
            {
                float dx = x + 0.5f - centre.X, dy = y + 0.5f - centre.Y;
                float u = ((dx * cos) - (dy * sin)) / width + 0.5f;
                float v = ((dx * sin) + (dy * cos)) / width + 0.5f;

                if (u < 0f || u >= 1f || v < 0f || v >= 1f)
                {
                    continue;
                }

                Vector4 t = Unpack(tex.Pixels[((int)(v * tex.Height) * tex.Width) + (int)(u * tex.Width)]);
                Vector3 rgb = new Vector3(t.X, t.Y, t.Z) * new Vector3(s.Color.X, s.Color.Y, s.Color.Z);
                float a = t.W * alpha;
                ref Vector4 d = ref px[(y * Size) + x];

                d = s.Blend == KrBlend.Additive
                    ? new Vector4(Vector3.Min(Vector3.One, new Vector3(d.X, d.Y, d.Z) + (rgb * a)), 1f)
                    : new Vector4((rgb * a) + (new Vector3(d.X, d.Y, d.Z) * (1f - a)), 1f);
            }
        }
    }

    // As KrParticles.KrFacing and KrScreen.
    private static readonly Matrix4x4 KrFacing = new(
        0.7063992f, 2.869e-8f, 0.7078136f, 0,
        -0.0353759f, 0.9987504f, 0.0353052f, 0,
        -0.7069288f, -0.0499792f, 0.7055164f, 0,
        0, 0, 0, 1);

    private static Vector2 KrScreen(Vector3 p, float px)
    {
        float camX = (0.7063993f * p.X) + (0.7078135f * p.Z);
        float camY = (-0.7069289f * p.X) + (-0.0499792f * p.Y) + (0.7055165f * p.Z);
        return new Vector2(camX * px, -camY * px);
    }

    private static TgaImage? Texture(KrEffects effects, Dictionary<string, TgaImage?> cache, string name)
    {
        if (!cache.TryGetValue(name, out TgaImage? t))
        {
            cache[name] = t = effects.Texture(name);
        }

        return t;
    }

    private static uint[] Rgba(byte[] b)
    {
        uint[] p = new uint[b.Length / 4];

        for (int i = 0; i < p.Length; i++)
        {
            p[i] = b[i * 4] | ((uint)b[(i * 4) + 1] << 8) | ((uint)b[(i * 4) + 2] << 16) | 0xFF000000u;
        }

        return p;
    }

    private static Vector4 Unpack(uint p) => new((p & 0xFF) / 255f, ((p >> 8) & 0xFF) / 255f, ((p >> 16) & 0xFF) / 255f, (p >> 24) / 255f);

    private static uint Pack(Vector4 c) =>
        (uint)(c.X * 255f) | ((uint)(c.Y * 255f) << 8) | ((uint)(c.Z * 255f) << 16) | 0xFF000000u;

    private static float Float(string[] args, string name, float fallback) =>
        Program.GetOption(args, name) is { } v ? float.Parse(v, CultureInfo.InvariantCulture) : fallback;
}
