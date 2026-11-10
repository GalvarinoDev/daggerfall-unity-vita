// PS Vita port: palette index lookup for the Daggerfall/Vita/*Palette shaders (Assets/Scripts/Vita/VitaPalette.cs).
// The Vita's GPU blends two mip levels even with point filtering (blue/pink specks on glancing floors, ceilings
// and densely tiled textures). Blended palette indices are unrelated colours, so pick one whole mip level here, as the hardware
// would with mip blending off: the same LOD maths (texels per pixel), rounded to the nearest level.
float4 _MainTex_TexelSize;

float VitaPaletteIndex(sampler2D indexTex, float2 uv)
{
    float2 texels = uv * _MainTex_TexelSize.zw;
    float2 dx = ddx(texels), dy = ddy(texels);
    float lod = max(0.0, floor(0.5 * log2(max(dot(dx, dx), dot(dy, dy))) + 0.5));
    return tex2Dlod(indexTex, float4(uv, 0.0, lod)).a;
}
