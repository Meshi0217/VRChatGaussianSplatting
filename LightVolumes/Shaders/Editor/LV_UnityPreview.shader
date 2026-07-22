Shader "Hidden/LV_DebugDisplayL0"
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
            };

            struct v2f
            {
                float4 worldPos : TEXCOORD0; 
                float4 vertex   : SV_POSITION; 
            };

            v2f vert (appdata v)
            {
                v2f o;
                o.worldPos = mul(unity_ObjectToWorld, v.vertex);

                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            float4 frag (v2f i) : SV_Target
            {
                return float4(LightVolumeSH_L0(i.worldPos.xyz), 1.0);
            }
            ENDCG
        }
    }
}