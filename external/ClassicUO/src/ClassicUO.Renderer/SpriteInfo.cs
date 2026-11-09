using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// One decoded art or animation frame, as it sits in the texture atlas.
    /// </summary>
    /// <remarks>
    /// A frame has two coordinate systems, and they are no longer the same
    /// one. <see cref="Source"/> is the frame's rectangle in the atlas, in the
    /// art's own pixels, and is what the sampler reads. <see cref="Width"/>,
    /// <see cref="Height"/> and <see cref="Center"/> are what the frame
    /// occupies on screen before the camera's zoom - which is the unit every
    /// anchoring, bounding-box and hit-test calculation in the client is
    /// written in. <see cref="Scale"/> converts the first into the second.
    ///
    /// For 2D art the two are the same and <see cref="Scale"/> is 1. Kingdom
    /// Reborn art is drawn about 1.5x the size of the 2D art, so it goes into
    /// the atlas at its native resolution and is scaled down at draw time
    /// instead. That way the camera's zoom and the KR scale compose into one
    /// sampling step from native pixels, and zooming in reveals KR's real
    /// detail rather than magnifying an already-downscaled sprite.
    ///
    /// The field holding the atlas rectangle used to be called UV, and the
    /// rename to Source was the point of the change rather than tidying: every
    /// site that read UV was assuming the two coordinate systems were
    /// interchangeable. Renaming it makes the compiler point at each one, so a
    /// site that still needs visiting cannot quietly draw a mobile in the
    /// wrong place.
    /// </remarks>
    public struct SpriteInfo
    {
        private float _scale;

        public Texture2D Texture;

        /// <summary>Where this frame's own pixels live in the atlas.</summary>
        public Rectangle Source;

        /// <summary>
        /// The frame's anchor, in screen pixels, stored as -x1 and -y2: the
        /// renderer puts the frame's top-left at
        /// <c>(x - Center.X, y - (Height + Center.Y))</c>.
        /// </summary>
        public Point Center;

        /// <summary>The frame's width on screen, before the camera's zoom.</summary>
        public int Width;

        /// <summary>The frame's height on screen, before the camera's zoom.</summary>
        public int Height;

        /// <summary>
        /// True when the pixels came from a Kingdom Reborn package rather than
        /// the 2D art. The hue shader needs to know: KR art is full 32-bit
        /// colour, so the red-channel ramp index the 2D art relies on produces
        /// a flat blob on it. Set only by the KR branch of
        /// <c>Animations.GetAnimationFrames</c>; everything else leaves it
        /// false and renders exactly as before.
        /// </summary>
        public bool IsKrArt;

        /// <summary>
        /// Factor from <see cref="Source"/> pixels to screen pixels. Reads as
        /// 1 when unset, so a <c>default(SpriteInfo)</c> behaves the way it
        /// always has.
        /// </summary>
        public float Scale
        {
            readonly get => _scale <= 0f ? 1f : _scale;
            set => _scale = value;
        }

        public static readonly SpriteInfo Empty = new SpriteInfo { Texture = null };

        /// <summary>
        /// Builds a frame, deriving its on-screen geometry from the atlas
        /// rectangle and the anchor the art was decoded with.
        /// </summary>
        /// <remarks>
        /// This scales the frame's <em>bounds</em>, not its size and its
        /// anchor separately. <see cref="Center"/>.Y is the bottom bound
        /// negated, so rounding the height and the anchor apart would let a
        /// mobile's feet drift off the tile it stands on; scaling x1/x2 and
        /// y1/y2 and taking the size from the difference keeps both edges
        /// within half a pixel of the true scaled position. Same reasoning as
        /// <c>AmouFrameScaler.Scale</c>, which did this to the pixels back
        /// when the resample happened at load time.
        /// </remarks>
        public static SpriteInfo Create(
            Texture2D texture,
            Rectangle source,
            int centerX,
            int centerY,
            float scale,
            bool isKrArt
        )
        {
            // Bounds relative to the anchor, as the frame table stores them.
            int x1 = -centerX;
            int y2 = -centerY;
            int x2 = x1 + source.Width;
            int y1 = y2 - source.Height;

            int sx1 = ScaleBound(x1, scale);
            int sy1 = ScaleBound(y1, scale);

            // A frame thinner than one screen pixel still has to exist; push
            // the far edge out rather than the anchor-bearing near edge.
            int sx2 = Math.Max(ScaleBound(x2, scale), sx1 + 1);
            int sy2 = Math.Max(ScaleBound(y2, scale), sy1 + 1);

            return new SpriteInfo
            {
                Texture = texture,
                Source = source,
                Center = new Point(-sx1, -sy2),
                Width = sx2 - sx1,
                Height = sy2 - sy1,
                Scale = scale,
                IsKrArt = isKrArt
            };
        }

        /// <summary>
        /// Points the frame at an atlas rectangle it is drawn from 1:1 - the
        /// 2D art producers, where the two coordinate systems coincide.
        /// </summary>
        public void SetSource(Rectangle source)
        {
            Source = source;
            Width = source.Width;
            Height = source.Height;
            Scale = 1f;
        }

        /// <summary>
        /// Turns an X inside the frame, in screen pixels, into the column of
        /// the frame's own pixels drawn there, or -1 when the point is outside
        /// the frame. The PixelPicker hit mask is built from the pixels as
        /// they go into the atlas, so a click has to come back through this
        /// before it is tested against the mask.
        /// </summary>
        /// <remarks>
        /// Two details, both measured over 32,655 KR frames rather than
        /// assumed.
        ///
        /// The half-pixel: the world batch samples with <c>PointClamp</c>,
        /// which takes the texel under the centre of the screen pixel, so the
        /// mask has to be read the same way. Without the 0.5 the clickable
        /// area sat a texel off the silhouette on 1.8% of pixels, all of them
        /// on the outline.
        ///
        /// The clamp: rounding the frame's bounds can leave the on-screen
        /// width a hair wider than the scaled source, which sent the last
        /// column past the edge of the mask on 0.18% of pixels and made the
        /// outline unclickable. <c>PointClamp</c> clamps, so this does too -
        /// but only at the top end. A point outside the frame has to stay
        /// outside, or a click just past a sprite would land on its edge
        /// column.
        /// </remarks>
        public readonly int ToSourceX(int screen)
        {
            if (screen < 0 || screen >= Width)
            {
                return -1;
            }

            return Scale == 1f
                ? screen
                : Math.Min((int)MathF.Floor((screen + 0.5f) / Scale), Source.Width - 1);
        }

        /// <summary>
        /// Turns a Y inside the frame, in screen pixels, into the row of the
        /// frame's own pixels drawn there, or -1 when the point is outside the
        /// frame. See <see cref="ToSourceX"/>.
        /// </summary>
        public readonly int ToSourceY(int screen)
        {
            if (screen < 0 || screen >= Height)
            {
                return -1;
            }

            return Scale == 1f
                ? screen
                : Math.Min((int)MathF.Floor((screen + 0.5f) / Scale), Source.Height - 1);
        }

        /// <summary>
        /// Turns a distance in screen pixels into the same distance in the
        /// frame's own pixels. Unlike <see cref="ToSourcePoint"/> this is a
        /// length rather than a position, so it rounds instead of taking a
        /// pixel centre - used to cut the source rectangle where a measurement
        /// made in screen space says it should be cut.
        /// </summary>
        public readonly int ToSourceLength(int screen) =>
            Scale == 1f ? screen : Math.Max(1, (int)MathF.Round(screen / Scale));

        /// <summary>
        /// Rounds a scaled bound to the screen pixel grid, half up. Monotonic
        /// across the sign change, so a frame's bounds can never cross over.
        /// </summary>
        private static int ScaleBound(int bound, float scale) =>
            scale == 1f ? bound : (int)MathF.Floor(bound * scale + 0.5f);
    }
}
