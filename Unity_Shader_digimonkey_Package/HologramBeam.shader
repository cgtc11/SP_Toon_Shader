Shader "DiGiMonkey/Hologram Beam"
{
    Properties
    {
        [Toggle] _DoubleSided("Render Both Sides", Float) = 1
        [HDR] _BeamColor("Beam Color", Color) = (0.0, 0.55, 1.0, 1.0)
        _Opacity("Opacity", Range(0, 1)) = 0.13
        _EmissionIntensity("Emission Intensity", Range(0, 12)) = 3.0
        _EdgePower("Edge Power", Range(0.25, 8)) = 2.0
        _ScanlineDensity("Scanline Density", Range(1, 250)) = 55
        _ScanlineSpeed("Scanline Speed", Range(-10, 10)) = 1.2
        _NoiseAmount("Noise Amount", Range(0, 1)) = 0.35

        [Header(Vertical Fade)]
        _BottomFadeWidth("Bottom Fade Width", Range(0.001, 1.0)) = 0.18
        _TopFadeWidth("Top Fade Width", Range(0.001, 1.0)) = 0.25

        [Header(Vertical Fade Axis)]
        [Enum(Legacy,0,LocalX,1,LocalY,2,LocalZ,3,UV,4)] _GradientAxis("Gradient Axis", Float) = 0
        [HideInInspector] _GradientUseZAxis("Legacy Gradient Z Axis", Float) = 0
        [Toggle] _GradientInvert("Invert Top/Bottom", Float) = 0
        _GradientBottomY("Gradient Minimum (Local Axis)", Float) = -0.5
        _GradientTopY("Gradient Maximum (Local Axis)", Float) = 0.5
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            Name "BeamForward"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _DoubleSided;
                half4 _BeamColor;
                half _Opacity;
                half _EmissionIntensity;
                half _EdgePower;
                half _ScanlineDensity;
                half _ScanlineSpeed;
                half _NoiseAmount;
                half _BottomFadeWidth;
                half _TopFadeWidth;
                half _GradientUseZAxis;
                half _GradientAxis;
                half _GradientInvert;
                half _GradientBottomY;
                half _GradientTopY;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionHCS : SV_POSITION; float3 positionWS : TEXCOORD0; half3 normalWS : TEXCOORD1; float2 uv : TEXCOORD2; float3 gradientPositionOS : TEXCOORD3; };

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                output.uv = input.uv;
                output.gradientPositionOS = input.positionOS.xyz;
                return output;
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                half faceVisible = IS_FRONT_VFACE(frontFace, 1.0h, 0.0h);
                clip(max(_DoubleSided, faceVisible) - 0.5h);

                half3 viewDir = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half edge = pow(1.0h - saturate(abs(dot(normalize(input.normalWS), viewDir))), _EdgePower);
                half gradientRange = max(_GradientTopY - _GradientBottomY, 0.0001h);
                float gradientAxis = _GradientAxis < 0.5h ? (_GradientUseZAxis > 0.5h ? 3.0 : 4.0) : _GradientAxis;
                float gradientAxisValue = gradientAxis < 1.5 ? input.gradientPositionOS.x
                    : (gradientAxis < 2.5 ? input.gradientPositionOS.y : input.gradientPositionOS.z);
                half vertical01Raw = gradientAxis > 3.5
                    ? (half)input.uv.y
                    : saturate((gradientAxisValue - _GradientBottomY) / gradientRange);
                half vertical01 = lerp(vertical01Raw, 1.0h - vertical01Raw, _GradientInvert);

                half bottomFade = smoothstep(0.0h, max(_BottomFadeWidth, 0.001h), vertical01);
                half topFade = smoothstep(0.0h, max(_TopFadeWidth, 0.001h), 1.0h - vertical01);
                half verticalFade = bottomFade * topFade;
                half scan = pow(saturate(1.0h - abs(sin((vertical01 * _ScanlineDensity + _Time.y * _ScanlineSpeed) * PI))), 10.0h);
                half noise = Hash21(floor(input.positionWS.xz * 24.0 + _Time.y * 2.0));
                half alpha = _Opacity * verticalFade * (0.25h + edge + scan * 0.6h) * (1.0h - noise * _NoiseAmount);
                half3 color = _BeamColor.rgb * _EmissionIntensity * (0.35h + edge + scan);
                return half4(color, saturate(alpha));
            }
            ENDHLSL
        }
    }
    CustomEditor "DiGiMonkey.HologramGradientShaderGUI"
    FallBack Off
}
