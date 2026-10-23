namespace zStudio.Rendering.Direct3D;

/// <summary>The preview's face-orientation switch, shared by every material created with it.</summary>
internal sealed class FaceOrientation { public volatile bool Enabled; }

internal enum ZoneEmphasisMode { None = 0, Emphasis = 1, Preview = 2 }

/// <summary>
/// How the polygon zone pass tints: <see cref="ZoneEmphasisMode.None"/> (every zone at half strength);
/// <see cref="ZoneEmphasisMode.Emphasis"/> (polygons listing <see cref="Zone"/> at <see cref="Strong"/>, the others at
/// <see cref="Weak"/>); <see cref="ZoneEmphasisMode.Preview"/> (the zone colours alone at <see cref="Strong"/> opacity, a cleared
/// polygon dark at .55).
/// </summary>
internal readonly record struct ZoneEmphasis(ZoneEmphasisMode Mode, byte Zone, float Strong, float Weak);

/// <summary>An RGBA8 (or BGRA8) texture, sampled with linear filtering, nearest mip and wrapping; uploads build a full mip chain.</summary>
internal sealed class RenderTexture
{
    private DeviceCache<TextureResource>? resource;
    private RenderTexture(byte[] pixels, int width, int height, bool bgra, bool shared = false)
    {
        IsShared = shared;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width); ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if (pixels.LongLength < 4L * width * height) throw new ArgumentException("The pixel array is smaller than the texture.", nameof(pixels));
        Pixels = pixels; Width = width; Height = height; IsBgra = bgra;
    }
    public static RenderTexture FromRgba(byte[] rgba, int width, int height) => new(rgba, width, height, false);
    public static RenderTexture FromBgra(byte[] bgra, int width, int height) => new(bgra, width, height, true);
    /// <summary>A shared aid's texture (the axis letters): nodes never release its device copies.</summary>
    internal static RenderTexture FromBgra(byte[] bgra, int width, int height, bool shared) => new(bgra, width, height, true, shared);
    internal bool IsShared { get; }
    public byte[] Pixels { get; }
    public int Width { get; }
    public int Height { get; }
    public bool IsBgra { get; }
    /// <summary>The uploaded texture (built on first draw for each device).</summary>
    internal TextureResource Resource(D3DGraphics graphics) => (resource ??= new()).Get(graphics, g => new TextureResource(g, this));
    /// <summary>Releases the uploaded copies now (a later draw uploads them again).</summary>
    internal void ReleaseDeviceResources() { if (!IsShared) resource?.Release(); }
}

/// <summary>
/// A mesh material. Base colour: the vertex colour when <see cref="VertexColorBlendingFactor"/> is 1, else
/// <see cref="DiffuseColor"/>; times the texture when <see cref="DiffuseMap"/> is set. Lit (<see cref="Unlit"/> false) adds the
/// view term .5 + .5·|dot(eye, normal)| except for textured vertex-tint materials. Output alpha is multiplied by 1 − fade, and it
/// alpha-blends (SrcAlpha/InvSrcAlpha; destination alpha stays 1).
/// <para>
/// A preview material (<see cref="Create"/>) draws through the zStudio pass table: priority face orientation (its
/// <see cref="FaceOrientation"/> enabled) &gt; polygon zones &gt; overlay (overlay alpha &gt; 0) &gt; vertex tint (a vertex-tint
/// material, or a fade &gt; 0), else the plain diffuse pass; depth: horizon materials neither test nor write depth, transparent
/// meshes test Less without writing, opaque meshes write and test Less in a preview pass or LessEqual in the plain pass. A plain
/// material (<see cref="CreatePlain"/>) is the plain diffuse pass only: no overlay, zones, fade or face orientation; it tests
/// LessEqual and writes depth, even on a transparent mesh.
/// </para>
/// </summary>
internal sealed class PreviewMaterial
{
    private readonly bool preview, horizon, vertexTint;
    private readonly FaceOrientation? orientation;
    private RenderTexture? map;
    private ZoneEmphasis emphasis;
    private Color4 diffuse = new(1, 1, 1, 1), overlay;
    private bool unlit, polygonZones;
    private float vertexBlending, fade;

