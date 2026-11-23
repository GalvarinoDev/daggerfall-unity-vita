// PS Vita port: rain and snow replayed in the vertex shader (VitaWeatherMesh). Each quad is one particle of the player's rain or snow
// system after another: a new random spot in the emitter box each cycle, motion from rest under the system's force (speed clamped for
// snow), size over lifetime 0.3 -> 1, Unity's max screen size clamp, rain as a billboard stretched along its velocity, then a short
// splash at the ground plane. Colour and blend as Particles/Alpha Blended Premultiply (the systems' material), particle colour white.
Shader "Daggerfall/Vita/Weather" {
    Properties {
        _MainTex("Particle Texture", 2D) = "white" {}
        _Box("Box x, z, ground y, splash time", Vector) = (60, 60, -50, 0.15)
        _Motion("Speed clamp, lifetime, stretch, max screen size", Vector) = (10000, 5, 4, 0.001)
        _Fade("Fade start, 1 / fade time", Vector) = (10000, 1, 0, 0)
        _Splash("Splash size, rise", Vector) = (0.1, 0.5, 0, 0)
    }
    SubShader {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Blend One OneMinusSrcAlpha
        ColorMask RGB
        Cull Off Lighting Off ZWrite Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _Box, _Motion, _Fade, _Splash;
            // uv0: quad corner; uv1: phase (0-1), cycle (s), spot seeds x, z; uv2: acceleration (local, m/s^2), start size;
            // uv3: ground hit time (s, 1e4 = never), start height
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 seed : TEXCOORD1; float4 accel : TEXCOORD2; float2 hit : TEXCOORD3; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half alpha : TEXCOORD1; UNITY_FOG_COORDS(2) };
            v2f vert(appdata v)
            {
                v2f o;
                float u = _Time.y / v.seed.y + v.seed.x;
                float cycle = fmod(floor(u), 4096.0);
                float t = frac(u) * v.seed.y;
                float2 spot = frac(cycle * float2(0.6180339, 0.7548777) + v.seed.zw);
                float3 a = v.accel.xyz;
                float len = length(a);
                float3 dir = a / len;
                float tc = _Motion.x / len;
                float te = min(t, v.hit.x);
                float t1 = min(te, tc);
                float3 p = float3((spot.x - 0.5) * _Box.x, v.hit.y, (spot.y - 0.5) * _Box.y) + 0.5 * a * t1 * t1 + dir * (_Motion.x * max(te - tc, 0));
                float3 vel = te < tc ? a * te : dir * _Motion.x;
                float age = t - v.hit.x;
                float splashing = age > 0 ? 1 : 0;
                p.y = splashing > 0 ? _Box.z + _Splash.y * age : p.y;
                float size = splashing > 0 ? _Splash.x : v.accel.w * (0.3006329 + 0.6993671 * t / _Motion.y);
                size = age > _Box.w ? 0 : size;
                float4 view = mul(UNITY_MATRIX_MV, float4(p, 1));
                size = min(size, _Motion.w * 2 * -view.z / UNITY_MATRIX_P[1][1]);
                float2 c = v.uv - 0.5;
                float2 d = mul((float3x3)UNITY_MATRIX_MV, vel).xy;
                d = dot(d, d) > 1e-6 ? normalize(d) : float2(0, 1);
                float stretch = splashing > 0 ? 0 : _Motion.z;
                view.xy += stretch > 0 ? (d * (c.y * stretch) + float2(d.y, -d.x) * c.x) * size : c * size;
                o.pos = mul(UNITY_MATRIX_P, view);
                o.uv = v.uv;
                o.alpha = 1 - saturate((t - _Fade.x) * _Fade.y);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            half4 frag(v2f i) : SV_Target
            {
                // Premultiply: tex x colour x colour alpha, colour = (1, 1, 1, alpha)
                half4 col = tex2D(_MainTex, i.uv) * half4(i.alpha, i.alpha, i.alpha, i.alpha * i.alpha);
                UNITY_APPLY_FOG_COLOR(i.fogCoord, col, half4(0, 0, 0, 0));
                return col;
            }
            ENDCG
        }
    }
}
