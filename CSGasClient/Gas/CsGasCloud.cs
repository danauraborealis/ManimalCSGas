using System.Collections.Generic;
using Comfort.Common;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // the actual backport. 1.0 ran a voxel VolumePropagation sim + WBOIT volumetric renderer
    // for the cs gas — that whole subsystem doesnt exist in the 4.0 client. so we spread an
    // authored particle cloud volumetrically (see VolumetricGasEmitter + csgas_fx.bundle) and
    // re-create the 1.0 gameplay effects on the local player while they stand in the gas
    // (1.0 calls it "tear gas asphyxiation"):
    //   - CSGasExposure stim debuff via TryDoExternalBuff — hands tremor + stamina-rate
    //     debuff, registered server-side through WTT CustomBuffService. TryDoExternalBuff
    //     dedupes by buff name so ticking it keeps ONE persistent effect alive (no icon
    //     flicker), and it fades out naturally after leaving the cloud
    //   - thorax damage over time (1.0: 2 hp/s)
    //   - cough: OnBreath "near death" breath bank + med-use blocking with a cough anim
    //   - eyes: the TearsScreenEffect vignette/distortion overlay (1.0 TearsEffect port) —
    //     it carries the whole visual, no health-system contusion/sanding on top
    // protection is two-tier: full gas masks (GP-5/GP-7/Avon) block everything; respirators
    // only cover the airway — health/cough effects stop but the eyes still burn.
    // bots keep their vanilla smoke-vision reaction since the emission area is untouched.
    public class CsGasCloud : MonoBehaviour
    {
        private const float TickInterval = 0.5f;
        // how long after explosion we wait for the pooled GrenadeEmission to attach before
        // falling back to whatever particles live on the grenade prefab itself
        private const float EmissionWaitGrace = 1f;

        // true only for the duration of our own ApplyDamage call — the ApplyHitDebuff mute
        // patch reads this so gas DOT ticks dont play hurt grunts (or apply shot-style
        // debuffs), while real gunfire damage taken inside the cloud stays fully audible
        internal static bool MutingHitDebuff;

        // rolling window stamped by every unprotected exposure tick (any cloud) — the
        // med-block patch reads this. window is a hair over two ticks so it cant flicker
        // between ticks but drops promptly on exit / masking up.
        private static float _lastLungExposureTime = -10f;
        internal static bool IsLocalPlayerGasBlocked => Time.time - _lastLungExposureTime < TickInterval * 2.5f;

        private SmokeGrenade _smoke;
        private VolumetricGasEmitter _emitter;
        private GrenadeEmission _emission;
        // the emitter destroys its own GameObject after the cloud fades, which makes
        // _emitter compare null again while the grenade object is still alive — without
        // this flag TryCreateEmitter would spawn a second emitter and the grenade would
        // visibly start emitting a fresh cloud from scratch
        private bool _emitterCreated;
        private bool _exploded;
        private float _explodedAt;
        private float _nextTick;
        private float _exposure;
        private float _nextCough;
        private float _nextTremor;
        private bool _intoxicationDone;
        private bool _tearsApplied;
        private static bool _tremorFailLogged;
        private static System.Reflection.MethodInfo _addTremor;

        private void Start()
        {
            _smoke = GetComponent<SmokeGrenade>();
            // the in-flight fuse trail is child particle systems on the grenade instance,
            // colored M18-green. instance dies with the grenade (not pooled), so whitening
            // startColor + gradients + material colors needs no restore bookkeeping.
            WhitenGrenadeParticles();
        }

        private void WhitenGrenadeParticles()
        {
            // BSG left an active debug sphere mesh under the Area in the 1.0 prefab — the
            // rebuilt container inherited it (thrown-clone diagnostics showed it rendering)
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                var n = t.name.ToLowerInvariant();
                if (n.Contains("debugshpere") || n.Contains("debugsphere"))
                    t.gameObject.SetActive(false);
            }

            foreach (var ps in GetComponentsInChildren<ParticleSystem>(true))
            {
                var main = ps.main;
                main.startColor = new ParticleSystem.MinMaxGradient(Color.white);

                var col = ps.colorOverLifetime;
                if (col.enabled && col.color.mode == ParticleSystemGradientMode.Gradient && col.color.gradient != null)
                {
                    var src = col.color.gradient;
                    var whitened = new Gradient();
                    var colorKeys = src.colorKeys;
                    for (var i = 0; i < colorKeys.Length; i++)
                        colorKeys[i].color = Color.white;
                    whitened.SetKeys(colorKeys, src.alphaKeys);
                    col.color = new ParticleSystem.MinMaxGradient(whitened);
                }

                var renderer = ps.GetComponent<ParticleSystemRenderer>();
                if (renderer == null || renderer.sharedMaterial == null) continue;

                // best case: replace the material outright with our cloud material — kills
                // the green even if it's baked into the vanilla texture. our texture is a
                // 2x2 sheet, so force matching grid + random-frame settings.
                var cloudMat = GasCloudAssets.GetCloudMaterial();
                if (cloudMat != null)
                {
                    renderer.sharedMaterial = cloudMat;
                    renderer.renderMode = ParticleSystemRenderMode.Billboard;
                    renderer.alignment = ParticleSystemRenderSpace.View;
                    var sheet = ps.textureSheetAnimation;
                    sheet.enabled = true;
                    sheet.mode = ParticleSystemAnimationMode.Grid;
                    sheet.numTilesX = 2;
                    sheet.numTilesY = 2;
                    sheet.animation = ParticleSystemAnimationType.WholeSheet;
                    sheet.frameOverTime = new ParticleSystem.MinMaxCurve(0f);
                    sheet.startFrame = new ParticleSystem.MinMaxCurve(0f, 4f);
                    continue;
                }

                // fallback (no bundle): whiten every color-typed shader property on a
                // per-renderer instance material (dies with the grenade)
                var mat = renderer.material;
                var shader = mat.shader;
                if (shader == null) continue;
                var count = shader.GetPropertyCount();
                for (var i = 0; i < count; i++)
                {
                    if (shader.GetPropertyType(i) != UnityEngine.Rendering.ShaderPropertyType.Color) continue;
                    var name = shader.GetPropertyName(i);
                    var current = mat.GetColor(name);
                    mat.SetColor(name, new Color(1f, 1f, 1f, current.a));
                }
            }
        }

        // from the AttachTo postfix — the pooled smoke plume. its particles get killed
        // per-tick (reskinning was tried and reverted: restoring the pooled material at
        // emission end recolored still-living particles green mid-air). also the
        // volumetric particle template fallback for installs without csgas_fx.bundle.
        public void AttachEmission(GrenadeEmission emission)
        {
            _emission = emission;
        }

        // from the OnExplosion postfix — emission is starting. emitter creation is deferred
        // to Update because the pooled GrenadeEmission may attach a moment after this.
        public void NotifyExploded()
        {
            if (_exploded) return;
            _exploded = true;
            _explodedAt = Time.time;
            AttachNozzleJet();
        }

        // the authored spark + vertical jet from csgas_fx (the vanilla plume equivalent we
        // suppress). parented to the canister at the smokeport so it rides along if the
        // grenade still rolls; self-destructs when emission ends.
        private void AttachNozzleJet()
        {
            var jetPrefab = GasCloudAssets.GetJetPrefab();
            if (jetPrefab == null || _smoke == null) return;

            var mount = FindSmokeport(transform) ?? transform;
            var jet = Instantiate(jetPrefab, mount.position, mount.rotation, mount);

            // ripped-from-BSG systems (Smoke flow / Trail) still declare WBOIT's custom
            // vertex streams — Standard Unlit reads them as scrambled UV/color and renders
            // giant marbled squares. force the default stream layout on every renderer.
            var defaultStreams = new List<ParticleSystemVertexStream>
            {
                ParticleSystemVertexStream.Position,
                ParticleSystemVertexStream.Normal,
                ParticleSystemVertexStream.Color,
                ParticleSystemVertexStream.UV
            };
            foreach (var renderer in jet.GetComponentsInChildren<ParticleSystemRenderer>(true))
                renderer.SetActiveVertexStreams(defaultStreams);

            Destroy(jet, Mathf.Max(1f, _smoke.EmitTime));
        }

        private static Transform FindSmokeport(Transform root)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("smokeport", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return t;
            }
            return null;
        }

        private void Update()
        {
            if (Time.time < _nextTick) return;
            _nextTick = Time.time + TickInterval;
            if (_smoke == null) return;

            TryCreateEmitter();
            SuppressVanillaPlume();

            // nothing hurts anyone before the fuse pops — the rebuilt prefab ships its
            // Area collider active, so without this gate the fallback sphere is "live"
            // from the moment of the throw (tears at your own hand)
            if (!_exploded)
            {
                _exposure = 0f;
                return;
            }

            var player = Singleton<GameWorld>.Instance?.MainPlayer;
            if (player == null || player.HealthController == null || !player.HealthController.IsAlive)
            {
                _exposure = 0f;
                return;
            }

            var head = player.PlayerBones != null ? player.PlayerBones.Head.position : player.Position;

            bool inside;
            if (_emitter != null)
            {
                // volumetric mode: membership in the raycast point cloud. no linecast needed —
                // the caster never produced points behind geometry
                inside = _emitter.IsInside(head, Plugin.GasPointRadiusM.Value);
            }
            else
            {
                // fallback (volumetric disabled or emitter creation failed): the M18's own
                // emission-area sphere + a wall linecast. area object is only active while
                // the size curve says the cloud exists
                var area = _smoke._smokeGrenadeSettings?._emissionArea;
                if (area == null || !area.gameObject.activeInHierarchy)
                {
                    _exposure = 0f;
                    return;
                }
                var center = area.transform.position;
                var radius = area.radius * MaxComponent(area.transform.lossyScale);
                inside = (head - center).sqrMagnitude <= radius * radius;
                if (inside && Plugin.GasRequiresLineOfSight.Value
                    && Physics.Linecast(center, head, LayersMaskController.GrenadeObstaclesColliderMask))
                    inside = false;
            }

            var protection = GetProtection(player);
            if (inside && protection == GasProtection.Full)
                inside = false;

            if (!inside)
            {
                _exposure = 0f;
                ReleaseTears();
                return;
            }

            // eye irritation vignette (1.0 TearsEffect port). respirators dont seal the
            // eyes, so only a full gas mask escapes this — and Full already zeroed `inside`
            if (Plugin.GasTearsEnabled.Value)
            {
                TearsScreenEffect.SetTarget(Plugin.GasTearsStrength.Value);
                _tearsApplied = true;
            }

            ApplyExposureTick(player, protection == GasProtection.Respirator);
        }

        // only the cloud that turned the tears on releases them — avoids one cloud's exit
        // stomping another cloud's active overlay
        private void ReleaseTears()
        {
            if (!_tearsApplied) return;
            _tearsApplied = false;
            TearsScreenEffect.SetTarget(0f);
        }

        private void TryCreateEmitter()
        {
            if (!_exploded || _emitterCreated || !Plugin.GasVolumetricCloud.Value) return;
            // Fika headless host: no camera, nobody rendering — skip the visual cloud (the
            // fallback sphere still exists for logic, and each real client grows its own)
            if (Camera.main == null) return;
            // the bundle prefab is preferred inside Create; the emission/grenade objects are
            // only template fallbacks
            if (_emission != null)
            {
                _emitter = VolumetricGasEmitter.Create(_smoke, _emission.gameObject);
                _emitterCreated = true;
            }
            else if (Time.time >= _explodedAt + EmissionWaitGrace)
            {
                _emitter = VolumetricGasEmitter.Create(_smoke, gameObject);
                _emitterCreated = true;
            }
        }

        // the pooled M18 plume is vanilla green and clashes under the authored white cloud —
        // AND it attaches at throw, so its ramp-up is the green in-flight trail. keep its
        // systems stopped from the moment it attaches (not just once our cloud is up).
        // per-tick because the game calls StartEmission after our attach hook (and can
        // re-Play systems later). Stop() is transient state, not a module edit, so the
        // pooled object replays normally when a vanilla smoke reuses it. audio (hiss) is
        // untouched on purpose. when the volumetric cloud is config-disabled the plume is
        // the only visual, so it stays (green and all).
        private void SuppressVanillaPlume()
        {
            if (_emission == null || !Plugin.GasVolumetricCloud.Value) return;
            foreach (var ps in _emission.GetComponentsInChildren<ParticleSystem>(true))
            {
                if (ps.isPlaying || ps.isEmitting)
                    ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            }
        }

        // lungsProtected = respirator on: airway is covered so the buff/cough/damage side is
        // blocked, but respirators dont seal the eyes — sanding + contusion still apply
        private void ApplyExposureTick(Player player, bool lungsProtected)
        {
            _exposure += TickInterval;
            var hc = player.ActiveHealthController;
            if (hc == null) return;

            if (!lungsProtected)
            {
                _lastLungExposureTime = Time.time; // arms the med-use block

                // one persistent stim debuff (tremor + stamina rate). dedupes by name, so
                // this just keeps it alive; it expires on its own after leaving the cloud
                hc.TryDoExternalBuff(CSGasConstants.GasBuffName);

                // the buff's own HandsTremor never visibly fires: globals Tremor.DefaultDelay
                // is 15s while the buff lives 8s, so the effect dies mid-warmup. add the
                // Tremor effect directly with zero delay instead (protected nested type →
                // one cached reflection call), overlapping windows like the old contusion.
                if (Time.time >= _nextTremor)
                {
                    _nextTremor = Time.time + 5f;
                    ApplyTremor(hc);
                }

                // 1.0: "damage to the thorax (2 HP/second)" — muted so the slow asphyxiation
                // drain doesnt spam hurt grunts every tick
                var dps = Plugin.GasThoraxDamagePerSec.Value;
                if (dps > 0f)
                {
                    var damage = dps * TickInterval;
                    MutingHitDebuff = true;
                    try
                    {
                        hc.ApplyDamage(EBodyPart.Chest, damage, new EFT.Ballistics.DamageInfo
                        {
                            DamageType = EDamageType.Environment,
                            Damage = damage
                        });
                    }
                    finally { MutingHitDebuff = false; }
                }

                if (Time.time >= _nextCough)
                {
                    _nextCough = Time.time + Plugin.GasCoughIntervalSec.Value;
                    // OnBreath + a forced Dying mask selects the "breath near death" bank —
                    // the heaviest cough/wheeze loop. (BadlyInjured picked the milder hurt
                    // breathing; Toxic and a plain Say(OnBreath) are silent — no bank.)
                    player.Speaker?.Play(EPhraseTrigger.OnBreath, ETagStatus.Dying, true, null);
                }
            }

            if (!lungsProtected && !_intoxicationDone && Plugin.GasLongExposureIntoxication.Value
                && _exposure >= Plugin.GasLongExposureSec.Value)
            {
                _intoxicationDone = true;
                hc.DoIntoxication();
            }
        }

        private static void ApplyTremor(EFT.HealthSystem.ActiveHealthController hc)
        {
            try
            {
                if (_addTremor == null)
                {
                    var tremorType = HarmonyLib.AccessTools.TypeByName("EFT.HealthSystem.ActiveHealthController+Tremor");
                    var generic = HarmonyLib.AccessTools.FirstMethod(
                        typeof(EFT.HealthSystem.ActiveHealthController),
                        m => m.Name == "AddEffect" && m.IsGenericMethodDefinition && m.GetParameters().Length == 6);
                    _addTremor = generic.MakeGenericMethod(tremorType);
                }
                // (bodyPart, delay, duration, residueTime, strength, callback)
                _addTremor.Invoke(hc, new object[] { EBodyPart.Head, 0f, 6.5f, null, null, null });
            }
            catch (System.Exception e)
            {
                if (!_tremorFailLogged)
                {
                    _tremorFailLogged = true;
                    Plugin.LogSource?.LogWarning("[CSGas] direct tremor failed: " + e.Message);
                }
            }
        }

        internal enum GasProtection { None, Respirator, Full }

        private static GasProtection GetProtection(Player player)
        {
            try
            {
                var faceCover = player.Equipment?.GetSlot(EquipmentSlot.FaceCover)?.ContainedItem;
                if (faceCover == null) return GasProtection.None;
                var tpl = faceCover.TemplateId.ToString();
                if (CSGasConstants.FullBlockTpls.Contains(tpl)) return GasProtection.Full;
                if (CSGasConstants.RespiratorTpls.Contains(tpl)) return GasProtection.Respirator;
                return GasProtection.None;
            }
            catch { return GasProtection.None; } // inventory mid-transaction — treat as unmasked for this tick
        }

        private static float MaxComponent(Vector3 v) => Mathf.Max(v.x, Mathf.Max(v.y, v.z));

        private void OnDestroy()
        {
            ReleaseTears(); // grenade object died while we were inside — let the overlay fade
        }
    }
}
