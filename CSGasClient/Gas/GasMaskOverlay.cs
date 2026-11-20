using BSG.CameraEffects;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // backport of retail's FaceCoverMaskEffect: a holder on the camera's TextureMask (the
    // black vignette NVGs use) showing the gas mask's eyepieces. 4.1 already ships both
    // textures on the NightVision component, so nothing is ripped.
    // NV and thermal share the same TextureMask and win while they're on.
    internal static class GasMaskOverlay
    {
        private sealed class Holder : ITextureMaskHolder { }
        private static readonly Holder _holder = new Holder();
        // CameraManager.Instance builds a new manager when there is none — read the field instead
        private static readonly AccessTools.FieldRef<CameraManager> _cameraManager =
            AccessTools.StaticFieldRefAccess<CameraManager>(AccessTools.Field(typeof(CameraManager), "instance"));

        private static TextureMask _enrolledIn;
        private static Item _lastItem;
        private static MaskOverlay _lastOverlay;

        public static void Tick()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                Release();
                return;
            }

            var player = Singleton<GameWorld>.Instance.MainPlayer;
            if (player == null || player.HealthController == null || !player.HealthController.IsAlive
                || player.PointOfView != EPointOfView.FirstPerson)
            {
                Release();
                return;
            }

            var item = player.Equipment?.GetSlot(EquipmentSlot.FaceCover)?.ContainedItem;
            if (!ReferenceEquals(item, _lastItem))
            {
                _lastItem = item;
                _lastOverlay = null;
                if (item != null) CSGasConstants.MaskOverlays.TryGetValue(item.TemplateId.ToString(), out _lastOverlay);
            }

            var cm = _cameraManager();
            if (cm == null)
            {
                Release();
                return;
            }
            var nv = cm.NightVision;
            var textureMask = nv ? nv.TextureMask : null;
            // the hideout hides helmet visors through VisorEffect.Visible — follow it
            var visible = !cm.VisorEffect || cm.VisorEffect.Visible;
            var texture = _lastOverlay == null || !nv ? null
                : _lastOverlay.Shape == MaskOverlay.EShape.Binocular ? nv.BinocularMaskTexture
                : nv.GasMaskTexture;

            if (!textureMask || !texture || !visible)
            {
                Release();
                return;
            }

            if (!ReferenceEquals(_enrolledIn, textureMask)) Release();

            var thermal = cm.ThermalVision;
            var deviceOn = nv.On || (thermal && thermal.On);
            // mask + size before TryToEnable: OnEnable applies whatever is set at that moment
            if (!deviceOn && (textureMask.Mask != texture || textureMask.Size != _lastOverlay.Size))
            {
                textureMask.Mask = texture;
                textureMask.Size = _lastOverlay.Size;
                textureMask.ApplySettings();
            }
            if (_enrolledIn is null)
            {
                textureMask.TryToEnable(_holder, true);
                _enrolledIn = textureMask;
            }
        }

        // must run before the camera goes, or the holder keeps the vignette on
        public static void Release()
        {
            // reference check — a destroyed camera still needs its slot cleared
            if (_enrolledIn is object)
            {
                if (_enrolledIn) _enrolledIn.TryToEnable(_holder, false);
                _enrolledIn = null;
            }
        }

        // raid end — also drops the cached item so the old raid's inventory isnt kept alive
        public static void Reset()
        {
            Release();
            _lastItem = null;
            _lastOverlay = null;
        }
    }
}
