Shader "PulseVideoPlayer/UI/VideoSurface"
{
    Properties
    {
        [PerRendererData] _MainTex ("Video", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _RGBRect ("RGB Region", Vector) = (0,0,1,1)
        _AlphaRect ("Alpha Region", Vector) = (0,0,1,1)
        _HasAlpha ("Has Alpha", Float) = 0
        _Unpremultiply ("Unpremultiply", Float) = 0
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend One OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 3.0
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            struct VertexInput { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct VertexOutput
            {
                float4 vertex : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : COLOR;
                float4 position : TEXCOORD1;
            };
            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            float4 _RGBRect;
            float4 _AlphaRect;
            float4 _Color;
            float4 _ClipRect;
            float _HasAlpha;
            float _Unpremultiply;

            VertexOutput Vert(VertexInput input)
            {
                VertexOutput output;
                output.position = input.vertex;
                output.vertex = UnityObjectToClipPos(input.vertex);
                output.uv = float2(input.uv.x, 1 - input.uv.y);
                output.color = input.color * _Color;
                return output;
            }

            float2 RegionUV(float2 uv, float4 rect)
            {
                // 限制在区域边缘像素中心，避免 RGB、隔离带和 Alpha 相互渗透。
                float2 halfTexel = abs(_MainTex_TexelSize.xy) * 0.5;
                return clamp(rect.xy + uv * rect.zw, rect.xy + halfTexel, rect.xy + rect.zw - halfTexel);
            }

            float4 Frag(VertexOutput input) : SV_Target
            {
                float3 rgb = tex2D(_MainTex, RegionUV(input.uv, _RGBRect)).rgb;
                float alpha = _HasAlpha > 0.5 ? tex2D(_MainTex, RegionUV(input.uv, _AlphaRect)).r : 1;
                if (_Unpremultiply > 0.5) rgb = alpha > 0.00001 ? rgb / alpha : 0;
                // 外部纹理按线性资源包装，仅 RGB 执行颜色空间转换，灰度 Alpha 保持线性覆盖率。
                #ifndef UNITY_COLORSPACE_GAMMA
                rgb = GammaToLinearSpace(rgb);
                #endif
                alpha *= input.color.a;
                #ifdef UNITY_UI_CLIP_RECT
                alpha *= UnityGet2DClipping(input.position.xy, _ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(alpha - 0.001);
                #endif
                return float4(rgb * input.color.rgb * alpha, alpha);
            }
            ENDCG
        }
    }
}
