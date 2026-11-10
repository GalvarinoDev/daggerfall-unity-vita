// PS Vita port: debug view of a palette index texture: the raw index as grey. Unlit.
// _Mode 0: index as grey via tex2D, 1: index as grey via tex2Dlod level 0, 2: the LOD VitaPaletteIndex picks (x0.25, red),
// 3: palette colour via tex2Dlod level 0, 4: palette colour via VitaPaletteIndex (as DefaultPalette)
Shader "Daggerfall/Vita/DebugIndex" {
    Properties {
        _MainTex("Palette Index Map", 2D) = "white" {}
        _PaletteTex("Palette", 2D) = "white" {}
        _Mode("Mode", Float) = 0
    }
    SubShader {
        Tags { "RenderType" = "Opaque" }
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            sampler2D _PaletteTex;
            float4 _MainTex_ST;
            float _Mode;
            #include "VitaPalette.cginc"

            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; };

            v2f vert(appdata_base v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.texcoord, _MainTex);
                return o;
            }

            float4 Pal(float a)
            {
                return tex2D(_PaletteTex, float2((floor(a * 255.0 + 0.5) + 0.5) / 256.0, 0.5));
            }

            float4 frag(v2f i) : SV_Target
            {
                float2 texels = i.uv * _MainTex_TexelSize.zw;
                float2 dx = ddx(texels), dy = ddy(texels);
                float lod = max(0.0, floor(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + 0.5));
                float a0 = tex2D(_MainTex, i.uv).a;
                float a1 = tex2Dlod(_MainTex, float4(i.uv, 0.0, 0.0)).a;
                float4 c = float4(a0, a0, a0, 1);
                c = _Mode > 0.5 ? float4(a1, a1, a1, 1) : c;
                c = _Mode > 1.5 ? float4(lod * 0.25, frac(i.uv.x), frac(i.uv.y), 1) : c;
                c = _Mode > 2.5 ? Pal(a1) : c;
                c = _Mode > 3.5 ? Pal(VitaPaletteIndex(_MainTex, i.uv)) : c;
                return c;
            }
            ENDCG
        }
    }
}
