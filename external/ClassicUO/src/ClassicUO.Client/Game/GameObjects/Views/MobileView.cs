// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;

namespace ClassicUO.Game.GameObjects
{
    internal partial class Mobile
    {
        private const int SIT_OFFSET_Y = 4;
        private static EquipConvData? _equipConvData;
        private static int _characterFrameStartY;
        private static int _startCharacterWaistY;
        private static int _startCharacterKneesY;
        private static int _startCharacterFeetY;
        private static int _characterFrameHeight;

        /// <summary>
        /// A ridden mount standing still plays KR's neutral idle (25), not its
        /// stand (1): KR's rider keeps still on a calm ostard, where the stand
        /// swished its tail several times a second. Asked for through the
        /// animal group ClassicUO never uses (Unknown), which the KR provider
        /// maps to 25, so a wild animal's stand is untouched.
        /// </summary>
        private static byte RiddenGroup(ushort mountGraphic, byte group) =>
            group == (byte)LowAnimationGroup.Stand
            && KrAnimationProvider.Instance is { } kr
            && Client.Game.UO.Animations.GetAnimType(mountGraphic) == AnimationGroupsType.Animal
            && kr.ResolveAction(mountGraphic, 25) >= 0
                ? (byte)LowAnimationGroup.Unknown
                : group;

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (IsDestroyed || !AllowedToDraw)
            {
                return false;
            }

            // KR models are taller than the 2D bodies, and under a low roof
            // (Wind, Trammel 5225,173: marble roof at z 25 over a floor at
            // z 5) the head rose past the roof tile above it - a diamond,
            // only a few pixels wide near its top - over the tiles behind,
            // which are drawn first. KR's 3D floors hide it.
            if (KrAnimationProvider.IsActive && CoveringSurfaceZ(X, Y) is int cover)
            {
                // Roofed all round, it is out of sight; walking, it is drawn
                // in front of the roof tiles ahead and showed whole.
                if (CoveringSurfaceZ(X + 1, Y) != null && CoveringSurfaceZ(X, Y + 1) != null
                    && CoveringSurfaceZ(X + 1, Y + 1) != null)
                {
                    FrameInfo = default;

                    return true;
                }

                // At a roof's edge: cut at the middle of the roof diamond
                // over its tile, where it is a whole tile wide.
                int cutY = posY + (int)Offset.Y + 44 - ((cover - Z) << 2);

                if (batcher.ClipBegin(-100000, cutY, 200000, 200000))
                {
                    bool drawn = DrawCore(batcher, posX, posY, depth);
                    batcher.ClipEnd();

                    return drawn;
                }
            }

            return DrawCore(batcher, posX, posY, depth);
        }

        /// <summary>
        /// The z of the lowest drawn roof or floor above this mobile at a
        /// tile, or null: one hidden to show the player indoors (at or above
        /// the scene's MaxZ) does not count.
        /// </summary>
        private int? CoveringSurfaceZ(int x, int y)
        {
            if (World.Map?.GetChunk(x, y, false) is not { } chunk)
            {
                return null;
            }

            int maxZ = Client.Game.GetScene<GameScene>()?.MaxZ ?? 127;
            int? best = null;

            for (GameObject o = chunk.GetHeadObject(x % 8, y % 8); o != null; o = o.TNext)
            {
                if (o is Land || o is Mobile || o.Z <= Z + 3 || o.Z >= maxZ)
                {
                    continue;
                }

                ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[o.Graphic];

                if ((data.IsRoof || data.IsSurface) && (best == null || o.Z < best))
                {
                    best = o.Z;
                }
            }

            return best;
        }

