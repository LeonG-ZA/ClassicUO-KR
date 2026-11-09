// SPDX-License-Identifier: BSD-2-Clause
//
// Alternate animation source backed by a local Kingdom Reborn install.
//
// This sits beside the MUL/UOP readers in AnimationsLoader: when the user has
// pointed the client at a UOKR folder and KR ships art for a body/action, the
// frames come from AnimationFrame*.uop; otherwise the caller falls through to
// the normal 2D art. No KR asset ships with the client - everything is read
// from the user's own install.
//
// Action numbering lines up with ClassicUO's own groups. Verified against the
// sound events in AnimationSequence.uop: for body 400, action 0 (WalkUnarmed)
// and action 15 (WalkWarmode) carry footstep sounds 299/300, action 23
// (OnmountRideSlow) carries four hoof-falls, and action 24 (OnmountRideFast)
// uses a different pair, 297/298. Monster fallbacks agree too: body 1 resolves
// GetHit3 to GetHit2, both Fidgets to Stand and Fly to Walk.

using System;
using System.Collections.Generic;
using ClassicUO.KR;
using ClassicUO.KR.Animations;
using ClassicUO.KR.World;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Assets
{
    public sealed class KrAnimationProvider : IDisposable
    {
        /// <summary>
        /// Maps a ClassicUO stored direction (0-4, as produced by
        /// Animations.GetAnimDirection) to a KR stored direction.
        /// </summary>
        /// <remarks>
        /// Both clients store five directions and mirror the other three, and
        /// the five KR directions run front-to-back in the same order, so this
        /// is the identity today. It stays a table so the order can be
        /// corrected without touching the decoder.
        /// </remarks>
        private static readonly int[] DirectionMap = [0, 1, 2, 3, 4];

        /// <summary>
        /// ClassicUO's low (animal) action numbering translated into KR's.
        /// </summary>
        /// <remarks>
        /// KR indexes every creature, animals included, the way ClassicUO
        /// indexes monsters - its slots 0-21 are <see cref="HighAnimationGroup"/>
        /// - and then adds its own 22-27 on top. ClassicUO drives animals with
        /// the much shorter <see cref="LowAnimationGroup"/> instead, so without
        /// this table an idle animal asks for Stand (low 2) and gets KR's Die1,
        /// which is why horses and dogs played their death animation on repeat.
        ///
        /// Verified by rendering body 204: KR 1 is a stand, 2 and 3 are the two
        /// deaths, 22 is the walk, 24 a gallop, 25 a neutral idle. The structure
        /// of KR's own fallback chain agrees - its 17 and 18 (Fidget1, Fidget2),
        /// 19 and 20 (Fly, Land) all redirect to the stand, and 16 (GetHit3) to
        /// 15 (GetHit2), exactly as the monster bodies do.
        ///
        /// Each entry is tried in order and the first with art wins, so a body
        /// missing KR's extras still lands somewhere sensible. Checked over all
        /// 66 animal bodies KR ships: slots 22 and 25 are present in every one,
        /// 24 in 63.
        /// </remarks>
        private static readonly int[][] LowToKrAction =
        [
            [0],      // Walk
            [24, 0],  // Run      -> gallop, else the walk
            [1],      // Stand
            [26, 1],  // Eat      -> KR has no grazing; 26 is its quietest idle
            [25, 1],  // Unknown  -> never requested by ClassicUO; asked for a
                      //             ridden mount standing (MobileView): KR's
                      //             neutral idle 25, as KR plays under a rider
            [4],      // Attack1
            [5],      // Attack2
            [6],      // Attack3
            [2],      // Die1
            [17, 1],  // Fidget1
            [18, 1],  // Fidget2
            [11, 25], // LieDown  -> KR's own slot, else the neutral idle
            [3, 2],   // Die2
        ];

        private readonly KrAssetStore _store;

        // The Enhanced Client's install, when people are drawn from it
        // (ec_humans): humans, elves, gargoyles and everything they wear - the
        // ClassicUO "people" group - come from here first, KR otherwise.
        private KrAssetStore _ec;

        private KrAnimationProvider(KrAssetStore store)
        {
            _store = store;
        }

        /// <summary>Whether people are drawn from the Enhanced Client (ec_humans with ec_path set).</summary>
        public bool EcPeople => _ec != null;

        /// <summary>
        /// Whether our own left-hand torch, lantern and candle (KrHeldLights)
        /// may be drawn: set from ec_humans being off. They are drawn only
        /// when this is on and people are KR's (no EC attached).
        /// </summary>
        public bool OwnHeldLights { get; set; }

        private bool UseOwnHeldLights => OwnHeldLights && _ec == null;

        /// <summary>
        /// Factor EC frames are drawn at: its human is 67 px tall where KR's
        /// is 93, so 2/3 of KR and 0.93 of EC both stand on the 2D tile grid.
        /// </summary>
        public double EcFrameScale { get; set; } = 0.93;

        /// <summary>
        /// Screen pixels an EC rider is lowered on a KR mount beyond the
        /// measured seat alignment (ec_rider_drop): judged by eye.
        /// </summary>
        public float EcRiderDrop { get; set; } = 0f;

        /// <summary>
        /// KR's seat height that stacking the rider's anchor on the mount's
        /// already gives, in native pixels: the ostard's measured offsets
        /// measured (2026-10-30) were exactly (seat.x, 10 - seat.y) in
        /// directions 1, 2 and 4. Facing the camera a by-eye -13 replaced the
        /// data's -35, but that was judged while the forest ostard was drawn
        /// from EC's unrelated body 827; on KR's own ostard the data holds.
        /// </summary>
        private const int StackedSeatY = 10;

        private readonly Dictionary<int, sbyte[]> _seats = [];

        /// <summary>
        /// Screen offset to move a rider by on a KR-drawn mount. KR stores
        /// each mount's seat in its ridden idle (action 25): the 16 bytes
        /// after the frame rate are eight signed (x, y) pairs, one per UO
        /// direction (N, NE, E, SE, S, SW, W, NW), y up; 0x80 everywhere
        /// for actions without one.
        /// </summary>
        public (float X, float Y) RiderOffset(int mountBody, int direction)
        {
            if ((uint)direction > 4 || ResolveAction(mountBody, 25) < 0)
            {
                return (0f, 0f);
            }

            sbyte[] seat = Seat(mountBody, out bool krSeatOnEc);

            if (seat == null)
            {
                return (0f, 0f);
            }

            // ClassicUO's unmirrored directions 0-4 are UO's SE, S, SW, W, NW.
            int i = (direction + 3) * 2;
            int x = seat[i], y = seat[i + 1];

            // EC's own seat, in EC's native pixels. The stacking constant is
            // set so EC's horse lands where KR's does (KR's SE: 7.3 px down).
            if (_ecSeats.Contains(mountBody))
            {
                return ((float)(x * EcFrameScale), (float)((EcStackedSeatY - y) * EcFrameScale));
            }

            // KR's seats are in KR's native pixels: on screen at KR's 2/3,
            // whatever scale EC's mount is drawn at.
            double scale = krSeatOnEc ? AmouFrameScaler.TileScale : FrameScale;

            return ((float)(x * scale), (float)((StackedSeatY - y) * scale));
        }

        /// <summary>
        /// KR's mount seats (action 25's flag bytes: eight signed (x, y)
        /// pairs, N to NW, y up, in KR's native pixels), for mounts drawn
        /// from the Enhanced Client, whose sequences carry none.
        /// </summary>
        private static readonly Dictionary<int, sbyte[]> KrSeats = new()
        {
            [8] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [114] = [-1, 2, -2, 2, -6, 6, 0, -1, 6, 6, 2, 2, 1, 2, 0, -3],
            [115] = [-1, 2, -2, 2, -3, 4, 0, -1, 3, 4, 2, 2, 1, 2, 0, -3],
            [116] = [-1, 2, -2, 2, -6, 6, 0, -1, 6, 6, 2, 2, 1, 2, 0, -3],
            [118] = [0, 7, -2, 5, -5, 6, 0, 5, 5, 6, 2, 5, 0, 7, 0, 8],
            [122] = [-1, 2, -2, 2, -5, 4, 0, 5, 5, 4, 2, 2, 1, 2, 0, -3],
            [132] = [0, 1, -8, -1, -1, 0, 2, 0, 1, 0, 8, -1, 0, 1, 0, -1],
            [169] = [-14, -11, -13, -7, -5, -2, -2, 0, 5, -2, 13, -7, 14, -11, 2, -16],
            [170] = [-2, -3, -7, -5, -4, 1, 0, 7, 4, 1, 7, -5, 2, -3, 0, -10],
            [187] = [-7, -3, -14, 1, -18, 12, -1, 6, 18, 12, 14, 1, 7, -3, 3, -4],
            [191] = [0, 1, -8, -1, -1, 0, 2, 0, 1, 0, 8, -1, 0, 1, 0, -1],
            [192] = [-1, 2, -2, 2, -5, 4, 0, 5, 5, 4, 2, 2, 1, 2, 0, -3],
            [193] = [-7, -3, -14, 1, -18, 12, -1, 6, 18, 12, 14, 1, 7, -3, 3, -4],
            [195] = [-14, -11, -13, -7, -5, -2, -2, 0, 5, -2, 13, -7, 14, -11, 2, -16],
            [200] = [-1, 2, -2, 2, -7, 7, 0, -1, 7, 7, 2, 2, 1, 2, 0, -3],
            [204] = [-1, 2, -2, 2, -5, 3, 0, -1, 5, 3, 2, 2, 1, 2, 0, -3],
            [210] = [-6, 17, -8, 26, -9, 30, 0, 45, 9, 30, 8, 26, 6, 17, 0, 6],
            [213] = [4, 7, 3, -1, 4, -11, 2, 0, -4, -11, -3, -1, -4, 7, -2, 8],
            [218] = [-6, 17, -8, 26, -9, 30, 0, 45, 9, 30, 8, 26, 6, 17, 0, 6],
            [219] = [-6, 17, -8, 26, -9, 30, 0, 45, 9, 30, 8, 26, 6, 17, 0, 6],
            [220] = [-2, -3, -7, -5, -4, 1, 0, 7, 4, 1, 7, -5, 2, -3, 0, -10],
            [226] = [-1, 2, -2, 2, -9, 6, 0, 1, 9, 6, 2, 2, 1, 2, 0, -3],
            [228] = [-1, 2, -2, 2, -5, 4, 1, -1, 5, 4, 2, 2, 1, 2, 0, -3],
            [243] = [-1, 0, -1, 5, -7, 14, 0, 25, 7, 14, 1, 5, 1, 0, 1, -4],
            [257] = [-1, 2, -2, 2, -6, 6, 0, -1, 6, 6, 2, 2, 1, 2, 0, -3],
            [276] = [2, 3, -1, -2, -6, -3, -1, 19, 6, -3, 1, -2, -2, 3, 0, -9],
            [277] = [3, -1, -1, -4, -3, -1, 0, 0, 3, -1, 1, -4, -3, -1, 0, -20],
            [284] = [4, 18, -1, 9, 1, 12, 0, 10, -1, 12, 1, 9, -4, 18, 0, 15],
            [574] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            [791] = [5, -5, -4, -11, -11, -6, 2, -6, 11, -6, 4, -11, -5, -5, 0, -7],
            [793] = [-1, 6, -2, 5, -6, 6, 0, -1, 6, 6, 2, 5, 1, 6, 0, -3],
            [794] = [-14, -22, 1, -7, 14, -21, 0, 0, -14, -21, -1, -7, 14, -22, -5, -5],
            [799] = [3, 2, -2, -5, -11, -3, 4, 4, 11, -3, 2, -5, -3, 2, -4, 7],
            [840] = [-7, -3, -14, 1, -10, 7, -1, 0, 10, 7, 14, 1, 7, -3, 0, 0],
            [841] = [-7, -3, -14, 1, -18, 12, -1, 6, 18, 12, 14, 1, 7, -3, 3, -4],
            [949] = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
        };

        /// <summary>
        /// EC's own seat bytes for EC mounts instead of KR's table. Off: tried
        /// 2026-11-08, they sink riders (the ostard's put the rider ~7 px down
        /// where KR lifts it 23 px), so EC must place riders by other means.
        /// </summary>
        private const bool UseEcSeats = false;

        private const int EcStackedSeatY = 8;

        private readonly HashSet<int> _ecSeats = [];

        private readonly HashSet<int> _krSeatsOnEc = [];

        private sbyte[] Seat(int body, out bool krSeatOnEc)
        {
            lock (_seats)
            {
                if (_seats.TryGetValue(body, out sbyte[] cached))
                {
                    krSeatOnEc = _krSeatsOnEc.Contains(body);

                    return cached;
                }

                sbyte[] seat = null;
                KrAnimationSequence sequence = _store.LoadSequence(ArtBody(body));
                krSeatOnEc = false;

                // EC keeps its seat in the same flag bytes, on action 25 or,
                // for some mounts (the ostards: 29-31), on another action.
                if (sequence is { Enhanced: true })
                {
                    if (UseEcSeats)
                    {
                        foreach (KrAnimationAction a in sequence.Actions.Count > 25
                            ? System.Linq.Enumerable.Concat(System.Linq.Enumerable.Skip(sequence.Actions, 25), System.Linq.Enumerable.Take(sequence.Actions, 25))
                            : sequence.Actions)
                        {
                            if (a.Flags is { Length: 16 } f && Array.Exists(f, b => b != 0x80 && b != 0))
                            {
                                seat = Array.ConvertAll(f, b => b == 0x80 ? (sbyte)0 : (sbyte)b);
                                _ecSeats.Add(body);

                                break;
                            }
                        }
                    }
                    else if (KrSeats.TryGetValue(body, out sbyte[] kr))
                    {
                        seat = kr;
                        krSeatOnEc = true;
                        _krSeatsOnEc.Add(body);
                    }
                }
                else if (sequence != null && sequence.Actions.Count > 25)
                {
                    byte[] flags = sequence.Actions[25].Flags;

                    if (flags.Length == 16 && Array.Exists(flags, b => b != 0x80))
                    {
                        seat = Array.ConvertAll(flags, b => (sbyte)b);
                    }
                }

                _seats[body] = seat;

                return seat;
            }
        }

        /// <summary>
        /// Draws people from the Enhanced Client at <paramref name="ecPath"/>.
        /// Its animation packages share KR's format.
        /// False, and KR is used, when the folder holds none.
        /// </summary>
        public bool AttachEnhanced(string ecPath)
        {
            if (!KrAssetStore.LooksLikeKrInstall(ecPath))
            {
                Log.Warn($"EC humans disabled: '{ecPath}' has no {KrAssetStore.AnimationFramePackages[0]}.");

                return false;
            }

            _ec = new KrAssetStore(ecPath);
            Log.Trace($"EC humans: people drawn from {ecPath}.");

            return true;
        }

        /// <summary>
        /// Unarmed people actions EC may lack, and the armed ones to play
        /// instead: the newer EC re-rendered its torches (501, 504) into the
        /// left hand for the armed actions only, dropping walk, run and stand.
        /// ClassicUO also plays a held light's walk (0) while its holder stands.
        /// </summary>
        private static readonly int[][] EcArmedFallback =
        [
            [1],    // 0 walk  -> walk armed
            [],
            [3],    // 2 run   -> run armed
            [],
            [37, 7], // 4 stand -> EC's equipment stand (shields, weapons have no 4), else armed stance
        ];

        /// <summary>The active provider, or null when KR animations are off.</summary>
        public static KrAnimationProvider Instance { get; private set; }

        private double _frameScale = AmouFrameScaler.TileScale;

        /// <summary>
        /// Factor KR frames are drawn at. Defaults to
        /// <see cref="AmouFrameScaler.TileScale"/> (2/3), which puts a KR
        /// human on the 2D client's 44x44 tile grid; 1.0 draws KR art at its
        /// native size.
        /// </summary>
        /// <remarks>
        /// This no longer touches the pixels. Frames are decoded and atlased
        /// at KR's native resolution and the scale is applied when the sprite
        /// is drawn, so what it changes is the geometry the renderer derives
        /// in <c>SpriteInfo.Create</c> - the frame's on-screen size and its
        /// anchor. Those are cached alongside the atlas entry, so callers that
        /// change this at runtime must still rebuild them; see
        /// <c>Animations.ResetSprites</c>. The decoded bitmaps themselves are
        /// scale-independent now and stay in this class's cache.
        /// </remarks>
        public double FrameScale
        {
            get => _frameScale;
            set => _frameScale = Math.Clamp(value, 0.1, 4.0);
        }

        public static bool IsActive => Instance != null;

        public string RootPath => _store.RootPath;

        /// <summary>
        /// Points the client at a UOKR install. Returns false (and logs) when the
        /// folder is missing or does not hold the animation packages; the client
        /// then just keeps using 2D art.
        /// </summary>
        public static bool Initialize(string krPath)
        {
            Shutdown();

            if (string.IsNullOrWhiteSpace(krPath))
            {
                return false;
            }

            if (!KrAssetStore.LooksLikeKrInstall(krPath))
            {
                Log.Warn($"KR animations disabled: '{krPath}' has no {KrAssetStore.AnimationFramePackages[0]}.");

                return false;
            }

            try
            {
                Instance = new KrAnimationProvider(new KrAssetStore(krPath));
                Log.Trace($"KR animations enabled from '{krPath}'.");

                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"KR animations disabled: {ex.Message}");
                Instance = null;

                return false;
            }
        }

        public static void Shutdown()
        {
            Instance?.Dispose();
            Instance = null;
        }

        /// <summary>True when KR knows this body at all.</summary>
        /// <remarks>
        /// The sequence record is the right test rather than the presence of a
        /// frame file for action 0: animals keep no art in slot 0 and redirect
        /// it, so they would read as missing. Results are cached, misses
        /// included, so this is cheap enough to ask per frame.
        /// </remarks>
        private readonly Dictionary<int, int> _equipAnims = [];
        private readonly HashSet<int> _equipAnimIds = [];

        /// <summary>
        /// The animation KR draws a worn item with. The 2D tiledata's anim id
        /// is the legacy one; KR's tileart names a newer one for many items
        /// (leather gloves 545 -> 760, dagger and samurai armour among them,
        /// 61 items KR has no art for under the legacy id). Used when KR has
        /// that body and the legacy one it lacks; not with EC humans, where
        /// EC draws people and their equipment.
        /// </summary>
        public int EquipmentAnim(int graphic, int legacyAnim)
        {
            if (_ec != null)
            {
                return EcEquipmentAnim(graphic, legacyAnim);
            }

            lock (_equipAnims)
            {
                if (_equipAnims.TryGetValue(graphic, out int cached))
                {
                    return cached;
                }

                int result = legacyAnim;

                try
                {
                    byte[] data = KrTileArt.ReadRaw(_store, graphic);

                    if (data != null && _store.LoadSequence(ArtBody(legacyAnim)) == null)
                    {
                        // KR's tileart (version 3): at 0x73 the first property set,
                        // a count, then (u8 id, u32 value); property 8 is the
                        // appearance. The second set holds the legacy id.
                        int o = 0x73;

                        if (data.Length > o && BitConverter.ToUInt16(data, 0) == 3)
                        {
                            int count = data[o++];

                            for (int i = 0; i < count && o + 5 <= data.Length; i++, o += 5)
                            {
                                if (data[o] == 8)
                                {
                                    int anim = (int)BitConverter.ToUInt32(data, o + 1);

                                    if (anim is > 0 and < ushort.MaxValue && anim != legacyAnim && _store.LoadSequence(ArtBody(anim)) != null)
                                    {
                                        result = anim;
                                        _equipAnimIds.Add(anim);
                                    }

                                    break;
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"KR tileart {graphic}: {ex.Message}");
                }

                _equipAnims[graphic] = result;
                Log.Trace($"KR equipment 0x{graphic:X4}: anim {legacyAnim} -> {result}");

                return result;
            }
        }

        /// <summary>
        /// EC's newer anim id for a worn item (dagger 622 -> 638): EC's tileart
        /// (version 4) has the same property set as KR's, 10 bytes later at
        /// 0x7D. Used when EC has art for it and none for the legacy id.
        /// </summary>
        private int EcEquipmentAnim(int graphic, int legacyAnim)
        {
            lock (_equipAnims)
            {
                if (_equipAnims.TryGetValue(graphic, out int cached))
                {
                    return cached;
                }

                int result = legacyAnim;

                try
                {
                    byte[] data = KrTileArt.ReadRaw(_ec, graphic);
                    int o = 0x73 + KrTileArt.EnhancedShift;

                    if (data != null && data.Length > o && BitConverter.ToUInt16(data, 0) == 4
                        && !_ec.HasAnimation(legacyAnim, 1))
                    {
                        int count = data[o++];

                        for (int i = 0; i < count && o + 5 <= data.Length; i++, o += 5)
                        {
                            if (data[o] == 8)
                            {
                                int anim = (int)BitConverter.ToUInt32(data, o + 1);

                                if (anim is > 0 and < ushort.MaxValue && anim != legacyAnim && _ec.HasAnimation(anim, 1))
                                {
                                    result = anim;
                                    _equipAnimIds.Add(anim);
                                }

                                break;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"EC tileart {graphic}: {ex.Message}");
                }

                _equipAnims[graphic] = result;
                Log.Trace($"EC equipment 0x{graphic:X4}: anim {legacyAnim} -> {result}");

                return result;
            }
        }

        /// <summary>Whether an animation id came from <see cref="EquipmentAnim"/>: it is equipment.</summary>
        public bool IsEquipmentAnim(int anim)
        {
            lock (_equipAnims)
            {
                return _equipAnimIds.Contains(anim);
            }
        }

        public bool HasBody(int body) =>
            _store.LoadSequence(ArtBody(body)) != null || (_ec != null && _ec.LoadSequence(body) != null);

        /// <summary>
        /// Whether a ridden mount should be drawn from the creature's own body
        /// rather than the 2D client's "ridden" body.
        /// </summary>
        /// <remarks>
        /// The 2D art has dedicated bodies for a mount with its rider baked in
        /// - the horse is 824, not 204 - and tiledata's AnimID points at them.
        /// KR has no such thing: its own body table (GameData.uop) lists 336
        /// creatures and stops at 808, and it ships neither a frame file nor a
        /// sequence for 824, 828, 846 or 848. It does have the mounted rider
        /// actions, so KR composed the pair at runtime from the ordinary
        /// creature model, which is what this restores.
        ///
        /// False whenever KR is off, or KR could draw the ridden body after
        /// all, or it has nothing for the creature either - so the 2D path is
        /// untouched in every case where it was already working.
        /// </remarks>
        public static bool PrefersCreatureBodyForMount(int riddenBody, int creatureBody)
        {
            // KR's store only for the ridden body: EC numbers bodies of its
            // own 826 and 827, with no riding actions, which hid the frenzied
            // ostard (826) and misplaced the rider on the forest ostard (827).
            KrAnimationProvider instance = Instance;

            return instance != null
                && riddenBody != creatureBody
                && instance._store.LoadSequence(riddenBody) == null
                && instance.HasBody(creatureBody);
        }

        /// <summary>
        /// Translates a ClassicUO action index into the KR action to play, for
        /// the numbering scheme the client is driving this body with. Only the
        /// low (animal) scheme differs from KR's; see <see cref="LowToKrAction"/>.
        /// Returns -1 when KR has nothing for it.
        /// </summary>
        public int ResolveAction(int body, int action, AnimationGroups scheme)
        {
            if (scheme != AnimationGroups.Low)
            {
                return ResolveAction(body, action);
            }

            if ((uint)action >= (uint)LowToKrAction.Length)
            {
                return -1;
            }

            foreach (int candidate in LowToKrAction[action])
            {
                int resolved = ResolveAction(body, candidate);

                if (resolved >= 0)
                {
                    return resolved;
                }
            }

            return -1;
        }

        /// <summary>
        /// Resolves the action to play for a body, following KR's own fallback
        /// chain when it ships no art for the requested one. Returns -1 when
        /// nothing in the chain has art, which means "use the 2D animation".
        /// </summary>
        /// <remarks>
        /// The action is a KR action index. Callers holding a ClassicUO index
        /// want the overload that takes the numbering scheme.
        /// </remarks>
        public int ResolveAction(int body, int action) => ResolveIn(_store, ArtBody(body), action);

        private static int ResolveIn(KrAssetStore store, int body, int action)
        {
            KrAnimationSequence sequence = store.LoadSequence(body);

            if (sequence != null)
            {
                int resolved = sequence.ResolveAction(action);

                if (resolved >= 0)
                {
                    return resolved;
                }

                // Only trust the sequence's "no art" verdict when it actually
                // knows the action; a handful of bodies ship art the sequence
                // does not list.
                if (sequence.TryGetAction(action, out _) && !store.HasAnimation(body, action))
                {
                    return -1;
                }
            }

            return store.HasAnimation(body, action) ? action : -1;
        }

        /// <summary>The EC action to play for a people action, or -1 when EC has none.</summary>
        private int ResolveEc(int body, int action)
        {
            int resolved = ResolveIn(_ec, body, action);

            if (resolved < 0 && (uint)action < (uint)EcArmedFallback.Length)
            {
                foreach (int candidate in EcArmedFallback[action])
                {
                    if ((resolved = ResolveIn(_ec, body, candidate)) >= 0)
                    {
                        break;
                    }
                }
            }

            return resolved;
        }

        /// <summary>Playback rate KR specifies for a body/action, or 0 when unknown.</summary>
        public float GetFrameRate(int body, int action, AnimationGroups scheme)
        {
            int resolved = ResolveAction(body, action, scheme);

            if (resolved < 0)
            {
                return 0f;
            }

            KrAnimationSequence sequence = _store.LoadSequence(ArtBody(body));

            return sequence != null && sequence.TryGetAction(resolved, out KrAnimationAction entry)
                ? entry.FramesPerSecond
                : 0f;
        }

        /// <summary>
        /// Decodes one direction of a body/action into the frame structure the
        /// rest of the client already understands. Returns false when KR has no
        /// art, so the caller falls back to the 2D animation.
        /// </summary>
        public bool TryReadFrames(int body, int action, int direction, AnimationGroups scheme, out AnimationsLoader.FrameInfo[] frames) =>
            TryReadFrames(body, action, direction, scheme, out frames, out _);

        /// <param name="scale">The factor these frames are drawn at: <see cref="FrameScale"/>
        /// for KR, <see cref="EcFrameScale"/> for EC.</param>
        public bool TryReadFrames(int body, int action, int direction, AnimationGroups scheme,
            out AnimationsLoader.FrameInfo[] frames, out double scale)
        {
            frames = null;
            scale = FrameScale;

            if ((uint)direction >= (uint)DirectionMap.Length)
            {
                return false;
            }

            // Our own left-hand torch, drawn on KR's human animation (EC's
            // people move differently, so only when people are KR's).
            if (UseOwnHeldLights && UsesOwnTorch(body, action, direction) is { } own)
            {
                frames = own;

                return true;
            }

            if (_ec != null && scheme == AnimationGroups.People)
            {
                int ecAction = ResolveEc(body, action);

                if (ecAction >= 0 && TryDecode(_ec, body, ecAction, direction, out frames))
                {
                    scale = EcFrameScale;

                    // Riding (people actions 23-29): the mount stays KR's,
                    // and EC's rider is drawn for EC's mount. Lower it so its
                    // seat lands where KR's rider's does.
                    if (action is >= 23 and <= 29 && SeatDrop(action, direction) is var drop and not 0)
                    {
                        for (int i = 0; i < frames.Length; i++)
                        {
                            frames[i].CenterY = (short)(frames[i].CenterY - drop);
                        }
                    }

                    return true;
                }
            }

            body = ArtBody(body);
            int resolved = ResolveAction(body, action, scheme);

            return resolved >= 0 && TryDecode(_store, body, resolved, direction, out frames);
        }

        private readonly Dictionary<(int, int, int), AnimationsLoader.FrameInfo[]> _ownTorch = [];

        /// <summary>
        /// Whether a person holding <paramref name="anim"/> should play the
        /// actions our held-light frames cover (see KrHeldLights): true for
        /// the torches, lanterns and candles when people are drawn from KR.
        /// </summary>
        public bool HasOwnTorch(int anim) => UseOwnHeldLights && KrHeldLights.Covers(anim);

        /// <summary>Whether our frames for <paramref name="anim"/> include standing (4).</summary>
        public bool HasOwnStand(int anim) => KrHeldLights.HasStand(anim);

        private AnimationsLoader.FrameInfo[] UsesOwnTorch(int body, int action, int direction)
        {
            if (!KrHeldLights.Covers(body) || !KrHeldLights.HasAction(body, action))
            {
                return null;
            }

            lock (_ownTorch)
            {
                if (_ownTorch.TryGetValue((body, action, direction), out AnimationsLoader.FrameInfo[] cached))
                {
                    return cached;
                }

                KrHeldLights.Frame[] src = KrHeldLights.Get(_store, body, action, direction);
                AnimationsLoader.FrameInfo[] result = null;

                if (src != null)
                {
                    result = new AnimationsLoader.FrameInfo[src.Length];

                    for (int i = 0; i < src.Length; i++)
                    {
                        result[i].Num = i;

                        if (src[i] is { } f)
                        {
                            result[i].Width = (short)f.Width;
                            result[i].Height = (short)f.Height;
                            result[i].CenterX = (short)f.CenterX;
                            // As in TryDecode: the bottom bound below the anchor, negated.
                            result[i].CenterY = (short)(f.CenterY - f.Height);
                            result[i].Pixels = f.Pixels;
                        }
                    }
                }

                _ownTorch[(body, action, direction)] = result;

                return result;
            }
        }

        /// <summary>
        /// The body whose KR frames a body is drawn with. KR's own frames for
        /// body 12 are a small drake (its body list names it
        /// Dragon_RetroGrey_Drake), but the KR client draws a body 12 dragon
        /// with body 59's big dragon, as seen in game (2026-11-02). Where KR
        /// makes the swap was not found: no data file holds it.
        /// The savage ridgeback, 188, has a row in KR's body table (texture
        /// Ridgeback_flame.dds) but no frames or sequence; the KR client
        /// draws it with the ridgeback's, 187 (seen in game, 2026-11-06).
        /// </summary>
        private static int ArtBody(int body) => body switch
        {
            12 => 59,
            188 => 187,
            _ => body
        };

        private readonly Dictionary<(int Action, int Direction), short> _seatDrops = [];

        /// <summary>
        /// How far (in EC's native pixels) to lower an EC rider so the bottom
        /// of its seat lands where KR's rider's does on the same KR mount,
        /// measured on the human body (400). KR's rider, riding, facing
        /// down: frame bottom 40 native px above the anchor (27 px at 2/3);
        /// EC's 43 (40 px at 0.93), so it sat ~13 px high on a KR ostard
        /// (KR's own rider and mount, measured 2026-10-30).
        /// </summary>
        private short SeatDrop(int action, int direction)
        {
            if (_seatDrops.TryGetValue((action, direction), out short drop))
            {
                return drop;
            }

            drop = 0;
            int ecAction = ResolveEc(400, action), krAction = ResolveAction(400, action);

            if (ecAction >= 0 && krAction >= 0
                && TryDecode(_ec, 400, ecAction, direction, out AnimationsLoader.FrameInfo[] ec)
                && TryDecode(_store, 400, krAction, direction, out AnimationsLoader.FrameInfo[] kr)
                && FirstBottom(ec) is short ecBottom && FirstBottom(kr) is short krBottom)
            {
                drop = (short)MathF.Round(ecBottom - (float)(krBottom * FrameScale / EcFrameScale) + (float)(EcRiderDrop / EcFrameScale));
            }

            _seatDrops[(action, direction)] = drop;

            return drop;

            static short? FirstBottom(AnimationsLoader.FrameInfo[] frames)
            {
                foreach (AnimationsLoader.FrameInfo f in frames)
                {
                    if (f.Width > 0)
                    {
                        return f.CenterY;
                    }
                }

                return null;
            }
        }

        private static bool TryDecode(KrAssetStore store, int body, int resolved, int direction, out AnimationsLoader.FrameInfo[] frames)
        {
            frames = null;
            AmouAnimation animation;

            try
            {
                animation = store.LoadAnimation(body, resolved);
            }
            catch (Exception ex)
            {
                Log.Warn($"KR animation {body}/{resolved} failed to load: {ex.Message}");

                return false;
            }

            if (animation == null || !animation.HasRegularDirections)
            {
                return false;
            }

            int krDirection = DirectionMap[direction];
            int count = animation.FramesPerDirection;
            AnimationsLoader.FrameInfo[] result = new AnimationsLoader.FrameInfo[count];

            for (int i = 0; i < count; i++)
            {
                if (!animation.TryGetFrameInfo(krDirection, i, out AmouFrameInfo info) || info.IsEmpty)
                {
                    result[i].Num = i;

                    continue;
                }

                AmouFrameBitmap bitmap;

                try
                {
                    // Native resolution, deliberately. The 2/3 resample that
                    // used to happen here is now applied when the sprite is
                    // drawn (see SpriteInfo), so the atlas holds KR's real
                    // pixels and the camera's zoom and the KR scale compose
                    // into one sampling step instead of two.
                    bitmap = animation.Decode(info);
                }
                catch (Exception ex)
                {
                    Log.Warn($"KR animation {body}/{resolved} dir {krDirection} frame {i}: {ex.Message}");
                    result[i].Num = i;

                    continue;
                }

                result[i].Num = i;
                result[i].Width = (short)bitmap.Width;
                result[i].Height = (short)bitmap.Height;
                result[i].CenterX = (short)bitmap.CenterX;
                // FrameInfo.CenterY is not the anchor's distance from the top of
                // the frame: the renderer draws the frame's top edge at
                // y - (Height + CenterY), so CenterY is the *bottom* bound
                // relative to the anchor, negated. KR gives the anchor as a
                // distance down from the top (-y1), so subtract the height to
                // turn it into -y2. CenterX needs no such adjustment: both
                // clients store it as -x1. Both are in KR's own pixels; the
                // renderer scales the bounds on its way to screen pixels in
                // SpriteInfo.Create, which is where the 2/3 now lives.
                result[i].CenterY = (short)(bitmap.CenterY - bitmap.Height);
                result[i].Pixels = bitmap.Pixels;
            }

            frames = result;

            return true;
        }

        public void Dispose()
        {
            _store.Dispose();
            _ec?.Dispose();
        }
    }
}
