// The Direct3D 11 backend's shaders (zStudio.Rendering.Direct3D): meshes, lines, the ground grid and the navigation aids.
// Row-vector convention: a point is transformed as mul(p, M).
#pragma pack_matrix(row_major)

// Per frame (and per screen-spaced viewport).
cbuffer Frame : register(b0)
{
    float4x4 View;
    float4x4 Projection;
    float4x4 ViewProjection;
    // The current viewport in physical pixels: width, height, 1 / width, 1 / height.
    float4 Viewport;
    float3 EyePos;
    // Physical pixels per DIP (line widths and zone stripes).
    float DpiScale;
    // Ground grid: x spacing, y plane height.
    float4 GridParams;
    float4 GridColor;
};

// Per draw.
cbuffer Object : register(b1)
{
    float4x4 World;
    // The material's diffuse colour (lines: the line colour).
    float4 Diffuse;
    // Diagnostic overlay: lerp(rgb, Overlay.rgb, Overlay.a).
    float4 Overlay;
    // Polygon zone emphasis: x zone / 255, y strong, z weak, w mode.
    float4 Emphasis;
    // x fade, y vertex colour blending, z line half-width in DIPs.
    float4 Params;
    // x textured, y unlit, z flat normals.
    uint4 Flags;
};

Texture2D DiffuseMap : register(t0);
SamplerState Surface : register(s0);

struct MeshVertex
{
    float3 position : POSITION;
    float3 normal : NORMAL;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
    float4 row0 : INSTANCE0;
    float4 row1 : INSTANCE1;
    float4 row2 : INSTANCE2;
    float4 row3 : INSTANCE3;
};

struct MeshPixel
{
    float4 position : SV_POSITION;
    // Unit vector from the vertex to the eye, and its distance.
    float4 eye : POSITION0;
    float3 normal : NORMAL;
    float4 world : POSITION1;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
};

// World position p · instance · World; the normal follows the same matrices' 3 × 3 parts (not their inverse transposes).
MeshPixel VSMesh(MeshVertex input)
{
    float4x4 instance = float4x4(input.row0, input.row1, input.row2, input.row3);
    MeshPixel output;
    output.world = mul(mul(float4(input.position, 1), instance), World);
    output.normal = normalize(mul(normalize(mul(input.normal, (float3x3)instance)), (float3x3)World));
    float3 eye = EyePos - output.world.xyz;
    output.eye = float4(normalize(eye), length(eye));
    output.uv = input.uv;
    output.color = input.color;
    output.position = mul(output.world, ViewProjection);
    return output;
}

float3 SurfaceNormal(MeshPixel input)
{
    return Flags.z != 0 ? normalize(cross(ddy(input.world.xyz), ddx(input.world.xyz))) : normalize(input.normal);
}

// The plain diffuse pass: diffuse colour times the texture, then the vertex colour by the blending factor; lit adds the
// view term .5 + .5·|dot(eye, normal)| with the eye taken per pixel.
float4 PSPlain(MeshPixel input) : SV_TARGET
{
    float4 c = Diffuse;
    if (Flags.x != 0) c *= DiffuseMap.Sample(Surface, input.uv);
    c = lerp(c, input.color, Params.y);
    if (Flags.y == 0)
    {
        float3 dir = normalize(EyePos - input.world.xyz);
        c.rgb *= clamp(.5 + .5 * abs(dot(dir, SurfaceNormal(input))), 0, 1);
    }
    return c;
}

// The preview passes. Vertex-tint surfaces take the vertex colour (which modulates the texture); the others the diffuse
// colour. Textured vertex-tint output does not depend on the view; the others shade by the view term when lit.
float4 Appearance(MeshPixel input, bool vertexTint)
{
    float4 result = vertexTint ? input.color : Diffuse;
    if (Flags.x != 0) result *= DiffuseMap.Sample(Surface, input.uv);
    if (Flags.y == 0 && (!vertexTint || Flags.x == 0))
    {
        float3 eye = vertexTint ? normalize(input.eye.xyz) : normalize(EyePos - input.world.xyz);
        result.rgb *= saturate(.5 + .5 * abs(dot(eye, SurfaceNormal(input))));
    }
    // A fade of 1 draws nothing (models the polygon zone tool cannot paint); 0 keeps the surface as it is.
    result.a *= 1 - saturate(Params.x);
    return result;
}

