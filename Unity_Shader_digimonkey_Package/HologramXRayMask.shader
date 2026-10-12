Shader "DiGiMonkey/XRay Mask"
{
    // Marking-only shader used together with XRay Outline. It draws nothing on screen.
    // It runs after the body material (Queue=AlphaTest) and writes a stencil mark on the visible parts of the character.
    // XRay Outline does not draw where the mark exists,
    // so XRay no longer appears on parts hidden by the character's own body (it only appears where other objects such as walls hide the character).
    // Keep _StencilRef the same as the XRay Outline material.

    Properties
    {
        [IntRange] _StencilRef("Stencil Ref (match XRay Outline)", Range(0, 255)) = 1
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="AlphaTest" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "XRayMask"
            Tags { "LightMode"="UniversalForward" }
            ColorMask 0
            ZWrite Off
            ZTest LEqual
            Cull Back
            Offset -1, -1

            Stencil
            {
                Ref [_StencilRef]
                Comp Always
                Pass Replace
            }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex Vert
            #pragma fragment Frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half _StencilRef;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                output.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            half4 Frag(Varyings input) : SV_Target
            {
                return half4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
