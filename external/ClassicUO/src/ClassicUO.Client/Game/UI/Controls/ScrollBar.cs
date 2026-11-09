// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Game.Scenes;
using ClassicUO.Input;
using ClassicUO.Renderer;
using Microsoft.Xna.Framework;
using System;

namespace ClassicUO.Game.UI.Controls
{
    internal class ScrollBar : ScrollBarBase
    {
        private Rectangle _rectSlider,
            _emptySpace;

        const ushort BUTTON_UP_0 = 251;
        const ushort BUTTON_UP_1 = 250;
        const ushort BUTTON_DOWN_0 = 253;
        const ushort BUTTON_DOWN_1 = 252;
        const ushort BACKGROUND_0 = 257;
        const ushort BACKGROUND_1 = 256;
        const ushort BACKGROUND_2 = 255;
        const ushort SLIDER = 254;

        public ScrollBar(int x, int y, int height)
        {
            Height = height;
            Location = new Point(x, y);
            AcceptMouseInput = true;

            ref readonly var gumpInfoUp = ref Client.Game.UO.Gumps.GetGump(BUTTON_UP_0);
            ref readonly var gumpInfoDown = ref Client.Game.UO.Gumps.GetGump(BUTTON_DOWN_0);
            ref readonly var gumpInfoBackground = ref Client.Game.UO.Gumps.GetGump(BACKGROUND_0);
            ref readonly var gumpInfoSlider = ref Client.Game.UO.Gumps.GetGump(SLIDER);

            Width = gumpInfoBackground.Source.Width;

            _rectDownButton = new Rectangle(
                0,
                Height - gumpInfoDown.Source.Height,
                gumpInfoDown.Source.Width,
                gumpInfoDown.Source.Height
            );
            _rectUpButton = new Rectangle(0, 0, gumpInfoUp.Source.Width, gumpInfoUp.Source.Height);
            _rectSlider = new Rectangle(
                (gumpInfoBackground.Source.Width - gumpInfoSlider.Source.Width) >> 1,
                gumpInfoUp.Source.Height + _sliderPosition,
                gumpInfoSlider.Source.Width,
                gumpInfoSlider.Source.Height
            );
            _emptySpace.X = 0;
            _emptySpace.Y = gumpInfoUp.Source.Height;
            _emptySpace.Width = gumpInfoSlider.Source.Width;
            _emptySpace.Height = Height - (gumpInfoDown.Source.Height + gumpInfoUp.Source.Height);
        }

        public override bool AddToRenderLists(RenderLists renderLists, int x, int y, ref float layerDepthRef)
        {
            if (Height <= 0 || !IsVisible)
            {
                return false;
            }
            float layerDepth = layerDepthRef;

            renderLists.AddGumpWithAtlas(batcher =>
            {

                var hueVector = ShaderHueTranslator.GetHueVector(0);

                ref readonly var gumpInfoUp0 = ref Client.Game.UO.Gumps.GetGump(BUTTON_UP_0);
                ref readonly var gumpInfoUp1 = ref Client.Game.UO.Gumps.GetGump(BUTTON_UP_1);
                ref readonly var gumpInfoDown0 = ref Client.Game.UO.Gumps.GetGump(BUTTON_DOWN_0);
                ref readonly var gumpInfoDown1 = ref Client.Game.UO.Gumps.GetGump(BUTTON_DOWN_1);
                ref readonly var gumpInfoBackground0 = ref Client.Game.UO.Gumps.GetGump(BACKGROUND_0);
                ref readonly var gumpInfoBackground1 = ref Client.Game.UO.Gumps.GetGump(BACKGROUND_1);
                ref readonly var gumpInfoBackground2 = ref Client.Game.UO.Gumps.GetGump(BACKGROUND_2);
                ref readonly var gumpInfoSlider = ref Client.Game.UO.Gumps.GetGump(SLIDER);

                // draw scrollbar background
                int middleHeight =
                    Height
                    - gumpInfoUp0.Source.Height
                    - gumpInfoDown0.Source.Height
                    - gumpInfoBackground0.Source.Height
                    - gumpInfoBackground2.Source.Height;

                if (middleHeight > 0)
                {
                    batcher.Draw(
                        gumpInfoBackground0.Texture,
                        new Vector2(x, y + gumpInfoUp0.Source.Height),
                        gumpInfoBackground0.Source,
                        hueVector,
                        layerDepth
                    );

                    batcher.DrawTiled(
                        gumpInfoBackground1.Texture,
                        new Rectangle(
                            x,
                            y + gumpInfoUp1.Source.Height + gumpInfoBackground0.Source.Height,
                            gumpInfoBackground0.Source.Width,
                            middleHeight
                        ),
                        gumpInfoBackground1.Source,
                        hueVector,
                        layerDepth
                    );

                    batcher.Draw(
                        gumpInfoBackground2.Texture,
                        new Vector2(
                            x,
                            y + Height - gumpInfoDown0.Source.Height - gumpInfoBackground2.Source.Height
                        ),
                        gumpInfoBackground2.Source,
                        hueVector,
                        layerDepth
                    );
                }
                else
                {
                    middleHeight = Height - gumpInfoUp0.Source.Height - gumpInfoDown0.Source.Height;

                    batcher.DrawTiled(
                        gumpInfoBackground1.Texture,
                        new Rectangle(
                            x,
                            y + gumpInfoUp0.Source.Height,
                            gumpInfoBackground0.Source.Width,
                            middleHeight
                        ),
                        gumpInfoBackground1.Source,
                        hueVector,
                        layerDepth
                    );
                }

                // draw up button
                if (_btUpClicked)
                {
                    batcher.Draw(gumpInfoUp1.Texture, new Vector2(x, y), gumpInfoUp1.Source, hueVector, layerDepth);
                }
                else
                {
                    batcher.Draw(gumpInfoUp0.Texture, new Vector2(x, y), gumpInfoUp0.Source, hueVector, layerDepth);
                }

                // draw down button
                if (_btDownClicked)
                {
                    batcher.Draw(
                        gumpInfoDown1.Texture,
                        new Vector2(x, y + Height - gumpInfoDown0.Source.Height),
                        gumpInfoDown1.Source,
                        hueVector,
                        layerDepth
                    );
                }
                else
                {
                    batcher.Draw(
                        gumpInfoDown0.Texture,
                        new Vector2(x, y + Height - gumpInfoDown0.Source.Height),
                        gumpInfoDown0.Source,
                        hueVector,
                        layerDepth
                    );
                }

                // draw slider
                if (MaxValue > MinValue && middleHeight > 0)
                {
                    batcher.Draw(
                        gumpInfoSlider.Texture,
                        new Vector2(
                            x + ((gumpInfoBackground0.Source.Width - gumpInfoSlider.Source.Width) >> 1),
                            y + gumpInfoUp0.Source.Height + _sliderPosition
                        ),
                        gumpInfoSlider.Source,
                        hueVector,
                        layerDepth
                    );
                }
                return true;
            });

            return base.AddToRenderLists(renderLists, x, y, ref layerDepthRef);
        }