        private bool DrawCore(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            // A creature KR shows as a particle scene (the wisp): the scene
            // is drawn by KrParticles over the world, not this sprite.
            if (Managers.KrParticles.DrawsBody(this))
            {
                // No sprite to pick: a creature-sized box from 60 px above
                // the feet to just below them (see CheckMouseSelection).
                FrameInfo.X = 20;
                FrameInfo.Y = 60;
                FrameInfo.Width = 40;
                FrameInfo.Height = 70;

                return true;
            }

            bool charSitting = false;
            ushort overridedHue = 0;

            AnimationsLoader.SittingInfoData seatData = AnimationsLoader.SittingInfoData.Empty;
            _equipConvData = null;
            FrameInfo.X = 0;
            FrameInfo.Y = 0;
            FrameInfo.Width = 0;
            FrameInfo.Height = 0;

            posY -= 3;
            int drawX = posX + (int)Offset.X;
            int drawY = posY + (int)(Offset.Y - Offset.Z);

            drawX += 22;
            drawY += 22;

            bool hasShadow = !IsDead && !IsHidden && ProfileManager.CurrentProfile.ShadowsEnabled;

            if (World.AuraManager.IsEnabled)
            {
                World.AuraManager.Draw(
                    batcher,
                    drawX,
                    drawY,
                    ProfileManager.CurrentProfile.PartyAura && World.Party.Contains(this)
                        ? ProfileManager.CurrentProfile.PartyAuraHue
                        : Notoriety.GetHue(NotorietyFlag),
                    depth
                );
            }

            bool isHuman = IsHuman;

            bool isGargoyle =
                Client.Game.UO.Version >= ClientVersion.CV_7000
                && (Graphic == 666 || Graphic == 667 || Graphic == 0x02B7 || Graphic == 0x02B6);

            Vector3 hueVec = ShaderHueTranslator.GetHueVector(0, false, AlphaHue / 255f);

            if (
                ProfileManager.CurrentProfile.HighlightGameObjects
                && ReferenceEquals(SelectedObject.Object, this)
            )
            {
                overridedHue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                hueVec.Y = 1;
            }
            else if (SelectedObject.HealthbarObject == this)
            {
                overridedHue = Notoriety.GetHue(NotorietyFlag);
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                overridedHue = Constants.OUT_RANGE_COLOR;
                hueVec.Y = 1;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                overridedHue = Constants.DEAD_RANGE_COLOR;
                hueVec.Y = 1;
            }
            else if (IsHidden)
            {
                overridedHue = 0x038E;
            }
            else
            {
                overridedHue = 0;

                if (IsDead)
                {
                    if (!isHuman)
                    {
                        overridedHue = 0x0386;
                    }
                }
                else
                {
                    if (ProfileManager.CurrentProfile.HighlightMobilesByPoisoned)
                    {
                        if (IsPoisoned)
                        {
                            overridedHue = ProfileManager.CurrentProfile.PoisonHue;
                        }
                    }
                    if (ProfileManager.CurrentProfile.HighlightMobilesByParalize)
                    {
                        if (IsParalyzed && NotorietyFlag != NotorietyFlag.Invulnerable)
                        {
                            overridedHue = ProfileManager.CurrentProfile.ParalyzedHue;
                        }
                    }
                    if (ProfileManager.CurrentProfile.HighlightMobilesByInvul)
                    {
                        if (NotorietyFlag != NotorietyFlag.Invulnerable && IsYellowHits)
                        {
                            overridedHue = ProfileManager.CurrentProfile.InvulnerableHue;
                        }
                    }
                }
            }

            bool isAttack = Serial == World.TargetManager.LastAttack;
            bool isUnderMouse =
                World.TargetManager.IsTargeting && ReferenceEquals(SelectedObject.Object, this);

            if (Serial != World.Player.Serial)
            {
                if (isAttack || isUnderMouse)
                {
                    overridedHue = Notoriety.GetHue(NotorietyFlag);
                }
            }

            ProcessSteps(out byte dir);
            byte layerDir = dir;

            Client.Game.UO.Animations.GetAnimDirection(ref dir, ref IsFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            Item mount = FindItemByLayer(Layer.Mount);
            sbyte mountOffsetY = 0;

            if (isHuman && mount != null && mount.Graphic != 0x3E96)
            {
                ushort mountGraphic = mount.GetGraphicForAnimation();
                byte animGroupMount = 0;

                if (
                    mountGraphic != 0xFFFF
                    && mountGraphic < Client.Game.UO.Animations.MaxAnimationCount
                )
                {
                    if (Mounts.TryGet(mount.Graphic, out var mountInfo))
                    {
                        mountOffsetY = mountInfo.OffsetY;
                    }

                    if (hasShadow)
                    {
                        DrawInternal(
                            batcher,
                            this,
                            null,
                            drawX,
                            drawY + 10,
                            hueVec,
                            IsFlipped,
                            animIndex,
                            true,
                            graphic,
                            animGroup,
                            dir,
                            isHuman,
                            false,
                            false,
                            false,
                            depth,
                            mountOffsetY,
                            overridedHue,
                            charSitting
                        );

                        animGroupMount = RiddenGroup(mountGraphic, GetGroupForAnimation(this, mountGraphic));

                        DrawInternal(
                            batcher,
                            this,
                            mount,
                            drawX,
                            drawY,
                            hueVec,
                            IsFlipped,
                            animIndex,
                            true,
                            mountGraphic,
                            animGroupMount,
                            dir,
                            isHuman,
                            false,
                            false,
                            false,
                            depth,
                            mountOffsetY,
                            overridedHue,
                            charSitting
                        );
                    }
                    else
                    {
                        animGroupMount = RiddenGroup(mountGraphic, GetGroupForAnimation(this, mountGraphic));
                    }

                    DrawInternal(
                        batcher,
                        this,
                        mount,
                        drawX,
                        drawY,
                        hueVec,
                        IsFlipped,
                        animIndex,
                        false,
                        mountGraphic,
                        animGroupMount,
                        dir,
                        isHuman,
                        false,
                        true,
                        false,
                        depth,
                        mountOffsetY,
                        overridedHue,
                        charSitting
                    );

                    drawY += mountOffsetY;

                    // KR's own seat for this mount and direction (measured
                    // measured; see KrAnimationProvider.RiderOffset).
                    if (KrAnimationProvider.Instance is { } krSeat)
                    {
                        (float sx, float sy) = krSeat.RiderOffset(mountGraphic, dir);
                        drawX += (int)MathF.Round(IsFlipped ? -sx : sx);
                        drawY += (int)MathF.Round(sy);
                    }
                }
            }
            else
            {
                if (TryGetSittingInfo(out seatData))
                {
                    animGroup = (byte)PeopleAnimationGroup.Stand;
                    animIndex = 0;

                    ProcessSteps(out dir);

                    Client.Game.UO.FileManager.Animations.FixSittingDirection(
                        ref dir,
                        ref IsFlipped,
                        ref drawX,
                        ref drawY,
                        ref seatData
                    );

                    drawY += SIT_OFFSET_Y;

                    if (dir == 3)
                    {
                        if (IsGargoyle)
                        {
                            drawY -= 30 - SIT_OFFSET_Y;
                            animGroup = 42;
                        }
                        else
                        {
                            animGroup = 25;
                        }
                    }
                    else if (IsGargoyle)
                    {
                        animGroup = 42;
                    }
                    else
                    {
                        charSitting = true;
                    }
                }
                else if (hasShadow)
                {
                    DrawInternal(
                        batcher,
                        this,
                        null,
                        drawX,
                        drawY,
                        hueVec,
                        IsFlipped,
                        animIndex,
                        true,
                        graphic,
                        animGroup,
                        dir,
                        isHuman,
                        false,
                        false,
                        false,
                        depth,
                        mountOffsetY,
                        overridedHue,
                        charSitting
                    );
                }
            }

            DrawInternal(
                batcher,
                this,
                null,
                drawX,
                drawY,
                hueVec,
                IsFlipped,
                animIndex,
                false,
                graphic,
                animGroup,
                dir,
                isHuman,
                false,
                false,
                isGargoyle,
                depth,
                mountOffsetY,
                overridedHue,
                charSitting
            );

            if (!IsEmpty)
            {
                // draw order built from the layer algorithm (PaperdollOrder), keyed on
                // the equipped item graphics; cloak is then repositioned by facing
                // direction to match the in-world tables. Stack-allocated, no GC churn.
                Span<Layer> layers = stackalloc Layer[PaperdollOrder.N];
                int layerCount = PaperdollOrder.BuildInWorld(this, IsFemale || isGargoyle, layerDir, layers);

                for (int i = 0; i < layerCount; i++)
                {
                    Layer layer = layers[i];

                    Item item = FindItemByLayer(layer);

                    if (item == null)
                    {
                        continue;
                    }

                    if (IsDead && (layer == Layer.Hair || layer == Layer.Beard))
                    {
                        continue;
                    }

                    if (isHuman)
                    {
                        if (IsCovered(this, layer))
                        {
                            continue;
                        }

                        if (item.ItemData.AnimID != 0)
                        {
                            graphic = item.ItemData.AnimID;
                            bool krRemapped = false;

                            if (isGargoyle)
                            {
                                FixGargoyleEquipments(ref graphic);
                            }
                            else if (ClassicUO.Assets.KrAnimationProvider.Instance is { } kre)
                            {
                                // KR's newer anim id for the item, where KR lacks the 2D one.
                                graphic = (ushort)kre.EquipmentAnim(item.Graphic, graphic);
                                krRemapped = graphic != item.ItemData.AnimID;
                            }

                            // equipconv.def turns the legacy id into a 2D one; KR's newer id is drawn as it is.
                            if (
                                !krRemapped
                                && Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                                    Graphic,
                                    out Dictionary<ushort, EquipConvData> map
                                )
                            )
                            {
                                if (map.TryGetValue(item.ItemData.AnimID, out EquipConvData data))
                                {
                                    _equipConvData = data;
                                    graphic = data.Graphic;
                                }
                            }

                            DrawInternal(
                                batcher,
                                this,
                                item,
                                drawX,
                                drawY,
                                hueVec,
                                IsFlipped,
                                animIndex,
                                false,
                                graphic,
                                isGargoyle /*&& item.ItemData.IsWeapon*/
                                && seatData.Graphic == 0
                                    ? GetGroupForAnimation(this, graphic, true)
                                    : animGroup,
                                dir,
                                isHuman,
                                true,
                                false,
                                isGargoyle,
                                depth,
                                mountOffsetY,
                                overridedHue,
                                charSitting
                            );
                        }
                        else
                        {
                            if (item.ItemData.IsLight)
                            {
                                Client.Game
                                    .GetScene<GameScene>()
                                    .AddLight(this, item, drawX, drawY);
                            }
                        }

                        _equipConvData = null;
                    }
                    else
                    {
                        if (item.ItemData.IsLight)
                        {
                            Client.Game.GetScene<GameScene>().AddLight(this, item, drawX, drawY);

                            /*DrawInternal
                            (
                                batcher,
                                this,
                                item,
                                drawX,
                                drawY,
                                IsFlipped,
                                animIndex,
                                false,
                                graphic,
                                animGroup,
                                dir,
                                isHuman,
                                false,
                                alpha: HueVector.Z
                            );
                            */
                            //break;
                        }
                    }
                }
            }

            //if (FileManager.Animations.SittingValue != 0)
            //{
            //    ref var sittingData = ref FileManager.Animations.SittingInfos[FileManager.Animations.SittingValue - 1];

            //    if (FileManager.Animations.Direction == 3 && sittingData.DrawBack &&
            //        HasEquipment && Equipment[(int) Layer.Cloak] == null)
            //    {

            //    }
            //}
            //

            FrameInfo.X = Math.Abs(FrameInfo.X);
            FrameInfo.Y = Math.Abs(FrameInfo.Y);
            FrameInfo.Width = FrameInfo.X + FrameInfo.Width;
            FrameInfo.Height = FrameInfo.Y + FrameInfo.Height;

            return true;
        }