    private PreviewMaterial(bool preview, bool horizon, bool vertexTint, FaceOrientation? orientation)
    {
        this.preview = preview; this.horizon = horizon; this.vertexTint = vertexTint; this.orientation = orientation;
    }
    public static PreviewMaterial Create(bool horizon = false, bool vertexTint = false, FaceOrientation? orientation = null) => new(true, horizon, vertexTint, orientation);
    public static PreviewMaterial CreatePlain() => new(false, false, false, null);

    public bool IsPreview => preview;
    public bool Horizon => horizon;
    public bool VertexTint => vertexTint;
    public FaceOrientation? Orientation => orientation;
    // A changed value redraws the views (setting the same value renders nothing).
    private static void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value; RenderView.InvalidateAll();
    }

    public Color4 DiffuseColor { get => diffuse; set => Set(ref diffuse, value); }
    public RenderTexture? DiffuseMap { get => map; set => Set(ref map, value); }
    public bool Unlit { get => unlit; set => Set(ref unlit, value); }
    public float VertexColorBlendingFactor { get => vertexBlending; set => Set(ref vertexBlending, value); }

    private void RequirePreview() { if (!preview) throw new InvalidOperationException("Diagnostic overlays require a preview material."); }
    /// <summary>Diagnostic tint: lerp(rgb, overlay.rgb, overlay.a); alpha, cutouts and depth policy unchanged. Alpha 0 is none.</summary>
    public Color4 Overlay { get { RequirePreview(); return overlay; } set { RequirePreview(); Set(ref overlay, value); } }
    /// <summary>Tints each polygon by the zone set its vertex colours carry (ids n/255 in RGB, count n/3 in A).</summary>
    public bool PolygonZones { get { RequirePreview(); return polygonZones; } set { RequirePreview(); Set(ref polygonZones, value); } }
    public ZoneEmphasis Emphasis { get { RequirePreview(); return emphasis; } set { RequirePreview(); Set(ref emphasis, value); } }
    /// <summary>Draws the surfaces at 1 − fade opacity.</summary>
    public float Fade { get { RequirePreview(); return fade; } set { RequirePreview(); Set(ref fade, value); } }

    /// <summary>A preview copy (same pass flags and state) for a mesh that must look different from the others sharing this one.</summary>
    public PreviewMaterial Copy()
    {
        RequirePreview();
        var copy = Create(horizon, vertexTint, orientation);
        copy.DiffuseColor = DiffuseColor; copy.DiffuseMap = DiffuseMap; copy.Unlit = Unlit; copy.VertexColorBlendingFactor = VertexColorBlendingFactor;
        copy.Overlay = Overlay; copy.PolygonZones = PolygonZones; copy.Emphasis = Emphasis; copy.Fade = Fade;
        return copy;
    }
    /// <summary>Gives a <see cref="Copy"/> this material's current appearance again, keeping the copy's fade.</summary>
    public void CopyTo(PreviewMaterial copy)
    {
        copy.DiffuseColor = DiffuseColor; copy.DiffuseMap = DiffuseMap; copy.Unlit = Unlit; copy.VertexColorBlendingFactor = VertexColorBlendingFactor;
        copy.Overlay = Overlay; copy.PolygonZones = PolygonZones; copy.Emphasis = Emphasis;
    }
    /// <summary>A plain material with this one's colour, texture, lighting and vertex blending.</summary>
    public PreviewMaterial ClonePlain()
    {
        var plain = CreatePlain();
        plain.diffuse = diffuse; plain.map = map; plain.unlit = unlit; plain.vertexBlending = vertexBlending;
        return plain;
    }
}
