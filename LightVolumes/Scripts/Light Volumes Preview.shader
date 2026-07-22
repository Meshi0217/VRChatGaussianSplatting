Shader "Hidden/LightVolumesPreview" {

    SubShader {

        Pass {

            HLSLPROGRAM
            #pragma vertex   vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "UnityCG.cginc"
            #include "Packages/red.sim.lightvolumes/Shaders/LightVolumes.cginc"

            StructuredBuffer<float3> _Positions;
            float   _Scale;

            struct Attributes {

                float3 posOS  : POSITION;
                float3 normOS : NORMAL;
                uint   id     : SV_InstanceID;

            };

            struct Varyings {

                float4 posCS  : SV_Position;
                float3 normWS : NORMAL;
                float3 posWS  : TEXCOORD0;

            };

            Varyings vert (Attributes v) {

                float3 world = _Positions[v.id] + v.posOS * _Scale;
                Varyings o;
                o.posCS  = mul(UNITY_MATRIX_VP, float4(world, 1));
                o.normWS = v.normOS;
                o.posWS  = world;
                return o;

            }

            float4 frag (Varyings i) : SV_Target {
                if(!_UdonLightVolumeEnabled){
                    float3 N = normalize(i.normWS);
                    float3 V = normalize(_WorldSpaceCameraPos + float3(1,1,1) - i.posWS);
                    float3 L = V;
                    float diff = saturate(dot(N, L));
                    float3 H = normalize(L + V);
                    float spec = pow(saturate(dot(N, H)), 32);
                    float3 lit = (0.35 + 0.65 * diff) + spec.xxx;
                    return float4(lit, 1);
                } else {
                    float3 L0;
                    float3 L1r;
                    float3 L1g;
                    float3 L1b;
                    LightVolumeSH(i.posWS, L0, L1r, L1g, L1b);
                    return float4(LightVolumeEvaluate(normalize(i.normWS), L0, L1r, L1g, L1b), 1);
                }
            }
            ENDHLSL

        }

    }

}