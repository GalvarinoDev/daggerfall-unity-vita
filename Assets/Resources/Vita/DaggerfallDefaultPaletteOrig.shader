// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// PS Vita port: Daggerfall/Default for palette textures (Assets/Scripts/Vita/VitaPalette.cs). _MainTex holds the original 8-bit
// palette indices (Alpha8), _PaletteTex the 256 colours: exact original colours at a quarter of ARGB32's memory. Point filtering only.
// Identical to Daggerfall/Default (Assets/Shaders) except the albedo lookup and the point lights: Unity's up to 4 vertex lights (all
// lights at pixelLightCount 0) are evaluated per pixel in the base pass instead of per vertex, so torches light DFU's big wall quads as
// PC's per-pixel lights do, without a forward-add pass (another draw call) per light.
Shader "Daggerfall/Vita/DefaultPalette Orig" {
    Properties {
        _Color("Color", Color) = (1,1,1,1)
        _SpecColor("Spec color", color) = (0.5,0.5,0.5,0.5)
        _MainTex("Palette Index Map", 2D) = "white" {}
        _PaletteTex("Palette", 2D) = "white" {}
        _BumpMap("Normal Map", 2D) = "bump" {}
        _EmissionMap("Emission Map", 2D) = "white" {}
        _EmissionColor("Emission Color", Color) = (0,0,0)
        _ParallaxMap("Parallax Map (R)", 2D) = "black" {}
        _Parallax("Parallax Scale", Range (0.005, 0.08)) = 0.05
        _MetallicGlossMap("Metallic Map (R)", 2D) = "black" {}
        _Smoothness("Smoothness", Range (0, 1)) = 0
    }
    SubShader {
        Tags { "RenderType" = "Opaque" }
        LOD 200

        CGPROGRAM
        #pragma target 3.0
        // As AtlasPalette: DFU bakes no lightmaps, and the near-light inputs need those interpolators (max 10)
        #pragma surface surf BlinnPhong vertex:vert nolightmap nodynlightmap nodirlightmap nometa noinstancing
        #pragma multi_compile __ VITA_NEAR_LIGHTS
        #pragma multi_compile_local __ _NORMALMAP
        #pragma multi_compile_local __ _EMISSION
        #pragma multi_compile_local __ _PARALLAXMAP
        #pragma multi_compile_local __ _METALLICGLOSSMAP

        half4 _Color;
        sampler2D _MainTex;
        sampler2D _PaletteTex;
        #include "VitaPalette.cginc"
        #ifdef _NORMALMAP
    	    sampler2D _BumpMap;
        #endif
        #ifdef _EMISSION
            sampler2D _EmissionMap;
            half4 _EmissionColor;
        #endif
        #ifdef _PARALLAXMAP
            sampler2D _ParallaxMap;
            float _Parallax;
        #endif
        #ifdef _METALLICGLOSSMAP
            sampler2D _MetallicGlossMap;
            float _Smoothness;
        #endif

    	struct Input {
            float2 uv_MainTex;
            #ifdef _PARALLAXMAP
                float3 viewDir;
            #endif
            half3 vitaLight0, vitaLight1;   // pixel to each near light / its range, from vert (linear, so exact)
    	};

        // The generated vertex function adds the 4 vertex lights through Shade4PointLights: switch that off (this snippet comes before
        // it) and light per pixel below with the same maths, plus a fade to 0 at the light's range as Unity's pixel light falloff has
        // The 2 lights nearest the camera, set every frame by VitaLights.cs. Unity's own 4 vertex lights are picked per object, and a dungeon
        // block is one combined mesh, so most torches in a block never lit anything
        float4 _VitaLightPos0, _VitaLightPos1;     // xyz position, w 1 / range
        half4 _VitaLightColor0, _VitaLightColor1;
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
            #ifdef VITA_NEAR_LIGHTS
                float3 pos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.vitaLight0 = (_VitaLightPos0.xyz - pos) * _VitaLightPos0.w;
                o.vitaLight1 = (_VitaLightPos1.xyz - pos) * _VitaLightPos1.w;
            #endif
        }

    	void surf (Input IN, inout SurfaceOutput o)
    	{
            // Get parallax offset
            #ifdef _PARALLAXMAP
                half height = tex2D(_ParallaxMap, IN.uv_MainTex).r;
                IN.uv_MainTex += ParallaxOffset(height, _Parallax, IN.viewDir);
            #endif

            // Albedo (colour) map
            // neighbouring palette ramps), then sample the centre of palette texel k
            half4 albedo = tex2D(_PaletteTex, float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5)) * _Color;

            // Normal map
            #ifdef _NORMALMAP
                o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_MainTex));
            #endif

            // Emission map
            #ifdef _EMISSION
                half3 emission = tex2D(_EmissionMap, IN.uv_MainTex).rgb * _EmissionColor;
                o.Albedo = albedo.rgb - emission; // Emission cancels out other lights
                o.Emission = emission;
            #else
                o.Albedo = albedo.rgb;
            #endif

            // Very rough approximation of metallic map using gloss and specular
            #ifdef _METALLICGLOSSMAP
                half4 metallicMap = tex2D(_MetallicGlossMap, IN.uv_MainTex);
                o.Gloss = 1 - metallicMap.r;
                o.Specular = _Smoothness;
            #endif

            // Vertex lights per pixel (base pass only: forward-add passes light one pixel light each)
            // Unity's surface analysis defines no pass macro: without SHADER_TARGET_SURFACE_ANALYSIS it saw the light inputs unused, dropped them,
            // and the lights got position 0
            // Not with normal/height maps (only loose mod files give palette textures those): their variants have no interpolators left
            #if (defined(SHADER_TARGET_SURFACE_ANALYSIS) || (defined(UNITY_PASS_FORWARDBASE) && defined(VITA_NEAR_LIGHTS))) && !defined(_NORMALMAP) && !defined(_PARALLAXMAP)
                o.Emission += o.Albedo * VitaNearLights(IN.vitaLight0, IN.vitaLight1);
            #endif

            // Assign alpha
            o.Alpha = albedo.a;
    	}
    	ENDCG
    } 
	FallBack "Diffuse"
}