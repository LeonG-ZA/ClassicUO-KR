// SPDX-License-Identifier: BSD-2-Clause
//
// The NIF 20.0.0.4 block reader and the particle simulation built on it.
// Needs a real install; skips itself without one.

using ClassicUO.KR.Effects;
using ClassicUO.KR.Uop;
using Xunit;

namespace ClassicUO.KR.Tests;

public class NifFileTests
{
    private static string? FindInstall() =>
        new[] { Environment.GetEnvironmentVariable("UOKR_PATH") }
            .FirstOrDefault(c => !string.IsNullOrWhiteSpace(c) && File.Exists(Path.Combine(c!, KrEffects.ScenePackage)));

    [Fact]
    public void ScenesParseToTheirLastByte()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using UopFile package = UopFile.Open(Path.Combine(path, KrEffects.ScenePackage));

        int exact = 0, unread = 0;
        List<string> wrong = [];

        foreach (UopEntry e in package.Entries)
        {
            byte[] data = package.Read(e);

            if (!NifHeader.LooksLikeNif(data))
            {
                continue;
            }

            if (NifFile.TryParse(data, out string? error) is not null)
            {
                exact++;
            }
            else if (error!.Contains("no reader", StringComparison.Ordinal))
            {
                unread++;   // a type not written yet - not a layout error
            }
            else
            {
                wrong.Add(error);
            }
        }

        // A misread layout shows as a block reading past its end or ending
        // short; none may.
        Assert.Empty(wrong);
        Assert.Equal(444, exact);
        Assert.Equal(7, unread);
    }

    [Fact]
    public void FireballFliesAsItsGraphicsScene()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);
        KrEffects effects = new(store);

        // A moving effect's scene is numbered by its 2D graphic: Fireball
        // 0x36D4 = 14036.
        string scene = effects.SceneForGraphic(0x36D4)!;
        Assert.Equal("00014036_LargeFireball.nif", scene);

        IKrEffect fx = Assert.IsType<NifEffect>(effects.Create(scene, 1));
        fx.Update(0.3f);

        Assert.Contains(fx.Sprites(), s => s.Texture.Equals("03004007_ornblast2.tga", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MagicArrowsFireTrailRunsAndEnds()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);
        KrEffects effects = new(store);

        string scene = effects.SceneForServerEffect(9041)!;
        IKrEffect fx = Assert.IsType<NifEffect>(effects.Create(scene, 1));

        fx.Update(0.5f);
        List<EmsSprite> sprites = fx.Sprites().ToList();

        Assert.NotEmpty(sprites);
        Assert.All(sprites, s => Assert.Equal("03004010_flmglintBIG.tga", s.Texture, ignoreCase: true));
        Assert.All(sprites, s => Assert.True(s.Additive));   // alpha flags 4097: ONE, ONE

        // Its emitter loops (controller flags 8), so the scene alone does not
        // end; its definition part does, at 2 s (KrParticles drops it there).
        for (int i = 0; i < 40; i++)
        {
            fx.Update(0.1f);
        }

        Assert.False(fx.Finished);
        Assert.Contains(effects.PartsForServerEffect(9041), p => p.Scene == scene && p.Duration == 2f);
    }

    [Fact]
    public void TheWispLoops()
    {
        string? path = FindInstall();

        if (path is null)
        {
            return;
        }

        using KrAssetStore store = new(path);
        KrEffects effects = new(store);

        NifEffect wisp = Assert.IsType<NifEffect>(effects.Create(effects.SceneForBody(58)!, 1));
        wisp.Loop = true;

        // Played once it is over in about 1.5 s; looping it is still
        // sparkling well after that.
        for (int i = 0; i < 100; i++)
        {
            wisp.Update(0.1f);
        }

        Assert.False(wisp.Finished);
        Assert.NotEmpty(wisp.Sprites());
        Assert.Null(effects.SceneForBody(400));

        // 10,000,000 + body names a creature's own scene; used only when
        // KR has no frames for the body.
        Assert.Equal("10000164_EnergyVortex.nif", effects.SceneForBody(164));
        Assert.Null(effects.SceneForBody(15));   // fire elemental: animated
    }
}
