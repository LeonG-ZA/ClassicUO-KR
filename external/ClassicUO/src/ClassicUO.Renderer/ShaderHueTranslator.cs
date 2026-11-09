// SPDX-License-Identifier: BSD-2-Clause

using System;
using Microsoft.Xna.Framework;
using System.Runtime.CompilerServices;

namespace ClassicUO.Renderer
{
    public static class ShaderHueTranslator
    {
        public const byte SHADER_NONE = 0;
        public const byte SHADER_HUED = 1;
        public const byte SHADER_PARTIAL_HUED = 2;
        public const byte SHADER_TEXT_HUE_NO_BLACK = 3;
        public const byte SHADER_TEXT_HUE = 4;
        public const byte SHADER_LAND = 5;
        public const byte SHADER_LAND_HUED = 6;

        /// <summary>KR terrain, lit with KR's fitted light (IsometricWorld.fx get_kr_light).</summary>
        public const byte SHADER_KR_LAND = 16;

        /// <summary>KR swamp/sewer water statics (IsometricWorld.fx SWAMP).</summary>
        public const byte SHADER_SWAMP = 17;

        /// <summary>KR swamp water land, terrain 70 (IsometricWorld.fx SWAMP_WATER).</summary>
        public const byte SHADER_SWAMP_WATER = 18;

        /// <summary>KR lava statics 0x12F4-0x1318 (IsometricWorld.fx LAVA).</summary>
        public const byte SHADER_LAVA = 19;

        /// <summary>KR star field land, terrain 53 (IsometricWorld.fx STARS).</summary>
        public const byte SHADER_STARS = 20;

        /// <summary>KR land composed on the GPU from the terrain atlas (IsometricWorld.fx KR_TERRAIN).</summary>
        public const byte SHADER_KR_TERRAIN = 21;
        public const byte SHADER_SPECTRAL = 7;
        public const byte SHADER_SHADOW = 8;
        public const byte SHADER_LIGHTS = 9;
        public const byte SHADER_EFFECT_HUED = 10;

        /// <summary>
        /// Kingdom Reborn variants of <see cref="SHADER_HUED"/> and
        /// <see cref="SHADER_PARTIAL_HUED"/>. KR art is full 32-bit colour, so
        /// the shader indexes the hue ramp by luminance instead of the red
        /// channel and gates the partial hue on saturation instead of
        /// r == g == b. Only reachable for sprites flagged
        /// <see cref="SpriteInfo.IsKrArt"/>; 2D art never takes these paths.
        /// </summary>
        public const byte SHADER_KR_HUED = 11;
        public const byte SHADER_KR_PARTIAL_HUED = 12;

        /// <summary>
        /// An unhued KR sprite. Identical to <see cref="SHADER_NONE"/> in what
        /// it does to the colour; it exists so the shader can tell a KR sprite
        /// apart and read it through the linear sampler at s3. Without it an
        /// unhued KR mobile would fall into SHADER_NONE and be point-sampled.
        /// </summary>
        public const byte SHADER_KR_NONE = 13;

        /// <summary>
        /// Unhued water - land tiles and water statics - drawn through the
        /// world shader's KR-style water (ripple shading and sun glints).
        /// Used only while <see cref="WaterShader"/> is on.
        /// </summary>
        public const byte SHADER_WATER = 14;

        /// <summary>
        /// The texture times an RGB colour packed into the hue's X as
        /// r * 65536 + g * 256 + b - KR particle colours.
        /// </summary>
        public const byte SHADER_TINT = 15;

        /// <summary>Packs a colour for <see cref="SHADER_TINT"/>.</summary>
        public static float PackTint(float r, float g, float b)
        {
            int ri = (int)(Math.Clamp(r, 0f, 1f) * 255f + 0.5f);
            int gi = (int)(Math.Clamp(g, 0f, 1f) * 255f + 0.5f);
            int bi = (int)(Math.Clamp(b, 0f, 1f) * 255f + 0.5f);

            return (ri << 16) | (gi << 8) | bi;
        }

        /// <summary>Set from the kr_water_shader setting at start-up.</summary>
        public static bool WaterShader;

        private const byte GUMP_OFFSET = 32; // above the KR modes (11-20); must match IsometricWorld.fx GUMP

        private const ushort SPECTRAL_COLOR_FLAG = 0x4000;

        public static readonly Vector3 SelectedHue = new Vector3(23, 1, 0);

        public static readonly Vector3 SelectedItemHue = new Vector3(0x0035, 1, 0);

        public static Vector3 GetHueVector(int hue)
        {
            return GetHueVector(hue, false, 1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        /// <param name="krArt">
        /// True when the sprite about to be drawn came from a Kingdom Reborn
        /// package (<see cref="SpriteInfo.IsKrArt"/>). Selects the KR hue
        /// modes; leave it false and the result is bit-for-bit what it was
        /// before the KR modes existed.
        /// </param>
        public static Vector3 GetHueVector(int hue, bool partial, float alpha, bool gump = false, bool effect = false, bool circletrans = false, bool krArt = false)
        {
            Vector3 hueVector;
            byte type;

            if ((hue & 0x8000) != 0)
            {
                partial = true;
                hue &= 0x7FFF;
            }

            if (hue == 0)
            {
                partial = false;
            }

            if ((hue & SPECTRAL_COLOR_FLAG) != 0)
            {
                type = SHADER_SPECTRAL;
            }
            else if (hue != 0)
            {
                hue -= 1;

                // TODO: check if effect + partial is a thing. Because on shader the effect uses the G component instead of R to get the hue index
                type = effect && !partial ? SHADER_EFFECT_HUED : partial ? SHADER_PARTIAL_HUED : SHADER_HUED;

                // KR art only ever reaches the two plain hue modes - it is
                // animation frames, never gump or effect art - so the swap is
                // confined to those.
                if (krArt && !effect && !gump)
                {
                    type = partial ? SHADER_KR_PARTIAL_HUED : SHADER_KR_HUED;
                }

                if (gump && !effect)
                {
                    type += GUMP_OFFSET;
                }
            }
            else
            {
                // An unhued KR sprite still has to be told apart from 2D art,
                // or the shader point-samples it. Gump and effect art never
                // comes from KR, same as the hued case above.
                type = krArt && !effect && !gump ? SHADER_KR_NONE : SHADER_NONE;
            }

            hueVector.X = hue;
            hueVector.Y = type;
            hueVector.Z = alpha;

            if (circletrans)
            {
                hueVector.Z += 1f;
            }

            return hueVector;
        }
    }
}
