// PS Vita port: Daggerfall/GhostShader (Assets/Shaders/GhostShader.shader) for spectral palette atlases (VitaPalette.Spectral):
// albedo and alpha from _PaletteTex, emission from _EmissionPalette, both looked up by the pixel's palette index. No normal map
// (the Vita makes none; the default "bump" upstream unpacks to the flat normal).
Shader "Daggerfall/Vita/GhostPalette" {
    Properties {
        _Color("Color", Color) = (1,1,1,1)
        _MainTex ("Palette Index Map", 2D) = "white" {}
        _PaletteTex("Palette", 2D) = "white" {}
        _EmissionPalette("Emission Palette", 2D) = "black" {}
        _Cutoff ("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _Glossiness ("Smoothness", Range(0.0, 1.0)) = 0.0
        _Metallic ("Metallic", Range(0.0, 1.0)) = 0.0
    }
    SubShader {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard alpha:blend
        #pragma target 3.0

        half4 _Color;
        sampler2D _MainTex;
        sampler2D _PaletteTex;
        sampler2D _EmissionPalette;
        #include "VitaPalette.cginc"

        struct Input {
            float2 uv_MainTex;
        };

        half _Cutoff;
        half _Glossiness;
        half _Metallic;

        void surf (Input IN, inout SurfaceOutputStandard o) {
            float2 index = float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5);
            fixed4 c = tex2D(_PaletteTex, index) * _Color;
            clip (c.a - _Cutoff);

            half3 emission = tex2D(_EmissionPalette, index).rgb;
            o.Albedo = c.rgb - emission; // Emission cancels out other lights
            o.Emission = emission;
            o.Metallic = _Metallic;
            o.Smoothness = _Glossiness;
            o.Alpha = c.a;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
