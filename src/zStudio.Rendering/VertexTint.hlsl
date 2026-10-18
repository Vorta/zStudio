// Helix 3.1 mesh-buffer ABI. Keep all field names for the shared material
// variable bindings, including fields unused by this particular pixel pass.
#pragma pack_matrix(row_major)
// Pinned Helix 3.1.2 global-buffer layout; stock diffuse lighting reconstructs
// its view vector from the camera position rather than interpolated vertex eyes.
cbuffer cbTransforms : register(b0)
{
    float4x4 mView, mProjection, mViewProjection;
    float4 vFrustum, vViewport, vResolution;
    float3 vEyePos;
    bool SSAOEnabled;
    float SSAOBias, SSAOIntensity, TimeStamp;
    bool IsPerspective;
    float OITPower, OITSlope;
    int OITWeightMode;
    float DpiScale;
};
cbuffer cbMesh : register(b1)
{
    float4x4 mWorld;
    bool bInvertNormal, bHasInstances, bHasInstanceParams, bHasBones;
    float4 vParams, vColor, wireframeColor;
    bool3 bParams;
    bool bBatched;
    float minTessDistance, maxTessDistance, minTessFactor, maxTessFactor;
    float4 vMaterialDiffuse, vMaterialAmbient, vMaterialEmissive, vMaterialSpecular, vMaterialReflect;
    bool bHasDiffuseMap, bHasNormalMap, bHasCubeMap, bRenderShadowMap;
    bool bHasEmissiveMap, bHasAlphaMap, bHasSpecularMap, bAutoTengent;
    bool bHasDisplacementMap, bRenderPBR, bRenderFlat;
    float sMaterialShininess;
    float4 displacementMapScaleMask, uvTransformR1, uvTransformR2;
    float vertColorBlending;
    float3 padding4;
};
Texture2D texDiffuseMap : register(t0);
SamplerState samplerSurface : register(s0);
struct TintPixel
{
    float4 position : SV_POSITION;
    float4 eye : POSITION0;
    float3 normal : NORMAL;
    float4 world : POSITION1;
    float4 shadow : TEXCOORD1;
    float2 uv : TEXCOORD0;
    float3 tangent : TANGENT;
    float3 bitangent : BINORMAL;
    float4 color : COLOR0;
    float4 emissive : COLOR1;
    float4 diffuse : COLOR2;
};
// Both source paths share their ordinary appearance. Authored vertex colors
// modulate textures; materials without vertex colors use the stock diffuse path.
float4 Appearance(TintPixel input, bool vertexTint)
{
    float4 result = vertexTint ? input.color : input.diffuse;
    if (bHasDiffuseMap) result *= texDiffuseMap.Sample(samplerSurface, input.uv);
    // DiffuseMaterialVariables binds EnableUnLit to bHasNormalMap.
    if (!bHasNormalMap && (!vertexTint || !bHasDiffuseMap))
    {
        float3 normal = bRenderFlat ? normalize(cross(ddy(input.world.xyz), ddx(input.world.xyz))) : normalize(input.normal);
        float3 eye = vertexTint ? normalize(input.eye.xyz) : normalize(vEyePos - input.world.xyz);
        result.rgb *= saturate(.5 + .5 * abs(dot(eye, normal)));
    }
    // vMaterialReflect.x fades the surface (models the polygon zone tool cannot paint); 0 keeps it as it is.
    result.a *= 1 - saturate(vMaterialReflect.x);
    return result;
}
float4 NormalAppearance(TintPixel input)
{
    return Appearance(input, vertColorBlending != 0);
}
float4 PSMain(TintPixel input) : SV_TARGET
{
    return NormalAppearance(input);
}
float4 PSOverlay(TintPixel input) : SV_TARGET
{
    float4 result = NormalAppearance(input);
    // RGB only: normal alpha, cutouts and opaque/transparent depth stay intact.
    // A diagnostic color is constant per material; no vertex interpolation is needed.
    result.rgb = lerp(result.rgb, vMaterialEmissive.rgb, vMaterialEmissive.a);
    return result;
}
// WorldSurfaceHighlights.ZoneColor: a golden-ratio hue per zone, grey for any.
float3 ZoneColor(uint zone)
{
    if (zone == 255) return float3(.8, .8, .8);
    float hue = frac(zone * 0.618033988749895) * 6;
    uint sector = min((uint)hue, 5);
    float f = hue - sector, v = .95, s = .75;
    float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
    float3 colors[6] = { float3(v, t, p), float3(q, v, p), float3(p, v, t), float3(p, q, v), float3(t, p, v), float3(v, p, q) };
    return colors[sector];
}
// COLOR0 carries the polygon's zone set here (ids in RGB as n/255, count in A as n/3), never an authored tint.
// One zone tints the polygon; two or three alternate in diagonal screen stripes. Without zones it looks normal.
// vMaterialSpecular sets the emphasis (PreviewMaterials.SetZoneEmphasis): w = 0 tints every zone at half strength;
// w = 1 tints polygons listing zone x (n/255) at y and the others at z; w = 2 draws only the zone colours at opacity y.
float4 PSPolygonZones(TintPixel input) : SV_TARGET
{
    uint mode = (uint)round(vMaterialSpecular.w);
    float4 result = mode == 2 ? float4(0, 0, 0, 0) : Appearance(input, false);
    uint count = (uint)round(saturate(input.color.a) * 3);
    // The stroke preview of a polygon left with no zones: a dark fill over its old tint.
    if (count == 0) return mode == 2 ? float4(.08, .08, .08, .55) : result;
    uint band = (uint)floor((input.position.x + input.position.y) / (6 * max(DpiScale, 1)));
    uint index = band % count;
    float id = index == 0 ? input.color.r : index == 1 ? input.color.g : input.color.b;
    float3 color = ZoneColor((uint)round(saturate(id) * 255));
    if (mode == 2) return float4(color, vMaterialSpecular.y);
    float strength = .5;
    if (mode == 1)
    {
        uint chosen = (uint)round(saturate(vMaterialSpecular.x) * 255);
        bool listed = (uint)round(saturate(input.color.r) * 255) == chosen
            || count > 1 && (uint)round(saturate(input.color.g) * 255) == chosen
            || count > 2 && (uint)round(saturate(input.color.b) * 255) == chosen;
        strength = listed ? vMaterialSpecular.y : vMaterialSpecular.z;
    }
    result.rgb = lerp(result.rgb, color, strength);
    return result;
}
