// SPDX-License-Identifier: BSD-2-Clause
//
// Kingdom Reborn art for classic static graphics.
//
// The lookup needs no decoding at all. Texture.uop's world art splits into
// three id blocks, and the 0000xxxx block is
// keyed directly by classic graphic id: 8,746 ids in the range 2..16382, of
// which 8,697 have a matching static in art.mul. ClassicUO knows a static's
// graphic when it draws it, so build/worldart/{graphic:D8}.dds is reachable the
// same way the land tiles were - and tileart.uop, whose records are version 3
// where ClassicUO's own TileArtInfo reads version 4 only, does not come into
// it.
//
// What did need working out was geometry, because the textures are padded to
// powers of two. Two measurements settled it, over the 8,686 statics that KR
// and the 2D client both have:
//
//   * The sprite is anchored at the TOP LEFT of the padded texture. The alpha
//     bounding box starts at (0, 0) for 87.7 % of them, and within two pixels
//     of it for most of the rest, so the bounds come from the alpha channel
//     and nothing else is needed to find them.
//
//   * There is no single KR-to-2D scale. The content box runs to a median of
//     1.11x the 2D sprite's width and 1.38x its height, and only 26 % and 42 %
//     respectively are within a tenth of 1.5x, so KR's art is redrawn rather
//     than rescaled and its footprint cannot be derived from the 2D art.
//
// So the sprite is cropped to its own alpha and drawn with ClassicUO's own
// bottom-centre anchor, which works from whatever width and height it is
// given. The default scale is the 2/3 the animation work already settled on
// for KR mobiles (see kr_frame_scale); the 1.5 that is its reciprocal is also
// the constant every tileart record carries at +0x0F. That is consistent, not
// proof - see the hypothesis note in section 14.

using System;
using System.Collections.Generic;
using System.IO;
using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;

namespace ClassicUO.KR.World;

public sealed class KrStaticArt
{
    /// <summary>The scale KR art is drawn at relative to the 2D tile grid.</summary>
    public const double DefaultScale = 2.0 / 3.0;

    /// <summary>Alpha at or below this counts as empty when finding the bounds.</summary>
    private const int AlphaFloor = 8;

    private readonly KrAssetStore _store;
    private readonly Dictionary<(int Graphic, int Companion, int Width, int Height), KrStaticSprite?>
        _sprites = [];

    private readonly Dictionary<(int, int, int, int, int, int, int), KrStaticSprite?> _boxed = [];

    private readonly Dictionary<int, bool> _opaque = [];