        private static ushort GetAnimationInfo(Mobile owner, Item item, bool isGargoyle)
        {
            if (item.ItemData.AnimID != 0)
            {
                var graphic = item.ItemData.AnimID;

                if (isGargoyle)
                {
                    FixGargoyleEquipments(ref graphic);
                }

                if (
                    Client.Game.UO.FileManager.Animations.EquipConversions.TryGetValue(
                        owner.Graphic,
                        out Dictionary<ushort, EquipConvData> map
                    )
                )
                {
                    if (map.TryGetValue(item.ItemData.AnimID, out EquipConvData data))
                    {
                        _equipConvData = data;
                        graphic = data.Graphic;
                    }
                }

                return graphic;
            }

            return 0xFFFF;
        }

        private static void FixGargoyleEquipments(ref ushort graphic)
        {
            switch (graphic)
            {
                // gargoyle robe
                case 0x01D5:
                    graphic = 0x0156;

                    break;

                // gargoyle dead shroud
                case 0x03CA:
                    graphic = 0x0223;

                    break;

                // gargoyle spellbook
                case 0x03D8:
                    graphic = 329;

                    break;

                // gargoyle necrobook
                case 0x0372:
                    graphic = 330;

                    break;

                // gargoyle chivalry book
                case 0x0374:
                    graphic = 328;

                    break;

                // gargoyle bushido book
                case 0x036F:
                    graphic = 327;

                    break;

                // gargoyle ninjitsu book
                case 0x036E:
                    graphic = 328;

                    break;

                // gargoyle masteries book
                case 0x0426:
                    graphic = 0x042B;

                    break;
                //NOTE: gargoyle mysticism book seems ok. Mha!


                /* into the mobtypes.txt file of 7.0.90+ client version we have:
                 *
                 *   1529 	EQUIPMENT	0		# EQUIP_Shield_Pirate_Male_H
                 *   1530 	EQUIPMENT	0		# EQUIP_Shield_Pirate_Female_H
                 *   1531 	EQUIPMENT	10000	# Equip_Shield_Pirate_Male_G
                 *   1532 	EQUIPMENT	10000	# Equip_Shield_Pirate_Female_G
                 *
                 *   This means that graphic 0xA649 [pirate shield] has 4 tiledata infos.
                 *   Standard client handles it automatically without any issue.
                 *   Maybe it's hardcoded into the client
                 */

                // EQUIP_Shield_Pirate_Male_H
                case 1529:
                    graphic = 1531;

                    break;

                // EQUIP_Shield_Pirate_Female_H
                case 1530:
                    graphic = 1532;

                    break;
            }
        }

