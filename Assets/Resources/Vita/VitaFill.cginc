// PS Vita port (fog at the end): the light the 7 palette shaders got from Unity besides the main (sun) light. They draw with noambient: Unity's SH cost
// 16 cycles per pixel (shaderaudit), and Unity then sets neither the SH nor the 4 vertex lights for them.
// So VitaLights.cs sets the same light as globals: the flat ambient (the game scene's mode; no other light goes into the SH, as
half4 _VitaAmbient, _VitaAmbientLinear;    // RenderSettings.ambientLight, and in linear
float4 _VitaLightPos0, _VitaLightPos1, _VitaLightPos2, _VitaLightPos3;     // xyz position, w 1 / range (VitaLights); 2-3 only here
half4 _VitaLightColor0, _VitaLightColor1, _VitaLightColor2, _VitaLightColor3;

// Baked town lamps (VitaLights, keyword VITA_TOWN_LIGHTS): their pools painted over the town by world xz at half brightness (the scale
// has the x2, the fire light setting and the dusk fade). Declared without the keyword: Unity's surface analysis compiles without it
sampler2D _VitaTownLightTex;
float4 _VitaTownLightRect;   // uv = world xz * xy + zw
half _VitaTownLightScale;
float2 VitaTownUV(float3 worldPos)
{
    return worldPos.xz * _VitaTownLightRect.xy + _VitaTownLightRect.zw;
}

// Ambient + the near lights as Unity's vertex lights (Shade4PointLights: N.L, falloff 1/(1 + 25 q)), for the shaders that had those, faded
// to 0 at the range as the per-pixel near lights are, so a light changing slot doesn't pop. Mixed in linear and back to gamma, as
// ShadeSHPerVertex/ShadeSHPerPixel do in gamma space. VITA_FILL_SKIP_01: the shader lights slots 0-1 per pixel itself
half3 VitaFillVertexLights(float3 worldPos, half3 worldNormal)
{
    float3 l0 = (_VitaLightPos0.xyz - worldPos) * _VitaLightPos0.w, l1 = (_VitaLightPos1.xyz - worldPos) * _VitaLightPos1.w;
    float3 l2 = (_VitaLightPos2.xyz - worldPos) * _VitaLightPos2.w, l3 = (_VitaLightPos3.xyz - worldPos) * _VitaLightPos3.w;
    float4 q = max(float4(dot(l0, l0), dot(l1, l1), dot(l2, l2), dot(l3, l3)), 0.000001);
    float4 ndotl = max(float4(0, 0, 0, 0), float4(dot(worldNormal, l0), dot(worldNormal, l1), dot(worldNormal, l2), dot(worldNormal, l3)) * rsqrt(q));
    float4 diff = ndotl * saturate((1.0 - q) * 5.0) / (1.0 + 25.0 * q);
    #ifdef VITA_FILL_SKIP_01
        half3 lights = _VitaLightColor2.rgb * diff.z + _VitaLightColor3.rgb * diff.w;
    #else
        half3 lights = _VitaLightColor0.rgb * diff.x + _VitaLightColor1.rgb * diff.y + _VitaLightColor2.rgb * diff.z + _VitaLightColor3.rgb * diff.w;
    #endif
    // Per vertex for sprites; VITA_TOWN_PER_PIXEL: the shader samples it per pixel itself (terrain: vertices 6.4 m apart)
    #if defined(VITA_TOWN_LIGHTS) && !defined(VITA_TOWN_PER_PIXEL)
        lights += tex2Dlod(_VitaTownLightTex, float4(VitaTownUV(worldPos), 0, 0)).rgb * _VitaTownLightScale;
    #endif
    return LinearToGammaSpace(GammaToLinearSpace(lights) + _VitaAmbientLinear.rgb);
}

// Fog: the per-pixel factor is nearly free (exp = linear = 54 cycles in the old Tilemap, shaderaudit); the cost is the blend to the fog
// colour (+6 cycles). So per vertex: factor f and fogColour x (1 - f), per pixel col x f + that (one mad); the factor per vertex as Unity's
// mobile path does (exact for linear fog, the game's clear-day fog; close for exp fog in weather, dungeons, interiors)
#if defined(FOG_LINEAR) || defined(FOG_EXP) || defined(FOG_EXP2)
    #ifdef UNITY_PASS_FORWARDADD
        #define VITA_FOG_COLOR half3(0, 0, 0)
    #else
        #define VITA_FOG_COLOR unity_FogColor.rgb
    #endif
    #undef UNITY_FOG_COORDS
    #undef UNITY_TRANSFER_FOG
    #undef UNITY_EXTRACT_FOG
    #undef UNITY_APPLY_FOG_COLOR
    #define UNITY_FOG_COORDS(idx) UNITY_FOG_COORDS_PACKED(idx, half4)
    #define UNITY_TRANSFER_FOG(o,outpos) UNITY_CALC_FOG_FACTOR((outpos).z); unityFogFactor = saturate(unityFogFactor); o.fogCoord = half4(VITA_FOG_COLOR * (1.0 - unityFogFactor), unityFogFactor)
    #define UNITY_EXTRACT_FOG(name) half4 _unity_fogCoord = name.fogCoord
    #define UNITY_APPLY_FOG_COLOR(coord,col,fogCol) col.rgb = col.rgb * (coord).w + (coord).rgb
#endif
