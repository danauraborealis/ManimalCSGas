using System;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // runtime surgery on the WTT-ContentBackport m8230 container so nobody has to do it in
    // the editor. the raw 1.0 prefab carries TearGasGrenadeSettings — a script that doesnt
    // exist in the 4.0 client — so GrenadePrefab.GrenadeItself deserializes null and the
    // throw pipeline NREs in FindThrowPosition (grenade eaten, nothing thrown). we add a
    // real SmokeGrenadeSettings to the world child, wire the prefab's own Area collider,
    // author the M18-style size curve, and point GrenadeItself at it — the factory then
    // routes through AddSmokeGrenade and the whole cs gas pipeline works again.
    internal static class M8230PrefabRepair
    {
        public static void TryRepair(GrenadePrefab gp)
        {
            if (gp == null || gp.GrenadeItself != null) return;
            if (gp.name.IndexOf("m8230", StringComparison.OrdinalIgnoreCase) < 0) return;

            try
            {
                // world root: the same string GrenadePrefab itself resolves (element 0
                // "defines grenade initial position"), with a name-scan fallback
                Transform world = null;
                if (gp.ThrowingParts != null && gp.ThrowingParts.Length > 0 && !string.IsNullOrEmpty(gp.ThrowingParts[0]))
                    world = TransformHelperClass.FindTransformRecursive(gp.transform, gp.ThrowingParts[0], false);
                if (world == null)
                    world = FindByName(gp.transform, n => n.Contains("world") && !n.Contains("tripwire"));
                if (world == null)
                {
                    Plugin.LogSource?.LogWarning("[CSGas] m8230 repair: no world child found — cannot fix throw");
                    return;
                }

                var settings = world.GetComponent<SmokeGrenadeSettings>();
                if (settings == null) settings = world.gameObject.AddComponent<SmokeGrenadeSettings>();

                // Grenade.Awake caches GetComponent<Collider>/<Rigidbody> on the world ROOT
                // (the factory adds SmokeGrenade right there). the m8230 world root ships
                // neither → Init NREd at IgnoreCollision. canister-sized capsule + item-
                // weight rigidbody restore what the M18 root carries natively.
                if (world.GetComponent<Collider>() == null)
                {
                    var capsule = world.gameObject.AddComponent<CapsuleCollider>();
                    capsule.direction = 1; // Y
                    capsule.radius = 0.035f;
                    capsule.height = 0.14f;
                    capsule.center = new Vector3(0f, 0.07f, 0f);
                }
                if (world.GetComponent<Rigidbody>() == null)
                {
                    var rb = world.gameObject.AddComponent<Rigidbody>();
                    rb.mass = 0.54f;
                    rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                }

                // emission area: the 1.0 prefab ships an "Area" sphere; synthesize one if
                // a future bundle revision drops it
                SphereCollider area = null;
                var areaT = FindByName(world, n => n == "area") ?? FindByName(gp.transform, n => n == "area");
                if (areaT != null) area = areaT.GetComponent<SphereCollider>();
                if (area == null)
                {
                    var go = new GameObject("Area");
                    go.transform.SetParent(world, false);
                    area = go.AddComponent<SphereCollider>();
                    area.isTrigger = true;
                    area.radius = 4f;
                    go.SetActive(false);
                }

                settings._emissionArea = area;
                settings._initialRadius = area.radius;
                settings._pivot = area.transform.localPosition;
                settings._radiusMultiplier = 1f;
                settings._areaStartPosNorm = 0.5f;
                // M18-style: quick ramp, hold for the emit window, short tail.
                // SmokeGrenade.LifeTime = EmitTime * lastKey.time
                settings._sizeOverTime = new AnimationCurve(
                    new Keyframe(0f, 0f), new Keyframe(0.08f, 1f), new Keyframe(1f, 1f), new Keyframe(1.15f, 0f));
                settings._torque = Vector3.zero;
                settings._torqueDelta = 0.3f;
                settings.Offset = Vector3.zero;
                settings.CollisionSound = GrenadeSettings.CollisionSounds.smokeM18;
                settings.Skoba = FindByName(world, n => n.Contains("skoba"));

                // the world child's canister MESH has vertices baked ~1m off origin, but the
                // bundle also ships a properly CENTERED duplicate (the hands rig uses it).
                // swap the world mesh to the centered copy and zero the node — deterministic,
                // no compensation math. fallback: bounds-based compensation incl. scale.
                var meshChild = world.Find("weapon_grenade_m8230");
                if (meshChild != null)
                {
                    var worldMeshFilter = meshChild.GetComponent<MeshFilter>();
                    Mesh centered = null;
                    foreach (var mf in gp.GetComponentsInChildren<MeshFilter>(true))
                    {
                        var m = mf.sharedMesh;
                        if (m == null || !m.name.StartsWith("weapon_grenade_m8230")) continue;
                        if (m.name.Contains("pin") || m.name.Contains("ring") || m.name.Contains("skoba")) continue;
                        if (m.bounds.center.magnitude < 0.05f) { centered = m; break; }
                    }
                    if (worldMeshFilter != null && centered != null)
                    {
                        worldMeshFilter.sharedMesh = centered;
                        meshChild.localPosition = Vector3.zero;
                        Plugin.LogSource?.LogInfo($"[CSGas] m8230 world mesh swapped to centered copy '{centered.name}'");
                    }
                    else if (worldMeshFilter != null && worldMeshFilter.sharedMesh != null)
                    {
                        var c = Vector3.Scale(worldMeshFilter.sharedMesh.bounds.center, meshChild.localScale);
                        meshChild.localPosition = -(meshChild.localRotation * c);
                        Plugin.LogSource?.LogInfo($"[CSGas] m8230 mesh recentered (no centered copy found) to {meshChild.localPosition}");
                    }
                }

                // BSG left an active debug sphere mesh under the Area — kill it
                var debugSphere = FindByName(world, n => n.Contains("debugshpere") || n.Contains("debugsphere"));
                if (debugSphere != null) debugSphere.gameObject.SetActive(false);

                // EmmisionEffect == the settings GameObject's NAME, and the local-player
                // throw path does NOT null-check a missing pool key — rename to the M18's
                // key so a valid pooled plume attaches (we suppress it anyway). keep
                // ThrowingParts[0] in sync: pool-return and the observed path resolve the
                // world part by that string.
                world.name = "weapon_m18_world";
                if (gp.ThrowingParts != null && gp.ThrowingParts.Length > 0)
                    gp.ThrowingParts[0] = "weapon_m18_world";

                gp.GrenadeItself = settings;
                Plugin.LogSource?.LogInfo("[CSGas] m8230 container repaired at runtime (SmokeGrenadeSettings wired)");
            }
            catch (Exception e)
            {
                Plugin.LogSource?.LogError("[CSGas] m8230 prefab repair failed: " + e);
            }
        }

        private static Transform FindByName(Transform root, Func<string, bool> match)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (match(t.name.ToLowerInvariant())) return t;
            }
            return null;
        }
    }
}