        private static bool GetTexture(
            ushort graphic,
            byte animGroup,
            ref byte animIndex,
            byte direction,
            out SpriteInfo spriteInfo,
            out bool isUOP
        )
        {
            spriteInfo = default;

            var frames = Client.Game.UO.Animations.GetAnimationFrames(
                graphic,
                animGroup,
                direction,
                out _,
                out isUOP
            );

            if (frames.Length == 0)
            {
                return false;
            }

            if (animIndex < 0)
            {
                animIndex = 0;
            }

            animIndex = (byte)(animIndex % frames.Length);

            spriteInfo = frames[animIndex];

            if (spriteInfo.Texture == null)
            {
                return false;
            }

            return true;
        }

        private static void DrawInternal(
            UltimaBatcher2D batcher,
            Mobile owner,
            Item entity,
            int x,
            int y,
            Vector3 hueVec,
            bool mirror,
            byte frameIndex,
            bool hasShadow,
            ushort id,
            byte animGroup,
            byte dir,
            bool isHuman,
            bool isEquip,
            bool isMount,
            bool forceUOP,
            float depth,
            sbyte mountOffset,
            ushort overridedHue,
            bool charIsSitting
        )
        {
            if (id >= Client.Game.UO.Animations.MaxAnimationCount || owner == null)
            {
                return;
            }

            var frames = Client.Game.UO.Animations.GetAnimationFrames(
                id,
                animGroup,
                dir,
                out var hueFromFile,
                out _,
                isEquip,
                false
            );

            if (hueFromFile == 0)
            {
                hueFromFile = overridedHue;
            }

            if (frames.Length == 0)
            {
                return;
            }

            // A 2D mount under a KR rider: the rider's index runs over its
            // own, longer cycle (10 frames on a run to the savage ridgeback's
            // 5), and clamping froze the mount on its last frame half the time.
            if (isMount && frameIndex >= frames.Length)
            {
                frameIndex = (byte)(frameIndex % frames.Length);
            }
            else if (frameIndex >= frames.Length)
            {
                frameIndex = (byte)(frames.Length - 1);
            }
            else if (frameIndex < 0)
            {
                frameIndex = 0;
            }

            ref var spriteInfo = ref frames[frameIndex % frames.Length];

            if (spriteInfo.Texture == null)
            {
                if (!(charIsSitting && entity == null && !hasShadow))
                {
                    return;
                }

                goto SKIP;
            }

            if (mirror)
            {
                x -= spriteInfo.Width - spriteInfo.Center.X;
            }
            else
            {
                x -= spriteInfo.Center.X;
            }

            y -= spriteInfo.Height + spriteInfo.Center.Y;

            SKIP:

            if (hasShadow)
            {
                batcher.DrawShadow(
                    spriteInfo.Texture,
                    new Vector2(x, y),
                    spriteInfo.Source,
                    mirror,
                    depth,
                    spriteInfo.Scale
                );
            }
            else
            {
                ushort hue = overridedHue;
                bool partialHue = false;

                if (hue == 0)
                {
                    hue = entity?.Hue ?? owner.Hue;
                    partialHue = !isMount && entity != null && entity.ItemData.IsPartialHue;

                    if ((hue & 0x8000) != 0)
                    {
                        partialHue = true;
                        hue &= 0x7FFF;
                    }

                    if (hue == 0)
                    {
                        hue = hueFromFile;

                        if (hue == 0 && _equipConvData.HasValue)
                        {
                            hue = _equipConvData.Value.Color;
                        }

                        partialHue = false;
                    }
                }

                // A hue with the 0x4000 flag on a KR model: the server's Air
                // Elemental is 0x4001, which turned KR's white model into a
                // dark, see-through swirl. KR draws it plain.
                if (spriteInfo.IsKrArt && (hue & 0x4000) != 0)
                {
                    hue = 0;
                    partialHue = false;
                }

                hueVec = ShaderHueTranslator.GetHueVector(hue, partialHue, hueVec.Z, krArt: spriteInfo.IsKrArt);

                if (spriteInfo.Texture != null)
                {
                    Vector2 pos = new Vector2(x, y);
                    Rectangle rect = spriteInfo.Source;

                    if (charIsSitting)
                    {
                        Vector3 mod = CalculateSitAnimation(y, entity, isHuman, ref spriteInfo);

                        batcher.DrawCharacterSitted(
                            spriteInfo.Texture,
                            pos,
                            rect,
                            mod,
                            hueVec,
                            mirror,
                            depth + 1f,
                            spriteInfo.Scale
                        );
                    }
                    else
                    {
                        // The sprite is cut into horizontal bands drawn at
                        // increasing depth so that a mount and its rider sort
                        // against the tiles they overlap. The cut is decided
                        // in screen pixels, because mountOffset is a screen
                        // offset, but it has to be made in the source
                        // rectangle - hence ToSourceLength - and the pen advances by
                        // the height the band is actually drawn at.
                        int diffY = (spriteInfo.Height + spriteInfo.Center.Y) - mountOffset;

                        int value = Math.Max(1, diffY);
                        int count = Math.Max((spriteInfo.Height / value) + 1, 2);

                        rect.Height = Math.Min(spriteInfo.ToSourceLength(value), rect.Height);
                        int remains = spriteInfo.Source.Height - rect.Height;

                        int tiles = (byte)owner.Direction % 2 == 0 ? 2 : 2;

                        for (int i = 0; i < count; ++i)
                        {
                            batcher.Draw(
                                spriteInfo.Texture,
                                pos,
                                rect,
                                hueVec,
                                0f,
                                Vector2.Zero,
                                spriteInfo.Scale,
                                mirror ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
                                depth + 1f + (i * tiles)
                            );

                            pos.Y += rect.Height * spriteInfo.Scale;
                            rect.Y += rect.Height;
                            rect.Height = remains;
                            remains -= rect.Height;
                        }
                    }

                    int xx = -spriteInfo.Center.X;
                    int yy = -(spriteInfo.Height + spriteInfo.Center.Y + 3);

                    if (mirror)
                    {
                        xx = -(spriteInfo.Width - spriteInfo.Center.X);
                    }

                    if (xx < owner.FrameInfo.X)
                    {
                        owner.FrameInfo.X = xx;
                    }

                    if (yy < owner.FrameInfo.Y)
                    {
                        owner.FrameInfo.Y = yy;
                    }

                    if (owner.FrameInfo.Width < xx + spriteInfo.Width)
                    {
                        owner.FrameInfo.Width = xx + spriteInfo.Width;
                    }

                    if (owner.FrameInfo.Height < yy + spriteInfo.Height)
                    {
                        owner.FrameInfo.Height = yy + spriteInfo.Height;
                    }
                }

                if (entity != null && entity.ItemData.IsLight)
                {
                    Client.Game
                        .GetScene<GameScene>()
                        .AddLight(owner, entity, mirror ? x + spriteInfo.Width : x, y);
                }
            }
        }

