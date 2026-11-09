// SPDX-License-Identifier: BSD-2-Clause
//
// KR's weather (kr_weather). KR draws it with scenes numbered from 70000 in
// EffectTexture.uop:
//
//   snow        00070000_Snow.nif - two particle clouds of SnowFlake.tga
//   rain        00070004/05/06_rain.nif - not particles: two or three large
//               planes of faint streaks (LightRain, SoftRain, HeavyRain),
//               alpha-blended over the view; layered, they read as a haze
//
// Snow runs as a looping particle scene on the player through KrParticles.
// Rain also splashes on the ground, as the puddle scenes do (70001
// RainPuddles, 70002 HvyRainPuddles): additive 03004048_Splash.tga rings
// born anywhere on a quad over the ground - 120/s light, 210/s heavy -
// living 0.53 s and growing in over the first 0.17 s. Drawn here across
// the view and pinned to the ground (kr_rain_splashes, off by default), kept out from under the roof hidden
// over a player indoors; the scene's mesh emitter is not run.
// Rain is drawn here as the scenes' streak textures tiled over the view and
// scrolled downward, one layer per plane, faded in and out by WeatherFade.
// Off, or without a KR install, the 2D weather is drawn instead.

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Managers
{
    internal static class KrWeather
    {
        public const string SnowScene = "00070000_Snow.nif";

        // Each rain level's planes, as its scene lists them.
        private static readonly string[] LightRain = ["03004107_LightRain.tga", "03004107_LightRain.tga"];
        private static readonly string[] MediumRain = ["03004107_LightRain.tga", "03004108_SoftRain.tga"];
        private static readonly string[] HeavyRain = ["03004108_HeavyRain.tga", "03004108_HeavyRain.tga", "03004107_LightRain.tga"];

        // Per layer: scroll speed in px/s, sideways drift, alpha, tile size.
        private static readonly (float Speed, float Drift, float Alpha, int Tile)[] Layers =
        [
            (520f, 60f, 0.45f, 256),
            (380f, 40f, 0.35f, 320),
            (300f, 25f, 0.30f, 384),
        ];

        private const string SplashTexture = "03004048_Splash.tga";

        // The scenes' birth rates, over KR's 865 x 649 game view.
        private const float LightSplashRate = 120f;
        private const float HeavySplashRate = 210f;
        private const float KrViewArea = 865f * 649f;
        private const float SplashLife = 0.5333f;
        private const float SplashGrow = 0.1667f;

        // The scene's particle radius (0.2 x its 0.656 scale) on a quad 26.2
        // units across, taken as the view's width: about 10 px across.
        private const float SplashPx = 10f;

        private struct Splash
        {
            public float X, Y, Age;
        }

        private static readonly List<Splash> _splashes = [];
        private static float _splashDebt;
        private static uint _lastSplashTick;

        public static bool Enabled =>
            Settings.GlobalSettings.KrWeather && KrEffectProvider.IsActive;

        /// <summary>
        /// Draws the current weather the KR way. False when KR weather is off
        /// or there is nothing to draw, so the 2D weather draws instead.
        /// </summary>
        public static bool Draw(World world, UltimaBatcher2D batcher, Point offset, Func<int, int, bool> open, float depth)
        {
            if (!Enabled)
            {
                KrParticles.SetWeather(null, 0);

                return false;
            }

            Weather weather = world.Weather;

            WeatherFade.Update(weather);

            // Still ours while fading, so the 2D weather doesn't start early.
            if (!weather.IsActive && WeatherFade.Shown == null || world.Player == null)
            {
                KrParticles.SetWeather(null, 0);

                return false;
            }

            if (WeatherFade.Shown == WeatherType.WT_SNOW)
            {
                KrParticles.SetWeather(SnowScene, world.Player.Serial);

                return true;
            }

            KrParticles.SetWeather(null, 0);

            // Clouds still gathering, or rain not yet started.
            if (WeatherFade.Shown == null || WeatherFade.Precipitation <= 0f)
            {
                _splashes.Clear();

                return true;
            }

            string[] planes = WeatherFade.Shown switch
            {
                WeatherType.WT_STORM_APPROACH => HeavyRain,
                WeatherType.WT_STORM_BREWING => MediumRain,
                _ => LightRain,
            };

            float splashRate = WeatherFade.Shown == WeatherType.WT_STORM_APPROACH ? HeavySplashRate : LightSplashRate;

            if (Settings.GlobalSettings.KrRainSplashes)
            {
                DrawSplashes(batcher, offset, open, splashRate, WeatherFade.Count, WeatherFade.Precipitation, depth);
            }

            DrawRain(batcher, planes, WeatherFade.Count, WeatherFade.Precipitation, depth);

            return true;
        }

        private static void DrawSplashes(UltimaBatcher2D batcher, Point offset, Func<int, int, bool> open, float rate, byte count, float amount, float depth)
        {
            Texture2D texture = KrParticles.EffectTexture(batcher.GraphicsDevice, SplashTexture);

            if (texture == null)
            {
                return;
            }

            Viewport view = batcher.GraphicsDevice.Viewport;
            uint now = Time.Ticks;
            float seconds = _lastSplashTick == 0 ? 0f : Math.Min((now - _lastSplashTick) / 1000f, 0.25f);
            _lastSplashTick = now;

            for (int i = _splashes.Count - 1; i >= 0; i--)
            {
                Splash s = _splashes[i];
                s.Age += seconds;

                if (s.Age >= SplashLife)
                {
                    _splashes.RemoveAt(i);
                }
                else
                {
                    _splashes[i] = s;
                }
            }

            // Born across the view, at ground positions (world pixels) so
            // they stay put as the view scrolls; only where the rain can
            // reach, not under a roof hidden to show the player inside.
            float density = Math.Clamp(count / 70f, 0.35f, 1f) * amount;
            _splashDebt += rate * density * (view.Width * view.Height / KrViewArea) * seconds;

            for (; _splashDebt >= 1f; _splashDebt -= 1f)
            {
                int x = Random.Shared.Next(view.Width) + offset.X;
                int y = Random.Shared.Next(view.Height) + offset.Y;

                if (open(x, y))
                {
                    _splashes.Add(new Splash { X = x, Y = y, Age = 0f });
                }
            }

            Rectangle source = new(0, 0, texture.Width, texture.Height);
            Vector3 hue = new(0, ShaderHueTranslator.SHADER_NONE, 1f);

            batcher.SetBlendState(BlendState.Additive);

            foreach (Splash s in _splashes)
            {
                float size = SplashPx * Math.Min(1f, s.Age / SplashGrow);

                batcher.Draw(
                    texture,
                    new Rectangle((int)(s.X - offset.X - size / 2f), (int)(s.Y - offset.Y - size / 2f), (int)MathF.Ceiling(size), (int)MathF.Ceiling(size)),
                    source,
                    hue,
                    0f,
                    Vector2.Zero,
                    SpriteEffects.None,
                    depth);
            }

            batcher.SetBlendState(null);
        }

        private static void DrawRain(UltimaBatcher2D batcher, string[] planes, byte count, float amount, float depth)
        {
            Viewport view = batcher.GraphicsDevice.Viewport;
            float seconds = (Time.Ticks % 600000u) / 1000f;

            // The packet's density (0-70) thins light rain out.
            float density = Math.Clamp(count / 70f, 0.35f, 1f) * amount;

            batcher.SetStencil(DepthStencilState.None);

            for (int i = 0; i < planes.Length && i < Layers.Length; i++)
            {
                Texture2D texture = KrParticles.EffectTexture(batcher.GraphicsDevice, planes[i]);

                if (texture == null)
                {
                    continue;
                }

                (float speed, float drift, float alpha, int tile) = Layers[i];
                int ox = (int)(seconds * drift) % tile;
                int oy = (int)(seconds * speed) % tile;
                Vector3 hue = new(0, ShaderHueTranslator.SHADER_NONE, alpha * density);

                for (int y = oy - tile; y < view.Height; y += tile)
                {
                    for (int x = -ox - tile; x < view.Width; x += tile)
                    {
                        batcher.Draw(
                            texture,
                            new Rectangle(x, y, tile, tile),
                            new Rectangle(0, 0, texture.Width, texture.Height),
                            hue,
                            0f,
                            Vector2.Zero,
                            SpriteEffects.None,
                            depth);
                    }
                }
            }

            batcher.SetStencil(DepthStencilState.Default);
        }
    }
}
