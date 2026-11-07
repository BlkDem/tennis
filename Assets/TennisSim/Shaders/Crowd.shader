// Spectators as camera-facing cards. Each card's four vertices sit on its seat; the vertex shader spreads
// them into a quad that turns about the vertical towards the camera, picks the pose from the crowd's
// excitement and the front or back view from where the camera is, and the fragment shader dresses
// every spectator in their own colours through the clothing mask.
//
// Vertex data, written by ProjectBuilder: position = seat, normal = the way the seat faces,
// uv0 = card corner (x -0.5..0.5, y 0..1), uv1 = per-spectator randoms (idle pose, shirt, shorts and
// clap phase, how easily they get excited).
// Atlas layout, from Tools/Blender/make_crowd_atlas.py: columns are poses, top row front view, bottom row back view.
Shader "TennisSim/Crowd"
{
    Properties
    {
        _Atlas ("Atlas", 2D) = "white" {}
        _Mask ("Mask (R shirt, G shorts, B hair)", 2D) = "black" {}
        _Size ("Card size in metres (x across, y up)", Vector) = (1.2, 2.4, 0, 0)
        _Grid ("Atlas columns (poses), rows (views)", Vector) = (5, 2, 0, 0)
        _Cutoff ("Alpha cutoff", Range(0, 1)) = 0.5
        _Brightness ("Brightness", Float) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        TEXTURE2D(_Atlas);
        SAMPLER(sampler_Atlas);
        TEXTURE2D(_Mask);
        SAMPLER(sampler_Mask);

        CBUFFER_START(UnityPerMaterial)
            float4 _Atlas_ST;
            float4 _Mask_ST;
            float4 _Size;
            float4 _Grid;
            float _Cutoff;
            float _Brightness;
        CBUFFER_END

        // 0 = quiet, 1 = everyone on their feet. Set by CrowdController.
        float _CrowdExcitement;

        struct Attributes
        {
            float4 positionOS : POSITION;
            float3 normalOS : NORMAL;
            float2 corner : TEXCOORD0;
            float4 seed : TEXCOORD1;
        };

        // Atlas columns: 0 sitting, 1 leaning forward, 2 and 3 clapping, 4 standing with arms up.
        float Pose(float4 seed)
        {
            float excitement = _CrowdExcitement;
            if (excitement > seed.w)
            {
                if (seed.w < excitement * 0.3) return 4.0; // The keenest get up.
                return 2.0 + step(0.5, frac(_Time.y * 2.6 + seed.z * 7.0));
            }
            return seed.x < 0.7 ? 0.0 : 1.0;
        }

        float3 Billboard(Attributes input, out float3 pivotWS, out float3 facingWS, out float2 uv)
        {
            pivotWS = TransformObjectToWorld(input.positionOS.xyz);
            facingWS = TransformObjectToWorldDir(input.normalOS);

            // The view matrix's first row is the camera's right: every card stays parallel to the screen.
            float3 right = UNITY_MATRIX_V[0].xyz;
            right.y = 0.0;
            right = normalize(right + float3(1e-5, 0.0, 0.0));
            float3 positionWS = pivotWS + right * (input.corner.x * _Size.x) + float3(0.0, input.corner.y * _Size.y, 0.0);

            float3 toCamera = _WorldSpaceCameraPos - pivotWS;
            float front = dot(facingWS.xz, toCamera.xz) >= 0.0 ? 1.0 : 0.0;
            float pose = Pose(input.seed);
            uv = float2((pose + input.corner.x + 0.5) / _Grid.x, (front + input.corner.y) / _Grid.y);
            return positionWS;
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile_fragment _ _SHADOWS_SOFT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 seed : TEXCOORD1;
                float3 pivotWS : TEXCOORD2;
                float3 facingWS : TEXCOORD3;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 pivot, facing;
                float2 uv;
                float3 positionWS = Billboard(input, pivot, facing, uv);
                output.positionCS = TransformWorldToHClip(positionWS);
                output.uv = uv;
                output.seed = input.seed;
                output.pivotWS = pivot;
                output.facingWS = facing;
                return output;
            }

            float3 Hue(float h)
            {
                return saturate(abs(frac(h + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
            }

            // A real crowd is mostly white, dark and blue, with muted colours between.
            float3 ShirtColour(float s)
            {
                if (s < 0.2) return float3(0.93, 0.93, 0.91);
                if (s < 0.32) return float3(0.12, 0.12, 0.13);
                if (s < 0.42) return float3(0.12, 0.2, 0.42);
                if (s < 0.5) return float3(0.5, 0.52, 0.54);
                return lerp(float3(0.5, 0.5, 0.5), Hue(frac(s * 5.31)), 0.42) * 0.9;
            }

            float3 ShortsColour(float s)
            {
                float k = frac(s * 13.7);
                if (k < 0.35) return float3(0.12, 0.16, 0.3);
                if (k < 0.6) return float3(0.1, 0.1, 0.11);
                if (k < 0.8) return float3(0.5, 0.45, 0.34);
                return float3(0.8, 0.8, 0.77);
            }

            float3 HairColour(float s)
            {
                float k = frac(s * 29.3);
                if (k < 0.35) return float3(0.25, 0.17, 0.11);
                if (k < 0.6) return float3(0.07, 0.06, 0.06);
                if (k < 0.75) return float3(0.75, 0.6, 0.38);
                if (k < 0.88) return float3(0.62, 0.62, 0.6);
                return float3(0.5, 0.22, 0.1);
            }

            // Lighter or darker skin, from one per-spectator number.
            float3 SkinTone(float s)
            {
                float k = frac(s * 17.9);
                if (k < 0.3) return float3(1.18, 1.12, 1.08);
                if (k < 0.65) return float3(1.0, 1.0, 1.0);
                if (k < 0.85) return float3(0.78, 0.72, 0.66);
                return float3(0.52, 0.45, 0.4);
            }

            half4 frag(Varyings input) : SV_Target
            {
                half4 albedo = SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, input.uv);
                clip(albedo.a - _Cutoff);
                half3 mask = SAMPLE_TEXTURE2D(_Mask, sampler_Mask, input.uv).rgb;

                // The atlas kit is white, so its brightness carries the folds and shading into the new colour.
                float shade = dot(albedo.rgb, float3(0.3, 0.59, 0.11)) * 1.15;
                float3 colour = albedo.rgb;

                // Skin is whatever warm colour is neither clothes nor hair.
                float warm = saturate((albedo.r - albedo.b) * 4.0 - 0.2);
                float skin = warm * saturate(1.0 - mask.r - mask.g - mask.b);
                colour *= lerp(float3(1.0, 1.0, 1.0), SkinTone(input.seed.x), skin);

                colour = lerp(colour, shade * 2.2 * HairColour(input.seed.w), mask.b);
                colour = lerp(colour, shade * ShirtColour(input.seed.y), mask.r);
                colour = lerp(colour, shade * ShortsColour(input.seed.z), mask.g);

                // One shadow sample per spectator, at chest height: stands shade whole rows.
                float4 shadowCoord = TransformWorldToShadowCoord(input.pivotWS + float3(0.0, 1.0, 0.0));
                Light sun = GetMainLight(shadowCoord);
                float towardsSun = saturate(dot(normalize(input.facingWS), sun.direction) * 0.5 + 0.5);
                float3 light = SampleSH(float3(0.0, 1.0, 0.0)) + sun.color * sun.shadowAttenuation * (0.35 + 0.65 * towardsSun);
                return half4(colour * light * _Brightness, 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                float3 pivot, facing;
                float2 uv;
                output.positionCS = TransformWorldToHClip(Billboard(input, pivot, facing, uv));
                output.uv = uv;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                clip(SAMPLE_TEXTURE2D(_Atlas, sampler_Atlas, input.uv).a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