float4 NormalAppearance(MeshPixel input)
{
    return Appearance(input, Params.y != 0);
}

float4 PSTint(MeshPixel input) : SV_TARGET
{
    return NormalAppearance(input);
}

// RGB only: alpha, cutouts and the depth policy stay as they are.
float4 PSOverlay(MeshPixel input) : SV_TARGET
{
    float4 result = NormalAppearance(input);
    result.rgb = lerp(result.rgb, Overlay.rgb, Overlay.a);
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

// The vertex colour carries the polygon's zone set here (ids in RGB as n/255, count in A as n/3), never an authored tint.
// One zone tints the polygon; two or three alternate in diagonal screen stripes 6 DIPs wide. Without zones it looks normal.
// Emphasis.w = 0 tints every zone at half strength; 1 tints polygons listing zone Emphasis.x at Emphasis.y and the others
// at Emphasis.z; 2 draws only the zone colours at opacity Emphasis.y (a cleared polygon dark at .55).
float4 PSPolygonZones(MeshPixel input) : SV_TARGET
{
    uint mode = (uint)round(Emphasis.w);
    float4 result = mode == 2 ? float4(0, 0, 0, 0) : Appearance(input, false);
    uint count = (uint)round(saturate(input.color.a) * 3);
    if (count == 0) return mode == 2 ? float4(.08, .08, .08, .55) : result;
    uint band = (uint)floor((input.position.x + input.position.y) / (6 * max(DpiScale, 1)));
    uint index = band % count;
    float id = index == 0 ? input.color.r : index == 1 ? input.color.g : input.color.b;
    float3 color = ZoneColor((uint)round(saturate(id) * 255));
    if (mode == 2) return float4(color, Emphasis.y);
    float strength = .5;
    if (mode == 1)
    {
        uint chosen = (uint)round(saturate(Emphasis.x) * 255);
        bool listed = (uint)round(saturate(input.color.r) * 255) == chosen
            || count > 1 && (uint)round(saturate(input.color.g) * 255) == chosen
            || count > 2 && (uint)round(saturate(input.color.b) * 255) == chosen;
        strength = listed ? Emphasis.y : Emphasis.z;
    }
    result.rgb = lerp(result.rgb, color, strength);
    return result;
}

// Rasterized winding (front faces wind counterclockwise on screen) includes instance, animation and mirrored transforms.
float4 PSOrientation(MeshPixel input, bool front : SV_IsFrontFace) : SV_TARGET
{
    float4 result = NormalAppearance(input);
    result.rgb = lerp(result.rgb, front ? float3(0, 0, 1) : float3(1, 0, 0), .5);
    return result;
}

// The vertex colour alone (the coordinate axes).
float4 PSColor(MeshPixel input) : SV_TARGET
{
    return input.color;
}

// The view cube: white times the face texture (its border colour outside it) times the vertex colour.
float4 PSViewCube(MeshPixel input) : SV_TARGET
{
    return Diffuse * DiffuseMap.Sample(Surface, input.uv) * input.color;
}

// Lines: a screen-space ribbon of Params.z DIPs to each side of each segment.
struct LineVertex
{
    float3 position : POSITION;
    float4 row0 : INSTANCE0;
    float4 row1 : INSTANCE1;
    float4 row2 : INSTANCE2;
    float4 row3 : INSTANCE3;
};

struct LineSegmentEnd
{
    float4 position : SV_POSITION;
};

struct LinePixel
{
    float4 position : SV_POSITION;
    noperspective float across : TEXCOORD0;
};

LineSegmentEnd VSLine(LineVertex input)
{
    float4x4 instance = float4x4(input.row0, input.row1, input.row2, input.row3);
    LineSegmentEnd output;
    output.position = mul(mul(mul(float4(input.position, 1), instance), World), ViewProjection);
    return output;
}

float2 ProjectToWindow(float4 position)
{
    return float2(Viewport.x * .5 * (1 + position.x / position.w), Viewport.y * .5 * (1 - position.y / position.w));
}

float4 WindowToProject(float2 position, float z, float w)
{
    return float4((position.x * 2 * Viewport.z - 1) * w, (position.y * 2 * Viewport.w - 1) * -w, z, w);
}

[maxvertexcount(4)]
void GSLine(line LineSegmentEnd input[2], inout TriangleStream<LinePixel> stream)
{
    float4 a = input[0].position, b = input[1].position;
    // An end behind the eye moves to w = 0 along the segment.
    if (a.w * b.w < 0)
    {
        if (a.w < 0) a = lerp(a, b, -a.w / (b.w - a.w));
        else b = lerp(b, a, -b.w / (a.w - b.w));
    }
    float2 aw = ProjectToWindow(a), bw = ProjectToWindow(b);
    float2 tangent = normalize(bw - aw);
    float2 side = Params.z * DpiScale * float2(tangent.y, -tangent.x);
    LinePixel output;
    output.position = WindowToProject(aw + side, a.z, a.w); output.across = 1; stream.Append(output);
    output.position = WindowToProject(aw - side, a.z, a.w); output.across = -1; stream.Append(output);
    output.position = WindowToProject(bw + side, b.z, b.w); output.across = 1; stream.Append(output);
    output.position = WindowToProject(bw - side, b.z, b.w); output.across = -1; stream.Append(output);
    stream.RestartStrip();
}

// A solid ribbon: the edge falloff exp2(-2 t² / σ) with σ = 2 / (smoothness + 1e-6) and smoothness 0 is 1 to within 1e-6.
float4 PSLine(LinePixel input) : SV_TARGET
{
    float4 color = Diffuse;
    color.a *= exp2(-2 * input.across * input.across / (2 / 1e-6));
    return color;
}

// The ground grid: a quad on the plane y = GridParams.y around the eye, independent of the near and far planes.
struct GridVertex
{
    float4 position : SV_POSITION;
    float3 world : POSITION0;
};

float VisibilityRange()
{
    // World distances independent of near/far clipping. Keep a broad clear region
    // beneath the camera even in a top-down view of a small, tightly framed asset.
    return max(100, max(abs(EyePos.y - GridParams.y) * 20, GridParams.x * 40));
}

GridVertex VSGrid(uint id : SV_VertexID)
{
    const float2 corners[4] = { float2(-1, 1), float2(1, 1), float2(-1, -1), float2(1, -1) };
    float2 xz = EyePos.xz + corners[id] * VisibilityRange();
    GridVertex result;
    result.world = float3(xz.x, GridParams.y, xz.y);
    result.position = mul(float4(result.world, 1), ViewProjection);
    return result;
}

struct GridPixel
{
    float4 color : SV_TARGET;
    float depth : SV_DEPTH;
};

GridPixel PSGrid(GridVertex input)
{
    float2 uv = input.world.xz / max(GridParams.x, .001);
    float2 footprint = max(fwidth(uv), .00001);
    float2 distanceToLine = abs(frac(uv + .5) - .5);
    float2 coverage = 1 - smoothstep(0, footprint, distanceToLine);
    // Avoid shimmer/solid stripes when many grid cells collapse into one pixel.
    coverage *= 1 - smoothstep(.25, 1, footprint);
    float lineCoverage = max(coverage.x, coverage.y);
    float distanceXZ = length(input.world.xz - EyePos.xz);
    float fade = 1 - smoothstep(VisibilityRange() * .65, VisibilityRange() * .95, distanceXZ);
    GridPixel result;
    result.color = float4(GridColor.rgb, GridColor.a * lineCoverage * fade);
    float4 projected = mul(float4(input.world, 1), ViewProjection);
    result.depth = saturate(projected.z / projected.w);
    return result;
}

// Screen-aligned label quads (the axis letters): position is already in clip space, the texture is premultiplied by nothing.
struct LabelVertex
{
    float4 position : POSITION;
    float2 uv : TEXCOORD0;
};

struct LabelPixel
{
    float4 position : SV_POSITION;
    float2 uv : TEXCOORD0;
};

LabelPixel VSLabel(LabelVertex input)
{
    LabelPixel output;
    output.position = input.position;
    output.uv = input.uv;
    return output;
}

float4 PSLabel(LabelPixel input) : SV_TARGET
{
    float4 color = DiffuseMap.Sample(Surface, input.uv) * Diffuse;
    // Texels under 0.1 alpha write neither colour nor depth: they hide nothing behind the quad.
    clip(color.a - 0.1);
    return color;
}
