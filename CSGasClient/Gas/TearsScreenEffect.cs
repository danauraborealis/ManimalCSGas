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
        private static readonly int ColorFromId = Shader.PropertyToID("_VignetteColorFrom");
        private static readonly int ColorToId = Shader.PropertyToID("_VignetteColorTo");
        private const float TintChangeSpeed = 1.5f; // per second, walking from one gas into another

        private Material _material;
        private float _strength;
        private float _target;
        private bool _tintable;
        private Color _baseFrom, _baseTo;
        private Color _tint = Color.white;
        private Color _tintTarget = Color.white;

        // fade out — keeps whatever colour the overlay has
        public static void SetTarget(float target)
        {
            if (_instance != null) _instance._target = target;
        }

        // finds the current raid camera each call — cameras die between raids, so the
        // component is recreated on demand rather than cached across sessions.
        // tint is the gas colour: the vignette's two greys are multiplied by it
        public static void SetTarget(float target, Color tint)
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
                _instance._tintable = material.HasProperty(ColorFromId) && material.HasProperty(ColorToId);
                if (_instance._tintable)
                {
                    _instance._baseFrom = material.GetColor(ColorFromId);
                    _instance._baseTo = material.GetColor(ColorToId);
                }
                _instance._tint = tint; // starts in the right colour instead of fading in from white
            }
            _instance._target = target;
            _instance._tintTarget = tint;
        }

        private void Update()
        {
            if (_tint != _tintTarget)
            {
                var step = TintChangeSpeed * Time.deltaTime;
                _tint = new Color(
                    Mathf.MoveTowards(_tint.r, _tintTarget.r, step),
                    Mathf.MoveTowards(_tint.g, _tintTarget.g, step),
                    Mathf.MoveTowards(_tint.b, _tintTarget.b, step),
                    1f);
            }

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
            if (_tintable)
            {
                _material.SetColor(ColorFromId, new Color(_baseFrom.r * _tint.r, _baseFrom.g * _tint.g, _baseFrom.b * _tint.b, _baseFrom.a));
                _material.SetColor(ColorToId, new Color(_baseTo.r * _tint.r, _baseTo.g * _tint.g, _baseTo.b * _tint.b, _baseTo.a));
            }

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
