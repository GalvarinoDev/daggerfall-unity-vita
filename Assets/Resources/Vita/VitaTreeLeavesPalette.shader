// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)

// PS Vita port: the alpha-tested parts (leaves) of the Low Poly Trees models near the player (Assets/Scripts/Vita/VitaNearTrees.cs).
// As Daggerfall/Vita/TreePalette, plus palette index 0 = transparent and both faces drawn (the mod's SpeedTree leaf material).
Shader "Daggerfall/Vita/TreeLeavesPalette" {
	Properties {
		_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
		_MainTex("Palette Index Map", 2D) = "white" {}
		_PaletteTex("Palette", 2D) = "white" {}
	}
	SubShader {
		Tags { "Queue" = "AlphaTest" "IgnoreProjector" = "True" "RenderType" = "TransparentCutout" }
		LOD 200
		Cull Off

		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf VitaLeaf alphatest:_Cutoff vertex:vert addshadow nolightmap nodynlightmap nodirlightmap nometa noinstancing noambient

		sampler2D _MainTex;
		sampler2D _PaletteTex;
		#include "VitaPalette.cginc"
		#include "VitaFill.cginc"

		// Both faces lit (the mod's SpeedTree leaves flip the normal on back faces; abs(N.L) is that without VFACE, and also lights a
		// face turned away from the sun: look-alike). Unity's Lambert lit the back of every card with ambient only (tree3: dark trees)
		half4 LightingVitaLeaf (SurfaceOutput s, half3 lightDir, half atten)
		{
			half4 c;
			c.rgb = s.Albedo * _LightColor0.rgb * (abs(dot(s.Normal, lightDir)) * atten);
			c.a = s.Alpha;
			return c;
		}
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
			o.Alpha = albedo.a;
		}
		ENDCG
	}
	FallBack "Transparent/Cutout/VertexLit"
}
