// SPDX-License-Identifier: BSD-2-Clause
//
// KR particle effects in the world (kr_particle_effects). A 0xC7 packet's KR
// effect id names a scene; .ems scripts and NIF particle scenes are
// simulated (EmsEffect, NifEffect) and drawn as billboards over the world.
//
// Placement is a *hypothesis*: the
// script's origin is the effect's anchor on the ground - an attached effect
// follows its mobile's feet - and one script unit is one tile edge, so x and
// y step like tiles (22 px across and down per unit) and z rises 32 px per
// unit. generic01's hand glow spawns 1.25-2 units up: 40-64 px, about hand
// height on a 2D mobile.

using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.GameObjects;
using ClassicUO.KR.Effects;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Managers
{
    internal static class KrParticles
    {
        private const float TilePx = 22f;
        private const float HeightPx = 32f;
        // Judged against the KR client's wisp, whose sparkles are 25-35 px:
        // a particle's size in units times this, in pixels.
        private const float SizePx = 20f;

        private sealed class Active
        {
            public IKrEffect Effect;
            public uint Serial;

            /// <summary>A creature's scene (the wisp, the Fire Elemental's fireball).</summary>
            public bool Body;

            /// <summary>Where a creature's scene is centred, in pixels from its feet (FramedBodySceneScale).</summary>
            public Vector2 BodyShift;

            /// <summary>Its additive particles screened, as KR draws the Water Elemental's.</summary>
            public bool BodyScreen;
            public ushort X, Y;
            public sbyte Z;
            public float Lift;

            /// <summary>Positions multiplied by this; 1 for effects, more for weather.</summary>
            public float Spread = 1f;
            public uint LastSeen;

            /// <summary>A scene's textured meshes (waterfalls), drawn into this each frame.</summary>
            public Texture2D Mesh;

            /// <summary>Particle size multiplier; 1 for effects (SizePx), more for static scenes.</summary>
            public float SizeScale = 1f;

            /// <summary>
            /// A static's .ems script (torchflame.ems on a wall torch): its
            /// tileart scale (0.5) on sizes and positions, and its height
            /// offset (-7) at 4 px a unit, as the NIF scenes' placement has.
            /// </summary>
            public float ScriptScale = 1f;
            public Vector2 ScriptOffset;
            public string Scene;
            public ushort Graphic;

            /// <summary>A static scene's tileart placement: its own record's (KrStaticProvider.ScenesFor).</summary>
            public KrStaticProvider.SceneRecord Placement;
            public Vector2 MeshOrigin;
            public uint MeshTicks;

            /// <summary>A script's mesh (lightning.nif's bolt), at MeshScale world units a scene unit.</summary>
            public bool MeshUp;
            public float MeshScale;

            /// <summary>The 2D effect's light graphic and how long it shone (s); 0 for none.</summary>
            public ushort LightGraphic;
            public float LightUntil;

            /// <summary>A definition part's duration (s), after which KR drops it; 0 to run until it ends.</summary>
            public float Until;
            public float Age;

            /// <summary>A moving effect's scene: anchored where the effect is drawn this frame.</summary>
            public bool Moving;

            /// <summary>A moving effect's screen direction of travel, radians.</summary>
            public float MoveAngle;
            public Vector2 MoveAnchor;
            public float MoveDistance;
        }

        /// <summary>ONE, ONE: an additive scene mesh's image (premultiplied, its alpha unused), as KR adds it.</summary>
        private static readonly BlendState OneOneBlend = new()
        {
            ColorSourceBlend = Blend.One,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.One,
            AlphaDestinationBlend = Blend.One
        };

        /// <summary>INVDESTCOLOR, ONE: a screened scene mesh's image (KrBlend.Screen), lightening only.</summary>
        private static readonly BlendState ScreenBlend = new()
        {
            ColorSourceBlend = Blend.InverseDestinationColor,
            ColorDestinationBlend = Blend.One,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One
        };

        private static readonly BlendState MultiplyBlend = new()
        {
            ColorSourceBlend = Blend.Zero,
            ColorDestinationBlend = Blend.SourceColor,
            AlphaSourceBlend = Blend.Zero,
            AlphaDestinationBlend = Blend.One,
        };

        private static readonly List<Active> _active = [];
        private static readonly Dictionary<string, Texture2D> _textures = new(StringComparer.OrdinalIgnoreCase);
        private static uint _lastTicks;
        private static int _seed;

        public static bool Enabled =>
            Settings.GlobalSettings.KrParticleEffects && KrEffectProvider.IsActive;

        /// <summary>Effects playing, not counting statics' scenes (torch flames).</summary>
        public static int EffectCount => _active.FindAll(a => a.Graphic == 0).Count;

        /// <summary>
        /// Starts the KR effect <paramref name="definitionId"/> attached to
        /// <paramref name="serial"/> (or at a tile when it is not a mobile).
        /// False when it is not an effect this can play.
        /// </summary>
        public static bool TrySpawn(int definitionId, uint serial, ushort x, ushort y, sbyte z, byte layer = 255,
            ushort lightGraphic = 0, int lightDuration = 0)
        {
            if (!Enabled || definitionId <= 0)
            {
                return false;
            }

            int first = _active.Count;

            if (SpawnParts(definitionId, serial, x, y, z))
            {
                GiveLight(first, lightGraphic, lightDuration);

                return true;
            }

            IKrEffect effect = KrEffectProvider.Instance.Create(definitionId, unchecked(++_seed * 7919));

            if (effect == null)
            {
                return false;
            }

            _active.Add(new Active
            {
                Effect = effect,
                Serial = serial,
                X = x,
                Y = y,
                Z = z,
                Lift = LayerLift(layer)
            });
            GiveLight(first, lightGraphic, lightDuration);

            return true;
        }

        /// <summary>
        /// The light the 2D effect would have given (GameEffectView: a graphic
        /// tiledata marks as a light, on the mobile or item it plays on, for
        /// its duration - packet duration x ITEM_EFFECT_ANIMATION_DELAY ms),
        /// kept by the scene played in its place, as by its first part.
        /// </summary>
        private static void GiveLight(int index, ushort graphic, int duration)
        {
            if (graphic > 1 && duration > 0 && index < _active.Count
                && Client.Game.UO.FileManager.TileData.StaticData[graphic].IsLight)
            {
                _active[index].LightGraphic = graphic;
                _active[index].LightUntil = duration * Constants.ITEM_EFFECT_ANIMATION_DELAY / 1000f;
            }
        }

        /// <summary>Adds an effect's light (GiveLight) at its mobile or item, as GameEffectView does.</summary>
        private static void AddLight(World world, Active a)
        {
            if (a.LightGraphic == 0 || a.Age >= a.LightUntil || a.Serial == 0
                || world.Get(a.Serial) is not Entity source || Client.Game.GetScene<Scenes.GameScene>() is not { } scene)
            {
                return;
            }

            scene.AddLight(source, source,
                source.RealScreenPosition.X + (int)source.Offset.X + 22,
                source.RealScreenPosition.Y + (int)(source.Offset.Y - source.Offset.Z) + 22);
        }

        /// <summary>KR's world unit (a tile edge) in client pixels: a tile step's 22 px over KR's 0.7064.</summary>
        private const float KrUnitPx = 22f / 0.7063993f;

        /// <summary>
        /// A spell effect's NIF scenes as its definition record lists them
        /// (KrEffects.Parts), the way KR drew Heal, Greater Heal,
        /// Reactive Armor and Clumsy: every part, each listed copy too; one
        /// scene unit a world unit times the part's scale; a particle's quad
        /// as wide as its diameter; anchored the part's height (z units, 4 px
        /// each) above the feet whatever layer the packet named; dropped at
        /// the part's duration. False when the record has no NIF part.
        /// </summary>
        private static bool SpawnParts(int definitionId, uint serial, ushort x, ushort y, sbyte z)
        {
            KrEffects effects = KrEffectProvider.Instance.Effects;

            IReadOnlyList<KrEffectPart> parts = effects.PartsForServerEffect(definitionId);

            // No record (the moongate's 8149): KR plays record 0, its
            // placeholder - blue sparks rising from hand height
            // (Placeholder01.ems; "03002006_pink.tga" is blue), what it
            // showed for 8149 where the client had played 00008148_MoonGate.nif.
            if (parts.Count == 0)
            {
                parts = effects.Parts(0);
            }

            return SpawnParts(parts, serial, x, y, z);
        }

        /// <summary>
        /// Plays definition record <paramref name="record"/> itself, not a
        /// server effect id's (N - 1): KR plays record 666, lightning.ems, for
        /// the 0x70 / 0xC0 lightning bolt.
        /// </summary>
        public static bool TrySpawnRecord(int record, uint serial, ushort x, ushort y, sbyte z) =>
            Enabled && SpawnParts(KrEffectProvider.Instance.Effects.Parts(record), serial, x, y, z);

        private static bool SpawnParts(IReadOnlyList<KrEffectPart> parts, uint serial, ushort x, ushort y, sbyte z)
        {
            bool any = false;
            KrEffects effects = KrEffectProvider.Instance.Effects;

            foreach (KrEffectPart part in parts)
            {
                if (part.Scale <= 0f || effects.Create(part.Scene, unchecked(++_seed * 7919)) is not { } effect)
                {
                    continue;
                }

                // A script (.ems) takes the part's scale over its own units
                // - z up, x and y on the ground, as the client draws scripts -
                // and its particles' sizes as a scene's: Invisibility's bubbles
                // start 5-6 units up, out of sight unscaled; at its 0.35 and
                // height 8 they fall from above the head to the body as KR's.
                bool script = effect is EmsEffect;

                _active.Add(new Active
                {
                    Effect = effect,
                    Serial = serial,
                    X = x,
                    Y = y,
                    Z = z,
                    // From the tile's top corner, 22 px over its centre: KR
                    // puts the character and its effect at one world point,
                    // (x, -y), which its camera shows half a tile step up.
                    // A trace of Agility (2026-11-03) drew the ring there, at
                    // the feet; from the centre ours sat ~22 px below them.
                    Lift = (part.Height * 4f) + TilePx,
                    Spread = script ? part.Scale : KrUnitPx / (31f * NifEffect.PositionScale) * part.Scale,
                    SizeScale = 2f * KrUnitPx / SizePx * part.Scale,
                    ScriptScale = 2f * KrUnitPx / SizePx * part.Scale,
                    Until = part.Duration
                });
                any = true;

                if (script)
                {
                    SpawnScriptMeshes(effects, part, serial, x, y, z);
                }
            }

            return any;
        }

        /// <summary>
        /// A script's mesh sequences (type = mesh, meshName): lightning.ems
        /// stands lightning.nif - three 2 x 8 unit quads, their lightning
        /// texture scrolling - on the target at its initial scale (10) times
        /// the part's (0.1), for the sequence's lifetime: a bolt 8 tile edges
        /// tall, standing up from the anchor (its quads stand along z).
        /// </summary>
        private static void SpawnScriptMeshes(KrEffects effects, KrEffectPart part, uint serial, ushort x, ushort y, sbyte z)
        {
            if (effects.Script(part.Scene) is not { } script)
            {
                return;
            }

            foreach (EmsSequence seq in script.Sequences)
            {
                if (!string.Equals(seq.Text("type")?.Trim(), "mesh", StringComparison.OrdinalIgnoreCase)
                    || seq.Text("meshname") is not { Length: > 0 } mesh
                    || effects.Create(mesh, unchecked(++_seed * 7919)) is not NifEffect nif)
                {
                    continue;
                }

                // Its quads are drawn as a mesh, not as square sprites - its
                // first one only, as KR drew one of lightning.nif's three.
                nif.PlanesAsSprites = false;
                nif.FirstShapeOnly = true;
                Random rng = new(_seed);
                float scale = seq.Channels.TryGetValue("scale", out List<EmsKey> keys) && keys.Find(k => k.At is null && !k.Final) is { } first
                    ? first.Value.Number(rng)
                    : 1f;

                _active.Add(new Active
                {
                    Effect = nif,
                    Serial = serial,
                    X = x,
                    Y = y,
                    Z = z,
                    Lift = part.Height * 4f,
                    MeshUp = true,
                    MeshScale = scale * part.Scale,
                    Until = seq.Number("lifetime", rng, 1f)
                });
            }
        }

        /// <summary>
        /// How far above the feet an effect on a mobile is anchored, by the
        /// 0xC7 packet's layer (RunUO's EffectLayer: 0 head, 1 right hand,
        /// 2 left hand, 3 waist, 4 left foot, 5 right foot, 7 centre feet,
        /// 255 none). Spells send their casting effect on the hands, and KR
        /// centres it there - drawn at the feet, Magic Arrow's ring of flame
        /// sat below the caster. Heights are judged on a 2D mobile.
        /// </summary>
        private static float LayerLift(byte layer) => layer switch
        {
            0 => 60f,
            1 or 2 => 35f,
            3 => 30f,
            _ => 0f
        };

        private static readonly Dictionary<uint, Active> _bodies = [];
        /// <summary>
        /// A scene running with a creature's frames, against the wisp's scale
        /// (1, 1). The scenes are authored at wildly different scales - KR's
        /// world matrix for them was 0.284 for the Fire Elemental, 0.0131 for
        /// the Water - so each is measured from a trace:
        /// Fire (10000015): systems spread 25-50 px over the ground, quads
        /// up to 15 px whose additive overlap is the one bright ball.
        /// Water (10000016): a ring ~75 px wide of ~10 px flares round its
        /// feet; at the fire's scale it threw flares the size of the screen,
        /// and the scene's own coordinates sit off its origin, so it is
        /// moved up onto the feet.
        /// </summary>
        private static (float Spread, float Size, Vector2 Shift) FramedBodySceneScale(ushort body) => body switch
        {
            15 => (4.5f, 7f, Vector2.Zero),
            16 => (0.034f, 0.1f, new Vector2(0f, -10f)),
            _ => (1f, 1f, Vector2.Zero)
        };

        /// <summary>
        /// Called each frame a mobile would be drawn: keeps the scene KR
        /// gives its body running on it, and returns true when that scene is
        /// all KR shows (the wisp), so the sprite is not drawn. A creature KR
        /// animates keeps its sprite and runs its scene as well (the Fire
        /// Elemental's fireball).
        /// </summary>
        public static bool DrawsBody(Mobile mobile)
        {
            if (!Enabled || KrEffectProvider.Instance.Effects.SceneForBody(mobile.Graphic, withFrames: true) is null)
            {
                return false;
            }

            if (!_bodies.TryGetValue(mobile.Serial, out Active a))
            {
                IKrEffect effect = KrEffectProvider.Instance.CreateForBody(mobile.Graphic, unchecked(++_seed * 7919));

                if (effect == null)
                {
                    return false;
                }

                (float spread, float size, Vector2 shift) = FramedBodySceneScale(mobile.Graphic);
                _bodies[mobile.Serial] = a = new Active
                {
                    Effect = effect, Serial = mobile.Serial, Body = true, Spread = spread, SizeScale = size, BodyShift = shift,
                    BodyScreen = mobile.Graphic == 16
                };
            }

            a.LastSeen = Time.Ticks;

            return !KrEffectProvider.Instance.Effects.HasFrames(mobile.Graphic);
        }

        private static readonly Dictionary<GameEffect, Active> _moving = [];

        // A moving effect's scene, from KR's Fireball
        //: the particles there
        // are 20-75 px on screen (core to flame trail), where the scene's own
        // sizes are 0.1-0.2 units - about 13 times the effect constant.
        // Judged against KR; tune by eye against KR.
        private const float MovingSizeScale = 13f;

        // KR's fireball flies 11-12 px above the ground at both
        // ends (0.37 units at KR's 31 px a unit) and arcs 14 px higher at
        // the middle of its flight.
        private const float MovingLift = 12f;
        private const float MovingArc = 14f;

        /// <summary>Pixels a unit of length for a flying script's axial particles (arrow.ems).</summary>
        private const float MovingAxialPx = 2f;

        /// <summary>
        /// Called each frame a moving effect would be drawn, at its screen
        /// position: plays the KR scene numbered by its graphic along its
        /// path (Fireball 0x36D4 -> 00014036_LargeFireball.nif). The scene's
        /// emitters ride the effect and leave their particles in world space,
        /// so they trail behind it. True when KR draws the effect, in which
        /// case the 2D sprite is skipped.
        /// </summary>
        /// <param name="target">The target's screen position, for the arc.</param>
        public static bool KeepMoving(GameEffect effect, Vector2 screen, Vector2 target)
        {
            if (!Enabled || effect is not MovingEffect)
            {
                return false;
            }

            if (!_moving.TryGetValue(effect, out Active a))
            {
                // By its number first (Fireball's 0x36D4), else the script its
                // tileart names: an arrow 0x0F42 flies as arrow.ems, the
                // sparkle 0x374B as 00014154_sparkle.nif, in KR and EC alike.
                if ((KrEffectProvider.Instance.Effects.SceneForGraphic(effect.Graphic)
                        ?? KrStaticProvider.Instance?.SceneFor(effect.Graphic, out _)) is not { } scene
                    || KrEffectProvider.Instance.Effects.Create(scene, unchecked(++_seed * 7919)) is not { } created)
                {
                    return false;
                }

                // A long flight outlasts the scene's emitters (2.67 s).
                if (created is NifEffect nif)
                {
                    nif.Loop = true;
                }

                // A script (arrow.ems) shows from the moment it is fired.
                if (created is EmsEffect script)
                {
                    script.Continuous = true;
                    script.Prime();
                }

                _moving[effect] = a = new Active
                {
                    Effect = created, Scene = scene, Moving = true, SizeScale = MovingSizeScale,
                    MoveDistance = Math.Max(1f, Vector2.Distance(screen, target))
                };
            }

            float progress = Math.Clamp(1f - (Vector2.Distance(screen, target) / a.MoveDistance), 0f, 1f);
            a.MoveAnchor = screen - new Vector2(0, MovingLift + (MovingArc * MathF.Sin(progress * MathF.PI)));
            a.LastSeen = Time.Ticks;

            if (Vector2.DistanceSquared(screen, target) > 1f)
            {
                a.MoveAngle = MathF.Atan2(target.Y - screen.Y, target.X - screen.X);
            }

            return true;
        }

        private static readonly Dictionary<long, Active> _statics = [];

        /// <summary>
        /// Called each frame a static would be drawn: keeps the looping KR
        /// scene its tileart names running on its tile (swamp bubbles).
        /// True when KR shows only the scene there, so the sprite is skipped.
        /// </summary>
        public static bool KeepStatic(ushort graphic, ushort x, ushort y, sbyte z) =>
            KeepScene(graphic, x, y, z, blankWithoutScene: true);

        /// <summary>
        /// As <see cref="KeepStatic"/>, for a ground item - the shrine's
        /// sparkles (0x373A-0x3779) come from the server: true only where KR
        /// shows the scene instead of the sprite. A blank sprite with no scene
        /// keeps its 2D art (the death vortex 0x3798).
        /// </summary>
        public static bool KeepItem(ushort graphic, ushort x, ushort y, sbyte z) =>
            KeepScene(SceneGraphic(graphic), x, y, z, blankWithoutScene: false);

        /// <summary>
        /// The graphic whose scene a server item plays. A north-south poison
        /// field is placed as 0x3922 (RunUO, ModernUO), a later frame of its
        /// animation; KR and EC keep the field's scene on its first, 0x3920.
        /// </summary>
        private static ushort SceneGraphic(ushort graphic) => graphic == 0x3922 ? (ushort)0x3920 : graphic;

        /// <summary>
        /// A static's script whose additive particles keep their alpha: a
        /// forge's coals01.ems, which KR adds at about 2% (vertex colour
        /// 06050505, Tokuno 766,1260) - faint specks in the basin, where at
        /// full strength ours glowed as blobs over the forge's rims.
        /// </summary>
        private static bool FadedScript(Active a) =>
            a.Scene != null && a.Scene.Equals("coals01.ems", StringComparison.OrdinalIgnoreCase);

        private static readonly HashSet<string> EffectOnlyScripts = new(StringComparer.OrdinalIgnoreCase)
        {
            "arrow.ems"
        };

        private static bool KeepScene(ushort graphic, ushort x, ushort y, sbyte z, bool blankWithoutScene)
        {
            // kr_light_sources_2d: no KR flame on a light source; its 2D
            // animation is drawn instead.
            if (!Enabled || KrStaticProvider.Instance is not { } provider || Map.ChunkMesh.LightAs2D(graphic))
            {
                return false;
            }

            // A blank KR sprite with no scene draws nothing: 0x350C shares
            // its tile with 0x350E, whose own art is the KR waterfall.
            if (provider.SceneFor(graphic, out bool blank) is null)
            {
                return blank && blankWithoutScene;
            }

            // Every scene in the static's tileart group: a waterfall's water,
            // its highlights, drops and mist.
            IReadOnlyList<KrStaticProvider.SceneRecord> records = provider.ScenesFor(graphic);

            // A spell or projectile graphic lying in the world: its tileart
            // names the effect's script, which KR plays only for the effect.
            // An arrow (0x0F42 at Trammel 3054,3389) wobbled in the air; a
            // flame strike column (0x3709 at 5336,1304, z 29) burned where KR
            // draws nothing, its KR sprite being blank.
            if (!blankWithoutScene && records.Count > 0 && EffectOnlyScripts.Contains(records[0].Scene))
            {
                return blank;
            }

            for (int i = 0; i < records.Count; i++)
            {
                KeepRecord(provider, graphic, x, y, z, i, records[i]);
            }

            return blank;
        }

        private static void KeepRecord(KrStaticProvider provider, ushort graphic, ushort x, ushort y, sbyte z, int index, KrStaticProvider.SceneRecord record)
        {
            string scene = record.Scene;
            long key = StaticKey(graphic, x, y, z) | ((long)index << 56);

            // A script (bees.ems) plays once; a static's keeps playing.
            if (_statics.TryGetValue(key, out Active running) && running.Effect is not NifEffect && running.Effect.Finished)
            {
                _statics.Remove(key);
            }

            if (!_statics.TryGetValue(key, out Active a))
            {
                if (KrEffectProvider.Instance.Effects.Create(scene, unchecked(++_seed * 7919)) is not { } effect)
                {
                    return;
                }

                if (effect is NifEffect nif)
                {
                    nif.Loop = true;
                    nif.RestartWhenFinished = true;
                    nif.PlanesAsSprites = false;

                    // In step with the same scene elsewhere, as KR plays them:
                    // the Crystal Cave barrier's five paralysis fields flash
                    // together there, but each started when it came into view
                    // here, out of step.
                    if (nif.LoopLength > 0f)
                    {
                        nif.Update((float)(Time.Ticks / 1000.0 % nif.LoopLength));
                    }
                }

                // A static scene's particle sizes at 31 px a scene unit (one
                // tile step), judged against a KR recording of the sparkle
                // shrine: at the effect constant the glow ball came out half
                // KR's 25 px.
                // Positions keep the effect scale: in KR's recording the red
                // sparks spread 60-100 px round the glow, as ours did; scaling
                // them by the tileart scale (0.35) shrank the whole effect.
                const float unitPx = 31f;

                // A NIF scene's particles: a quad's edge is the particle's
                // diameter, at the tileart scale - KR's the Crystal
                // Cave barrier (scale 0.6) draws a static's particles through
                // world = 0.6 x I, sizes and travel alike, as spell parts are
                // (2 x KrUnitPx x part scale).
                _statics[key] = a = new Active
                {
                    Effect = effect, X = x, Y = y, Z = z, Scene = scene, Graphic = graphic, Placement = record,
                    SizeScale = effect is NifEffect ? 2f * unitPx / SizePx * record.Scale : unitPx / SizePx
                };

                if (effect is EmsEffect script)
                {
                    script.Continuous = true;
                    script.SizeMultiplier = record.Size;
                }

                if (effect is not NifEffect)
                {
                    (float scriptX, float scriptY, float scriptHeight, float scriptScale) = (record.X, record.Y, record.Height, record.Scale);
                    // A script's size is its quad's edge in world units, at
                    // the tileart scale: KR's a waterfall's
                    // splash01.ems mist (scale 0.5, sizes 2 growing to 5-10)
                    // drew quads 1.3-2.6 units wide. At 20 px a size unit it
                    // was too small; at twice this it covered the falls.
                    a.ScriptScale = KrUnitPx / SizePx * scriptScale;
                    // A forge's coals spread about a third as far: KR keeps each
                    // system within ~20 x 22 px of the basin (Tokuno
                    // 766,1260), where ours reached over its rims.
                    a.Spread = scene.Equals("coals01.ems", StringComparison.OrdinalIgnoreCase) ? scriptScale * 0.35f : scriptScale;
                    // From the tile anchor, lowered by the tileart height at
                    // 4 px a unit: -16 - 4 x height. The anchor is the tile
                    // point, 22 px below the sprite's own position.
                    // - Wall torch (torchflame.ems, height -7): +12. Judged
                    //   against recordings at +8 (-16 left the flame ~38 px
                    //   above the torch head, +22 ~23 below, 0 slightly high).
                    // - Forge (coals01.ems, height 0.01): -16. KR
                    //   put the coals ~34 px above the forge sprite's bottom,
                    //   in its fire bed. A fixed +8 left them ~14 px above it,
                    //   on the forge's front wall.
                    // Both fit within a few pixels.
                    // The tileart's (x, y) moves it along the tile axes, 22 px
                    // a unit: a candle KR draws off-centre on its stick
                    // (0x142C, x -0.3) had its flame 7 px right of KR's.
                    a.ScriptOffset = new Vector2(
                        (scriptX - scriptY) * TilePx,
                        ((scriptX + scriptY) * TilePx) - 16f - (4f * scriptHeight));
                }
            }

            a.LastSeen = Time.Ticks;
        }

        private static long StaticKey(ushort graphic, ushort x, ushort y, sbyte z) =>
            ((long)graphic << 40) | ((long)x << 24) | ((long)y << 8) | (byte)z;

        /// <summary>
        /// Draws the textured meshes of a static's KR scene - the sheet of
        /// water in 00013549_waterfall.nif, its texture scrolling - centred on
        /// the static's tile, in the effects pass: drawn with the statics, the
        /// shrine's rock blocks on nearer tiles covered the falls, which KR
        /// shows over them.
        /// The mesh is drawn into a small texture about 30 times a second.
        /// Scene space is Gamebryo's, z up, projected as the client's iso
        /// (x - y across); the mirrored projection leaned the sheets "\\",
        /// against KR's "/". MeshPx (pixels a scene unit) is judged against
        /// the 2D waterfall art (44x66).
        /// </summary>
        /// <summary>
        /// A static's scene in the world's depth order, just after the static
        /// itself (a chunk-mesh static: View.CalculateDepthZ + 0.5, its
        /// priority z + 1 for its height). Tested against the depth the world
        /// wrote, so whatever is drawn in front of the static covers its
        /// flame - the pavers over Britain's wall torch at 1510,1638, the
        /// cedar in front of the Cove palisade torch at 2284,1213 - and what
        /// is behind does not, as the walkway behind the palisade's torches.
        /// </summary>
        /// <summary>
        /// A static's scene with a drawn floor above it on its own tile. KR's
        /// depth is nearly all height (its camera looks almost straight
        /// down), so a floor over a torch hides its smoke; ours sorts by
        /// x + y first, and the smoke of a lower storey's torches and a
        /// waterfall's mist rose through the top floor of the Ilshenar tower
        /// at 565,432. Floors at or above the roof cut-off (maxZ) are hidden
        /// with the roofs and cover nothing.
        /// </summary>
        /// <summary>
        /// A scene static raised at least 10 z over another static on its tile.
        /// The Tokuno falls at 798,1252-1255 repeat the upper falls' 0x34F3 at
        /// z 39 over the lower falls and a rock (z 25-26); KR draws
        /// nothing of them, and their particles poured a stream down over the
        /// bridge. Its z 27 copy at 796,1248, alone, KR does draw.
        /// </summary>
        private static bool RaisedOverAnother(World world, Active a)
        {
            // Only a waterfall's scenes (its sheet, and its particle-only
            // 03013555 mist): a wall torch (Trammel 1445,3866) and the
            // braziers on the Ilshenar tower (566,432) stand as high over
            // what is under them, and lost their flames.
            if (a.Scene == null || !a.Scene.Contains("waterfall", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            for (GameObject o = world.Map.GetTile(a.X, a.Y, false); o != null; o = o.TNext)
            {
                if (o is Static st && st.Z <= a.Z - 10)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool UnderFloor(World world, Active a, int maxZ)
        {
            for (GameObject o = world.Map.GetTile(a.X, a.Y, false); o != null; o = o.TNext)
            {
                if (o.Z <= a.Z || o.Z >= maxZ || !o.AllowedToDraw)
                {
                    continue;
                }

                // Not land: a cliff's land stands over the waterfall pieces on
                // its tiles (Trammel 1328,539), whose falls then went missing.
                // Floors, not roofs: a wall torch under the eaves (Trammel
                // 2269,1216) lost its flame to the roof over its tile.
                if (o is Static st && st.ItemData.IsSurface && !st.ItemData.IsRoof
                    || o is Multi m && m.ItemData.IsSurface && !m.ItemData.IsRoof)
                {
                    return true;
                }
            }

            return false;
        }

        private static float StaticDepth(Active a) => (a.X + a.Y) + ((127 + a.Z + 2) * 0.01f) + 0.5f;

        /// <summary>
        /// A particle of a static's scene, in pixels from its tile's centre,
        /// placed as the meshes are, at the tileart scale - the Tokuno oven's
        /// spark emitter, 14 units out at 0.1, threw sparks two tiles away
        /// unscaled. The particle's travel takes it too, as KR's the
        /// Crystal Cave barrier shows; it was left unscaled once, judged by eye
        /// against a video of the red sparkle (0.35), with sizes half KR's.
        /// </summary>
        private static Vector2 StaticScenePoint(Active a, System.Numerics.Vector3 scene, System.Numerics.Vector3 emitter)
        {
            (float sceneHeight, float sceneScale) = (a.Placement.Height, a.Placement.Scale);
            float shift = -(a.Z + sceneHeight) / 11f;
            // The whole system at the tileart scale, as KR's the
            // Crystal Cave barrier draws it (world = scale x I).
            System.Numerics.Vector3 placed = scene * sceneScale;
            System.Numerics.Vector3 world = System.Numerics.Vector3.Transform(placed, KrFacing)
                + new System.Numerics.Vector3(shift, 0f, -shift);
            System.Numerics.Vector2 p = KrScreen(world);

            return new Vector2(p.X, p.Y);
        }

        /// <summary>
        /// A static scene's particle sorted by the ground point it is over, as
        /// KR's 3D depth does: the Moonglow fountain's droplets (its scene on
        /// 0x173E, at the basin's edge) fall into the basin, whose nearer
        /// pieces covered them at the static's own depth. Only ever brought
        /// forward of the static's depth (StaticDepth), never behind it.
        /// </summary>
        private static float StaticSceneDepth(Active a, System.Numerics.Vector3 scene)
        {
            System.Numerics.Vector3 ground = System.Numerics.Vector3.Transform(scene * a.Placement.Scale, KrFacing);

            // KR's world: x along the tile x axis, z along minus tile y.
            return (a.X + ground.X + a.Y - ground.Z) + ((127 + a.Z + 2) * 0.01f) + 0.5f;
        }

        private static void DrawMesh(UltimaBatcher2D batcher, Active a, Vector2 centre, float depth)
        {
            if (a.Effect is not NifEffect nif || !nif.HasMeshes || a.Graphic == 0)
            {
                return;
            }

            // Placement, fitted to three scenes: from the tile's
            // corner, shifted along the ground diagonal by (z + tileart
            // height) / 11 tiles - KR's camera looks almost straight down, so
            // height shows as a shift south, 4 px a z unit as in 2D.
            (float sceneHeight, float sceneScale) = (a.Placement.Height, a.Placement.Scale);
            System.Numerics.Matrix4x4 facing = KrFacing * System.Numerics.Matrix4x4.CreateScale(sceneScale);
            float shift = -(a.Z + sceneHeight) / 11f;
            System.Numerics.Vector3 place = new(shift, 0f, -shift);

            uint now = Time.Ticks;

            // One image for every copy of a scene at the same placement: they
            // play in step, so they look the same. A waterfall is ~100 tiles
            // of a few scenes; drawn per tile on the CPU 30 times a second,
            // the game slowed whenever one was in view.
            (string, KrStaticProvider.SceneRecord, sbyte) key = (a.Scene, a.Placement, a.Z);

            if (!_sharedMeshes.TryGetValue(key, out SharedMesh shared))
            {
                _sharedMeshes[key] = shared = new SharedMesh();
            }

            shared.LastUsed = now;

            if (!shared.Rendered || now - shared.Ticks >= 33)
            {
                shared.Ticks = now;
                shared.Rendered = true;
                uint[] pixels = nif.RenderMeshes(
                    name => KrEffectProvider.Instance.Effects.Texture(name),
                    v => KrScreen(System.Numerics.Vector3.Transform(v, facing) + place),
                    out int w, out int h, out System.Numerics.Vector2 origin);

                // Nothing visible: drop the last image, or it was drawn again
                // until the next refresh - the Crystal Cave barrier's thin last
                // arcs kept flickering through the dark between its flashes.
                if (pixels == null)
                {
                    shared.Texture?.Dispose();
                    shared.Texture = null;
                }
                else
                {
                    // Premultiplied already (NifEffect.RenderMeshes), as sprites
                    // here are.
                    if (shared.Texture == null || shared.Texture.Width != w || shared.Texture.Height != h)
                    {
                        shared.Texture?.Dispose();
                        shared.Texture = new Texture2D(batcher.GraphicsDevice, w, h, false, SurfaceFormat.Color);
                    }

                    shared.Texture.SetData(pixels);
                    shared.Origin = new Vector2(origin.X, origin.Y);
                    shared.Additive = nif.LastMeshesAdditive;
                    shared.Screen = nif.LastMeshesScreen;
                }
            }

            if (shared.Texture == null)
            {
                return;
            }

            if (shared.Additive || shared.Screen)
            {
                batcher.SetBlendState(shared.Screen ? ScreenBlend : OneOneBlend);
            }

            batcher.Draw(
                shared.Texture,
                centre + shared.Origin,
                null,
                new Vector3(0, ShaderHueTranslator.SHADER_NONE, 1f),
                0f,
                Vector2.Zero,
                Vector2.One,
                SpriteEffects.None,
                depth);

            if (shared.Additive || shared.Screen)
            {
                batcher.SetBlendState(null);
            }
        }

        private sealed class SharedMesh
        {
            public Texture2D Texture;
            public Vector2 Origin;
            public uint Ticks, LastUsed;
            public bool Rendered, Additive, Screen;
        }

        private static readonly Dictionary<(string, KrStaticProvider.SceneRecord, sbyte), SharedMesh> _sharedMeshes = [];

        /// <summary>
        /// A spell effect's textured meshes - Reactive Armor's golden rings,
        /// spun, grown by their morphs and faded by their alpha controllers -
        /// projected as its particles are (NifEffect.ScreenUnits, the part's
        /// scale through Spread), from the effect's anchor.
        /// </summary>
        private const float EffectMeshScale = 2f;

        private static void DrawEffectMesh(UltimaBatcher2D batcher, Active a, Vector2 anchor, float depth)
        {
            if (a.Effect is not NifEffect { HasMeshes: true } nif)
            {
                return;
            }

            uint now = Time.Ticks;

            if (a.Mesh == null || now - a.MeshTicks >= 33)
            {
                a.MeshTicks = now;
                // At half resolution, drawn twice the size: the meshes are
                // soft glows (unhide's sphere), rasterised on the CPU, and
                // hide/unhide's 20 parts cost ~100 ms a frame at full size.
                float px = (a.MeshUp ? KrUnitPx * a.MeshScale : 31f * NifEffect.PositionScale * a.Spread) / EffectMeshScale;
                uint[] pixels = nif.RenderMeshes(
                    name => KrEffectProvider.Instance.Effects.Texture(name),
                    v => nif.ScreenUnits(v) * px,
                    out int w, out int h, out System.Numerics.Vector2 origin);

                if (pixels == null)
                {
                    a.Mesh?.Dispose();
                    a.Mesh = null;

                    return;
                }

                if (a.Mesh == null || a.Mesh.Width != w || a.Mesh.Height != h)
                {
                    a.Mesh?.Dispose();
                    a.Mesh = new Texture2D(batcher.GraphicsDevice, w, h, false, SurfaceFormat.Color);
                }

                a.Mesh.SetData(pixels);
                a.MeshOrigin = new Vector2(origin.X, origin.Y) * EffectMeshScale;
            }

            if (a.Mesh != null)
            {
                // Added as a static's are: Teleport's beam (5023) is a faint glow in KR, a solid blue column as alpha.
                bool lighten = nif.LastMeshesAdditive || nif.LastMeshesScreen;

                if (lighten)
                {
                    batcher.SetBlendState(nif.LastMeshesScreen ? ScreenBlend : OneOneBlend);
                }

                batcher.Draw(a.Mesh, anchor + a.MeshOrigin, null, new Vector3(0, ShaderHueTranslator.SHADER_NONE, 1f),
                    0f, Vector2.Zero, new Vector2(EffectMeshScale), SpriteEffects.None, depth);

                if (lighten)
                {
                    batcher.SetBlendState(null);
                }
            }
        }

        /// <summary>
        /// KR's camera rotation for objects (a waterfall's world matrix over
        /// its scale): a scene's world matrix is its node chain, then this
        /// times the tileart scale, then the tile.
        /// </summary>
        private static readonly System.Numerics.Matrix4x4 KrFacing = new(
            0.7063992f, 2.869e-8f, 0.7078136f, 0,
            -0.0353759f, 0.9987504f, 0.0353052f, 0,
            -0.7069288f, -0.0499792f, 0.7055164f, 0,
            0, 0, 0, 1);

        /// <summary>
        /// KR world, relative to a tile's corner, to client pixels from the
        /// tile's centre: through KR's view rotation (measured; the camera
        /// looks almost straight down, tilted 3 degrees), at 31.1 px a unit -
        /// 22 px a tile step, the client's, over KR's 0.7064.
        /// </summary>
        private static System.Numerics.Vector2 KrScreen(System.Numerics.Vector3 p)
        {
            System.Numerics.Vector3 d = p - new System.Numerics.Vector3(0.5f, 0f, -0.5f);
            float camX = (0.7063993f * d.X) + (0.7078135f * d.Z);
            float camY = (-0.7069289f * d.X) + (-0.0499792f * d.Y) + (0.7055165f * d.Z);
            const float px = 22f / 0.7063993f;

            return new System.Numerics.Vector2(camX * px, -camY * px);
        }

        /// <summary>Whether this mobile is currently drawn as a particle scene.</summary>
        public static bool IsDrawnAsScene(uint serial) => _bodies.ContainsKey(serial);

        public static void Draw(World world, UltimaBatcher2D batcher, Point offset, float overDepth)
        {
            KrEffectFallback.Update(world);

            uint now = Time.Ticks;
            float dt = _lastTicks == 0 ? 0f : Math.Min(0.1f, (now - _lastTicks) / 1000f);
            _lastTicks = now;

            // Creature scenes stop when the creature is no longer drawn.
            List<uint> gone = null;

            foreach (KeyValuePair<uint, Active> kv in _bodies)
            {
                if (now - kv.Value.LastSeen > 1000 || world.Get(kv.Key) == null)
                {
                    (gone ??= []).Add(kv.Key);
                }
            }

            if (gone != null)
            {
                foreach (uint s in gone)
                {
                    _bodies.Remove(s);
                }
            }

            // A moving effect's scene ends with it: KR drops every particle in
            // the frame the fireball arrives, trail and all.
            List<GameEffect> landed = null;

            foreach (KeyValuePair<GameEffect, Active> kv in _moving)
            {
                if (kv.Key.IsDestroyed || now - kv.Value.LastSeen > 100)
                {
                    (landed ??= []).Add(kv.Key);
                }
            }

            if (landed != null)
            {
                foreach (GameEffect e in landed)
                {
                    _moving.Remove(e);
                }
            }

            // Static scenes stop when the static is no longer drawn.
            List<long> offScreen = null;

            foreach (KeyValuePair<long, Active> kv in _statics)
            {
                if (now - kv.Value.LastSeen > 1000)
                {
                    (offScreen ??= []).Add(kv.Key);
                }
            }

            if (offScreen != null)
            {
                foreach (long k in offScreen)
                {
                    _statics[k].Mesh?.Dispose();
                    _statics.Remove(k);
                }
            }

            List<(string, KrStaticProvider.SceneRecord, sbyte)> unused = null;

            foreach (var kv in _sharedMeshes)
            {
                if (now - kv.Value.LastUsed > 1000)
                {
                    (unused ??= []).Add(kv.Key);
                }
            }

            if (unused != null)
            {
                foreach (var k in unused)
                {
                    _sharedMeshes[k].Texture?.Dispose();
                    _sharedMeshes.Remove(k);
                }
            }

            if (_active.Count == 0 && _bodies.Count == 0 && _statics.Count == 0 && _moving.Count == 0 && _weather == null)
            {
                return;
            }

            batcher.SetStencil(DepthStencilState.None);
            bool depthTested = false;
            int maxZ = Client.Game.GetScene<Scenes.GameScene>()?.MaxZ ?? 127;

            // Alpha, then additive glows on top. Multiply layers (ZERO,
            // SRCCOLOR) are not drawn: the wisp's darkens the ground under it
            // in a wide blue disc that the KR client does not show.
            // *Hypothesis:* KR composites effects from a buffer of their own,
            // where a multiply leaves nothing. MultiplyBlend stays for when
            // that is known.
            for (int pass = 1; pass < 3; pass++)
            {
                KrBlend blend = pass switch { 0 => KrBlend.Multiply, 1 => KrBlend.Alpha, _ => KrBlend.Additive };
                batcher.SetBlendState(blend switch
                {
                    KrBlend.Multiply => MultiplyBlend,
                    KrBlend.Additive => BlendState.Additive,
                    _ => null
                });

                foreach (Active a in Running())
                {
                    Vector2 anchor = Anchor(world, a, offset) + a.BodyShift;

                    // A static's scene sorts with the world (StaticDepth), and a
                    // creature's with the creature, or a wisp or an energy
                    // vortex showed through the walls in front of it; other
                    // effects stay over it.
                    bool inWorld = a.Graphic != 0 || a.Body && world.Mobiles.Get(a.Serial) != null;
                    float depth = a.Graphic != 0 ? StaticDepth(a)
                        : inWorld ? world.Mobiles.Get(a.Serial).CalculateDepthZ() + 1f
                        : overDepth;

                    if (inWorld != depthTested)
                    {
                        depthTested = inWorld;
                        batcher.SetStencil(inWorld ? DepthStencilState.DepthRead : DepthStencilState.None);
                    }

                    if (pass == 1)   // the first pass drawn: advance each effect once
                    {
                        a.Effect.Origin = new System.Numerics.Vector2(anchor.X + offset.X, anchor.Y + offset.Y);
                        a.Effect.Update(dt);
                        a.Age += dt;
                        AddLight(world, a);
                    }

                    if (a.Graphic != 0 && (UnderFloor(world, a, maxZ) || RaisedOverAnother(world, a)))
                    {
                        continue;
                    }


                    foreach (EmsSprite sprite in a.Effect.Sprites())
                    {
                        // A glow map is added over the sprite's own colour: a
                        // second, untinted sprite drawn with the additive ones,
                        // at the particle's alpha.
                        int layers = sprite.Glow != null ? 2 : 1;

                        for (int layer = 0; layer < layers; layer++)
                        {
                            EmsSprite s = layer == 0
                                ? sprite
                                : sprite with { Texture = sprite.Glow, Color = new System.Numerics.Vector4(1f, 1f, 1f, sprite.Color.W), Blend = KrBlend.Additive };

                            if (s.Blend == blend)
                            {
                                if (a.Graphic != 0 && a.Effect is NifEffect)
                                {
                                    // A static's scene: through KR's camera like
                                    // its meshes, or the sparkles' dots sat 60 px
                                    // below their flare (the effect camera shows
                                    // height as up, KR's top-down one barely).
                                    DrawSprite(batcher, s, anchor + new Vector2(0, a.Z << 2) + StaticScenePoint(a, s.Scene, s.Emitter),
                                        Math.Max(depth, StaticSceneDepth(a, s.Scene)), 0f, a.SizeScale, oneOne: true);
                                }
                                else
                                {
                                    // KR screens the Water Elemental's flares
                                    // (INVDESTCOLOR, DESTALPHA): added, they
                                    // ran together into a solid white ring.
                                    bool screened = a.BodyScreen && blend == KrBlend.Additive;

                                    if (screened)
                                    {
                                        batcher.SetBlendState(ScreenBlend);
                                    }

                                    DrawSprite(batcher, s, s.HasOrigin ? new Vector2(s.Origin.X - offset.X, s.Origin.Y - offset.Y) : anchor + a.ScriptOffset, depth, a.Spread,
                                        a.Effect is NifEffect ? a.SizeScale : a.ScriptScale, oneOne: (a.Graphic != 0 && !FadedScript(a)) || a.Effect is NifEffect,
                                        heightY: a.Graphic != 0 && a.Effect is EmsEffect, groundDepth: a.Body, feetY: anchor.Y - a.BodyShift.Y, groundZ: FadedScript(a),
                                        travelAngle: a.Moving && a.Effect is EmsEffect ? a.MoveAngle : float.NaN);

                                    if (screened)
                                    {
                                        batcher.SetBlendState(BlendState.Additive);
                                    }
                                }
                            }
                        }
                    }

                    // A static's scene meshes (waterfalls), from the tile's
                    // centre - which the anchor already is: adding 22 px put
                    // the shrine's falls one tile step down-right of KR's.
                    // Without the static's z, which the placement carries.
                    if (pass == 1 && a.Graphic != 0)
                    {
                        DrawMesh(batcher, a, anchor + new Vector2(0, a.Z << 2), depth);
                    }
                    else if (pass == 1 && !a.Moving)
                    {
                        DrawEffectMesh(batcher, a, anchor, depth);
                    }
                }
            }

            batcher.SetBlendState(null);
            batcher.SetStencil(DepthStencilState.Default);

            _active.RemoveAll(a =>
            {
                // A script's mesh (no particles of its own) lasts its lifetime.
                bool done = a.MeshUp ? a.Age >= a.Until : a.Effect.Finished || (a.Until > 0f && a.Age >= a.Until);

                if (done)
                {
                    a.Mesh?.Dispose();
                }

                return done;
            });
        }

        private static IEnumerable<Active> Running()
        {
            foreach (Active a in _active)
            {
                yield return a;
            }

            foreach (Active a in _bodies.Values)
            {
                yield return a;
            }

            foreach (Active a in _statics.Values)
            {
                yield return a;
            }

            foreach (Active a in _moving.Values)
            {
                yield return a;
            }

            if (_weather != null)
            {
                yield return _weather;
            }
        }

        private static Vector2 Anchor(World world, Active a, Point offset)
        {
            if (a.Moving)
            {
                return a.MoveAnchor;
            }

            if (a.Serial != 0 && world.Get(a.Serial) is Mobile m)
            {
                return new Vector2(
                    m.RealScreenPosition.X + (int)m.Offset.X + 22,
                    m.RealScreenPosition.Y - 3 + (int)(m.Offset.Y - m.Offset.Z) + 22 - a.Lift);
            }

            return new Vector2(
                ((a.X - a.Y) * 22) - offset.X,
                ((a.X + a.Y) * 22) - (a.Z << 2) - offset.Y - a.Lift);
        }

        private static void DrawSprite(UltimaBatcher2D batcher, in EmsSprite s, Vector2 anchor, float depth, float spread = 1f, float sizeScale = 1f, bool oneOne = false, bool heightY = false, bool groundDepth = false, float feetY = float.NaN, bool groundZ = false, float travelAngle = float.NaN)
        {
            Texture2D texture = Texture(batcher.GraphicsDevice, s.Texture);

            if (texture == null || s.Size <= 0f || (s.Color.W <= 0f && s.Blend != KrBlend.Multiply))
            {
                return;
            }

            int fw = texture.Width / s.Columns;
            int fh = texture.Height / s.Rows;
            Rectangle source = new((s.Frame % s.Columns) * fw, (s.Frame / s.Columns % s.Rows) * fh, fw, fh);

            // A static's script (heightY) works in KR's world axes, where y
            // is height, negative up: the candle flame's (0, -0.2, 0). Read
            // as the second tile axis it pushed the flame right of the
            // candlestick (0xB1A at 1412,1713).
            // groundZ: the script's z lies along the ground (KR's world z,
            // minus the tile y), not up. A forge's coals01.ems spreads along
            // the basin in z; read as height it stood up out of the forge as
            // flames, where KR's lie flat in the basin (Tokuno 766,1260). A
            // torch's flame is upright in z, so the rest keep it as height.
            Vector2 pos = anchor + (spread * (heightY && groundZ
                ? new Vector2(
                    (s.Position.X + s.Position.Z) * TilePx,
                    ((s.Position.X - s.Position.Z) * TilePx) + (s.Position.Y * HeightPx))
                : heightY
                ? new Vector2(
                    s.Position.X * TilePx,
                    (s.Position.X * TilePx) + (s.Position.Y * HeightPx) - (s.Position.Z * HeightPx))
                : new Vector2(
                    (s.Position.X - s.Position.Y) * TilePx,
                    ((s.Position.X + s.Position.Y) * TilePx) - (s.Position.Z * HeightPx))));

            float scale = s.Size * SizePx * sizeScale / fw;

            // A creature's scene: a particle drawn below its feet sorts with
            // the ground it is drawn over, a tile (22 px down) at a time, or
            // the floor in front cut off the lower half of an energy vortex.
            // From the creature's feet, not the scene's shifted anchor: the
            // Water Elemental's ring, moved up, lost the boost just below
            // its feet.
            float ground = float.IsNaN(feetY) ? anchor.Y : feetY;

            if (groundDepth && pos.Y > ground)
            {
                depth += (pos.Y - ground) / 22f;
            }

            // An Axial particle (torchflame.ems) stands its texture up: KR's
            // quads, at a Trammel wall torch, run the texture's
            // u down the screen - the sideways flame upright, tip on top - at
            // about 31 px a unit of length and 26 a unit of width (0.95 x 0.67
            // units for a length near 1 and a width of 0.8).
            // Square, whatever the texture's shape: KR stretches Blade Spirits'
            // 128 x 16 arrow (5034) over a square quad into
            // a broad white blade; kept 8:1, ours drew thin arrows.
            Vector2 scales = new(scale, s.Size * SizePx * sizeScale / fh);
            float turn = 0f;

            Vector2 pivot = new(fw / 2f, fh / 2f);

            if (s.Length > 0f)
            {
                // 16 px a unit of length, 22 of width: at the trace's 31 and
                // 26 the torch flames came out twice KR's height in a
                // recording, and their overlap washed the orange to white.
                scales = new Vector2(s.Length * 16f / fw, s.Size * 22f / fh);
                turn = MathHelper.PiOver2;

                // Grown up from its base: the texture's bright end (u = 1,
                // the bottom once turned) sits on the particle. Centred, half
                // of each flame hung below the torch head as a pale haze, and
                // the bases never stacked into KR's bright ball.
                pivot = new Vector2(fw, fh / 2f);

                // A flying script's axial particle (arrow.ems) lies along its
                // flight, head (u = 0) foremost at the effect's position, the
                // texture kept to its own shape. At 2 px a unit of length,
                // KR's arrow (14 units) is about as long as the 2D one.
                if (!float.IsNaN(travelAngle))
                {
                    float f = s.Length * MovingAxialPx / fw;
                    scales = new Vector2(f, f * s.Size);
                    turn = travelAngle + MathHelper.Pi;
                    pivot = new Vector2(0f, fh / 2f);
                }
            }
            // The shader scales colour by this alpha; for a multiply that would
            // fade to black rather than to no effect, so it is drawn at full.
            // KR blends a static's additive particles ONE/ONE (a wall torch's
            // colour op MODULATE): texture times colour is added and
            // the particle's alpha plays no part - weighting by it as well
            // drew the torch flames at about half KR's brightness. The
            // texture's alpha stays (the additive state): a grey TGA loads as
            // white with the grey as alpha, where KR reads it as luminance, so
            // without it the torch's halo became a milky white blob.
            // Spell scenes too: Curse (5028) drew ONE, ONE,
            // its skulls stacking into KR's cyan-white balls.
            bool addAll = oneOne && s.Blend == KrBlend.Additive;
            float alpha = s.Blend == KrBlend.Multiply || addAll ? 1f : Math.Clamp(s.Color.W, 0f, 1f);
            // The particle's own colour (emitter colour times its colour
            // keys) tints the texture; white needs no tint.
            bool tinted = s.Color.X < 0.999f || s.Color.Y < 0.999f || s.Color.Z < 0.999f;
            Vector3 hue = tinted
                ? new(ShaderHueTranslator.PackTint(s.Color.X, s.Color.Y, s.Color.Z), ShaderHueTranslator.SHADER_TINT, alpha)
                : new(0, ShaderHueTranslator.SHADER_NONE, alpha);

            batcher.Draw(
                texture,
                pos,
                source,
                hue,
                MathHelper.ToRadians(s.RotationDegrees) + turn,
                pivot,
                scales,
                SpriteEffects.None,
                depth);
        }

        /// <summary>A KR effect texture (TGA in EffectTexture.uop), cached.</summary>
        public static Texture2D EffectTexture(GraphicsDevice device, string name) => Texture(device, name);

        private const float WeatherSpread = 2.5f;

        private static Active _weather;
        private static string _weatherScene;

        /// <summary>
        /// Keeps a looping weather scene (KR snow) running on the player, or
        /// stops it when <paramref name="scene"/> is null.
        /// </summary>
        public static void SetWeather(string scene, uint player)
        {
            if (scene == null)
            {
                _weather = null;
                _weatherScene = null;

                return;
            }

            if (_weather != null && _weatherScene == scene && _weather.Serial == player)
            {
                return;
            }

            if (KrEffectProvider.Instance?.Effects.Create(scene, unchecked(++_seed * 7919)) is NifEffect nif)
            {
                nif.Loop = true;
                // KR's snow clouds cover about +-300 px drawn like an effect;
                // spread over the view instead, flakes keeping their size.
                _weather = new Active { Effect = nif, Serial = player, Spread = WeatherSpread };
                _weatherScene = scene;
            }
        }

        private static Texture2D Texture(GraphicsDevice device, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            if (_textures.TryGetValue(name, out Texture2D cached))
            {
                return cached;
            }

            Texture2D texture = null;
            TgaImage image = KrEffectProvider.Instance.Effects.Texture(name);

            if (image != null)
            {
                // The batcher blends premultiplied colour; TGA alpha is straight.
                uint[] pixels = (uint[])image.Pixels.Clone();

                for (int i = 0; i < pixels.Length; i++)
                {
                    uint p = pixels[i];
                    uint a = p >> 24;

                    if (a != 255)
                    {
                        uint r = (p & 0xFF) * a / 255;
                        uint g = ((p >> 8) & 0xFF) * a / 255;
                        uint b = ((p >> 16) & 0xFF) * a / 255;
                        pixels[i] = r | (g << 8) | (b << 16) | (a << 24);
                    }
                }

                texture = new Texture2D(device, image.Width, image.Height, false, SurfaceFormat.Color);
                texture.SetData(pixels);
            }

            _textures[name] = texture;

            return texture;
        }
    }
}
