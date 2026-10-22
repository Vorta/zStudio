using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// The view cube and the coordinate axes (the Direct3D backend), each drawn in its own small square viewport over a cleared
/// depth buffer after everything else. The viewport is 100 × scale DIPs, centred at the relative position (−1..1, Y up) and
/// clamped inside the surface; neither is drawn (or hit) while the surface is under 100 physical pixels on an axis. Both look
/// along the main camera's look direction with its up direction from 20 units away; the cube is always perspective (45°), the
/// axes follow the main projection.
/// </summary>
internal sealed class NavigationAids
{
    private const float Size = 100, CameraDistance = 20, Near = .01f, Far = 1000, CubeSide = 5;
    private const float AxisLength = 5.5f, AxisDiameter = .6f, AxisHead = 1.7f, LabelDistance = AxisLength + 1.5f;
    // The axis letters' em size in physical pixels.
    private const double LabelEm = 19;
    // The glyph's centre sits this many physical pixels below the projected label point.
    private const float LabelDrop = 2;
    private static readonly Color4 LabelColor = new(169 / 255f, 169 / 255f, 169 / 255f, 1);
    private static readonly Lazy<MeshData> CubeMesh = new(BuildCube);
    private static readonly Lazy<MeshData> AxesMesh = new(BuildAxes);
    private static readonly Lazy<RenderTexture[]> Labels = new(() => [Label("X"), Label("Y"), Label("Z")]);
    private ID3D11Buffer? labelQuad;
    private int labelGeneration;

    /// <summary>The square viewport (physical pixels) of an aid at a relative position and scale on a surface of the given physical size.</summary>
    public static (float X, float Y, float Side) Viewport(double horizontal, double vertical, double scale, float width, float height, double dpiScale)
    {
        float side = (float)(Size * scale * dpiScale);
        int x = (int)(width / 2 * (1 + horizontal) - side / 2), y = (int)(height / 2 * (1 - vertical) - side / 2);
        x = Math.Max(0, Math.Min(x, (int)(width - side)));
        y = Math.Max(0, Math.Min(y, (int)(height - side)));
        return (x, y, side);
    }
    public static bool Fits(float width, float height) => width >= Size && height >= Size;

