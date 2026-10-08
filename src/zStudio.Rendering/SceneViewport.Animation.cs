using System.Numerics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit;
using HelixToolkit.Maths;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using SharpDX.Direct3D11;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;
using MeshGeometry3D = HelixToolkit.SharpDX.MeshGeometry3D;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

namespace Recoil.Zbd.Rendering;

public sealed partial class SceneViewport
{
    private AnimationPreviewContext? animationContext;
    private AssetResolver? animationResolver;
    private CancellationToken animationToken;
    private readonly Dictionary<long, AnimatedMesh[]> animatedMeshes = [];
    private readonly Dictionary<int, IReadOnlyList<MeshPart>> animationGeometry = [];
    private readonly Dictionary<string, AnimationTextureSlot> animationTextureSlots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AnimationTextureSlot> animationTextureReferences = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<IReadOnlyList<string>, Dictionary<string, AnimationTextureSlot?>> animationTextureCycles = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<DecodedImage, TextureModel> animationTextureModels = [];
    private readonly Dictionary<int, (string? Name, AnimationTextureSlot? Slot)> animationBaseTextures = [];
    private long animationTextureNameUnits;
    private long animationTextureLookupUnits;
    private long animationTextureCycleMembers, animationTextureCycleMemberVisits;
    internal long MaximumAnimationTextureCycleMembers { get; set; } = 1_000_000;
    private bool animationTextureLimitReported;
    private int animationTextureNotices;
    private readonly HashSet<int> replacedNodes = [];
    private Vector3 levelMin, levelMax;
    private AnimationFrame? animationFrame;
    private bool updatingAnimation, animationEffectLighting = true;
    private SortingGroupModel3D? animationGroup;
    private OITRenderType previousTransparency;
    public int AnimationMeshCount => animatedMeshes.Values.Sum(m => m.Length);

    public async Task ShowAnimationAsync(AnimationPreviewContext context, AnimationFrame frame, AssetResolver resolver, bool includeLevel, CancellationToken token, int lod = 0, bool showHorizon = true, CancellationToken? previewLifetime = null)
    {
        int expectedGeneration = generation + 1;
        if (includeLevel)
            await ShowAsync(context.World, context.World.Assets.First(a => a.Kind == AssetKind.World), resolver, null, lod, token, showHorizon, context.Mission);
        else { Clear(); AttachEffects(); QueueRenderSize(); }
        token.ThrowIfCancellationRequested();
        if (generation != expectedGeneration) return;
        previewTextures ??= new PreviewTextureCache(textureMemory, resolver.BeginTextureLookup(context.World.Path), masks: false);
        animationContext = context; animationResolver = resolver; animationToken = token;
        Mission = context.Mission; PreviewScene = context.Scene; InspectionNodes = context.InspectionNodes; InspectionSourcePath = context.World.Path; horizonEnabled = showHorizon; ConfigureHorizon(context.Scene);
        // Helix 3.1.2's OIT paths drop the unlit DiffuseMaterial effect cards.
        // Standard alpha blending with a sorted group renders their actual alpha.
        previousTransparency = viewport.OITRenderMode; viewport.OITRenderMode = OITRenderType.None;
        animationGroup = sceneAlphaGroup ?? new() { EnableSorting = true, SortTransparentOnly = true, SortingInterval = 0 };
        if (sceneAlphaGroup == null) viewport.Items.Add(animationGroup);
        levelMin = sceneMin; levelMax = sceneMax;
        // Spawned effect cards can change on their first visible tick. Prepare the
        // template maps before enabling playback, as with bound material cycles.
        foreach (string name in context.Effects.Values.SelectMany(e => e.Textures)) QueueAnimationTexture(name);
        UpdateAnimationFrame(frame);
        await Task.WhenAll(animationTextureSlots.Values.Select(slot => slot.Load).ToArray());
        token.ThrowIfCancellationRequested(); if (generation != expectedGeneration) return; UpdateAnimationFrame(frame);
        animationToken = previewLifetime ?? token;
        if (includeLevel) FrameAll(); else FrameAnimation(frame);
    }

