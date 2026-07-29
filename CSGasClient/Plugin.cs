using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Manimal.CSGas.Gas;
using UnityEngine;

namespace Manimal.CSGas
{
    // forge-compliant: GUID reverse-domain lowercase, name "Username-ModName".
    // version comes from ModVersion.g.cs (generated from $(ModVersion) in Directory.Build.props).
    // hard dep on commonlib so the item template exists before any of our patches run.
    [BepInPlugin(PluginGuid, PluginName, ModVersion.Value)]
    [BepInDependency("com.wtt.commonlib")]
    // soft: only ordering — when Fika is present it must load first so our conditional
    // FikaPlayer patch can resolve its type
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.manimal.csgas";
        public const string PluginName = "Manimal-CSGas";

        public static ManualLogSource LogSource;

        // --- gas cloud config (read live by CsGasCloud each tick) ---
        public static ConfigEntry<float> GasCoughIntervalSec;
        public static ConfigEntry<float> GasThoraxDamagePerSec;
        public static ConfigEntry<bool> GasTearsEnabled;
        public static ConfigEntry<float> GasTearsStrength;
        public static ConfigEntry<bool> GasLongExposureIntoxication;
        public static ConfigEntry<float> GasLongExposureSec;
        public static ConfigEntry<bool> GasRequiresLineOfSight;
        public static ConfigEntry<string> GasMaskExtraTpls;
        public static ConfigEntry<string> GasRespiratorExtraTpls;

        // cough animation blending — same knobs as WatchAnims, read live each play
        public static ConfigEntry<float> CoughBlendInTime;
        public static ConfigEntry<float> CoughBlendOutTime;
        public static ConfigEntry<float> CoughBlendOutLead;

        // --- volumetric cloud (RShG-2 explosion caster repurposed as gas propagation) ---
        public static ConfigEntry<bool> GasVolumetricCloud;
        public static ConfigEntry<float> GasMaxRadius;
        public static ConfigEntry<float> GasGrowTimeSec;
        public static ConfigEntry<int> GasRayCount;
        public static ConfigEntry<int> GasParticlesPerEmit;
        public static ConfigEntry<float> GasEmitIntervalSec;
        public static ConfigEntry<float> GasPointRadiusM;
        public static ConfigEntry<float> GasCloudOpacity;

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("CSGas loaded!");

            GasCoughIntervalSec = Config.Bind("Gas", "CoughIntervalSec", 4f,
                new ConfigDescription("Seconds between coughs while inside the cloud.", new AcceptableValueRange<float>(1f, 15f)));
            GasThoraxDamagePerSec = Config.Bind("Gas", "ThoraxDamagePerSec", 2f,
                new ConfigDescription("Asphyxiation damage to the thorax while inside (1.0 uses 2 HP/s). 0 disables.", new AcceptableValueRange<float>(0f, 10f)));
            GasTearsEnabled = Config.Bind("Gas", "TearsEnabled", true,
                "Eye-irritation screen overlay. Kill switch in case a render pipeline conflict corrupts the screen.");
            GasTearsStrength = Config.Bind("Gas", "TearsStrength", 0.5f,
                new ConfigDescription("Eye-irritation vignette intensity while in the gas (1.0 caps tear gas at 0.5).", new AcceptableValueRange<float>(0f, 1f)));
            GasLongExposureIntoxication = Config.Bind("Gas", "LongExposureIntoxication", false,
                "Apply the Intoxication effect after prolonged exposure (harsh — off by default).");
            GasLongExposureSec = Config.Bind("Gas", "LongExposureSec", 25f,
                new ConfigDescription("Seconds of continuous exposure that count as prolonged.", new AcceptableValueRange<float>(5f, 120f)));
            GasRequiresLineOfSight = Config.Bind("Gas", "RequiresLineOfSight", true,
                "A wall between you and the cloud center blocks the effect (crude stand-in for 1.0's propagation).");
            GasMaskExtraTpls = Config.Bind("Gas", "ExtraGasMaskTpls", "",
                "Comma-separated extra FaceCover tpls that FULLY block the gas (like GP-5/GP-7/Avon M53A1).");
            GasRespiratorExtraTpls = Config.Bind("Gas", "ExtraRespiratorTpls", "",
                "Comma-separated extra FaceCover tpls that block only the health/cough effects (eyes still burn), like the respirators.");

