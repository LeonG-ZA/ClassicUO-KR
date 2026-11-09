// SPDX-License-Identifier: BSD-2-Clause
//
// KR's particle effects from the user's own UOKR install, for the
// kr_particle_effects setting. Nothing from the install is shipped: scripts
// and textures are read at run time.

using System;
using ClassicUO.KR;
using ClassicUO.KR.Effects;
using ClassicUO.Utility.Logging;

namespace ClassicUO.Assets
{
    public sealed class KrEffectProvider
    {
        private KrEffectProvider(KrAssetStore store, bool enhanced = false, KrEffects fallback = null)
        {
            Store = store;
            Effects = new KrEffects(store) { Fallback = fallback };
            IsEnhanced = enhanced;
        }

        /// <summary>Whether effects come from an Enhanced Client install (ec_effects).</summary>
        public bool IsEnhanced { get; }

        public static KrEffectProvider Instance { get; private set; }

        public static bool IsActive => Instance != null;

        public KrAssetStore Store { get; }

        public KrEffects Effects { get; }

        /// <remarks>
        /// With <paramref name="ecPath"/> set to an Enhanced Client install,
        /// effects - spell effects and the flames and sparks on statics - are
        /// played from it instead, with KR's (when installed) behind it for any
        /// scene EC's cannot be read. When that install cannot be read, KR is
        /// used as usual.
        /// </remarks>
        public static bool Initialize(string krPath, string ecPath = null)
        {
            Instance = null;

            bool kr = !string.IsNullOrWhiteSpace(krPath) && KrAssetStore.LooksLikeKrInstall(krPath);
            bool enhanced = !string.IsNullOrWhiteSpace(ecPath)
                && System.IO.File.Exists(System.IO.Path.Combine(ecPath, KrEffects.ScenePackage))
                && KrAssetStore.LooksLikeKrInstall(ecPath);

            if (!string.IsNullOrWhiteSpace(ecPath) && !enhanced)
            {
                Log.Warn($"EC effects: '{ecPath}' is not an Enhanced Client install; using KR's effects.");
            }

            if (!kr && !enhanced)
            {
                return false;
            }

            try
            {
                if (enhanced)
                {
                    KrEffects fallback = kr ? new KrEffects(new KrAssetStore(krPath)) : null;

                    Instance = new KrEffectProvider(new KrAssetStore(ecPath), true, fallback);
                    Log.Trace($"EC effects: on{(kr ? ", KR behind them" : "")}.");
                }
                else
                {
                    Instance = new KrEffectProvider(new KrAssetStore(krPath));
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($"KR effects unavailable: {ex.Message}");

                return false;
            }
        }

        /// <summary>
        /// A creature's looping scene, or null: standing in for one KR does
        /// not animate (the wisp), or running with its frames (the Fire
        /// Elemental's fireball).
        /// </summary>
        public IKrEffect CreateForBody(int body, int seed)
        {
            string scene = Effects.SceneForBody(body, withFrames: true);

            if (scene is null || Effects.Create(scene, seed) is not NifEffect nif)
            {
                return null;
            }

            nif.Loop = true;

            return nif;
        }

        /// <summary>A running effect for a server effect id - .ems or NIF - or null.</summary>
        public IKrEffect Create(int effectId, int seed)
        {
            string scene = Effects.SceneForServerEffect(effectId);

            return scene is null ? null : Effects.Create(scene, seed);
        }

        /// <summary>The script a server effect id plays, when it is an .ems scene this can run.</summary>
        public EmsScript ScriptFor(int effectId)
        {
            string scene = Effects.SceneForServerEffect(effectId);

            return scene is null ? null : Effects.Script(scene);
        }
    }
}
