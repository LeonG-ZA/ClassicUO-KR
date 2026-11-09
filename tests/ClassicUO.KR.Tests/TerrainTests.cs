// SPDX-License-Identifier: BSD-2-Clause

using System.Buffers.Binary;
using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class TerrainTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c =>
            !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrTerrainDefinition.PackageName)));
    }

    // ---- name resolution -------------------------------------------------

    [Theory]
    [InlineData(@"Data\WorldArt\02000010_Grass_A.tga", "build/worldart/02000010.dds")]
    [InlineData("02000010_Grass_A.tga", "build/worldart/02000010.dds")]
    [InlineData("Data/WorldArt/00006951_dirt.tga", "build/worldart/00006951.dds")]
    // Labels and shader class names carry no art id.
    [InlineData("Swamp2 edges", null)]
    [InlineData("UODefaultTerrainLayer", null)]
    [InlineData("marble (teal)", null)]
    [InlineData("1234567_short.tga", null)]
    [InlineData("", null)]
    public void WorldArtEntryNameTakesTheArtIdFromAFileName(string name, string? expected)
    {
        Assert.Equal(expected, KrTerrainDefinition.WorldArtEntryName(name));
    }

    [Fact]
    public void BuildsTheDocumentedTexturePatterns()
    {
        Assert.Equal("build/terraintexture/00000001.dds", KrTexturePackages.TerrainTextureEntryName(1));
        Assert.Equal("build/terraintexture/00000022.dds", KrTexturePackages.TerrainTextureEntryName(22));
        Assert.Equal("build/worldart/02000010.dds", KrTexturePackages.WorldArtEntryName(2000010));
        Assert.Equal("build/terraindefinition/190.bin", KrTerrainDefinition.EntryName(190));
    }

    // ---- the record grammar ----------------------------------------------

    private static byte[] BuildDefinition(int id, uint nameIndex, uint baseIndex, int items, (uint Texture, float Scale, byte Slot)[] layers)
    {
        byte[] data = new byte[KrTerrainDefinition.FixedSize + (items * 12) + (layers.Length * KrTerrainLayer.RecordSize)];

        BinaryPrimitives.WriteUInt32LittleEndian(data, nameIndex);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4), id);
        BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0x14), items);

        int offset = 0x18 + (items * 12);

        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset), 1);
        offset += 2;

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), baseIndex);
        offset += 4;

        data[offset++] = (byte)layers.Length;

        foreach ((uint texture, float scale, byte slot) in layers)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset), texture);
            data[offset + 4] = 0;
            BinaryPrimitives.WriteSingleLittleEndian(data.AsSpan(offset + 5), scale);
            data[offset + 9] = slot;
            offset += KrTerrainLayer.RecordSize;
        }

        return data;
    }

    [Fact]
    public void ParsesADefinitionWithItsLayerSet()
    {
        byte[] raw = BuildDefinition(
            52,
            29878,
            29663,
            items: 2,
            [(29879u, 5f, 0), (29879u, 5f, 1), (29666u, 16f, 2)]);

        KrTerrainDefinition d = KrTerrainDefinition.Parse(raw);

        Assert.Equal(52, d.Id);
        Assert.Equal(29878u, d.NameStringIndex);
        Assert.Equal(29663u, d.BaseTextureStringIndex);
        Assert.Equal(2, d.ItemCount);
        Assert.Equal(3, d.Layers.Length);

        Assert.Equal(29879u, d.Layers[0].TextureStringIndex);
        Assert.Equal(5f, d.Layers[0].Scale);
        Assert.Equal(0, d.Layers[0].Slot);
        Assert.Equal(16f, d.Layers[2].Scale);
        Assert.Equal(2, d.Layers[2].Slot);
    }

    [Theory]
    // The shipped lengths, which are 71 + 12*items + 17*layers.
    [InlineData(1, 3, 134)]
    [InlineData(2, 3, 146)]
    [InlineData(1, 4, 151)]
    [InlineData(3, 3, 158)]
    [InlineData(2, 5, 180)]
    public void RecordLengthFollowsFromTheItemAndLayerCounts(int items, int layers, int expected)
    {
        (uint, float, byte)[] set = [.. Enumerable.Range(0, layers).Select(i => (1u, 1f, (byte)i))];

        byte[] raw = BuildDefinition(0, 1, 2, items, set);

        Assert.Equal(expected, raw.Length);
        Assert.Equal(layers, KrTerrainDefinition.Parse(raw).Layers.Length);
    }

    [Fact]
    public void RejectsADefinitionWhoseLengthDoesNotMatchItsCounts()
    {
        byte[] raw = BuildDefinition(0, 1, 2, items: 1, [(1u, 1f, 0)]);

        Assert.Throws<InvalidDataException>(() => KrTerrainDefinition.Parse([.. raw, (byte)0]));
        Assert.Throws<InvalidDataException>(() => KrTerrainDefinition.Parse(raw.AsSpan(0, raw.Length - 1).ToArray()));
        Assert.Throws<InvalidDataException>(() => KrTerrainDefinition.Parse(new byte[8]));
    }

    // ---- the string dictionary -------------------------------------------

    [Fact]
    public void ParsesAStringDictionary()
    {
        List<byte> data = [.. new byte[16]];
        BinaryPrimitives.WriteUInt32LittleEndian(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(data).Slice(8), 3);

        foreach (string s in (string[])["UOSpriteShader", "02000010_Grass_A.tga"])
        {
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(s);
            data.AddRange([(byte)(bytes.Length & 0xFF), (byte)(bytes.Length >> 8)]);
            data.AddRange(bytes);
        }

        KrStringDictionary dictionary = KrStringDictionary.Parse([.. data]);

        // KR's indexes are 1-based: string 0 is an implicit empty entry, which
        // is why the declared count is one more than the strings stored.
        Assert.Equal(3, dictionary.Count);
        Assert.Equal(3u, dictionary.DeclaredCount);
        Assert.Equal(string.Empty, dictionary[0]);
        Assert.Equal("UOSpriteShader", dictionary[1]);
        Assert.Equal("02000010_Grass_A.tga", dictionary[2]);
        Assert.Null(dictionary[3]);
        Assert.Null(dictionary[-1]);
    }

    // ---- the DDS decoder -------------------------------------------------

    private static byte[] Dds(int width, int height, string fourCc, byte[] payload)
    {
        byte[] data = new byte[DdsImage.HeaderSize + payload.Length];

        data[0] = (byte)'D';
        data[1] = (byte)'D';
        data[2] = (byte)'S';
        data[3] = (byte)' ';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x10), (uint)width);

        for (int i = 0; i < 4; i++)
        {
            data[0x54 + i] = (byte)fourCc[i];
        }

        payload.CopyTo(data.AsSpan(DdsImage.HeaderSize));

        return data;
    }

    /// <summary>
    /// Colour index 2 is (2*c0 + c1)/3 and index 3 is (c0 + 2*c1)/3. Swapping
    /// them still yields a plausible-looking image, so this is pinned with an
    /// explicit block rather than left to an eyeball check.
    /// </summary>
    [Fact]
    public void DecodesDxt1ColourIndexesInTheRightOrder()
    {
        // c0 = pure red (0xF800), c1 = pure blue (0x001F), c0 > c1 so the
        // block is in four-colour mode. First row indexes 0,1,2,3.
        byte[] block =
        [
            0x00, 0xF8, 0x1F, 0x00,
            0xE4, 0x00, 0x00, 0x00
        ];

        DdsImage image = DdsImage.Decode(Dds(4, 4, "DXT1", block));

        Assert.Equal(4, image.Width);
        Assert.Equal(DdsFormat.Dxt1, image.Format);

        // (A << 24) | (B << 16) | (G << 8) | R
        // red, blue, then (2*red + blue)/3 and (red + 2*blue)/3
        Assert.Equal(0xFF0000FFu, image.Pixels[0]);
        Assert.Equal(0xFFFF0000u, image.Pixels[1]);
        Assert.Equal(0xFF5500AAu, image.Pixels[2]);
        Assert.Equal(0xFFAA0055u, image.Pixels[3]);
    }

    [Fact]
    public void DecodesDxt1PunchThroughTransparency()
    {
        // c0 <= c1 selects the three-colour mode, where index 3 is transparent
        // and index 2 is the midpoint.
        byte[] block =
        [
            0x1F, 0x00, 0x00, 0xF8,
            0xE4, 0x00, 0x00, 0x00
        ];

        DdsImage image = DdsImage.Decode(Dds(4, 4, "DXT1", block));

        Assert.Equal(0xFFFF0000u, image.Pixels[0]);
        Assert.Equal(0xFF0000FFu, image.Pixels[1]);
        Assert.Equal(0xFF7F007Fu, image.Pixels[2]);
        Assert.Equal(0u, image.Pixels[3]);
    }

    [Fact]
    public void DecodesDxt5Alpha()
    {
        // a0 = 255, a1 = 0, so the eight alpha slots interpolate between them;
        // the first four pixels take slots 0, 1, 2 and 3.
        byte[] block =
        [
            0xFF, 0x00,
            0x88, 0x06, 0x00, 0x00, 0x00, 0x00,
            0x00, 0xF8, 0x1F, 0x00,
            0x00, 0x00, 0x00, 0x00
        ];

        DdsImage image = DdsImage.Decode(Dds(4, 4, "DXT5", block));

        Assert.Equal(DdsFormat.Dxt5, image.Format);
        Assert.Equal(255u, image.Pixels[0] >> 24);
        Assert.Equal(0u, image.Pixels[1] >> 24);
        Assert.Equal(6 * 255 / 7u, image.Pixels[2] >> 24);
        Assert.Equal(5 * 255 / 7u, image.Pixels[3] >> 24);
    }

    [Fact]
    public void RejectsWhatIsNotADds()
    {
        Assert.False(DdsImage.IsDds(new byte[4]));
        Assert.Throws<InvalidDataException>(() => DdsImage.Decode(new byte[200]));
        Assert.Throws<InvalidDataException>(() => DdsImage.Decode(Dds(4, 4, "DXT3", new byte[16])));
        // A header that promises more pixels than the payload can hold.
        Assert.Throws<InvalidDataException>(() => DdsImage.Decode(Dds(64, 64, "DXT1", new byte[8])));
    }

    // ---- against the real assets -----------------------------------------

    [Fact]
    public void EveryTerrainDefinitionParses()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using UopFile file = UopFile.Open(Path.Combine(root, KrTerrainDefinition.PackageName));

        List<KrTerrainDefinition> all = KrTerrainDefinition.LoadAll(file);

        Assert.Equal(191, all.Count);
        Assert.Equal(file.Entries.Count, all.Count);

        for (int i = 0; i < all.Count; i++)
        {
            // Ids run 0..190 with no gaps, and each record says its own id.
            Assert.Equal(i, all[i].Id);
            Assert.InRange(all[i].Layers.Length, 3, 5);

            foreach (KrTerrainLayer layer in all[i].Layers)
            {
                Assert.Equal(0, layer.Flag);
                Assert.InRange(layer.Slot, 0, 4);
                Assert.InRange(layer.Scale, 0.5f, 48f);
            }
        }
    }

    /// <summary>
    /// Follows every texture reference in every definition into Texture.uop.
    /// This is the check that both the record grammar and the
    /// name-to-entry rule hold.
    /// </summary>
    [Fact]
    public void EveryNamedTextureReferenceResolves()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using UopFile defs = UopFile.Open(Path.Combine(root, KrTerrainDefinition.PackageName));
        using UopFile art = UopFile.Open(Path.Combine(root, KrTexturePackages.WorldArtPackage));

        KrStringDictionary strings =
            KrStringDictionary.Load(Path.Combine(root, KrStringDictionary.PackageName));

        int refs = 0;
        int named = 0;

        foreach (KrTerrainDefinition d in KrTerrainDefinition.LoadAll(defs))
        {
            // Every layer names a real texture; the "base" field is the class.
            List<uint> indexes = [.. d.Layers.Select(l => l.TextureStringIndex)];

            foreach (uint index in indexes)
            {
                refs++;

                string? entry = KrTerrainDefinition.WorldArtEntryName(strings[index]);

                if (entry is null)
                {
                    continue;
                }

                named++;

                Assert.True(art.Read(entry) is not null, $"terrain {d.Id}: {entry} is absent");
            }
        }

        Assert.Equal(626, refs);
        Assert.Equal(626, named);
    }

    /// <summary>
    /// Pins the string-dictionary index base. 53 of the 191 terrain
    /// definitions name a texture whose eight-digit art id is 0200{id:D3}0 for
    /// the terrain's own id - a coincidence that only survives at this base
    /// (shifting the index by one drops it to 3).
    /// </summary>
    [Fact]
    public void StringDictionaryIndexesLineUpWithTerrainIds()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using UopFile defs = UopFile.Open(Path.Combine(root, KrTerrainDefinition.PackageName));

        KrStringDictionary strings =
            KrStringDictionary.Load(Path.Combine(root, KrStringDictionary.PackageName));

        // 30,220 stored strings at 1..30,220 plus the implicit empty string 0.
        Assert.Equal(30221, strings.Count);
        Assert.Equal(30221u, strings.DeclaredCount);

        // Read 1-based, the field section 9 called the base texture is the
        // shader class of every one of the 191 terrains (docs section 29).
        int classes = 0;

        foreach (KrTerrainDefinition d in KrTerrainDefinition.LoadAll(defs))
        {
            if (strings[d.BaseTextureStringIndex] is "UODefaultTerrainLayer" or "UOWaterTerrainLayer" or "UOBumpMapTerrainLayer")
            {
                classes++;
            }
        }

        Assert.Equal(191, classes);
    }

    /// <summary>
    /// TerrainTexture.uop holds 20 DDS entries despite its name having nothing
    /// to do with the ground: these are the sky and celestial textures.
    /// </summary>
    [Fact]
    public void TerrainTexturePackageHoldsTwentyDecodableTextures()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using UopFile package = UopFile.Open(Path.Combine(root, KrTexturePackages.TerrainTexturePackage));

        Assert.Equal(20, package.Entries.Count);

        List<int> ids = [];

        for (int id = 0; id <= 64; id++)
        {
            byte[]? data = package.Read(KrTexturePackages.TerrainTextureEntryName(id));

            if (data is null)
            {
                continue;
            }

            ids.Add(id);

            DdsImage image = DdsImage.Decode(data);

            Assert.Contains(image.Width, (int[])[128, 256]);
            Assert.Equal(image.Width, image.Height);
            Assert.Equal(image.Width * image.Height, image.Pixels.Length);
        }

        // Every entry was named, so the pattern accounts for the whole package.
        Assert.Equal(20, ids.Count);
        Assert.Equal<int[]>([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 20, 21, 22], [.. ids]);
    }

    [Fact]
    public void GroundTexturesDecodeFromTheWorldArtPackage()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using UopFile art = UopFile.Open(Path.Combine(root, KrTexturePackages.WorldArtPackage));

        // Grass_A, Forest_A and the noise map the terrain shader blends with.
        foreach (int id in (int[])[2000010, 2000040, 1000045])
        {
            byte[]? data = art.Read(KrTexturePackages.WorldArtEntryName(id));

            Assert.NotNull(data);

            DdsImage image = DdsImage.Decode(data);

            Assert.InRange(image.Width, 64, 1024);
            Assert.Equal(image.Width * image.Height, image.Pixels.Length);
        }
    }
}
