// Project:         Daggerfall Unity
// Copyright:       Copyright (C) 2009-2016 Gavin Clayton
// License:         MIT License (http://www.opensource.org/licenses/mit-license.php)
// Web Site:        http://www.dfworkshop.net
// Contact:         Gavin Clayton (interkarma@dfworkshop.net)
// Project Page:    https://github.com/Interkarma/daggerfall-unity

// Shader used by all freestanding billboards including mobiles
// For wilderness foliage shader see DaggerfallBillboardBatch instead
// PS Vita port: Daggerfall/Billboard for 8-bit palette sprite atlases (Assets/Scripts/Vita/VitaPalette.cs). _MainTex holds the original
// palette indices (Alpha8), _PaletteTex the 256 colours (alpha index transparent). Point filtering only.
// Identical to Daggerfall/Billboard (Assets/Shaders) except the albedo lookup and the emission map (an Alpha8 mask).
Shader "Daggerfall/Vita/BillboardPalette" {
    Properties {
        _Color("Color", Color) = (1,1,1,1)
        _Cutoff("Alpha Cutoff", Range(0.0, 1.0)) = 0.5
        _MainTex("Palette Index Map", 2D) = "white" {}
        _PaletteTex("Palette", 2D) = "white" {}
        _BumpMap("Normal Map", 2D) = "bump" {}
        _EmissionMap("Emission Map", 2D) = "white" {}
        _EmissionColor("Emission Color", Color) = (0,0,0)
    }
    SubShader {
        Tags { "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" "CanUseSpriteAtlas" = "True" }
        LOD 200
        
        CGPROGRAM
        #pragma target 3.0
        #pragma surface surf Lambert alphatest:_Cutoff addshadow vertex:vert noambient
        #pragma multi_compile __ VITA_TOWN_LIGHTS
        #pragma multi_compile_local __ _NORMALMAP
        #pragma multi_compile __ _EMISSION // 2018.2 skips multi_compile_local (2019.1+): the glow variant was never built

        half4 _Color;
        sampler2D _MainTex;
        sampler2D _PaletteTex;
        #include "VitaPalette.cginc"
        #ifdef _NORMALMAP
            sampler2D _BumpMap;
        #endif
        #ifdef _EMISSION
            sampler2D _EmissionMap;
            half4 _EmissionColor;
        #endif

        // Vita: ambient + fill lights per vertex (VitaFill.cginc): Unity's SH cost 16 cycles per pixel here (shaderaudit)
        #include "VitaFill.cginc"
        struct Input {
            float2 uv_MainTex;
            #ifdef _NORMALMAP
                float2 uv_BumpMap;
            #endif
            #ifdef _EMISSION
                float2 uv_EmissionMap;
            #endif
            half3 vitaFill;   // VitaFill / VitaFillVertexLights, from vert
        };

        void vert (inout appdata_full v, out Input o)
        {
            UNITY_INITIALIZE_OUTPUT(Input, o);
            o.vitaFill = VitaFillVertexLights(mul(unity_ObjectToWorld, v.vertex).xyz, UnityObjectToWorldNormal(v.normal));
        }

        void surf (Input IN, inout SurfaceOutput o)
        {
            // index k is stored as k/255: round back to k, then sample the centre of palette texel k (as DefaultPalette)
            half4 albedo = tex2D(_PaletteTex, float2((floor((float)VitaPaletteIndex(_MainTex, IN.uv_MainTex) * 255.0 + 0.5) + 0.5) / 256.0, 0.5)) * _Color;
            #ifdef _NORMALMAP
                o.Normal = UnpackNormal(tex2D(_BumpMap, IN.uv_BumpMap));
            #endif
            #ifdef _EMISSION
                half3 emission = albedo.rgb * tex2D(_EmissionMap, IN.uv_EmissionMap).a * _EmissionColor; // Alpha8 mask of the glowing frames (TextureReader.TryPackAtlasOnCpu)
                o.Albedo = albedo.rgb - emission; // Emission cancels out other lights
                o.Emission = emission;
            #else
                o.Albedo = albedo.rgb;
            #endif
            o.Emission += o.Albedo * IN.vitaFill;
            o.Alpha = albedo.a;
        }
        ENDCG
    } 
    FallBack "Diffuse"
}