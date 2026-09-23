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
    private readonly Dictionary<string, (TextureModel Texture, bool Alpha)> animationTextures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task> animationTextureLoads = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> replacedNodes = [];
    private Vector3 levelMin, levelMax;
    private AnimationFrame? animationFrame;
    private bool updatingAnimation, animationEffectLighting = true;
    private SortingGroupModel3D? animationGroup;
    private OITRenderType previousTransparency;
    public int AnimationMeshCount => animatedMeshes.Values.Sum(m => m.Length);

    public async Task ShowAnimationAsync(AnimationPreviewContext context, AnimationFrame frame, AssetResolver resolver, bool includeLevel, CancellationToken token, int lod = 0, bool showHorizon = true, CancellationToken? previewLifetime = null)
    {
        if (includeLevel)
            await ShowAsync(context.World, context.World.Assets.First(a => a.Kind == AssetKind.World), resolver, null, lod, token, showHorizon, context.Mission);
        else { Clear(); effects ??= PreviewMaterials.CreateEffects(); viewport.EffectsManager = effects; }
        token.ThrowIfCancellationRequested();
        animationContext = context; animationResolver = resolver; animationToken = token;
        Mission = context.Mission; PreviewScene = context.Scene; horizonEnabled = showHorizon; ConfigureHorizon(context.Scene);
        // Helix 3.1.2's OIT paths drop the unlit DiffuseMaterial effect cards.
        // Standard alpha blending with a sorted group renders their actual alpha.
        previousTransparency = viewport.OITRenderMode; viewport.OITRenderMode = OITRenderType.None;
        animationGroup = sceneAlphaGroup ?? new() { EnableSorting = true, SortTransparentOnly = true, SortingInterval = 0 };
        if (sceneAlphaGroup == null) viewport.Items.Add(animationGroup);
        levelMin = sceneMin; levelMax = sceneMax;
        // Spawned effect cards can change on their first visible tick. Prepare the
        // template maps before enabling playback, as with bound material cycles.
        foreach (string name in context.Effects.Values.SelectMany(e => e.Textures).Distinct(StringComparer.OrdinalIgnoreCase)) QueueAnimationTexture(name);
        UpdateAnimationFrame(frame);
        await Task.WhenAll(animationTextureLoads.Values.ToArray());
        token.ThrowIfCancellationRequested(); UpdateAnimationFrame(frame);
        animationToken = previewLifetime ?? token;
        if (includeLevel) FrameAll(); else FrameAnimation(frame);
    }

    public void UpdateAnimationFrame(AnimationFrame frame, bool followCamera = false, bool effectLighting = true)
    {
        if (animationContext == null || animationToken.IsCancellationRequested || updatingAnimation) return;
        updatingAnimation = true; animationFrame = frame; animationEffectLighting = effectLighting;
        try
        {
        var scene = animationContext.Scene; var ids = frame.Nodes.Select(p => p.Id).ToHashSet();
        foreach (long expired in animatedMeshes.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            foreach (var item in animatedMeshes[expired]) { animationGroup?.Children.Remove(item.Mesh); item.Mesh.Dispose(); }
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
                    var material = PreviewMaterials.Create(horizon); material.EnableUnLit = true;
                    var mesh = new MeshGeometryModel3D { Geometry = Mesh(part), Material = material, CullMode = CullMode.None, IsThrowingShadow = false, RenderOrder = horizon ? 0 : 1, IsDepthClipEnabled = !horizon };
                    int source = pose.SourceNode; mesh.MouseDown3D += (_, _) => NodeSelected?.Invoke(source);
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
                    if (part != null) item.Mesh.Geometry = Mesh(part);
                    item.Morph = pose.Morph;
                }
                JsonMaterial(scene, item.Part.MaterialIndex, out var color, out int textureIndex);
                string? textureName = pose.Texture ?? (textureIndex >= 0 && textureIndex < scene.Textures.Count ? scene.Textures[textureIndex].Text("name") : null);
                if (pose.Texture == null && animationContext.MaterialCycles.TryGetValue(item.Part.MaterialIndex, out var cycle))
                {
                    foreach (string name in cycle.Textures) QueueAnimationTexture(name);
                    textureName = cycle.At(pose.CycleTime, pose.Variant);
                }
                bool alphaTexture = false;
                TextureModel? diffuseMap = null;
                if (textureName != null)
                {
                    QueueAnimationTexture(textureName);
                    if (animationTextures.TryGetValue(textureName, out var texture)) { diffuseMap = texture.Texture; alphaTexture = texture.Alpha; }
                    else item.Mesh.Visibility = Visibility.Collapsed;
                }
                item.Material.DiffuseMap = diffuseMap;
                float alpha = pose.Opacity * color.Alpha;
                item.Material.DiffuseColor = AnimationColor(pose, color, frame, effectLighting);
                item.Mesh.IsTransparent = !horizon && (alphaTexture || alpha < 1);
                if (!horizon && pose.Visible && item.Mesh.Geometry?.Positions is { } positions) IncludeBounds(positions, transform);
            }
        }
        if (followCamera && frame.Camera is { } c && viewport.Camera is HCamera live)
        {
            authoredCameraPose = true;
            live.Position = new(c.Position.X, c.Position.Y, c.Position.Z);
            var look = c.Target - c.Position; if (look.LengthSquared() > 1e-8f) live.LookDirection = new(look.X, look.Y, look.Z);
            live.FieldOfView = Math.Clamp(c.FieldOfView, 1, 170);
        }
        if (!preparingCamera) viewport.InvalidateRender();
        }
        finally { updatingAnimation = false; }
    }
    private Matrix4x4 CameraFacingTransform(AnimationNodePose pose, GameModel model)
    {
        var transform = pose.Transform;
        // The retail facade projection differs; this preview keeps effect cards readable.
        if ((pose.Texture != null || model.Metadata.Int("model_type") == 1) && viewport.Camera is HCamera camera && Matrix4x4.Decompose(transform, out var size, out _, out var position))
        {
            var p = camera.Position; var facing = new Vector3((float)p.X, (float)p.Y, (float)p.Z);
            if ((model.Metadata.UInt("flags") & 0x10) == 0 && pose.Texture == null) facing.Y = position.Y;
            transform = Matrix4x4.CreateScale(size) * Matrix4x4.CreateBillboard(position, facing, Vector3.UnitY, Vector3.UnitZ);
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
        if (effectLighting && frame.Fog is { Enabled: true } fog && viewport.Camera is HCamera camera)
        {
            var p = camera.Position;
            float distance = Vector3.Distance(pose.Transform.Translation, new((float)p.X, (float)p.Y, (float)p.Z));
            rgb = Vector3.Lerp(rgb, fog.Color, Math.Clamp((distance - fog.Start) / Math.Max(.001f, fog.End - fog.Start), 0, 1));
        }
        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        return new(rgb.X, rgb.Y, rgb.Z, pose.Opacity * color.Alpha);
    }
    private void RefreshAnimationCamera()
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
                if (facade) item.Mesh.Transform = new MatrixTransform3D(ToWpf(HorizonTransform(pose.SourceNode, CameraFacingTransform(pose, model))));
                if (animationEffectLighting && frame.Fog is { Enabled: true })
                {
                    JsonMaterial(scene, item.Part.MaterialIndex, out var color, out _);
                    item.Material.DiffuseColor = AnimationColor(pose, color, frame, true);
                }
            }
        }
    }

    public void FrameAnimation(AnimationFrame frame)
    {
        if (animationContext == null) return;
        var min = sceneMin; var max = sceneMax; sceneMin = new(float.PositiveInfinity); sceneMax = new(float.NegativeInfinity);
        foreach (var pose in frame.Nodes.Where(p => p.Visible && !IsHorizon(p.SourceNode))) if (animatedMeshes.TryGetValue(pose.Id, out var items)) foreach (var item in items) IncludeBounds(item.Part.Positions, pose.Transform);
        FrameAll(); sceneMin = min; sceneMax = max; UpdateClipPlanes();
    }
    private void QueueAnimationTexture(string name)
    {
        if (animationTextureLoads.ContainsKey(name) || animationContext == null || animationResolver == null) return;
        int current = generation; var context = animationContext; var resolver = animationResolver; var token = animationToken;
        animationTextureLoads[name] = Load();
        async Task Load()
        {
            try
            {
                var decoded = await Task.Run(async () =>
                {
                    var match = await resolver.ResolveTextureAsync(context.World.Path, name, null, token).ConfigureAwait(false);
                    return match == null ? null : TextureDecoder.Decode(match.Document, match.Asset, token);
                }, token);
                if (current != generation || token.IsCancellationRequested) return;
                if (decoded != null) { animationTextures[name] = (new TextureModel(decoded.Rgba, SharpDX.DXGI.Format.R8G8B8A8_UNorm, decoded.Width, decoded.Height), HasAlpha(decoded)); if (animationFrame != null) UpdateAnimationFrame(animationFrame, false, animationEffectLighting); }
                else Information?.Invoke("Missing animation texture: " + name);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is System.IO.IOException or InvalidDataException or UnauthorizedAccessException) { if (current == generation) Information?.Invoke("Animation texture: " + ex.Message); }
        }
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
    private static MeshGeometry3D Mesh(MeshPart p) => new() { Positions = new Vector3Collection(p.Positions), Normals = new Vector3Collection(p.Normals), TextureCoordinates = new Vector2Collection(p.TextureCoordinates), Indices = new IntCollection(p.Indices) };
    private static Matrix3D ToWpf(Matrix4x4 m) => new(m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44);
    private void ClearAnimationResources()
    {
        foreach (var items in animatedMeshes.Values) foreach (var item in items) item.Mesh.Dispose();
        if (animationGroup != null) { animationGroup.Children.Clear(); animationGroup.Dispose(); animationGroup = null; viewport.OITRenderMode = previousTransparency; }
        animatedMeshes.Clear(); animationGeometry.Clear(); animationTextures.Clear(); animationTextureLoads.Clear(); replacedNodes.Clear(); animationContext = null; animationResolver = null; animationFrame = null;
    }
    private sealed class AnimatedMesh(MeshGeometryModel3D mesh, DiffuseMaterial material, MeshPart part)
    {
        public MeshGeometryModel3D Mesh { get; } = mesh;
        public DiffuseMaterial Material { get; } = material;
        public MeshPart Part { get; } = part;
        public float Morph { get; set; }
    }
}
