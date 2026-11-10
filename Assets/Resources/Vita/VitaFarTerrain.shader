// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
//
// Distant terrain light: the far mesh of VitaFarTerrain.cs, drawn by the main camera after the
// skybox. Per vertex only: Lambert sun + ambient on the baked ground colour, then the fog densities the reference port installs (DT 3.69
// fcolor: blend = exp2(-density * distance), linear: (end - d) / (end - start + 1)) towards the fog colour. Each vertex is moved along its
// own view ray to an eye depth just inside the far plane (0.90-0.999 of it, same order as the true depth), so its screen position is
// exact, everything nearer covers it, and no second camera or bigger far plane is needed.

Shader "Daggerfall/Vita/FarTerrain"
{
    Properties
    {
        _VitaFarZTest("ZTest", Float) = 4
    }
    SubShader
    {
        Tags { "Queue" = "Geometry+501" "RenderType" = "Opaque" "IgnoreProjector" = "True" }
        Pass
        {
            ZWrite On
            ZTest [_VitaFarZTest]
            Cull Back

            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _VitaFarSunDir;      // xyz: towards the sun (world)
            half4 _VitaFarSunColor;     // light colour * intensity, 0 at night
            half4 _VitaFarAmbient;
            half4 _VitaFarFogColor;
            float4 _VitaFarFog;         // x: exp2 density per metre, y: exp2 density squared, z/w: linear fog or edge fade a + b * d
            half4 _VitaFarWater;        // the reference's water colour; drawn where the vertex wet sign (uv.x) interpolates below 0
            float4 _VitaFarDepth;       // x: eye depth for the nearest far vertex, y: extra depth range, z: 1 / depth falloff (m)

            struct v2f
            {
                float4 pos : SV_POSITION;
                half4 color : COLOR;        // lit, fogged land
                half4 water : TEXCOORD0;    // lit, fogged water; w: wet sign (-1 water, +1 land)
            };

            v2f vert(appdata_full v)
            {
                v2f o;
                float3 w = mul(unity_ObjectToWorld, v.vertex).xyz;
                float3 d = w - _WorldSpaceCameraPos;
                float dist = length(d);
                float eye = -mul(UNITY_MATRIX_V, float4(w, 1)).z;
                float pulled = _VitaFarDepth.x + _VitaFarDepth.y * (1.0 - exp2(-max(abs(eye), 1.0) * _VitaFarDepth.z));
                // Only the depth is pulled in: the real position keeps x, y, w, so the GPU clips a triangle that reaches behind the camera
                // correctly. Moving the vertex along its ray instead (same screen point in front, but mirrored behind) drew such side
                // triangles as a long sliver across the sky (seen near Privateer's Hold).
                o.pos = mul(UNITY_MATRIX_VP, float4(w, 1));
                float4 pc = mul(UNITY_MATRIX_P, float4(0, 0, -pulled, 1));
                o.pos.z = pc.z / pc.w * o.pos.w;

                half ndl = saturate(dot((half3)v.normal, (half3)_VitaFarSunDir.xyz));
                half3 c = v.color.rgb * (_VitaFarAmbient.rgb + _VitaFarSunColor.rgb * ndl);
                float fog = saturate(_VitaFarFog.z + _VitaFarFog.w * dist) * exp2(-dist * (_VitaFarFog.x + _VitaFarFog.y * dist));
                o.color = half4(lerp(_VitaFarFogColor.rgb, c, (half)fog), 1);
                half3 cw = _VitaFarWater.rgb * (_VitaFarAmbient.rgb + _VitaFarSunColor.rgb * saturate((half)_VitaFarSunDir.y));
                o.water = half4(lerp(_VitaFarFogColor.rgb, cw, (half)fog), v.texcoord.x);
                return o;
            }

            half4 frag(v2f i) : SV_Target
            {
                return i.water.w < 0 ? half4(i.water.rgb, 1) : i.color;     // select, no branch (parallel mode)
            }
            ENDCG
        }
    }
}
