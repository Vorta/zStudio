// Helix 3.1 mesh-buffer ABI. Keep all field names for the shared material
// variable bindings, including fields unused by this particular pixel pass.
#pragma pack_matrix(row_major)
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
float4 PSMain(TintPixel input) : SV_TARGET
{
    // Authored vertex RGB and material alpha modulate the texture. Helix's
    // stock vertex-color blend instead replaces it, losing the texture entirely.
    float4 result = input.color;
    if (bHasDiffuseMap) result *= texDiffuseMap.Sample(samplerSurface, input.uv);
    if (!bHasNormalMap)
    {
        float3 normal = bRenderFlat ? normalize(cross(ddy(input.world.xyz), ddx(input.world.xyz))) : normalize(input.normal);
        result.rgb *= saturate(.5 + .5 * abs(dot(normalize(input.eye.xyz), normal)));
    }
    return result;
}
