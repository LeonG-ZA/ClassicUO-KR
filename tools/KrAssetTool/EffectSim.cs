// SPDX-License-Identifier: BSD-2-Clause
// Runs one KR effect headless and prints particle counts and extent over
// time - a check on the simulation before it is drawn in the client.

using System.Numerics;
using ClassicUO.KR.Effects;

namespace ClassicUO.KR.Tool;

internal static class EffectSim
{
    public static int Run(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: effect-sim <uokrPath> <serverEffectId | scene name>");

            return 1;
        }

        using KrAssetStore store = new(args[0]);
        KrEffects effects = new(store);
        string scene = int.TryParse(args[1], out int id) ? effects.SceneForServerEffect(id) ?? "" : args[1];
        Console.WriteLine($"scene: {scene}");

        // --fields: the emitter, controller and key blocks, to see why a
        // scene does or does not emit.
        if (args.Contains("--fields") && effects.Scene(scene) is { } nif)
        {
            for (int i = 0; i < nif.Blocks.Count; i++)
            {
                NifBlock b = nif.Blocks[i];

                if (b.Get<Vector3[]>("vertices") is { Length: > 0 } verts)
                {
                    Vector3 lo = verts[0], hi = verts[0];

                    foreach (Vector3 v in verts)
                    {
                        lo = Vector3.Min(lo, v);
                        hi = Vector3.Max(hi, v);
                    }

                    Vector2[] uvs = b.Get<Vector2[]>("uvs", []);
                    Vector2 ulo = uvs.Length > 0 ? uvs[0] : default, uhi = ulo;

                    foreach (Vector2 u in uvs)
                    {
                        ulo = Vector2.Min(ulo, u);
                        uhi = Vector2.Max(uhi, u);
                    }

                    Console.WriteLine($"[{i,3}] mesh: {verts.Length} verts {lo} .. {hi}, uv {ulo} .. {uhi}, "
                        + $"{b.Get<ushort[]>("triangles", []).Length / 3} triangles, colours {b.Get<Vector4[]>("colors", []).Length}");
                }

                if (b.Type is "NiNode" or "NiTriShape" or "NiTexturingProperty" or "NiAlphaProperty" or "NiTextureTransformController" or "NiSourceTexture")
                {
                    Console.WriteLine($"[{i,3}] {b.Type}: " + string.Join(", ", b.Fields.Select(kv => kv.Key + "=" + Show(kv.Value))));
                }

                if (b.Type.Contains("Ctlr") || b.Type.Contains("Interpolator") || b.Type.Contains("Data") || b.Type.Contains("Emitter"))
                {
                    Console.WriteLine($"[{i,3}] {b.Type}: " + string.Join(", ", b.Fields.Select(kv => kv.Key + "=" + Show(kv.Value))));
                }
            }
        }

        IKrEffect? fx = effects.Create(scene, 1);

        if (Program.GetOption(args, "--mesh-png") is { } outDir && fx is NifEffect mesh)
        {
            Directory.CreateDirectory(outDir);
            float px = float.Parse(Program.GetOption(args, "--px") ?? "11", System.Globalization.CultureInfo.InvariantCulture);
            (string Name, Func<Vector3, Vector2> Project)[] projections =
            [
                // Iso, z up: tiles from x and y at px per unit.
                ("iso", v => new Vector2((v.X - v.Y) * px * 0.5f, ((v.X + v.Y) * px * 0.25f) - (v.Z * px * 0.5f))),
                ("iso-flipy", v => new Vector2((v.X + v.Y) * px * 0.5f, ((v.X - v.Y) * px * 0.25f) - (v.Z * px * 0.5f))),
                // Front camera, as particle scenes are drawn.
                ("front", v => new Vector2(v.X * px, -v.Z * px)),
            ];

            for (int f = 0; f < 4; f++)
            {
                foreach ((string name, Func<Vector3, Vector2> project) in projections)
                {
                    if (mesh.RenderMeshes(effects.Texture, project, out int w, out int h, out Vector2 origin) is { } pixels)
                    {
                        WritePpm(Path.Combine(outDir, $"{name}_{f}.pam"), pixels, w, h);
                        Console.WriteLine($"{name} frame {f}: {w}x{h}, origin {origin}");
                    }
                }

                mesh.Update(0.25f);
            }

            return 0;
        }

