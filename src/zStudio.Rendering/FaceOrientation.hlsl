// Rasterized winding includes instance, animation and mirrored transforms.
// Tint the normal appearance once, retaining authored alpha and texture holes.
float4 PSOrientation(TintPixel input, bool front : SV_IsFrontFace) : SV_TARGET
{
    float4 result = NormalAppearance(input);
    result.rgb = lerp(result.rgb, front ? float3(0, 0, 1) : float3(1, 0, 0), .5);
    return result;
}
