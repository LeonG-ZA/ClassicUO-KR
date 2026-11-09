// SPDX-License-Identifier: BSD-2-Clause
//
// KR's lighting data from the user's own UOKR install, for the kr_lighting
// setting:
//
//   * an object's light is named in its tileart, as a UOSpriteShader whose
//     first layer is "Data\Textures\NNNNNNNN_name.tga" - the mushroom
//     0xD11 names 00000006_MushroomA, a candle 00000007_CandleA;
//   * those textures are TerrainTexture.uop ids 1-17, colour with the shape
//     in alpha, drawn at one texel per 2D pixel (a 256 texture spans 256 px);
//   * the night ambient is TerrainTexture 21, a dark-blue noise.

using System;
using System.Collections.Generic;
using ClassicUO.KR;
using ClassicUO.KR.Textures;
using ClassicUO.KR.Uop;
using ClassicUO.KR.World;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Assets
{
    public sealed class KrLightProvider : IDisposable
    {
        /// <summary>00000001_light_generic, for 2D light sources KR names no light for.</summary>
        public const int GenericLight = 1;

        /// <summary>The dark-blue noise KR draws the night ambient with.</summary>
        public const int AmbientNoise = 21;

        /// <summary>Grey-teal clouds, a weather layer; unused, too strong a tint.</summary>
        public const int StormClouds = 22;

        /// <summary>Grey clouds, never seen; drawn for rain and storms.</summary>
        public const int OvercastClouds = 20;

        /// <summary>00000002_light_post, the lamp posts' light.</summary>
        public const int LightPost = 2;

        /// <summary>
        /// Screen pixels per texel KR draws a light at: 1 for all, except the
        /// lamp posts' light_post at 4/3 (341 px for its 256x256
        /// texture).
        /// </summary>
        public static float SizeScale(int id) => id == LightPost ? 4f / 3f : 1f;

        private readonly KrAssetStore _store;
        private readonly KrStringDictionary _strings;
        private readonly Dictionary<ushort, int> _byGraphic = [];

        private KrLightProvider(KrAssetStore store, KrStringDictionary strings)
        {
            _store = store;
            _strings = strings;
        }

        public static KrLightProvider Instance { get; private set; }

        public static bool IsActive => Instance != null;

        public static bool Initialize(string krPath)
        {
            Shutdown();

            if (string.IsNullOrWhiteSpace(krPath) || !KrAssetStore.LooksLikeKrInstall(krPath))
            {
                return false;
            }

            try
            {
                KrAssetStore store = new(krPath);
                KrStringDictionary strings = KrStringDictionary.Load(
                    System.IO.Path.Combine(krPath, KrStringDictionary.PackageName));

                Instance = new KrLightProvider(store, strings);

                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"KR lighting unavailable ({ex.Message}); using 2D lights.");
                Instance = null;

                return false;
            }
        }

        public static void Shutdown()
        {
            Instance?.Dispose();
            Instance = null;
        }

        /// <summary>The TerrainTexture id of the light a static's tileart names, or -1 when none.</summary>
        public int LightFor(ushort graphic)
        {
            if (_byGraphic.TryGetValue(graphic, out int id))
            {
                return id;
            }

            id = -1;

            if (KrTileArt.ReadRaw(_store, graphic) is { } raw)
            {
                foreach (KrTileArt.ShaderDefinition def in KrTileArt.Shaders(raw, _strings))
                {
                    if (def.Layers.Count > 0
                        && def.Layers[0].Texture.Contains("\\Textures\\", StringComparison.OrdinalIgnoreCase)
                        && KrTileArt.ArtNumber(def.Layers[0].Texture) is { } n and > 0 and < 64)
                    {
                        id = n;

                        break;
                    }
                }
            }

            _byGraphic[graphic] = id;

            return id;
        }

        /// <summary>A TerrainTexture's pixels (0xAABBGGRR, top-down), or false when it is missing.</summary>
        public bool TryGetTexture(int id, out uint[] pixels, out int width, out int height)
        {
            pixels = null;
            width = height = 0;

            byte[] raw = _store.GetPackage(KrTexturePackages.TerrainTexturePackage)
                ?.Read(KrTexturePackages.TerrainTextureEntryName(id));

            if (raw == null)
            {
                return false;
            }

            try
            {
                DdsImage image = DdsImage.Decode(raw);

                pixels = image.Pixels;
                width = image.Width;
                height = image.Height;

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        public void Dispose() => _store.Dispose();
    }
}
