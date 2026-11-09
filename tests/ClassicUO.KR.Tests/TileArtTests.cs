// SPDX-License-Identifier: BSD-2-Clause
//
// The fixed block of a tileart.uop record, and placing a static by it.
//

using System.Buffers.Binary;
using ClassicUO.KR;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class TileArtTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrFacetFile.PackageName(0))));
    }

    private static byte[] Record(int graphic, int cx, int cy, int ox, int oy, int w2, int h2)
    {
        byte[] b = new byte[0x80];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 3);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x06), graphic);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x4B), cx);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x4F), cy);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x53), ox);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x57), oy);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x63), w2);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x67), h2);
        return b;
    }

    [Fact]
    public void ReadsTheFixedBlock()
    {
        KrTileArt? t = KrTileArt.Parse(Record(3278, 210, 271, -79, -37, 122, 178));

        Assert.NotNull(t);
        Assert.Equal(new KrTileArt(3278, 0, 0, 210, 271, -79, -37, 122, 178), t);
    }

    [Fact]
    public void ReadsAnEnhancedRecordTenBytesLater()
    {
        // Version 4 (the Enhanced Client): every field after the graphic id
        // sits 10 bytes later.
        byte[] b = new byte[0x80];
        BinaryPrimitives.WriteUInt16LittleEndian(b, 4);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x06), 3278);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x4B + 10), 210);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x4F + 10), 271);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x53 + 10), -79);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x57 + 10), -37);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x63 + 10), 122);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0x67 + 10), 178);

        Assert.Equal(new KrTileArt(3278, 0, 0, 210, 271, -79, -37, 122, 178), KrTileArt.Parse(b));
        Assert.Null(KrTileArt.Parse(b.AsSpan(0, 0x70)));
    }

    [Fact]
    public void RejectsAShortRecordOrAnotherVersion()
    {
        Assert.Null(KrTileArt.Parse(new byte[0x40]));

        byte[] v5 = Record(1, 0, 0, 0, 0, 44, 44);
        BinaryPrimitives.WriteUInt16LittleEndian(v5, 5);
        Assert.Null(KrTileArt.Parse(v5));
    }

    [Fact]
    public void RealRecordsCarryTheTwoDimensionalSize()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        // art.mul sizes: 53 is 44x106, 3319 is 44x40, 3278 is 122x178.
        foreach ((int g, int w, int h) in (ReadOnlySpan<(int, int, int)>)[(53, 44, 106), (3319, 44, 40), (3278, 122, 178)])
        {
            KrTileArt? t = KrTileArt.Read(store, g);

            Assert.NotNull(t);
            Assert.Equal(g, t!.Value.Graphic);
            Assert.Equal(w, t.Value.Width2D);
            Assert.Equal(h, t.Value.Height2D);
        }
    }

    [Fact]
    public void APlacedSpriteIsCentredOnTheTileAndSitsOnItsBottom()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrTileArt? t = KrTileArt.Read(store, 2917);

        if (t is null)
        {
            return;
        }

        KrStaticSprite? sprite = art.SpriteOnTile(2917, t.Value);

        Assert.NotNull(sprite);

        // ClassicUO centres the box on the tile, so the width has to be even
        // for the tile's centre to fall on a pixel boundary.
        Assert.Equal(0, sprite!.Width % 2);
        Assert.Equal(sprite.Width * sprite.Height, sprite.Pixels.Length);

        // 2917's art bottom is 15 KR px above the tile's bottom corner, which
        // at 2/3 is 10 screen px; the canvas bottom IS the tile's bottom corner.
        Assert.InRange(sprite.Height - KrStaticArtBottom(sprite), 9, 11);
    }

    private static int KrStaticArtBottom(KrStaticSprite s)
    {
        KrStaticArt.TryGetBounds(s.Pixels, s.Width, s.Height, out _, out int y0, out _, out int h);
        return y0 + h;
    }

    [Fact]
    public void AWallOverlapsItsHalfOfTheTile()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        KrTileArt? t = KrTileArt.Read(store, 10);

        if (t is null)
        {
            return;
        }

        KrStaticSprite? sprite = art.SpriteOnTile(10, t.Value);

        Assert.NotNull(sprite);

        KrStaticArt.TryGetBounds(sprite!.Pixels, sprite.Width, sprite.Height, out int x0, out _, out int w, out _);

        // Relative to the tile's centre: KR's wall pieces reach a few pixels
        // past the half-tile line on both sides, which is how KR hides the
        // joins between them. Squeezing them inside the 2D edges is what left
        // seams.
        int half = sprite.Width / 2;
        int left = x0 - half;
        int right = x0 + w - half;

        Assert.True(left < -22, $"left edge {left} does not reach past the tile corner");
        Assert.True(right > 0, $"right edge {right} does not reach past the centre line");
    }

    [Fact]
    public void AStatuesStripsLineUpIntoOneTexture()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        // The cat statue in Britain: three statics on neighbouring tiles, all
        // drawing strips of graphic 4770's texture (map0 at 2438,541):
        // 4772 at (-1, 0), 4770 at (0, 0), 4771 at (0, -1). Each strip's left
        // edge is placed at the tile's left corner plus 2/3 of +0x53, and its
        // bottom at the tile's bottom corner plus 2/3 of +0x57. If that is how
        // KR places them, the texture's own origin lands in the same screen
        // spot from all three strips.
        const double s = 2.0 / 3.0;

        List<(double X, double Y)> origins = [];

        foreach ((int g, int dx, int dy) in (ReadOnlySpan<(int, int, int)>)[(4772, -1, 0), (4770, 0, 0), (4771, 0, -1)])
        {
            KrTileArt t = KrTileArt.Read(store, g)!.Value;

            double tileCentreX = (dx - dy) * 22.0;
            double tileBottom = (dx + dy) * 22.0;

            double regionLeft = tileCentreX - 22.0 + (s * t.OffsetX);
            double regionBottom = tileBottom + (s * t.OffsetY);

            origins.Add((regionLeft - (s * t.RegionX), regionBottom - (s * (t.CornerY + 1))));
        }

        foreach ((double x, double y) in origins)
        {
            Assert.InRange(x - origins[0].X, -1.5, 1.5);
            Assert.InRange(y - origins[0].Y, -1.5, 1.5);
        }
    }

    [Fact]
    public void CarpetRecordsDoNotDescribeTheirArt()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        // 2749 and 2754 are carpet pieces: their region is (0,0)-(0,0) and
        // (0,0)-(0,59), and drawing it literally gave a 1 px sliver per tile.
        Assert.False(KrTileArt.Read(store, 2749)!.Value.HasRegion);
        Assert.False(KrTileArt.Read(store, 2754)!.Value.HasRegion);

        // A statue strip and a wall do describe theirs.
        Assert.True(KrTileArt.Read(store, 4770)!.Value.HasRegion);
        Assert.True(KrTileArt.Read(store, 10)!.Value.HasRegion);
    }

    [Fact]
    public void ACarpetMotifLeadsToAnOpaqueBase()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);

        // The red carpet: 2760 is the solid base, 2759 a transparent motif
        // meant to lie on it. The base is not 2759's first reference, but it
        // is one of them.
        Assert.True(art.IsOpaque(2760));
        Assert.False(art.IsOpaque(2759));

        KrStringDictionary strings = KrStringDictionary.Load(
            Path.Combine(path, KrStringDictionary.PackageName));

        List<int> refs = KrTileArt.References(KrTileArt.ReadRaw(store, 2759)!, 2759, strings);

        Assert.Contains(2760, refs);
        Assert.NotEqual(2760, refs[0]);
    }

    [Fact]
    public void AFlatPieceIsAFloorDiamond()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticSprite? flat = new KrStaticArt(store).FlatDiamond(2759, 2760);

        Assert.NotNull(flat);
        Assert.Equal(44, flat!.Width);
        Assert.Equal(44, flat.Height);

        // Corners of the box are outside the diamond and stay empty; its
        // centre is covered, and opaque because the base is.
        Assert.Equal(0u, flat.Pixels[0] >> 24);
        Assert.Equal(0u, flat.Pixels[43] >> 24);
        Assert.Equal(255u, flat.Pixels[(22 * 44) + 22] >> 24);
    }

    [Fact]
    public void ARecordThatNamesItselfDrawsItsOwnTexture()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStringDictionary strings = KrStringDictionary.Load(
            Path.Combine(path, KrStringDictionary.PackageName));

        // 37 names 2 (an ankh) before itself; 331 names 386 (a sandstone
        // block) first. Drawing those put ankhs around the Britain inn.
        foreach (int g in (ReadOnlySpan<int>)[37, 322, 331])
        {
            Assert.Equal(g, KrTileArt.SourceGraphic(KrTileArt.ReadRaw(store, g)!, g, strings));
        }

        // A statue strip names only the shared texture.
        Assert.Equal(4770, KrTileArt.SourceGraphic(KrTileArt.ReadRaw(store, 4772)!, 4772, strings));
    }

    [Fact]
    public void ATreesSlicesDrawFromTheTextureTheirGroupOwns()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStringDictionary strings = KrStringDictionary.Load(
            Path.Combine(path, KrStringDictionary.PackageName));

        // The jungle tree at Trammel 2729-2733, 2167-2171: each slice's KR
        // sprite shader names the owner's texture as its first layer.
        foreach ((int slice, int owner) in ((int, int)[])
            [(3416, 3417), (3418, 3417), (3419, 3417), (3420, 3423), (3424, 3423), (3426, 3423)])
        {
            byte[] raw = KrTileArt.ReadRaw(store, slice)!;
            KrTileArt.ShaderDefinition? shader = KrTileArt.KrShader(raw, strings);

            Assert.Equal("UOSpriteShader", shader?.Class);
            Assert.Equal(owner, KrTileArt.SourceGraphic(raw, slice, strings));
        }
    }

    [Fact]
    public void FloorsCarryATerrainShader()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStringDictionary strings = KrStringDictionary.Load(
            Path.Combine(path, KrStringDictionary.PackageName));

        KrTileArt.ShaderDefinition paver = KrTileArt.KrShader(KrTileArt.ReadRaw(store, 1311)!, strings)!;

        Assert.Equal("UOStaticTerrainShader", paver.Class);
        Assert.Equal(
            ["Data\\WorldArt\\02000260_Stone_Medium_A.tga", "Data\\WorldArt\\02000261_Stone_Medium_B.tga", "Data\\WorldArt\\01000003_noise_alpha.tga"],
            paver.Layers.Select(l => l.Texture));
        Assert.Equal([4f, 4f, 24f], paver.Layers.Select(l => l.Scale));

        KrTileArt.ShaderDefinition cave = KrTileArt.KrShader(KrTileArt.ReadRaw(store, 1340)!, strings)!;

        Assert.Equal("UOStaticTerrainShader", cave.Class);
        Assert.Equal([12f, 6f, 16f], cave.Layers.Select(l => l.Scale));
    }

    [Fact]
    public void StonePaversAreLaidWithStoneMedium()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);

        KrStaticArt art = new(store);
        KrStringDictionary strings = KrStringDictionary.Load(
            Path.Combine(path, KrStringDictionary.PackageName));

        // 1309-1312 have no world art; their records name
        // 02000261_Stone_Medium_B beside their source (TileArtLegacyX9).
        foreach (int g in (int[])[1309, 1310, 1311, 1312])
        {
            Assert.Null(art.Texture(g));

            byte[] raw = KrTileArt.ReadRaw(store, g)!;
            int source = KrTileArt.SourceGraphic(raw, g, strings) ?? g;

            Assert.Equal(2000261, KrTileArt.TerrainTexture(raw, source, strings));
        }

        Assert.NotNull(art.Texture(2000260));

        // Cave floor names its terrain in the record's own name field.
        foreach (int g in (int[])[1339, 1340, 1342])
        {
            Assert.Null(art.Texture(g));

            byte[] raw = KrTileArt.ReadRaw(store, g)!;
            int source = KrTileArt.SourceGraphic(raw, g, strings) ?? g;

            Assert.Equal(2000131, KrTileArt.TerrainTexture(raw, source, strings));
        }

        Assert.NotNull(art.Texture(2000130));
    }
}
