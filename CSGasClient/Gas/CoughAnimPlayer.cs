using System.Collections;
using System.Collections.Generic;
using System.Linq;
using EFT;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // plays the first-person cough clip on the weapon/arms animator — the WatchAnims
    // mechanism verbatim: nothing reaches the weapon animator's hand_nv_on state in 4.0
    // (FirearmsAnimator.SetInteract is a no-op), so we override that state's clip on an
    // AnimatorOverrideController and Play it directly on its layer, blending the layer
    // weight in/out so the pose snap from Play() is never visible.
    internal static class CoughAnimPlayer
    {
        private const string StateAndClipName = "hand_nv_on";
        private const string OurControllerName = "Manimal.CSGas cough override";

        // one override controller per animator, created lazily; cleared on GameWorld.Dispose
        internal static readonly Dictionary<object, AnimatorOverrideController> Controllers =
            new Dictionary<object, AnimatorOverrideController>();

        private static Coroutine _playRoutine;

        public static void TryPlay(Player player)
        {
            var clip = GasCloudAssets.GetCoughClip();
            if (clip == null || player == null) return;

            // FirearmsAnimator is null for non-weapon hands controllers — no arms rig to
            // cough with, the voice line alone carries it
            var firearmsAnimator = player.HandsController != null ? player.HandsController.FirearmsAnimator : null;
            var animator = firearmsAnimator != null ? firearmsAnimator.Animator : null;
            if (animator == null) return;
            if (!firearmsAnimator.IsIdling()) return;

            var overrideController = EnsureOverrideController(animator);
            if (overrideController == null) return;

            SetOverride(overrideController, clip);
            WeaponAnimationSpeedControllerClass.SetUseLeftHand(animator, true);
            animator.Play(StateAndClipName, -1, 0f);

            StaticManager.KillCoroutine(_playRoutine);
            _playRoutine = StaticManager.BeginCoroutine(DriveAndRestore(animator, overrideController, clip.length));
        }

        private static AnimatorOverrideController EnsureOverrideController(IAnimator animator)
        {
            var current = animator.runtimeAnimatorController;
            if (current == null) return null;

            if (current is AnimatorOverrideController live && live.name == OurControllerName)
                return live;

            if (Controllers.TryGetValue(animator, out var cached))
            {
                // ours existed but something (another mod, controller swap) reassigned —
                // reinstall like WatchAnims does
                animator.runtimeAnimatorController = cached;
                return cached;
            }

            var existing = current as AnimatorOverrideController;
            var baseController = existing != null ? existing.runtimeAnimatorController : current;
            if (!baseController.animationClips.Any(c => c != null && c.name == StateAndClipName))
            {
                Plugin.LogSource?.LogWarning($"[CSGas] controller '{baseController.name}' has no {StateAndClipName} clip — cough anim unavailable here");
                return null;
            }

            var ours = new AnimatorOverrideController(baseController) { name = OurControllerName };
            Controllers[animator] = ours;
            animator.runtimeAnimatorController = ours;
            return ours;
        }

        // points every original clip named hand_nv_on at the cough clip (null restores)
        private static void SetOverride(AnimatorOverrideController controller, AnimationClip clip)
        {
            var pairs = new List<KeyValuePair<AnimationClip, AnimationClip>>(controller.overridesCount);
            controller.GetOverrides(pairs);
            for (var i = 0; i < pairs.Count; i++)
            {
                if (pairs[i].Key != null && pairs[i].Key.name == StateAndClipName)
                    pairs[i] = new KeyValuePair<AnimationClip, AnimationClip>(pairs[i].Key, clip);
            }
            controller.ApplyOverrides(pairs);
        }

        private static IEnumerator DriveAndRestore(IAnimator animator, AnimatorOverrideController controller, float clipLength)
        {
            // snapshot the config at play start — same F12 knobs as WatchAnims
            var blendIn = Mathf.Max(0.01f, Plugin.CoughBlendInTime.Value);
            var blendOut = Mathf.Max(0.01f, Plugin.CoughBlendOutTime.Value);
            var blendOutLead = Plugin.CoughBlendOutLead.Value;

            yield return null;

            var stateHash = Animator.StringToHash(StateAndClipName);
            var layer = -1;
            for (var i = 0; i < animator.layerCount; i++)
            {
                if (animator.GetCurrentAnimatorStateInfo(i).shortNameHash == stateHash
                    || animator.GetNextAnimatorStateInfo(i).shortNameHash == stateHash)
                {
                    layer = i;
                    break;
                }
            }

            if (layer >= 0)
            {
                var weight = animator.GetLayerWeight(layer);
                while (weight < 1f)
                {
                    weight = Mathf.MoveTowards(weight, 1f, Time.deltaTime / blendIn);
                    animator.SetLayerWeight(layer, weight);
                    yield return null;
                }

                yield return new WaitForSeconds(Mathf.Max(0f, clipLength - blendIn - blendOutLead));

                while (weight > 0f)
                {
                    weight = Mathf.MoveTowards(weight, 0f, Time.deltaTime / blendOut);
                    animator.SetLayerWeight(layer, weight);
                    yield return null;
                }
            }

            WeaponAnimationSpeedControllerClass.SetUseLeftHand(animator, false);
            SetOverride(controller, null);
        }
    }
}