    public KrStaticArt(KrAssetStore store, double scale = DefaultScale)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        Scale = scale;
    }

    /// <summary>How much KR art is shrunk to sit on the 2D tile grid.</summary>
    public double Scale { get; }

    /// <summary>
    /// How many sprite pixels per display pixel to keep. 1 resamples KR art
    /// down to its 2D size, as before; 1 / <see cref="Scale"/> (1.5) keeps its
    /// native resolution - the layout is the same, the GPU does the downscale,
    /// and zooming in shows KR's real detail. Never more than the art has.
    /// </summary>
    public double Detail { get; set; } = 1.0;

    /// <summary>How many sprites have been decoded and are being held.</summary>
    public int CachedCount => _sprites.Count;

    /// <summary>
    /// The KR sprite for a classic static graphic, or null when KR has no art
    /// for it or the texture is entirely transparent.
    /// </summary>
    public KrStaticSprite? Sprite(int graphic) => Sprite(graphic, 0, 0);

    /// <summary>
    /// The sprite for a graphic with KR's companion static composited into it.
    /// </summary>
    /// <remarks>
    /// KR builds a tree from two statics on one tile, a trunk and a crown of
    /// leaves, and the 2D map holds only one of them (section 16). Merging the
    /// companion into the sprite rather than drawing a second static keeps the
    /// chunk mesh's two passes counting and writing exactly one sprite per
    /// object, so sorting, hueing and picking are untouched.
    ///
    /// The two are aligned the way they would be drawn: both anchored bottom
    /// centre on the same tile.
    /// </remarks>
    public KrStaticSprite? SpriteWith(int graphic, int companion, int fitWidth, int fitHeight) =>
        SpriteCore(graphic, companion, fitWidth, fitHeight);

    /// <summary>
    /// The KR sprite for a classic static graphic, fitted to a footprint.
    /// </summary>
    /// <remarks>
    /// When <paramref name="fitWidth"/> and <paramref name="fitHeight"/> are
    /// given, the sprite is resampled to exactly that size instead of being
    /// scaled by <see cref="Scale"/>. **Anything that tiles needs this.** A
    /// roof or a floor is built from sprites that have to meet their
    /// neighbours exactly, and ClassicUO anchors each one from its own width
    /// and height, so a sprite even a pixel off its 2D footprint staggers
    /// against the next one. KR's art is a median 1.11x the 2D width and 1.38x
    /// the height, which in game showed up as roof tiles sliding into the
    /// building beneath them.
    ///
    /// Fitting squashes a little - the aspect is not preserved - but the world
    /// is ClassicUO's 2D grid and a static's footprint on it is what the rest
    /// of the scene is composed against.
    /// </remarks>
    public KrStaticSprite? Sprite(int graphic, int fitWidth, int fitHeight) =>
        SpriteWith(graphic, -1, fitWidth, fitHeight);

    private KrStaticSprite? SpriteCore(int graphic, int companion, int fitWidth, int fitHeight)
    {
        (int, int, int, int) key = (graphic, companion, fitWidth, fitHeight);

        if (_sprites.TryGetValue(key, out KrStaticSprite? cached))
        {
            return cached;
        }

        KrStaticSprite? sprite = Build(graphic, companion, fitWidth, fitHeight);

        _sprites[key] = sprite;

        return sprite;
    }

    /// <summary>
    /// The sprite for a graphic, scaled to sit exactly where the 2D sprite's
    /// own art sits inside its box.
    /// </summary>
    /// <remarks>
    /// Fitting to the 2D sprite's full width and height (section 14) is not
    /// enough on its own, because a 2D sprite often carries transparent margin
    /// around its art. KR's art is trimmed to its own bounds, so stretching
    /// that into the full box pushes the object down into the margin below it
    /// - in game, a fallen log sat buried in the ground.
    ///
    /// Giving the 2D art's own bounds within its box puts KR's art in exactly
    /// the same place, so the baseline holds and the anchor arithmetic, which
    /// still works from the full box, is unchanged.
    /// </remarks>
    public KrStaticSprite? SpriteInBox(
        int graphic,
        int companion,
        int boxWidth,
        int boxHeight,
        int contentX,
        int contentY,
        int contentWidth,
        int contentHeight)
    {
        if (boxWidth <= 0 || boxHeight <= 0 || contentWidth <= 0 || contentHeight <= 0)
        {
            return Sprite(graphic, boxWidth, boxHeight);
        }

        (int, int, int, int, int, int, int) key =
            (graphic, companion, boxWidth, boxHeight, contentX, contentY, contentWidth);

        if (_boxed.TryGetValue(key, out KrStaticSprite? cached))
        {
            return cached;
        }

        KrStaticSprite? content = SpriteWith(graphic, companion, contentWidth, contentHeight);

        KrStaticSprite? boxed = null;

        if (content is not null)
        {
            uint[] canvas = new uint[boxWidth * boxHeight];

            for (int y = 0; y < content.Height; y++)
            {
                int dy = contentY + y;

                if (dy < 0 || dy >= boxHeight)
                {
                    continue;
                }

                for (int x = 0; x < content.Width; x++)
                {
                    int dx = contentX + x;

                    if (dx < 0 || dx >= boxWidth)
                    {
                        continue;
                    }

                    canvas[(dy * boxWidth) + dx] = content.Pixels[(y * content.Width) + x];
                }
            }

            boxed = new KrStaticSprite(canvas, boxWidth, boxHeight);
        }

        _boxed[key] = boxed;

        return boxed;
    }

    /// <summary>
    /// The sprite at KR's own size, with empty space left below it so that it
    /// sits at the same height as the 2D art would.
    /// </summary>
    /// <remarks>
    /// For anything that does not tile - a log, a tree, a statue - squeezing
    /// KR's art into the 2D sprite's footprint throws away the very thing that
    /// makes it KR art. KR draws these larger and in more detail than the 2D
    /// sprite, and forcing a fit made a fallen log look like the 2D one again.
    ///
    /// The baseline still has to be right, though. ClassicUO's anchor puts the
    /// BOX's bottom at a fixed screen row, so art sitting flush with the bottom
    /// of its box lands lower than 2D art that has transparent margin beneath
    /// it. Carrying that margin over - as empty rows under KR's art - puts the
    /// object back at the height the 2D sprite would have drawn it.
    /// </remarks>
    public KrStaticSprite? SpriteAnchored(int graphic, int companion, int bottomMargin)
    {
        if (bottomMargin <= 0)
        {
            return SpriteWith(graphic, companion, 0, 0);
        }

        (int, int, int, int, int, int, int) key = (graphic, companion, -1, -1, 0, bottomMargin, 0);

        if (_boxed.TryGetValue(key, out KrStaticSprite? cached))
        {
            return cached;
        }

        KrStaticSprite? art = SpriteWith(graphic, companion, 0, 0);

        KrStaticSprite? anchored = null;

        if (art is not null)
        {
            int height = art.Height + bottomMargin;

            uint[] canvas = new uint[art.Width * height];

            art.Pixels.AsSpan().CopyTo(canvas);

            anchored = new KrStaticSprite(canvas, art.Width, height);
        }

        _boxed[key] = anchored;

        return anchored;
    }

    /// <summary>
    /// The sprite placed the way KR places it, on a canvas laid out so that
    /// ClassicUO's own anchor lands the art in the right spot.
    /// </summary>
    /// <remarks>
    /// KR anchors a static by two offsets in its tileart record (see
    /// <see cref="KrTileArt"/>): the texture's left edge relative to the
    /// tile's left corner, and the art's bottom relative to the tile's bottom
    /// corner, both in KR pixels. Scaled by <see cref="Scale"/> those match
    /// the 2D client to about 2 px over 2,867 graphics.
    ///
    /// ClassicUO cannot be given an offset directly: it draws a sprite of
    /// width W and height H with its box centred on the tile and its bottom on
    /// the tile's bottom corner. So the art is placed inside a canvas sized to
    /// make that true. The canvas is as wide as it needs to be either side of
    /// the tile's centre, and as tall as the art reaches above the tile's
    /// bottom. Art reaching below the bottom corner - a positive y offset -
    /// is clipped, and <see cref="ClippedBelow"/> counts how often.
    /// </remarks>
    public KrStaticSprite? SpriteOnTile(int graphic, KrTileArt tileArt) =>
        SpriteOnTile(graphic, tileArt, graphic, null);

    /// <param name="sourceGraphic">
    /// The graphic whose texture holds this static's region. Its own for most
    /// statics; the owning graphic for a slice of a large object.
    /// </param>
    /// <param name="centreX">
    /// Where to centre the region horizontally, in screen px from the tile's
    /// centre, overriding KR's x offset. Not used by the client: KR's own
    /// offset places every static correctly, slices included. Kept for tools.
    /// </param>
    public KrStaticSprite? SpriteOnTile(int graphic, KrTileArt tileArt, int sourceGraphic, double? centreX)
    {
        (int, int, int, int, int, int, int) key =
            (graphic, -2, sourceGraphic, centreX is null ? int.MinValue : (int)Math.Round(centreX.Value * 16), 0, 0, 0);

        if (_boxed.TryGetValue(key, out KrStaticSprite? cached))
        {
            return cached;
        }

        KrStaticSprite? placed = null;

        DdsImage? image = Texture(sourceGraphic);

        // The region, clamped to the texture. A region outside it - 15 of
        // 1,109 checked - leaves nothing to draw.
        int k0 = Math.Max(0, tileArt.RegionX);
        int l0 = Math.Max(0, tileArt.RegionY);
        int k1 = image is null ? -1 : Math.Min(image.Width - 1, tileArt.CornerX);
        int l1 = image is null ? -1 : Math.Min(image.Height - 1, tileArt.CornerY);

        if (image is not null && k1 >= k0 && l1 >= l0)
        {
            int kw = k1 - k0 + 1;
            int kh = l1 - l0 + 1;

            int cw = Math.Max(1, (int)Math.Round(kw * Scale));
            int ch = Math.Max(1, (int)Math.Round(kh * Scale));

            // Pixels per display pixel, never past the art's own.
            double d = Math.Clamp(Detail, 1.0, (double)kw / cw);
            int pcw = Math.Max(1, (int)Math.Round(cw * d));
            int pch = Math.Max(1, (int)Math.Round(ch * d));

            uint[] content = Crop(image, k0, l0, kw, kh);

            if (pcw != kw || pch != kh)
            {
                content = KrTerrainArt.Resample(content, kw, kh, pcw, pch);
            }

            // Region rectangle relative to the tile: x from the tile's centre,
            // y from the tile's bottom corner (negative is up).
            // +0x53 measures from the REGION's left edge, not the texture's.
            // The two readings agree for most statics, whose region starts at
            // the texture's left; they part company for the strips of a shared
            // texture, and only this one puts a statue's strips back together.
            double left = centreX is { } c
                ? c - (cw / 2.0)
                : (Scale * tileArt.OffsetX) - 22.0;
            double bottom = Scale * tileArt.OffsetY;
            double top = bottom - ch;

            int half = (int)Math.Ceiling(Math.Max(-left, left + cw));
            int width = Math.Max(2, half * 2);
            int height = Math.Max(1, (int)Math.Ceiling(-top));

            int dx = (int)Math.Round(left + half);
            int dy = (int)Math.Round(top + height);

            if (bottom > 0)
            {
                ClippedBelow++;
            }

            // The canvas in pixels, laid out in display units as before.
            int pw = Math.Max(2, (int)Math.Round(width * d));
            int ph = Math.Max(1, (int)Math.Round(height * d));
            int pdx = (int)Math.Round(dx * d);
            int pdy = (int)Math.Round(dy * d);
            uint[] canvas = new uint[pw * ph];

            for (int y = 0; y < pch; y++)
            {
                int ty = pdy + y;

                if (ty < 0 || ty >= ph)
                {
                    continue;
                }

                for (int x = 0; x < pcw; x++)
                {
                    int tx = pdx + x;

                    if (tx < 0 || tx >= pw)
                    {
                        continue;
                    }

                    canvas[(ty * pw) + tx] = content[(y * pcw) + x];
                }
            }

            placed = new KrStaticSprite(canvas, pw, ph, width, height);
        }

        _boxed[key] = placed;

        return placed;
    }

    /// <summary>
    /// A flat piece - carpet, floor - laid onto the tile's diamond the way KR
    /// lays it: its square texture mapped onto the floor in world space.
    /// </summary>
    /// <remarks>
    /// KR builds a carpet in layers, like terrain: a solid base (2750 blue
    /// felt, 2760 red), semi-transparent motif pieces whose records name that
    /// base as their source (2749 and 2758), and edge and corner pieces with
    /// fringe (2754 to 2757). Each is a square texture covering one tile.
    /// Squeezing that square into the 2D sprite's diamond-shaped box is what
    /// made a stepped edge along every border.
    ///
    /// So the diamond is sampled the way terrain is (KrTerrainArt.GroundDiamond):
    /// the tile's corners take the texture's corners, and the texture ends up
    /// turned 45 degrees on screen. The source is drawn first, then the piece's
    /// own art over it when that is a different texture.
    ///
    /// A texture spans one tile per 64 px (<see cref="FlatSpan"/>), sampled by
    /// the tile's world position, so a 256 px base runs on across 4 x 4 tiles
    /// the way terrain does.
    /// </remarks>
    public KrStaticSprite? FlatDiamond(int graphic, int sourceGraphic) =>
        FlatDiamond(graphic, sourceGraphic, 0, 0);

    /// <param name="tileX">World x of the tile, which picks the part of a
    /// multi-tile texture this tile shows.</param>
    /// <param name="tileY">World y of the tile.</param>
    public KrStaticSprite? FlatDiamond(int graphic, int sourceGraphic, int tileX, int tileY)
    {
        int vx = tileX & (MaxFlatSpan - 1);
        int vy = tileY & (MaxFlatSpan - 1);

        (int, int, int, int, int, int, int) key = (graphic, -3, sourceGraphic, vx, vy, 0, 0);

        if (_boxed.TryGetValue(key, out KrStaticSprite? cached))
        {
            return cached;
        }

        DdsImage? under = Texture(sourceGraphic);
        DdsImage? over = sourceGraphic != graphic ? Texture(graphic) : null;

        KrStaticSprite? flat = null;

        if (under is not null || over is not null)
        {
            const int size = 44;
            const float half = size / 2f;

            uint[] canvas = new uint[size * size];

            for (int py = 0; py < size; py++)
            {
                int count = py < size / 2 ? (py + 1) * 2 : (size - py) * 2;
                int start = (size - count) / 2;

                for (int px = start; px < start + count; px++)
                {
                    float u = (px - half + py) / size;
                    float v = (py - px + half) / size;

                    uint pixel = under is null ? 0u : SampleSpanned(under, u, v, vx, vy);

                    if (over is not null)
                    {
                        pixel = OverPixel(pixel, SampleSpanned(over, u, v, vx, vy));
                    }

                    canvas[(py * size) + px] = pixel;
                }
            }

            ExtendDiamond(canvas, size);
            flat = new KrStaticSprite(canvas, size, size);
        }

        _boxed[key] = flat;

        return flat;
    }

    /// <summary>
    /// Copies each row's end pixels of a <paramref name="size"/>-square tile
    /// diamond one pixel outward. KR art is drawn through a linear filter, so a
    /// diamond's edge pixels blended half with the transparent pixel outside
    /// them: where two floor pieces met, both edges went see-through and the
    /// ground showed as a dotted line (snow at 4515,1388). Neighbours now
    /// overlap by a pixel instead.
    /// </summary>
    public static void ExtendDiamond(uint[] pixels, int size)
    {
        for (int py = 0; py < size; py++)
        {
            int count = py < size / 2 ? (py + 1) * 2 : (size - py) * 2;
            int start = (size - count) / 2;
            int row = py * size;

            if (start > 0)
            {
                pixels[row + start - 1] = pixels[row + start];
            }

            if (start + count < size)
            {
                pixels[row + start + count] = pixels[row + start + count - 1];
            }
        }
    }

    /// <summary>
    /// Whether a graphic's KR texture covers its square: at least 90 % of its
    /// pixels fully opaque. A carpet's base is; its motif pieces are not.
    /// </summary>
    public bool IsOpaque(int graphic)
    {
        if (_opaque.TryGetValue(graphic, out bool cached))
        {
            return cached;
        }

        DdsImage? image = Texture(graphic);
        bool opaque = false;

        if (image is not null && image.Pixels.Length > 0)
        {
            int solid = 0;

            foreach (uint p in image.Pixels)
            {
                if ((p >> 24) >= 250)
                {
                    solid++;
                }
            }

            opaque = solid >= image.Pixels.Length * 0.9;
        }

        _opaque[graphic] = opaque;

        return opaque;
    }

    /// <summary>A graphic's KR texture's average colour over its solid pixels.</summary>
    public (double R, double G, double B)? TextureColour(int graphic) =>
        Texture(graphic) is { } image ? MeanColour(image.Pixels) : null;

    /// <summary>Average colour over pixels with alpha above 200, (A&lt;&lt;24)|(B&lt;&lt;16)|(G&lt;&lt;8)|R.</summary>
    public static (double R, double G, double B)? MeanColour(ReadOnlySpan<uint> pixels)
    {
        double r = 0, g = 0, b = 0;
        int n = 0;

        foreach (uint p in pixels)
        {
            if ((p >> 24) <= 200)
            {
                continue;
            }

            r += p & 0xFF;
            g += (p >> 8) & 0xFF;
            b += (p >> 16) & 0xFF;
            n++;
        }

        return n == 0 ? null : (r / n, g / n, b / n);
    }

    /// <summary>
    /// The most tiles a flat texture spans along one axis. A position within
    /// it is taken modulo this, so it must be a power of two.
    /// </summary>
    public const int MaxFlatSpan = 8;

    /// <summary>
    /// How many tiles a flat texture spans: one per 64 px. The carpet piece
    /// textures are 64 px and one tile each; a 256 px base spans 4 x 4.
    /// </summary>
    /// <remarks>
    /// Checked by rendering the ornate red carpet (2795-2797 with edges
    /// 2787-2794) from the map: at one texture per tile its medallions came
    /// out a quarter of the size they are in the 2D art and in KR; at 4 x 4
    /// tiles they match.
    /// </remarks>
    public static int FlatSpan(DdsImage image) =>
        Math.Clamp(image.Width / 64, 1, MaxFlatSpan) switch
        {
            // A 512 px floor (02000130_Cave_Floor_A) spans 8 tiles, as KR's
            // cave near Minoc shows; capped at 4 it repeated every 4.
            >= 8 => 8,
            >= 4 => 4,
            >= 2 => 2,
            _ => 1,
        };

    private static uint SampleSpanned(DdsImage image, float u, float v, int vx, int vy)
    {
        int span = FlatSpan(image);

        return SampleClamped(image, ((vx % span) + u) / span, ((vy % span) + v) / span);
    }

    private static uint SampleClamped(DdsImage image, float u, float v)
    {
        int x = Math.Clamp((int)(u * image.Width), 0, image.Width - 1);
        int y = Math.Clamp((int)(v * image.Height), 0, image.Height - 1);

        return image.Pixels[(y * image.Width) + x];
    }

    private static uint OverPixel(uint dst, uint src)
    {
        uint sa = src >> 24;

        if (sa == 0)
        {
            return dst;
        }

        if (sa == 255)
        {
            return src;
        }

        uint da = dst >> 24;
        uint inv = 255 - sa;

        uint r = (((src & 0xFF) * sa) + ((dst & 0xFF) * inv)) / 255;
        uint g = ((((src >> 8) & 0xFF) * sa) + (((dst >> 8) & 0xFF) * inv)) / 255;
        uint b = ((((src >> 16) & 0xFF) * sa) + (((dst >> 16) & 0xFF) * inv)) / 255;
        uint a = sa + ((da * inv) / 255);

        return (a << 24) | (b << 16) | (g << 8) | r;
    }

    /// <summary>How many sprites had art below the tile's bottom corner clipped.</summary>
    public int ClippedBelow { get; private set; }

    private KrStaticSprite? Build(int graphic, int companion, int fitWidth, int fitHeight)
    {
        if (graphic < 0)
        {
            return null;
        }

        if (!TryCropped(graphic, out uint[] cropped, out int width, out int height))
        {
            return null;
        }

        if (companion >= 0 && TryCropped(companion, out uint[] other, out int ow, out int oh))
        {
            Merge(ref cropped, ref width, ref height, other, ow, oh);
        }

        int targetWidth = fitWidth > 0
            ? fitWidth
            : Math.Max(1, (int)Math.Round(width * Scale));

        int targetHeight = fitHeight > 0
            ? fitHeight
            : Math.Max(1, (int)Math.Round(height * Scale));

        // Pixels per display pixel, never past the art's own.
        double d = Math.Clamp(Detail, 1.0, Math.Max(1.0, Math.Min((double)width / targetWidth, (double)height / targetHeight)));
        int pixelWidth = Math.Max(1, (int)Math.Round(targetWidth * d));
        int pixelHeight = Math.Max(1, (int)Math.Round(targetHeight * d));

        uint[] pixels = pixelWidth == width && pixelHeight == height
            ? cropped
            : KrTerrainArt.Resample(cropped, width, height, pixelWidth, pixelHeight);

        return new KrStaticSprite(pixels, pixelWidth, pixelHeight, targetWidth, targetHeight);
    }

    /// <summary>A world-art texture's width and height from its header, or null when there is none.</summary>
    public (int Width, int Height)? TextureSize(int graphic)
    {
        byte[]? raw = _store.GetPackage(KrTexturePackages.WorldArtPackage)?.Read(KrTexturePackages.WorldArtEntryName(graphic));

        if (raw is null || raw.Length < 20 || !DdsImage.IsDds(raw))
        {
            return null;
        }

        // DDS header: height at 12, width at 16.
        return (BitConverter.ToInt32(raw, 16), BitConverter.ToInt32(raw, 12));
    }

    /// <summary>The decoded world-art texture for a static graphic, cached.</summary>
    // Recently decoded textures. Building one static's sprite asks for its
    // texture several times (the provider's check, SpriteOnTile, flat pieces,
    // size and opacity tests), and each call read and decoded the DDS anew -
    // a large part of the stutter when new buildings came into view. Bounded:
    // the oldest is dropped past TextureCacheSize (~256 KB each).
    private const int TextureCacheSize = 64;
    private readonly Dictionary<int, DdsImage?> _textureCache = [];
    private readonly Queue<int> _textureOrder = new();

    public DdsImage? Texture(int graphic)
    {
        if (_textureCache.TryGetValue(graphic, out DdsImage? cached))
        {
            return cached;
        }

        DdsImage? image = DecodeTexture(graphic);

        if (_textureOrder.Count >= TextureCacheSize)
        {
            _textureCache.Remove(_textureOrder.Dequeue());
        }

        _textureCache[graphic] = image;
        _textureOrder.Enqueue(graphic);

        return image;
    }

    private DdsImage? DecodeTexture(int graphic)
    {
        string entry = KrTexturePackages.WorldArtEntryName(graphic);

        UopFile? package = _store.GetPackage(KrTexturePackages.WorldArtPackage);
        byte[]? raw = package?.Read(entry);

        if (raw is null || !DdsImage.IsDds(raw))
        {
            return null;
        }

        try
        {
            return DdsImage.Decode(raw);
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// The tight bounds of a texture's visible pixels. False when nothing in
    /// it is visible at all.
    /// </summary>
    public static bool TryGetBounds(DdsImage image, out int x0, out int y0, out int width, out int height) =>
        TryGetBounds(image.Pixels, image.Width, image.Height, out x0, out y0, out width, out height);

    /// <summary>
    /// The tight bounds of an image's visible pixels. False when nothing in it
    /// is visible at all.
    /// </summary>
    public static bool TryGetBounds(
        ReadOnlySpan<uint> pixels, int imageWidth, int imageHeight,
        out int x0, out int y0, out int width, out int height)
    {
        x0 = y0 = width = height = 0;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

        for (int y = 0; y < imageHeight; y++)
        {
            int row = y * imageWidth;

            for (int x = 0; x < imageWidth; x++)
            {
                if ((pixels[row + x] >> 24) <= AlphaFloor)
                {
                    continue;
                }

                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        }

        if (maxX < 0)
        {
            return false;
        }

        x0 = minX;
        y0 = minY;
        width = maxX - minX + 1;
        height = maxY - minY + 1;

        return true;
    }

    /// <summary>A graphic's art, trimmed to its own visible bounds.</summary>
    private bool TryCropped(int graphic, out uint[] pixels, out int width, out int height)
    {
        pixels = [];
        width = height = 0;

        DdsImage? image = Texture(graphic);

        if (image is null)
        {
            return false;
        }

        if (!TryGetBounds(image, out int x0, out int y0, width: out width, height: out height))
        {
            return false;
        }

        pixels = Crop(image, x0, y0, width, height);

        return true;
    }

    /// <summary>
    /// Lays one sprite over another the way the two would be drawn: both
    /// anchored bottom centre, on a canvas big enough for both.
    /// </summary>
    private static void Merge(
        ref uint[] basePixels, ref int baseWidth, ref int baseHeight,
        uint[] overlay, int overlayWidth, int overlayHeight)
    {
        int width = Math.Max(baseWidth, overlayWidth);
        int height = Math.Max(baseHeight, overlayHeight);

        uint[] canvas = new uint[width * height];

        Blit(canvas, width, height, basePixels, baseWidth, baseHeight);
        Blit(canvas, width, height, overlay, overlayWidth, overlayHeight);

        basePixels = canvas;
        baseWidth = width;
        baseHeight = height;
    }

    private static void Blit(
        uint[] canvas, int canvasWidth, int canvasHeight,
        uint[] src, int srcWidth, int srcHeight)
    {
        int x0 = (canvasWidth - srcWidth) / 2;
        int y0 = canvasHeight - srcHeight;

        for (int y = 0; y < srcHeight; y++)
        {
            int dst = ((y0 + y) * canvasWidth) + x0;
            int from = y * srcWidth;

            for (int x = 0; x < srcWidth; x++)
            {
                uint s = src[from + x];
                uint a = s >> 24;

                if (a == 0)
                {
                    continue;
                }

                if (a == 255)
                {
                    canvas[dst + x] = s;

                    continue;
                }

                uint d = canvas[dst + x];
                uint inv = 255 - a;

                uint r = (((s & 0xFF) * a) + ((d & 0xFF) * inv)) / 255;
                uint g = ((((s >> 8) & 0xFF) * a) + (((d >> 8) & 0xFF) * inv)) / 255;
                uint b = ((((s >> 16) & 0xFF) * a) + (((d >> 16) & 0xFF) * inv)) / 255;
                uint outA = Math.Max(d >> 24, a);

                canvas[dst + x] = (outA << 24) | (b << 16) | (g << 8) | r;
            }
        }
    }

    private static uint[] Crop(DdsImage image, int x0, int y0, int width, int height)
    {
        uint[] crop = new uint[width * height];

        for (int y = 0; y < height; y++)
        {
            int src = ((y0 + y) * image.Width) + x0;
            int dst = y * width;

            for (int x = 0; x < width; x++)
            {
                crop[dst + x] = image.Pixels[src + x];
            }
        }

        return crop;
    }

    public void Clear()
    {
        _textureCache.Clear();
        _textureOrder.Clear();
        _sprites.Clear();
        _boxed.Clear();
    }
}

/// <summary>One KR static sprite, cropped to its own art and scaled.</summary>
public sealed class KrStaticSprite
{
    public KrStaticSprite(uint[] pixels, int width, int height)
        : this(pixels, width, height, width, height)
    {
    }

    public KrStaticSprite(uint[] pixels, int width, int height, int displayWidth, int displayHeight)
    {
        Pixels = pixels;
        Width = width;
        Height = height;
        DisplayWidth = displayWidth;
        DisplayHeight = displayHeight;
    }

    public uint[] Pixels { get; }

    /// <summary>The pixels' size.</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>
    /// The size it is drawn at on the 2D grid, before zoom. Larger pixels
    /// than this (<see cref="KrStaticArt.Detail"/>) are KR's own detail, which
    /// the GPU scales down at draw time instead of the CPU throwing it away.
    /// </summary>
    public int DisplayWidth { get; }

    public int DisplayHeight { get; }
}
