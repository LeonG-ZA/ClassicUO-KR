// SPDX-License-Identifier: BSD-2-Clause
//
// Eases the overall light level: a change from the server (day turning to
// night) is drawn over a few seconds instead of at once. Arriving somewhere
// new - another map, or a jump further than walking covers, like a dungeon
// entrance or a gate - takes the new level at once, including a level that
// comes in just before or after the jump.

using System;

namespace ClassicUO.Game.Managers
{
    internal static class LightFade
    {
        private const float Seconds = 10f;

        // Further than a step a frame, even running.
        private const int TeleportTiles = 8;

        // How long after a jump the level is still taken at once.
        private const uint TeleportGraceMs = 1500;

        private static float _shown = -1f, _from;
        private static int _target;
        private static uint _start, _teleportTick;
        private static int _lastMap = -1, _lastX, _lastY;

        /// <summary>The overall light level to draw, 0 (day) to 31, eased towards the server's.</summary>
        public static float Overall { get; private set; }

        /// <summary>Still easing towards the server's level.</summary>
        public static bool IsFading => _shown >= 0f && _shown != _target;

        /// <summary>Steps the fade; safe to call more than once a frame.</summary>
        public static float Update(World world)
        {
            uint now = Time.Ticks;
            int target = world.Light.Overall;

            if (world.Player is { } player)
            {
                if (_lastMap != world.MapIndex
                    || Math.Max(Math.Abs(player.X - _lastX), Math.Abs(player.Y - _lastY)) > TeleportTiles)
                {
                    _teleportTick = now;
                }

                _lastMap = world.MapIndex;
                _lastX = player.X;
                _lastY = player.Y;
            }

            if (_shown < 0f || now - _teleportTick < TeleportGraceMs)
            {
                _shown = _target = target;
            }
            else if (target != _target)
            {
                _from = _shown;
                _target = target;
                _start = now;
            }

            if (_shown != _target)
            {
                float t = Math.Min(1f, (now - _start) / 1000f / Seconds);

                _shown = t >= 1f ? _target : _from + (_target - _from) * t;
            }

            return Overall = _shown;
        }
    }
}
