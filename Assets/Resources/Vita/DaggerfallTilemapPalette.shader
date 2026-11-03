// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// PS Vita port: Daggerfall/Tilemap's Vita branch, trimmed.
// Same output: tile index from the point-filtered tilemap, one whole mip level of the palette-index atlas, palette lookup, Lambert.
// Trimmed: no _BumpMap varying (Unity's surface analysis ignores #if inside Input, so only a separate shader drops it), no
// forward-add/lightmap/meta/deferred passes, and the layout values are literals so the compiler folds the uniform maths (uniform-only
// fragment maths runs per pipe per tile on the Vita). TerrainMaterialProvider still sets the same properties; it sets these values.
Shader "Daggerfall/Vita/TilemapPalette" {
	Properties {
		[HideInInspector] _MainTex("BaseMap (RGB)", 2D) = "white" {}
		[HideInInspector] _Control ("Control (RGBA)", 2D) = "red" {}
		[HideInInspector] _SplatTex3("Layer 3 (A)", 2D) = "white" {}
		[HideInInspector] _SplatTex2("Layer 2 (B)", 2D) = "white" {}
		[HideInInspector] _SplatTex1("Layer 1 (G)", 2D) = "white" {}
		[HideInInspector] _SplatTex0("Layer 0 (R)", 2D) = "white" {}
		_TileAtlasTex ("Tileset Atlas (palette indices)", 2D) = "white" {}
		_PaletteTex ("Palette", 2D) = "white" {}
		_TilemapTex("Tilemap (R)", 2D) = "red" {}
	}
	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 200

		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf Lambert noforwardadd nolightmap nodynlightmap nodirlightmap nometa noinstancing exclude_path:deferred exclude_path:prepass vertex:vert noambient
		#pragma multi_compile __ VITA_PIXEL_TERRAIN_LIGHTS
		#pragma multi_compile __ VITA_TOWN_LIGHTS

		// TextureReader.GetTerrainTilesetTexture's layout (TerrainMaterialProvider sets the same values) and MapsFile.WorldMapTileDim
		#define TILESET_DIM 16.0
		#define TILEMAP_DIM 128.0
		#define MAX_INDEX 255.0
		#define ATLAS_SIZE 2048.0
		#define GUTTER_SIZE 32.0

		sampler2D _TileAtlasTex;
		sampler2D _PaletteTex;
		sampler2D _TilemapTex;

		// Vita: ambient + fill lights per vertex (VitaFill.cginc): Unity's SH cost 16 cycles per pixel here (shaderaudit).
		// VITA_PIXEL_TERRAIN_LIGHTS (VitaLights: a small light such as the player's torch, range 6 m, is in slot 0-1): those two per pixel
		// as DefaultPalette's VitaNearLights (no N.L), since per vertex (6.4 m apart) the torch pool jumped as you walked
		#ifdef VITA_PIXEL_TERRAIN_LIGHTS
			#define VITA_FILL_SKIP_01
		#endif
		#define VITA_TOWN_PER_PIXEL
		#include "VitaFill.cginc"
		struct Input
		{
			float2 uv_MainTex;
			half3 vitaFill;   // VitaFill / VitaFillVertexLights, from vert
			half3 vitaLight0, vitaLight1;   // pixel to slot 0-1 light / its range, from vert (linear, so exact)
			float2 vitaTownUV;   // baked town lamps (VitaFill.cginc)
		};

		void vert (inout appdata_full v, out Input o)
		{
			UNITY_INITIALIZE_OUTPUT(Input, o);
			float3 pos = mul(unity_ObjectToWorld, v.vertex).xyz;
			o.vitaFill = VitaFillVertexLights(pos, UnityObjectToWorldNormal(v.normal));
			#ifdef VITA_PIXEL_TERRAIN_LIGHTS
				o.vitaLight0 = (_VitaLightPos0.xyz - pos) * _VitaLightPos0.w;
				o.vitaLight1 = (_VitaLightPos1.xyz - pos) * _VitaLightPos1.w;
			#endif
			#ifdef VITA_TOWN_LIGHTS
				o.vitaTownUV = VitaTownUV(pos);
			#endif
		}

		void surf (Input IN, inout SurfaceOutput o)
		{
			float2 unwrappedUV = IN.uv_MainTex * TILEMAP_DIM;

			// Point-filtered without mips: sampling at the varying picks the same texel as floor(unwrappedUV) (non-dependent read)
			float index = floor(tex2D(_TilemapTex, IN.uv_MainTex).a * MAX_INDEX + 0.5);
			float ypos = floor((index + 0.5) * (1.0 / TILESET_DIM));
			float xpos = index - ypos * TILESET_DIM;
			float2 uv = float2(xpos, ypos) * (1.0 / TILESET_DIM) + frac(unwrappedUV) * (1.0 / GUTTER_SIZE) + GUTTER_SIZE / ATLAS_SIZE;

			// One whole mip level from the gradients in atlas texels (blended palette indices are wrong colours):
			// 0.5 * log2(|d|^2 * (ATLAS_SIZE / GUTTER_SIZE)^2) = 0.5 * log2(|d|^2) + log2(ATLAS_SIZE / GUTTER_SIZE)
			float2 dx = ddx(unwrappedUV), dy = ddy(unwrappedUV);
			float lod = max(0.0, floor(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + (log2(ATLAS_SIZE / GUTTER_SIZE) + 0.5)));
			float paletteIndex = tex2Dlod(_TileAtlasTex, float4(uv, 0, lod)).a;
			half4 c = tex2D(_PaletteTex, float2((floor(paletteIndex * 255.0 + 0.5) + 0.5) * (1.0 / 256.0), 0.5));
			o.Albedo = c.rgb;
			half3 fill = IN.vitaFill;
			#if defined(SHADER_TARGET_SURFACE_ANALYSIS) || defined(VITA_PIXEL_TERRAIN_LIGHTS)
				half2 q = half2(dot(IN.vitaLight0, IN.vitaLight0), dot(IN.vitaLight1, IN.vitaLight1));
				half2 diff = saturate((1.0h - q) * 5.0h) / (1.0h + 25.0h * q);
				fill += _VitaLightColor0.rgb * diff.x + _VitaLightColor1.rgb * diff.y;   // added in gamma as DefaultPalette does (no pow per pixel)
			#endif
			#if defined(SHADER_TARGET_SURFACE_ANALYSIS) || defined(VITA_TOWN_LIGHTS)
				fill += tex2D(_VitaTownLightTex, IN.vitaTownUV).rgb * _VitaTownLightScale;
			#endif
			o.Emission += o.Albedo * fill;
			o.Alpha = c.a;
		}
		ENDCG
	}
	FallBack "Mobile/VertexLit"
}
