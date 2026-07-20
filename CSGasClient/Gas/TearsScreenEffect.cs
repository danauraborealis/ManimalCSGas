using UnityEngine;

namespace Manimal.CSGas.Gas
{
    // port of 1.0's TearsEffect + TearGasAccumulator pair: a camera image effect (blurred
    // white vignette, single _Strength param) whose strength ramps toward a target instead
    // of snapping — 1.0 drove it via _strengthChangeSpeed and capped tear gas at 0.5.
    // CsGasCloud calls SetTarget every tick; when the target hits 0 and the ramp finishes,
    // the component removes itself from the camera.
    public class TearsScreenEffect : MonoBehaviour
    {
        private const float StrengthChangeSpeed = 0.35f; // per second, both directions

        private static TearsScreenEffect _instance;
        private static readonly int StrengthId = Shader.PropertyToID("_Strength");
        private static readonly int MainTexId = Shader.PropertyToID("_MainTex");

        private Material _material;
        private float _strength;
        private float _target;

        // finds the current raid camera each call — cameras die between raids, so the
        // component is recreated on demand rather than cached across sessions
        public static void SetTarget(float target)
        {
            if (_instance == null)
            {
                if (target <= 0f) return; // nothing to fade out — dont bother spawning
                var material = GasCloudAssets.GetTearsMaterial();
                if (material == null) return; // bundle without tears assets — overlay disabled
                var cam = Camera.main;
                if (cam == null) return;
                _instance = cam.gameObject.AddComponent<TearsScreenEffect>();
                // private copy — the bundle asset survives camera teardown between raids
                _instance._material = new Material(material);
            }
            _instance._target = target;
        }

        private void Update()
        {
            _strength = Mathf.MoveTowards(_strength, _target, StrengthChangeSpeed * Time.deltaTime);
            if (_target <= 0f && _strength <= 0.001f)
                Destroy(this);
        }

        // [ImageEffectOpaque] is the load-bearing part — the Icebreaker volumetric fog hit
        // the exact same quarter-screen/flip/burn-in artifact, and the fix was moving the
        // effect from the end of the image chain (after EFT's upscale/composite, where size
        // and flip conventions no longer match) into the pre-transparency slot. cost: the
        // tears render under transparents (smoke puffs draw over the vignette) — acceptable.
        // the manual ortho quad stays as belt-and-braces for the quad geometry itself.
        [ImageEffectOpaque]
        private void OnRenderImage(RenderTexture src, RenderTexture dst)
        {
            if (_material == null || _strength <= 0.001f)
            {
                Graphics.Blit(src, dst);
                return;
            }

            _material.SetFloat(StrengthId, _strength);
            _material.SetTexture(MainTexId, src);

            Graphics.SetRenderTarget(dst);
            GL.PushMatrix();
            GL.LoadOrtho();
            _material.SetPass(0);
            GL.Begin(GL.QUADS);
            GL.TexCoord2(0f, 0f); GL.Vertex3(0f, 0f, 0.1f);
            GL.TexCoord2(1f, 0f); GL.Vertex3(1f, 0f, 0.1f);
            GL.TexCoord2(1f, 1f); GL.Vertex3(1f, 1f, 0.1f);
            GL.TexCoord2(0f, 1f); GL.Vertex3(0f, 1f, 0.1f);
            GL.End();
            GL.PopMatrix();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
            if (_material != null) Destroy(_material);
        }
    }
}
