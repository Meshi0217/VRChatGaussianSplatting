// Writes the compute-sorted (key, id) buffer into the render-order texture with the exact layout
// the draw expects: texel IndexToUV(rank) holds the splat id of that rank, as a float -- the same
// contract Hidden/GaussianSplatting/CopyRenderOrder fulfils for the blit sort's key-value texture.
Shader "Hidden/GaussianSplatting/CopyRenderOrderFromBuffer"
{
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Overlay" }
        Pass
        {
            Cull Off
            ZWrite Off
            ZTest Always

            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 4.5

            #include "UnityCG.cginc"
            #include "Utils.cginc"

            StructuredBuffer<uint2> _SortedKeys;
            int _ElementCount;

            float frag(v2f_img input) : SV_Target
            {
                uint index = UVToIndex((uint2)floor(input.pos.xy));
                if (index >= (uint)_ElementCount)
                {
                    // The draw never reads these texels (it culls id >= actualSplatCount before
                    // the order fetch). The sentinel matches the blit sort's out-of-range value
                    // (RadixSort.shader) so an accidental read yields an invalid id, not splat 0.
                    return 1e10;
                }
                return (float)_SortedKeys[index].y;
            }
            ENDCG
        }
    }
    Fallback Off
}