    public void UpdateAnimationFrame(AnimationFrame frame, bool followCamera = false, bool effectLighting = true)
    {
        if (animationContext == null || animationToken.IsCancellationRequested || updatingAnimation) return;
        updatingAnimation = true; animationFrame = frame; animationEffectLighting = effectLighting; ++inspectionSerial;
        try
        {
        var scene = animationContext.Scene; var poses = frame.Nodes.ToDictionary(p => p.Id);
        foreach (long expired in animatedMeshes.Where(pair => !poses.TryGetValue(pair.Key, out var pose) ||
            pair.Value.Any(item => !inspectionMeshes.TryGetValue(item.Mesh, out var meta) || meta.Node != pose.SourceNode || meta.Model != pose.Model)).Select(pair => pair.Key).ToArray())
        {
            foreach (var item in animatedMeshes[expired]) { inspectionMeshes.Remove(item.Mesh); animationGroup?.Children.Remove(item.Mesh); item.Mesh.Dispose(); }
            animatedMeshes.Remove(expired);
        }
        // Shared world poses replace their baseline instance. Owned animation
        // copies and effect cards must not erase the source actor behind them.
        var replacement = frame.Nodes.Where(p => p.Id >> 32 == 0).Select(p => p.SourceNode).ToHashSet();
        bool replacementChanged = !replacedNodes.SetEquals(replacement);
        if (replacementChanged) { replacedNodes.Clear(); replacedNodes.UnionWith(replacement); }
        if (replacementChanged) foreach (var mesh in meshes)
        {
            var visible = placements[mesh].Where(p => !replacedNodes.Contains(p.NodeIndex)).ToArray();
            visiblePlacements[mesh] = visible; mesh.Instances = visible.Select(p => p.Transform).ToList();
            mesh.Visibility = visible.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        sceneMin = levelMin; sceneMax = levelMax;
        foreach (var pose in frame.Nodes)
        {
            if (pose.Model < 0 || pose.Model >= scene.Models.Count) continue;
            if (!animatedMeshes.TryGetValue(pose.Id, out var items))
            {
                if (!animationGeometry.TryGetValue(pose.Model, out var parts)) animationGeometry[pose.Model] = parts = GeometryBuilder.Build(scene.Models[pose.Model], token: animationToken);
                items = parts.Select(part =>
                {
                    bool horizon = IsHorizon(pose.SourceNode);
                    var material = PreviewMaterials.Create(horizon, part.Colors.Length != 0); material.EnableUnLit = true;
                    material.VertexColorBlendingFactor = part.Colors.Length == 0 ? 0 : 1;
                    var mesh = new MeshGeometryModel3D { Geometry = Mesh(part), Material = material, CullMode = CullMode.None, IsThrowingShadow = false, RenderOrder = horizon ? 0 : 1, IsDepthClipEnabled = !horizon };
                    RegisterInspectionMesh(mesh, part.MaterialIndex, pose.Id, pose.SourceNode, pose.Model);
                    inspectionPolygons[mesh] = part.VertexPolygons;
                    int source = pose.SourceNode; mesh.MouseDown3D += (_, e) =>
                    {
                        if (!IsPickupDragging && e is MouseDown3DEventArgs { OriginalInputEventArgs: System.Windows.Input.MouseButtonEventArgs { ChangedButton: System.Windows.Input.MouseButton.Left } })
                        { SelectFramingNode(source); NodeSelected?.Invoke(source); }
                    };
                    animationGroup!.Children.Add(mesh);
                    return new AnimatedMesh(mesh, material, part);
                }).ToArray();
                animatedMeshes[pose.Id] = items;
            }
            IReadOnlyList<MeshPart>? morphed = null;
            var model = scene.Models[pose.Model];
            if (model.Morphs.Length == model.Vertices.Length && items.Any(i => i.Morph != pose.Morph))
                morphed = GeometryBuilder.Build(model with { Vertices = model.Vertices.Select((v, i) => v + model.Morphs[i] * pose.Morph).ToArray() }, token: animationToken);
            foreach (var item in items)
            {
                bool horizon = IsHorizon(pose.SourceNode);
                item.Mesh.Visibility = pose.Visible && pose.Opacity > 0 && (!horizon || horizonEnabled) ? Visibility.Visible : Visibility.Collapsed;
                var transform = CameraFacingTransform(pose, model);
                item.Mesh.Transform = new MatrixTransform3D(ToWpf(HorizonTransform(pose.SourceNode, transform)));
                if (morphed != null)
                {
                    var part = morphed.FirstOrDefault(p => p.MaterialIndex == item.Part.MaterialIndex);
                    if (part != null) { item.Mesh.Geometry = Mesh(part); item.VertexTint = null; }
                    item.Morph = pose.Morph;
                }
                JsonMaterial(scene, item.Part.MaterialIndex, out var color, out int textureIndex);
                string? textureName;
                AnimationTextureSlot? textureSlot;
                if (pose.Texture == null && animationContext.MaterialCycles.TryGetValue(item.Part.MaterialIndex, out var cycle))
                {
                    var prepared = PrepareAnimationTextureCycle(cycle.Textures);
                    textureName = cycle.At(pose.CycleTime, pose.Variant);
                    // At returns a stored member reference. Both this lookup and
                    // prepared-cycle identity lookup are independent of name length.
                    textureSlot = textureName == null ? null : prepared?.GetValueOrDefault(textureName);
                }
                else if (pose.Texture is { } authoredTexture) { textureName = authoredTexture; textureSlot = QueueAnimationTexture(authoredTexture); }
                else if (textureIndex >= 0 && textureIndex < scene.Textures.Count)
                {
                    if (!animationBaseTextures.TryGetValue(textureIndex, out var prepared))
                    {
                        // Parsed JSON strings may decode on every GetValue call. Freeze each selected
                        // source name once before frames or shared meshes repeatedly request it.
                        if (animationBaseTextures.Count >= 8192)
                        { AnimationTextureLimit("Animation base texture reference allowance reached."); textureName = ""; textureSlot = null; }
                        else
                        {
                            textureName = scene.Textures[textureIndex].Text("name");
                            textureSlot = textureName == null ? null : QueueAnimationTexture(textureName);
                            animationBaseTextures.Add(textureIndex, (textureSlot == null && textureName != null ? "" : textureName, textureSlot));
                        }
                    }
                    else { textureName = prepared.Name; textureSlot = prepared.Slot; }
                }
                else { textureName = null; textureSlot = null; }
                bool alphaTexture = false;
                TextureModel? diffuseMap = null;
                if (textureName != null)
                {
                    if (textureSlot?.Texture is { } texture) { diffuseMap = texture; alphaTexture = textureSlot.Alpha; }
                    else item.Mesh.Visibility = Visibility.Collapsed;
                }
                item.Material.DiffuseMap = diffuseMap;
                float alpha = pose.Opacity * color.Alpha;
                SetAnimationColor(item, AnimationColor(pose, color, frame, effectLighting));
                item.Mesh.IsTransparent = !horizon && (alphaTexture || alpha < 1);
                if (!horizon && pose.Visible && item.Mesh.Geometry?.Positions is { } positions) IncludeBounds(positions, transform);
            }
        }
        if (followCamera && frame.Camera is { } c)
        {
            ChangeProjection("perspective"); axisView = null; autoPerspective = false;
            var live = (HCamera)viewport.Camera!;
            authoredCameraPose = true;
            live.Position = new(c.Position.X, c.Position.Y, c.Position.Z);
            var look = c.Target - c.Position; if (look.LengthSquared() > 1e-8f) live.LookDirection = new(look.X, look.Y, look.Z);
            live.FieldOfView = Math.Clamp(c.FieldOfView, 1, 170);
            orbitPivot = null; navigationReferenceDistance = null;
        }
        if (!preparingCamera) viewport.InvalidateRender();
        }
        finally { updatingAnimation = false; }
    }
    private Matrix4x4 CameraFacingTransform(AnimationNodePose pose, GameModel model)
    {
        var transform = pose.Transform;
        // The retail facade projection differs; this preview keeps effect cards readable.
        if ((pose.Texture != null || model.Metadata.Int("model_type") == 1) && viewport.Camera is ProjectionCamera camera && Matrix4x4.Decompose(transform, out var size, out _, out var position))
        {
            var p = camera.Position; var facing = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            if (camera is OrthographicCamera) facing = position - new Vector3((float)camera.LookDirection.X, (float)camera.LookDirection.Y, (float)camera.LookDirection.Z);
            if ((model.Metadata.UInt("flags") & 0x10) == 0 && pose.Texture == null) facing.Y = position.Y;
            var up = camera.UpDirection;
            var billboardUp = Math.Abs(Vector3.Dot(Vector3.Normalize(facing - position), Vector3.UnitY)) > .999f
                ? new Vector3((float)up.X, (float)up.Y, (float)up.Z) : Vector3.UnitY;
            if (Vector3.DistanceSquared(position, facing) < 1e-10f) facing = position + Vector3.UnitZ;
            transform = Matrix4x4.CreateScale(size) * Matrix4x4.CreateBillboard(position, facing, billboardUp, Vector3.UnitZ);
        }
        return transform;
    }
    private Color4 AnimationColor(AnimationNodePose pose, Color4 color, AnimationFrame frame, bool effectLighting)
    {
        Vector3 rgb = new(color.Red, color.Green, color.Blue);
        if (effectLighting)
            foreach (var light in frame.Lights.Where(l => l.Active && l.Range > 0))
            {
                float falloff = Math.Clamp(1 - Vector3.Distance(pose.Transform.Translation, light.Position) / light.Range, 0, 1);
                rgb = Vector3.Lerp(rgb, Vector3.Max(rgb * .65f, light.Color), Math.Clamp(light.Intensity * falloff, 0, 1));
            }
        if (effectLighting && frame.Fog is { Enabled: true } fog && viewport.Camera is ProjectionCamera camera)
        {
            var p = camera.Position;
            float distance = Vector3.Distance(pose.Transform.Translation, new((float)p.X, (float)p.Y, (float)p.Z));
            rgb = Vector3.Lerp(rgb, fog.Color, Math.Clamp((distance - fog.Start) / Math.Max(.001f, fog.End - fog.Start), 0, 1));
        }
        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        return new(rgb.X, rgb.Y, rgb.Z, pose.Opacity * color.Alpha);
    }
    private static void SetAnimationColor(AnimatedMesh item, Color4 color)
    {
        item.Material.DiffuseColor = color;
        // The vertex-tint shader consumes geometry colors, including fog and opacity.
        if (item.Part.Colors.Length != 0 && item.VertexTint != color && item.Mesh.Geometry is MeshGeometry3D colored)
        {
            colored.Colors = VertexColors(item.Part, color);
            item.VertexTint = color;
        }
    }
    private void RefreshAnimationCamera(bool updateHitTests = false)
    {
        if (animationFrame is not { } frame || animationContext == null || updatingAnimation) return;
        var scene = animationContext.Scene;
        // Camera motion changes only facade orientation and fog. It must not
        // rebuild poses, replace instances, morph geometry or sample textures.
        foreach (var pose in frame.Nodes)
        {
            if (!animatedMeshes.TryGetValue(pose.Id, out var items)) continue;
            var model = scene.Models[pose.Model];
            bool facade = pose.Texture != null || model.Metadata.Int("model_type") == 1;
            foreach (var item in items)
            {
                if (facade)
                {
                    item.Mesh.Transform = new MatrixTransform3D(ToWpf(HorizonTransform(pose.SourceNode, CameraFacingTransform(pose, model))));
                    if (updateHitTests) item.Mesh.SceneNode.UpdateAllTransformMatrix();
                }
                if (animationEffectLighting && frame.Fog is { Enabled: true })
                {
                    JsonMaterial(scene, item.Part.MaterialIndex, out var color, out _);
                    SetAnimationColor(item, AnimationColor(pose, color, frame, true));
                }
            }
        }
    }

    public void FrameAnimation(AnimationFrame frame)
    {
        TryFrame("asset", manual: false);
    }
    private Dictionary<string, AnimationTextureSlot?>? PrepareAnimationTextureCycle(IReadOnlyList<string> names)
    {
        if (animationTextureCycles.TryGetValue(names, out var prepared)) return prepared;
        // Reserve the entire first traversal before allocating a map or reading
        // members. Rejected cycles do constant work per later frame/mesh.
        if (animationTextureCycles.Count >= 4096 || names.Count > MaximumAnimationTextureCycleMembers - animationTextureCycleMembers)
        { AnimationTextureLimit("Animation texture cycle preparation allowance reached."); return null; }
        animationTextureCycleMembers += names.Count;
        prepared = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < names.Count; i++)
        {
            animationToken.ThrowIfCancellationRequested(); animationTextureCycleMemberVisits++;
            string name = names[i];
            if (!prepared.ContainsKey(name)) prepared.Add(name, QueueAnimationTexture(name));
        }
        animationTextureCycles.Add(names, prepared); return prepared;
    }
    private AnimationTextureSlot? QueueAnimationTexture(string name)
    {
        if (animationContext == null || animationResolver == null || previewTextures == null) return null;
        // Accepted authored references return the live slot directly. Publication
        // and every later frame read that slot without another content hash.
        if (animationTextureReferences.TryGetValue(name, out var slot)) return slot;
        if (animationTextureReferences.Count >= 8192)
        { AnimationTextureLimit("Animation texture reference allowance reached."); return null; }
        // Unfamiliar string instances still need ordinal deduplication. Bound that
        // comparison separately before hashing. An operand exceeding the remaining
        // comparison allowance spends neither that allowance nor retained capacity.
        if (name.Length + 1L > 1_000_000 - animationTextureLookupUnits)
        { AnimationTextureLimit("Animation texture name comparison allowance reached."); return null; }
        animationTextureLookupUnits += name.Length + 1L;
        if (animationTextureSlots.TryGetValue(name, out slot)) { animationTextureReferences.Add(name, slot); return slot; }
        if (animationTextureSlots.Count >= 4096 || name.Length + 1L > 1_000_000 - animationTextureNameUnits)
        { AnimationTextureLimit("Animation texture request limit reached (4096 names / 1,000,000 retained name characters)."); return null; }
        animationTextureNameUnits += name.Length + 1L;
        slot = new(); animationTextureSlots.Add(name, slot); animationTextureReferences.Add(name, slot);
        int current = generation; var cache = previewTextures; var token = animationToken;
        var use = cache.Retain();
        slot.Load = Load(); return slot;
        async Task Load()
        {
            try
            {
                var decoded = await Task.Run(() => cache.GetAsync(name, token), token);
                if (current != generation || token.IsCancellationRequested) return;
                if (decoded != null)
                {
                    var image = decoded.Image;
                    if (!animationTextureModels.TryGetValue(image, out var map)) animationTextureModels[image] = map = new TextureModel(image.Rgba, SharpDX.DXGI.Format.R8G8B8A8_UNorm, image.Width, image.Height);
                    slot.Texture = map; slot.Alpha = decoded.Alpha;
                    if (animationFrame != null) UpdateAnimationFrame(animationFrame, false, animationEffectLighting);
                }
                else TextureNotice("Missing animation texture: ", name);
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) when (current != generation || disposed) { }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidDataException or UnauthorizedAccessException) { if (current == generation) TextureNotice("Animation texture: ", ex.Message); }
            finally { use.Dispose(); }
        }
        void TextureNotice(string prefix, string detail)
        {
            if (animationTextureNotices < 32) { animationTextureNotices++; Information?.Invoke(prefix + PreviewTextureCache.Label(detail)); }
            else if (animationTextureNotices == 32) { animationTextureNotices++; Information?.Invoke("Additional animation texture notices omitted."); }
        }
    }
    private void AnimationTextureLimit(string message)
    {
        if (animationTextureLimitReported) return;
        animationTextureLimitReported = true; Information?.Invoke(message);
    }
    private void IncludeBounds(IEnumerable<Vector3> positions, Matrix4x4 transform)
    {
        Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        foreach (var p in positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
        if (!float.IsFinite(min.X)) return;
        for (int corner = 0; corner < 8; corner++)
        {
            var p = Vector3.Transform(new((corner & 1) == 0 ? min.X : max.X, (corner & 2) == 0 ? min.Y : max.Y, (corner & 4) == 0 ? min.Z : max.Z), transform);
            sceneMin = Vector3.Min(sceneMin, p); sceneMax = Vector3.Max(sceneMax, p);
        }
    }
    private static MeshGeometry3D Mesh(MeshPart p) => new() { Positions = new Vector3Collection(p.Positions), Normals = new Vector3Collection(p.Normals), TextureCoordinates = new Vector2Collection(p.TextureCoordinates), Indices = new IntCollection(p.Indices), Colors = new Color4Collection(p.Colors.Select(v => new Color4(v.X, v.Y, v.Z, v.W))) };
    private static Color4Collection VertexColors(MeshPart part, Color4 tint) => new(part.Colors.Select(v => new Color4(v.X * tint.Red, v.Y * tint.Green, v.Z * tint.Blue, v.W * tint.Alpha)));
    private static Matrix3D ToWpf(Matrix4x4 m) => new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
    private void ClearAnimationResources()
    {
        foreach (var items in animatedMeshes.Values) foreach (var item in items) item.Mesh.Dispose();
        if (animationGroup != null) { animationGroup.Children.Clear(); animationGroup.Dispose(); animationGroup = null; viewport.OITRenderMode = previousTransparency; }
        animatedMeshes.Clear(); animationGeometry.Clear(); animationTextureSlots.Clear(); replacedNodes.Clear(); animationContext = null; animationResolver = null; animationFrame = null;
        animationTextureModels.Clear(); animationTextureReferences.Clear(); animationTextureCycles.Clear(); animationTextureNameUnits = 0; animationTextureLookupUnits = 0;
        animationBaseTextures.Clear();
        animationTextureCycleMembers = animationTextureCycleMemberVisits = 0; animationTextureLimitReported = false; animationTextureNotices = 0;
    }
    private sealed class AnimationTextureSlot
    {
        internal Task Load { get; set; } = Task.CompletedTask;
        internal TextureModel? Texture { get; set; }
        internal bool Alpha { get; set; }
    }
    private sealed class AnimatedMesh(MeshGeometryModel3D mesh, DiffuseMaterial material, MeshPart part)
    {
        public MeshGeometryModel3D Mesh { get; } = mesh;
        public DiffuseMaterial Material { get; } = material;
        public MeshPart Part { get; } = part;
        public float Morph { get; set; }
        public Color4? VertexTint { get; set; }
    }
}