        protected override int GetScrollableArea()
        {
            ref readonly var gumpInfoUp = ref Client.Game.UO.Gumps.GetGump(BUTTON_UP_0);
            ref readonly var gumpInfoDown = ref Client.Game.UO.Gumps.GetGump(BUTTON_DOWN_0);
            ref readonly var gumpInfoSlider = ref Client.Game.UO.Gumps.GetGump(SLIDER);

            return Height
                - gumpInfoUp.Source.Height
                - gumpInfoDown.Source.Height
                - gumpInfoSlider.Source.Height;
        }

        protected override void OnMouseDown(int x, int y, MouseButtonType button)
        {
            base.OnMouseDown(x, y, button);

            if (_btnSliderClicked && _emptySpace.Contains(x, y))
            {
                CalculateByPosition(x, y);
            }
        }

        protected override void CalculateByPosition(int x, int y)
        {
            if (y != _clickPosition.Y)
            {
                y -= _emptySpace.Y + (_rectSlider.Height >> 1);

                if (y < 0)
                {
                    y = 0;
                }

                int scrollableArea = GetScrollableArea();

                if (y > scrollableArea)
                {
                    y = scrollableArea;
                }

                _sliderPosition = y;
                _clickPosition.X = x;
                _clickPosition.Y = y;

                ref readonly var gumpInfoUp = ref Client.Game.UO.Gumps.GetGump(BUTTON_UP_0);
                ref readonly var gumpInfoDown = ref Client.Game.UO.Gumps.GetGump(BUTTON_DOWN_0);
                ref readonly var gumpInfoSlider = ref Client.Game.UO.Gumps.GetGump(SLIDER);

                if (
                    y == 0
                    && _clickPosition.Y < gumpInfoUp.Source.Height + (gumpInfoSlider.Source.Height >> 1)
                )
                {
                    _clickPosition.Y = gumpInfoUp.Source.Height + (gumpInfoSlider.Source.Height >> 1);
                }
                else if (
                    y == scrollableArea
                    && _clickPosition.Y
                        > Height - gumpInfoDown.Source.Height - (gumpInfoSlider.Source.Height >> 1)
                )
                {
                    _clickPosition.Y =
                        Height - gumpInfoDown.Source.Height - (gumpInfoSlider.Source.Height >> 1);
                }

                _value = (int)
                    Math.Round(y / (float)scrollableArea * (MaxValue - MinValue) + MinValue);
            }
        }

        public override bool Contains(int x, int y)
        {
            return x >= 0 && x <= Width && y >= 0 && y <= Height;
        }
    }
}
