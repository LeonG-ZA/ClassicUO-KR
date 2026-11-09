// SPDX-License-Identifier: BSD-2-Clause
//
// The Enhanced Client's statics: its version 4 tileart and world art,
// against KR's. No-ops without both installs.

using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class EcStaticTests
{
    private static string? Find(string variable, string fallback)
    {
        string? path = Environment.GetEnvironmentVariable(variable);

        path = string.IsNullOrWhiteSpace(path) ? fallback : path;

        return File.Exists(Path.Combine(path, KrTileArt.PackageName)) ? path : null;
    }

    [Fact]
    public void EnhancedTileArtAgreesWithKr()
    {
        string? kr = Find("UOKR_PATH", "");
        string? ec = Find("EC_PATH", "");

        if (kr is null || ec is null)
        {
            return;
        }

        using KrAssetStore krStore = new(kr);
        using KrAssetStore ecStore = new(ec);
        KrStringDictionary krStrings = KrStringDictionary.Load(Path.Combine(kr, KrStringDictionary.PackageName));
        KrStringDictionary ecStrings = KrStringDictionary.Load(Path.Combine(ec, KrStringDictionary.PackageName));
        UopFile ecArt = ecStore.GetPackage(KrTexturePackages.WorldArtPackage)!;
        UopFile krArt = krStore.GetPackage(KrTexturePackages.WorldArtPackage)!;

        int both = 0, parsed = 0, size = 0, region = 0, offset = 0;
        int krSource = 0, sameSource = 0, krShader = 0, sameShader = 0, krTexture = 0, ecTexture = 0;

        for (int g = 0; g < 0x4000; g++)
        {
            byte[]? k = KrTileArt.ReadRaw(krStore, g);
            byte[]? e = KrTileArt.ReadRaw(ecStore, g);

            if (k is null || e is null)
            {
                continue;
            }

            both++;

            if (KrTileArt.Parse(k) is not { } kp || KrTileArt.Parse(e) is not { } ep)
            {
                continue;
            }

            parsed++;
            size += kp.Width2D == ep.Width2D && kp.Height2D == ep.Height2D ? 1 : 0;
            region += (kp.RegionX, kp.RegionY, kp.CornerX, kp.CornerY) == (ep.RegionX, ep.RegionY, ep.CornerX, ep.CornerY) ? 1 : 0;
            offset += (kp.OffsetX, kp.OffsetY) == (ep.OffsetX, ep.OffsetY) ? 1 : 0;

            if (KrTileArt.SourceGraphic(k, g, krStrings) is { } ks)
            {
                krSource++;
                sameSource += KrTileArt.SourceGraphic(e, g, ecStrings) == ks ? 1 : 0;
            }

            if (KrTileArt.KrShader(k, krStrings) is { } kd)
            {
                krShader++;
                KrTileArt.ShaderDefinition? ed = KrTileArt.KrShader(e, ecStrings);
                sameShader += ed is not null && ed.Class == kd.Class
                    && KrTileArt.ArtNumber(ed.Layers[0].Texture) == KrTileArt.ArtNumber(kd.Layers[0].Texture) ? 1 : 0;
            }

            if (krArt.Read(KrTexturePackages.WorldArtEntryName(g)) is not null)
            {
                krTexture++;
                ecTexture += ecArt.Read(KrTexturePackages.WorldArtEntryName(g)) is not null ? 1 : 0;
            }
        }

        string stats =
            $"records {both}, parsed {parsed}, 2D size {size}, region {region}, offset {offset}, " +
            $"source {sameSource}/{krSource}, shader {sameShader}/{krShader}, textures {ecTexture}/{krTexture}";

        // 2026-10-31: 16384 records, all parsed; 2D size 15809, region 16044,
        // offset 16065, source 16329/16384, shader 13000/13051, textures
        // 8746/8746. The rest are EC's own corrections.
        Assert.True(parsed == both && both > 16000, stats);
        Assert.True(region > parsed * 0.97 && offset > parsed * 0.97, stats);
        Assert.True(sameSource > krSource * 0.99 && sameShader > krShader * 0.99, stats);
        Assert.True(ecTexture == krTexture, stats);
    }

    [Fact]
    public void EnhancedWorldArtDecodes()
    {
        string? ec = Find("EC_PATH", "");

        if (ec is null)
        {
            return;
        }

        using KrAssetStore store = new(ec);
        UopFile art = store.GetPackage(KrTexturePackages.WorldArtPackage)!;
        int tried = 0;

        // A spread of statics: walls, trees, furniture, signs.
        foreach (int g in new[] { 0x0006, 0x00A6, 0x0CCA, 0x0D41, 0x0B2C, 0x0A2A, 0x1EB1, 0x3417 })
        {
            if (art.Read(KrTexturePackages.WorldArtEntryName(g)) is not { } raw)
            {
                continue;
            }

            tried++;
            DdsImage image = DdsImage.Decode(raw);

            Assert.True(image.Width > 0 && image.Height > 0);
            Assert.Contains(image.Pixels, p => (p >> 24) == 255);
        }

        Assert.True(tried >= 4, $"only {tried} sample textures present");
    }
}
