Shader "Custom/AlwaysOnTopTransparentMask"
{
    Properties
    {
        _MainTex("Texture", 2D) = "white" {}
        _Inflation("Inflation", Float) = 0
        _InvertedAlpha("Inverted Alpha", Float) = 1

        [Header(Culling)]
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Render Face", Float) = 2 // Added Culling option

        [Header(DepthTest)]
        [Enum(UnityEngine.Rendering.CompareFunction)] _ZTest("ZTest", Float) = 4 // "LEqual"
        [Enum(UnityEngine.Rendering.BlendOp)] _BlendOpColor("Blend Color", Float) = 2 // "ReverseSubtract"
        [Enum(UnityEngine.Rendering.BlendOp)] _BlendOpAlpha("Blend Alpha", Float) = 3 // "Min"

        [Header(Stencil Gate)]
        [Enum(UnityEngine.Rendering.CompareFunction)] _StencilComp("Stencil Compare", Float) = 6 // NotEqual
        _StencilRef("Stencil Ref", Float) = 64
        _StencilReadMask("Stencil Read Mask", Float) = 255
    }

    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" }
        LOD 100

        Pass
        {
            Cull [_Cull] // Added Culling command
            ZWrite Off
            ZTest [_ZTest]
            Stencil
            {
                Ref [_StencilRef]
                ReadMask [_StencilReadMask]
                Comp [_StencilComp]
                Pass Keep
                Fail Keep
                ZFail Keep
            }
            BlendOp [_BlendOpColor], [_BlendOpAlpha]
            Blend Zero One, One One

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv     : TEXCOORD0;
                float3 normal : NORMAL;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;
            float _Inflation;
            float _InvertedAlpha;

            v2f vert(appdata v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_OUTPUT(v2f, o);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                // Apply vertex offset along normal
                o.vertex = UnityObjectToClipPos(v.vertex + v.normal * _Inflation);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 col = tex2D(_MainTex, i.uv);
                float alpha = lerp(col.r, 1.0 - col.r, _InvertedAlpha);
                return float4(0, 0, 0, alpha);
            }
            ENDCG
        }
    }
}
