// Constant-buffer ABI shared with HelixToolkit 3.1's camera and plane grid core.
#pragma pack_matrix(row_major)
cbuffer cbTransforms : register(b0)
{
    float4x4 mView;
    float4x4 mProjection;
    float4x4 mViewProjection;
    float4 vFrustum;
    float4 vViewport;
    float4 vResolution;
    float3 vEyePos;
    bool SSAOEnabled;
    float SSAOBias;
    float SSAOIntensity;
    float TimeStamp;
    bool IsPerspective;
    float OITPower;
    float OITSlope;
    int OITWeightMode;
    float DpiScale;
};
cbuffer cbPlaneGridModel : register(b4)
{
    float4x4 mWorld;
    float gridSpacing;
    float gridThickness;
    float fadingFactor;
    float planeD;
    float4 pColor;
    float4 gColor;
    bool hasShadowMap;
    int axis;
    int type;
    float padding3;
};
struct GridVertex
{
    float4 position : SV_POSITION;
    float3 world : POSITION0;
};
float VisibilityRange()
{
    // World distances independent of near/far clipping. Keep a broad clear region
    // beneath the camera even in a top-down view of a small, tightly framed asset.
    return max(100, max(abs(vEyePos.y - planeD) * 20, gridSpacing * 40));
}
GridVertex VSMain(uint id : SV_VertexID)
{
    const float2 corners[4] = { float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, -1) };
    float2 xz = vEyePos.xz + corners[id] * VisibilityRange();
    GridVertex result;
    result.world = float3(xz.x, planeD, xz.y);
    result.position = mul(float4(result.world, 1), mViewProjection);
    return result;
}
struct GridPixel
{
    float4 color : SV_TARGET;
    float depth : SV_DEPTH;
};
GridPixel PSMain(GridVertex input)
{
    float2 uv = input.world.xz / max(gridSpacing, .001);
    float2 footprint = max(fwidth(uv), .00001);
    float2 distanceToLine = abs(frac(uv + .5) - .5);
    float2 coverage = 1 - smoothstep(0, footprint, distanceToLine);
    // Avoid shimmer/solid stripes when many grid cells collapse into one pixel.
    coverage *= 1 - smoothstep(.25, 1, footprint);
    float lineCoverage = max(coverage.x, coverage.y);
    float distanceXZ = length(input.world.xz - vEyePos.xz);
    float fade = 1 - smoothstep(VisibilityRange() * .65, VisibilityRange() * .95, distanceXZ);
    GridPixel result;
    result.color = float4(gColor.rgb, gColor.a * lineCoverage * fade);
    float4 projected = mul(float4(input.world, 1), mViewProjection);
    result.depth = saturate(projected.z / projected.w);
    return result;
}
