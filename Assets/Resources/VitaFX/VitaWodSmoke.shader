// PS Vita port: World of Daggerfall / RMB Resource Pack camp smoke (model 52990). The mod's material is Unity's
// Particles/Standard Unlit, alpha blend, colour mode "Color", soft particles (_FADING_ON), _Color (23.97, 23.97, 23.97, 0.392). Same maths
// without the soft fade (it needs the camera depth texture = a depth pass on the Vita): hue/saturation from the particle colour, value
// from the texture times _Color (HSV with value = max channel, so particle colour / its max channel x that value), alpha tex x _Color.a x particle.
Shader "Daggerfall/Vita/WodSmoke" {
    Properties {
        _MainTex("Texture", 2D) = "white" {}
        _Color("Color", Color) = (23.968628, 23.968628, 23.968628, 0.39215687)
    }
    SubShader {
        Tags { "Queue" = "Transparent" "IgnoreProjector" = "True" "RenderType" = "Transparent" "PreviewType" = "Plane" }
        Blend SrcAlpha OneMinusSrcAlpha
        ZWrite Off
        Cull Off
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fog
            #include "UnityCG.cginc"
            sampler2D _MainTex;
            float4 _MainTex_ST;
            half4 _Color;
            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; fixed4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; half4 tint : TEXCOORD1; UNITY_FOG_COORDS(2) };
            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                half m = max(max(v.color.r, v.color.g), max(v.color.b, 0.001));
                o.tint = half4(v.color.rgb / m * _Color.r, v.color.a * _Color.a);
                UNITY_TRANSFER_FOG(o, o.pos);
                return o;
            }
            half4 frag(v2f i) : SV_Target
            {
                half4 t = tex2D(_MainTex, i.uv);
                half4 c = half4(i.tint.rgb * max(max(t.r, t.g), t.b), t.a * i.tint.a);
                UNITY_APPLY_FOG(i.fogCoord, c);
                return c;
            }
            ENDCG
        }
    }
}
