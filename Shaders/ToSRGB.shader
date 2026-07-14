Shader "VRChatGaussianSplatting/ToSRGB"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+499" }

        // URP has no GrabPass, and its transparent queue is drawn in a single pass, so nothing can
        // be injected between two render queues. GaussianSplatRendererFeature therefore claims the
        // whole splat sequence: this LightMode keeps the pass out of URP's own transparent pass,
        // and the feature copies the camera colour into _GS_LinearBackground before drawing it.
        Pass
        {
            Tags { "LightMode" = "GaussianSplatToSRGB" }
            ZWrite Off
            ZTest Always
            Cull Off
            CGPROGRAM
            #pragma multi_compile_instancing
            #include_with_pragmas "FullscreenCommon.cginc"
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_GS_LinearBackground);
            float4 frag(v2f i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 col = GS_SAMPLE_GRABPASS_TEXTURE(_GS_LinearBackground, i.uv);
                return fixed4(LinearToGammaSpace(col.rgb), 0.0);
            }
            ENDCG
        }
    }

    FallBack Off
}
