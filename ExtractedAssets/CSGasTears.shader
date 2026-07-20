// recreation of 1.0's Hidden/TearsEffect, structured after the real shader's property
// surface (AssetRipper dump of the compiled original — its GPU program isnt recoverable,
// so the math here is reconstructed):
//   - screen distortion: two scrolling noise samples warp the image (watery eyes),
//     weighted away from _CenterOfView so the middle stays readable
//   - vignette: noise-modulated ring between _VignetteColorFrom/_VignetteColorTo with a
//     sin-pulse (_VignetteBlinking = speed X, contrast Y) — the eye-burn throb
// _Strength (0..1) is driven from code, same contract as the original (its a plain
// uniform there too, not in the Properties block).
//
// REPLACE the old CSGasTears.shader in the bundle project with this file, then create a
// material named "csgas_tears" from it with Smoke_blender_generated_01 assigned to BOTH
// texture slots (Distortion + Vignette Noise), include both in csgas_fx.bundle, rebuild.
Shader "Hidden/CSGasTears"
{
    Properties
    {
        [HideInInspector] _MainTex ("Base Map (RGB)", 2D) = "white" {}
        [Header(Screen Distortion)]
        [NoScaleOffset] _DistortionTex ("Texture", 2D) = "white" {}
        _DistortionSpeed ("Speed", Vector) = (-0.12, 0.2, 0.26, 0.11)
        _DistortionPower ("Power", Vector) = (0.2, 0.3, 0, 0)
        _CenterOfView ("Center Of View", Range(0, 1)) = 0.5
        [Header(Vignette)]
        [NoScaleOffset] _VignetteNoiseTex ("Noise Texture", 2D) = "white" {}
        _VignetteNoiseTexTillingOffset ("Noise Texture Tilling Offset", Vector) = (1, 1, 0, 0)
        _VignetteAppearMultiplier ("Appear Multiplier", Range(1, 10)) = 4
        _VignetteNoiseScrollSpeed ("Noise Scroll Speed", Range(-5, 5)) = 0.2
        // real defaults are black; the reference screenshot reads whitish-grey, so these
        // default to that — tune on the material
        _VignetteColorFrom ("Color From", Color) = (0.55, 0.55, 0.55, 1)
        _VignetteColorTo ("Color To", Color) = (0.88, 0.88, 0.88, 1)
        _VignetteRadius ("Radius", Range(0, 1)) = 0.4
        _VignetteFalloff ("Falloff", Vector) = (0, 1, 0, 0)
        _VignetteBlinking ("Speed (X), Contrast (Y)", Vector) = (5, 6, 0, 0)
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float4 _MainTex_TexelSize;
            sampler2D _DistortionTex;
            sampler2D _VignetteNoiseTex;
            float4 _DistortionSpeed;
            float4 _DistortionPower;
            float _CenterOfView;
            float4 _VignetteNoiseTexTillingOffset;
            float _VignetteAppearMultiplier;
            float _VignetteNoiseScrollSpeed;
            fixed4 _VignetteColorFrom;
            fixed4 _VignetteColorTo;
            float _VignetteRadius;
            float4 _VignetteFalloff;
            float4 _VignetteBlinking;
            float _Strength; // driven from code, 0..1

            // custom vert instead of vert_img: EFT renders into scaled/flipped RTs
            // (resolution scaling), so screen reads must go through _MainTex_ST and honor
            // the RT flip — ignoring them squeezes the image into a corner upside down
            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 suv : TEXCOORD0; // raw 0..1 screen uv — vignette geometry
                float2 tuv : TEXCOORD1; // ST-adjusted uv — texture reads
            };

            v2f vert (appdata_img v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float2 uv = v.texcoord;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0) uv.y = 1.0 - uv.y;
                #endif
                o.suv = v.texcoord;
                o.tuv = uv * _MainTex_ST.xy + _MainTex_ST.zw;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float2 uv = i.suv;
                float2 cd = uv - 0.5;
                float dist = length(cd) * 2.0;
                // distortion strength grows away from the protected view center
                float edge = saturate(dist - _CenterOfView + 0.5);

                // two independently scrolling warp layers (original: _DistortionSpeed.xy/.zw).
                // dump lists power (0.2, 0.3) — that scale in raw UV would tear the screen
                // apart, so treat it as percent-ish: x0.1 internally
                float2 n1 = tex2D(_DistortionTex, uv + _Time.y * _DistortionSpeed.xy).rg - 0.5;
                float2 n2 = tex2D(_DistortionTex, uv * 1.7 + _Time.y * _DistortionSpeed.zw).rg - 0.5;
                // warp applied in ST space so the offset stays inside the valid RT region
                float2 warp = (n1 + n2) * (_DistortionPower.xy * 0.1) * _Strength * edge * _MainTex_ST.xy;
                fixed4 col = tex2D(_MainTex, i.tuv + warp);

                // noise-modulated vignette ring
                float noise = tex2D(_VignetteNoiseTex,
                    uv * _VignetteNoiseTexTillingOffset.xy + _VignetteNoiseTexTillingOffset.zw
                    + _Time.y * _VignetteNoiseScrollSpeed).r;
                float vStr = saturate(_Strength * _VignetteAppearMultiplier * 0.25); // full ring by strength 1 at default multiplier
                float ring = smoothstep(_VignetteRadius,
                    _VignetteRadius + max(_VignetteFalloff.y, 0.001),
                    dist + (noise - 0.5) * 0.35);
                // eye-burn throb
                float blink = pow(0.5 + 0.5 * sin(_Time.y * _VignetteBlinking.x), max(_VignetteBlinking.y, 1.0));
                ring *= vStr * (0.75 + 0.25 * blink);

                fixed3 vcol = lerp(_VignetteColorFrom.rgb, _VignetteColorTo.rgb, noise);
                col.rgb = lerp(col.rgb, vcol, saturate(ring));
                return col;
            }
            ENDCG
        }
    }
    Fallback Off
}
