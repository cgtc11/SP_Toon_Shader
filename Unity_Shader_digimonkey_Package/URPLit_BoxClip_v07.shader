Shader "DiGiMonkey/URPLit_BoxClip_Perfect"
{
    Properties
    {
        [Header(Base Texture)]
        [MainTexture] _BaseMap("Albedo / Transparency (RGBA)", 2D) = "white" {}
        [MainColor] _BaseColor("Color Tint", Color) = (1,1,1,1)

        [Header(PBR Metallic Smoothness)]
        [Toggle(_METALLICSMA_ON)] _UseMetallicMap("Use MetallicSmoothness Map", Float) = 0
        _MetallicGlossMap("Metallic (R) Smoothness (A)", 2D) = "white" {}
        _Metallic("Metallic Scale (No Map)", Range(0.0, 1.0)) = 1.0
        _Smoothness("Smoothness Scale", Range(0.0, 1.0)) = 0.8

        [Header(Normal Map)]
        [Toggle(_NORMALMAP_ON)] _UseNormal("Use Normal Map", Float) = 0
        [Toggle(_FLIP_NORMAL_Y)] _FlipNormalY("Flip Normal Y (DirectX to OpenGL)", Float) = 0
        _BumpMap("Normal Map", 2D) = "bump" {}
        _BumpScale("Normal Scale", Float) = 1.0

        [Header(Emission)]
        [Toggle(_EMISSION_ON)] _UseEmission("Use Emission", Float) = 0
        _EmissionMap("Emission Map", 2D) = "white" {}
        [HDR] _EmissionColor("Emission Color", Color) = (0,0,0,0)

        [Header(Box Mask Settings)]
        [Toggle] _EnableBoxCut("Enable Box Cut", Float) = 1.0
        _FadeWidth("Fade Width", Range(0.0, 1.0)) = 0.1
        [Toggle] _FadeInward("Fade Toward Inside", Float) = 0.0

        [Header(DEBUG)]
        [Toggle] _DebugAdditionalLights("Debug: Show Additional Lights Count", Float) = 0.0
    }

    SubShader
    {
        Tags 
        { 
            "RenderType" = "Opaque" 
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            
            #pragma shader_feature_local _METALLICSMA_ON
            #pragma shader_feature_local _NORMALMAP_ON
            #pragma shader_feature_local _FLIP_NORMAL_Y
            #pragma shader_feature_local _EMISSION_ON

            // マルチコンパイルを追加して環境リフレクションやライトデータを正しく受け取る
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE
            #pragma multi_compile _ _SHADOWS_SOFT
            #pragma multi_compile_fragment _ _AMBIENT_OCCLUSION

            // Point/Spotライト（Additional Lights）を有効化
            // Forward（Per Pixel）用
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _ADDITIONAL_LIGHT_SHADOWS
            // Forward+ 用（Unity 6.0は_FORWARD_PLUS、6.1以降は_CLUSTER_LIGHT_LOOP）
            #pragma multi_compile _ _FORWARD_PLUS
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Input.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/RealtimeLights.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float _Metallic;
                float _Smoothness;
                float _BumpScale;
                float4 _EmissionColor;
                float _EnableBoxCut;
                float _FadeWidth;
                float _FadeInward;
                float _DebugAdditionalLights;
            CBUFFER_END

            float4x4 _BoxWorldToLocal; 

            Texture2D _BaseMap; SamplerState sampler_BaseMap;
            Texture2D _MetallicGlossMap; SamplerState sampler_MetallicGlossMap;
            Texture2D _BumpMap; SamplerState sampler_BumpMap;
            Texture2D _EmissionMap; SamplerState sampler_EmissionMap;

            struct Attributes
            {
                float4 positionOS   : POSITION;
                float3 normalOS     : NORMAL;
                float4 tangentOS    : TANGENT;
                float2 uv           : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionCS   : SV_POSITION;
                float3 positionWS   : TEXCOORD0;
                float2 uv           : TEXCOORD1;
                float3 normalWS     : TEXCOORD3;
                float3 tangentWS    : TEXCOORD4;
                float3 bitangentWS  : TEXCOORD5;
            };

            Varyings vert(Attributes input)
            {
                Varyings output = (Varyings)0;
                
                output.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                output.positionCS = TransformWorldToHClip(output.positionWS);
                
                output.uv = input.uv;

                float3 normalWS = TransformObjectToWorldNormal(input.normalOS);
                float3 tangentWS = TransformObjectToWorldDir(input.tangentOS.xyz);
                float3 bitangentWS = cross(normalWS, tangentWS) * input.tangentOS.w;

                output.normalWS = normalWS;
                output.tangentWS = tangentWS;
                output.bitangentWS = bitangentWS;

                return output;
            }

            float BoxSDF(float3 localPos, float3 halfExtents)
            {
                float3 q = abs(localPos) - halfExtents;
                return length(max(q, 0.0)) + min(max(q.x, max(q.y, q.z)), 0.0);
            }

            float GetDither4x4(float2 screenPos)
            {
                const float ditherTable[16] = {
                    1.0 / 17.0,  9.0 / 17.0,  3.0 / 17.0, 11.0 / 17.0,
                    13.0 / 17.0,  5.0 / 17.0, 15.0 / 17.0,  7.0 / 17.0,
                    4.0 / 17.0, 12.0 / 17.0,  2.0 / 17.0, 10.0 / 17.0,
                    16.0 / 17.0,  8.0 / 17.0, 14.0 / 17.0,  6.0 / 17.0
                };
                uint2 pixelPos = uint2(screenPos) % 4;
                return ditherTable[pixelPos.y * 4 + pixelPos.x];
            }

            // 1灯分のCook-Torrance BRDFを計算（MainLight/Additional Lights共通）
            float3 ComputeDirectLighting(float3 N, float3 V, float3 L, float3 lightColor,
                                          float3 albedo, float3 F0, float smoothness, float metallic)
            {
                float3 H = normalize(L + V);

                float NdotL = saturate(dot(N, L));
                float NdotV = saturate(dot(N, V));
                float NdotH = saturate(dot(N, H));
                float LdotH = saturate(dot(L, H));

                float alpha = (1.0 - smoothness) * (1.0 - smoothness);
                float alpha2 = alpha * alpha;
                float denom = (NdotH * NdotH * (alpha2 - 1.0) + 1.0);
                float D = alpha2 / (3.14159265 * denom * denom);

                float3 F = F0 + (1.0 - F0) * pow(1.0 - LdotH, 5.0);

                float k = ((1.0 - smoothness) + 1.0) * ((1.0 - smoothness) + 1.0) / 8.0;
                float g1v = NdotV / (NdotV * (1.0 - k) + k);
                float g1l = NdotL / (NdotL * (1.0 - k) + k);
                float G = g1v * g1l;

                float3 specularBRDF = (D * F * G) / max(4.0 * NdotV * NdotL, 0.001);
                float3 kD = (float3(1.0, 1.0, 1.0) - F) * (1.0 - metallic);
                float3 diffuseBRDF = kD * albedo / 3.14159265;

                return (diffuseBRDF + specularBRDF) * lightColor * NdotL;
            }

            float4 frag(Varyings input) : SV_Target
            {
                // 1. Boxクリッピング処理
                if (_EnableBoxCut > 0.5)
                {
                    float3 boxLocalPos = mul(_BoxWorldToLocal, float4(input.positionWS, 1.0)).xyz;
                    float dist = BoxSDF(boxLocalPos, float3(0.5, 0.5, 0.5)); 
                    float fw = max(_FadeWidth, 0.0001);

                    // OFF: 境界から外側(dist 0→+fw)でフェード（従来通り）
                    // ON : 境界から内側(dist -fw→0)でフェード
                    float fade = (_FadeInward > 0.5)
                        ? saturate(dist / fw + 1.0)
                        : saturate(dist / fw);
                    float dither = GetDither4x4(input.positionCS.xy);
                    
                    if (fade < dither)
                    {
                        discard;
                    }
                }

                // 2. テクスチャとPBRパラメータの取得
                float4 albedoTex = _BaseMap.Sample(sampler_BaseMap, input.uv);
                float3 albedo = albedoTex.rgb * _BaseColor.rgb;

                float targetMetallic = _Metallic;
                float targetSmoothness = _Smoothness;

                #if defined(_METALLICSMA_ON)
                    float4 metSmaTex = _MetallicGlossMap.Sample(sampler_MetallicGlossMap, input.uv);
                    targetMetallic = metSmaTex.r; 
                    targetSmoothness = metSmaTex.a * _Smoothness; 
                #endif

                // 法線マップ変換
                float3 N = normalize(input.normalWS);
                #if defined(_NORMALMAP_ON)
                    float4 packedNormal = _BumpMap.Sample(sampler_BumpMap, input.uv);
                    float3 tangentNormal = UnpackNormalScale(packedNormal, _BumpScale);
                    
                    #if defined(_FLIP_NORMAL_Y)
                        tangentNormal.y = -tangentNormal.y;
                    #endif

                    float3 T = normalize(input.tangentWS);
                    float3 B = normalize(input.bitangentWS);
                    N = normalize(tangentNormal.x * T + tangentNormal.y * B + tangentNormal.z * N);
                #endif

                // 3. 各種ベクトルの計算
                float3 V = normalize(_WorldSpaceCameraPos - input.positionWS);
                float3 F0 = lerp(float3(0.04, 0.04, 0.04), albedo, targetMetallic);

                // 4. メインライト（Directional Light）のPBR計算
                float3 mainL = normalize(_MainLightPosition.xyz);
                float3 directLighting = ComputeDirectLighting(N, V, mainL, _MainLightColor.rgb,
                                                                albedo, F0, targetSmoothness, targetMetallic);

                // 4.5 追加ライト（Point / Spot）のPBR計算　※Forward / Forward+ 両対応
                InputData inputData = (InputData)0;
                inputData.positionWS = input.positionWS;
                inputData.normalWS = N;
                inputData.viewDirectionWS = V;
                inputData.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(input.positionCS);

                uint pixelLightCount = GetAdditionalLightsCount();

                if (_DebugAdditionalLights > 0.5)
                {
                    // 赤: 検出されたAdditional Lightsの数 (4灯で真っ赤) / 緑: このパスに来ている確認用
                    return float4(saturate(pixelLightCount / 4.0), 1.0, 0.0, 1.0);
                }

                LIGHT_LOOP_BEGIN(pixelLightCount)
                    Light addLight = GetAdditionalLight(lightIndex, inputData.positionWS, half4(1, 1, 1, 1));
                    float3 addLightColor = addLight.color * addLight.distanceAttenuation * addLight.shadowAttenuation;
                    directLighting += ComputeDirectLighting(N, V, addLight.direction, addLightColor,
                                                              albedo, F0, targetSmoothness, targetMetallic);
                LIGHT_LOOP_END

                float NdotV = saturate(dot(N, V));

                // 5. 【修正】環境リフレクション（Skybox）のサンプリング処理
                float3 reflectVector = reflect(-V, N);
                float roughness = 1.0 - targetSmoothness;

                // URP内部で管理されるグローバルオクルージョン設定（1.0固定）
                float ambientOcclusion = 1.0; 

                // 【修正】第一引数に反射ベクトル、第二引数にラフネス、第三引数にAOを正しく引き渡す
                float3 environmentReflection = GlossyEnvironmentReflection(reflectVector, roughness, ambientOcclusion);
                
                // 間接光のフレネル（インダイレクトスペックラー）
                float3 F_indirect = F0 + (max(float3(targetSmoothness, targetSmoothness, targetSmoothness), F0) - F0) * pow(1.0 - NdotV, 5.0);
                float3 indirectSpecular = environmentReflection * F_indirect;

                // 環境光（SH）の反映
                float3 envLightSH = SampleSH(N);
                float3 indirectDiffuse = envLightSH * albedo * (1.0 - targetMetallic); 
                
                float3 environmentLighting = indirectDiffuse + indirectSpecular;

                // 6. エミッシブ（発光）の計算
                float3 emission = float3(0, 0, 0);
                #if defined(_EMISSION_ON)
                    float4 emissiveTex = _EmissionMap.Sample(sampler_EmissionMap, input.uv);
                    emission = emissiveTex.rgb * _EmissionColor.rgb;
                #endif

                float3 finalColor = directLighting + environmentLighting + emission;

                return float4(finalColor, albedoTex.a * _BaseColor.a);
            }
            ENDHLSL
        }
    }
    FallBack "Hidden/Universal Render Pipeline/FallbackError"
}