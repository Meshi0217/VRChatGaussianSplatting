Shader "Hidden/LV_DebugDisplayL1"
{
    SubShader
    {
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"
            #include "Packages/red.sim.lightvolumes/Shaders/LightVolumes.cginc"

            struct appdata
            {
                float4 vertex : POSITION; 
                float3 normal : NORMAL;
            };

            struct v2f
            {
                float4 worldPos    : TEXCOORD0; 
                float3 worldNormal : TEXCOORD1;
                float4 vertex      : SV_POSITION; 
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex);
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                float3 normalDir = normalize(i.worldNormal);

                float3 L0 = 0; float3 L1r = 0; float3 L1g = 0; float3 L1b = 0;
                LightVolumeSH(i.worldPos.xyz, L0, L1r, L1g, L1b);

                float3 result;
                result.r = dot(L1r, normalDir) + L0.r;
                result.g = dot(L1g, normalDir) + L0.g;
                result.b = dot(L1b, normalDir) + L0.b;
                return float4(result, 1.0);
            }
            ENDCG
        }
    }
}