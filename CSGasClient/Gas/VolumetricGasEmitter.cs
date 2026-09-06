using System.Collections.Generic;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // the volume propagation half of the backport, borrowed from the RShG-2's volumetric
    // explosion. VolumetricBounds is BSG's occlusion-aware spherical caster — uniform sphere
    // directions, one batched RaycastCommand pass, then three concentric shells of points
    // clipped at whatever geometry each ray hit. thats exactly a gas propagation query,
    // just computed instantly instead of simulated voxel-by-voxel like 1.0 did.
    //
    // the rocket pairs it with VolumetricExplosion, whose particle material reads a _ControlPoints
    // GraphicsBuffer — that needs the rocket's shader, so we skip it and place particles
    // ourselves: clone the M18's longest-lived smoke system (real EFT smoke material) and
    // Emit(EmitParams) at cloud points, lerped grenade→point by a grow factor so the gas
    // visibly spreads outward instead of appearing everywhere at once.
    //
    // lives on its own GameObject so the cloud outlives the grenade object; CsGasCloud
    // queries IsInside for the gameplay side — a point cloud membership test that respects
    // walls for free (a blocked ray never produced points behind the wall).
    public class VolumetricGasEmitter : MonoBehaviour
    {
        private const float RecastInterval = 5f;
        private const float PointJitter = 0.45f;

        private readonly List<Vector3> _points = new List<Vector3>();
        // cumulative sampling weights per point (~distance², so blocked directions — whose
        // points cluster at the obstruction — get proportionally few particles and open
        // directions absorb the rest, approximating volume conservation)
        private float[] _cdf = new float[0];
        private float _cdfTotal;
        private VolumetricBounds _caster;
        private ParticleSystem _ps;
        private Transform _grenade;   // may die before the cloud does — cache origin every frame it exists
        private Vector3 _origin;
        private float _growStart;
        private float _emitUntil;
        private float _nextEmit;
        private float _nextRecast;
        private float _particleLifetime;
        private bool _finished;

        // template priority: the authored csgas_fx.bundle prefab (1.0 texture, editor-tuned),
        // then the pooled GrenadeEmission (the real smoke plume), then the grenade prefab
        public static VolumetricGasEmitter Create(SmokeGrenade smoke, GameObject templateSource)
        {
            var fromBundle = false;
            ParticleSystem template = null;

            var bundlePrefab = GasCloudAssets.GetCloudPrefab();
            if (bundlePrefab != null)
            {
                template = bundlePrefab.GetComponentInChildren<ParticleSystem>(true);
                fromBundle = template != null;
            }
            if (template == null)
                template = PickSmokeTemplate(templateSource != null ? templateSource : smoke.gameObject);
            if (template == null)
            {
                Plugin.LogSource?.LogWarning("[CSGas] no particle system found on grenade prefab — volumetric cloud disabled for this grenade");
                return null;
            }

            var go = new GameObject("CSGasVolumetricCloud");
            var emitter = go.AddComponent<VolumetricGasEmitter>();
            emitter._grenade = smoke.transform;
            emitter._origin = smoke.transform.position + Vector3.up * 0.3f;
            emitter._emitUntil = Time.time + smoke.EmitTime;
            emitter._growStart = Time.time;
            emitter.BuildParticleSystem(template, fromBundle);
            emitter.BuildCaster();
            return emitter;
        }

        // the M18 prefab carries several systems (smoke, sparks, flash). the actual smoke
        // is the long-lived one — pick by startLifetime instead of guessing names.
        private static ParticleSystem PickSmokeTemplate(GameObject root)
        {
            ParticleSystem best = null;
            var bestLifetime = 0f;
            foreach (var ps in root.GetComponentsInChildren<ParticleSystem>(true))
            {
                var lifetime = ps.main.startLifetime.constantMax;
                if (lifetime > bestLifetime)
                {
                    bestLifetime = lifetime;
                    best = ps;
                }
            }
            return best;
        }

        private void BuildParticleSystem(ParticleSystem template, bool fromBundle)
        {
            _ps = Instantiate(template, transform);
            _ps.transform.localPosition = Vector3.zero;
            _ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = _ps.main;
            // world space so EmitParams positions are world positions and drift ignores this GO
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = Mathf.Max(main.maxParticles, 3000);
            _particleLifetime = main.startLifetime.constantMax;

            // M18-clone fallback only: own material copies so we dont share the pooled
            // emission's material lifecycle. no tinting — the cloud stays as authored.
            if (!fromBundle)
            {
                foreach (var renderer in _ps.GetComponentsInChildren<ParticleSystemRenderer>(true))
                {
                    if (renderer.sharedMaterial == null) continue;
                    renderer.sharedMaterial = new Material(renderer.sharedMaterial);
                }
            }

            // we place every particle ourselves — kill the prefab's own emission
            var emission = _ps.emission;
            emission.rateOverTime = 0f;
            emission.rateOverDistance = 0f;
            emission.SetBursts(new ParticleSystem.Burst[0]);

            _ps.Play();
        }

        private void BuildCaster()
        {
            // radius 1 + subdivisions n → ~n unit directions; maxParticles caps output at
            // the 3 shells the caster emits per direction
            var rays = Plugin.GasRayCount.Value;
            _caster = new VolumetricBounds(1f, rays, rays * 3, Plugin.GasMaxRadius.Value);
            Recast();
        }

        private void Recast()
        {
            _nextRecast = Time.time + RecastInterval;
            _caster.RayCast(_points, _origin);

            // rebuild the weighted-sampling table: a grenade against a wall clumps half its
            // points at the wall face; distance²-weighting sends those particles into the
            // open side instead (with a small floor so the canister itself still smokes)
            if (_cdf.Length < _points.Count) _cdf = new float[_points.Count];
            _cdfTotal = 0f;
            for (var i = 0; i < _points.Count; i++)
            {
                var d = (_points[i] - _origin).magnitude;
                _cdfTotal += d * d + 0.25f;
                _cdf[i] = _cdfTotal;
            }
        }

        // weighted random point — binary search over the cumulative table
        private Vector3 SamplePoint()
        {
            var r = Random.value * _cdfTotal;
            int lo = 0, hi = _points.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi) >> 1;
                if (_cdf[mid] < r) lo = mid + 1; else hi = mid;
            }
            return _points[lo];
        }

        private float GrowFactor =>
            Plugin.GasGrowTimeSec.Value <= 0f
                ? 1f
                : Mathf.Clamp01((Time.time - _growStart) / Plugin.GasGrowTimeSec.Value);

        private void Update()
        {
            if (_grenade != null)
                _origin = _grenade.position + Vector3.up * 0.3f;

            if (Time.time >= _emitUntil)
            {
                if (!_finished)
                {
                    _finished = true;
                    // cloud is done emitting — let the last particles die, then clean up
                    Destroy(gameObject, _particleLifetime + 2f);
                }
                return;
            }

            // grenade got kicked/rolled somewhere new, or a door state changed — re-fill
            if (Time.time >= _nextRecast)
                Recast();

            if (Time.time < _nextEmit || _points.Count == 0) return;
            _nextEmit = Time.time + Plugin.GasEmitIntervalSec.Value;

            // white with config alpha — the opacity slider scales the whole cloud while the
            // prefab's color-over-lifetime envelope keeps the fade shape
            var grow = GrowFactor;
            var emitParams = new ParticleSystem.EmitParams
            {
                startColor = new Color(1f, 1f, 1f, Plugin.GasCloudOpacity.Value)
            };
            for (var i = 0; i < Plugin.GasParticlesPerEmit.Value; i++)
            {
                var point = SamplePoint();
                emitParams.position = Vector3.Lerp(_origin, point, grow) + Random.insideUnitSphere * PointJitter;
                emitParams.velocity = Random.insideUnitSphere * 0.08f;
                _ps.Emit(emitParams, 1);
            }
        }

        // gameplay membership test: near any (growth-scaled) cloud point. walls come free —
        // rays that hit geometry never produced points on the far side.
        public bool IsInside(Vector3 position, float pointRadius)
        {
            if (_finished && Time.time >= _emitUntil + _particleLifetime) return false;
            var grow = GrowFactor;
            var sqr = pointRadius * pointRadius;
            for (var i = 0; i < _points.Count; i++)
            {
                var p = Vector3.Lerp(_origin, _points[i], grow);
                if ((position - p).sqrMagnitude <= sqr) return true;
            }
            return false;
        }

        private void OnDestroy()
        {
            // Persistent NativeArray inside — must be disposed or Unity leaks + complains
            _caster?.Dispose();
            _caster = null;
        }
    }
}
