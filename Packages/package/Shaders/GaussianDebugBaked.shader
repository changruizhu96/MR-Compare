// SPDX-License-Identifier: MIT
Shader "Gaussian Splatting/Debug Baked Points (Color)"
{
    Properties
    {
        _SplatSize("Point Size", Float) = 5.0 
    }
    SubShader
    {
        
        Tags { "RenderType"="Transparent" "Queue"="Transparent" }

        Pass
        {
            Name "DebugBaked"
            ZWrite On
            Cull Off
            
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 5.0

            #include "UnityCG.cginc"

            
            StructuredBuffer<float3> _BakedPosBuffer;
            StructuredBuffer<float4> _BakedColorBuffer; // r, g, b, a
            
            float _SplatSize;
            float4x4 _MatrixObjectToWorld;

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float4 color : COLOR;
            };


            static const float2 kQuadOffsets[6] = {
                float2(-1, -1), float2( 1, -1), float2( 1,  1), // Tri 1
                float2(-1, -1), float2( 1,  1), float2(-1,  1)  // Tri 2
            };

            v2f vert (uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
            {
                v2f o;

 
                float3 localPos = _BakedPosBuffer[instID];
                float4 bakedCol = _BakedColorBuffer[instID];


                float3 centerWorldPos = mul(_MatrixObjectToWorld, float4(localPos, 1.0)).xyz;
                float4 centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1.0));

                o.vertex = centerClipPos;


                float2 quadPos = kQuadOffsets[vtxID % 6]; 
                

                o.vertex.xy += (quadPos * _SplatSize / _ScreenParams.xy) * o.vertex.w;

                
                o.color = bakedCol; 
                o.color.a = 1.0f; 
                
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                return i.color;
            }
            ENDCG
        }
    }
}