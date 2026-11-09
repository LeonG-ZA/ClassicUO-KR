// Writes what the flame tuner shows: every static with one of our flames, drawn
// as the client draws it, and a short animated strip of each flame.
//
//   FlameExport <ecPath> <uoPath> <flamesDir> <outDir>
//
// flamesDir is src/ClassicUO.KR/Resources/flames. outDir holds images made
// from the user's own install, so keep it out of the repository.

using System.Globalization;
using System.Text;
using System.Text.Json;
using ClassicUO.Assets;
using ClassicUO.KR.Effects;
using ClassicUO.KR.Uop;
using ClassicUO.KR.Util;
using ClassicUO.Utility;

CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: FlameExport <ecPath> <uoPath> <flamesDir> <outDir>");

    return 1;
}

string ec = args[0], uo = args[1], flamesDir = args[2], outDir = args[3];
Directory.CreateDirectory(Path.Combine(outDir, "art"));
Directory.CreateDirectory(Path.Combine(outDir, "strips"));

ClientVersionHelper.IsClientVersionValid("7.0.107.76", out ClientVersion version);
UOFileManager files = new(version, uo);
files.Arts.Load();

byte[] tiledata = File.ReadAllBytes(Path.Combine(uo, "tiledata.mul"));

string Name(int id)
{
    int off = (512 * 964) + (id / 32 * 1316) + 4 + (id % 32 * 41) + 21;
    int end = off;

    while (end < off + 20 && tiledata[end] != 0)
    {
        end++;
    }

    return Encoding.ASCII.GetString(tiledata, off, end - off);
}

KrStaticProvider.NativeDetail = true;

if (!KrStaticProvider.Initialize("", 2.0 / 3.0, files.Arts, ec))
{
    Console.Error.WriteLine("Could not read the Enhanced Client install.");

    return 1;
}

KrStaticProvider provider = KrStaticProvider.Instance;
using UopFile effects = UopFile.Open(Path.Combine(ec, "EffectTexture.uop"));

// --- strips -----------------------------------------------------------------
const int StripW = 90, StripH = 150, AnchorX = 45, AnchorY = 125, Frames = 16;
Dictionary<string, TgaImage> textures = [];
HashSet<string> written = [];

TgaImage Texture(string name)
{
    if (!textures.TryGetValue(name, out TgaImage? t))
    {
        t = TgaImage.TryDecode(effects.Read("data/effects/" + name)!)!;
        textures[name] = t;
    }

    return t;
}

string Strip(string scene, float scale)
{
    string id = string.Create(CultureInfo.InvariantCulture, $"{Path.GetFileNameWithoutExtension(scene)}_{(int)MathF.Round(scale * 100)}.png");

    if (!written.Add(id))
    {
        return id;
    }

    EmsEffect effect = new(EmsScript.Parse(File.ReadAllText(Path.Combine(flamesDir, scene))), 11) { Continuous = true };

    for (int i = 0; i < 90; i++)
    {
        effect.Update(1f / 30f);
    }

    float sizeScale = 22f / 0.7063993f / 20f * scale;
    uint[] sheet = new uint[StripW * Frames * StripH];

    for (int f = 0; f < Frames; f++)
    {
        for (int i = 0; i < 2; i++)
        {
            effect.Update(1f / 30f);
        }

        float[] sum = new float[StripW * StripH * 3];

        foreach (EmsSprite s in effect.Sprites())
        {
            TgaImage t = Texture(s.Texture);
            float ax = AnchorX + (scale * s.Position.X * 22f);
            float ay = AnchorY + (scale * ((s.Position.X * 22f) + (s.Position.Y * 32f) - (s.Position.Z * 32f)));
            bool axial = s.Length > 0f;
            float qw = axial ? s.Size * 22f : s.Size * 20f * sizeScale;
            float qh = axial ? s.Length * 16f : qw;

            for (int y = 0; y < StripH; y++)
            {
                for (int x = 0; x < StripW; x++)
                {
                    float u, v;

                    if (axial)
                    {
                        float up = (ay - (y + 0.5f)) / qh;
                        v = ((x + 0.5f - ax) / qw) + 0.5f;
                        u = 1f - up;

                        if (up < 0f || up >= 1f || v < 0f || v >= 1f)
                        {
                            continue;
                        }
                    }
                    else
                    {
                        u = ((x + 0.5f - ax) / qw) + 0.5f;
                        v = ((y + 0.5f - ay) / qh) + 0.5f;

                        if (u < 0f || u >= 1f || v < 0f || v >= 1f)
                        {
                            continue;
                        }
                    }

                    uint p = t.Pixels[((int)(Math.Clamp(v, 0f, 0.999f) * t.Height) * t.Width) + (int)(Math.Clamp(u, 0f, 0.999f) * t.Width)];
                    int at = ((y * StripW) + x) * 3;
                    sum[at] += (p & 255) * s.Color.X;
                    sum[at + 1] += ((p >> 8) & 255) * s.Color.Y;
                    sum[at + 2] += ((p >> 16) & 255) * s.Color.Z;
                }
            }
        }

        for (int y = 0; y < StripH; y++)
        {
            for (int x = 0; x < StripW; x++)
            {
                int at = ((y * StripW) + x) * 3;
                uint r = (uint)Math.Min(255f, sum[at]), g = (uint)Math.Min(255f, sum[at + 1]), b = (uint)Math.Min(255f, sum[at + 2]);
                sheet[(y * StripW * Frames) + (f * StripW) + x] = 0xFF000000u | (b << 16) | (g << 8) | r;
            }
        }
    }

    PngWriter.Write(Path.Combine(outDir, "strips", id), StripW * Frames, StripH, sheet);

    return id;
}

