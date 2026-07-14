Shader "VRChatGaussianSplatting/ToLinear"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+600" }

        // Closes out the splat sequence. _GS_SRGBBackground is the colour target copied by
        // GaussianSplatRendererFeature right before this pass; _GS_LinearBackground is still the
        // pre-splat copy that ToSRGB was given, which is what lets us subtract the background's
        // contribution back out of the front-to-back accumulation.
        Pass
        {
            Tags { "LightMode" = "GaussianSplatToLinear" }
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma multi_compile_instancing
            #include "FullscreenCommon.cginc"
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_GS_SRGBBackground);
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_GS_LinearBackground);
            float4 frag(v2f i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float4 colPostSplat = GS_SAMPLE_GRABPASS_TEXTURE(_GS_SRGBBackground, i.uv);

                //Fix for front to back splat rendering
                float4 colPreSplat = GS_SAMPLE_GRABPASS_TEXTURE(_GS_LinearBackground, i.uv);
                colPostSplat.rgb -= LinearToGammaSpace(colPreSplat.rgb) * colPostSplat.a;

                colPostSplat.rgb = GammaToLinearSpace(colPostSplat.rgb);
                return colPostSplat;
            }
            ENDCG
        }
    }

    FallBack Off
}
