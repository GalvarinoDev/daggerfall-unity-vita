// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// PS Vita port: Daggerfall/BillboardBatchNoShadows for 8-bit palette sprite atlases (Assets/Scripts/Vita/VitaPalette.cs). _MainTex holds the original
// palette indices (Alpha8), _PaletteTex the 256 colours (alpha index transparent). Point filtering only.
// Identical to Daggerfall/BillboardBatchNoShadows (Assets/Shaders) except the albedo lookup and the emission map (an Alpha8 mask, read with the main UV), and no normal map.
Shader "Daggerfall/Vita/BillboardBatchNoShadowsPalette Orig" {
	// Efficient transparent-cutout styled billboard batch.
	// NOTES:
	//  - Use this shader for best billboard performance.
	//  - Does not work with VertexLit path.
    //  - Does not cast shadows.
	Properties {
		_Color("Color", Color) = (1,1,1,1)
		_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
		_MainTex("Palette Index Map", 2D) = "white" {}
		_PaletteTex("Palette", 2D) = "white" {}
		_BumpMap("Normal Map", 2D) = "bump" {}
		_EmissionMap("Emission Map", 2D) = "white" {}
		_EmissionColor("Emission Color", Color) = (0,0,0)
		_UpVector ("Up Vector (XYZ)", Vector) = (0,1,0,0)
	}
	SubShader {
		Tags { "IgnoreProjector" = "True" "RenderType" = "Transparent" }
		LOD 200
		
		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf Lambert alphatest:_Cutoff vertex:vert

		half4 _Color;
		sampler2D _MainTex;
		sampler2D _PaletteTex;
		#include "VitaPalette.cginc"
		sampler2D _BumpMap;
		sampler2D _EmissionMap;
		half4 _EmissionColor;
		float3 _UpVector;

		// One UV: the emission mask shares the atlas layout, and the Vita atlases have no normal map (o.Normal from a flat default bump
		struct Input {
			float2 uv_MainTex;
		};

		void vert (inout appdata_full v)
		{
			// Direction we are viewing the billboard from
			float3 viewDirection = UNITY_MATRIX_V._m02_m12_m22;
			float3 rightVector = normalize(cross(viewDirection, _UpVector));

			// Transform billboard normal for lighting support
			// Comment out this line to stop light changing as billboards rotate
			v.normal = mul((float3x3)UNITY_MATRIX_V, v.normal);

			// Offset vertices based on corners scaled by size
			v.vertex.xyz += rightVector * (v.tangent.z - 0.5) * v.tangent.x;
			v.vertex.xyz += _UpVector * (v.tangent.w - 0.5) * v.tangent.y;
		}

		void surf (Input IN, inout SurfaceOutput o)
		{
			// index k is stored as k/255: round back to k, then sample the centre of palette texel k (as DefaultPalette)
			half4 albedo = tex2D(_PaletteTex, float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5)) * _Color;
			half3 emission = albedo.rgb * tex2D(_EmissionMap, IN.uv_MainTex).a * _EmissionColor; // Alpha8 mask of the glowing frames (TextureReader.TryPackAtlasOnCpu)
			o.Albedo = albedo.rgb - emission; // Emission cancels out other lights
			o.Alpha = albedo.a;
			o.Emission = emission;
		}
		ENDCG
	} 
	FallBack "Diffuse"
}