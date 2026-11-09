// held-lights <uokrPath> <outDir>: builds the client's held-light frames from
// the embedded data and a KR install, as the client does, and writes each
// frame with its anchor, so they can be checked against the offline tools.

using ClassicUO.KR;
using ClassicUO.KR.Animations;
using ClassicUO.KR.Util;

internal static class HeldLightsCheck
{
    public static int Run(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("usage: held-lights <uokrPath> [outDir]");

            return 1;
        }

        using KrAssetStore store = new(args[0]);
        string outDir = args.Length > 1 ? args[1] : "held-lights-check";
        Directory.CreateDirectory(outDir);
        int n = 0;
        using StreamWriter anchors = new(Path.Combine(outDir, "anchors.txt"));

        foreach (int anim in new[] { 500, 501, 502, 503, 504, 505 })
        {
            foreach (int action in new[] { 1, 3, 4, 7, 23, 24, 25 })
            {
                if (!KrHeldLights.HasAction(anim, action))
                {
                    continue;
                }

                for (int d = 0; d < 5; d++)
                {
                    KrHeldLights.Frame[]? set = KrHeldLights.Get(store, anim, action, d);

                    if (set == null)
                    {
                        Console.WriteLine($"missing {anim} a{action} d{d}");
                        continue;
                    }

                    for (int f = 0; f < set.Length; f++)
                    {
                        KrHeldLights.Frame fr = set[f];
                        string name = $"{anim}_a{action}_d{d}_f{f}";
                        PngWriter.Write(Path.Combine(outDir, name + ".png"), fr.Width, fr.Height, fr.Pixels);
                        anchors.WriteLine($"{name} {fr.Width} {fr.Height} {fr.CenterX} {fr.CenterY}");
                        n++;
                    }
                }
            }
        }

        Console.WriteLine($"{n} frames written to {outDir}");

        return 0;
    }
}
