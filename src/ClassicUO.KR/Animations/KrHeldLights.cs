// SPDX-License-Identifier: BSD-2-Clause
// Our own left-hand torch, lantern and candle frames for KR's human.
//
// KR holds its torch in the right hand, and its frames cannot simply be moved
// to the left: KR cut the right hand and leg out of them, so the stick comes
// apart. These are placed frame by frame on KR's own body animation, where
// EC's left-hand torch and lantern sit on the same animation (EC's human
// animation is KR's, scaled), then nudged by hand per frame.
//
// No EA art ships in Resources/held-lights.bin: only our own
// torch (wood, wrap and flame we paint) and numbers. Everything else is built
// here from the user's KR install the first time it is drawn: KR's lantern
// frames, KR's candle (world art 0x142C, scaled down), the glow (from KR's
// lantern), and KR's body, which closes the fist over the light and hides it
// where the light is behind the body. The steps match bake_lights.py v2,
// which composed the same frames offline.
//
// All three exist for the armed walk (1), run (3), stance (7), stand (4) and
// riding: walk (23), run (24) and stand (25). Anything else falls back to
// KR's frames.

using System.Buffers.Binary;
using System.IO.Compression;
using ClassicUO.KR.Textures;

namespace ClassicUO.KR.Animations;

public static class KrHeldLights
{
    /// <summary>One frame: straight RGBA8888, anchor from the top-left.</summary>
    public sealed record Frame(int Width, int Height, int CenterX, int CenterY, uint[] Pixels);

    /// <summary>Anim ids with our frames: torch 501 lit / 504 unlit, lantern 500 / 503, candle 502 / 505.</summary>
    public static bool Covers(int anim) => anim is >= 500 and <= 505;

    /// <summary>Whether our frames for <paramref name="anim"/> include standing (4).</summary>
    public static bool HasStand(int anim) => Covers(anim);

    /// <summary>The actions our frames exist for.</summary>
    public static bool HasAction(int anim, int action) => action is 1 or 3 or 7 or 23 or 24 or 25 || (action == 4 && HasStand(anim));

    private const int KindTorch = 0, KindLantern = 1, KindCandle = 2;
    private const int Pad = 60;                    // room around the canvas for the glow
    private const int KrBody = 400, KrLantern = 500;
    private const int CandleArt = 0x142C;
    private const double CandleScale = 0.375;

    private sealed class Image
    {
        public int X, Y, W, H;
        public byte[] Rgba = [];
    }

    private sealed class Record
    {
        public int Kind, Action, Direction, FrameIndex, Frames, Dx, Dy;
        public bool Behind, Has;
        public float GripX, GripY, HeadX, HeadY;   // torch
        public Image? Lit, Unlit;                  // torch
        public int SrcAction, SrcFrame, ClipW, ClipH, X, Y;   // lantern (X, Y also the candle's)
        public float FistX, FistY, GlowX, GlowY;
    }

    private static readonly object Sync = new();
    private static Dictionary<(int Kind, int Action, int Direction), Record[]>? _records;
    private static int _w, _h, _ox, _oy;
    private static readonly Dictionary<(int Anim, int Action, int Direction), Frame[]?> _built = [];

    /// <summary>
    /// Our frames for an anim id, action and stored direction (0-4), built
    /// from <paramref name="store"/> (the user's KR install), or null.
    /// </summary>
    public static Frame[]? Get(KrAssetStore store, int anim, int action, int direction)
    {
        if (!Covers(anim) || !HasAction(anim, action))
        {
            return null;
        }

        lock (Sync)
        {
            if (_built.TryGetValue((anim, action, direction), out Frame[]? done))
            {
                return done;
            }

            Load();
            Frame[]? result = null;

            try
            {
                result = Build(store, anim, action, direction);
            }
            catch (Exception)
            {
                result = null;   // KR's own frames are drawn instead
            }

            _built[(anim, action, direction)] = result;

            return result;
        }
    }

