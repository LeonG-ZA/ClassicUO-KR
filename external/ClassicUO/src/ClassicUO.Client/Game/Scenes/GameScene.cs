// SPDX-License-Identifier: BSD-2-Clause

using ClassicUO.Assets;
using ClassicUO.Configuration;
using ClassicUO.Game.Data;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Managers;
using ClassicUO.Game.UI.Gumps;
using ClassicUO.Input;
using ClassicUO.Network;
using ClassicUO.Renderer;
using ClassicUO.Resources;
using ClassicUO.Utility;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using SDL3;
using System;
using System.Collections.Generic;
using System.Net.Sockets;

namespace ClassicUO.Game.Scenes
{
    internal partial class GameScene : Scene
    {
        private static readonly Func<BlendState> _darknessBlend = new(() =>
        {
            return new BlendState
            {
                ColorSourceBlend = Blend.Zero,
                ColorDestinationBlend = Blend.SourceColor,
                ColorBlendFunction = BlendFunction.Add
            };
        });

        // KR composites screen = world x light map x 4 (MODULATE4X). A light
        // target holds 0..1, so it stores half the factor and this blend
        // doubles it back: result = 2 x light x world, up to twice as bright.
        private static readonly Func<BlendState> _krLightsBlend = new(() =>
        {
            return new BlendState
            {
                ColorSourceBlend = Blend.DestinationColor,
                ColorDestinationBlend = Blend.SourceColor,
                ColorBlendFunction = BlendFunction.Add
            };
        });

        private readonly Dictionary<int, Texture2D> _krLightTextures = [];

        /// <summary>
        /// One repeat of KR's cloud texture, in 2D pixels: KR stretches it
        /// over its 1280 px wide light target and 649 / 0.75 px high, at 23.5
        /// px a tile where we draw 22.
        /// </summary>
        private const int KrCloudWidth = 1198;

        private const int KrCloudHeight = 810;

        /// <summary>
        /// How much faster than the ground the clouds slide sideways: KR
        /// scrolls them k = 0.027196 of a repeat per tile on both axes, which
        /// keeps them on the ground vertically but is 1280 / 865 too fast
        /// across.
        /// </summary>
        private const double KrCloudDrift = 0.027196 * KrCloudWidth / 22;

        /// <summary>Mean brightness of the grey clouds (20), 0..1.</summary>
        private const float KrCloudMean = 0.280f;

        /// <summary>
        /// How much of KR's grey clouds (20) cover the sky, 0..1: rain and
        /// storms bring them (our choice; what KR picks its cloud textures by
        /// is not measured), fading in and out with the weather (WeatherFade).
        /// </summary>
        private float KrCloudCover()
        {
            Managers.WeatherFade.Update(_world.Weather);

            return Managers.WeatherFade.Clouds;
        }

        private static readonly Func<BlendState> _altLightsBlend = new(() =>
        {
            return new BlendState
            {
                ColorSourceBlend = Blend.DestinationColor,
                ColorDestinationBlend = Blend.One,
                ColorBlendFunction = BlendFunction.Add
            };
        });

        private const float MAX_LAYER_DEPTH = 0x8000;
        private uint _time_cleanup = Time.Ticks + 5000;
        private bool _alphaChanged;
        private long _alphaTimer;
        private bool _forceStopScene;
        private HealthLinesManager _healthLinesManager;

        private Point _lastSelectedMultiPositionInHouseCustomization;
        private int _lightCount;
        private readonly LightData[] _lights = new LightData[
            LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
        ];
        private Item _multi;
        private Rectangle _rectangleObj = Rectangle.Empty,
            _rectanglePlayer;
        private long _timePing;

        private uint _timeToPlaceMultiInHouseCustomization;
        private readonly UseItemQueue _useItemQueue;
        private bool _useObjectHandles;
        private AnimatedStaticsManager _animatedStaticsManager;

        private readonly World _world;

        // Track the previously highlighted mesh sprite so we can restore its hue
        private GameObject _prevMeshHighlight;

        public GameScene(World world)
        {
            _world = world;
            _useItemQueue = new UseItemQueue(world);
        }

        public bool UpdateDrawPosition { get; set; }
        public bool DisconnectionRequested { get; set; }
        public bool UseLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseCustomLightLevel
                ? _world.Light.Personal < _world.Light.Overall
                : _world.Light.RealPersonal < _world.Light.RealOverall
            // Still easing back to daylight.
            || Managers.LightFade.IsFading;
        public bool UseAltLights =>
            ProfileManager.CurrentProfile != null
            && ProfileManager.CurrentProfile.UseAlternativeLights;

        public void DoubleClickDelayed(uint serial)
        {
            _useItemQueue.Add(serial);
        }

        public override void Load()
        {
            base.Load();

            Client.Game.Window.AllowUserResizing = true;

            Camera.Zoom = ProfileManager.CurrentProfile.DefaultScale;
            Camera.Bounds.X = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.X);
            Camera.Bounds.Y = Math.Max(0, ProfileManager.CurrentProfile.GameWindowPosition.Y);
            Camera.Bounds.Width = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.X);
            Camera.Bounds.Height = Math.Max(0, ProfileManager.CurrentProfile.GameWindowSize.Y);

            Client.Game.UO.GameCursor.ItemHold.Clear();

            _world.Macros.Clear();
            _world.Macros.Load();
            _animatedStaticsManager = new AnimatedStaticsManager();
            _animatedStaticsManager.Initialize();
            _world.InfoBars.Load();
            _healthLinesManager = new HealthLinesManager(_world);

            _world.CommandManager.Initialize();

            WorldViewportGump viewport = new WorldViewportGump(_world, this);
            UIManager.Add(viewport, false);

            if (!ProfileManager.CurrentProfile.TopbarGumpIsDisabled)
            {
                TopBarGump.Create(_world);
            }

            NetClient.Socket.Disconnected += SocketOnDisconnected;
            _world.MessageManager.MessageReceived += ChatOnMessageReceived;
            UIManager.ContainerScale = ProfileManager.CurrentProfile.ContainersScale / 100f;
            Data.MovementSpeed.FastRotation = ProfileManager.CurrentProfile.FastRotation;

            SDL.SDL_SetWindowMinimumSize(Client.Game.Window.Handle, Client.Game.ScaleWithDpi(640), Client.Game.ScaleWithDpi(480));

