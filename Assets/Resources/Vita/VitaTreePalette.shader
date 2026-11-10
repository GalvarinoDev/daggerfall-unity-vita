// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)

// PS Vita port: the opaque parts (bark) of the Low Poly Trees models near the player (Assets/Scripts/Vita/VitaNearTrees.cs).
// _MainTex holds 8-bit palette indices (Alpha8), _PaletteTex the atlas's own 256 colours (vita/tools/lowpolytrees.py).
// Vertex colour = the material colour (PC applies no terrain tree colour to the mod's SpeedTree materials). Lit like the billboard batches: Lambert + VitaFill per vertex.
Shader "Daggerfall/Vita/TreePalette" {
	Properties {
		_MainTex("Palette Index Map", 2D) = "white" {}
		_PaletteTex("Palette", 2D) = "white" {}
	}
	SubShader {
		Tags { "RenderType" = "Opaque" }
		LOD 200

		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf Lambert vertex:vert addshadow nolightmap nodynlightmap nodirlightmap nometa noinstancing noambient

		sampler2D _MainTex;
		sampler2D _PaletteTex;
		#include "VitaPalette.cginc"
		#include "VitaFill.cginc"
		struct Input {
			float2 uv_MainTex;
			half3 tint;
			half3 vitaFill;
		};

		void vert (inout appdata_full v, out Input o)
		{
			UNITY_INITIALIZE_OUTPUT(Input, o);
			o.tint = v.color.rgb;
			o.vitaFill = VitaFillVertexLights(mul(unity_ObjectToWorld, v.vertex).xyz, UnityObjectToWorldNormal(v.normal));
		}

		void surf (Input IN, inout SurfaceOutput o)
		{
			half4 albedo = tex2D(_PaletteTex, float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5));
			o.Albedo = albedo.rgb * IN.tint;
			o.Emission = o.Albedo * IN.vitaFill;
			o.Alpha = 1;
		}
		ENDCG
	}
	FallBack "Diffuse"
}
