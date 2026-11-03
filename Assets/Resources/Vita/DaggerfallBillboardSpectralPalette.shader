// PS Vita port: Daggerfall/Billboard for spectral palette atlases (ghost, wraith, Lysandus; Assets/Scripts/Vita/VitaPalette.cs Spectral).
// Same as Daggerfall/Vita/BillboardPalette with emission on, except the emission colour comes from _EmissionPalette (per index, what
// BaseImageFile.GetSpectralEmissionColors32 gives that pixel upstream) instead of an emission map.
Shader "Daggerfall/Vita/BillboardSpectralPalette" {
    Properties {
        _Color("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _MainTex("Palette Index Map", 2D) = "white" {}
        _PaletteTex("Palette", 2D) = "white" {}
        _EmissionPalette("Emission Palette", 2D) = "black" {}
    }
    SubShader {
        Tags { "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "True" }
        LOD 200

        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Lambert alphatest:_Cutoff addshadow vertex:vert noambient
        #pragma multi_compile __ VITA_TOWN_LIGHTS

        half4 _Color;
        sampler2D _MainTex;
        sampler2D _PaletteTex;
        sampler2D _EmissionPalette;
        #include "VitaPalette.cginc"

        #include "VitaFill.cginc"
        struct Input {
            float2 uv_MainTex;
            half3 vitaFill;
        };

        void vert (inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vitaFill = VitaFillVertexLights(mul(unity_ObjectToWorld, v.vertex).xyz, UnityObjectToWorldNormal(v.normal));
        }

        void surf (Input IN, inout SurfaceOutput o)
        {
            float2 index = float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5);
            half4 albedo = tex2D(_PaletteTex, index) * _Color;
            half3 emission = tex2D(_EmissionPalette, index).rgb;
            o.Albedo = albedo.rgb - emission; // Emission cancels out other lights
            o.Emission = emission + o.Albedo * IN.vitaFill;
            o.Alpha = albedo.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
