// SPDX-License-Identifier: BSD-2-Clause
//
// Eases weather changes instead of switching at once: clouds gather before
// the rain starts, and the rain eases off before the clouds clear. A change
// between two kinds of weather fades the old rain out and the new one in,
// with the clouds kept if both have them. Drives kr_weather's rain and
// kr_lighting's clouds; the 2D weather and KR's snow scene still switch at
// once.

using System;

namespace ClassicUO.Game.Managers
{
    internal static class WeatherFade
    {
        private const float PrecipitationSeconds = 4f;
        private const float CloudSeconds = 4f;

        private static uint _lastTick;

        /// <summary>The weather being shown, which lags the packet's while fading.</summary>
        public static WeatherType? Shown { get; private set; }

        /// <summary>How much of the shown weather's rain is drawn, 0..1.</summary>
        public static float Precipitation { get; private set; }

        /// <summary>How much of the cloud layer is drawn, 0..1.</summary>
        public static float Clouds { get; private set; }

        /// <summary>The shown weather's density, kept while it fades out.</summary>
        public static byte Count { get; private set; }

        public static bool HasClouds(WeatherType? type) =>
            type is WeatherType.WT_RAIN or WeatherType.WT_STORM_APPROACH or WeatherType.WT_STORM_BREWING;

        /// <summary>Steps the fade towards the current weather; safe to call more than once a frame.</summary>
        public static void Update(Weather weather)
        {
            uint now = Time.Ticks;
            float seconds = _lastTick == 0 ? 0f : Math.Min((now - _lastTick) / 1000f, 0.25f);
            _lastTick = now;

            WeatherType? target = weather.IsActive ? weather.Type : null;
            float targetClouds = HasClouds(target) ? 1f : 0f;
            float precipitationStep = seconds / PrecipitationSeconds;
            float cloudStep = seconds / CloudSeconds;

            if (Shown != target)
            {
                // The old rain first, then the clouds, then the new weather.
                if (Precipitation > 0f)
                {
                    Precipitation = Math.Max(0f, Precipitation - precipitationStep);

                    return;
                }

                if (Clouds != targetClouds)
                {
                    Clouds = Approach(Clouds, targetClouds, cloudStep);

                    return;
                }

                Shown = target;
            }

            if (target != null)
            {
                Count = weather.Count;
            }

            Clouds = Approach(Clouds, targetClouds, cloudStep);
            Precipitation = Approach(Precipitation, target != null ? 1f : 0f, precipitationStep);
        }

        private static float Approach(float value, float target, float step) =>
            value < target ? Math.Min(target, value + step) : Math.Max(target, value - step);
    }
}
