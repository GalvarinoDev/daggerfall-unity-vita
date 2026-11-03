// PS Vita port: Daggerfall/UIBlend for palette-index atlases (Assets/Scripts/Vita/VitaWeaponPack.cs): Alpha8 index → _PaletteTex colour,
// times _ColorTint, alpha blended. No mips and point filtering, so a plain read gives the exact index.
Shader "Daggerfall/Vita/UIPalette"
{
    Properties
    {
        _MainTex ("Indices", 2D) = "" {}
        _PaletteTex ("Palette", 2D) = "white" {}
        _ColorTint ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    }

    SubShader {

        Tags { "ForceSupported" = "True" "RenderType"="Overlay" }

        Lighting Off
        Cull Off
        ZWrite Off
        ZTest Always
        Fog { Mode Off }

        Pass {
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct appdata_t {
                float4 vertex : POSITION;
                float2 texcoord : TEXCOORD0;
            };

            struct v2f {
                float4 vertex : SV_POSITION;
                float2 texcoord : TEXCOORD0;
            };

            sampler2D _MainTex;
            sampler2D _PaletteTex;
            float4 _ColorTint;

            v2f vert (appdata_t v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.texcoord = v.texcoord;
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float index = tex2D(_MainTex, i.texcoord).a;
                return tex2D(_PaletteTex, float2((floor(index * 255.0 + 0.5) + 0.5) / 256.0, 0.5)) * _ColorTint;
            }
            ENDCG
        }
    }

    Fallback off
}
