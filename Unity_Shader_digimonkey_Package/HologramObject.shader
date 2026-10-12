Shader "DiGiMonkey/Hologram Object"
{
    Properties
    {
        [MainTexture] _BaseMap("Mask / Detail Texture", 2D) = "white" {}
        [Header(Texture Projection)]
        [Enum(OriginalUV,0,Planar,1,Box,2,Cylindrical,3,Spherical,4)] _ProjectionMode("Projection Mode", Float) = 0
        [Enum(FrontXY,0,SideZY,1,TopXZ,2)] _ProjectionPlane("Projection Plane", Float) = 0
        _ProjectionCenter("Projection Center (Local XYZ)", Vector) = (0, 0, 0, 0)
        _ProjectionSize("Projection Size (Local XYZ)", Vector) = (1, 1, 1, 0)
        _ProjectionBlend("Box Blend Sharpness", Range(1, 16)) = 4
        [Toggle] _DoubleSided("Render Both Sides", Float) = 1
        [HDR] _HologramColor("Hologram Color", Color) = (0.0, 0.65, 1.0, 1.0)
        _Opacity("Base Opacity", Range(0, 1)) = 0.28
        _EmissionIntensity("Emission Intensity", Range(0, 12)) = 4.0
        _TextureContrast("Texture Contrast", Range(0, 3)) = 1.0

        [Header(Vertical Gradient)]
        [Enum(Legacy,0,LocalX,1,LocalY,2,LocalZ,3,UV,4)] _GradientAxis("Gradient Axis", Float) = 0
        [HideInInspector] _GradientUseZAxis("Legacy Gradient Z Axis", Float) = 0
        [Toggle] _GradientInvert("Invert Top/Bottom", Float) = 0
        _GradientBottomY("Gradient Minimum (Local Axis)", Float) = -0.5
        _GradientTopY("Gradient Maximum (Local Axis)", Float) = 0.5
        _BottomFadeWidth("Bottom Fade Width", Range(0.001, 1.0)) = 0.25
        _BottomFadeContrast("Bottom Fade Contrast", Range(0.1, 8.0)) = 1.0
        [HDR] _TopColor("Top Color", Color) = (0.15, 0.9, 1.0, 1.0)
        _TopColorBlend("Top Color Blend", Range(0, 1)) = 0.65
        _TopBrightness("Top Brightness", Range(0, 5)) = 1.6

        [Header(Fresnel)]
        _FresnelPower("Fresnel Power", Range(0.25, 10)) = 3.0
        _FresnelIntensity("Fresnel Intensity", Range(0, 5)) = 1.5

        [Header(Scanlines)]
        _ScanlineDensity("Scanline Density", Range(1, 300)) = 90
        _ScanlineSpeed("Scanline Upward Speed", Range(-10, 10)) = 1.5
        _ScanlineSharpness("Scanline Sharpness", Range(1, 32)) = 12
        _ScanlineIntensity("Scanline Intensity", Range(0, 4)) = 1.0

        [Header(Moving Soft Light Band)]
        [HDR] _SoftBandColor("Soft Band Color", Color) = (0.15, 0.9, 1.0, 1.0)
        _SoftBandIntensity("Soft Band Intensity", Range(0, 8)) = 1.8
        _SoftBandPosition("Soft Band Position", Range(0, 1)) = 0.0
        _SoftBandSpeed("Soft Band Upward Speed", Range(-3, 3)) = 0.25
        _SoftBandWidth("Soft Band Core Width", Range(0.001, 0.5)) = 0.12
        _SoftBandLowerSoftness("Soft Band Lower Softness", Range(0.001, 0.5)) = 0.16
        _SoftBandUpperSoftness("Soft Band Upper Softness", Range(0.001, 0.5)) = 0.07

        [Header(Noise and Flicker)]
        _NoiseBlockWidth("Noise Block Width", Range(0.001, 1.0)) = 0.06
        _NoiseBlockHeight("Noise Block Height", Range(0.001, 1.0)) = 0.035
        _NoiseSpeed("Noise Upward Speed", Range(-10, 10)) = 1.2
        _NoiseAmount("Noise Amount", Range(0, 1)) = 0.18
        _FlickerAmount("Flicker Amount", Range(0, 0.5)) = 0.06

        [Header(Vertex Glitch)]
        _GlitchSpeed("Glitch Speed", Range(0, 30)) = 8
        _GlitchStrength("Glitch Strength", Range(0, 0.25)) = 0.025
        _GlitchFrequency("Glitch Frequency", Range(1, 80)) = 18
        _GlitchBandWidth("Glitch Band Width", Range(0.001, 0.25)) = 0.035
    }

    SubShader
    {
        Tags
        {
            "RenderType"="Transparent"
            "Queue"="Transparent"
            "RenderPipeline"="UniversalPipeline"
        }

        Pass
        {
            Name "HologramForward"
            Tags { "LightMode"="UniversalForward" }

            Blend SrcAlpha One
            ZWrite Off
            Cull Off

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_BaseMap);
            SAMPLER(sampler_BaseMap);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseMap_ST;
                float _ProjectionMode;
                float _ProjectionPlane;
                float4 _ProjectionCenter;
                float4 _ProjectionSize;
                float _ProjectionBlend;
                half _DoubleSided;
                half4 _HologramColor;
                half _Opacity;
                half _EmissionIntensity;
                half _TextureContrast;
                half _GradientUseZAxis;
                half _GradientAxis;
                half _GradientInvert;
                half _GradientBottomY;
                half _GradientTopY;
                half _BottomFadeWidth;
                half _BottomFadeContrast;
                half4 _TopColor;
                half _TopColorBlend;
                half _TopBrightness;
                half _FresnelPower;
                half _FresnelIntensity;
                half _ScanlineDensity;
                half _ScanlineSpeed;
                half _ScanlineSharpness;
                half _ScanlineIntensity;
                half4 _SoftBandColor;
                half _SoftBandIntensity;
                half _SoftBandPosition;
                half _SoftBandSpeed;
                half _SoftBandWidth;
                half _SoftBandLowerSoftness;
                half _SoftBandUpperSoftness;
                half _NoiseBlockWidth;
                half _NoiseBlockHeight;
                half _NoiseSpeed;
                half _NoiseAmount;
                half _FlickerAmount;
                half _GlitchSpeed;
                half _GlitchStrength;
                half _GlitchFrequency;
                half _GlitchBandWidth;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
                float2 uv : TEXCOORD2;
                float worldY : TEXCOORD3;
                float3 gradientPositionOS : TEXCOORD4;

                float3 projectionPositionOS : TEXCOORD6;
                float3 projectionNormalOS : TEXCOORD7;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash11(float p)
            {
                return frac(sin(p * 127.1) * 43758.5453);
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            Varyings Vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

                // Anchor the mapping to the model before vertex glitch displacement.
                output.projectionPositionOS = input.positionOS.xyz;
                output.projectionNormalOS = input.normalOS;
                float3 positionOS = input.positionOS.xyz;
                float3 basePositionWS = TransformObjectToWorld(positionOS);
                float phase = basePositionWS.y * _GlitchFrequency - _Time.y * _GlitchSpeed;
                float band = step(1.0 - _GlitchBandWidth, frac(phase));
                float slice = floor(phase);
                float offset = (Hash11(slice) * 2.0 - 1.0) * _GlitchStrength * band;
                positionOS.x += offset;

                VertexPositionInputs pos = GetVertexPositionInputs(positionOS);
                VertexNormalInputs nrm = GetVertexNormalInputs(input.normalOS);
                output.positionHCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = nrm.normalWS;
                output.uv = TRANSFORM_TEX(input.uv, _BaseMap);
                output.worldY = pos.positionWS.y;
                output.gradientPositionOS = positionOS;

                return output;
            }

            float2 TransformProjectionUV(float2 uv)
            {
                return uv * _BaseMap_ST.xy + _BaseMap_ST.zw;
            }

            // Explicit gradients avoid a coarse mip stripe at the angular wrap seam.
            half4 SampleAngularProjection(float2 uv)
            {
                float2 dx = ddx(uv);
                float2 dy = ddy(uv);
                dx.x -= round(dx.x);
                dy.x -= round(dy.x);
                return SAMPLE_TEXTURE2D_GRAD(_BaseMap, sampler_BaseMap,
                    TransformProjectionUV(uv), dx * _BaseMap_ST.xy, dy * _BaseMap_ST.xy);
            }

            half4 SampleHologramTexture(Varyings input)
            {
                if (_ProjectionMode < 0.5)
                    return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, input.uv);

                float3 size = max(abs(_ProjectionSize.xyz), float3(0.0001, 0.0001, 0.0001));
                float3 p = (input.projectionPositionOS - _ProjectionCenter.xyz) / size;

                if (_ProjectionMode < 1.5)
                {
                    float2 uv = p.xy;
                    if (_ProjectionPlane > 1.5) uv = p.xz;
                    else if (_ProjectionPlane > 0.5) uv = p.zy;
                    return SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, TransformProjectionUV(uv + 0.5));
                }

                if (_ProjectionMode < 2.5)
                {
                    // Smoothly blend three planar samples in model space.
                    // Under nonuniform projection sizing, normals transform inversely to positions.
                    float3 n = SafeNormalize(input.projectionNormalOS * size);
                    float3 weights = pow(abs(n), _ProjectionBlend);
                    weights /= max(weights.x + weights.y + weights.z, 0.0001);
                    float3 sideSign = step(float3(0, 0, 0), n) * 2.0 - 1.0;
                    half4 x = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap,
                        TransformProjectionUV(float2(-p.z * sideSign.x, p.y) + 0.5));
                    half4 y = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap,
                        TransformProjectionUV(float2(p.x, -p.z * sideSign.y) + 0.5));
                    half4 z = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap,
                        TransformProjectionUV(float2(p.x * sideSign.z, p.y) + 0.5));
                    return x * weights.x + y * weights.y + z * weights.z;
                }

                // Y is the cylinder/sphere vertical axis, +Z is the image center.
                // The seam is at -Z. Guard atan2 at the cylinder axis and sphere poles.
                float longitude = dot(p.xz, p.xz) > 0.00000001 ? atan2(p.x, p.z) : 0.0;
                float u = longitude / (2.0 * PI) + 0.5;
                if (_ProjectionMode < 3.5)
                    return SampleAngularProjection(float2(u, p.y + 0.5));

                float radius = max(length(p), 0.0001);
                float v = asin(clamp(p.y / radius, -1.0, 1.0)) / PI + 0.5;
                return SampleAngularProjection(float2(u, v));
            }

            half4 Frag(Varyings input, FRONT_FACE_TYPE frontFace : FRONT_FACE_SEMANTIC) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                half faceVisible = IS_FRONT_VFACE(frontFace, 1.0h, 0.0h);
                clip(max(_DoubleSided, faceVisible) - 0.5h);

                half4 tex = SampleHologramTexture(input);
                half textureMask = saturate(dot(tex.rgb, half3(0.299, 0.587, 0.114)) * tex.a * _TextureContrast);

                half3 normalWS = normalize(input.normalWS);
                half3 viewDirWS = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half fresnel = pow(1.0h - saturate(dot(normalWS, viewDirWS)), _FresnelPower);

                float wave = sin((input.worldY * _ScanlineDensity - _Time.y * _ScanlineSpeed) * PI);
                half scanline = pow(saturate(1.0h - abs(wave)), _ScanlineSharpness);

                // Use world X/Y only, never Z (depth). Positive speed moves upward.
                float blockWidth = max((float)_NoiseBlockWidth, 0.0001);
                float blockHeight = max((float)_NoiseBlockHeight, 0.0001);
                float2 noiseCoordinate = float2(
                    input.positionWS.x / blockWidth,
                    input.positionWS.y / blockHeight - _Time.y * _NoiseSpeed
                );
                float2 noiseCell = floor(noiseCoordinate);
                half noise = Hash21(noiseCell);
                half stability = saturate(1.0h - noise * _NoiseAmount);
                half flicker = 1.0h - _FlickerAmount + Hash11(floor(_Time.y * 24.0)) * _FlickerAmount;

                half signal = _Opacity
                            + fresnel * _FresnelIntensity
                            + scanline * _ScanlineIntensity;

                float gradientAxis = _GradientAxis < 0.5h ? (_GradientUseZAxis > 0.5h ? 3.0 : 2.0) : _GradientAxis;
                float gradientAxisValue = gradientAxis < 1.5 ? input.gradientPositionOS.x
                    : (gradientAxis < 2.5 ? input.gradientPositionOS.y : input.gradientPositionOS.z);
                half gradientRange = max(_GradientTopY - _GradientBottomY, 0.0001h);
                half vertical01Raw = saturate((gradientAxisValue - _GradientBottomY) / gradientRange);
                half vertical01 = lerp(vertical01Raw, 1.0h - vertical01Raw, _GradientInvert);
                half fadePosition = saturate(vertical01 / max(_BottomFadeWidth, 0.001h));
                half bottomFade = pow(smoothstep(0.0h, 1.0h, fadePosition), _BottomFadeContrast);

                half softBandCenter = frac(_SoftBandPosition + _Time.y * _SoftBandSpeed);
                half softBandOffset = vertical01 - softBandCenter;
                half softBandEdgeSoftness = softBandOffset >= 0.0h
                    ? _SoftBandUpperSoftness
                    : _SoftBandLowerSoftness;
                half softBandHalfWidth = _SoftBandWidth * 0.5h;
                half softBand = 1.0h - smoothstep(
                    softBandHalfWidth,
                    softBandHalfWidth + max(softBandEdgeSoftness, 0.001h),
                    abs(softBandOffset)
                );

                signal += softBand * _SoftBandIntensity;
                half alpha = saturate(signal * stability * flicker * lerp(0.35h, 1.0h, textureMask)) * bottomFade;

                half detail = lerp(0.5h, 1.5h, textureMask);
                half3 verticalColor = lerp(_HologramColor.rgb, _TopColor.rgb, vertical01 * _TopColorBlend);
                half verticalBrightness = lerp(1.0h, _TopBrightness, vertical01);
                half3 color = verticalColor
                            * _EmissionIntensity
                            * verticalBrightness
                            * detail
                            * (0.45h + fresnel * 1.4h + scanline * 0.8h)
                            * stability
                            * flicker
                            * bottomFade;

                color += _SoftBandColor.rgb
                       * _EmissionIntensity
                       * _SoftBandIntensity
                       * softBand
                       * stability
                       * flicker
                       * bottomFade;

                return half4(color, alpha);
            }
            ENDHLSL
        }
    }
    CustomEditor "DiGiMonkey.HologramObjectShaderGUI"
    FallBack Off
}
