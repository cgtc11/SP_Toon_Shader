Shader "DiGiMonkey/XRay Outline"
{
    // The normal look is handled by the character's own body material (URP Lit, Toon, anything).
    // This is an overlay-only shader, added as extra material slots on the same mesh.
    // ZTest Greater draws only the pixels that fail the depth test, i.e. pixels hidden behind something.
    // Usage: add "DiGiMonkey/XRay Mask" then "DiGiMonkey/XRay Outline" (in this order) at the end of the Materials array
    //   (Tools > Hologram URP > Add XRay To Selected Object adds them automatically).
    // XRay Mask writes a stencil mark on the visible parts of the character itself, and this shader does not draw over that mark.
    // As a result it ignores occlusion by the character's own parts (e.g. head in front of face) and only draws where other objects such as walls hide the character.
    // Keep _StencilRef the same as the XRay Mask material.

    Properties
    {
        [Header(Occluded Look Seen Through Walls)]
        [HDR] _OccludedOutlineColor("Occluded Outline Color", Color) = (0.1, 0.85, 1.0, 1.0)
        [HDR] _OccludedFillColor("Occluded Fill Color", Color) = (0.05, 0.35, 0.55, 1.0)
        _OccludedFillOpacity("Occluded Fill Opacity", Range(0, 1)) = 0.35
        _OccludedEdgePower("Occluded Edge Power", Range(0.25, 8)) = 2.5
        _OccludedEdgeIntensity("Occluded Edge Intensity", Range(0, 4)) = 1.5
        [Toggle] _OccludedFillEnabled("Show Flat Fill When Occluded", Float) = 1
        [Toggle] _OccludedOutlineEnabled("Show Outline When Occluded", Float) = 1

        [Header(Self Occlusion)]
        [IntRange] _StencilRef("Stencil Ref (match XRay Mask)", Range(0, 255)) = 1

        [Header(Pulse Animation)]
        _PulseSpeed("Pulse Speed", Range(0, 10)) = 2.0
        _PulseAmount("Pulse Amount", Range(0, 1)) = 0.25
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent" "RenderPipeline"="UniversalPipeline" "IgnoreProjector"="True" }

        Pass
        {
            Name "XRayOccluded"
            Tags { "LightMode"="UniversalForward" }
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest Greater
            Cull Back

            Stencil
            {
                Ref [_StencilRef]
                Comp NotEqual
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _OccludedOutlineColor;
                half4 _OccludedFillColor;
                half _OccludedFillOpacity;
                half _OccludedEdgePower;
                half _OccludedEdgeIntensity;
                half _OccludedFillEnabled;
                half _OccludedOutlineEnabled;
                half _PulseSpeed;
                half _PulseAmount;
                half _StencilRef;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                half3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs pos = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionHCS = pos.positionCS;
                output.positionWS = pos.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                clip(_OccludedFillEnabled + _OccludedOutlineEnabled - 0.5h);

                half3 viewDir = SafeNormalize(GetWorldSpaceViewDir(input.positionWS));
                half rim = pow(1.0h - saturate(abs(dot(normalize(input.normalWS), viewDir))), _OccludedEdgePower);
                half outline = rim * _OccludedEdgeIntensity * _OccludedOutlineEnabled;

                half pulse = 1.0h - _PulseAmount + _PulseAmount * (0.5h + 0.5h * sin(_Time.y * _PulseSpeed));

                half3 color = _OccludedFillColor.rgb * _OccludedFillOpacity * _OccludedFillEnabled
                            + _OccludedOutlineColor.rgb * outline;

                half alpha = saturate((_OccludedFillOpacity * _OccludedFillEnabled) + outline) * pulse;

                return half4(color * pulse, alpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
