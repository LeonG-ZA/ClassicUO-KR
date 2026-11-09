// SPDX-License-Identifier: BSD-2-Clause
//
// Sprite cache for Kingdom Reborn static art.
//
// This mirrors Arts - an atlas plus a lazily filled table of SpriteInfo - but
// is keyed by graphic alone, because a static is one sprite rather than a
// pattern spread over tiles the way a terrain texture is.
//
// Unlike the 2D art, KR's sprites are anti-aliased, so they go into the atlas
// premultiplied and with a transparent gutter. Without the gutter a filtered
// sample at a sprite's edge reaches into whatever the packer put next door;
// without premultiplication the filter composes colour and alpha separately
// and leaves a dark fringe around every edge.

using ClassicUO.Assets;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer.Statics
{
    public sealed class KrStatics
    {
        private const int GraphicCount = 0x4000;

        private readonly TextureAtlas _atlas;
        private readonly SpriteInfo[] _sprites = new SpriteInfo[GraphicCount];
        private readonly bool[] _missing = new bool[GraphicCount];

        public KrStatics(GraphicsDevice device)
        {
            _atlas = new TextureAtlas(device, 4096, 4096, SurfaceFormat.Color);
        }

        /// <summary>
        /// The KR sprite for a static graphic, or an empty one when KR has no
        /// art for it.
        /// </summary>
        /// <param name="fitWidth">
        /// The 2D sprite's width, which the KR sprite is resampled to so that
        /// tiling surfaces - roofs, floors - still meet exactly. Zero draws it
        /// at the provider's own scale instead.
        /// </param>
        public ref readonly SpriteInfo GetArt(
            ushort graphic, int fitWidth = 0, int fitHeight = 0, bool keepFit = false,
            int tileX = 0, int tileY = 0, bool flat = false)
        {
            if (graphic >= GraphicCount)
            {
                return ref SpriteInfo.Empty;
            }

            KrStaticProvider positional = KrStaticProvider.Instance;

            // A flat piece whose texture runs on across tiles has one sprite
            // per position within its span, not one per graphic.
            if (positional != null && positional.IsPositional(graphic, fitWidth, fitHeight, keepFit, flat))
            {
                return ref GetFlat(positional, graphic, fitWidth, fitHeight, keepFit, flat, tileX, tileY);
            }

            ref SpriteInfo info = ref _sprites[graphic];

            if (info.Texture != null || _missing[graphic])
            {
                return ref info;
            }

            KrStaticProvider provider = KrStaticProvider.Instance;

            if (provider == null
                || !provider.TryGetSprite(
                    graphic, fitWidth, fitHeight, keepFit, false, 0, 0,
                    out uint[] pixels, out int width, out int height, out int displayWidth, out int displayHeight))
            {
                _missing[graphic] = true;

                return ref SpriteInfo.Empty;
            }

            info.Texture = _atlas.AddSprite(
                pixels, width, height, out Rectangle source, padding: 1, premultiplyAlpha: true);

            SetDisplay(ref info, source, displayWidth, displayHeight);

            return ref info;
        }

        /// <summary>
        /// The atlas rectangle holds the sprite's own pixels (native detail);
        /// Width and Height are the size it is drawn at, which every anchor
        /// in the client is worked out from, and Scale the factor between.
        /// </summary>
        private static void SetDisplay(ref SpriteInfo info, Rectangle source, int displayWidth, int displayHeight)
        {
            info.SetSource(source);

            if (displayWidth > 0 && displayHeight > 0 && (displayWidth != source.Width || displayHeight != source.Height))
            {
                info.Width = displayWidth;
                info.Height = displayHeight;
                info.Scale = displayWidth / (float)source.Width;
            }
        }

        private readonly System.Collections.Generic.Dictionary<int, SpriteInfo> _flat = [];
        private SpriteInfo _flatScratch;

        private ref readonly SpriteInfo GetFlat(
            KrStaticProvider provider, ushort graphic, int fitWidth, int fitHeight, bool keepFit,
            bool flat, int tileX, int tileY)
        {
            // Position within 32 tiles on each axis: the largest period of a
            // terrain-style floor composite (KrTerrainArt.MaxCompositeRepeat);
            // carpet spans (up to 8) divide it.
            int key = (graphic << 10) | ((tileX & 31) << 5) | (tileY & 31);

            if (_flat.TryGetValue(key, out _flatScratch))
            {
                return ref _flatScratch;
            }

            if (!provider.TryGetSprite(
                    graphic, fitWidth, fitHeight, keepFit, flat, tileX, tileY,
                    out uint[] pixels, out int width, out int height, out int displayWidth, out int displayHeight))
            {
                _flatScratch = default;
                _flat[key] = _flatScratch;

                return ref _flatScratch;
            }

            SpriteInfo info = default;
            info.Texture = _atlas.AddSprite(
                pixels, width, height, out Rectangle source, padding: 1, premultiplyAlpha: true);
            SetDisplay(ref info, source, displayWidth, displayHeight);

            _flat[key] = info;
            _flatScratch = info;

            return ref _flatScratch;
        }
    }
}
