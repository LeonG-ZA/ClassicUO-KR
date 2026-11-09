// SPDX-License-Identifier: BSD-2-Clause
// A server effect's particles frame by frame, as KrParticles.SpawnParts
// plays its definition parts, in the units KR's own quads use:
// screen position in KR world units from the feet (x right, y up) and quad
// edge in world units - to set against KR's own frames.

using System.Globalization;
using System.Text;
using ClassicUO.KR.Effects;

namespace ClassicUO.KR.Tool;

internal static class EffectDump
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: effect-dump <uokrPath> <serverEffectId> <out.json> [--frames 150] [--fps 30]");

            return 1;
        }

        using KrAssetStore store = new(args[0]);
        KrEffects effects = new(store);
        int id = int.Parse(args[1], CultureInfo.InvariantCulture);
        int frames = int.Parse(Program.GetOption(args, "--frames") ?? "150", CultureInfo.InvariantCulture);
        float fps = float.Parse(Program.GetOption(args, "--fps") ?? "30", CultureInfo.InvariantCulture);
        const float krUnitPx = 22f / 0.7063993f;

        List<(NifEffect Fx, KrEffectPart Part)> running = [];
        int seed = 7919;

        foreach (KrEffectPart part in effects.PartsForServerEffect(id))
        {
            if (part.Scale > 0f && part.Scene.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) && effects.Create(part.Scene, seed++) is NifEffect nif)
            {
                running.Add((nif, part));
            }
        }

        StringBuilder json = new("[");

        for (int f = 0; f < frames; f++)
        {
            float time = f / fps;
            json.Append(f == 0 ? "[" : ",[");
            bool first = true;

            foreach ((NifEffect fx, KrEffectPart part) in running)
            {
                if (part.Duration > 0f && time >= part.Duration)
                {
                    continue;
                }

                StringBuilder draw = new();

                foreach (EmsSprite s in fx.Sprites())
                {
                    System.Numerics.Vector2 screen = fx.ScreenUnits(s.Scene) * part.Scale;
                    float x = screen.X, y = -screen.Y + (part.Height * 4f / krUnitPx);
                    draw.Append(draw.Length == 0 ? "" : ",").Append(FormattableString.Invariant(
                        $"[{x:0.###},{y:0.###},{2f * s.Size * part.Scale:0.###},{(int)(Math.Clamp(s.Color.W, 0f, 1f) * 255)}]"));
                }

                if (draw.Length > 0)
                {
                    json.Append(first ? "" : ",").Append(FormattableString.Invariant($"{{\"ws\":{part.Scale:0.###},\"p\":[")).Append(draw).Append("]}");
                    first = false;
                }
            }

            json.Append(']');

            foreach ((NifEffect fx, _) in running)
            {
                fx.Update(1f / fps);
            }
        }

        File.WriteAllText(args[2], json.Append(']').ToString());

        return 0;
    }
}
