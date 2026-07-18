// SPDX-License-Identifier: MIT
Shader "Hidden/Gaussian Splatting/Composite"
{
    Properties
    {
        _GSColorMatchEnabled ("GS Color Match Enabled", Float) = 0
        _GSColorMatchBias ("GS Color Match Bias", Vector) = (0, 0, 0, 0)
        _GSColorMatchToneCurve ("GS Color Match Tone Curve", 2D) = "black" {}
        _GSColorMatchLut3DEnabled ("GS Color Match 3D LUT Enabled", Float) = 0
        _GSColorMatchLut3D ("GS Color Match 3D LUT", 3D) = "" {}
        _GSColorMatchStrength ("GS Color Match Strength", Float) = 1
        _GSColorMatchInputIsLinear ("GS Color Match Input Is Linear", Float) = 0
    }

    SubShader
    {
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Off
            Blend SrcAlpha OneMinusSrcAlpha

CGPROGRAM
#pragma vertex vert
#pragma fragment frag
#pragma require compute
#pragma use_dxc
#include "UnityCG.cginc"

struct v2f
{
    float4 vertex : SV_POSITION;
};

v2f vert (uint vtxID : SV_VertexID)
{
    v2f o;
    float2 quadPos = float2(vtxID&1, (vtxID>>1)&1) * 4.0 - 1.0;
	o.vertex = float4(quadPos, 1, 1);
    return o;
}

Texture2D _GaussianSplatRT;
float _GSColorMatchEnabled;
float4x4 _GSColorMatchMatrix;
float4 _GSColorMatchBias;
sampler2D _GSColorMatchToneCurve;
float _GSColorMatchLut3DEnabled;
sampler3D _GSColorMatchLut3D;
float _GSColorMatchStrength;
float _GSColorMatchInputIsLinear;

float3 ApplyColorMatch(float3 c)
{
    if (_GSColorMatchEnabled > 0.5)
    {
        c = mul((float3x3)_GSColorMatchMatrix, c) + _GSColorMatchBias.rgb;
        c = max(c, 0);
        c = float3(
            tex2D(_GSColorMatchToneCurve, float2(saturate(c.r), 0.5)).r,
            tex2D(_GSColorMatchToneCurve, float2(saturate(c.g), 0.5)).g,
            tex2D(_GSColorMatchToneCurve, float2(saturate(c.b), 0.5)).b);
        if (_GSColorMatchLut3DEnabled > 0.5)
            c = tex3D(_GSColorMatchLut3D, saturate(c)).rgb;
    }
    return c;
}

half4 frag (v2f i) : SV_Target
{
    half4 col = _GaussianSplatRT.Load(int3(i.vertex.xy, 0));
    float outputAlpha = saturate(col.a * 1.5);
    float blendAlpha = sqrt(outputAlpha);
    float3 premultipliedSource = _GSColorMatchInputIsLinear > 0.5 ? col.rgb : GammaToLinearSpace(col.rgb);
    float3 source = blendAlpha > 1e-5 ? premultipliedSource / blendAlpha : premultipliedSource;
    float3 c = ApplyColorMatch(source);
    c = lerp(source, c, _GSColorMatchStrength);
    col.rgb = c * blendAlpha;
    col.a = outputAlpha;
    return col;
}
ENDCG
        }
    }
}