            if (ProfileManager.CurrentProfile.WindowBorderless)
            {
                Client.Game.SetWindowBorderless(true);
            }
            else if (Settings.GlobalSettings.IsWindowMaximized)
            {
                Client.Game.MaximizeWindow();
            }
            else if (Settings.GlobalSettings.WindowSize.HasValue)
            {
                int w = Settings.GlobalSettings.WindowSize.Value.X;
                int h = Settings.GlobalSettings.WindowSize.Value.Y;

                w = Math.Max(Client.Game.ScaleWithDpi(640), w);
                h = Math.Max(Client.Game.ScaleWithDpi(480), h);

                Client.Game.SetWindowSize(w, h);
            }

            Plugin.OnConnected();
        }

        private void ChatOnMessageReceived(object sender, MessageEventArgs e)
        {
            if (e.Type == MessageType.Command)
            {
                return;
            }

            string name;
            string text;

            ushort hue = e.Hue;

            switch (e.Type)
            {
                case MessageType.Regular:
                case MessageType.Limit3Spell:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = ResGeneral.System;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.System:
                case MessageType.GmChat:
                    name =
                        string.IsNullOrEmpty(e.Name)
                        || string.Equals(
                            e.Name,
                            "system",
                            StringComparison.InvariantCultureIgnoreCase
                        )
                            ? ResGeneral.System
                            : e.Name;

                    text = e.Text;

                    break;

                case MessageType.Emote:
                    name = e.Name;
                    text = $"{e.Text}";

                    if (e.Hue == 0)
                    {
                        hue = ProfileManager.CurrentProfile.EmoteHue;
                    }

                    break;

                case MessageType.Label:

                    if (e.Parent == null || !SerialHelper.IsValid(e.Parent.Serial))
                    {
                        name = string.Empty;
                    }
                    else if (string.IsNullOrEmpty(e.Name))
                    {
                        name = ResGeneral.YouSee;
                    }
                    else
                    {
                        name = e.Name;
                    }

                    text = e.Text;

                    break;

                case MessageType.Spell:
                    name = e.Name;
                    text = e.Text;

                    break;

                case MessageType.Party:
                    text = e.Text;
                    name = string.Format(ResGeneral.Party0, e.Name);
                    hue = ProfileManager.CurrentProfile.PartyMessageHue;

                    break;

                case MessageType.Alliance:
                    text = e.Text;
                    name = string.Format(ResGeneral.Alliance0, e.Name);
                    hue = ProfileManager.CurrentProfile.AllyMessageHue;

                    break;

                case MessageType.Guild:
                    text = e.Text;
                    name = string.Format(ResGeneral.Guild0, e.Name);
                    hue = ProfileManager.CurrentProfile.GuildMessageHue;

                    break;

                default:
                    text = e.Text;
                    name = e.Name;
                    hue = e.Hue;

                    Log.Warn($"Unhandled text type {e.Type}  -  text: '{e.Text}'");

                    break;
            }

            if (!string.IsNullOrEmpty(text))
            {
                _world.Journal.Add(text, hue, name, e.Parent?.Serial, e.TextType, e.IsUnicode, e.Type);
            }
        }

        public override void Unload()
        {
            if (IsDestroyed)
            {
                return;
            }

            ProfileManager.CurrentProfile.GameWindowPosition = new Point(
                Camera.Bounds.X,
                Camera.Bounds.Y
            );
            ProfileManager.CurrentProfile.GameWindowSize = new Point(
                Camera.Bounds.Width,
                Camera.Bounds.Height
            );
            ProfileManager.CurrentProfile.DefaultScale = Camera.Zoom;

            Client.Game.Audio?.StopMusic();
            Client.Game.Audio?.StopSounds();

            Client.Game.SetWindowTitle(string.Empty);
            Client.Game.UO.GameCursor.ItemHold.Clear();

            try
            {
                Plugin.OnDisconnected();
            }
            catch { }

            _world.TargetManager.Reset();

            // special case for wmap. this allow us to save settings
            UIManager.GetGump<WorldMapGump>()?.SaveSettings();

            ProfileManager.CurrentProfile?.Save(_world, ProfileManager.ProfilePath);

            _world.Macros.Save();
            _world.Macros.Clear();
            _world.InfoBars.Save();
            ProfileManager.UnLoadProfile();

            StaticFilters.CleanCaveTextures();
            StaticFilters.CleanTreeTextures();

            NetClient.Socket.Disconnected -= SocketOnDisconnected;
            NetClient.Socket.Disconnect();

            _world.CommandManager.UnRegisterAll();
            _world.Weather.Reset();
            UIManager.Clear();
            _world.Clear();
            _world.ChatManager.Clear();
            _world.DelayedObjectClickManager.Clear();

            _useItemQueue?.Clear();
            _world.MessageManager.MessageReceived -= ChatOnMessageReceived;

            Settings.GlobalSettings.WindowSize = new Point(
                Client.Game.ClientBounds.Width,
                Client.Game.ClientBounds.Height
            );

            Settings.GlobalSettings.IsWindowMaximized = Client.Game.IsWindowMaximized();
            Client.Game.SetWindowBorderless(false);

            base.Unload();
        }

        private void SocketOnDisconnected(object sender, SocketError e)
        {
            if (Settings.GlobalSettings.Reconnect)
            {
                _forceStopScene = true;
            }
            else
            {
                UIManager.Add(
                    new MessageBoxGump(
                        _world,
                        200,
                        200,
                        string.Format(
                            ResGeneral.ConnectionLost0,
                            StringHelper.AddSpaceBeforeCapital(e.ToString())
                        ),
                        s =>
                        {
                            if (s)
                            {
                                Client.Game.SetScene(new LoginScene(_world));
                            }
                        }
                    )
                );
            }
        }

        public void RequestQuitGame()
        {
            UIManager.Add(
                new QuestionGump(
                    _world,
                    ResGeneral.QuitPrompt,
                    s =>
                    {
                        if (s)
                        {
                            if (
                                (
                                    _world.ClientFeatures.Flags
                                    & CharacterListFlags.CLF_OWERWRITE_CONFIGURATION_BUTTON
                                ) != 0
                            )
                            {
                                DisconnectionRequested = true;
                                NetClient.Socket.Send_LogoutNotification();
                            }
                            else
                            {
                                NetClient.Socket.Disconnect();
                                Client.Game.SetScene(new LoginScene(_world));
                            }
                        }
                    }
                )
            );
        }

        public void AddLight(GameObject obj, GameObject lightObject, int x, int y)
        {
            if (
                _lightCount >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT
                || !UseLights && !UseAltLights && !KrLightProvider.IsActive
                || obj == null
            )
            {
                return;
            }

            bool canBeAdded = true;

            int testX = obj.X + 1;
            int testY = obj.Y + 1;

            GameObject tile = _world.Map.GetTile(testX, testY);

            if (tile != null)
            {
                sbyte z5 = (sbyte)(obj.Z + 5);

                for (GameObject o = tile; o != null; o = o.TNext)
                {
                    if (
                        (!(o is Static s) || s.ItemData.IsTransparent)
                            && (!(o is Multi m) || m.ItemData.IsTransparent)
                        || !o.AllowedToDraw
                    )
                    {
                        continue;
                    }

                    if (o.Z < _maxZ && o.Z >= z5)
                    {
                        canBeAdded = false;

                        break;
                    }
                }
            }

            if (canBeAdded && KrLightProvider.Instance is { } krLights && IsKrUnlitWindow(krLights, lightObject.Graphic))
            {
                canBeAdded = false;
            }

            if (canBeAdded)
            {
                ref LightData light = ref _lights[_lightCount];

                ushort graphic = lightObject.Graphic;

                if (
                    graphic >= 0x3E02 && graphic <= 0x3E0B
                    || graphic >= 0x3914 && graphic <= 0x3929
                    || graphic == 0x0B1D
                )
                {
                    light.ID = 2;
                }
                else
                {
                    if (obj == lightObject && obj is Item item)
                    {
                        light.ID = item.LightID;
                    }
                    else if (lightObject is Item it)
                    {
                        light.ID = (byte)it.ItemData.LightIndex;

                        if (obj is Mobile mob)
                        {
                            switch (mob.Direction)
                            {
                                case Direction.Right:
                                    y += 33;
                                    x += 22;

                                    break;

                                case Direction.Left:
                                    y += 33;
                                    x -= 22;

                                    break;

                                case Direction.East:
                                    x += 22;
                                    y += 55;

                                    break;

                                case Direction.Down:
                                    y += 55;

                                    break;

                                case Direction.South:
                                    x -= 22;
                                    y += 55;

                                    break;
                            }
                        }
                    }
                    else if (obj is Mobile _)
                    {
                        light.ID = 1;
                    }
                    else
                    {
                        ref StaticTiles data = ref Client.Game.UO.FileManager.TileData.StaticData[obj.Graphic];
                        light.ID = data.Layer;
                    }
                }

                light.Color = 0;
                light.IsHue = false;

                if (ProfileManager.CurrentProfile.UseColoredLights)
                {
                    if (light.ID > 200)
                    {
                        light.Color = (ushort)(light.ID - 200);
                        light.ID = 1;
                    }

                    if (LightColors.GetHue(graphic, out ushort color, out bool ishue))
                    {
                        light.Color = color;
                        light.IsHue = ishue;
                    }
                }

                if (light.ID >= LightsLoader.MAX_LIGHTS_DATA_INDEX_COUNT)
                {
                    return;
                }

                if (light.Color != 0)
                {
                    light.Color++;
                }

                // KR's own light for the object, or its generic one for a 2D
                // light source KR names none for (moongates, the forge).
                light.KrTexture = KrLightProvider.Instance?.LightFor(graphic) is > 0 and var kr
                    ? kr
                    : KrLightProvider.GenericLight;

                light.DrawX = x;
                light.DrawY = y;

                // KR draws a lava tile's light (0x12EE-0x131D, light_generic)
                // well off the tile: seen at Trammel 4598,3617, each of the
                // pit's 40 lights sat (-6.5, +0.5) tiles from its lava, about
                // half the texture up and left, so their glow fell on the
                // ground beside the pit rather than on the lava.
                if (KrLightProvider.IsActive && ClassicUO.Game.Map.ChunkMesh.IsLava(graphic))
                {
                    light.DrawX -= 154;
                    light.DrawY -= 132;
                }

                _lightCount++;
            }
        }

        /// <summary>
        /// Builds the meshes of the chunks just outside the view, a few
        /// milliseconds a frame, so a chunk is usually ready before it scrolls
        /// in. Built on the frame they came into view, the eight chunks of a
        /// step into new ground cost ~100 ms together (KR land: ~12 ms each).
        /// </summary>
        private void PrefetchChunkMeshes(Map.Map map, int minChunkX, int minChunkY, int maxChunkX, int maxChunkY)
        {
            const double budgetMs = 4.0;
            const int ring = 2;
            double start = Managers.FrameProbe.Now;

            for (int cx = minChunkX - ring; cx <= maxChunkX + ring; cx++)
            {
                for (int cy = minChunkY - ring; cy <= maxChunkY + ring; cy++)
                {
                    if (cx >= minChunkX && cx <= maxChunkX && cy >= minChunkY && cy <= maxChunkY)
                    {
                        continue;
                    }

                    if (Managers.FrameProbe.Now - start > budgetMs)
                    {
                        return;
                    }

                    if (cx < 0 || cy < 0)
                    {
                        continue;
                    }

                    var chunk = map.GetChunk2(cx, cy, true);

                    if (chunk != null && !chunk.IsDestroyed && chunk.Mesh.IsDirty)
                    {
                        chunk.Mesh.Build(chunk, _world, Client.Game.GraphicsDevice);
                    }
                }
            }
        }

        private void FillGameObjectList()
        {
            _renderLists.Clear();
            _visibleChunks.Clear();

            _foliageCount = 0;

            if (!_world.InGame)
            {
                return;
            }

            _alphaChanged = _alphaTimer < Time.Ticks;

            if (_alphaChanged)
            {
                _alphaTimer = Time.Ticks + Constants.ALPHA_TIME;
            }

            if (ProfileManager.CurrentProfile.UseCircleOfTransparency)
            {
                float r = ProfileManager.CurrentProfile.CircleOfTransparencyRadius;
                _cotRadiusSq = r * r;
                _cotPlayerScreenPos = _world.Player.GetScreenPosition();
                _cotGradientMode = ProfileManager.CurrentProfile.CircleOfTransparencyType == 1;
            }
            else
            {
                _cotRadiusSq = 0;
                _cotGradientMode = false;
            }

            FoliageIndex++;

            if (FoliageIndex >= 100)
            {
                FoliageIndex = 1;
            }

            GetViewPort();

            var ctrlShiftHeld = Keyboard.Ctrl && Keyboard.Shift;
            var useObjectHandles = _world.NameOverHeadManager.IsToggled || ctrlShiftHeld;
            if (useObjectHandles != _useObjectHandles)
            {
                _useObjectHandles = useObjectHandles;
                if (_useObjectHandles)
                {
                    _world.NameOverHeadManager.Open();
                    if (_world.NameOverHeadManager.IsToggled && !ctrlShiftHeld)
                    {
                        _world.NameOverHeadManager.SetMenuVisible(false);
                    }
                }
                else
                {
                    _world.NameOverHeadManager.Close();
                }
            }
            else if (_useObjectHandles && _world.NameOverHeadManager.IsToggled)
            {
                _world.NameOverHeadManager.SetMenuVisible(ctrlShiftHeld);
            }

            _rectanglePlayer.X = (int)(
                _world.Player.RealScreenPosition.X
                - _world.Player.FrameInfo.X
                + 22
                + _world.Player.Offset.X
            );
            _rectanglePlayer.Y = (int)(
                _world.Player.RealScreenPosition.Y
                - _world.Player.FrameInfo.Y
                + 22
                + (_world.Player.Offset.Y - _world.Player.Offset.Z)
            );
            _rectanglePlayer.Width = _world.Player.FrameInfo.Width;
            _rectanglePlayer.Height = _world.Player.FrameInfo.Height;

            int minX = _minTile.X;
            int minY = _minTile.Y;
            int maxX = _maxTile.X;
            int maxY = _maxTile.Y;
            Map.Map map = _world.Map;
            bool use_handles = _useObjectHandles;
            (var minChunkX, var minChunkY) = (minX >> 3, minY >> 3);
            (var maxChunkX, var maxChunkY) = (maxX >> 3, maxY >> 3);

            for (var chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
            {
                for (var chunkY = minChunkY; chunkY <= maxChunkY; chunkY++)
                {
                    var chunk = map.GetChunk2(chunkX, chunkY, true);
                    if (chunk == null || chunk.IsDestroyed)
                        continue;

                    // Build chunk mesh if dirty
                    if (chunk.Mesh.IsDirty)
                    {
                        double probeMesh = Managers.FrameProbe.Now;
                        chunk.Mesh.Build(chunk, _world, Client.Game.GraphicsDevice);
                        Managers.FrameProbe.Add(Managers.FrameProbe.Section.MeshBuild, probeMesh);
                        Managers.FrameProbe.MeshBuilt();
                    }

                    // Reset visibility and alpha for this frame
                    chunk.Mesh.Land.ResetVisibility();
                    chunk.Mesh.Land.ResetAlpha();
                    chunk.Mesh.Statics.ResetVisibility();
                    chunk.Mesh.Statics.ResetAlpha();

                    _visibleChunks.Add(chunk);

                    for (var x = 0; x < 8; x++)
                    {
                        for (var y = 0; y < 8; y++)
                        {
                            var firstObj = chunk.GetHeadObject(x, y);
                            if (firstObj == null || firstObj.IsDestroyed)
                                continue;

                            AddTileToRenderList(
                                firstObj,
                                use_handles,
                                150,
                                chunk
                            );
                        }
                    }
                }
            }

            PrefetchChunkMeshes(map, minChunkX, minChunkY, maxChunkX, maxChunkY);

            if (_alphaChanged)
            {
                for (int i = 0; i < _foliageCount; i++)
                {
                    GameObject f = _foliages[i];

                    if (f.FoliageIndex == FoliageIndex)
                    {
                        CalculateAlpha(ref f.AlphaHue, Constants.FOLIAGE_ALPHA);
                    }
                    else if (f.Z < _maxZ)
                    {
                        CalculateAlpha(ref f.AlphaHue, 0xFF);
                    }
                }
            }

            UpdateTextServerEntities(_world.Mobiles.Values, true);
            UpdateTextServerEntities(_world.Items.Values, false);

            UpdateDrawPosition = false;
        }

        private void UpdateTextServerEntities<T>(IEnumerable<T> entities, bool force)
            where T : Entity
        {
            foreach (T e in entities)
            {
                if (
                    e.TextContainer != null
                    && !e.TextContainer.IsEmpty
                    && (force || e.Graphic == 0x2006)
                )
                {
                    e.UpdateRealScreenPosition(_offset.X, _offset.Y);
                }
            }
        }

        public override void Update()
        {
            Profile currentProfile = ProfileManager.CurrentProfile;

            SelectedObject.TranslatedMousePositionByViewport = Camera.MouseToWorldPosition();

            base.Update();

            if (_time_cleanup < Time.Ticks)
            {
                _world.Map?.ClearUnusedBlocks();
                _time_cleanup = Time.Ticks + 500;
            }

            PacketHandlers.SendMegaClilocRequests(_world);

            if (_forceStopScene)
            {
                LoginScene loginScene = new LoginScene(_world);
                Client.Game.SetScene(loginScene);
                loginScene.Reconnect = true;

                return;
            }

            if (!_world.InGame)
            {
                return;
            }

            if (Time.Ticks > _timePing)
            {
                NetClient.Socket.Statistics.SendPing();
                _timePing = (long)Time.Ticks + 1000;
            }

            _world.Update();
            _animatedStaticsManager.Process();
            _world.BoatMovingManager.Update();
            _world.Player.Pathfinder.ProcessAutoWalk();
            _world.DelayedObjectClickManager.Update();

            if (!MoveCharacterByMouseInput() && !currentProfile.DisableArrowBtn)
            {
                Direction dir = DirectionHelper.DirectionFromKeyboardArrows(
                    _flags[0],
                    _flags[2],
                    _flags[1],
                    _flags[3]
                );

                if (_world.InGame && !_world.Player.Pathfinder.AutoWalking && dir != Direction.NONE)
                {
                    _world.Player.Walk(dir, currentProfile.AlwaysRun);
                }
            }

            if (
                _followingMode && SerialHelper.IsMobile(_followingTarget) && !_world.Player.Pathfinder.AutoWalking
            )
            {
                Mobile follow = _world.Mobiles.Get(_followingTarget);

                if (follow != null)
                {
                    int distance = follow.Distance;

                    if (distance > _world.ClientViewRange)
                    {
                        StopFollowing();
                    }
                    else if (distance > 3)
                    {
                        _world.Player.Pathfinder.WalkTo(follow.X, follow.Y, follow.Z, 1);
                    }
                }
                else
                {
                    StopFollowing();
                }
            }

            _world.Macros.Update();

            if (
                (currentProfile.CorpseOpenOptions == 1 || currentProfile.CorpseOpenOptions == 3)
                    && _world.TargetManager.IsTargeting
                || (currentProfile.CorpseOpenOptions == 2 || currentProfile.CorpseOpenOptions == 3)
                    && _world.Player.IsHidden
            )
            {
                _useItemQueue.ClearCorpses();
            }

            _useItemQueue.Update();

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            if (
                _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
                && _world.CustomHouseManager == null
                && _world.TargetManager.MultiTargetInfo != null
            )
            {
                if (_multi == null)
                {
                    _multi = Item.Create(_world, 0);
                    _multi.Graphic = _world.TargetManager.MultiTargetInfo.Model;
                    _multi.Hue = _world.TargetManager.MultiTargetInfo.Hue;
                    _multi.IsMulti = true;
                }

                if (SelectedObject.Object is GameObject gobj)
                {
                    ushort x,
                        y;
                    sbyte z;

                    int cellX = gobj.X % 8;
                    int cellY = gobj.Y % 8;

                    GameObject o = _world.Map.GetChunk(gobj.X, gobj.Y)?.Tiles[cellX, cellY];

                    if (o != null)
                    {
                        x = o.X;
                        y = o.Y;
                        z = o.Z;
                    }
                    else
                    {
                        x = gobj.X;
                        y = gobj.Y;
                        z = gobj.Z;
                    }

                    _world.Map.GetMapZ(x, y, out sbyte groundZ, out sbyte _);

                    if (gobj is Static st && st.ItemData.IsWet)
                    {
                        groundZ = gobj.Z;
                    }

                    x = (ushort)(x - _world.TargetManager.MultiTargetInfo.XOff);
                    y = (ushort)(y - _world.TargetManager.MultiTargetInfo.YOff);
                    z = (sbyte)(groundZ - _world.TargetManager.MultiTargetInfo.ZOff);

                    _multi.SetInWorldTile(x, y, z);
                    _multi.CheckGraphicChange();

                    _world.HouseManager.TryGetHouse(_multi.Serial, out House house);

                    foreach (Multi s in house.Components)
                    {
                        s.IsHousePreview = true;
                        s.SetInWorldTile(
                            (ushort)(_multi.X + s.MultiOffsetX),
                            (ushort)(_multi.Y + s.MultiOffsetY),
                            (sbyte)(_multi.Z + s.MultiOffsetZ)
                        );
                    }
                }
            }
            else if (_multi != null)
            {
                _world.HouseManager.RemoveMultiTargetHouse();
                _multi.Destroy();
                _multi = null;
            }

            if (_isMouseLeftDown && !Client.Game.UO.GameCursor.ItemHold.Enabled)
            {
                if (
                    _world.CustomHouseManager != null
                    && _world.CustomHouseManager.SelectedGraphic != 0
                    && !_world.CustomHouseManager.SeekTile
                    && !_world.CustomHouseManager.Erasing
                    && Time.Ticks > _timeToPlaceMultiInHouseCustomization
                )
                {
                    if (
                        SelectedObject.Object is GameObject obj
                        && (
                            obj.X != _lastSelectedMultiPositionInHouseCustomization.X
                            || obj.Y != _lastSelectedMultiPositionInHouseCustomization.Y
                        )
                    )
                    {
                        _world.CustomHouseManager.OnTargetWorld(obj);
                        _timeToPlaceMultiInHouseCustomization = Time.Ticks + 50;
                        _lastSelectedMultiPositionInHouseCustomization.X = obj.X;
                        _lastSelectedMultiPositionInHouseCustomization.Y = obj.Y;
                    }
                }
                else if (Time.Ticks - _holdMouse2secOverItemTime >= 1000)
                {
                    if (SelectedObject.Object is Item it && GameActions.PickUp(_world, it.Serial, 0, 0))
                    {
                        _isMouseLeftDown = false;
                        _holdMouse2secOverItemTime = 0;
                    }
                }
            }
        }

        public override bool Draw(UltimaBatcher2D batcher, RenderTargets renderTargets)
        {
            if (!_world.InGame)
            {
                return false;
            }

            if (CheckDeathScreen(batcher))
            {
                return true;
            }

            Viewport r_viewport = batcher.GraphicsDevice.Viewport;
            Viewport camera_viewport = Camera.GetViewport();
            Matrix matrix = Camera.ViewTransformMatrix;

            bool can_draw_lights = false;

            double probeLights = Managers.FrameProbe.Now;
            can_draw_lights = PrepareLightsRendering(batcher, ref matrix, renderTargets);
            Managers.FrameProbe.Add(Managers.FrameProbe.Section.Lights, probeLights);
            batcher.GraphicsDevice.Viewport = camera_viewport;

            DrawWorld(batcher, ref matrix, renderTargets);

            batcher.GraphicsDevice.Viewport = r_viewport;

            return base.Draw(batcher, renderTargets);
        }

        private void DrawWorld(UltimaBatcher2D batcher, ref Matrix matrix, RenderTargets renderTargets)
        {
            batcher.GraphicsDevice.SetRenderTarget(renderTargets.WorldRenderTarget);
            SelectedObject.Object = null;
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            double probeFill = Managers.FrameProbe.Now;
            FillGameObjectList();
            Managers.FrameProbe.Add(Managers.FrameProbe.Section.Fill, probeFill);

            // Restore previous highlight's original hue before applying new one
            if (_prevMeshHighlight != null
                && !_prevMeshHighlight.IsDestroyed
                && _prevMeshHighlight.InChunkMesh
                && _prevMeshHighlight.MeshSpriteIndex >= 0)
            {
                var prevChunk = _world.Map.GetChunk(_prevMeshHighlight.X, _prevMeshHighlight.Y);
                if (prevChunk?.Mesh != null)
                {
                    var prevLayer = _prevMeshHighlight is Land ? prevChunk.Mesh.Land : prevChunk.Mesh.Statics;
                    ApplyMeshHue(_prevMeshHighlight, prevLayer);
                }
            }
            _prevMeshHighlight = null;

            // Apply highlight hue to mesh vertex for selected meshed object
            // (instead of redrawing it on top, which breaks z-order for overlapping objects)
            if (ProfileManager.CurrentProfile.HighlightGameObjects
                && SelectedObject.Object is GameObject selObj
                && selObj.InChunkMesh && selObj.MeshSpriteIndex >= 0
                // GPU terrain's hue slot is its table entry (ApplyMeshHue).
                && selObj is not Land { KrTerrainGpu: true })
            {
                var chunk = _world.Map.GetChunk(selObj.X, selObj.Y);
                if (chunk?.Mesh != null)
                {
                    var layer = selObj is Land ? chunk.Mesh.Land : chunk.Mesh.Statics;
                    float shaderType = selObj is Land land && land.IsStretched
                        ? ShaderHueTranslator.SHADER_LAND_HUED
                        : ShaderHueTranslator.SHADER_HUED;
                    layer.SetHue(
                        selObj.MeshSpriteIndex,
                        Constants.HIGHLIGHT_CURRENT_OBJECT_HUE - 1,
                        shaderType
                    );
                    _prevMeshHighlight = selObj;
                }
            }

            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD_PREPARE);
            Profiler.EnterContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
            batcher.SetSampler(SamplerState.PointClamp);

            batcher.Begin(null, matrix);
            batcher.SetBrightlight(ProfileManager.CurrentProfile.TerrainShadowsLevel * 0.1f);

            if (ProfileManager.CurrentProfile.UseCircleOfTransparency
                && ProfileManager.CurrentProfile.CircleOfTransparencyType != 1) // gradient mode uses CPU alpha, not shader
            {
                batcher.SetCircleOfTransparencyRadius(
                    (float)ProfileManager.CurrentProfile.CircleOfTransparencyRadius / Camera.Zoom
                );
            }
            else
            {
                batcher.SetCircleOfTransparencyRadius(0f);
            }

            // Wrapped hourly so the waves keep float precision.
            batcher.SetWater((Time.Ticks % 3600000u) / 1000f, new Vector2(_offset.X, _offset.Y));

            var krTerrain = Client.Game.UO.KrTerrain;

            if (ShaderHueTranslator.WaterShader && krTerrain != null && krTerrain.EnsureWater())
            {
                batcher.SetWaterTextures(krTerrain.WaterColor, krTerrain.WaterNormal, krTerrain.WaterReflect);
                batcher.SetSwampTexture(krTerrain.WaterSwamp, krTerrain.SwampBump, krTerrain.SwampReflect);
                batcher.SetLavaTextures(krTerrain.LavaNormal, krTerrain.Lava);
                batcher.SetStarsTextures(krTerrain.StarsBump, krTerrain.StarsReflect, krTerrain.StarsField);
                batcher.SetKrTerrain(krTerrain.Atlas.Texture, krTerrain.Atlas.Table);
            }

            // GPU terrain keeps its mode when hued (ApplyMeshHue), so the
            // dead grey reaches it here.
            batcher.SetKrTerrainHue(
                _world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect ? (ushort)Constants.DEAD_RANGE_COLOR : (ushort)0
            );

            // https://shawnhargreaves.com/blog/depth-sorting-alpha-blended-objects.html
            batcher.SetStencil(DepthStencilState.Default);

            // KR's grunge on statics and items while the world's objects
            // draw, with KR statics on.
            batcher.SetGrunge(KrStaticProvider.IsActive ? krTerrain?.Grunge : null);

            double probeLists = Managers.FrameProbe.Now;
            RenderedObjectsCount = _renderLists.DrawRenderLists(
                batcher,
                _maxGroundZ,
                _visibleChunks,
                _offset.X,
                _offset.Y
            );


            if (
                _multi != null
                && _world.TargetManager.IsTargeting
                && _world.TargetManager.TargetingState == CursorTarget.MultiPlacement
            )
            {
                _multi.Draw(
                    batcher,
                    _multi.RealScreenPosition.X,
                    _multi.RealScreenPosition.Y,
                    _multi.CalculateDepthZ()
                );
            }

            batcher.SetGrunge(null);

            Managers.FrameProbe.Add(Managers.FrameProbe.Section.DrawLists, probeLists);
            double probeParticles = Managers.FrameProbe.Now;
            Managers.KrParticles.Draw(_world, batcher, _offset, MAX_LAYER_DEPTH - 2);
            Managers.FrameProbe.Add(Managers.FrameProbe.Section.Particles, probeParticles);

            // draw weather: KR's when kr_weather is on, else the 2D weather
            if (!Managers.KrWeather.Draw(_world, batcher, _offset, KrRainReaches, MAX_LAYER_DEPTH - 1))
            {
                _world.Weather.Draw(batcher, 0, 0, MAX_LAYER_DEPTH - 1);
            }

            DrawSelection(batcher, MAX_LAYER_DEPTH);

            batcher.SetSampler(null);
            batcher.SetStencil(null);
            batcher.SetCircleOfTransparencyRadius(0f);
            batcher.End();

            int flushes = batcher.FlushesDone;
            int switches = batcher.TextureSwitches;
            batcher.GraphicsDevice.SetRenderTarget(null);
            Profiler.ExitContext(Profiler.ProfilerContext.RENDER_FRAME_WORLD);
        }

        private bool PrepareLightsRendering(UltimaBatcher2D batcher, ref Matrix matrix, RenderTargets renderTargets)
        {
            InitializeRenderTargets(renderTargets);

            if (
                !UseLights && !UseAltLights && !KrLightProvider.IsActive
                || _world.Player.IsDead && ProfileManager.CurrentProfile.EnableBlackWhiteEffect
            )
            {
                batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
                batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Transparent, 0f, 0);
                batcher.GraphicsDevice.SetRenderTarget(null);

                return false;
            }

            if (KrLightProvider.IsActive)
            {
                PrepareKrLights(batcher, ref matrix, renderTargets);

                return true;
            }

            batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
            batcher.GraphicsDevice.Clear(ClearOptions.Target, Color.Black, 0f, 0);

            if (!UseAltLights)
            {
                // As IsometricLight.Recalculate, with the eased level.
                float lightColor = Math.Max(_world.Light.Personal, 32f - Managers.LightFade.Update(_world)) * 0.03125f;

                if (ProfileManager.CurrentProfile.UseDarkNights)
                {
                    lightColor -= 0.04f;
                }

                batcher.GraphicsDevice.Clear(
                    ClearOptions.Target,
                    new Vector4(lightColor, lightColor, lightColor, 1),
                    0f,
                    0
                );
            }

            batcher.Begin(null, matrix);
            batcher.SetBlendState(BlendState.Additive);

            Vector3 hue = Vector3.Zero;

            hue.Z = 1f;

            for (int i = 0; i < _lightCount; i++)
            {
                ref LightData l = ref _lights[i];
                ref readonly var lightInfo = ref Client.Game.UO.Lights.GetLight(l.ID);

                if (lightInfo.Texture == null)
                {
                    continue;
                }

                hue.X = l.Color;
                hue.Y =
                    hue.X > 1.0f
                        ? l.IsHue
                            ? ShaderHueTranslator.SHADER_HUED
                            : ShaderHueTranslator.SHADER_LIGHTS
                        : ShaderHueTranslator.SHADER_NONE;

                batcher.Draw(
                    lightInfo.Texture,
                    new Vector2(
                        l.DrawX - lightInfo.Source.Width * 0.5f,
                        l.DrawY - lightInfo.Source.Height * 0.5f
                    ),
                    lightInfo.Source,
                    hue,
                    0f
                );
            }

            _lightCount = 0;

            batcher.SetBlendState(null);
            batcher.End();

            batcher.GraphicsDevice.SetRenderTarget(null);

            return true;
        }

        /// <summary>
        /// KR's light map:
        /// screen = world x 4 x (0.25a + L(1 - a)), a = (31 - level) / 31 the
        /// daylight left and L the light target - TerrainTexture 21's blue
        /// noise, anchored to the world, with each object's light texture over
        /// it. Stored halved for the 2x blend: a/2 + 2(1 - a)L, the lights
        /// added rather than KR's alpha blend over the faint noise.
        /// </summary>
        private void PrepareKrLights(UltimaBatcher2D batcher, ref Matrix matrix, RenderTargets renderTargets)
        {
            float cover = KrCloudCover();
            bool weather = cover > 0f;
            float level = Math.Clamp(Managers.LightFade.Update(_world), 0f, 31f);
            float daylight = Math.Clamp(Math.Max(31f - level, _world.Light.Personal) / 31f, 0f, 1f);
            float dark = 1f - daylight;

            // Under clouds the daylight part of the light map is the cloud
            // layer (KR's clouds went to level 30 for it), scaled to the
            // same brightness so rain doesn't change the light; the night noise
            // stays, already cloudy, so night rain looks as night does.
            float baseLight = daylight * 0.5f * (1f - cover);

            batcher.GraphicsDevice.SetRenderTarget(renderTargets.LightRenderTarget);
            batcher.GraphicsDevice.Clear(
                ClearOptions.Target,
                new Vector4(baseLight, baseLight, baseLight, 1f),
                0f,
                0
            );

            if (dark > 0.01f || weather)
            {
                // The shader returns colour x alpha with hue.Z in the alpha,
                // and the additive blend multiplies by that alpha again, so
                // hue.Z is the square root of the strength: rgb x a x 2(1 - a).
                Vector3 hue = new(0f, 0f, MathF.Sqrt(Math.Min(1f, 2f * dark)));

                batcher.Begin(null, matrix);
                batcher.SetBlendState(BlendState.Additive);

                // Stretched 3-5x, so sampled smoothly.
                batcher.SetSampler(SamplerState.LinearClamp);

                if (weather)
                {
                    DrawKrCloudLayer(batcher, KrLightTexture(KrLightProvider.OvercastClouds), cover * daylight * 0.5f / KrCloudMean);
                }

                DrawKrCloudLayer(batcher, KrLightTexture(KrLightProvider.AmbientNoise), 2f * dark);

                batcher.SetSampler(null);

                for (int i = 0; i < _lightCount; i++)
                {
                    ref LightData l = ref _lights[i];

                    if (KrLightTexture(l.KrTexture) is not { } texture)
                    {
                        continue;
                    }

                    float scale = KrLightProvider.SizeScale(l.KrTexture);

                    batcher.Draw(
                        texture,
                        new Vector2(l.DrawX - texture.Width * scale * 0.5f, l.DrawY - texture.Height * scale * 0.5f),
                        texture.Bounds,
                        hue,
                        0f,
                        Vector2.Zero,
                        scale,
                        SpriteEffects.None,
                        0f
                    );
                }

                batcher.SetBlendState(null);
                batcher.End();
            }

            _lightCount = 0;
            batcher.GraphicsDevice.SetRenderTarget(null);
        }

        /// <summary>
        /// Whether rain lands at a ground point (world pixels, as
        /// <c>(x - y) * 22, (x + y) * 22 - 4z</c>): not on a tile under the roof
        /// hidden while the player is indoors (anything at or above _maxZ).
        /// Outdoors every point is open; a splash over a drawn roof lands on it.
        /// </summary>
        private bool KrRainReaches(int px, int py)
        {
            if (_maxZ >= 127 || _world.Player is not { } player)
            {
                return true;
            }

            // The tile under the point, taken at the player's height.
            int z4 = player.Z * 4;
            int x = (int)MathF.Floor((px + py + z4) / 44f);
            int y = (int)MathF.Floor((py + z4 - px) / 44f);

            if (x < 0 || y < 0 || _world.Map.GetChunk(x, y, false) is not { } chunk)
            {
                return true;
            }

            for (GameObject obj = chunk.GetHeadObject(x % 8, y % 8); obj != null; obj = obj.TNext)
            {
                if (obj is not Land && obj is not Mobile && obj.Z >= _maxZ)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Tiles a light-map base layer (night noise or clouds) at
        /// <paramref name="strength"/>. Strengths past the shader's alpha of 1
        /// (KR's 2(1 - a), or the clouds' up to ~1.8) are drawn in passes.
        /// </summary>
        private void DrawKrCloudLayer(UltimaBatcher2D batcher, Texture2D texture, float strength)
        {
            if (texture == null || strength <= 0.005f)
            {
                return;
            }

            int passes = (int)MathF.Ceiling(strength);
            Vector3 hue = new(0f, 0f, MathF.Sqrt(strength / passes));

            // The world's origin on screen, as statics are placed
            // (GameObject.UpdateRealScreenPosition), so the layer moves with
            // the world (sideways, KrCloudDrift times as fast). Not the
            // player's: their tile changes a step before their sprite gets
            // there, which jumped it.
            double ox = -_offset.X * KrCloudDrift;
            int oy = -_offset.Y;
            // Cover what the camera shows, in the batch's coordinates: the
            // camera zooms about the screen centre, so zoomed out its view
            // starts left of and above 0, and tiling from 0 left a strip at
            // the edge uncovered - black once multiplied, sliding as the
            // player moved.
            Point lo = Camera.ScreenToWorld(Point.Zero);
            Point hi = Camera.ScreenToWorld(new Point(Camera.Bounds.Width, Camera.Bounds.Height));
            int phaseX = (int)(((ox % KrCloudWidth) + KrCloudWidth) % KrCloudWidth);
            int phaseY = ((oy % KrCloudHeight) + KrCloudHeight) % KrCloudHeight;
            int startX = phaseX + (int)MathF.Floor((lo.X - phaseX) / (float)KrCloudWidth) * KrCloudWidth;
            int startY = phaseY + (int)MathF.Floor((lo.Y - phaseY) / (float)KrCloudHeight) * KrCloudHeight;

            for (int pass = 0; pass < passes; pass++)
            {
                for (int y = startY; y < hi.Y; y += KrCloudHeight)
                {
                    for (int x = startX; x < hi.X; x += KrCloudWidth)
                    {
                        batcher.Draw(texture, new Rectangle(x, y, KrCloudWidth, KrCloudHeight), hue, 0f);
                    }
                }
            }
        }

        private Texture2D KrLightTexture(int id)
        {
            if (!_krLightTextures.TryGetValue(id, out Texture2D texture))
            {
                texture = null;

                if (KrLightProvider.Instance?.TryGetTexture(id, out uint[] pixels, out int w, out int h) == true)
                {
                    texture = new Texture2D(Client.Game.GraphicsDevice, w, h, false, SurfaceFormat.Color);
                    texture.SetData(pixels);
                }

                _krLightTextures[id] = texture;
            }

            return texture;
        }

        private void InitializeRenderTargets(RenderTargets renderTargets)
        {
            renderTargets.SetLightsConfiguration(
                KrLightProvider.IsActive ? _krLightsBlend
                    : UseAltLights ? _altLightsBlend : (UseLights ? _darknessBlend : () => null),
                () =>
                {
                    Vector3 v = Vector3.Zero;
                    v.Z = UseAltLights && !KrLightProvider.IsActive ? 0.5f : 1f;
                    return v;
                }
            );
        }

        public override void DrawUI(UltimaBatcher2D batcher)
        {
            _healthLinesManager.Draw(batcher, 0f);

            if (!UIManager.IsMouseOverWorld)
            {
                SelectedObject.Object = null;
            }

            _world.WorldTextManager.ProcessWorldText(true);
            _world.WorldTextManager.Draw(batcher, Camera.Bounds.X, Camera.Bounds.Y, 0);
        }

        public void DrawSelection(UltimaBatcher2D batcher, float layerDepth)
        {
            if (_isSelectionActive)
            {
                Vector3 selectionHue = new()
                {
                    Z = 0.7f
                };

                Point upperLeftInWorld = Camera.ScreenToWorld(new Point(
                    Math.Min(_selectionStart.X, Mouse.Position.X) - Camera.Bounds.X,
                    Math.Min(_selectionStart.Y, Mouse.Position.Y) - Camera.Bounds.Y
                ));

                Point lowerRightInWorld = Camera.ScreenToWorld(new Point(
                    Math.Max(_selectionStart.X, Mouse.Position.X) - Camera.Bounds.X,
                    Math.Max(_selectionStart.Y, Mouse.Position.Y) - Camera.Bounds.Y
                ));

                Rectangle selectionRect = new Rectangle(
                    upperLeftInWorld.X,
                    upperLeftInWorld.Y,
                    lowerRightInWorld.X - upperLeftInWorld.X,
                    lowerRightInWorld.Y - upperLeftInWorld.Y
                );

                batcher.Draw(
                    SolidColorTextureCache.GetTexture(Color.Black),
                    selectionRect,
                    selectionHue,
                    layerDepth
                );

                selectionHue.Z = 0.3f;

                batcher.DrawRectangle(
                    SolidColorTextureCache.GetTexture(Color.DeepSkyBlue),
                    selectionRect.X,
                    selectionRect.Y,
                    selectionRect.Width,
                    selectionRect.Height,
                    selectionHue,
                    layerDepth
                );
            }
        }

        private static readonly RenderedText _youAreDeadText = RenderedText.Create(
            ResGeneral.YouAreDead,
            0xFFFF,
            3,
            false,
            FontStyle.BlackBorder,
            TEXT_ALIGN_TYPE.TS_LEFT
        );

        private bool CheckDeathScreen(UltimaBatcher2D batcher)
        {
            if (
                ProfileManager.CurrentProfile != null
                && ProfileManager.CurrentProfile.EnableDeathScreen
            )
            {
                if (_world.InGame)
                {
                    if (_world.Player.IsDead && _world.Player.DeathScreenTimer > Time.Ticks)
                    {
                        batcher.Begin();
                        _youAreDeadText.Draw(
                            batcher,
                            Camera.Bounds.X + (Camera.Bounds.Width / 2 - _youAreDeadText.Width / 2),
                            Camera.Bounds.Bottom / 2,
                            0f
                        );
                        batcher.End();

                        return true;
                    }
                }
            }

            return false;
        }

        private void StopFollowing()
        {
            if (_followingMode)
            {
                _followingMode = false;
                _followingTarget = 0;
                _world.Player.Pathfinder.StopAutoWalk();

                _world.MessageManager.HandleMessage(
                    _world.Player,
                    ResGeneral.StoppedFollowing,
                    string.Empty,
                    0,
                    MessageType.Regular,
                    3,
                    TextType.CLIENT
                );
            }
        }

        private struct LightData
        {
            public byte ID;
            public ushort Color;
            public bool IsHue;
            public int DrawX,
                DrawY;

            /// <summary>The KR light texture (TerrainTexture id) kr_lighting draws.</summary>
            public int KrTexture;
        }

        /// <summary>
        /// Whether a static gives light: a 2D light source, or, under
        /// kr_lighting, any object whose KR tileart names a light (mushrooms,
        /// treasure, crystals).
        /// </summary>
        public static bool GivesLight(ushort graphic, bool isLight)
        {
            if (KrLightProvider.Instance is not { } kr)
            {
                return isLight;
            }

            return !IsKrUnlitWindow(kr, graphic) && (isLight || kr.LightFor(graphic) > 0);
        }

        /// <summary>
        /// KR draws no light for windows. Seen at Trammel 4541,928 by night:
        /// the windows 0x13A, 0x13B, 0x154 and 0x156 were on screen, all naming
        /// a light_window texture in their tileart, and every frame drew only
        /// the two mushrooms' lights. Windows are the tiledata Window flag
        /// (windows, stained glass, shutters, arrow loops) or a light_window
        /// light (TerrainTexture 3 and 4), which also catches 0x154 and 0x156.
        /// </summary>
        private static bool IsKrUnlitWindow(KrLightProvider kr, ushort graphic) =>
            graphic < Client.Game.UO.FileManager.TileData.StaticData.Length
                && Client.Game.UO.FileManager.TileData.StaticData[graphic].IsWindow
            || kr.LightFor(graphic) is 3 or 4;
    }
}
