// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// PS Vita port: Daggerfall/Vita/DefaultPalette for a dungeon's texture atlas (Assets/Scripts/Vita/VitaAtlas.cs), so a model with many
// textures is one draw. Each vertex carries its texture's rect in the atlas (uv2: x, y, width, height in atlas UV); the model's own UV
// still runs over the whole tiled texture, so the shader wraps it into the rect itself (frac), and picks the same whole mip level as
// VitaPaletteIndex would for the separate texture. Mips stop at VITA_ATLAS_MAX_LOD: each texture has a wrapped border of
// 2^VITA_ATLAS_MAX_LOD texels in the atlas, which is one texel at that level. Lighting is DefaultPalette's (no keyword variants:
// VitaAtlas only takes plain textures).
Shader "Daggerfall/Vita/AtlasPalette" {
    Properties {
        _Color("Color", Color) = (1,1,1,1)
        _SpecColor("Spec color", color) = (0.5,0.5,0.5,0.5)
        _MainTex("Palette Index Atlas", 2D) = "white" {}
        _PaletteTex("Palette", 2D) = "white" {}
    }
    SubShader {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf BlinnPhong vertex:vert nolightmap nodynlightmap nodirlightmap nometa noinstancing exclude_path:deferred exclude_path:prepass noambient
        #pragma multi_compile __ VITA_NEAR_LIGHTS
        #pragma multi_compile __ VITA_TOWN_LIGHTS

        #define VITA_ATLAS_MAX_LOD 4.0     // VitaAtlas.MaxLod

        half4 _Color;
        sampler2D _MainTex;
        sampler2D _PaletteTex;
        float4 _MainTex_TexelSize;

    	// Vita: the ambient colour (VitaFill.cginc): Unity's SH cost 16 cycles per pixel here (shaderaudit)
    	#include "VitaFill.cginc"
    	struct Input {
            float2 uv_MainTex;
            float4 atlasRect;
            half3 vitaLight0, vitaLight1;   // pixel to each near light / its range, from vert (linear, so exact)
            float2 vitaTownUV;   // baked town lamps (VitaFill.cginc)
    	};

        // As DefaultPalette: 2 near lights per pixel instead of per vertex
        // The 2 lights nearest the camera, set every frame by VitaLights.cs. Unity's own 4 vertex lights are picked per object, and a dungeon
        // block is one combined mesh, so most torches in a block never lit anything. Globals in VitaFill.cginc
        // Unity's 4-light falloff 1/(1 + 25 q) with q = (distance / range)^2, faded to 0 at the range. No N.L, 2 lights: per pixel with
        // position and face normal for 4 lights cost 78 vs 20 cycles (psp2shaderperf) and took the dungeon 55 -> 26 FPS
        half3 VitaNearLights(half3 l0, half3 l1)
        {
            half2 q = half2(dot(l0, l0), dot(l1, l1));
            half2 diff = saturate((1.0h - q) * 5.0h) / (1.0h + 25.0h * q);
            return _VitaLightColor0.rgb * diff.x + _VitaLightColor1.rgb * diff.y;
        }
        #define Shade4PointLights(a, b, c, d, e, f, g, h, i, j) float3(0,0,0)

        void vert(inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            float3 pos = mul(unity_ObjectToWorld, v.vertex).xyz;
            #ifdef VITA_TOWN_LIGHTS
                o.vitaTownUV = VitaTownUV(pos);
            #endif
            #ifdef VITA_NEAR_LIGHTS
                o.vitaLight0 = (_VitaLightPos0.xyz - pos) * _VitaLightPos0.w;
                o.vitaLight1 = (_VitaLightPos1.xyz - pos) * _VitaLightPos1.w;
            #endif
            o.atlasRect = v.texcoord1;
        }

    	void surf (Input IN, inout SurfaceOutput o)
    	{
            // Mip level from the texels of the texture itself (rect size x atlas size), rounded to a whole level (VitaPalette.cginc)
            float2 texels = IN.uv_MainTex * (IN.atlasRect.zw * _MainTex_TexelSize.zw);
            float2 dx = ddx(texels), dy = ddy(texels);
            float lod = clamp(floor(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + 0.5), 0.0, VITA_ATLAS_MAX_LOD);
            float2 uv = IN.atlasRect.xy + frac(IN.uv_MainTex) * IN.atlasRect.zw;
            float paletteIndex = tex2Dlod(_MainTex, float4(uv, 0.0, lod)).a;
            half4 albedo = tex2D(_PaletteTex, float2((floor(paletteIndex * 255.0 + 0.5) + 0.5) / 256.0, 0.5)) * _Color;
            o.Albedo = albedo.rgb;

            // Unity's surface analysis defines no pass macro: without SHADER_TARGET_SURFACE_ANALYSIS it saw the light inputs unused, dropped them,
            // and the lights got position 0 (no torch light at all)
            #if defined(SHADER_TARGET_SURFACE_ANALYSIS) || (defined(UNITY_PASS_FORWARDBASE) && defined(VITA_NEAR_LIGHTS))
                o.Emission += o.Albedo * VitaNearLights(IN.vitaLight0, IN.vitaLight1);
            #endif

            #if (defined(SHADER_TARGET_SURFACE_ANALYSIS) || (defined(UNITY_PASS_FORWARDBASE) && defined(VITA_TOWN_LIGHTS)))
                o.Emission += o.Albedo * (tex2D(_VitaTownLightTex, IN.vitaTownUV).rgb * _VitaTownLightScale);
            #endif
            o.Emission += o.Albedo * _VitaAmbient.rgb;
            o.Alpha = albedo.a;
    	}
    	ENDCG
    } 
	FallBack "Diffuse"
}