        if (fx is null)
        {
            Console.WriteLine("cannot run it");

            if (store.GetPackage(KrEffects.ScenePackage)?.Read(KrEffects.SceneEntryName(scene)) is { } raw)
            {
                NifFile.TryParse(raw, out string? why);
                Console.WriteLine($"parse: {why}");
            }

            return 2;
        }

        for (int step = 1; step <= 40 && !fx.Finished; step++)
        {
            fx.Update(0.1f);
            List<EmsSprite> s = fx.Sprites().ToList();

            if (step % 2 == 0 || s.Count == 0)
            {
                Vector3 min = s.Count > 0 ? s.Aggregate(new Vector3(float.MaxValue), (m, p) => Vector3.Min(m, p.Position)) : default;
                Vector3 max = s.Count > 0 ? s.Aggregate(new Vector3(float.MinValue), (m, p) => Vector3.Max(m, p.Position)) : default;
                float size = s.Count > 0 ? s.Average(p => p.Size) : 0f;
                float alpha = s.Count > 0 ? s.Average(p => p.Color.W) : 0f;
                if (args.Contains("--by-texture"))
                {
                    foreach (var g in s.GroupBy(p => p.Texture))
                    {
                        Console.WriteLine($"    {g.Key}: {g.Count()} size {g.Average(p => p.Size):0.000} max {g.Max(p => p.Size):0.000} blend {g.First().Blend} spread {g.Max(p => p.Position.Length()):0.00} length {g.Average(p => p.Length):0.00}");
                    }
                }

                Console.WriteLine($"t={step / 10f:0.0}s  {s.Count,4} particles  x {min.X:0.00}..{max.X:0.00}  y {min.Y:0.00}..{max.Y:0.00}  z {min.Z:0.00}..{max.Z:0.00}  size {size:0.000}  alpha {alpha:0.00}  tex {(s.Count > 0 ? s[0].Texture : "")}");
            }
        }

        // Extent per texture at the end, to compare systems with planes.
        foreach (IGrouping<string, EmsSprite> g in fx.Sprites().GroupBy(p => p.Texture))
        {
            Vector3 lo = g.Aggregate(new Vector3(float.MaxValue), (m, p) => Vector3.Min(m, p.Position));
            Vector3 hi = g.Aggregate(new Vector3(float.MinValue), (m, p) => Vector3.Max(m, p.Position));
            Console.WriteLine($"  {g.Key,-32} {g.Count(),4}  x {lo.X:0.00}..{hi.X:0.00}  y {lo.Y:0.00}..{hi.Y:0.00}  z {lo.Z:0.00}..{hi.Z:0.00}");
        }

        // Planes (meshes drawn as sprites), if the scene has any.
        foreach (EmsSprite p in fx.Sprites().Where(p => p.Texture.Contains("Galaxy", StringComparison.OrdinalIgnoreCase)).Take(4))
        {
            Console.WriteLine($"plane {p.Texture} at ({p.Position.X:0.00}, {p.Position.Y:0.00}, {p.Position.Z:0.00}) size {p.Size:0.00} rot {p.RotationDegrees:0}");
        }

        Console.WriteLine(fx.Finished ? "finished" : "still running at 4 s");

        return 0;
    }

    private static string Show(object v) => v switch
    {
        NifKeys<int> k => "[" + string.Join(" ", k.Keys.Select(x => $"{x.Time:0.##}:{x.Value}")) + "]",
        NifKeys<float> k => "[" + string.Join(" ", k.Keys.Select(x => $"{x.Time:0.##}:{x.Value:0.##}")) + "]",
        float f => f.ToString("0.###"),
        _ => v?.ToString() ?? "null"
    };

    // PAM (RGBA) so Pillow can read it without a PNG encoder here.
    private static void WritePpm(string path, uint[] pixels, int w, int h)
    {
        using FileStream f = File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P7\nWIDTH {w}\nHEIGHT {h}\nDEPTH 4\nMAXVAL 255\nTUPLTYPE RGB_ALPHA\nENDHDR\n");
        f.Write(header);

        foreach (uint p in pixels)
        {
            f.WriteByte((byte)p);
            f.WriteByte((byte)(p >> 8));
            f.WriteByte((byte)(p >> 16));
            f.WriteByte((byte)(p >> 24));
        }
    }
}