// --- objects ----------------------------------------------------------------
// Where a script's fire starts, so changing its size can keep the base still.
(float X, float Z) FlameBase(string scene)
{
    EmsExpr? at = EmsScript.Parse(File.ReadAllText(Path.Combine(flamesDir, scene))).Sequences[0].Get("chaoslocation");
    Random rng = new(1);
    float x = 0f, z = 0f;

    for (int i = 0; i < 400 && at is not null; i++)
    {
        System.Numerics.Vector3 v = at.Vector(rng);
        x += v.X / 400f;
        z += v.Z / 400f;
    }

    return (MathF.Round(x, 3), MathF.Round(z, 3));
}

static bool OurFlame(string scene) =>
    scene.StartsWith("torchflame", StringComparison.OrdinalIgnoreCase)
    || scene.StartsWith("candleflame", StringComparison.OrdinalIgnoreCase);

List<Dictionary<string, object>> objects = [];
int group = 0;
string previousKey = "";
int previousGraphic = -10;

for (int g = 0; g < 0x4000; g++)
{
    provider.SceneFor((ushort)g, out _);
    var flames = provider.ScenesFor((ushort)g).Where(r => OurFlame(r.Scene)).ToList();

    if (flames.Count == 0)
    {
        continue;
    }

    ArtInfo art = files.Arts.GetArt((uint)(g + 0x4000));

    if (art.Width == 0)
    {
        Console.WriteLine($"0x{g:X4}: no art");

        continue;
    }

    // Statics with no EC world art are drawn with their 2D art.
    if (!provider.TryGetSprite((ushort)g, art.Width, art.Height, false, false, 0, 0,
            out uint[] pixels, out int w, out int h, out int dw, out int dh))
    {
        pixels = art.Pixels.ToArray();
        w = dw = art.Width;
        h = dh = art.Height;
    }

    string file = $"{g:X4}.png";
    PngWriter.Write(Path.Combine(outDir, "art", file), w, h, pixels);

    string name = Name(g);
    string key = name + "|" + string.Join(",", flames.Select(f => f.Scene));

    if (key != previousKey || g != previousGraphic + 1)
    {
        group++;
    }

    previousKey = key;
    previousGraphic = g;

    objects.Add(new Dictionary<string, object>
    {
        ["graphic"] = g,
        ["name"] = name,
        ["art"] = "art/" + file,
        ["w"] = dw,
        ["h"] = dh,
        ["group"] = group,
        ["flames"] = flames.Select(f => new Dictionary<string, object>
        {
            ["scene"] = f.Scene,
            ["x"] = MathF.Round(f.X, 3),
            ["y"] = MathF.Round(f.Y, 3),
            ["height"] = MathF.Round(f.Height, 3),
            ["scale"] = f.Scale,
            ["size"] = f.Size,
            ["baseX"] = FlameBase(f.Scene).X,
            ["baseZ"] = FlameBase(f.Scene).Z,
            ["strip"] = "strips/" + Strip(f.Scene, f.Scale),
        }).ToList(),
    });
}

Dictionary<string, object> output = new()
{
    ["strip"] = new Dictionary<string, int> { ["frames"] = Frames, ["w"] = StripW, ["h"] = StripH, ["ax"] = AnchorX, ["ay"] = AnchorY },
    ["objects"] = objects,
};

File.WriteAllText(Path.Combine(outDir, "flames.json"), JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{objects.Count} objects, {objects.Sum(o => ((System.Collections.ICollection)o["flames"]).Count)} flames, {written.Count} strips, {group} groups");

return 0;
