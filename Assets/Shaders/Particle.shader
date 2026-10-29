Shader "Custom/GPUParticles"
{
    Properties
    {
        [HideInInspector] _ParticleSize ("", Float) = 0
        [HideInInspector] _FarParticleColor ("", Color) = (0, 0, 0, 0)
        [HideInInspector] _NearParticleColor ("", Color) = (0, 0, 0, 0)
        [HideInInspector] _VelocityStretch ("", Float) = 0
        [HideInInspector] _GlowColor ("", Color) = (0, 0, 0, 0)
        [HideInInspector] _GlowIntensity ("", Float) = 0
        [HideInInspector] _GlowSharpness ("", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "Transparent"
        }

        Pass
        {
            Blend SrcAlpha OneMinusSrcAlpha
            Cull Off
            ZWrite Off

            HLSLPROGRAM

            #pragma target 4.5
            #pragma vertex vert
            #pragma fragment frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Particle
            {
                float3 position;
                float3 velocity;
            };

            StructuredBuffer<Particle> _Particles;
            StructuredBuffer<float4> _D20Planes;

            float _ParticleSize;
            float4 _FarParticleColor;
            float4 _NearParticleColor;
            float _VelocityStretch;
            float4 _GlowColor;
            float _GlowIntensity;
            float _GlowSharpness;
            float4x4 _D20WorldToLocal;
            uint _D20PlaneCount;

            static const float ColorTransitionDistance = 4.0f;
            static const float2 QuadVertices[6] =
            {
                float2(-1.0f, -1.0f),
                float2( 1.0f, -1.0f),
                float2(-1.0f,  1.0f),
                float2(-1.0f,  1.0f),
                float2( 1.0f, -1.0f),
                float2( 1.0f,  1.0f)
            };

            struct Varyings
            {
                float4 position : SV_POSITION;
                float2 uv : TEXCOORD0;
                half4 color : COLOR0;
            };

            // Approximate the particle's distance to the D20 object's surface.
            float DistanceToD20Surface(float3 worldPosition)
            {
                float3 localPosition = mul(_D20WorldToLocal, float4(worldPosition, 1.0f)).xyz;
                float signedDistance = -1.0e+30f;
                for (uint i = 0u; i < _D20PlaneCount; i++)
                {
                    float4 plane = _D20Planes[i];
                    signedDistance = max(signedDistance, dot(plane.xyz, localPosition) + plane.w);
                }

                return abs(signedDistance);
            }

            // Compute a camera-facing particle vertex with velocity-based stretching and distance-based color.
            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings OUT;

                Particle particle = _Particles[vertexID / 6];

                OUT.position = TransformWorldToHClip(particle.position);

                float2 corner = QuadVertices[vertexID % 6];
                float2 billboardOffset = corner * _ParticleSize;
                float2 viewVelocity = TransformWorldToViewDir(particle.velocity).xy;
                float viewSpeed = length(viewVelocity);

                if (_VelocityStretch > 0.0f && viewSpeed > 0.0001f)
                {
                    float2 motionDirection = viewVelocity / viewSpeed;
                    float2 sideDirection = float2(motionDirection.y, -motionDirection.x);
                    float halfLength = _ParticleSize + viewSpeed * _VelocityStretch * 0.5f;
                    billboardOffset = sideDirection * corner.x * _ParticleSize + motionDirection * corner.y * halfLength;
                }

                OUT.position.xy += billboardOffset * float2(UNITY_MATRIX_P._m00, UNITY_MATRIX_P._m11);
                OUT.uv = corner * 0.5f + 0.5f;
                float nearSurface = saturate(1.0f - DistanceToD20Surface(particle.position) / ColorTransitionDistance);
                OUT.color = lerp(_FarParticleColor, _NearParticleColor, nearSurface);

                return OUT;
            }

            // Compute the particle's final color with glow and a soft circular edge.
            half4 frag(Varyings IN) : SV_Target
            {
                float radius = length(IN.uv - 0.5f) * 2.0f;
                float edge = 1.0f - smoothstep(0.84f, 1.0f, radius);
                float core = pow(saturate(1.0f - radius), max(_GlowSharpness, 0.01f));
                float glow = core * _GlowIntensity;
                float3 color = lerp(IN.color.rgb, _GlowColor.rgb, saturate(glow)) * (1.0f + glow);

                return half4(color, IN.color.a * edge);
            }

            ENDHLSL
        }
    }
}