    // ---- the data file ----

    private static void Load()
    {
        if (_records != null)
        {
            return;
        }

        Dictionary<(int, int, int), Record[]> result = [];
        using Stream? res = typeof(KrHeldLights).Assembly.GetManifestResourceStream("ClassicUO.KR.Resources.held-lights.bin");

        if (res != null)
        {
            using GZipStream gz = new(res, CompressionMode.Decompress);
            using MemoryStream ms = new();
            gz.CopyTo(ms);
            ReadOnlySpan<byte> s = ms.GetBuffer().AsSpan(0, (int)ms.Length);

            if (s.Length >= 16 && s[0] == 'K' && s[1] == 'R' && s[2] == 'H' && s[3] == 'L'
                && BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(4)) == 3)
            {
                _w = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(6));
                _h = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(8));
                _ox = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(10));
                _oy = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(12));
                int count = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(14));
                int o = 16;

                for (int i = 0; i < count; i++)
                {
                    Record r = new()
                    {
                        Kind = s[o], Action = s[o + 1], Direction = s[o + 2], FrameIndex = s[o + 3], Frames = s[o + 4],
                        Behind = (s[o + 5] & 1) != 0, Has = (s[o + 5] & 2) != 0,
                        Dx = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o + 6)),
                        Dy = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o + 8)),
                    };
                    o += 10;

                    if (r.Has)
                    {
                        switch (r.Kind)
                        {
                            case KindTorch:
                                r.GripX = F32(s, ref o); r.GripY = F32(s, ref o);
                                r.HeadX = F32(s, ref o); r.HeadY = F32(s, ref o);
                                r.Lit = ReadImage(s, ref o);
                                r.Unlit = ReadImage(s, ref o);
                                break;
                            case KindLantern:
                                r.SrcAction = s[o]; r.SrcFrame = s[o + 1];
                                r.ClipW = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o + 2));
                                r.ClipH = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o + 4));
                                r.X = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o + 6));
                                r.Y = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o + 8));
                                o += 10;
                                r.FistX = F32(s, ref o); r.FistY = F32(s, ref o);
                                r.GlowX = F32(s, ref o); r.GlowY = F32(s, ref o);
                                break;
                            default:
                                r.X = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o));
                                r.Y = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o + 2));
                                o += 4;
                                r.FistX = F32(s, ref o); r.FistY = F32(s, ref o);
                                r.GlowX = F32(s, ref o); r.GlowY = F32(s, ref o);
                                break;
                        }
                    }

                    if (!result.TryGetValue((r.Kind, r.Action, r.Direction), out Record[]? set))
                    {
                        result[(r.Kind, r.Action, r.Direction)] = set = new Record[r.Frames];
                    }

                    if (r.FrameIndex < set.Length)
                    {
                        set[r.FrameIndex] = r;
                    }
                }
            }
        }

        _records = result;
    }

    private static float F32(ReadOnlySpan<byte> s, ref int o)
    {
        float v = BinaryPrimitives.ReadSingleLittleEndian(s.Slice(o));
        o += 4;

        return v;
    }

    private static Image ReadImage(ReadOnlySpan<byte> s, ref int o)
    {
        Image im = new()
        {
            X = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o)),
            Y = BinaryPrimitives.ReadInt16LittleEndian(s.Slice(o + 2)),
            W = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o + 4)),
            H = BinaryPrimitives.ReadUInt16LittleEndian(s.Slice(o + 6)),
        };
        o += 8;
        int n = im.W * im.H * 4;
        im.Rgba = s.Slice(o, n).ToArray();
        o += n;

        return im;
    }

    // ---- building frames ----

    private static Frame[]? Build(KrAssetStore store, int anim, int action, int direction)
    {
        int kind = anim is 501 or 504 ? KindTorch : anim is 500 or 503 ? KindLantern : KindCandle;
        bool lit = anim <= 502;

        if (_records == null || !_records.TryGetValue((kind, action, direction), out Record[]? recs))
        {
            return null;
        }

        EnsureHalos(store);
        List<Bitmap> body = KrFrames(store, KrBody, action, direction);
        Frame[] frames = new Frame[recs.Length];

        for (int f = 0; f < recs.Length; f++)
        {
            Record? r = recs[f];

            if (r == null || !r.Has || f >= body.Count)
            {
                frames[f] = Empty();
                continue;
            }

            bool[] mask = BodyMask(body[f]);
            frames[f] = kind == KindTorch ? Torch(r, mask, lit) : Hung(store, r, mask, kind, lit, direction);
        }

        return frames;
    }

    private static Frame Empty() => new(1, 1, 0, 0, [0u]);

    private static Frame Torch(Record r, bool[] mask, bool lit)
    {
        Canvas t = new(_w, _h);
        Image im = (lit ? r.Lit : r.Unlit)!;
        t.Copy(im.Rgba, im.W, im.H, im.X, im.Y);

        // KR's fist closes over the stick.
        for (int y = 0; y < _h; y++)
        {
            for (int x = 0; x < _w; x++)
            {
                double ddx = x - r.GripX, ddy = y - r.GripY;

                if (ddx * ddx + ddy * ddy <= 9 && mask[y * _w + x])
                {
                    t.P[(y * _w + x) * 4 + 3] = 0;
                }
            }
        }

        Canvas torch = new(_w, _h);
        torch.Copy(t.P, _w, _h, r.Dx, r.Dy);

        if (r.Behind)
        {
            torch.HideWhere(mask, _w, _h, 0, 0);
        }

        Canvas c = new(_w, _h);

        if (lit)
        {
            Canvas g = new(_w, _h);
            Canvas halo = Halos.Torch;
            g.Over(halo.P, halo.W, halo.H, (int)Math.Round(r.HeadX - halo.W / 2.0), (int)Math.Round(r.HeadY - 4 - halo.H / 2.0));
            c.Over(g.P, _w, _h, r.Dx, r.Dy);
        }

        c.Over(torch.P, _w, _h, 0, 0);

        return c.ToFrame(_ox, _oy);
    }

    /// <summary>The lantern and candle: KR's art in the left fist, on a padded canvas.</summary>
    private static Frame Hung(KrAssetStore store, Record r, bool[] mask, int kind, bool lit, int direction)
    {
        Canvas item = new(_w, _h);
        double radius;

        if (kind == KindLantern)
        {
            List<Bitmap> lan = KrFrames(store, KrLantern, r.SrcAction, direction);

            if (r.SrcFrame >= lan.Count)
            {
                return Empty();
            }

            // Only the lantern itself: KR's glow (the soft pixels) is left out.
            Bitmap b = lan[r.SrcFrame];
            int w = Math.Min(b.W, r.ClipW), h = Math.Min(b.H, r.ClipH);
            byte[] solid = new byte[w * h * 4];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int si = (y * b.W + x) * 4, di = (y * w + x) * 4;

                    if (b.Rgba[si + 3] > 200)
                    {
                        Array.Copy(b.Rgba, si, solid, di, 4);
                    }
                }
            }

            item.Copy(solid, w, h, r.X, r.Y);
            radius = 3.0;
        }
        else
        {
            Canvas candle = Candle(store) ?? throw new InvalidOperationException("no KR candle art");
            item.Copy(candle.P, candle.W, candle.H, r.X, r.Y);
            radius = 2.2;
        }

        for (int y = 0; y < _h; y++)
        {
            for (int x = 0; x < _w; x++)
            {
                double ddx = x - r.FistX, ddy = y - r.FistY;

                if (ddx * ddx + ddy * ddy <= radius * radius && mask[y * _w + x])
                {
                    item.P[(y * _w + x) * 4 + 3] = 0;
                }
            }
        }

        int pw = _w + 2 * Pad, ph = _h + 2 * Pad;
        Canvas a = new(pw, ph);
        a.Copy(item.P, _w, _h, Pad + r.Dx, Pad + r.Dy);

        if (r.Behind)
        {
            a.HideWhere(mask, _w, _h, Pad, Pad);
        }

        Canvas c = new(pw, ph);

        if (lit)
        {
            Canvas halo = kind == KindLantern ? Halos.Lantern : Halos.Candle;
            c.Over(halo.P, halo.W, halo.H,
                (int)Math.Round(Pad + r.Dx + r.GlowX - halo.W / 2.0), (int)Math.Round(Pad + r.Dy + r.GlowY - halo.H / 2.0));
        }
        else if (kind == KindLantern)
        {
            // KR's unlit lantern (503) is about this much darker.
            for (int i = 0; i < a.P.Length; i += 4)
            {
                a.P[i] = (byte)(a.P[i] * 0.55);
                a.P[i + 1] = (byte)(a.P[i + 1] * 0.55);
                a.P[i + 2] = (byte)(a.P[i + 2] * 0.55);
            }
        }
        else
        {
            // No flame: the top 3 rows of the candle are its flame.
            int top = -1;

            for (int y = 0; y < ph && top < 0; y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    if (a.P[(y * pw + x) * 4 + 3] != 0)
                    {
                        top = y;
                        break;
                    }
                }
            }

            for (int y = Math.Max(0, top); top >= 0 && y < Math.Min(ph, top + 3); y++)
            {
                for (int x = 0; x < pw; x++)
                {
                    a.P[(y * pw + x) * 4 + 3] = 0;
                }
            }
        }

        c.Over(a.P, pw, ph, 0, 0);

        return c.ToFrame(_ox + Pad, _oy + Pad);
    }

    private static bool[] BodyMask(Bitmap b)
    {
        bool[] m = new bool[_w * _h];
        int x0 = _ox - b.Cx, y0 = _oy - b.Cy;

        for (int y = 0; y < b.H; y++)
        {
            int cy = y0 + y;

            if ((uint)cy >= (uint)_h)
            {
                continue;
            }

            for (int x = 0; x < b.W; x++)
            {
                int cx = x0 + x;

                if ((uint)cx < (uint)_w && b.Rgba[(y * b.W + x) * 4 + 3] > 128)
                {
                    m[cy * _w + cx] = true;
                }
            }
        }

        return m;
    }

    // ---- KR art ----

    private sealed class Bitmap
    {
        public int W, H, Cx, Cy;
        public byte[] Rgba = [];
    }

    private static readonly Dictionary<(int, int, int), List<Bitmap>> _kr = [];

    /// <summary>KR's frames for one direction, the empty ones left out.</summary>
    private static List<Bitmap> KrFrames(KrAssetStore store, int body, int action, int direction)
    {
        if (_kr.TryGetValue((body, action, direction), out List<Bitmap>? cached))
        {
            return cached;
        }

        List<Bitmap> list = [];
        AmouAnimation? anim = store.LoadAnimation(body, action);

        if (anim != null && anim.HasRegularDirections)
        {
            for (int f = 0; f < anim.FramesPerDirection; f++)
            {
                if (!anim.TryGetFrameInfo(direction, f, out AmouFrameInfo info) || info.IsEmpty)
                {
                    continue;
                }

                AmouFrameBitmap bmp = anim.Decode(info);
                list.Add(new Bitmap { W = bmp.Width, H = bmp.Height, Cx = bmp.CenterX, Cy = bmp.CenterY, Rgba = Bytes(bmp.Pixels) });
            }
        }

        _kr[(body, action, direction)] = list;

        return list;
    }

    private static byte[] Bytes(uint[] px)
    {
        byte[] b = new byte[px.Length * 4];

        for (int i = 0; i < px.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 4), px[i]);
        }

        return b;
    }

    private static Canvas? _candle;
    private static bool _candleTried;

    /// <summary>KR's candle (world art 0x142C), cropped to its pixels and scaled down.</summary>
    private static Canvas? Candle(KrAssetStore store)
    {
        if (_candleTried)
        {
            return _candle;
        }

        _candleTried = true;
        byte[]? data = store.GetPackage(KrTexturePackages.WorldArtPackage)?.Read(KrTexturePackages.WorldArtEntryName(CandleArt));

        if (data == null)
        {
            return null;
        }

        DdsImage dds = DdsImage.Decode(data);
        byte[] px = Bytes(dds.Pixels);
        int x0 = dds.Width, y0 = dds.Height, x1 = -1, y1 = -1;

        for (int y = 0; y < dds.Height; y++)
        {
            for (int x = 0; x < dds.Width; x++)
            {
                if (px[(y * dds.Width + x) * 4 + 3] != 0)
                {
                    x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                }
            }
        }

        if (x1 < 0)
        {
            return null;
        }

        int cw = x1 - x0 + 1, ch = y1 - y0 + 1;
        byte[] crop = new byte[cw * ch * 4];

        for (int y = 0; y < ch; y++)
        {
            Array.Copy(px, ((y + y0) * dds.Width + x0) * 4, crop, y * cw * 4, cw * 4);
        }

        int ow = Math.Max(1, (int)Math.Round(cw * CandleScale)), oh = Math.Max(1, (int)Math.Round(ch * CandleScale));
        _candle = Lanczos.Resize(crop, cw, ch, ow, oh);

        return _candle;
    }

    /// <summary>
    /// KR's glow for a held light, as a clean disc: the average colour of the
    /// soft pixels of KR's standing lantern (500, action 4, direction 0, first
    /// frame) by distance from its centre, drawn at a scale.
    /// </summary>
    private static class Halos
    {
        public static Canvas Lantern = null!, Candle = null!, Torch = null!;
    }

    /// <summary>Must run with the KR store before building: the glows come from KR's lantern.</summary>
    private static void EnsureHalos(KrAssetStore store)
    {
        if (Halos.Lantern != null)
        {
            return;
        }

        List<Bitmap> lan = KrFrames(store, KrLantern, 4, 0);

        if (lan.Count == 0)
        {
            throw new InvalidOperationException("no KR lantern");
        }

        Bitmap b = lan[0];
        double sx = 0, sy = 0;
        int n = 0;

        for (int y = 0; y < b.H; y++)
        {
            for (int x = 0; x < b.W; x++)
            {
                if (b.Rgba[(y * b.W + x) * 4 + 3] > 0)
                {
                    sx += x; sy += y; n++;
                }
            }
        }

        double cx = sx / n, cy = sy / n;
        Dictionary<int, double[]> acc = [];

        for (int y = 0; y < b.H; y++)
        {
            for (int x = 0; x < b.W; x++)
            {
                int i = (y * b.W + x) * 4;
                int al = b.Rgba[i + 3];

                if (al == 0 || al > 200)
                {
                    continue;
                }

                int r = (int)Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));

                if (!acc.TryGetValue(r, out double[]? s))
                {
                    acc[r] = s = new double[5];
                }

                for (int c = 0; c < 4; c++)
                {
                    s[c] += b.Rgba[i + c];
                }

                s[4]++;
            }
        }

        int R = acc.Keys.Max();
        double[]?[] prof = new double[R + 1][];

        for (int r = 0; r <= R; r++)
        {
            if (acc.TryGetValue(r, out double[]? s) && s[4] > 0)
            {
                prof[r] = [s[0] / s[4], s[1] / s[4], s[2] / s[4], s[3] / s[4]];
            }
        }

        double[] first = prof.First(p => p != null)!;

        for (int r = 0; r <= R; r++)
        {
            prof[r] ??= r < prof.Length / 2 ? first : [0, 0, 0, 0];
            first = prof[r]!;
        }

        Halos.Lantern = Disc(prof!, R, 1.0);
        Halos.Candle = Disc(prof!, R, 0.3);
        Canvas t = Disc(prof!, R, 0.65);

        for (int i = 0; i < t.P.Length; i += 4)
        {
            t.P[i] = (byte)Math.Min(255, (int)(t.P[i] * 1.15));
            t.P[i + 1] = (byte)(int)(t.P[i + 1] * 0.85);
            t.P[i + 2] = (byte)(int)(t.P[i + 2] * 0.55);
        }

        Halos.Torch = t;
    }

    private static Canvas Disc(double[][] prof, int R, double scale)
    {
        int size = (int)(2 * R * scale) + 1;
        Canvas c = new(size, size);
        double mid = size / 2.0;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int r = (int)(Math.Sqrt((x - mid) * (x - mid) + (y - mid) * (y - mid)) / scale);

                if (r > R)
                {
                    continue;
                }

                double[] q = prof[r];
                double al = q[3] / 255;
                int a = (int)q[3];
                int i = (y * size + x) * 4;

                for (int k = 0; k < 3; k++)
                {
                    // Premultiplied, then back to straight alpha, as the offline tool did.
                    int pm = (int)(q[k] * al);
                    c.P[i + k] = (byte)(a is 0 or 255 ? pm : Math.Min(255, 255 * pm / a));
                }

                c.P[i + 3] = (byte)a;
            }
        }

        return c;
    }

    // ---- a small RGBA canvas ----

    private sealed class Canvas(int w, int h)
    {
        public readonly int W = w, H = h;
        public readonly byte[] P = new byte[w * h * 4];

        /// <summary>Puts pixels at (ox, oy), replacing what is there, clipped to the canvas.</summary>
        public void Copy(byte[] src, int sw, int sh, int ox, int oy)
        {
            for (int y = 0; y < sh; y++)
            {
                int dy = oy + y;

                if ((uint)dy >= (uint)H)
                {
                    continue;
                }

                for (int x = 0; x < sw; x++)
                {
                    int dx = ox + x;

                    if ((uint)dx < (uint)W)
                    {
                        Array.Copy(src, (y * sw + x) * 4, P, (dy * W + dx) * 4, 4);
                    }
                }
            }
        }

        /// <summary>Straight-alpha "over", clipped to the canvas.</summary>
        public void Over(byte[] src, int sw, int sh, int ox, int oy)
        {
            for (int y = 0; y < sh; y++)
            {
                int dy = oy + y;

                if ((uint)dy >= (uint)H)
                {
                    continue;
                }

                for (int x = 0; x < sw; x++)
                {
                    int dx = ox + x;

                    if ((uint)dx >= (uint)W)
                    {
                        continue;
                    }

                    int si = (y * sw + x) * 4, di = (dy * W + dx) * 4;
                    int sa = src[si + 3];

                    if (sa == 0)
                    {
                        continue;
                    }

                    int da = P[di + 3];

                    if (da == 0 || sa == 255)
                    {
                        Array.Copy(src, si, P, di, 4);
                        continue;
                    }

                    double s = sa / 255.0, d = da / 255.0 * (1 - s), oa = s + d;

                    for (int k = 0; k < 3; k++)
                    {
                        P[di + k] = (byte)Math.Round((src[si + k] * s + P[di + k] * d) / oa);
                    }

                    P[di + 3] = (byte)Math.Round(oa * 255);
                }
            }
        }

        /// <summary>Clears alpha where KR's body is (its mask at (ox, oy)): the light is behind it.</summary>
        public void HideWhere(bool[] mask, int mw, int mh, int ox, int oy)
        {
            for (int y = 0; y < mh; y++)
            {
                for (int x = 0; x < mw; x++)
                {
                    if (mask[y * mw + x] && (uint)(x + ox) < (uint)W && (uint)(y + oy) < (uint)H)
                    {
                        P[((y + oy) * W + x + ox) * 4 + 3] = 0;
                    }
                }
            }
        }

        /// <summary>Cropped to its pixels; the anchor (ax, ay) from the canvas's top-left.</summary>
        public Frame ToFrame(int ax, int ay)
        {
            int x0 = W, y0 = H, x1 = -1, y1 = -1;

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    if (P[(y * W + x) * 4 + 3] != 0)
                    {
                        x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
                    }
                }
            }

            if (x1 < 0)
            {
                return new Frame(1, 1, 0, 0, [0u]);
            }

            int w = x1 - x0 + 1, h = y1 - y0 + 1;
            uint[] px = new uint[w * h];

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    px[y * w + x] = BinaryPrimitives.ReadUInt32LittleEndian(P.AsSpan(((y + y0) * W + x + x0) * 4));
                }
            }

            return new Frame(w, h, ax - x0, ay - y0, px);
        }
    }

    // ---- scaling the candle ----

    private static class Lanczos
    {
        /// <summary>Lanczos-3 resize in premultiplied alpha (as Pillow's LANCZOS on 'RGBa').</summary>
        public static Canvas Resize(byte[] src, int sw, int sh, int dw, int dh)
        {
            double[] pm = new double[sw * sh * 4];

            for (int i = 0; i < sw * sh; i++)
            {
                double a = src[i * 4 + 3];
                pm[i * 4] = Math.Round(src[i * 4] * a / 255);
                pm[i * 4 + 1] = Math.Round(src[i * 4 + 1] * a / 255);
                pm[i * 4 + 2] = Math.Round(src[i * 4 + 2] * a / 255);
                pm[i * 4 + 3] = a;
            }

            double[] hor = Pass(pm, sw, sh, dw, true);
            double[] ver = Pass(hor, dw, sh, dh, false);
            Canvas c = new(dw, dh);

            for (int i = 0; i < dw * dh; i++)
            {
                int a = Clamp(ver[i * 4 + 3]);

                for (int k = 0; k < 3; k++)
                {
                    int v = Clamp(ver[i * 4 + k]);
                    c.P[i * 4 + k] = (byte)(a is 0 or 255 ? v : Math.Min(255, 255 * v / a));
                }

                c.P[i * 4 + 3] = (byte)a;
            }

            return c;
        }

        private static int Clamp(double v) => (int)Math.Clamp(Math.Round(v), 0, 255);

        private static double Kernel(double x)
        {
            x = Math.Abs(x);

            if (x >= 3)
            {
                return 0;
            }

            return Sinc(x) * Sinc(x / 3);
        }

        private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

        private static double[] Pass(double[] src, int sw, int sh, int outLen, bool horizontal)
        {
            int inLen = horizontal ? sw : sh;
            int ow = horizontal ? outLen : sw, oh = horizontal ? sh : outLen;
            double[] dst = new double[ow * oh * 4];
            double scale = (double)inLen / outLen, fs = Math.Max(scale, 1), support = 3 * fs;

            for (int o = 0; o < outLen; o++)
            {
                double centre = (o + 0.5) * scale;
                int lo = Math.Max((int)(centre - support + 0.5), 0), hi = Math.Min((int)(centre + support + 0.5), inLen);
                double[] wts = new double[hi - lo];
                double sum = 0;

                for (int i = lo; i < hi; i++)
                {
                    sum += wts[i - lo] = Kernel((i - centre + 0.5) / fs);
                }

                for (int j = 0; j < (horizontal ? sh : sw); j++)
                {
                    for (int k = 0; k < 4; k++)
                    {
                        double v = 0;

                        for (int i = lo; i < hi; i++)
                        {
                            int si = horizontal ? (j * sw + i) : (i * sw + j);
                            v += src[si * 4 + k] * wts[i - lo];
                        }

                        int di = horizontal ? (j * ow + o) : (o * ow + j);
                        dst[di * 4 + k] = Math.Clamp(Math.Round(v / sum), 0, 255);
                    }
                }
            }

            return dst;
        }
    }
}
