// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// PS Vita port: Daggerfall/Tilemap's Vita branch, trimmed (the exterior is GPU bound and terrain was ~13 ms of it).
// Same output: tile index from the point-filtered tilemap, one whole mip level of the palette-index atlas, palette lookup, Lambert.
// Trimmed: no _BumpMap varying (Unity's surface analysis ignores #if inside Input, so only a separate shader drops it), no
// forward-add/lightmap/meta/deferred passes, and the layout values are literals so the compiler folds the uniform maths (uniform-only
// fragment maths runs per pipe per tile on the Vita). TerrainMaterialProvider still sets the same properties; it sets these values.
Shader "Daggerfall/Vita/TilemapPalette Orig" {
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
		#pragma surface surf Lambert noforwardadd nolightmap nodynlightmap nodirlightmap nometa noinstancing exclude_path:deferred exclude_path:prepass

		// TextureReader.GetTerrainTilesetTexture's layout (TerrainMaterialProvider sets the same values) and MapsFile.WorldMapTileDim
		#define TILESET_DIM 16.0
		#define TILEMAP_DIM 128.0
		#define MAX_INDEX 255.0
		#define ATLAS_SIZE 2048.0
		#define GUTTER_SIZE 32.0

		sampler2D _TileAtlasTex;
		sampler2D _PaletteTex;
		sampler2D _TilemapTex;

		struct Input
		{
			float2 uv_MainTex;
		};

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
			// Level 7 is one texel per atlas cell, the tile's own colour. Past it a texel spans neighbouring tiles, so far ridges seen at a grazing angle drew in the water tile's colour.
			float lod = clamp(floor(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + (log2(ATLAS_SIZE / GUTTER_SIZE) + 0.5)), 0.0, log2(ATLAS_SIZE / TILESET_DIM));
			float paletteIndex = tex2Dlod(_TileAtlasTex, float4(uv, 0, lod)).a;
			half4 c = tex2D(_PaletteTex, float2((floor(paletteIndex * 255.0 + 0.5) + 0.5) * (1.0 / 256.0), 0.5));
			o.Albedo = c.rgb;
			o.Alpha = c.a;
		}
		ENDCG
	}
	FallBack "Mobile/VertexLit"
}