            CoughBlendInTime = Config.Bind("Cough Animation", "Blend in time", 0.15f,
                new ConfigDescription("Seconds to fade the cough animation in.", new AcceptableValueRange<float>(0f, 2f)));
            CoughBlendOutTime = Config.Bind("Cough Animation", "Blend out time", 0.15f,
                new ConfigDescription("Seconds the fade back to idle takes.", new AcceptableValueRange<float>(0f, 2f)));
            CoughBlendOutLead = Config.Bind("Cough Animation", "Blend out lead", 0.5f,
                new ConfigDescription("How many seconds before the clip's end the fade-out starts.", new AcceptableValueRange<float>(0f, 3f)));

            GasVolumetricCloud = Config.Bind("Volumetric", "Enabled", true,
                "Room-filling gas cloud using the RShG-2 volumetric explosion raycaster. Off = simple sphere cloud.");
            GasMaxRadius = Config.Bind("Volumetric", "MaxRadiusM", 8f,
                new ConfigDescription("Max gas spread distance (m). 8 is the clamp 1.0 applies to volumetric throwables.", new AcceptableValueRange<float>(2f, 20f)));
            GasGrowTimeSec = Config.Bind("Volumetric", "GrowTimeSec", 10f,
                new ConfigDescription("Seconds for the cloud to reach max spread.", new AcceptableValueRange<float>(0f, 60f)));
            GasRayCount = Config.Bind("Volumetric", "RayCount", 500,
                new ConfigDescription("Rays cast to map the open space around the grenade (batched, cheap).", new AcceptableValueRange<int>(100, 2000)));
            GasParticlesPerEmit = Config.Bind("Volumetric", "ParticlesPerEmit", 25,
                new ConfigDescription("Smoke particles placed per emit tick.", new AcceptableValueRange<int>(5, 200)));
            GasEmitIntervalSec = Config.Bind("Volumetric", "EmitIntervalSec", 0.35f,
                new ConfigDescription("Seconds between emit ticks.", new AcceptableValueRange<float>(0.1f, 2f)));
            GasPointRadiusM = Config.Bind("Volumetric", "PointRadiusM", 1.8f,
                new ConfigDescription("You count as inside the gas when within this distance of a cloud point.", new AcceptableValueRange<float>(0.5f, 5f)));
            GasCloudOpacity = Config.Bind("Volumetric", "CloudOpacity", 0.6f,
                new ConfigDescription("Per-particle alpha for the gas cloud. Applies to newly emitted puffs (existing ones fade out at their old value).", new AcceptableValueRange<float>(0.05f, 1f)));

            foreach (var tpl in GasMaskExtraTpls.Value.Split(','))
            {
                var trimmed = tpl.Trim();
                if (trimmed.Length > 0) CSGasConstants.FullBlockTpls.Add(trimmed);
            }
            foreach (var tpl in GasRespiratorExtraTpls.Value.Split(','))
            {
                var trimmed = tpl.Trim();
                if (trimmed.Length > 0) CSGasConstants.RespiratorTpls.Add(trimmed);
            }

            SafeEnable("CsGasPrefabRepairPatch",   () => new CsGasPrefabRepairPatch().Enable());
            SafeEnable("CsGasEmissionKeyPatch",    () => new CsGasEmissionKeyPatch().Enable());
            SafeEnable("CsGasGrenadeInitFixPatch", () => new CsGasGrenadeInitFixPatch().Enable());
            SafeEnable("CsGasCloudPatch",          () => new CsGasCloudPatch().Enable());
            SafeEnable("CsGasExplosionPatch",      () => new CsGasExplosionPatch().Enable());
            SafeEnable("CsGasEmissionAttachPatch", () => new CsGasEmissionAttachPatch().Enable());
            SafeEnable("CsGasHitDebuffMutePatch",  () => new CsGasHitDebuffMutePatch().Enable());
            SafeEnable("CsGasHurtVoiceMutePatch",  () => new CsGasHurtVoiceMutePatch().Enable());
            SafeEnable("CsGasMedBlockPatch",       () => new CsGasMedBlockPatch().Enable());
            if (HarmonyLib.AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer") != null)
                SafeEnable("CsGasMedBlockFikaPatch", () => new CsGasMedBlockFikaPatch().Enable());
            SafeEnable("CsGasWorldDisposePatch",   () => new CsGasWorldDisposePatch().Enable());
        }

        // wrapper so one bad patch logs + moves on instead of taking down the whole Awake
        private static void SafeEnable(string name, System.Action enable)
        {
            try { enable(); }
            catch (System.Exception e)
            {
                LogSource?.LogError($"[CSGas] patch '{name}' failed to register, skipping: {e.Message}");
            }
        }
    }
}
