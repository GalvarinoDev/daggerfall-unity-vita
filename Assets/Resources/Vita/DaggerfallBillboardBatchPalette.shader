// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// PS Vita port: Daggerfall/BillboardBatch for 8-bit palette sprite atlases (Assets/Scripts/Vita/VitaPalette.cs). _MainTex holds the original
// palette indices (Alpha8), _PaletteTex the 256 colours (alpha index transparent). Point filtering only.
// Identical to Daggerfall/BillboardBatch (Assets/Shaders) except the albedo lookup and the emission map (an Alpha8 mask, read with the main UV), and no normal map.
Shader "Daggerfall/Vita/BillboardBatchPalette" {
	// Efficient transparent-cutout styled billboard batch.
	// NOTES:
	//  - Use this shader for best billboard performance.
	//  - Does not work with VertexLit path.
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
		Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
		LOD 200
		
		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf Lambert alphatest:_Cutoff vertex:vert addshadow noambient
		#pragma multi_compile __ VITA_TOWN_LIGHTS
		#pragma multi_compile __ VITA_NEAR_TREES

		half4 _Color;
		sampler2D _MainTex;
		sampler2D _PaletteTex;
		#include "VitaPalette.cginc"
		sampler2D _BumpMap;
		sampler2D _EmissionMap;
		half4 _EmissionColor;
		float3 _UpVector;
		// VitaNearTrees: billboards inside this world xz rect (xmin, zmin, xmax, zmax) collapse to a point, the 3D trees stand there
		float4 _VitaNearTreeRect;

		// One UV: the emission mask shares the atlas layout, and the Vita atlases have no normal map (o.Normal from a flat default bump
		// map cost a texture read plus tangent-space lighting per pixel for the same result; trees were ~19 ms of the exterior frame)
		// Vita: ambient + fill lights per vertex (VitaFill.cginc): Unity's SH cost 16 cycles per pixel here (shaderaudit)
		#include "VitaFill.cginc"
		struct Input {
			float2 uv_MainTex;
			half3 vitaFill;   // VitaFill / VitaFillVertexLights, from vert
		};

		void vert (inout appdata_full v, out Input o)
		{
			UNITY_INITIALIZE_OUTPUT(Input, o);
			// Direction we are viewing the billboard from
			float3 viewDirection = UNITY_MATRIX_V._m02_m12_m22;
			float3 rightVector = normalize(cross(viewDirection, _UpVector));

			// Transform billboard normal for lighting support
			// Comment out this line to stop light changing as billboards rotate
			v.normal = mul((float3x3)UNITY_MATRIX_V, v.normal);

			#if defined(VITA_NEAR_TREES)
			float3 origin = mul(unity_ObjectToWorld, v.vertex).xyz;
			float2 inside = step(_VitaNearTreeRect.xy, origin.xz) * step(origin.xz, _VitaNearTreeRect.zw);
			v.tangent.xy *= 1.0 - inside.x * inside.y;
			#endif
			// Offset vertices based on corners scaled by size
			v.vertex.xyz += rightVector * (v.tangent.z - 0.5) * v.tangent.x;
			v.vertex.xyz += _UpVector * (v.tangent.w - 0.5) * v.tangent.y;
			o.vitaFill = VitaFillVertexLights(mul(unity_ObjectToWorld, v.vertex).xyz, UnityObjectToWorldNormal(v.normal));
		}

		void surf (Input IN, inout SurfaceOutput o)
		{
			// index k is stored as k/255: round back to k, then sample the centre of palette texel k (as DefaultPalette)
			half4 albedo = tex2D(_PaletteTex, float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5)) * _Color;
			half3 emission = albedo.rgb * tex2D(_EmissionMap, IN.uv_MainTex).a * _EmissionColor; // Alpha8 mask of the glowing frames (TextureReader.TryPackAtlasOnCpu)
			o.Albedo = albedo.rgb - emission; // Emission cancels out other lights
			o.Emission = emission;
			o.Emission += o.Albedo * IN.vitaFill;   // after the emission (amb2 had it before: no ambient on trees)
			o.Alpha = albedo.a;
		}
		ENDCG
	} 
	FallBack "Transparent/VertexLit"
}