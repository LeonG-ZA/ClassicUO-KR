// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.Scenes;
using ClassicUO.IO;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;

namespace ClassicUO.Game.GameObjects
{
    internal sealed partial class Static
    {
        private int _canBeTransparent;

        public override bool TransparentTest(int z)
        {
            bool r = true;

            if (Z <= z - ItemData.Height)
            {
                r = false;
            }
            else if (z < Z && (_canBeTransparent & 0xFF) == 0)
            {
                r = false;
            }

            return r;
        }

        public override bool Draw(UltimaBatcher2D batcher, int posX, int posY, float depth)
        {
            if (!AllowedToDraw || IsDestroyed)
            {
                return false;
            }

            // A KR effect scene on this static (swamp bubbles); where KR draws
            // only the scene, the 2D sprite is not drawn either.
            // A scene's textured mesh (a waterfall's sheet) is drawn by
            // KrParticles over the world: alone where KR's sprite is blank
            // (0x34ED), on top of the art otherwise (0x350E).
            if (ClassicUO.Game.Managers.KrParticles.KeepStatic(Graphic, X, Y, Z))
            {
                return true;
            }

            if (ClassicUO.Game.Map.ChunkMesh.LavaOnLavaLand(this))
            {
                return true;
            }

            ushort graphic = Graphic;
            ushort hue = Hue;
            bool partial = ItemData.IsPartialHue;

            if (ProfileManager.CurrentProfile.HighlightGameObjects && SelectedObject.Object == this)
            {
                hue = Constants.HIGHLIGHT_CURRENT_OBJECT_HUE;
                partial = false;
            }
            else if (
                ProfileManager.CurrentProfile.NoColorObjectsOutOfRange
                && Distance > World.ClientViewRange
            )
            {
                hue = Constants.OUT_RANGE_COLOR;
                partial = false;
            }
            else if (World.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect)
            {
                hue = Constants.DEAD_RANGE_COLOR;
                partial = false;
            }

            bool isTree = StaticFilters.IsTree(graphic, out _);

            // Trees and foliage stay visible inside CoT circle
            bool cot = !isTree && !ItemData.IsFoliage && TransparentTest(World.Player.Z + 5);
            Vector3 hueVec = ShaderHueTranslator.GetHueVector(hue, partial, AlphaHue / 255f, circletrans: cot);

            if (isTree && ProfileManager.CurrentProfile.TreeToStumps)
            {
                graphic = Constants.TREE_REPLACE_GRAPHIC;
            }

            DrawStaticAnimated(
                batcher,
                graphic,
                posX,
                posY,
                hueVec,
                ProfileManager.CurrentProfile.ShadowsEnabled
                    && ProfileManager.CurrentProfile.ShadowsStatics
                    && (isTree || ItemData.IsFoliage || StaticFilters.IsRock(graphic)),
                depth,
                ProfileManager.CurrentProfile.AnimatedWaterEffect && ItemData.IsWet,
                X,
                Y
            );

            if (Scenes.GameScene.GivesLight(Graphic, ItemData.IsLight) && !InChunkMesh)
            {
                Client.Game.GetScene<GameScene>().AddLight(this, this, posX + 22, posY + 22);
            }

            return true;
        }

        public override bool CheckMouseSelection()
        {
            if (
                !(
                    SelectedObject.Object == this
                    || FoliageIndex != -1
                        && Client.Game.GetScene<GameScene>().FoliageIndex == FoliageIndex
                )
            )
            {
                ushort graphic = Graphic;

                bool isTree = StaticFilters.IsTree(graphic, out _);

                if (isTree && ProfileManager.CurrentProfile.TreeToStumps)
                {
                    graphic = Constants.TREE_REPLACE_GRAPHIC;
                }

                ref var index = ref Client.Game.UO.FileManager.Arts.File.GetValidRefEntry(graphic + 0x4000);

                Point position = RealScreenPosition;
                // The 2D art's own size places the 2D mask PixelCheck tests;
                // index.Width/Height hold the KR sprite's when KR art is drawn.
                ref readonly var art2D = ref Client.Game.UO.Arts.GetArt(graphic);
                position.X -= art2D.Texture != null ? (art2D.Source.Width >> 1) - 22 : index.Width;
                position.Y -= art2D.Texture != null ? art2D.Source.Height - 44 : index.Height;

                return Client.Game.UO.Arts.PixelCheck(
                    graphic,
                    SelectedObject.TranslatedMousePositionByViewport.X - position.X,
                    SelectedObject.TranslatedMousePositionByViewport.Y - position.Y
                );
            }

            return false;
        }
    }
}