        private static Vector3 CalculateSitAnimation(
            int y,
            Item entity,
            bool isHuman,
            ref SpriteInfo spriteInfo
        )
        {
            Vector3 mod = new Vector3();

            const float UPPER_BODY_RATIO = 0.35f;
            const float MID_BODY_RATIO = 0.60f;
            const float LOWER_BODY_RATIO = 0.94f;

            if (entity == null && isHuman)
            {
                int frameHeight = spriteInfo.Height;
                if (frameHeight == 0)
                {
                    frameHeight = 61;
                }

                _characterFrameStartY =
                    y - (spriteInfo.Texture != null ? 0 : frameHeight - SIT_OFFSET_Y);
                _characterFrameHeight = frameHeight;
                _startCharacterWaistY =
                    (int)(frameHeight * UPPER_BODY_RATIO) + _characterFrameStartY;
                _startCharacterKneesY = (int)(frameHeight * MID_BODY_RATIO) + _characterFrameStartY;
                _startCharacterFeetY =
                    (int)(frameHeight * LOWER_BODY_RATIO) + _characterFrameStartY;

                if (spriteInfo.Texture == null)
                {
                    return mod;
                }
            }

            mod.X = UPPER_BODY_RATIO;
            mod.Y = MID_BODY_RATIO;
            mod.Z = LOWER_BODY_RATIO;

            if (entity != null)
            {
                float itemsEndY = y + spriteInfo.Height;

                if (y >= _startCharacterWaistY)
                {
                    mod.X = 0;
                }
                else if (itemsEndY <= _startCharacterWaistY)
                {
                    mod.X = 1.0f;
                }
                else
                {
                    float upperBodyDiff = _startCharacterWaistY - y;
                    mod.X = upperBodyDiff / spriteInfo.Height;

                    if (mod.X < 0)
                    {
                        mod.X = 0;
                    }
                }

                if (_startCharacterWaistY >= itemsEndY || y >= _startCharacterKneesY)
                {
                    mod.Y = 0;
                }
                else if (_startCharacterWaistY <= y && itemsEndY <= _startCharacterKneesY)
                {
                    mod.Y = 1.0f;
                }
                else
                {
                    float midBodyDiff;

                    if (y >= _startCharacterWaistY)
                    {
                        midBodyDiff = _startCharacterKneesY - y;
                    }
                    else if (itemsEndY <= _startCharacterKneesY)
                    {
                        midBodyDiff = itemsEndY - _startCharacterWaistY;
                    }
                    else
                    {
                        midBodyDiff = _startCharacterKneesY - _startCharacterWaistY;
                    }

                    mod.Y = mod.X + midBodyDiff / spriteInfo.Height;

                    if (mod.Y < 0)
                    {
                        mod.Y = 0;
                    }
                }

                if (itemsEndY <= _startCharacterKneesY)
                {
                    mod.Z = 0;
                }
                else if (y >= _startCharacterKneesY)
                {
                    mod.Z = 1.0f;
                }
                else
                {
                    float lowerBodyDiff = itemsEndY - _startCharacterKneesY;
                    mod.Z = mod.Y + lowerBodyDiff / spriteInfo.Height;

                    if (mod.Z < 0)
                    {
                        mod.Z = 0;
                    }
                }
            }

            return mod;
        }

