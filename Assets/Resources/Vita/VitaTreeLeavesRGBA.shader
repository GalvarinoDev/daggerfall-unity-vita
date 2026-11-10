// Project:         Daggerfall Unity - PS Vita port
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)

// PS Vita port: Low Poly Trees leaves from a full-colour RGBA32 copy of the palette atlas (VitaNearTrees.RgbaLeaves trial). The mod's
// leaves aren't Daggerfall palette art, so the palette shader's two dependent reads and its own mip choice (TreeLeavesPalette) buy
// nothing here: one plain read, bilinear and mipped as on PC. Leaves are most of the trees' pixel cost (tree2).
Shader "Daggerfall/Vita/TreeLeavesRGBA" {
	Properties {
		_Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
		_MainTex("Albedo", 2D) = "white" {}
	}
	SubShader {
		Tags { "Queue" = "AlphaTest" "IgnoreProjector" = "True" "RenderType" = "TransparentCutout" }
		LOD 200
		Cull Off

		CGPROGRAM
		#pragma target 3.0
		#pragma surface surf VitaLeaf alphatest:_Cutoff vertex:vert addshadow nolightmap nodynlightmap nodirlightmap nometa noinstancing noambient

		sampler2D _MainTex;
		#include "VitaFill.cginc"

		// PC (the mod's compiled Nature/SpeedTree, GEOM_TYPE_LEAF): albedo x (light x max(N.L, 0) + SH + vertex lights), the same normal
		// on both faces, no VFACE. _VitaLeafAbs = 1 (VitaNearTrees.AbsLeafLight) is tree3's abs(N.L), which also lit faces turned away from
		// the sun; plain Lambert only looked too dark while every tree still carried a 0-50% instance tint PC never applies
		half _VitaLeafAbs;
		half4 LightingVitaLeaf (SurfaceOutput s, half3 lightDir, half atten)
		{
			half4 c;
			half ndl = dot(s.Normal, lightDir);
			ndl = _VitaLeafAbs > 0.5 ? abs(ndl) : max(ndl, 0);
			c.rgb = s.Albedo * _LightColor0.rgb * (ndl * atten);
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
			half4 albedo = tex2D(_MainTex, IN.uv_MainTex);
			o.Albedo = albedo.rgb * IN.tint;
			o.Emission = o.Albedo * IN.vitaFill;
			o.Alpha = albedo.a;
		}
		ENDCG
	}
	FallBack "Transparent/Cutout/VertexLit"
}
