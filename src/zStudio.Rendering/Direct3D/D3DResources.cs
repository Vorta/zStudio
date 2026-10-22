using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace zStudio.Rendering.Direct3D;

/// <summary>A cache entry of device resources made from retained CPU data, valid for one device generation.</summary>
internal abstract class DeviceResource : IDisposable
{
    private protected DeviceResource(D3DGraphics graphics) => Generation = graphics.Generation;
    public int Generation { get; }
    /// <summary>Released (early, by a node that no longer draws it, or with its device): the next use rebuilds it.</summary>
    public bool IsReleased { get; private set; }
    public void Dispose() { if (IsReleased) return; IsReleased = true; Release(); }
    private protected abstract void Release();
    /// <summary>The entry for <paramref name="graphics"/>: <paramref name="current"/> when it is still valid, otherwise a new one (the stale one released).</summary>
    public static T Refresh<T>(ref T? current, D3DGraphics graphics, Func<D3DGraphics, T> create) where T : DeviceResource
    {
        if (current is { IsReleased: false } valid && valid.Generation == graphics.Generation) return valid;
        current?.Dispose(); current = null;
        return current = create(graphics);
    }
}

/// <summary>The vertex and index buffers of one <see cref="MeshData"/> (immutable streams) plus its current colour stream.</summary>
internal sealed class MeshBuffers : DeviceResource
{
    private readonly D3DGraphics graphics;
    // The last two colour arrays: swapping a colour array back (fog, zone colours) reuses its upload.
    private (Color4[] Source, ID3D11Buffer Buffer)? current, previous;
    public MeshBuffers(D3DGraphics graphics, MeshData mesh) : base(graphics)
    {
        this.graphics = graphics;
        Positions = graphics.Device.CreateBuffer(mesh.Positions, BindFlags.VertexBuffer, ResourceUsage.Immutable);
        Normals = mesh.Normals is { Length: > 0 } normals ? graphics.Device.CreateBuffer(normals, BindFlags.VertexBuffer, ResourceUsage.Immutable) : null;
        TextureCoordinates = mesh.TextureCoordinates is { Length: > 0 } uv ? graphics.Device.CreateBuffer(uv, BindFlags.VertexBuffer, ResourceUsage.Immutable) : null;
        Indices = graphics.Device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        IndexCount = mesh.Indices.Length;
    }
    public ID3D11Buffer Positions { get; }
    public ID3D11Buffer? Normals { get; }
    public ID3D11Buffer? TextureCoordinates { get; }
    public ID3D11Buffer Indices { get; }
    public int IndexCount { get; }
    public ID3D11Buffer? Colors(Color4[]? source)
    {
        if (source is not { Length: > 0 }) return null;
        if (current is { } c && ReferenceEquals(c.Source, source)) return c.Buffer;
        if (previous is { } p && ReferenceEquals(p.Source, source)) { (current, previous) = (previous, current); return p.Buffer; }
        previous?.Buffer.Dispose();
        previous = current;
        var buffer = graphics.Device.CreateBuffer(source, BindFlags.VertexBuffer, ResourceUsage.Immutable);
        current = (source, buffer);
        return buffer;
    }
    private protected override void Release()
    {
        Positions.Dispose(); Normals?.Dispose(); TextureCoordinates?.Dispose(); Indices.Dispose();
        current?.Buffer.Dispose(); previous?.Buffer.Dispose(); current = previous = null;
    }
}

/// <summary>The position and index buffers of one <see cref="LineData"/>.</summary>
internal sealed class LineBuffers : DeviceResource
{
    public LineBuffers(D3DGraphics graphics, LineData line) : base(graphics)
    {
        Positions = graphics.Device.CreateBuffer(line.Positions, BindFlags.VertexBuffer, ResourceUsage.Immutable);
        Indices = graphics.Device.CreateBuffer(line.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        IndexCount = line.Indices.Length & ~1;
    }
    public ID3D11Buffer Positions { get; }
    public ID3D11Buffer Indices { get; }
    public int IndexCount { get; }
    private protected override void Release() { Positions.Dispose(); Indices.Dispose(); }
}

/// <summary>A node's instance matrices as a per-instance vertex stream.</summary>
internal sealed class InstanceBuffer : DeviceResource
{
    public InstanceBuffer(D3DGraphics graphics, IReadOnlyList<Matrix4x4> instances) : base(graphics)
    {
        Source = instances; Count = instances.Count;
        ReadOnlySpan<Matrix4x4> data = instances switch
        {
            Matrix4x4[] array => array,
            List<Matrix4x4> list => CollectionsMarshal.AsSpan(list),
            _ => [.. instances]
        };
        Buffer = graphics.Device.CreateBuffer(data, BindFlags.VertexBuffer, ResourceUsage.Immutable);
    }
    public IReadOnlyList<Matrix4x4> Source { get; }
    public int Count { get; }
    public ID3D11Buffer Buffer { get; }
    private protected override void Release() => Buffer.Dispose();
}

/// <summary>A texture with its full mip chain (the driver's box filter) and its shader view.</summary>
internal sealed class TextureResource : DeviceResource
{
    public TextureResource(D3DGraphics graphics, RenderTexture texture) : base(graphics)
    {
        var description = new Texture2DDescription
        {
            Width = (uint)texture.Width, Height = (uint)texture.Height, MipLevels = 0, ArraySize = 1,
            Format = texture.IsBgra ? Format.B8G8R8A8_UNorm : Format.R8G8B8A8_UNorm, SampleDescription = new(1, 0),
            Usage = ResourceUsage.Default, BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget, MiscFlags = ResourceOptionFlags.GenerateMips
        };
        Texture = graphics.Device.CreateTexture2D(description);
        try
        {
            View = graphics.Device.CreateShaderResourceView(Texture);
            graphics.Context.UpdateSubresource(texture.Pixels.AsSpan(0, texture.Width * texture.Height * 4), Texture, 0, (uint)texture.Width * 4);
            graphics.Context.GenerateMips(View);
        }
        catch { View?.Dispose(); Texture.Dispose(); throw; }
    }
    public ID3D11Texture2D Texture { get; }
    public ID3D11ShaderResourceView View { get; }
    private protected override void Release() { View.Dispose(); Texture.Dispose(); }
}
