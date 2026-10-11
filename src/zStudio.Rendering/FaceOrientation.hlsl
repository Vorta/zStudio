// Rasterized winding includes instance, animation and mirrored transforms.
// Deliberately independent of vertex normals, lighting, textures and opacity.
float4 PSMain(bool front : SV_IsFrontFace) : SV_TARGET
{
    return front ? float4(0, 0, 1, 1) : float4(1, 0, 0, 1);
}
