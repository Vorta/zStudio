using System.Numerics;
using System.Windows;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Draws a view's nodes. Per frame: opaque meshes and lines in ascending render order (among equals
/// horizon materials first, then submission order), then the ground grids, then transparent meshes in submission order (a sorting group's back to front), then each
/// top-most group over a cleared depth buffer. Composition: p · instance · transform · parent transforms.
/// </summary>
internal sealed class SceneRenderer
{
    private readonly List<Draw> opaque = [], transparent = [];
    private readonly List<(GroundGridNode Grid, Matrix4x4 World)> grids = [];
    private readonly List<(TopMostGroupNode Group, Matrix4x4 World)> topMost = [];
    private readonly List<Draw> overlay = [];
    private readonly List<(float Distance, int Index)> sortScratch = [];
    private int sequence;
    private readonly record struct Draw(GeometryNode Node, Matrix4x4 World, int Order, int Sequence);

    /// <summary>Whether a node draws itself (its own visibility, flags and guide state; its ancestors are checked by the walk).</summary>
    internal static bool IsDrawn(RenderView view, Node node) => node.IsShown && node.RendersItself && (!node.IsGuide || view.GuidesVisible);

    public void Render(D3DGraphics g, RenderView view, in FrameConstants frame, Vector3 eye, Vector3 look)
    {
        opaque.Clear(); transparent.Clear(); grids.Clear(); topMost.Clear(); sequence = 0;
        Collect(view, view.Items, Matrix4x4.Identity, eye);
        // Stable: equal render orders draw horizon materials first (no depth test: nothing drawn before them may be painted over),
        // then keep submission order.
        opaque.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order)
            : IsHorizon(a.Node) != IsHorizon(b.Node) ? (IsHorizon(a.Node) ? -1 : 1) : a.Sequence.CompareTo(b.Sequence));
        var context = g.Context;
        g.Write(g.FrameBuffer, frame);
        context.VSSetConstantBuffer(0, g.FrameBuffer); context.GSSetConstantBuffer(0, g.FrameBuffer); context.PSSetConstantBuffer(0, g.FrameBuffer);
        context.VSSetConstantBuffer(1, g.ObjectBuffer); context.GSSetConstantBuffer(1, g.ObjectBuffer); context.PSSetConstantBuffer(1, g.ObjectBuffer);
        context.OMSetBlendState(g.AlphaBlend);
        foreach (var draw in opaque) DrawNode(g, draw, inTransparentBucket: false);
        foreach (var (grid, _) in grids) DrawGrid(g, grid, frame, eye, look);
        foreach (var draw in transparent) DrawNode(g, draw, inTransparentBucket: true);
        foreach (var (group, world) in topMost)
        {
            context.ClearDepthStencilView(Target.Depth!, DepthStencilClearFlags.Depth, 1, 0);
            overlay.Clear();
            CollectOverlay(view, group.Children, world);
            foreach (var draw in overlay) DrawNode(g, draw, draw.Node is MeshNode { IsTransparent: true });
        }
    }

    /// <summary>The target being drawn (set by the view for the frame).</summary>
    public (ID3D11RenderTargetView? Color, ID3D11DepthStencilView? Depth) Target { get; set; }

    private void Collect(RenderView view, IReadOnlyList<Node> nodes, Matrix4x4 parent, Vector3 eye)
    {
        foreach (var node in nodes)
        {
            if (!IsDrawn(view, node)) continue;
            var world = node.Transform is { } local ? local * parent : parent;
            switch (node)
            {
                case TopMostGroupNode group: topMost.Add((group, world)); break;
                case SortingGroupNode { SortingEnabled: true } sorting: CollectSorted(view, sorting, world, eye); break;
                case GroupNode group: Collect(view, group.Children, world, eye); break;
                case TransformGizmo { Control: { } control }: Collect(view, [control], world, eye); break;
                case MeshNode mesh: (mesh.IsTransparent ? transparent : opaque).Add(new(mesh, world, mesh.RenderOrder, sequence++)); break;
                case LineNode line: opaque.Add(new(line, world, line.RenderOrder, sequence++)); break;
                case GroundGridNode grid: grids.Add((grid, world)); break;
            }
        }
    }

    // Opaque children first in their order, then the transparent ones back to front by squared eye distance to their world bounds
    // (instances included); equal distances keep their order.
    private void CollectSorted(RenderView view, SortingGroupNode group, Matrix4x4 world, Vector3 eye)
    {
        var children = group.Children;
        List<Node> unsorted = [];
        int start = sortScratch.Count;
        for (int i = 0; i < children.Count; i++)
        {
            if (children[i] is MeshNode { IsTransparent: true } mesh)
            {
                var local = mesh.Transform is { } t ? t * world : world;
                sortScratch.Add((BoundsDistance(mesh, local, eye), i));
            }
            else unsorted.Add(children[i]);
        }
        Collect(view, unsorted, world, eye);
        var sorted = sortScratch.GetRange(start, sortScratch.Count - start);
        sortScratch.RemoveRange(start, sortScratch.Count - start);
        sorted.Sort((a, b) => a.Distance != b.Distance ? b.Distance.CompareTo(a.Distance) : a.Index.CompareTo(b.Index));
        List<Node> ordered = new(sorted.Count);
        foreach (var (_, index) in sorted) ordered.Add(children[index]);
        Collect(view, ordered, world, eye);
    }

    private static float BoundsDistance(MeshNode mesh, Matrix4x4 world, Vector3 eye)
    {
        if (mesh.Geometry is not { Positions.Length: > 0 } geometry) return 0;
        var (boundsMin, boundsMax) = mesh.WorldBounds(world, geometry, geometry.Bounds);
        var outside = Vector3.Max(Vector3.Zero, Vector3.Max(boundsMin - eye, eye - boundsMax));
        return outside.LengthSquared();
    }

    // Everything under a top-most group, in tree order.
    private void CollectOverlay(RenderView view, IReadOnlyList<Node> nodes, Matrix4x4 parent)
    {
        foreach (var node in nodes)
        {
            if (!IsDrawn(view, node)) continue;
            var world = node.Transform is { } local ? local * parent : parent;
            switch (node)
            {
                case GroupNode group: CollectOverlay(view, group.Children, world); break;
                case TransformGizmo { Control: { } control }: CollectOverlay(view, [control], world); break;
                case GeometryNode geometry: overlay.Add(new(geometry, world, geometry.RenderOrder, sequence++)); break;
            }
        }
    }

    private static bool IsHorizon(GeometryNode node) => node is MeshNode { Material.Horizon: true };

    private static void DrawNode(D3DGraphics g, in Draw draw, bool inTransparentBucket)
    {
        if (draw.Node is MeshNode mesh) DrawMesh(g, mesh, draw.World, inTransparentBucket);
        else if (draw.Node is LineNode line) DrawLine(g, line, draw.World);
    }

    private static void DrawMesh(D3DGraphics g, MeshNode node, Matrix4x4 world, bool transparent)
    {
        if (node.Material is not { } material || node.Geometry is not { } geometry || geometry.Buffers(g) is not { } buffers) return;
        var instances = node.InstanceStream(g);
        if (instances is { Count: 0 }) return;
        var context = g.Context;
        // Pass: the preview pass table (priority face orientation > polygon zones > overlay > vertex tint or fade), else plain.
        // Depth: the plain diffuse pass tests LessEqual and writes (plain materials in either bucket, preview materials drawn
        // opaque without a preview pass); the preview passes test Less and write when opaque; transparent preview surfaces
        // test Less without writing; horizon surfaces neither test nor write.
        ID3D11PixelShader pixel;
        ID3D11DepthStencilState depth;
        if (!material.IsPreview) { pixel = g.PSPlain; depth = g.DepthLessEqualWrite; }
        else
        {
            pixel = material.Orientation?.Enabled == true ? g.PSOrientation : material.PolygonZones ? g.PSPolygonZones
                : material.Overlay.Alpha > 0 ? g.PSOverlay : material.VertexTint || material.Fade > 0 ? g.PSTint : g.PSPlain;
            depth = material.Horizon ? g.DepthNone : transparent ? g.DepthLessNoWrite
                : ReferenceEquals(pixel, g.PSPlain) ? g.DepthLessEqualWrite : g.DepthLessWrite;
        }
        var texture = material.DiffuseMap?.Resource(g);
        var emphasis = material.IsPreview ? material.Emphasis : default;
        var constants = new ObjectConstants
        {
            World = world,
            Diffuse = material.DiffuseColor.ToVector4(),
            Overlay = material.IsPreview ? material.Overlay.ToVector4() : default,
            Emphasis = new(emphasis.Zone / 255f, emphasis.Strong, emphasis.Weak, (float)emphasis.Mode),
            Params = new(material.IsPreview ? material.Fade : 0, material.VertexColorBlendingFactor, 0, 0),
            Textured = texture != null ? 1u : 0, Unlit = material.Unlit ? 1u : 0
        };
        g.Write(g.ObjectBuffer, constants);
        context.IASetInputLayout(g.MeshLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        var colors = buffers.Colors(geometry.Colors);
        context.IASetVertexBuffers(0, 5,
            [buffers.Positions, buffers.Normals ?? g.ZeroBuffer, buffers.TextureCoordinates ?? g.ZeroBuffer, colors ?? g.ZeroBuffer, instances?.Buffer ?? g.IdentityInstance],
            [12u, buffers.Normals != null ? 12u : 0, buffers.TextureCoordinates != null ? 8u : 0, colors != null ? 16u : 0, 64u], [0u, 0, 0, 0, 0]);
        context.IASetIndexBuffer(buffers.Indices, Format.R32_UInt, 0);
        context.VSSetShader(g.VSMesh); context.GSSetShader(null); context.PSSetShader(pixel);
        context.PSSetShaderResource(0, texture?.View!); context.PSSetSampler(0, g.SurfaceSampler);
        context.RSSetState(g.Rasterizer(node.CullMode, node.DepthBias, node.IsDepthClipEnabled));
        context.OMSetDepthStencilState(depth);
        context.DrawIndexedInstanced((uint)buffers.IndexCount, (uint)(instances?.Count ?? 1), 0, 0, 0);
    }

    private static void DrawLine(D3DGraphics g, LineNode node, Matrix4x4 world)
    {
        if (node.Geometry is not { } geometry || geometry.Buffers(g) is not { IndexCount: > 0 } buffers) return;
        var instances = node.InstanceStream(g);
        if (instances is { Count: 0 }) return;
        var context = g.Context;
        var color = node.Color;
        g.Write(g.ObjectBuffer, new ObjectConstants
        {
            World = world,
            Diffuse = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f),
            Params = new(0, 0, (float)node.Thickness, 0)
        });
        context.IASetInputLayout(g.LineLayout);
        context.IASetPrimitiveTopology(PrimitiveTopology.LineList);
        context.IASetVertexBuffers(0, 2, [buffers.Positions, instances?.Buffer ?? g.IdentityInstance], [12u, 64u], [0u, 0]);
        context.IASetIndexBuffer(buffers.Indices, Format.R32_UInt, 0);
        context.VSSetShader(g.VSLine); context.GSSetShader(g.GSLine); context.PSSetShader(g.PSLine);
        context.RSSetState(g.Rasterizer(CullMode.None, node.DepthBias, node.IsDepthClipEnabled));
        context.OMSetDepthStencilState(g.DepthLessEqualWrite);
        context.DrawIndexedInstanced((uint)buffers.IndexCount, (uint)(instances?.Count ?? 1), 0, 0, 0);
        context.GSSetShader(null);
    }

    private static void DrawGrid(D3DGraphics g, GroundGridNode grid, FrameConstants frame, Vector3 eye, Vector3 look)
    {
        grid.UpdateSpacing(eye, look);
        var color = grid.GridColor;
        frame.GridParams = new(grid.ActualSpacing, (float)grid.Offset, 0, 0);
        frame.GridColor = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        g.Write(g.FrameBuffer, frame);
        var context = g.Context;
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleStrip);
        context.VSSetShader(g.VSGrid); context.GSSetShader(null); context.PSSetShader(g.PSGrid);
        context.RSSetState(g.GridRasterizer);
        context.OMSetDepthStencilState(g.DepthLessEqualNoWrite);
        context.Draw(4, 0);
        frame.GridParams = default; frame.GridColor = default;
        g.Write(g.FrameBuffer, frame);
    }
}
