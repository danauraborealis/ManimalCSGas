using System.Reflection;
using Comfort.Common;
using EFT;
using HarmonyLib;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // attach point for the whole gas backport. SmokeGrenade.Init receives the ThrowWeap
    // item, so this is the earliest moment we can tell "this is OUR grenade" — the clone
    // spawns the M18 prefab, which carries the SmokeGrenade component. everything else
    // (tint, exposure effects) lives in the CsGasCloud component.
    public class CsGasCloudPatch : ModulePatch
    {
        // Grenade declares Init overloads up the hierarchy — name-only lookup is ambiguous
        // (it cost us a silent no-op: SafeEnable logged the failure and nothing ran)
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SmokeGrenade), nameof(SmokeGrenade.Init), new[]
            {
                typeof(GrenadeSettings), typeof(string), typeof(ThrowWeapItemClass),
                typeof(float), typeof(ISharedBallisticsCalculator), typeof(bool)
            });

        [PatchPostfix]
        public static void Postfix(SmokeGrenade __instance, ThrowWeapItemClass throwWeap)
        {
            if (throwWeap == null || __instance == null) return;
            if (throwWeap.TemplateId.ToString() != CSGasConstants.CsGasTpl) return;
            if (__instance.GetComponent<CsGasCloud>() != null) return;
            __instance.gameObject.AddComponent<CsGasCloud>();
        }
    }

    // OnExplosion is when emission actually starts — that's the moment to spin up the
    // volumetric cloud (before this the grenade is still flying)
    public class CsGasExplosionPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(SmokeGrenade), nameof(SmokeGrenade.OnExplosion));

        [PatchPostfix]
        public static void Postfix(SmokeGrenade __instance)
        {
            if (__instance == null) return;
            var cloud = __instance.GetComponent<CsGasCloud>();
            if (cloud != null) cloud.NotifyExploded();
        }
    }

    // 1.0: "preventing the use of medical items" — while the local player stands
    // unprotected in the gas, every med use (quickslot and inventory both funnel into this
    // Proceed overload) is cancelled and answered with a cough instead. respirator or gas
    // mask lifts the block (CsGasCloud only arms it on unprotected lung ticks). SPT's
    // LocalPlayer doesnt override this overload, so the base Player patch catches it.
    public class CsGasMedBlockPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.Proceed), new[]
            {
                typeof(MedsItemClass), typeof(GStruct382<EBodyPart>),
                typeof(Callback<GInterface203>), typeof(int), typeof(bool)
            });

        [PatchPrefix]
        public static bool Prefix(Player __instance, Callback<GInterface203> callback)
        {
            if (__instance == null || !__instance.IsYourPlayer) return true;
            if (!CsGasCloud.IsLocalPlayerGasBlocked) return true;

            // Callback<T> has no Fail extension — a failed Result<T> is the same thing
            callback?.Invoke(new Result<GInterface203>(default, "Coughing in CS gas"));
            // forced Dying mask → "breath near death" bank (see CsGasCloud cough tick)
            __instance.Speaker?.Play(EPhraseTrigger.OnBreath, ETagStatus.Dying, true, null);
            CoughAnimPlayer.TryPlay(__instance);
            return false;
        }
    }

    // animators die with the raid — drop the cached override controllers
    public class CsGasWorldDisposePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(EFT.GameWorld), nameof(EFT.GameWorld.Dispose));

        [PatchPrefix]
        public static void Prefix()
        {
            CoughAnimPlayer.Controllers.Clear();
        }
    }

    // gas DOT ticks route through ActiveHealthController.ApplyDamage and land in
    // Player.ApplyHitDebuff → OnBeingHurt grunt every tick. the flag-scoped mute missed in
    // testing (the event evidently fires deferred, after our finally reset), so match the
    // damage signature instead: small Environment-type damage while gassed IS our tick —
    // bullets/frags/falls use other damage types and stay fully audible. no subclass
    // overrides this method.
    public class CsGasHitDebuffMutePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), nameof(Player.ApplyHitDebuff));

        [PatchPrefix]
        public static bool Prefix(float damage, EDamageType damageType)
        {
            if (CsGasCloud.MutingHitDebuff) return false;
            if (damageType == EDamageType.Environment && damage <= 3f && CsGasCloud.IsLocalPlayerGasBlocked)
                return false;
            return true;
        }
    }

    // ...and the grunt survived that mute because it actually comes from method_74, the
    // ApplyDamageEvent subscriber, which Says OnBeingHurt for any non-self-inflicted type.
    // same signature gate. obfuscated name — re-check on SPT client updates.
    public class CsGasHurtVoiceMutePatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Player), "method_74", new[]
            {
                typeof(EBodyPart), typeof(float), typeof(DamageInfoStruct)
            });

        [PatchPrefix]
        public static bool Prefix(float damage, DamageInfoStruct damageInfo)
        {
            if (damageInfo.DamageType == EDamageType.Environment && damage <= 3f
                && CsGasCloud.IsLocalPlayerGasBlocked)
                return false;
            return true;
        }
    }

    // the smoke plume is NOT on the grenade prefab — it's a pooled GrenadeEmission fetched
    // from the Effects singleton (keyed by GrenadePrefab.GrenadeItself.EmmisionEffect) and
    // glued to the grenade here. both the local-player and observed paths funnel through
    // AttachTo, so this is the one choke point where we can grab the real smoke systems
    // for tinting + as the volumetric particle template.
    public class CsGasEmissionAttachPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(GrenadeEmission), nameof(GrenadeEmission.AttachTo));

        [PatchPostfix]
        public static void Postfix(GrenadeEmission __instance, Transform t)
        {
            if (__instance == null || t == null) return;
            // the attach target isnt guaranteed to be the grenade root — search upward
            var cloud = t.GetComponentInParent<CsGasCloud>();
            if (cloud != null) cloud.AttachEmission(__instance);
        }
    }
}
