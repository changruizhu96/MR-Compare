// SPDX-License-Identifier: MIT
Shader"Gaussian Splatting/Customizable Gaussian Splats"
{
    Properties
    {
        _Transparency ("Global Transparency", Range(0, 1)) = 1.0
        [Enum(UnityEngine.Rendering.BlendMode)] _SrcBlend ("Src Blend", Float) = 5
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Dst Blend", Float) = 10
    }

    SubShader
    {
        Tags { 
            "RenderType" = "Transparent" 
            "Queue" = "Transparent" 
            "IgnoreProjector" = "True" 
            "ForceNoShadowCasting" = "True"
        }

        Pass
        {
ZWrite Off
Blend [_SrcBlend] [_DstBlend]
Cull Off
            
CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#pragma multi_compile_instancing

#include "UnityCG.cginc"
#include "GaussianSplatting.hlsl"

StructuredBuffer<uint> _OrderBuffer;

struct v2f
{
    half4 col : COLOR0;
    float2 pos : TEXCOORD0;
    float4 vertex : SV_POSITION;
    UNITY_VERTEX_OUTPUT_STEREO
};

StructuredBuffer<SplatViewData> _SplatViewData;
ByteAddressBuffer _SplatSelectedBits;
uint _SplatBitsValid;
uint _OptimizeForQuest;
float _Transparency;

v2f vert(uint vtxID : SV_VertexID, uint instID : SV_InstanceID)
{
    v2f o;
    UNITY_SETUP_INSTANCE_ID(vtxID);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

    instID = _OrderBuffer[instID];
    SplatViewData view = _SplatViewData[instID];

    float4 centerClipPos = view.pos;

    
    if (_OptimizeForQuest)
    {
        SplatData splat = LoadSplatData(instID);
        float3 centerWorldPos = mul(unity_ObjectToWorld, float4(splat.pos, 1)).xyz;
        centerClipPos = mul(UNITY_MATRIX_VP, float4(centerWorldPos, 1));
    }

    
    if (centerClipPos.w <= 0)
    {
        o.vertex = asfloat(0x7fc00000); 
        return o;
    }

    
    o.col.r = f16tof32(view.color.x >> 16);
    o.col.g = f16tof32(view.color.x);
    o.col.b = f16tof32(view.color.y >> 16);
    o.col.a = f16tof32(view.color.y);

    
    uint idx = vtxID;
    float2 quadPos = float2(idx & 1, (idx >> 1) & 1) * 2.0 - 1.0;
    quadPos *= 2;
    o.pos = quadPos;

    
    float2 deltaScreenPos = (quadPos.x * view.axis1 + quadPos.y * view.axis2) * 2 / _ScreenParams.xy;
    o.vertex = centerClipPos;
    o.vertex.xy += deltaScreenPos * centerClipPos.w;

    
    if (_SplatBitsValid)
    {
        uint wordIdx = instID / 32;
        uint bitIdx = instID & 31;
        uint selVal = _SplatSelectedBits.Load(wordIdx * 4);
        if (selVal & (1 << bitIdx))
        {
            o.col.a = -1;
        }
    }
    return o;
}

half4 frag(v2f i) : SV_Target
{
    
    float power = -dot(i.pos, i.pos);
    half alpha = exp(power);

    
    if (i.col.a >= 0)
    {
        alpha = saturate(alpha * i.col.a);
    }
    else
    {
        half3 selectedColor = half3(1, 0, 1);
        if (alpha > 7.0 / 255.0)
        {
            if (alpha < 10.0 / 255.0)
            {
                alpha = 1;
                i.col.rgb = selectedColor;
            }
            alpha = saturate(alpha + 0.3);
        }
        i.col.rgb = lerp(i.col.rgb, selectedColor, 0.5);
    }
    
    
    alpha *= _Transparency;
    alpha = saturate(alpha);

    
    if (alpha < 1.0 / 255.0)
        discard;

    
    half4 res = half4(i.col.rgb * alpha, alpha);
    return res;
}

ENDCG
        }
    }
CustomEditor"GaussianSplatEditor"
}