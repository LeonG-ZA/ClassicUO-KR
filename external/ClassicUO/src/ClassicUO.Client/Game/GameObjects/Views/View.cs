// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.Runtime.CompilerServices;
using ClassicUO.Configuration;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.GameObjects
{
    enum ObjectHandlesStatus
    {
        NONE,
        OPEN,
        CLOSED,
        DISPLAYING
    }

    internal abstract partial class GameObject
    {
        public byte AlphaHue;
        public bool AllowedToDraw = true;
        public bool InChunkMesh;
        public int MeshSpriteIndex = -1;
        public ObjectHandlesStatus ObjectHandlesStatus;
        public Rectangle FrameInfo;
        protected bool IsFlipped;

        public abstract bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public float CalculateDepthZ()
        {
            int x = X;
            int y = Y;
            int z = PriorityZ;

            // Offsets are in SCREEN coordinates
            if (Offset.X > 0 && Offset.Y < 0)
            {
                // North
            }
            else if (Offset.X > 0 && Offset.Y == 0)
            {
                // Northeast
                x++;
            }
            else if (Offset.X > 0 && Offset.Y > 0)
            {
                // East
                z += Math.Max(0, (int)Offset.Z);
                x++;
            }
            else if (Offset.X == 0 && Offset.Y > 0)
            {
                // Southeast
                x++;
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // South
                z += Math.Max(0, (int)Offset.Z);
                y++;
            }
            else if (Offset.X < 0 && Offset.Y == 0)
            {
                // Southwest
                y++;
            }
            else if (Offset.X < 0 && Offset.Y > 0)
            {
                // West
            }
            else if (Offset.X == 0 && Offset.Y < 0)
            {
                // Northwest
            }

            return (x + y) + (127 + z) * 0.01f;
        }

        public Rectangle GetOnScreenRectangle()
        {
            Rectangle prect = Rectangle.Empty;

            prect.X = (int)(RealScreenPosition.X - FrameInfo.X + 22 + Offset.X);
            prect.Y = (int)(RealScreenPosition.Y - FrameInfo.Y + 22 + (Offset.Y - Offset.Z));
            prect.Width = FrameInfo.Width;
            prect.Height = FrameInfo.Height;

            return prect;
        }

        public virtual bool TransparentTest(int z)
        {
            return false;
        }

        protected static void DrawStatic(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth,
            bool isWet = false
        )
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)((artInfo.Source.Width >> 1) - 22);
                index.Height = (short)(artInfo.Source.Height - 44);

                x -= index.Width;
                y -= index.Height;

                var pos = new Vector2(x, y);
                var scale = Vector2.One;
                if (isWet)
                {
                    batcher.Draw(
                        artInfo.Texture,
                        pos,
                        artInfo.Source,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );

                    var sin = (float)Math.Sin(Time.Ticks / 1000f);
                    var cos = (float)Math.Cos(Time.Ticks / 1000f);
                    scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f);
                }

                batcher.Draw(
                    artInfo.Texture,
                    pos,
                    artInfo.Source,
                    hue,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawGump(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            float depth
        )
        {
            ref readonly var gumpInfo = ref Client.Game.UO.Gumps.GetGump(graphic);

            if (gumpInfo.Texture != null)
            {
                batcher.Draw(
                    gumpInfo.Texture,
                    new Vector2(x, y),
                    gumpInfo.Source,
                    hue,
                    0f,
                    Vector2.Zero,
                    1f,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawStaticRotated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            float angle,
            Vector3 hue,
            float depth
        )
        {
            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            if (artInfo.Texture != null)
            {
                ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                index.Width = (short)((artInfo.Source.Width >> 1) - 22);
                index.Height = (short)(artInfo.Source.Height - 44);

                batcher.Draw(
                    artInfo.Texture,
                    new Rectangle(
                        x - index.Width,
                        y - index.Height,
                        artInfo.Source.Width,
                        artInfo.Source.Height
                    ),
                    artInfo.Source,
                    hue,
                    angle,
                    Vector2.Zero,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }

        protected static void DrawStaticAnimated(
            UltimaBatcher2D batcher,
            ushort graphic,
            int x,
            int y,
            Vector3 hue,
            bool shadow,
            float depth,
            bool isWet = false,
            int tileX = -1,
            int tileY = -1
        )
        {
            ref UOFileIndex index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);

            // Swamp is decided on the static's own graphic: its animation
            // frames (0x3241 and on) need not carry the KR swamp shader.
            ushort baseGraphic = graphic;
            bool swamp = ClassicUO.Game.Map.ChunkMesh.IsSwamp(graphic);
            bool lava = ClassicUO.Game.Map.ChunkMesh.IsLava(graphic);

            graphic = (ushort)(graphic + index.AnimOffset);

            swamp |= ClassicUO.Game.Map.ChunkMesh.IsSwamp(graphic);

            ref readonly var artInfo = ref Client.Game.UO.Arts.GetArt(graphic);

            // Only flat water is water: a water trough (0x0B41-0x0B44) is wet
            // but stands 6 high, and is a wooden object holding water.
            // Nor is what KR's water shader draws with no water texture: blood.
            bool openWater = Client.Game.UO.FileManager.TileData.StaticData[graphic].IsWet
                // Height 1 too: a water channel's 0x346E and 0x3494 (Trammel
                // 5264,3942), which KR draws as steady water, flickered
                // through their 2D frames.
                && Client.Game.UO.FileManager.TileData.StaticData[graphic].Height <= 1
                && !ClassicUO.Game.Map.ChunkMesh.KrNotWater(graphic);

            // Swamp before water: swamp statics with bubbles (0x3241) are wet and
            // flat too, but KR draws them with Swamp_Water_A, not as water.
            if (ShaderHueTranslator.WaterShader && hue.X == 0 && hue.Y == ShaderHueTranslator.SHADER_NONE && openWater && !swamp)
            {
                hue.Y = ShaderHueTranslator.SHADER_WATER;
            }
            else if (ShaderHueTranslator.WaterShader && hue.X == 0 && hue.Y == ShaderHueTranslator.SHADER_NONE
                && swamp)
            {
                hue.Y = ShaderHueTranslator.SHADER_SWAMP;
            }
            else if (ShaderHueTranslator.WaterShader && hue.X == 0 && hue.Y == ShaderHueTranslator.SHADER_NONE
                && lava)
            {
                hue.Y = ShaderHueTranslator.SHADER_LAVA;
            }

            // Kingdom Reborn art, for the statics the chunk mesh does not take:
            // trees, foliage, animated tiles and rocks all come through here.
            // The anchor below works from the sprite's own size, so KR art of a
            // different size needs no special case.
            var staticTexture = artInfo.Texture;
            var staticSource = artInfo.Source;
            // KR art kept at native detail is larger in the atlas than on
            // screen: this is the factor to its display size.
            float spriteScale = 1f;

            bool krWater = false;

            // Shore water: KR's edge mask, so the shader can pulse the edge.
            if (openWater && KrTerrainProvider.IsActive && artInfo.Texture != null && artInfo.Source.Width == 44)
            {
                // By the static's own graphic, not its animation frame's: a
                // water channel's frames (0x346E-0x3473, Trammel 5264,3942)
                // have no edge mask of their own and drew nothing in turn.
                SpriteInfo shore = Client.Game.UO.KrTerrain.GetShoreWater(baseGraphic);

                if (shore.Texture != null)
                {
                    staticTexture = shore.Texture;
                    staticSource = shore.Source;
                    krWater = true;
                }
            }

            if (!krWater && tileX >= 0 && artInfo.Texture != null
                && openWater
                && ClassicUO.Game.Map.ChunkMesh.IsWaterDiamond(artInfo.Source.Width, artInfo.Source.Height))
            {
                SpriteInfo water = Client.Game.UO.KrTerrain.GetWater(tileX, tileY);

                if (water.Texture != null)
                {
                    staticTexture = water.Texture;
                    staticSource = water.Source;
                    krWater = true;
                }
            }

            // Lava (0x12EE and on) keeps its 2D diamond under the LAVA mode:
            // its KR record is a water shader, with no sprite of its own, and
            // the art lookup gave a 90 px square - the 'fire' a Tokuno oven
            // at 699,1275 holds, which KR draws as shaded floor.
            // kr_light_sources_2d: light sources keep their 2D art and animation.
            if (!krWater && !lava && KrStaticProvider.IsActive && artInfo.Texture != null
                && !ClassicUO.Game.Map.ChunkMesh.LightAs2D(baseGraphic))
            {
                ref var tileData = ref Client.Game.UO.FileManager.TileData.StaticData[graphic];

                // An animated floor piece KR draws as a terrain composite (lava
                // edges 0x1327, 0x1342: Lava_A and Lava_Grey cut by a mask) is
                // laid on its tile like any floor; without the position and the
                // flat flag it fell back to the animated 2D art.
                bool composite = KrStaticProvider.Instance.HasFloorComposite(baseGraphic);

                ref readonly var krInfo = ref composite
                    ? ref Client.Game.UO.KrStatics.GetArt(baseGraphic, 44, 44, false, tileX, tileY, flat: true)
                    : ref KrStaticFrame(baseGraphic, graphic, artInfo, openWater, tileX, tileY);

                if (krInfo.Texture != null)
                {
                    staticTexture = krInfo.Texture;
                    staticSource = krInfo.Source;
                    spriteScale = krInfo.Scale;

                    KrTerrainProvider.NoteStatic(
                        graphic, shadow ? "view: kr (with shadow)" : "view: kr");
                }
                else
                {
                    KrTerrainProvider.NoteStatic(graphic, "view: kr missing, 2d");

                    // An animated piece KR has a record for but draws nothing
                    // of (0x92C, a slice of the oven 0x92B): its 2D frame would
                    // put half a 2D oven over the KR one.
                    // Not water: a channel's frames (0x346E, height 1, at
                    // Trammel 5264,3942) blanked in turn, the water blinking.
                    if (graphic != baseGraphic && KrStaticProvider.Instance.HasTileArt(baseGraphic)
                        && !KrStaticProvider.Instance.UsesLegacyArt(baseGraphic)
                        && !Client.Game.UO.FileManager.TileData.StaticData[baseGraphic].IsWet)
                    {
                        staticTexture = null;
                    }
                }
            }

            if (staticTexture == null)
            {
                KrTerrainProvider.NoteStatic(graphic, "view: NOTHING DRAWN");
            }


            if (staticTexture != null)
            {
                index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);
                int displayWidth = (int)MathF.Round(staticSource.Width * spriteScale);
                int displayHeight = (int)MathF.Round(staticSource.Height * spriteScale);
                index.Width = (short)((displayWidth >> 1) - 22);
                index.Height = (short)(displayHeight - 44);

                x -= index.Width;
                y -= index.Height;

                Vector2 pos = new Vector2(x, y);

                if (shadow)
                {
                    batcher.DrawShadow(staticTexture, pos, staticSource, false, depth + 0.25f, spriteScale);
                }

                var scale = new Vector2(spriteScale, spriteScale);
                if (isWet)
                {
                    batcher.Draw(
                        staticTexture,
                        pos,
                        staticSource,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        depth + 0.5f
                    );

                    var sin = (float)Math.Sin(Time.Ticks / 1000f);
                    var cos = (float)Math.Cos(Time.Ticks / 1000f);
                    scale = new Vector2(1.1f + sin * 0.1f, 1.1f + cos * 0.5f * 0.1f) * spriteScale;
                }

                batcher.Draw(
                    staticTexture,
                    pos,
                    staticSource,
                    hue,
                    0f,
                    Vector2.Zero,
                    scale,
                    SpriteEffects.None,
                    depth + 0.5f
                );
            }
        }
    
        /// <summary>
        /// KR art for an animated static: KR draws its tileart still - the
        /// skull candle 0x1854 is one sprite, 00006228_SkullWithCandle, with
        /// only its light flickering - so the base graphic's art is used, not
        /// the current 2D frame's, whose art swayed left and right. A base
        /// with a KR record but nothing to draw draws nothing: the Tokuno
        /// oven's 0x92C is a slice of 0x92B's texture, and falling back to its
        /// frames' art put an animated fire square beside the oven.
        /// </summary>
        private static ref readonly SpriteInfo KrStaticFrame(
            ushort baseGraphic, ushort frame, in SpriteInfo frameArt, bool openWater, int tileX = -1, int tileY = -1)
        {
            // A floor piece drawn on its own (a house's boards, through
            // MultiView) needs its tile and the flat flag, as the chunk mesh
            // passes them: without them each tile got the whole plank
            // texture squeezed onto it, big stepped planks.
            if (frame == baseGraphic && tileX >= 0)
            {
                ref var data = ref Client.Game.UO.FileManager.TileData.StaticData[baseGraphic];

                return ref Client.Game.UO.KrStatics.GetArt(
                    frame, frameArt.Source.Width, frameArt.Source.Height, openWater, tileX, tileY,
                    ClassicUO.Game.Map.ChunkMesh.LiesFlat(ref data));
            }

            if (frame != baseGraphic)
            {
                ref readonly var baseArt = ref Client.Game.UO.Arts.GetArt(baseGraphic);
                ref readonly var still = ref Client.Game.UO.KrStatics.GetArt(
                    baseGraphic, baseArt.Source.Width, baseArt.Source.Height, openWater);

                if (still.Texture != null || KrStaticProvider.Instance.HasTileArt(baseGraphic))
                {
                    return ref still;
                }
            }

            return ref Client.Game.UO.KrStatics.GetArt(frame, frameArt.Source.Width, frameArt.Source.Height, openWater);
        }
}
}