        public override bool CheckMouseSelection()
        {
            Point position = RealScreenPosition;
            position.Y -= 3;
            position.X += (int)Offset.X + 22;
            position.Y += (int)(Offset.Y - Offset.Z) + 22;

            Rectangle r = FrameInfo;
            r.X = position.X - r.X;
            r.Y = position.Y - r.Y;

            if (!r.Contains(SelectedObject.TranslatedMousePositionByViewport))
            {
                return false;
            }

            // Drawn as a KR particle scene: the box is the creature.
            if (Managers.KrParticles.IsDrawnAsScene(Serial))
            {
                return true;
            }

            bool isHuman = IsHuman;
            bool isGargoyle =
                Client.Game.UO.Version >= ClientVersion.CV_7000
                && (Graphic == 666 || Graphic == 667 || Graphic == 0x02B7 || Graphic == 0x02B6);

            var animations = Client.Game.UO.Animations;

            ProcessSteps(out byte dir);
            bool isFlipped = IsFlipped;
            animations.GetAnimDirection(ref dir, ref isFlipped);

            ushort graphic = GetGraphicForAnimation();
            byte animGroup = GetGroupForAnimation(this, graphic, true);
            byte animIndex = AnimIndex;

            byte animGroupBackup = animGroup;
            byte animIndexBackup = animIndex;

            SpriteInfo spriteInfo;
            bool isUop;

            if (isHuman)
            {
                Item mount = FindItemByLayer(Layer.Mount);
                if (mount != null)
                {
                    var mountGraphic = mount.GetGraphicForAnimation();

                    if (mountGraphic != 0xFFFF)
                    {
                        var animGroupMount = RiddenGroup(mountGraphic, GetGroupForAnimation(this, mountGraphic));

                        if (
                            GetTexture(
                                mountGraphic,
                                animGroupMount,
                                ref animIndex,
                                dir,
                                out spriteInfo,
                                out isUop
                            )
                        )
                        {
                            int x =
                                position.X
                                - (
                                    isFlipped
                                        ? spriteInfo.Width - spriteInfo.Center.X
                                        : spriteInfo.Center.X
                                );
                            int y = position.Y - (spriteInfo.Height + spriteInfo.Center.Y);

                            if (
                                animations.PixelCheck(
                                    mountGraphic,
                                    animGroupMount,
                                    dir,
                                    isUop,
                                    animIndex,
                                    // Back into the frame's own pixels: the
                                    // mask was built from the atlas entry, and
                                    // for KR art that is not the size the frame
                                    // is drawn at.
                                    spriteInfo.ToSourceX(
                                        isFlipped
                                            ? x
                                                + spriteInfo.Width
                                                - SelectedObject.TranslatedMousePositionByViewport.X
                                            : SelectedObject.TranslatedMousePositionByViewport.X - x
                                    ),
                                    spriteInfo.ToSourceY(
                                        SelectedObject.TranslatedMousePositionByViewport.Y - y
                                    )
                                )
                            )
                            {
                                return true;
                            }

                            if (Mounts.TryGet(mount.Graphic, out var moutInfo))
                            {
                                position.Y += moutInfo.OffsetY;
                            }
                        }
                    }
                }
            }

            if (GetTexture(graphic, animGroup, ref animIndex, dir, out spriteInfo, out isUop))
            {
                int x =
                    position.X
                    - (isFlipped ? spriteInfo.Width - spriteInfo.Center.X : spriteInfo.Center.X);
                int y = position.Y - (spriteInfo.Height + spriteInfo.Center.Y);

                if (
                    animations.PixelCheck(
                        graphic,
                        animGroup,
                        dir,
                        isUop,
                        animIndex,
                        spriteInfo.ToSourceX(
                            isFlipped
                                ? x
                                    + spriteInfo.Width
                                    - SelectedObject.TranslatedMousePositionByViewport.X
                                : SelectedObject.TranslatedMousePositionByViewport.X - x
                        ),
                        spriteInfo.ToSourceY(
                            SelectedObject.TranslatedMousePositionByViewport.Y - y
                        )
                    )
                )
                {
                    return true;
                }
            }

            if (!IsEmpty && isHuman)
            {
                for (Layer layer = Layer.Invalid + 1; layer < Layer.Mount; ++layer)
                {
                    Item item = FindItemByLayer(layer);

                    if (
                        item == null
                        || (IsDead && (layer == Layer.Hair || layer == Layer.Beard))
                        || IsCovered(this, layer)
                    )
                    {
                        continue;
                    }

                    graphic = GetAnimationInfo(this, item, isGargoyle);

                    if (graphic != 0xFFFF)
                    {
                        animGroup = animGroupBackup;
                        animIndex = animIndexBackup;

                        if (
                            GetTexture(
                                graphic,
                                animGroup,
                                ref animIndex,
                                dir,
                                out spriteInfo,
                                out isUop
                            )
                        )
                        {
                            int x =
                                position.X
                                - (
                                    isFlipped
                                        ? spriteInfo.Width - spriteInfo.Center.X
                                        : spriteInfo.Center.X
                                );
                            int y = position.Y - (spriteInfo.Height + spriteInfo.Center.Y);

                            if (
                                animations.PixelCheck(
                                    graphic,
                                    animGroup,
                                    dir,
                                    isUop,
                                    animIndex,
                                    // Back into the frame's own pixels: the
                                    // mask was built from the atlas entry, and
                                    // for KR art that is not the size the frame
                                    // is drawn at.
                                    spriteInfo.ToSourceX(
                                        isFlipped
                                            ? x
                                                + spriteInfo.Width
                                                - SelectedObject.TranslatedMousePositionByViewport.X
                                            : SelectedObject.TranslatedMousePositionByViewport.X - x
                                    ),
                                    spriteInfo.ToSourceY(
                                        SelectedObject.TranslatedMousePositionByViewport.Y - y
                                    )
                                )
                            )
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        internal static bool IsCovered(Mobile mobile, Layer layer)
        {
            if (mobile.IsEmpty)
            {
                return false;
            }

            // Explicit per-layer occlusion. The paint order alone is not enough: a layer's
            // gump can paint outside the bounds of the item meant to cover it (oversized
            // custom art leaks past the occluder — e.g. chest 0x3DC0 sticking out below
            // robe 0x3CAC), so layers that an occluder is expected to fully hide are culled
            // here. Robe graphics are compared raw (item.Graphic); pants/skirt/robe AnimIDs
            // use the equip AnimID (item.ItemData.AnimID). The exception robe graphics are
            // open/half robes that leave the chest/arms visible.
            Item robe = mobile.FindItemByLayer(Layer.Robe);
            ushort robeGfx = robe?.Graphic ?? 0;
            ushort robeAnim = robe?.ItemData.AnimID ?? 0;

            bool robeLeavesChestVisible =
                robeGfx == 0x9985 || robeGfx == 0x9986 || robeGfx == 0xA2CA
                || robeGfx == 0xA2CB || robeGfx == 0xA412 || robeGfx == 0xB1DE;

            switch (layer)
            {
                case Layer.Shoes:
                {
                    Item pants = mobile.FindItemByLayer(Layer.Pants);
                    ushort pantsAnim = pants?.ItemData.AnimID ?? 0;

                    if (robeAnim != 0x504 && pantsAnim != 0x513 && pantsAnim != 0x514)
                    {
                        ushort pantsGfx = pants?.Graphic ?? 0;

                        if (pantsGfx < 0xAEB2)
                        {
                            if (pantsGfx == 0xAEB1 || pantsGfx == 0x1411)
                            {
                                return true;
                            }

                            if (pantsGfx != 0xAEA2)
                            {
                                // plate/studded legs paint under shoes
                                return mobile.FindItemByLayer(Layer.Legs) != null;
                            }
                        }
                        else
                        {
                            if (pantsGfx == 0xAEC0)
                            {
                                return true;
                            }

                            if (pantsGfx != 0xAECF)
                            {
                                return mobile.FindItemByLayer(Layer.Legs) != null;
                            }
                        }
                    }

                    return true;
                }

                case Layer.Pants:
                {
                    if (mobile.FindItemByLayer(Layer.Legs) != null || robeAnim == 0x504)
                    {
                        return true;
                    }

                    Item pants = mobile.FindItemByLayer(Layer.Pants);
                    ushort pantsAnim = pants?.ItemData.AnimID ?? 0;

                    if (pantsAnim != 0x1EB && pantsAnim != 0x1FA && pantsAnim != 0x200)
                    {
                        return false;
                    }

                    Item skirt = mobile.FindItemByLayer(Layer.Skirt);

                    if (skirt != null)
                    {
                        ushort skirtAnim = skirt.ItemData.AnimID;

                        if (skirtAnim != 0x1C7 && skirtAnim != 0x1E4)
                        {
                            return true;
                        }
                    }

                    if (robe == null)
                    {
                        return false;
                    }

                    if (robeAnim < 0x4EC)
                    {
                        if (robeAnim > 0x4E7)
                        {
                            return false;
                        }

                        return robeAnim != 0x229;
                    }

                    return (uint)(robeAnim - 0x5E2) > 3;
                }

                case Layer.Tunic:
                {
                    // tunic AnimID 0x238 is moved on top of the robe (surcoat); hide it
                    // when a full robe is worn underneath.
                    Item tunic = mobile.FindItemByLayer(Layer.Tunic);

                    if (tunic != null && tunic.ItemData.AnimID == 0x0238)
                    {
                        return robe != null && !robeLeavesChestVisible;
                    }

                    break;
                }

                case Layer.Torso:
                {
                    if (robeGfx != 0 && !robeLeavesChestVisible)
                    {
                        return true;
                    }

                    Item tunic = mobile.FindItemByLayer(Layer.Tunic);

                    if (tunic != null && tunic.Graphic != 0x1541 && tunic.Graphic != 0x1542)
                    {
                        Item torso = mobile.FindItemByLayer(Layer.Torso);

                        if (torso != null && (torso.Graphic == 0x782A || torso.Graphic == 0x782B))
                        {
                            return true;
                        }
                    }

                    break;
                }

                case Layer.Arms:
                    return robeGfx != 0 && !robeLeavesChestVisible;

                case Layer.Necklace:
                {
                    if (robe == null)
                    {
                        return false;
                    }

                    // open/half robes that leave a neck item (AnimID 0x5EC) visible
                    if (robeAnim == 0x5F2 || robeAnim == 0x5F5
                        || (robeAnim >= 0x4E8 && robeAnim <= 0x4EB)
                        || (robeAnim >= 0x5E2 && robeAnim <= 0x5E5))
                    {
                        return false;
                    }

                    Item neck = mobile.FindItemByLayer(Layer.Necklace);

                    return neck != null && neck.ItemData.AnimID == 0x5EC;
                }

                case Layer.Bracelet:
                {
                    Item bracelet = mobile.FindItemByLayer(Layer.Bracelet);

                    return bracelet != null
                        && bracelet.Graphic == 0xB1C0
                        && mobile.FindItemByLayer(Layer.Arms) != null;
                }

                case Layer.Hair:
                {
                    Item helmet = mobile.FindItemByLayer(Layer.Helmet);

                    if (helmet != null && (uint)(helmet.Graphic - 0xA42B) < 2)
                    {
                        return true;
                    }

                    goto case Layer.Helmet;
                }

                case Layer.Helmet:
                    // hair/helmet hidden under a hood-style robe
                    if (robeGfx < 0x4B9E)
                    {
                        if (robeGfx != 0x4B9D)
                        {
                            if (robeGfx < 0x2FBA)
                            {
                                if (robeGfx != 0x2FB9)
                                {
                                    if (robeGfx > 0x2687)
                                    {
                                        return false;
                                    }

                                    if (robeGfx < 0x2683 && (robeGfx < 0x204E || robeGfx > 0x204F))
                                    {
                                        return false;
                                    }
                                }
                            }
                            else if (robeGfx != 0x3173)
                            {
                                return false;
                            }

                            return true;
                        }
                    }
                    else if (robeGfx < 0xA0B0)
                    {
                        if (robeGfx < 0xA0AB && robeGfx != 0x7816)
                        {
                            return false;
                        }
                    }
                    else if (robeGfx != 0xB2B7)
                    {
                        return false;
                    }

                    // these hoods cover the head on every body except gargoyles
                    bool isGargoyle = mobile.Graphic == 0x029A || mobile.Graphic == 0x029B
                        || mobile.Graphic == 0x02B6 || mobile.Graphic == 0x02B7;

                    return !isGargoyle;

                case Layer.Skirt:
                {
                    Item skirt = mobile.FindItemByLayer(Layer.Skirt);
                    ushort skirtAnim = skirt?.ItemData.AnimID ?? 0;

                    if (skirtAnim != 0x1C7 && skirtAnim != 0x1E4)
                    {
                        return false;
                    }

                    Item pants = mobile.FindItemByLayer(Layer.Pants);
                    ushort pantsAnim = pants?.ItemData.AnimID ?? 0;

                    return pantsAnim == 0x1EB || pantsAnim == 0x1FA || pantsAnim == 0x200;
                }
            }

            return false;
        }
    }
}
