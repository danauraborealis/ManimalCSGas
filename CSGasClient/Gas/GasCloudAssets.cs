using System;
using System.IO;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // loads the authored gas assets from csgas_fx.bundle: the cloud particle prefab and the
    // tears vignette shader. bundles are registered in bundles.json, so the CLIENT gets
    // them through SPT's bundle pipeline — resolve the local file via BundleManager
    // (direct mod path for a local server, downloaded cache for Fika/headless remotes).
    // never assume client and server share a disk (WTT review feedback). the old
    // hardcoded server-folder path survives only as a last-resort fallback. SPT doesnt
    // eager-load these bundles (no item references them), so LoadFromFile + Unload(false)
    // is fine. one load attempt, everything cached; each asset degrades independently
    // (no prefab → M18 smoke clone; no shader → no tears overlay).
    internal static class GasCloudAssets
    {
        private const string FxBundleKey = "csgas_fx.bundle";
        private const string CoughBundleKey = "cough_animation.bundle";
        private const string LegacyBundleDir = @"SPT\user\mods\CSGasServer\bundles";
        private const string PrefabName = "csgas_cloud_particles";
        private const string TearsShaderName = "Hidden/CSGasTears";
        private const string RealTearsShaderName = "Hidden/TearsEffect";

        private static GameObject _cloudPrefab;
        private static Shader _tearsShader;
        private static Material _tearsMaterial;
        private static AnimationClip _coughClip;
        private static bool _loadTried;
        private static bool _coughLoadTried;

        public static GameObject GetCloudPrefab()
        {
            EnsureLoaded();
            return _cloudPrefab;
        }

        // the cloud prefab's particle material — reused for the grenade's fuse trail so the
        // trail can never be green regardless of where the vanilla color lives
        public static Material GetCloudMaterial()
        {
            var prefab = GetCloudPrefab();
            if (prefab == null) return null;
            var renderer = prefab.GetComponentInChildren<ParticleSystemRenderer>(true);
            return renderer != null ? renderer.sharedMaterial : null;
        }

        // the authored material carries the noise textures the tears shader needs — a bare
        // new Material(shader) would sample white. falls back to shader-only if no .mat.
        public static Material GetTearsMaterial()
        {
            EnsureLoaded();
            if (_tearsMaterial != null) return _tearsMaterial;
            if (_tearsShader != null) return _tearsMaterial = new Material(_tearsShader);
            return null;
        }

        // resolves a manifest bundle key to a local file the client can read. exact key
        // first, then filename match (other mods' keys may carry subfolders), then the
        // legacy same-disk server path.
        private static string ResolveBundlePath(string key)
        {
            try
            {
                if (SPT.Custom.Utils.BundleManager.Bundles.TryGetValue(key, out var item))
                    return SPT.Custom.Utils.BundleManager.GetBundleFilePath(item);
                foreach (var kv in SPT.Custom.Utils.BundleManager.Bundles)
                {
                    if (Path.GetFileName(kv.Key) == key)
                        return SPT.Custom.Utils.BundleManager.GetBundleFilePath(kv.Value);
                }
                Plugin.LogSource?.LogWarning($"[CSGas] '{key}' not in the client bundle manifest — trying legacy local path");
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogWarning($"[CSGas] BundleManager lookup failed for '{key}': {e.Message} — trying legacy local path");
            }
            return Path.Combine(BepInEx.Paths.GameRootPath, LegacyBundleDir, key);
        }

        // first-person cough clip (med-use-blocked reaction), separate bundle so the user
        // can iterate on the animation without rebuilding the fx bundle
        public static AnimationClip GetCoughClip()
        {
            if (_coughLoadTried) return _coughClip;
            _coughLoadTried = true;
            try
            {
                var path = ResolveBundlePath(CoughBundleKey);
                if (!File.Exists(path))
                {
                    Plugin.LogSource?.LogWarning($"[CSGas] cough bundle not found at {path} — med-block cough animation disabled");
                    return null;
                }
                var bundle = AssetBundle.LoadFromFile(path);
                if (bundle == null)
                {
                    Plugin.LogSource?.LogError("[CSGas] AssetBundle.LoadFromFile returned null for cough_animation.bundle");
                    return null;
                }
                var clips = bundle.LoadAllAssets<AnimationClip>();
                _coughClip = clips != null && clips.Length > 0 ? clips[0] : null;
                if (_coughClip == null)
                    Plugin.LogSource?.LogError("[CSGas] cough_animation.bundle contains no AnimationClip");
                else
                    Plugin.LogSource?.LogInfo($"[CSGas] cough clip '{_coughClip.name}' loaded ({_coughClip.length:0.00}s)");
                bundle.Unload(false);
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogError($"[CSGas] cough bundle load failed: {e.Message}");
            }
            return _coughClip;
        }

        private static void EnsureLoaded()
        {
            if (_loadTried) return;
            _loadTried = true;
            try
            {
                var bundlePath = ResolveBundlePath(FxBundleKey);
                if (!File.Exists(bundlePath))
                {
                    Plugin.LogSource?.LogWarning($"[CSGas] cloud bundle not found at {bundlePath} — falling back to M18 smoke clone, no tears overlay");
                    return;
                }

                var bundle = AssetBundle.LoadFromFile(bundlePath);
                if (bundle == null)
                {
                    Plugin.LogSource?.LogError("[CSGas] AssetBundle.LoadFromFile returned null for csgas_fx.bundle");
                    return;
                }

                // load by full asset path — short-name LoadAsset came back null for
                // "Assets/CS Gas/csgas_cloud_particles.prefab" (test-raid log)
                foreach (var assetName in bundle.GetAllAssetNames())
                {
                    if (assetName.EndsWith(PrefabName + ".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        _cloudPrefab = bundle.LoadAsset<GameObject>(assetName);
                        break;
                    }
                }
                if (_cloudPrefab != null && _cloudPrefab.GetComponentInChildren<ParticleSystem>(true) == null)
                {
                    Plugin.LogSource?.LogError($"[CSGas] prefab '{PrefabName}' has no ParticleSystem — ignoring it");
                    _cloudPrefab = null;
                }

                // prefer the authored csgas_tears material (carries the noise textures);
                // shader-name match covers a renamed .mat
                foreach (var mat in bundle.LoadAllAssets<Material>())
                {
                    if (mat == null || mat.shader == null) continue;
                    if (mat.name == "csgas_tears"
                        || mat.shader.name == TearsShaderName || mat.shader.name == RealTearsShaderName)
                    {
                        _tearsMaterial = mat;
                        _tearsShader = mat.shader;
                        break;
                    }
                }
                if (_tearsShader == null)
                {
                    // the REAL 1.0 shader (UABEA-transplanted, "Hidden/TearsEffect") wins
                    // over the authored replica when both are present
                    foreach (var shader in bundle.LoadAllAssets<Shader>())
                    {
                        if (shader == null) continue;
                        if (shader.name == RealTearsShaderName) { _tearsShader = shader; break; }
                        if (shader.name == TearsShaderName) _tearsShader = shader;
                    }
                }
                // a transplanted object may lack an m_Container entry, in which case
                // LoadAllAssets skips it — but loading the bundle registers its shaders,
                // so Shader.Find gets a second chance at the real one
                if (_tearsShader == null)
                {
                    var real = Shader.Find(RealTearsShaderName);
                    if (real != null) _tearsShader = real;
                }

                if (_cloudPrefab == null || _tearsShader == null)
                    Plugin.LogSource?.LogWarning(
                        $"[CSGas] bundle loaded, prefab={(_cloudPrefab != null ? "ok" : "MISSING")} tearsShader={(_tearsShader != null ? "ok" : "MISSING")}. contents: {string.Join(", ", bundle.GetAllAssetNames())}");
                else
                    Plugin.LogSource?.LogInfo("[CSGas] cloud prefab + tears shader loaded from csgas_fx.bundle");
                bundle.Unload(false);
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogError($"[CSGas] cloud bundle load failed: {e.Message}");
            }
        }
    }
}
