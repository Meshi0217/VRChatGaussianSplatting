// Simple unlit shader with no culling. Used for occlusion baking.
Shader "Hidden/VRCLV/OcclusionShader" {
    Properties {
        [MainColor] _Color ("Color", Color) = (1,1,1,1)
    }
    SubShader {
        Tags { "RenderType"="Opaque" "Queue"="Geometry" }
        Cull Off
        ZWrite On
        
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;

            float4 vert (float4 vertex : POSITION) : SV_POSITION {
                return UnityObjectToClipPos(vertex);
            }

            float4 frag () : SV_Target {
                return _Color;
            }
            ENDCG
        }
    }
}
