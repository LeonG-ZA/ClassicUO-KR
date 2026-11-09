// SPDX-License-Identifier: BSD-2-Clause
//
// KR's light sources: the light an object's tileart names, and the
// TerrainTexture it lives in. Reads a
// real UOKR install; no-ops without one.

using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using Xunit;

namespace ClassicUO.KR.Tests;

public class LightTests
{
    private static string? FindInstall()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("UOKR_PATH")];

        return candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && KrAssetStore.LooksLikeKrInstall(c!));
    }

    /// <summary>The lookup KrLightProvider.LightFor makes.</summary>
    private static int LightFor(KrAssetStore store, KrStringDictionary strings, int graphic)
    {
        if (KrTileArt.ReadRaw(store, graphic) is not { } raw)
        {
            return -1;
        }

        foreach (KrTileArt.ShaderDefinition def in KrTileArt.Shaders(raw, strings))
        {
            if (def.Layers.Count > 0
                && def.Layers[0].Texture.Contains("\\Textures\\", StringComparison.OrdinalIgnoreCase)
                && KrTileArt.ArtNumber(def.Layers[0].Texture) is { } n and > 0 and < 64)
            {
                return n;
            }
        }

        return -1;
    }

    [Fact]
    public void TileArtNamesEachObjectsLight()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);
        KrStringDictionary strings = KrStringDictionary.Load(Path.Combine(root, KrStringDictionary.PackageName));

        Assert.Equal(6, LightFor(store, strings, 0x0D11));  // mushrooms: MushroomA
        Assert.Equal(7, LightFor(store, strings, 0x0B1A));  // candle: CandleA
        Assert.Equal(2, LightFor(store, strings, 0x0B20));  // lamp post: light_post
        Assert.Equal(-1, LightFor(store, strings, 0x0597)); // slate roof: none
    }

    [Fact]
    public void LightTexturesDecode()
    {
        string? root = FindInstall();

        if (root is null)
        {
            return;
        }

        using KrAssetStore store = new(root);
        UopFile package = store.GetPackage(KrTexturePackages.TerrainTexturePackage)!;

        // 6 MushroomA (a 256x256 DXT5) and 21, the ambient noise.
        foreach (int id in new[] { 1, 6, 7, 21 })
        {
            DdsImage image = DdsImage.Decode(package.Read(KrTexturePackages.TerrainTextureEntryName(id))!);

            Assert.True(image.Width >= 64 && image.Height >= 64, $"texture {id} is {image.Width}x{image.Height}");
        }

        DdsImage mushroom = DdsImage.Decode(package.Read(KrTexturePackages.TerrainTextureEntryName(6))!);

        Assert.Equal((256, 256), (mushroom.Width, mushroom.Height));
    }
}
