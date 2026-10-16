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
float4 NormalAppearance(TintPixel input)
{
    bool vertexTint = vertColorBlending != 0;
    float4 result = vertexTint ? input.color : input.diffuse;
    if (bHasDiffuseMap) result *= texDiffuseMap.Sample(samplerSurface, input.uv);
    // DiffuseMaterialVariables binds EnableUnLit to bHasNormalMap.
    if (!bHasNormalMap && (!vertexTint || !bHasDiffuseMap))
    {
        float3 normal = bRenderFlat ? normalize(cross(ddy(input.world.xyz), ddx(input.world.xyz))) : normalize(input.normal);
        float3 eye = vertexTint ? normalize(input.eye.xyz) : normalize(vEyePos - input.world.xyz);
        result.rgb *= saturate(.5 + .5 * abs(dot(eye, normal)));
    }
    return result;
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
