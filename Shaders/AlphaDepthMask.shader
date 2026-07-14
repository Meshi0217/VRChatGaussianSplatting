Shader "VRChatGaussianSplatting/AlphaDepthMask"
{
    SubShader
    {
        Tags { "Queue" = "Transparent+500" }

        // Destination alpha cannot be read in a fragment shader, so this pass reads a copy of the
        // colour target instead. GaussianSplatRendererFeature makes that copy into _GS_GrabTexture
        // immediately before drawing this pass, which is what GrabPass {} used to do.
        Pass
        {
            Tags { "LightMode" = "GaussianSplatAlphaMask" }
            ZWrite Off
            ZTest Always
            Cull Off
            ColorMask 0
            Stencil {
                Ref 1
                Comp Always
                Pass Replace   // write 1 into stencil
            }
            CGPROGRAM
            #pragma multi_compile_instancing
            #include "FullscreenCommon.cginc"
            UNITY_DECLARE_SCREENSPACE_TEXTURE(_GS_GrabTexture);
            float4 frag(v2f i) : SV_Target {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                fixed4 col = GS_SAMPLE_GRABPASS_TEXTURE(_GS_GrabTexture, i.uv);
                if(col.a < 0.99) discard;
                return 0.0;
            }
            ENDCG
        }
    }

    FallBack Off
}
