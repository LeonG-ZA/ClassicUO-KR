// SPDX-License-Identifier: BSD-2-Clause

using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// KR particle effects from a shard that sends their 2D fallback. RunUO
    /// sends a particle effect (0xC7, with its KR effect id) only to clients
    /// it takes for UO:TD, KR or EC (kr_advertise_uotd); every other client
    /// gets the effect's 2D graphic, speed, duration, hue and render mode
    /// (0x70 / 0xC0); ServUO and ModernUO do the same. The table, from the
    /// scripts of RunUO, ServUO and ModernUO
    /// (tools/effect-fallback-table.py), names the particle effects each 2D
    /// effect stands for; where several share one, the sound the server
    /// plays on the same tile tells them apart - Heal (5005, sound 0x1F2) and
    /// Night Sight (5007, 0x1E3) both send 0x376A at speed 9 for 32 frames.
    /// </summary>
    internal static partial class KrEffectFallback
    {
        internal readonly record struct Entry(int Graphic, int Speed, int Duration, int Hue, int Render, int Sound, int Effect, int Layer, int Calls);

        /// <summary>
        /// How long an effect waits for its sound, and how long a sound waits
        /// for its effect: the server sends both in one tick, in either order.
        /// </summary>
        private const uint PairMs = 250;

        /// <summary>
        /// The fields send their 0x376A sparkle with no sound, on a tile the
        /// field item has just been placed on (Spells/Fourth/FireField.cs).
        /// </summary>
        private static readonly Dictionary<int, ushort[]> FieldItems = new()
        {
            [5025] = [0x0080, 0x0082],   // Wall of Stone
            [5029] = [0x398C, 0x3996],   // Fire Field
            [5040] = [0x3915, 0x3922],   // Poison Field
            [5048] = [0x3967, 0x3979],   // Paralyze Field
            [5051] = [0x3946, 0x3956]    // Energy Field
        };

        /// <summary>
        /// Effects sent as a pair at two spots in one tick, first one then the
        /// other: Teleport, recalling guards and creatures, moongate-like
        /// teleporters send 0x3728 as 2023 at the old spot and 5023 at the
        /// new (Spells/Third/Teleport.cs). A lone 0x3728 is mostly 2023 - a
        /// guard or creature leaving.
        /// </summary>
        private static readonly Dictionary<int, int> Departures = new()
        {
            [2023] = 5023
        };

        /// <summary>The Lightning spell's KR scene: RunUO sends it (0xC7, layer waist) before each bolt.</summary>
        private const int LightningEffect = 5031;

        private sealed class Pending
        {
            public List<Entry> Candidates;
            public GraphicEffectType Type;
            public uint Source;
            public ushort Graphic, Hue, X, Y;
            public sbyte Z;
            public byte Speed, Duration;
            public GraphicEffectBlendMode Blend;
            public uint Ticks;
            public bool Second;
        }

        private static Dictionary<(int, int, int), List<Entry>> _byKey;
        private static readonly Dictionary<(int, int, int), (ushort X, ushort Y, uint Ticks)> _firsts = [];
        private static readonly List<Pending> _pending = [];
        private static readonly List<(ushort Sound, ushort X, ushort Y, uint Ticks)> _sounds = [];

        /// <summary>The table, for KrEffectSheet.</summary>
        public static IReadOnlyList<Entry> Entries => Table;

        /// <summary>On when KR effects play and the shard is not asked for 0xC7.</summary>
        public static bool Active => KrParticles.Enabled && !Settings.GlobalSettings.KrAdvertiseUotd;

        /// <summary>
        /// A fixed 2D effect (0x70 / 0xC0): true when it stands for a KR
        /// particle effect, which is played - now, or once its sound has come.
        /// </summary>
        public static bool TryPlay(
            World world, GraphicEffectType type, uint source, ushort graphic, ushort x, ushort y, sbyte z,
            byte speed, byte duration, ushort hue, GraphicEffectBlendMode blend, bool hued)
        {
            if (!Active)
            {
                return false;
            }

            if (type == GraphicEffectType.Lightning)
            {
                Log(world, $"bolt -> {LightningEffect}", LightningEffect);

                return KrParticles.TrySpawn(LightningEffect, source, x, y, z, 3);
            }

            List<Entry> candidates = Candidates(graphic, speed, duration, hue, (int)blend, hued);

            if (candidates == null)
            {
                return false;
            }

            Pending p = new()
            {
                Candidates = candidates, Type = type, Source = source, Graphic = graphic, Hue = hue,
                X = x, Y = y, Z = z, Speed = speed, Duration = duration, Blend = blend, Ticks = Time.Ticks
            };

            // The second of two at different spots in one tick (Departures).
            var key = (graphic, speed, duration);

            if (_firsts.TryGetValue(key, out var first) && Time.Ticks - first.Ticks <= PairMs && (first.X != x || first.Y != y))
            {
                p.Second = true;
                _firsts.Remove(key);
            }
            else
            {
                _firsts[key] = (x, y, Time.Ticks);
            }

            if (!Resolve(world, p, waiting: true))
            {
                _pending.Add(p);
            }

            return true;
        }

        /// <summary>A sound (0x54): settles an effect on its tile waiting for it.</summary>
        public static void OnSound(World world, ushort sound, ushort x, ushort y)
        {
            if (!Active)
            {
                return;
            }

            uint now = Time.Ticks;
            _sounds.RemoveAll(s => now - s.Ticks > PairMs);
            _sounds.Add((sound, x, y, now));

            for (int i = 0; i < _pending.Count; i++)
            {
                if (Resolve(world, _pending[i], waiting: true))
                {
                    _pending.RemoveAt(i--);
                }
            }
        }

        /// <summary>Each frame: an effect whose sound has not come plays its best guess.</summary>
        public static void Update(World world)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (Time.Ticks - _pending[i].Ticks > PairMs)
                {
                    Resolve(world, _pending[i], waiting: false);
                    _pending.RemoveAt(i--);
                }
            }
        }

        private static List<Entry> Candidates(ushort graphic, byte speed, byte duration, ushort hue, int render, bool hued)
        {
            if (_byKey == null)
            {
                _byKey = [];

                foreach (Entry e in Table)
                {
                    if (!_byKey.TryGetValue((e.Graphic, e.Speed, e.Duration), out List<Entry> list))
                    {
                        _byKey[(e.Graphic, e.Speed, e.Duration)] = list = [];
                    }

                    list.Add(e);
                }
            }

            if (!_byKey.TryGetValue((graphic, speed, duration), out List<Entry> all))
            {
                return null;
            }

            // 0xC0 carries hue and render mode; 0x70 does not. An entry's -1
            // (a script's variable, such as a creature's own hue) matches any.
            if (hued)
            {
                List<Entry> exact = all.FindAll(e => (e.Hue == hue || e.Hue < 0) && (e.Render == render || e.Render < 0));

                if (exact.Count > 0)
                {
                    return exact;
                }
            }

            return all;
        }

        /// <summary>Plays the effect once it is known which; false while it waits for its sound.</summary>
        private static bool Resolve(World world, Pending p, bool waiting)
        {
            List<Entry> c = p.Candidates;
            string how = null;

            if (!SameEffect(c))
            {
                List<Entry> field = c.FindAll(e => FieldItems.TryGetValue(e.Effect, out ushort[] items) && HasItem(world, p.X, p.Y, items));

                if (field.Count > 0)
                {
                    c = field;
                    how = "field item";
                }
                else
                {
                    // Without its field item it is no field: ModernUO's Magic
                    // Trap sends Wall of Stone's sparkle with no sound.
                    List<Entry> unfielded = c.FindAll(e => !FieldItems.ContainsKey(e.Effect));

                    if (unfielded.Count > 0)
                    {
                        c = unfielded;
                    }

                    List<Entry> heard = c.FindAll(e => e.Sound >= 0 && Heard(e.Sound, p.X, p.Y));

                    if (heard.Count > 0)
                    {
                        c = heard;
                        how = $"sound 0x{heard[0].Sound:X}";
                    }
                    else if (waiting && c.Exists(e => e.Sound >= 0))
                    {
                        return false;
                    }
                    else
                    {
                        List<Entry> silent = c.FindAll(e => e.Sound < 0);

                        if (silent.Count > 0)
                        {
                            c = silent;
                        }

                        how = "no sound";
                    }
                }
            }

            foreach (var (from, to) in Departures)
            {
                if (c.Exists(e => e.Effect == from) && c.Exists(e => e.Effect == to))
                {
                    int want = p.Second ? to : from;
                    c = c.FindAll(e => e.Effect == want);
                    how = (how != null ? how + ", " : "") + (p.Second ? "new spot" : "old spot");

                    break;
                }
            }

            Entry pick = Best(c);
            Log(world, $"2D 0x{p.Graphic:X4} {p.Speed}/{p.Duration}{(how != null ? $" ({how})" : "")} -> {pick.Effect}", pick.Effect);

            if (!KrParticles.TrySpawn(pick.Effect, p.Source, p.X, p.Y, p.Z, (byte)pick.Layer, p.Graphic, p.Duration))
            {
                // No KR scene for it: KR draws the 2D graphic, as it does for
                // a 0xC7 effect without one.
                world.SpawnEffect(
                    p.Type, p.Source, 0, p.Graphic, p.Hue, p.X, p.Y, p.Z, p.X, p.Y, p.Z,
                    p.Speed, p.Duration, false, false, false, p.Blend);
            }

            return true;
        }

        private static bool SameEffect(List<Entry> c)
        {
            for (int i = 1; i < c.Count; i++)
            {
                if (c[i].Effect != c[0].Effect)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Among effects still tied: one with a KR scene, then the one more
        /// calls send over the servers' scripts, then the lowest id. Replayed
        /// over each server's calls this picks the script's own effect for
        /// 259 of RunUO-KR's 263, 262 of RunUO's 266, 534 of ServUO's 554
        /// and 261 of ModernUO's 265. Of the spells, the rest:
        /// - Reactive Armor (5008) and Heal (5005): same graphic and sound,
        ///   so one plays the other's.
        /// - Gift of Renewal (5005, no sound) plays the doors' (5022).
        /// - Rising Colossus (ServUO, 5034) plays a lone teleport's 2D one.
        /// The others are ServUO's bosses and store items.
        /// </summary>
        private static Entry Best(List<Entry> c)
        {
            Entry best = c[0];

            for (int i = 1; i < c.Count; i++)
            {
                Entry e = c[i];
                int byScene = HasScene(e.Effect).CompareTo(HasScene(best.Effect));

                if (byScene > 0 || byScene == 0 && (e.Calls > best.Calls || e.Calls == best.Calls && e.Effect < best.Effect))
                {
                    best = e;
                }
            }

            return best;
        }

        // A definition record of its own: what KR plays (KrParticles.SpawnParts);
        // without one, KR's placeholder - so it ranks below one that has one.
        private static bool HasScene(int effect) => KrEffectProvider.Instance?.Effects.PartsForServerEffect(effect).Count > 0;

        private static bool Heard(int sound, ushort x, ushort y)
        {
            foreach (var s in _sounds)
            {
                if (s.Sound == sound && s.X == x && s.Y == y && Time.Ticks - s.Ticks <= PairMs)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool HasItem(World world, ushort x, ushort y, ushort[] graphics)
        {
            for (GameObject o = world.Map?.GetTile(x, y, false); o != null; o = o.TNext)
            {
                if (o is Item item && System.Array.IndexOf(graphics, item.Graphic) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>kr_effect_log: the KR scene an effect plays, to the journal.</summary>
        public static void Log(World world, string what, int effect)
        {
            if (Settings.GlobalSettings.KrEffectLog)
            {
                string scene = KrEffectProvider.Instance?.Effects.SceneForServerEffect(effect) ?? "no KR scene";
                GameActions.Print(world, $"KR fx {what}: {scene}", 0x0035);
            }
        }
    }
}
