// SPDX-License-Identifier: BSD-2-Clause

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClassicUO.Configuration.Json;
using Microsoft.Xna.Framework;

namespace ClassicUO.Configuration
{
    [JsonSourceGenerationOptions(WriteIndented = true, GenerationMode = JsonSourceGenerationMode.Metadata)]
    [JsonSerializable(typeof(Settings), GenerationMode = JsonSourceGenerationMode.Metadata)]
    sealed partial class SettingsJsonContext : JsonSerializerContext
    {
        // horrible fix: https://github.com/ClassicUO/ClassicUO/issues/1663
        public static SettingsJsonContext RealDefault { get; } = new SettingsJsonContext(
            new JsonSerializerOptions()
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });
    }

    internal sealed class Settings
    {
        public const string SETTINGS_FILENAME = "settings.json";
        public static Settings GlobalSettings = new Settings();
        public static string CustomSettingsFilepath = null;


        [JsonPropertyName("username")] public string Username { get; set; } = string.Empty;

        [JsonPropertyName("password")] public string Password { get; set; } = string.Empty;

        [JsonPropertyName("ip")] public string IP { get; set; } = "127.0.0.1";

        [JsonPropertyName("port"), JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)] public ushort Port { get; set; } = 2593;

        /**
         * Ignores the login servers relay packet, connects back with the settings IP
         */
        [JsonPropertyName("ignore_relay_ip")] public bool IgnoreRelayIp { get; set; } = false;

        [JsonPropertyName("ultimaonlinedirectory")] public string UltimaOnlineDirectory { get; set; } = "";

        [JsonPropertyName("profilespath")] public string ProfilesPath { get; set; } = string.Empty;

        [JsonPropertyName("clientversion")] public string ClientVersion { get; set; } = string.Empty;

        [JsonPropertyName("lang")] public string Language { get; set; } = "";

        [JsonPropertyName("lastservernum")] public ushort LastServerNum { get; set; } = 1;

        [JsonPropertyName("last_server_name")] public string LastServerName { get; set; } = string.Empty;

        [JsonPropertyName("fps")] public int FPS { get; set; } = 60;

        [JsonPropertyName("screen_scale")] public float ScreenScale { get; set; } = 1f;

        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_position")] public Point? WindowPosition { get; set; }
        [JsonConverter(typeof(NullablePoint2Converter))] [JsonPropertyName("window_size")] public Point? WindowSize { get; set; }

        [JsonPropertyName("is_win_maximized")] public bool IsWindowMaximized { get; set; } = true;

        [JsonPropertyName("saveaccount")] public bool SaveAccount { get; set; }

        [JsonPropertyName("autologin")] public bool AutoLogin { get; set; }

        [JsonPropertyName("reconnect")] public bool Reconnect { get; set; }

        [JsonPropertyName("reconnect_time")] public int ReconnectTime { get; set; } = 1;

        [JsonPropertyName("login_music")] public bool LoginMusic { get; set; } = true;

        [JsonPropertyName("login_music_volume")] public int LoginMusicVolume { get; set; } = 70;

        [JsonPropertyName("fixed_time_step")] public bool FixedTimeStep { get; set; } = true;

        [JsonPropertyName("run_mouse_in_separate_thread")]
        public bool RunMouseInASeparateThread { get; set; } = true;

        [JsonPropertyName("force_driver")] public byte ForceDriver { get; set; }

        [JsonPropertyName("use_verdata")] public bool UseVerdata { get; set; }

        [JsonPropertyName("maps_layouts")] public string MapsLayouts { get; set; }

        [JsonPropertyName("encryption")] public byte Encryption { get; set; }

        [JsonPropertyName("plugins")] public string[] Plugins { get; set; } = { @"./Assistant/Razor.dll" };
        
        [JsonPropertyName("files_override")] public string OverrideFile { get; set; }

        /// <summary>Folder of a local Kingdom Reborn install to read KR art from. Empty disables KR art.</summary>
        [JsonPropertyName("kr_path")] public string KrPath { get; set; } = string.Empty;

        /// <summary>Render mobiles with KR animations when <see cref="KrPath"/> has them.</summary>
        [JsonPropertyName("kr_animations")] public bool KrAnimations { get; set; } = true;

        /// <summary>
        /// An Enhanced Client install. With <see cref="EcHumans"/> on, people -
        /// humans, elves, gargoyles and what they wear - are drawn from its
        /// animations instead of KR's, which leave some held items in the wrong
        /// hand. Needs KR animations on.
        /// </summary>
        /// <summary>
        /// Light sources (tiledata LightSource) that KR draws with a separate
        /// flame scene - torches, candles, braziers - keep their 2D art and
        /// animation, with no KR art or flame. Light sources KR draws in one
        /// piece keep their KR art. A stopgap while KR's flame animations
        /// are wrong.
        /// </summary>
        [JsonPropertyName("kr_light_sources_2d")] public bool KrLightSources2D { get; set; } = false;

        /// <summary>Shadows on the ground only, once a pixel, as KR draws them.</summary>
        [JsonPropertyName("kr_ground_shadows")] public bool KrGroundShadows { get; set; } = true;

        /// <summary>
        /// KR's lighting: the night darkens the world through a blue noise
        /// ambient and each light-giving object - candles, windows, mushrooms -
        /// brightens it with its own KR light texture. Replaces the 2D client's lights.
        /// </summary>
        [JsonPropertyName("kr_lighting")] public bool KrLighting { get; set; } = true;

        [JsonPropertyName("ec_path")] public string EcPath { get; set; } = string.Empty;

        [JsonPropertyName("ec_humans")] public bool EcHumans { get; set; } = true;

        /// <summary>
        /// Draw the ground from the Enhanced Client at <see cref="EcPath"/>
        /// instead of KR: its terrain definitions, textures and land-to-terrain
        /// table (legacyterrainmap.csv). Off by default; KR stays the source.
        /// Needs kr_terrain on.
        /// </summary>
        [JsonPropertyName("ec_terrain")] public bool EcTerrain { get; set; } = true;

        /// <summary>
        /// Draw statics - buildings, trees, items - from the Enhanced Client at
        /// <see cref="EcPath"/> instead of KR: its tileart, world art and facets.
        /// Off by default; KR stays the source. Needs kr_statics on.
        /// </summary>
        [JsonPropertyName("ec_statics")] public bool EcStatics { get; set; } = true;

        /// <summary>
        /// Play effects - spell effects, and flames and sparks on statics - from
        /// the Enhanced Client at <see cref="EcPath"/> instead of KR, with KR
        /// behind them for scenes EC's cannot be read. Off by default; KR stays
        /// the source. Needs kr_particle_effects on.
        /// </summary>
        [JsonPropertyName("ec_effects")] public bool EcEffects { get; set; } = true;

        /// <summary>Factor EC people are drawn at; 0.93 puts EC's human on the 2D grid.</summary>
        /// <summary>Extra screen px an EC rider sits lower on a KR mount, beyond the measured seat.</summary>
        [JsonPropertyName("ec_rider_drop")] public float EcRiderDrop { get; set; } = 0f;

        [JsonPropertyName("ec_frame_scale")] public double EcFrameScale { get; set; } = 0.93;

        /// <summary>
        /// Paint land tiles with KR terrain textures when <see cref="KrPath"/>
        /// has them. Off by default: it changes the look of the whole world,
        /// and it costs a ~2.4s sweep of the facet packages at startup.
        /// </summary>
        [JsonPropertyName("kr_terrain")] public bool KrTerrain { get; set; } = true;

        // KR terrain on the GPU: uniform composite land is composed by the
        // shader from an atlas of the terrains' textures, at their own
        // resolution, instead of the CPU baking a 44 px tile per cell. Off
        // keeps the CPU path for everything (a fallback for a GPU it fails on).
        [JsonPropertyName("kr_gpu_terrain")] public bool KrGpuTerrain { get; set; } = true;

        // KR statics at their native resolution: kept in the atlas at KR's
        // own detail and scaled down by the GPU when drawn, instead of being
        // resampled to the 2D size on the CPU. Off resamples as before.
        [JsonPropertyName("kr_native_detail")] public bool KrNativeDetail { get; set; } = true;

        /// <summary>
        /// Factor KR animation frames are resampled by. The default 2/3 is the
        /// reciprocal of the measured KR-to-2D human ratio, which puts a KR
        /// human on the 2D client's 44x44 tile grid; 1.0 passes KR art through
        /// at its native size. Stored as a fraction so the default keeps its
        /// exact value - the options slider works in whole percent.
        /// </summary>
        /// <summary>
        /// Draw statics - buildings, trees, furniture - from KR's world art
        /// instead of art.mul. Off by default: it changes the look of
        /// everything above the ground, and anything KR has no art for stays
        /// on the 2D sprite.
        /// </summary>
        // KR-style water: a ripple and sun-glint shader over the water tiles
        // (KR draws water with UOWaterShader). Off draws plain 2D water.
        [JsonPropertyName("kr_water_shader")] public bool KrWaterShader { get; set; } = true;

        // KR particle effects: spell effects, and flames and sparks on statics.
        // Without kr_advertise_uotd the shard sends a spell's 2D effect, which
        // is played as the KR effect it stands for (KrEffectFallback). Off by
        // default.
        [JsonPropertyName("kr_particle_effects")] public bool KrParticleEffects { get; set; } = true;

        // With kr_particle_effects, ask the shard for its particle effects
        // (packet 0xC7, which carries the KR effect id): RunUO sends 0xC7
        // only to clients it takes for UO:TD, KR or EC, so the play-character
        // packet sets the UO:TD client flag (0x100). Some shards turn UO:TD
        // clients away. Off by default.
        [JsonPropertyName("kr_advertise_uotd")] public bool KrAdvertiseUotd { get; set; } = false;

        // Writes each spell effect's KR scene to the journal ("KR fx ..."),
        // from 0xC7 or from a 2D effect, to compare the two. Off by default.
        [JsonPropertyName("kr_effect_log")] public bool KrEffectLog { get; set; } = false;

        // KR weather: snow as KR's particle scene, rain as its streak sheets.
        // Off draws the 2D weather. Needs kr_path.
        [JsonPropertyName("kr_weather")] public bool KrWeather { get; set; } = true;

        // With kr_weather, rain splashes on the ground as KR's puddle scenes
        // do (Splash.tga rings), kept off ground under a roof hidden over the
        // player. Off by default.
        [JsonPropertyName("kr_rain_splashes")] public bool KrRainSplashes { get; set; } = false;

        [JsonPropertyName("kr_statics")] public bool KrStatics { get; set; } = true;

        // Items in containers and the backpack drawn with the KR (or EC, with
        // ec_statics) item art, as the world draws them. Needs kr_statics.
        // Off keeps the 2D art in containers.
        [JsonPropertyName("kr_container_item_art")] public bool KrContainerItemArt { get; set; } = true;

        [JsonPropertyName("kr_frame_scale")] public double KrFrameScale { get; set; } = 2.0 / 3.0;

        public static string GetSettingsFilepath()
        {
            if (CustomSettingsFilepath != null)
            {
                if (Path.IsPathRooted(CustomSettingsFilepath))
                {
                    return CustomSettingsFilepath;
                }

                return Path.Combine(CUOEnviroment.ExecutablePath, CustomSettingsFilepath);
            }

            return Path.Combine(CUOEnviroment.ExecutablePath, SETTINGS_FILENAME);
        }


        public void Save()
        {
            // Make a copy of the settings object that we will use in the saving process
            var json = JsonSerializer.Serialize(this, SettingsJsonContext.RealDefault.Settings);
            var settingsToSave = JsonSerializer.Deserialize(json, SettingsJsonContext.RealDefault.Settings);

            // Make sure we don't save username and password if `saveaccount` flag is not set
            // NOTE: Even if we pass username and password via command-line arguments they won't be saved
            if (!settingsToSave.SaveAccount)
            {
                settingsToSave.Username = string.Empty;
                settingsToSave.Password = string.Empty;
            }

            settingsToSave.ProfilesPath = string.Empty;

            // NOTE: We can do any other settings clean-ups here before we save them

            ConfigurationResolver.Save(settingsToSave, GetSettingsFilepath(), SettingsJsonContext.RealDefault.Settings);
        }
    }
}