    private static Matrix4x4 AidView(RenderCamera camera, out Vector3 eye)
    {
        eye = -Vector3.Normalize(RenderCamera.ToFloat(camera.LookDirection)) * CameraDistance;
        return Matrix4x4.CreateLookAt(eye, Vector3.Zero, RenderCamera.ToFloat(camera.UpDirection));
    }
    private static Matrix4x4 AidProjection(bool perspective) => perspective
        ? Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 4, 1, Near, Far)
        : Matrix4x4.CreateOrthographic(CameraDistance, CameraDistance, Near, Far);

    public void Render(D3DGraphics g, RenderView view, RenderCamera camera, float width, float height, double dpiScale, ID3D11DepthStencilView depth, float fullWidth, float fullHeight)
    {
        if (!Fits(width, height)) return;
        var context = g.Context;
        if (view.ShowViewCube && view.ViewCubeTexture is { } texture)
        {
            var (x, y, side) = Viewport(view.ViewCubeHorizontalPosition, view.ViewCubeVerticalPosition, view.ViewCubeSize, width, height, dpiScale);
            Begin(g, camera, depth, x, y, side, perspective: true, dpiScale);
            DrawMesh(g, CubeMesh.Value, g.PSViewCube, texture.Resource(g).View, g.CubeSampler);
        }
        if (view.ShowCoordinateSystem)
        {
            var (x, y, side) = Viewport(-.8, -.8, 1, width, height, dpiScale);
            var frame = Begin(g, camera, depth, x, y, side, perspective: camera is PerspectiveRenderCamera, dpiScale);
            DrawMesh(g, AxesMesh.Value, g.PSColor, null, g.SurfaceSampler);
            DrawLabels(g, frame, side);
        }
        context.RSSetViewport(0, 0, fullWidth, fullHeight);
        context.RSSetScissorRect(0, 0, (int)MathF.Ceiling(fullWidth), (int)MathF.Ceiling(fullHeight));
    }

    private static FrameConstants Begin(D3DGraphics g, RenderCamera camera, ID3D11DepthStencilView depth, float x, float y, float side, bool perspective, double dpiScale)
    {
        var context = g.Context;
        context.ClearDepthStencilView(depth, DepthStencilClearFlags.Depth, 1, 0);
        var view = AidView(camera, out var eye);
        var projection = AidProjection(perspective);
        var frame = new FrameConstants
        {
            View = view, Projection = projection, ViewProjection = view * projection,
            Viewport = new(side, side, 1 / side, 1 / side), EyePosition = eye, DpiScale = (float)dpiScale
        };
        g.Write(g.FrameBuffer, frame);
        context.RSSetViewport(x, y, side, side);
        context.RSSetScissorRect((int)x, (int)y, (int)side + (int)x, (int)side + (int)y);
        return frame;
    }

    private static void DrawMesh(D3DGraphics g, MeshData mesh, ID3D11PixelShader pixel, ID3D11ShaderResourceView? texture, ID3D11SamplerState sampler)
    {
        if (mesh.Buffers(g) is not { } buffers) return;
        var context = g.Context;
        g.Write(g.ObjectBuffer, new ObjectConstants { World = Matrix4x4.Identity, Diffuse = Vector4.One, Textured = texture != null ? 1u : 0, Unlit = 1 });
        var colors = buffers.Colors(mesh.Colors);
        context.IASetInputLayout(g.MeshLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.IASetVertexBuffers(0, 5,
            [buffers.Positions, buffers.Normals ?? g.ZeroBuffer, buffers.TextureCoordinates ?? g.ZeroBuffer, colors ?? g.ZeroBuffer, g.IdentityInstance],
            [12u, buffers.Normals != null ? 12u : 0, buffers.TextureCoordinates != null ? 8u : 0, colors != null ? 16u : 0, 64u], [0u, 0, 0, 0, 0]);
        context.IASetIndexBuffer(buffers.Indices, Format.R32_UInt, 0);
        context.VSSetShader(g.VSMesh); context.GSSetShader(null); context.PSSetShader(pixel);
        context.PSSetShaderResource(0, texture!); context.PSSetSampler(0, sampler);
        context.RSSetState(g.Rasterizer(CullMode.Back, 0, true));
        context.OMSetDepthStencilState(g.DepthLessWrite);
        context.DrawIndexedInstanced((uint)buffers.IndexCount, 1, 0, 0, 0);
    }

    // The axis letters: screen-aligned quads centred on the projected label points, drawn over the arrows.
    private void DrawLabels(D3DGraphics g, in FrameConstants frame, float side)
    {
        var context = g.Context;
        if (labelQuad == null || labelGeneration != g.Generation)
        {
            labelQuad?.Dispose();
            labelQuad = g.Device.CreateBuffer(24u * 4, BindFlags.VertexBuffer, ResourceUsage.Dynamic, CpuAccessFlags.Write);
            labelGeneration = g.Generation;
        }
        Vector3[] anchors = [new(LabelDistance, 0, 0), new(0, LabelDistance, 0), new(0, 0, LabelDistance)];
        var textures = Labels.Value;
        for (int i = 0; i < 3; i++)
        {
            var clip = Vector4.Transform(new Vector4(anchors[i], 1), frame.ViewProjection);
            if (clip.W <= 0) continue;
            var center = new Vector2(clip.X / clip.W, clip.Y / clip.W - 2 * LabelDrop / side);
            var texture = textures[i];
            float hx = texture.Width / side, hy = texture.Height / side;
            float z = Math.Clamp(clip.Z / clip.W, 0, 1);
            var mapped = context.Map(labelQuad, MapMode.WriteDiscard);
            try
            {
                var quad = mapped.AsSpan<float>(24);
                ReadOnlySpan<float> data =
                [
                    center.X - hx, center.Y + hy, z, 1, 0, 0,
                    center.X + hx, center.Y + hy, z, 1, 1, 0,
                    center.X - hx, center.Y - hy, z, 1, 0, 1,
                    center.X + hx, center.Y - hy, z, 1, 1, 1,
                ];
                data.CopyTo(quad);
            }
            finally { context.Unmap(labelQuad, 0); }
            g.Write(g.ObjectBuffer, new ObjectConstants { World = Matrix4x4.Identity, Diffuse = LabelColor.ToVector4(), Textured = 1, Unlit = 1 });
            context.IASetInputLayout(g.LabelLayout);
            context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
            context.IASetVertexBuffer(0, labelQuad, 24);
            context.VSSetShader(g.VSLabel); context.GSSetShader(null); context.PSSetShader(g.PSLabel);
            context.PSSetShaderResource(0, texture.Resource(g).View); context.PSSetSampler(0, g.LabelSampler);
            context.RSSetState(g.Rasterizer(CullMode.None, 0, true));
            context.OMSetDepthStencilState(g.DepthNone);
            context.Draw(4, 0);
        }
    }

    /// <summary>
    /// The view cube hit under <paramref name="point"/> (DIPs): the look direction the hit face asks for (its inward normal,
    /// turned along the current look), or for the base ring ±Y along the look (the top or bottom view).
    /// </summary>
    public static bool HitCube(RenderView view, RenderCamera camera, Point point, float width, float height, double dpiScale, out Vector3 lookNormal)
    {
        lookNormal = default;
        if (!view.ShowViewCube || view.ViewCubeTexture == null || !Fits(width, height)) return false;
        var (x, y, side) = Viewport(view.ViewCubeHorizontalPosition, view.ViewCubeVerticalPosition, view.ViewCubeSize, width, height, dpiScale);
        double px = point.X * dpiScale - x, py = point.Y * dpiScale - y;
        if (px < 0 || py < 0 || px > side || py > side) return false;
        var aidView = AidView(camera, out var eye);
        var projection = AidProjection(true);
        if (!Matrix4x4.Invert(aidView, out var inverse)) return false;
        float nx = (float)(2 * px / side - 1) / projection.M11, ny = (float)(1 - 2 * py / side) / projection.M22;
        var direction = Vector3.Normalize(Vector3.TransformNormal(new Vector3(nx, ny, -1), inverse));
        var mesh = CubeMesh.Value;
        if (!mesh.Bvh.Nearest(eye, direction, out _, out int triangle)) return false;
        var look = RenderCamera.ToFloat(camera.LookDirection);
        Vector3 normal;
        if (triangle < RingTriangles) normal = Vector3.UnitY;
        else
        {
            int a = mesh.Indices[3 * triangle], b = mesh.Indices[3 * triangle + 1], c = mesh.Indices[3 * triangle + 2];
            normal = -Vector3.Normalize(Vector3.Cross(mesh.Positions[b] - mesh.Positions[a], mesh.Positions[c] - mesh.Positions[a]));
        }
        if (Vector3.Dot(normal, look) < 0) normal = -normal;
        lookNormal = Vector3.Normalize(normal);
        return true;
    }

    // The cube mesh: the base ring (24 sections, both windings) then the six faces in the texture's face order +Z, −Z, +X, −X, +Y, −Y.
    private const int RingTriangles = 2 * 2 * 23;
    private static MeshData BuildCube()
    {
        var builder = new SimpleMeshBuilder();
        Vector3 up = Vector3.UnitY, front = Vector3.UnitZ, left = Vector3.UnitX;
        var center = up * -CubeSide / 2 * 1.1f;
        for (int i = 0; i < 24; i++)
        {
            double angle = 360 * i / 23 * Math.PI / 180;
            var dir = left * (float)Math.Cos(angle) + front * (float)Math.Sin(angle);
            builder.Positions.Add(center + dir * (CubeSide - .75f));
            builder.Positions.Add(center + dir * (CubeSide + 1.1f));
        }
        List<int> strip = [];
        for (int i = 0; i + 2 < 48; i += 2)
        {
            strip.AddRange([i, i + 1, i + 2]);
            if (i + 3 < 48) strip.AddRange([i + 1, i + 3, i + 2]);
        }
        builder.Indices.AddRange(strip);
        for (int i = 0; i < strip.Count; i += 3) builder.Indices.AddRange([strip[i], strip[i + 2], strip[i + 1]]);
        for (int i = 0; i < 48; i++) { builder.Normals.Add(up); builder.TextureCoordinates.Add(new(-1, -1)); }
        int ringVertices = builder.Positions.Count;
        builder.AddCubeFace(Vector3.Zero, front, up, CubeSide, CubeSide, CubeSide);
        builder.AddCubeFace(Vector3.Zero, -front, up, CubeSide, CubeSide, CubeSide);
        builder.AddCubeFace(Vector3.Zero, left, up, CubeSide, CubeSide, CubeSide);
        builder.AddCubeFace(Vector3.Zero, -left, up, CubeSide, CubeSide, CubeSide);
        builder.AddCubeFace(Vector3.Zero, up, left, CubeSide, CubeSide, CubeSide);
        builder.AddCubeFace(Vector3.Zero, -up, -left, CubeSide, CubeSide, CubeSide);
        for (int i = ringVertices; i < builder.Positions.Count; i++)
        {
            int face = (i - ringVertices) / 4;
            var uv = builder.TextureCoordinates[i];
            builder.TextureCoordinates[i] = new(uv.X / 6 + face / 6f, uv.Y);
        }
        return builder.ToMesh([.. Enumerable.Repeat(new Color4(1, 1, 1, 1), builder.Positions.Count)]);
    }

    // Three arrows from the origin (X red, Y green (0,128,0), Z blue), coloured per vertex.
    private static MeshData BuildAxes()
    {
        var builder = new SimpleMeshBuilder();
        builder.AddArrow(Vector3.Zero, new(AxisLength, 0, 0), AxisDiameter, AxisHead, 8);
        builder.AddArrow(Vector3.Zero, new(0, AxisLength, 0), AxisDiameter, AxisHead, 8);
        builder.AddArrow(Vector3.Zero, new(0, 0, AxisLength), AxisDiameter, AxisHead, 8);
        int segment = builder.Positions.Count / 3;
        Color4[] axes = [new(1, 0, 0, 1), new(0, 128 / 255f, 0, 1), new(0, 0, 1, 1)];
        var colors = new Color4[builder.Positions.Count];
        for (int i = 0; i < colors.Length; i++) colors[i] = axes[Math.Min(2, i / segment)];
        return builder.ToMesh(colors);
    }

    // A white letter (alpha coverage) on transparent, Arial, sized in physical pixels, cropped to its ink (the quad centres the glyph).
    private static RenderTexture Label(string text)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface("Arial"), LabelEm, Brushes.White, 1);
        int width = (int)Math.Ceiling(formatted.WidthIncludingTrailingWhitespace) + 4, height = (int)Math.Ceiling(formatted.Height) + 4;
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen()) context.DrawText(formatted, new Point(2, 2));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        int left = width, right = -1, top = height, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] != 0) { left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y); }
        if (right < left) { left = top = 0; right = width - 1; bottom = height - 1; }
        // One transparent texel around the ink keeps linear filtering at the edges clean.
        left = Math.Max(0, left - 1); top = Math.Max(0, top - 1); right = Math.Min(width - 1, right + 1); bottom = Math.Min(height - 1, bottom + 1);
        int w = right - left + 1, h = bottom - top + 1;
        var cropped = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte a = pixels[((y + top) * width + x + left) * 4 + 3];
                int at = (y * w + x) * 4;
                cropped[at] = cropped[at + 1] = cropped[at + 2] = 255; cropped[at + 3] = a;
            }
        return RenderTexture.FromBgra(cropped, w, h);
    }

    public void Release() { labelQuad?.Dispose(); labelQuad = null; }
}
