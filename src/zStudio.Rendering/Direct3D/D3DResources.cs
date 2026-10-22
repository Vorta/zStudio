using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Device resources made from retained CPU data. Each one registers with its device, which releases whatever is still alive
/// when it is disposed (a recovery or the view closing): the interop releases native objects only on Dispose, never on
/// collection.
/// </summary>
internal abstract class DeviceResource : IDisposable
{
    private protected DeviceResource(D3DGraphics graphics) { Graphics = graphics; graphics.Track(this); }
    public D3DGraphics Graphics { get; }
    /// <summary>Released (early, by a node that no longer draws it, or with its device): the next use rebuilds it.</summary>
    public bool IsReleased { get; private set; }
    public bool IsValidFor(D3DGraphics graphics) => !IsReleased && ReferenceEquals(Graphics, graphics) && !graphics.IsDisposed;
    public void Dispose()
    {
        if (IsReleased) return;
        IsReleased = true; Graphics.Untrack(this); Release();
    }
    private protected abstract void Release();
}

/// <summary>The device copies of one piece of retained data, one per live device (a shared aid drawn by several views keeps one each).</summary>
internal sealed class DeviceCache<T> where T : DeviceResource
{
    private readonly List<T> entries = new(1);
    public T Get(D3DGraphics graphics, Func<D3DGraphics, T> create)
    {
        foreach (var entry in entries) if (entry.IsValidFor(graphics)) return entry;
        // Entries of released resources or disposed devices go; their native objects were released with them.
        for (int i = entries.Count - 1; i >= 0; i--)
            if (entries[i].IsReleased || entries[i].Graphics.IsDisposed) { entries[i].Dispose(); entries.RemoveAt(i); }
        var created = create(graphics);
        entries.Add(created);
        return created;
    }
    public void Release() { foreach (var entry in entries) entry.Dispose(); entries.Clear(); }
}

/// <summary>The vertex and index buffers of one <see cref="MeshData"/> (immutable streams) plus its current colour stream.</summary>
internal sealed class MeshBuffers : DeviceResource
{
    // The current colour array's buffer, and the previous one's while that array is still alive elsewhere: swapping a colour
    // array back (fog, zone colours) reuses its upload without this cache keeping the old array alive.
    private (Color4[] Source, ID3D11Buffer Buffer)? current;
    private (WeakReference<Color4[]> Source, ID3D11Buffer Buffer)? previous;
    public MeshBuffers(D3DGraphics graphics, MeshData mesh) : base(graphics)
    {
        try
        {
            Positions = graphics.Device.CreateBuffer(mesh.Positions, BindFlags.VertexBuffer, ResourceUsage.Immutable);
            Normals = mesh.Normals is { Length: > 0 } normals ? graphics.Device.CreateBuffer(normals, BindFlags.VertexBuffer, ResourceUsage.Immutable) : null;
            TextureCoordinates = mesh.TextureCoordinates is { Length: > 0 } uv ? graphics.Device.CreateBuffer(uv, BindFlags.VertexBuffer, ResourceUsage.Immutable) : null;
            Indices = graphics.Device.CreateBuffer(mesh.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        }
        catch { Dispose(); throw; }
        IndexCount = mesh.Indices.Length;
    }
    public ID3D11Buffer Positions { get; } = null!;
    public ID3D11Buffer? Normals { get; }
    public ID3D11Buffer? TextureCoordinates { get; }
    public ID3D11Buffer Indices { get; } = null!;
    public int IndexCount { get; }
    public ID3D11Buffer? Colors(Color4[]? source)
    {
        if (source is not { Length: > 0 }) return null;
        if (current is { } c && ReferenceEquals(c.Source, source)) return c.Buffer;
        if (previous is { } p && p.Source.TryGetTarget(out var old) && ReferenceEquals(old, source))
        {
            var swapped = current;
            current = (source, p.Buffer);
            previous = swapped is { } s ? (new WeakReference<Color4[]>(s.Source), s.Buffer) : null;
            return p.Buffer;
        }
        previous?.Buffer.Dispose();
        previous = current is { } last ? (new WeakReference<Color4[]>(last.Source), last.Buffer) : null;
        var buffer = Graphics.Device.CreateBuffer(source, BindFlags.VertexBuffer, ResourceUsage.Immutable);
        current = (source, buffer);
        return buffer;
    }
    private protected override void Release()
    {
        Positions?.Dispose(); Normals?.Dispose(); TextureCoordinates?.Dispose(); Indices?.Dispose();
        current?.Buffer.Dispose(); previous?.Buffer.Dispose(); current = null; previous = null;
    }
}

/// <summary>The position and index buffers of one <see cref="LineData"/>.</summary>
internal sealed class LineBuffers : DeviceResource
{
    public LineBuffers(D3DGraphics graphics, LineData line) : base(graphics)
    {
        try
        {
            Positions = graphics.Device.CreateBuffer(line.Positions, BindFlags.VertexBuffer, ResourceUsage.Immutable);
            Indices = graphics.Device.CreateBuffer(line.Indices, BindFlags.IndexBuffer, ResourceUsage.Immutable);
        }
        catch { Dispose(); throw; }
        IndexCount = line.Indices.Length & ~1;
    }
    public ID3D11Buffer Positions { get; } = null!;
    public ID3D11Buffer Indices { get; } = null!;
    public int IndexCount { get; }
    private protected override void Release() { Positions?.Dispose(); Indices?.Dispose(); }
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
        try { Buffer = graphics.Device.CreateBuffer(data, BindFlags.VertexBuffer, ResourceUsage.Immutable); }
        catch { Dispose(); throw; }
    }
    public IReadOnlyList<Matrix4x4> Source { get; }
    public int Count { get; }
    public ID3D11Buffer Buffer { get; } = null!;
    private protected override void Release() => Buffer?.Dispose();
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
        try
        {
            Texture = graphics.Device.CreateTexture2D(description);
            View = graphics.Device.CreateShaderResourceView(Texture);
            graphics.Context.UpdateSubresource(texture.Pixels.AsSpan(0, texture.Width * texture.Height * 4), Texture, 0, (uint)texture.Width * 4);
            graphics.Context.GenerateMips(View);
        }
        catch { Dispose(); throw; }
    }
    public ID3D11Texture2D Texture { get; } = null!;
    public ID3D11ShaderResourceView View { get; } = null!;
    private protected override void Release() { View?.Dispose(); Texture?.Dispose(); }
}
