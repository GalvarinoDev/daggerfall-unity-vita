// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

Shader "Daggerfall/Tilemap" {
	Properties {
		// These params are required to stop terrain system throwing errors
		// However we won't be using them as Unity likes to init these textures
		// and will overwrite any assignments we already made
		// TODO: Combine splat painting with tilemapping
		[HideInInspector] _MainTex("BaseMap (RGB)", 2D) = "white" {}
		[HideInInspector] _Control ("Control (RGBA)", 2D) = "red" {}
		[HideInInspector] _SplatTex3("Layer 3 (A)", 2D) = "white" {}
		[HideInInspector] _SplatTex2("Layer 2 (B)", 2D) = "white" {}
		[HideInInspector] _SplatTex1("Layer 1 (G)", 2D) = "white" {}
		[HideInInspector] _SplatTex0("Layer 0 (R)", 2D) = "white" {}

		// These params are used for our shader
		_TileAtlasTex ("Tileset Atlas (RGB)", 2D) = "white" {}
		_TilemapTex("Tilemap (R)", 2D) = "red" {}
		_BumpMap("Normal Map", 2D) = "bump" {}
		_TilesetDim("Tileset Dimension (in tiles)", Int) = 16
		_TilemapDim("Tilemap Dimension (in tiles)", Int) = 128
		_MaxIndex("Max Tileset Index", Int) = 255
		_AtlasSize("Atlas Size (in pixels)", Float) = 2048.0
		_GutterSize("Gutter Size (in pixels)", Float) = 32.0
	}
	SubShader {
		Tags { "RenderType"="Opaque" }
		LOD 200
		
		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf Lambert
		#pragma glsl
		#if defined(SHADER_API_PSP2)
		#define uint int // PS Vita Cg has no uint
		#endif

		sampler2D _TileAtlasTex;
		#if defined(SHADER_API_PSP2)
		sampler2D _PaletteTex;	// Vita: _TileAtlasTex holds 8-bit palette indices (VitaPalette)
		#endif
		sampler2D _TilemapTex;
		sampler2D _BumpMap;
		#if defined(SHADER_API_PSP2)
		// Vita: as floats. Unity sets these through Material.SetInt (stored as floats); read as int uniforms the whole ground came out
		// as one flat colour
		float _TilesetDim;
		float _TilemapDim;
		float _MaxIndex;
		#else
		uint _TilesetDim;
		uint _TilemapDim;
		uint _MaxIndex;
		#endif
		float _AtlasSize;
		float _GutterSize;

		struct Input
		{
			float2 uv_MainTex;
			float2 uv_BumpMap;
		};

		void surf (Input IN, inout SurfaceOutput o)
		{
			float2 unwrappedUV = IN.uv_MainTex * _TilemapDim;

			// Get offset to tile in atlas
		#if defined(SHADER_API_PSP2)
			// The tilemap is point-filtered without mips, so sampling at the varying itself picks the same texel as the floor maths, as a
			// non-dependent read
			float index = floor(tex2D(_TilemapTex, IN.uv_MainTex).a * _MaxIndex + 0.5);
		#else
			uint index = tex2D(_TilemapTex, floor(unwrappedUV) / _TilemapDim).a * _MaxIndex + 0.5;
		#endif
		#if defined(SHADER_API_PSP2)
			// Vita fragment profile has no integer divide/modulo
			float ypos = floor((index + 0.5) / _TilesetDim);
			float xpos = index - ypos * _TilesetDim;
		#else
			uint xpos = index % _TilesetDim;
			uint ypos = index / _TilesetDim;
		#endif
			float2 uv = float2(xpos, ypos) / _TilesetDim;

			// Offset to fragment position inside tile
			float2 offset = frac(unwrappedUV) / _GutterSize;
			uv += offset + _GutterSize / _AtlasSize;

			// Sample based on gradient and set output
			float2 uvr = unwrappedUV / _GutterSize;
		#if defined(SHADER_API_PSP2)
			// Vita: tex2Dgrad sampled the smallest mips; choose the level from the same
			// gradients (in atlas texels), rounded to one whole level (blended palette indices are wrong colours), then look the colour up
			float2 dx = ddx(uvr) * _AtlasSize, dy = ddy(uvr) * _AtlasSize;
			float paletteIndex = tex2Dlod(_TileAtlasTex, float4(uv, 0, max(0.0, floor(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + 0.5)))).a;
			half4 c = tex2D(_PaletteTex, float2((floor(paletteIndex * 255.0 + 0.5) + 0.5) / 256.0, 0.5));
		#else
			half4 c = tex2Dgrad(_TileAtlasTex, uv, ddx(uvr), ddy(uvr));
		#endif
			o.Albedo = c.rgb;
			o.Alpha = c.a;
		#if !defined(SHADER_API_PSP2)
			o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_BumpMap));
		#endif
		}
		ENDCG
	} 
	FallBack "Mobile/VertexLit"
}
