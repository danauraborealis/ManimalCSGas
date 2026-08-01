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

    // a bot with broken AI memory NREs in BotMemoryClass.Spotted whenever it HEARS
    // anything — and because Grenade.InvokeBlowUpEvent is { method_6(); OnExplosion(); },
    // that crash (raised inside method_6's GrenadeExplosion bot event) aborts the chain
    // BEFORE OnExplosion → the gas never starts. root cause is a bot mod's half-initialized
    // bot (not ours); this finalizer swallows the sensor crash so one broken bot cant kill
    // every grenade on the map (or spam NREs on every gunshot). logs once per raid-ish.
    public class CsGasBotHearingGuardPatch : ModulePatch
    {
        private static float _lastLogTime = -600f;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(BotMemoryClass), nameof(BotMemoryClass.Spotted));

        [PatchFinalizer]
        public static System.Exception Finalizer(System.Exception __exception)
        {
            if (__exception != null && Time.time - _lastLogTime > 300f)
            {
                _lastLogTime = Time.time;
                Plugin.LogSource?.LogWarning(
                    "[CSGas] suppressed a BotMemoryClass.Spotted exception (broken bot AI state — would abort grenade explosions): "
                    + __exception.Message);
            }
            return null;
        }
    }

    // Fika compatibility: FikaPlayer OVERRIDES the meds Proceed, so the base-Player patch
    // above never fires for the local player in a Fika raid — patch the override too.
    // registered only when Fika is loaded (soft dependency guarantees load order).
    // everything else needs no Fika work: FikaHostSmokeGrenade : SmokeGrenade and
    // FikaClientSmokeGrenade : ObservedSmokeGrenade both inherit Init/OnExplosion, so the
    // cloud+effects patches fire on every peer; all effects are IsYourPlayer/MainPlayer
    // gated so each client only gasses themselves.
    public class CsGasMedBlockFikaPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer"), "Proceed", new[]
            {
                typeof(MedsItemClass), typeof(GStruct382<EBodyPart>),
                typeof(Callback<GInterface203>), typeof(int), typeof(bool)
            });

        [PatchPrefix]
        public static bool Prefix(Player __instance, Callback<GInterface203> callback)
            => CsGasMedBlockPatch.Prefix(__instance, callback);
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

    // the plume pool key is the settings GameObject's NAME, and the local-player throw
    // path calls .AttachTo on the result without a null check. the rebuilt container's
    // world object is named weapon_m8230_world_manimal (no pool entry) — fall back to the
    // M18's pooled emission for m8230-ish keys so the throw survives. we suppress the
    // plume for our grenade anyway, so which pool it comes from is cosmetic.
    public class CsGasEmissionKeyPatch : ModulePatch
    {
        private static bool _logged;

        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Systems.Effects.Effects), nameof(Systems.Effects.Effects.GetEmissionEffect));

        [PatchPostfix]
        public static void Postfix(Systems.Effects.Effects __instance, string key, ref GrenadeEmission __result)
        {
            if (__result != null || key == null) return;
            if (key.IndexOf("m8230", System.StringComparison.OrdinalIgnoreCase) < 0) return;
            __result = __instance.GetEmissionEffect("weapon_m18_world");
            if (_logged) return;
            _logged = true;
            Plugin.LogSource?.LogInfo($"[CSGas] emission key '{key}' has no pool entry — using weapon_m18_world fallback");
        }
    }

    // universal in-hands hook (GoldenPick's pattern): WeaponPrefab.OnEnable fires whenever
    // any weapon GameObject activates — the moment the m8230 container appears in hands we
    // repair its missing settings, before any throw can NRE
    public class CsGasPrefabRepairPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(WeaponPrefab), nameof(WeaponPrefab.OnEnable));

        [PatchPostfix]
        public static void Postfix(WeaponPrefab __instance)
        {
            if (__instance is GrenadePrefab gp) M8230PrefabRepair.TryRepair(gp);
        }
    }

    // the thrown grenade is an Instantiate of the container's world child — which sits
    // INACTIVE at clone time on the repaired m8230, so AddComponent<SmokeGrenade>'s Awake
    // (which caches GetComponent Collider/Rigidbody — both BSG-authored on that root) is
    // deferred and Init NREs on the uncached collider. activate the clone and backfill
    // what Awake would have cached. gated to our tpl; vanilla grenades untouched.
    public class CsGasGrenadeInitFixPatch : ModulePatch
    {
        protected override MethodBase GetTargetMethod() =>
            AccessTools.Method(typeof(Grenade), nameof(Grenade.Init), new[]
            {
                typeof(GrenadeSettings), typeof(string), typeof(ThrowWeapItemClass),
                typeof(float), typeof(ISharedBallisticsCalculator), typeof(bool)
            });

        private static readonly FieldInfo ColliderField = AccessTools.Field(typeof(Grenade), "collider_0");
        private static readonly System.Reflection.PropertyInfo RigidbodyProp = AccessTools.Property(typeof(Grenade), "Rigidbody");

        [PatchPrefix]
        public static void Prefix(Grenade __instance, ThrowWeapItemClass throwWeap)
        {
            if (__instance == null || throwWeap == null) return;
            if (throwWeap.TemplateId.ToString() != CSGasConstants.CsGasTpl) return;

            if (!__instance.gameObject.activeSelf)
                __instance.gameObject.SetActive(true); // also runs the deferred Awake

            if (ColliderField != null && ColliderField.GetValue(__instance) == null)
                ColliderField.SetValue(__instance, __instance.GetComponent<Collider>());
            if (RigidbodyProp != null && (Rigidbody)RigidbodyProp.GetValue(__instance) == null)
                RigidbodyProp.SetValue(__instance, __instance.GetComponent<Rigidbody>());
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
