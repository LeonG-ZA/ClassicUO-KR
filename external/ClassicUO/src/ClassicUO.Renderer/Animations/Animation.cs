using System;
using System.Runtime.CompilerServices;
using ClassicUO.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Animations
{
    public sealed class Animations
    {
        const int MAX_ANIMATIONS_DATA_INDEX_COUNT = 2048;

        private readonly GraphicsDevice _device;
        private TextureAtlas _atlas;
        private readonly PixelPicker _picker = new PixelPicker();
        private readonly AnimationsLoader _animationLoader;
        private IndexAnimation[] _dataIndex = new IndexAnimation[MAX_ANIMATIONS_DATA_INDEX_COUNT];

        private AnimationDirection[][][] _cache;

        public Animations(AnimationsLoader animationLoader, GraphicsDevice device)
        {
            _animationLoader = animationLoader;
            _device = device;
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
        }

        /// <summary>
        /// Throws away every decoded animation sprite so the next draw rebuilds
        /// it from the source art.
        /// </summary>
        /// <remarks>
        /// Needed when something that changes how a frame is produced is altered
        /// while the client is running - today that is the KR frame scale. The
        /// cached <see cref="SpriteInfo"/> values point into the atlas, so the
        /// cache and the atlas have to go together: clearing the cache alone
        /// would leave the old sprites occupying atlas pages forever, and
        /// replacing the atlas alone would leave dangling textures. Call this
        /// between frames, not mid-draw.
        /// </remarks>
        public void ResetSprites()
        {
            _cache = null;

            _atlas.Dispose();
            _atlas = new TextureAtlas(_device, 4096, 4096, SurfaceFormat.Color);
        }


        private ref AnimationDirection GetSprite(int body, int action, int dir)
        {
            if (_cache == null)
                _cache = new AnimationDirection[Math.Max(body, MAX_ANIMATIONS_DATA_INDEX_COUNT)][][];

            if (body >= _cache.Length)
                Array.Resize(ref _cache, body);

            if (_cache[body] == null)
                _cache[body] = new AnimationDirection[AnimationsLoader.MAX_ACTIONS][];

            if (_cache[body][action] == null)
                _cache[body][action] = new AnimationDirection[AnimationsLoader.MAX_DIRECTIONS];

            return ref _cache[body][action][dir];
        }

        public int MaxAnimationCount => _dataIndex.Length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationGroupsType GetAnimType(ushort graphic) => graphic < _dataIndex.Length ? _dataIndex[graphic]?.Type ?? 0 : 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public AnimationFlags GetAnimFlags(ushort graphic) => graphic < _dataIndex.Length ? _dataIndex[graphic]?.Flags ?? 0 : 0;

        /// <param name="x">
        /// Horizontal offset into the frame, in the frame's own pixels. The
        /// mask is built from the frame as it goes into the atlas, so a caller
        /// working in screen pixels has to put the offset through
        /// <see cref="SpriteInfo.ToSource"/> first - for KR art the two are
        /// not the same.
        /// </param>
        /// <param name="y">Vertical offset into the frame, in the frame's own pixels.</param>
        public bool PixelCheck(
            ushort animID,
            byte group,
            byte direction,
            bool uop,
            int frame,
            int x,
            int y
        )
        {
            ConvertBodyIfNeeded(ref animID);

            if (uop)
            {
                _animationLoader.ReplaceUopGroup(animID, ref group);
            }

            uint packed32 = (uint)((group | (direction << 8) | ((uop ? 0x01 : 0x00) << 16)));
            uint packed32_2 = (uint)((animID | (frame << 16)));
            ulong packed = (packed32_2 | ((ulong)packed32 << 32));

            return _picker.Get(packed, x, y);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetAnimDirection(ref byte dir, ref bool mirror)
        {
            switch (dir)
            {
                case 2:
                case 4:
                    mirror = dir == 2;
                    dir = 1;

                    break;

                case 1:
                case 5:
                    mirror = dir == 1;
                    dir = 2;

                    break;

                case 0:
                case 6:
                    mirror = dir == 0;
                    dir = 3;

                    break;

                case 3:
                    dir = 0;

                    break;

                case 7:
                    dir = 4;

                    break;
            }
        }

        public void GetAnimationDimensions(
            byte animIndex,
            ushort graphic,
            byte dir,
            byte animGroup,
            bool ismounted,
            byte frameIndex,
            out int centerX,
            out int centerY,
            out int width,
            out int height
        )
        {
            dir &= 0x7F;
            bool mirror = false;
            GetAnimDirection(ref dir, ref mirror);

            if (frameIndex == 0xFF)
            {
                frameIndex = (byte)animIndex;
            }

            var frames = GetAnimationFrames(graphic, animGroup, dir, out _, out _, false);

            if (!frames.IsEmpty && frames[frameIndex].Texture != null)
            {
                // Screen pixels, not atlas pixels: overhead text, health bars
                // and the name gump all place themselves off these and are
                // laid out in screen space.
                centerX = frames[frameIndex].Center.X;
                centerY = frames[frameIndex].Center.Y;
                width = frames[frameIndex].Width;
                height = frames[frameIndex].Height;
                return;
            }

            centerX = 0;
            centerY = 0;
            width = 0;
            height = ismounted ? 100 : 60;
        }

        private IndexAnimation GetIndexAnim(ushort id, ref ushort hue, bool isCorpse)
        {
            if (id >= ushort.MaxValue)
                return null;

            if (id >= _dataIndex.Length)
                Array.Resize(ref _dataIndex, id + 1);

            ref var index = ref _dataIndex[id];
            do
            {
                if (index == null)
                {
                    index = new IndexAnimation();
                    var indices = _animationLoader.GetIndices
                    (
                        _animationLoader.FileManager.Version,
                        id,
                        ref hue,
                        ref index.Flags,
                        out index.FileIndex,
                        out index.Type
                    );

                    if (!indices.IsEmpty)
                    {
                        if ((index.Flags & AnimationFlags.UseUopAnimation) != 0)
                        {
                            index.UopGroups = new AnimationGroupUop[indices.Length];
                            for (int i = 0; i < index.UopGroups.Length; i++)
                            {
                                index.UopGroups[i] = new AnimationGroupUop();
                                index.UopGroups[i].FileIndex = index.FileIndex;
                                index.UopGroups[i].DecompressedLength = indices[i].UncompressedSize;
                                index.UopGroups[i].CompressedLength = indices[i].Size;
                                index.UopGroups[i].Offset = indices[i].Position;
                                index.UopGroups[i].CompressionType = indices[i].CompressionType;
                            }

                            break;
                        }

                        index.Groups = new AnimationGroup[indices.Length / AnimationsLoader.MAX_DIRECTIONS];
                        for (int i = 0; i < index.Groups.Length; i++)
                        {
                            index.Groups[i] = new AnimationGroup();

                            for (int d = 0; d < AnimationsLoader.MAX_DIRECTIONS; d++)
                            {
                                ref readonly var animIdx = ref indices[i * AnimationsLoader.MAX_DIRECTIONS + d];
                                index.Groups[i].Direction[d].Address = animIdx.Position;
                                index.Groups[i].Direction[d].Size = /*index.FileIndex > 0 ? Math.Max(1, animIdx.Size) :*/ animIdx.Size;
                                index.Groups[i].Direction[d].IsVerdata = animIdx.IsVerdata;
                            }
                        }

                        //if (index.FileIndex == 0)
                        //{
                        //    var replaced = isCorpse ? _animationLoader.ReplaceCorpse(ref id, ref hue) : _animationLoader.ReplaceBody(ref id, ref hue);
                        //    if (replaced)
                        //    {
                        //        if (id >= _dataIndex.Length)
                        //        {
                        //            Array.Resize(ref _dataIndex, id + 1);
                        //        }

                        //        index = ref _dataIndex[id];
                        //    }
                        //}
                    }
                }
            } while (index == null);

            // Whatever the 2D index listed for it (entries pointing at no art
            // stopped the draw before KR was asked), set up once as KR's.
            if (!index.KrOwn && KrDrawsOwnBody(id, index))
            {
                index.KrOwn = true;
                KrOwnBodyIndex(id, index, isCorpse);
            }

            return index;
        }

        /// <summary>
        /// A body KR has frames for and the 2D files have no art of their own
        /// for: Body.def would draw it as another body (the Blood Elemental,
        /// 159, as a red Water Elemental, 16), and KR would then be asked for
        /// that body's frames. KR's own body is drawn instead.
        /// </summary>
        private bool KrDrawsOwnBody(ushort id, IndexAnimation index) =>
            index != null
            && (index.FileIndex == 0 && (index.Flags & AnimationFlags.UseUopAnimation) == 0
                && KrAnimationProvider.Instance?.HasBody(id) == true
                // A worn item's newer KR anim id: KR's art, whatever the 2D files hold for it.
                || KrAnimationProvider.Instance?.IsEquipmentAnim(id) == true);

        /// <summary>
        /// Its own, empty entry - frames are cached per entry, so sharing the
        /// stand-in's would draw one creature's frames for both - typed as
        /// the body Body.def names, so its actions are chosen the same way.
        /// </summary>
        private void KrOwnBodyIndex(ushort id, IndexAnimation index, bool isCorpse)
        {
            ushort target = id, targetHue = 0;

            IndexAnimation stand = null;

            if (KrAnimationProvider.Instance.IsEquipmentAnim(id))
            {
                index.Type = AnimationGroupsType.Equipment;
                index.Flags = 0;
            }
            else if ((isCorpse ? _animationLoader.ReplaceCorpse(ref target, ref targetHue) : _animationLoader.ReplaceBody(ref target, ref targetHue))
                && target != id && GetIndexAnim(target, ref targetHue, isCorpse) is { } found)
            {
                stand = found;
                index.Type = stand.Type;
                index.Flags = stand.Flags & ~AnimationFlags.UseUopAnimation;
            }
            else if (index.Type == AnimationGroupsType.Unknown)
            {
                index.Type = AnimationGroupsType.Monster;
            }

            index.Groups = new AnimationGroup[AnimationsLoader.MAX_ACTIONS];

            for (int i = 0; i < index.Groups.Length; i++)
            {
                index.Groups[i] = new AnimationGroup();

                // Only the actions KR has art for (and walk and stand, which a
                // creature always needs, KR redirecting them as it likes):
                // offered every one, the Blood Elemental idled through
                // fidgets (17, 18) that KR plays as its stand, restarting it
                // each time - a twitching fit.
                // EC's worn items lack stand (4) and fall back in the provider (to 37), so all their actions stay open.
                if (i > 1 && !(KrAnimationProvider.Instance.EcPeople && index.Type == AnimationGroupsType.Equipment)
                    && KrAnimationProvider.Instance.ResolveAction(id, i) != i)
                {
                    for (int d = 0; d < AnimationsLoader.MAX_DIRECTIONS; d++)
                    {
                        index.Groups[i].Direction[d].Address = uint.MaxValue;
                    }
                }
            }
        }

        public Span<SpriteInfo> GetAnimationFrames(
            ushort id,
            byte action,
            byte dir,
            out ushort hue,
            out bool useUOP,
            bool isEquip = false,
            bool isCorpse = false,
            bool forceUOP = false
        )
        {
            hue = 0;
            useUOP = false;

            if (action >= AnimationsLoader.MAX_ACTIONS || dir >= AnimationsLoader.MAX_DIRECTIONS)
            {
                return Span<SpriteInfo>.Empty;
            }

            var index = GetIndexAnim(id, ref hue, isCorpse);
            if (index == null)
                return Span<SpriteInfo>.Empty;

            useUOP = (index.Flags & AnimationFlags.UseUopAnimation) != 0;

            if (KrDrawsOwnBody(id, index))
            {
                hue = 0;
            }
            else while (!useUOP && index.FileIndex == 0)
            {
                var replaced = isCorpse ? _animationLoader.ReplaceCorpse(ref id, ref hue) : _animationLoader.ReplaceBody(ref id, ref hue);
                if (!replaced)
                    break;

                index = GetIndexAnim(id, ref hue, isCorpse);
                if (index == null)
                    return Span<SpriteInfo>.Empty;
            }


            index.Hue = hue;

            if (useUOP)
            {
                _animationLoader.ReplaceUopGroup(id, ref action);
            }

            // When we are searching for an equipment item we must ignore any other animation which is not equipment
            var currentAnimType = GetAnimType(id);
            if (isEquip && currentAnimType != AnimationGroupsType.Equipment && currentAnimType != AnimationGroupsType.Human)
            {
                return Span<SpriteInfo>.Empty;
            }

            // NOTE:
            // for UOP: we don't call the method index.GetUopGroup(ref x) because the action has been already changed by the method ReplaceAnimationValues
            AnimationGroup groupObj = null;
            if (useUOP)
            {
                if (index.UopGroups == null || action >= index.UopGroups.Length)
                    return Span<SpriteInfo>.Empty;
                groupObj = index.UopGroups[action];
            }
            else if (index.Groups != null && action < index.Groups.Length)
            {
                groupObj = index.Groups[action];
            }

            if (groupObj == null)
            {
                return Span<SpriteInfo>.Empty;
            }

            ref var animDir = ref groupObj.Direction[dir];

            if (animDir.Address == uint.MaxValue)
            {
                return Span<SpriteInfo>.Empty;
            }

            Span<AnimationsLoader.FrameInfo> frames = default;
            bool fromKr = false;
            double krScale = 1.0;

            if (animDir.FrameCount <= 0 && animDir.SpriteInfos == null)
            {
                // Kingdom Reborn art, when the user has pointed us at a UOKR
                // install and it ships this body/action. Anything KR is missing
                // falls through to the 2D art below.
                // KR indexes animals the way ClassicUO indexes monsters, so the
                // provider needs to know which numbering this body is being
                // driven with before it can pick the right action.
                var scheme = AnimationsLoader.GetAnimationGroup(currentAnimType, index.Flags);

                if (KrAnimationProvider.Instance != null &&
                    KrAnimationProvider.Instance.TryReadFrames(id, action, dir, scheme, out var krFrames, out krScale))
                {
                    frames = krFrames;
                    fromKr = true;
                }
                else if (useUOP
                //animDir.IsUOP ||
                ///* If it's not flagged as UOP, but there is no mul data, try to load
                //* it as a UOP anyway. */
                //(animDir.Address == 0 && animDir.Size == 0)
                )
                {
                    var uopGroupObj = (AnimationGroupUop)groupObj;
                    var ff = new AnimationsLoader.AnimationDirection()
                    {
                        Position = uopGroupObj.Offset,
                        Size = uopGroupObj.CompressedLength,
                        UncompressedSize = uopGroupObj.DecompressedLength,
                        CompressionType = uopGroupObj.CompressionType
                    };

                    frames = _animationLoader.ReadUOPAnimationFrames(
                        id,
                        action,
                        dir,
                        index.Type,
                        index.FileIndex,
                        ff
                    );
                }
                else
                {
                    var ff = new AnimationsLoader.AnimationDirection()
                    {
                        Position = groupObj.Direction[dir].Address,
                        Size = groupObj.Direction[dir].Size,
                    };

                    frames = _animationLoader.ReadMULAnimationFrames(index.FileIndex, ff, groupObj.Direction[dir].IsVerdata);
                }

                if (frames.IsEmpty)
                {
                    animDir.FrameCount = 0;
                    animDir.SpriteInfos = Array.Empty<SpriteInfo>();
                    return Span<SpriteInfo>.Empty;
                }

                animDir.FrameCount = (byte)frames.Length;
                animDir.SpriteInfos = new SpriteInfo[frames.Length];

                // KR frames are atlased at their native resolution and shrunk
                // when they are drawn, so the camera's zoom and the KR scale
                // resolve to a single sampling step from KR's real pixels.
                // 2D art is already at the 44x44 tile grid's scale.
                // KR and EC frames each carry their own factor (EC's people
                // are smaller than KR's, ec_frame_scale).
                float frameScale = fromKr ? (float)krScale : 1f;

                for (int i = 0; i < frames.Length; i++)
                {
                    ref var frame = ref frames[i];
                    ref var spriteInfo = ref animDir.SpriteInfos[frame.Num];

                    if (frame.Width <= 0 || frame.Height <= 0)
                    {
                        spriteInfo = SpriteInfo.Empty;

                        /* Missing frame. */
                        continue;
                    }

                    uint keyUpper = (uint)((action | (dir << 8) | ((useUOP ? 1 : 0) << 16)));
                    uint keyLower = (uint)((id | (frame.Num << 16)));
                    ulong key = (keyLower | ((ulong)keyUpper << 32));

                    // The hit mask is in the frame's own pixels, matching what
                    // goes into the atlas. SpriteInfo.ToSource brings a click
                    // back into these coordinates.
                    _picker.Set(key, frame.Width, frame.Height, frame.Pixels);

                    // Only KR frames get the gutter and the premultiply: they
                    // are the only anti-aliased art here, so the only art the
                    // shader samples with linear filtering and the only art
                    // whose partial alpha the premultiplied blend state gets
                    // wrong. Padding the 2D frames too would cost atlas pages
                    // for nothing. The hit mask above is built from the
                    // straight pixels, before either.
                    var texture = _atlas.AddSprite(
                        frame.Pixels.AsSpan(),
                        frame.Width,
                        frame.Height,
                        out Rectangle source,
                        fromKr ? 1 : 0,
                        fromKr
                    );

                    spriteInfo = SpriteInfo.Create(
                        texture,
                        source,
                        frame.CenterX,
                        frame.CenterY,
                        frameScale,
                        fromKr
                    );
                }
            }

            return animDir.SpriteInfos.AsSpan(0, animDir.FrameCount);
        }

        public void UpdateAnimationTable(BodyConvFlags flags)
        {
            _animationLoader.ProcessBodyConvDef(flags);
            //if (flags != _lastFlags)
            //{
            //    if (_lastFlags != (BodyConvFlags)(-1))
            //    {
            //        /* This happens when you log out of an account then into another
            //         * one with different expansions activated. Just reload the anim
            //         * files from scratch. */
            //        Array.Clear(_dataIndex, 0, _dataIndex.Length);
            //        LoadInternal();
            //    }

            //    ProcessBodyConvDef(flags);
            //}

            //_lastFlags = flags;
        }

        public void ConvertBodyIfNeeded(
            ref ushort graphic,
            bool isParent = false,
            bool forceUOP = false,
            bool isCorpse = false
        )
        {
            if (graphic >= _dataIndex.Length)
                return;

            ushort hue = 0;

            if (_dataIndex[graphic] != null && _dataIndex[graphic].FileIndex == 0 && (_dataIndex[graphic].Flags & AnimationFlags.UseUopAnimation) == 0
                && !KrDrawsOwnBody(graphic, _dataIndex[graphic]))
                _ = isCorpse ? _animationLoader.ReplaceCorpse(ref graphic, ref hue) : _animationLoader.ReplaceBody(ref graphic, ref hue);
        }

        public bool AnimationExists(ushort graphic, byte group, bool isCorpse = false)
        {
            if (graphic < _dataIndex.Length && group < AnimationsLoader.MAX_ACTIONS)
            {
                var frames = GetAnimationFrames(
                    graphic,
                    group,
                    0,
                    out var _,
                    out _,
                    false,
                    isCorpse
                );

                return !frames.IsEmpty && frames[0].Texture != null;
            }

            return false;
        }

        private sealed class IndexAnimation
        {
            public int FileIndex;
            public ushort Hue;
            public AnimationFlags Flags;
            public AnimationGroup[] Groups;
            public AnimationGroupUop[] UopGroups;
            public AnimationGroupsType Type = AnimationGroupsType.Unknown;

            /// <summary>Drawn from KR's frames for this body itself (KrOwnBodyIndex).</summary>
            public bool KrOwn;
        }
    }
}