// SPDX-License-Identifier: BSD-2-Clause
//
// KR's rain puddle scenes: Splash.tga
// particles from a mesh emitter. Reads a real UOKR install; no-ops without
// one.

using ClassicUO.KR.Effects;
using Xunit;

namespace ClassicUO.KR.Tests;

public class WeatherSceneTests
{
    [Theory]
    [InlineData("00070001_RainPuddles.nif", 120f)]
    [InlineData("00070002_HvyRainPuddles.nif", 210f)]
    public void PuddleScenesSplash(string scene, float rate)
    {
        string root = Environment.GetEnvironmentVariable("UOKR_PATH") ?? "";

        if (!File.Exists(Path.Combine(root, KrEffects.ScenePackage)))
        {
            return;
        }

        using KrAssetStore store = new(root);
        NifFile file = NifFile.TryParse(store.GetPackage(KrEffects.ScenePackage)!.Read(KrEffects.SceneEntryName(scene))!, out _)!;

        NifBlock emitter = file.Blocks.Single(b => b.Type == "NiPSysMeshEmitter");
        NifBlock birth = file.Blocks.Single(b => b.Type == "NiFloatInterpolator");

        Assert.Equal(rate, birth.Float("value"));
        Assert.Equal(0.5333f, emitter.Float("life"), 3);
        Assert.Contains(file.Blocks, b => b.Type == "NiSourceTexture" && b.Get("file", "") == "03004048_Splash.tga");

        NifEffect nif = Assert.IsType<NifEffect>(new KrEffects(store).Create(scene, 1));
        nif.Loop = true;

        for (int i = 0; i < 60; i++)
        {
            nif.Update(1f / 30f);
        }

        // Base and detail are both the splash: drawn as their product
        // (NifEffect.Look's composite, "base:...|detail:...").
        Assert.Contains(nif.Sprites(), s => s.Texture == "base:03004048_Splash.tga|detail:03004048_Splash.tga");
    }
}
