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
    private GeometryReservation? animationGeometryReservation, animationMeshReservation;
    private long animationGeometryBytes, animationMeshBytes;
    private readonly Dictionary<int, long> animationGeometryScratch = [];
    private readonly Dictionary<int, long> animationGeometryCopies = [];
    private readonly Dictionary<int, int> animationPossibleParts = [];
    private readonly Dictionary<long, (int Node, int Model, float Morph)> animationPoses = [];
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
    // One unit per pose/polygon visit or material-part/light visit. This is
    // recomputed per submission, not consumed across playback or camera motion.
    internal long MaximumAnimationLightingWork { get; set; } = 1_000_000;
    internal long AnimationLightingWork { get; private set; }
    internal long AnimationLightVisits { get; private set; }
    private SortingGroupModel3D? animationGroup;
    private OITRenderType previousTransparency;
    public int AnimationMeshCount => animatedMeshes.Values.Sum(m => m.Length);

    public async Task ShowAnimationAsync(AnimationPreviewContext context, AnimationFrame frame, AssetResolver resolver, bool includeLevel, CancellationToken token, int lod = 0, bool showHorizon = true, CancellationToken? previewLifetime = null)
    {
        int expectedGeneration = generation + 1;
        try
        {
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
        catch
        {
            if (generation == expectedGeneration) Clear();
            throw;
        }
    }

    public void UpdateAnimationFrame(AnimationFrame frame, bool followCamera = false, bool effectLighting = true)
    {
        if (animationContext == null || animationToken.IsCancellationRequested || updatingAnimation) return;
        // Preflight the complete next frame before removing the previous one or
        // allocating per-pose material/geometry/native objects. Repeated frames
        // recompute simultaneous retention, rather than spending a lifetime quota.
        long renderUnits = staticRenderUnits;
        ReserveRenderUnits(ref renderUnits, frame.Nodes.Count, MaximumRenderUnits);
        long lightingWork = AdmitAnimationLighting(frame, effectLighting);
        long nextGeometry = 0, scratch = 0;
        animationGeometryReservation ??= new(this);
        animationMeshReservation ??= new(this);
        foreach (var pose in frame.Nodes)
        {
            animationToken.ThrowIfCancellationRequested();
            if (pose.Model < 0 || pose.Model >= animationContext.Scene.Models.Count) continue;
            if (!animationGeometry.TryGetValue(pose.Model, out var parts))
            {
                var source = animationContext.Scene.Models[pose.Model];
                long buildBytes = GeometryBuildBytes(source, animationToken, out long copyUpperBound);
                animationGeometryReservation.Resize(checked(animationGeometryBytes + buildBytes));
                try
                {
                    parts = GeometryBuilder.Build(source, token: animationToken);
                    int possibleParts = source.Polygons.Where(p => p.Vertices.Length >= 3).Select(p => p.MaterialIndex).Distinct().Count();
                    animationGeometry.Add(pose.Model, parts);
                    animationGeometryBytes = checked(animationGeometryBytes + parts.Sum(GeometryPartBytes));
                    animationGeometryScratch.Add(pose.Model, checked(buildBytes + 12L * source.Vertices.LongLength));
                    // A degenerate base polygon can become nondegenerate after a
                    // morph. Admit the source's maximum topology, not only base parts.
                    animationGeometryCopies.Add(pose.Model, source.Morphs.Length == source.Vertices.Length
                        ? copyUpperBound : parts.Sum(GeometryCopyBytes));
                    animationPossibleParts.Add(pose.Model, source.Morphs.Length == source.Vertices.Length ? possibleParts : parts.Count);
                }
                finally { animationGeometryReservation.Resize(animationGeometryBytes); }
            }
            ReserveRenderUnits(ref renderUnits, 2L * animationPossibleParts[pose.Model], MaximumRenderUnits);
            // Helix constructors copy every collection. Every pose keeps private
            // geometry: morphs and per-pose fog/alpha tint must not modify siblings.
            // Each retained mesh also owns its unclamped pre-fog RGBA. Camera
            // refresh must not repeat the frame's material-part/light product.
            long copies = checked(animationGeometryCopies[pose.Model] + 16L * animationPossibleParts[pose.Model]);
            nextGeometry = checked(nextGeometry + copies);
            long colors = parts.Select(p => 32L * p.Colors.LongLength).DefaultIfEmpty().Max();
            var model = animationContext.Scene.Models[pose.Model];
            long replacement = model.Morphs.Length == model.Vertices.Length
                ? checked(animationGeometryScratch[pose.Model] + 2 * copies) : colors;
            scratch = Math.Max(scratch, replacement);
        }
        // Expired identities leave before new ones are attached. Existing models
        // retain the same bounded topology across morph changes. A single reusable
        // scratch allowance covers old+new geometry during each sequential swap.
        animationMeshReservation.Resize(checked(Math.Max(animationMeshBytes, nextGeometry) + scratch));
        updatingAnimation = true; animationFrame = frame; animationEffectLighting = effectLighting; ++inspectionSerial;
        AnimationLightingWork = lightingWork; AnimationLightVisits = 0;
        try
        {
        var scene = animationContext.Scene; var poses = frame.Nodes.ToDictionary(p => p.Id);
        foreach (long expired in animatedMeshes.Where(pair => !poses.TryGetValue(pair.Key, out var pose) ||
            !animationPoses.TryGetValue(pair.Key, out var prior) || prior.Node != pose.SourceNode || prior.Model != pose.Model).Select(pair => pair.Key).ToArray())
        {
            foreach (var item in animatedMeshes[expired]) { inspectionMeshes.Remove(item.Mesh); inspectionPolygons.Remove(item.Mesh); animationGroup?.Children.Remove(item.Mesh); item.Mesh.Dispose(); }
            animatedMeshes.Remove(expired);
            animationPoses.Remove(expired);
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
            animationToken.ThrowIfCancellationRequested();
            if (pose.Model < 0 || pose.Model >= scene.Models.Count) continue;
            var model = scene.Models[pose.Model];
            animatedMeshes.TryGetValue(pose.Id, out var items);
            bool morphChanged = model.Morphs.Length == model.Vertices.Length &&
                (!animationPoses.TryGetValue(pose.Id, out var previousPose) || previousPose.Morph != pose.Morph);
            if (items == null || morphChanged)
            {
                // Morphing can create/remove a material part, including a pose with
                // no currently nondegenerate polygons. Reconcile the complete current
                // parts, preserving identities of surviving material meshes.
                var parts = model.Morphs.Length == model.Vertices.Length && pose.Morph != 0
                    ? GeometryBuilder.Build(model with { Vertices = model.Vertices.Select((v, i) => v + model.Morphs[i] * pose.Morph).ToArray() }, token: animationToken)
                    : animationGeometry[pose.Model];
                var remaining = (items ?? []).ToDictionary(item => item.Part.MaterialIndex);
                List<AnimatedMesh> next = new(parts.Count), created = [];
                try
                {
                    foreach (var part in parts)
                    {
                        if (remaining.Remove(part.MaterialIndex, out var item))
                        {
                            item.Mesh.Geometry = Mesh(part); item.Part = part; item.VertexTint = null;
                            inspectionPolygons[item.Mesh] = part.VertexPolygons;
                        }
                        else { item = CreateAnimationMesh(pose, part); created.Add(item); }
                        next.Add(item);
                    }
                    items = next.ToArray();
                }
                catch
                {
                    foreach (var item in created) RemoveAnimationMesh(item);
                    throw;
                }
                foreach (var item in remaining.Values) RemoveAnimationMesh(item);
                animatedMeshes[pose.Id] = items;
                animationPoses[pose.Id] = (pose.SourceNode, pose.Model, pose.Morph);
            }
            foreach (var item in items)
            {
                animationToken.ThrowIfCancellationRequested();
                bool horizon = IsHorizon(pose.SourceNode);
                item.Mesh.Visibility = pose.Visible && pose.Opacity > 0 && (!horizon || horizonEnabled) ? Visibility.Visible : Visibility.Collapsed;
                var transform = CameraFacingTransform(pose, model);
                item.Mesh.Transform = new MatrixTransform3D(ToWpf(HorizonTransform(pose.SourceNode, transform)));
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
                item.LitColor = AnimationLitColor(pose, color, frame, effectLighting);
                SetAnimationColor(item, AnimationFogColor(pose, item.LitColor, frame, effectLighting));
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
        animationMeshBytes = nextGeometry;
        animationMeshReservation.Resize(checked(nextGeometry + scratch));
        }
        catch
        {
            // An unexpected upload failure may leave some new objects attached.
            // Keep their allowance until Clear or a successful replacement removes them.
            animationMeshBytes = Math.Max(animationMeshBytes, nextGeometry);
            throw;
        }
        finally { updatingAnimation = false; }
    }
    private AnimatedMesh CreateAnimationMesh(AnimationNodePose pose, MeshPart part)
    {
        bool horizon = IsHorizon(pose.SourceNode);
        var material = PreviewMaterials.Create(horizon, part.Colors.Length != 0); material.EnableUnLit = true;
        material.VertexColorBlendingFactor = part.Colors.Length == 0 ? 0 : 1;
        var mesh = new MeshGeometryModel3D { Geometry = Mesh(part), Material = material, CullMode = CullMode.None, IsThrowingShadow = false, RenderOrder = horizon ? 0 : 1, IsDepthClipEnabled = !horizon };
        try
        {
            RegisterInspectionMesh(mesh, part.MaterialIndex, pose.Id, pose.SourceNode, pose.Model);
            inspectionPolygons[mesh] = part.VertexPolygons;
            int source = pose.SourceNode; mesh.MouseDown3D += (_, e) =>
            {
                if (!IsPickupDragging && e is MouseDown3DEventArgs { OriginalInputEventArgs: System.Windows.Input.MouseButtonEventArgs { ChangedButton: System.Windows.Input.MouseButton.Left } })
                { SelectFramingNode(source); NodeSelected?.Invoke(source); }
            };
            animationGroup!.Children.Add(mesh);
            return new(mesh, material, part);
        }
        catch
        {
            inspectionMeshes.Remove(mesh); inspectionPolygons.Remove(mesh); animationGroup?.Children.Remove(mesh); mesh.Dispose();
            throw;
        }
    }
    private void RemoveAnimationMesh(AnimatedMesh item)
    {
        inspectionMeshes.Remove(item.Mesh); inspectionPolygons.Remove(item.Mesh);
        animationGroup?.Children.Remove(item.Mesh); item.Mesh.Dispose();
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
    private long AdmitAnimationLighting(AnimationFrame frame, bool effectLighting)
    {
        long work = 0;
        Take(frame.Nodes.Count);
        // Count each source model once, including material parts that a morph
        // can open. This small scratch is admitted before collection growth and
        // released before geometry construction; nothing escapes the preflight.
        using GeometryReservation storage = new(this);
        Dictionary<int, int> parts = [];
        long polygons = 0;
        foreach (var pose in frame.Nodes)
        {
            animationToken.ThrowIfCancellationRequested();
            if (pose.Model < 0 || pose.Model >= animationContext!.Scene.Models.Count) continue;
            if (!parts.TryGetValue(pose.Model, out int count))
            {
                var model = animationContext.Scene.Models[pose.Model];
                Take(model.Polygons.LongLength);
                polygons += model.Polygons.LongLength;
                storage.Resize(checked(128L * (parts.Count + 1L + polygons)));
                // A fresh set keeps one large model followed by many small ones
                // linear; clearing a retained large hash table per model would
                // multiply work by its former capacity. Reserve all sets' storage.
                HashSet<int> materials = [];
                foreach (var polygon in model.Polygons)
                {
                    animationToken.ThrowIfCancellationRequested();
                    if (polygon.Vertices.Length >= 3) materials.Add(polygon.MaterialIndex);
                }
                parts.Add(pose.Model, count = materials.Count);
            }
            // Inactive lights still cost eligibility checks. Preserve authored
            // order and exact arithmetic for every admitted contribution.
            Take(count * (1L + (effectLighting ? frame.Lights.Count : 0)));
        }
        return work;

        void Take(long units)
        {
            animationToken.ThrowIfCancellationRequested();
            if (units < 0 || units > MaximumAnimationLightingWork - work)
                throw new RenderLimitException($"The animation preview exceeds its {MaximumAnimationLightingWork:N0}-unit lighting work allowance per frame; reduce simultaneous material parts or lights.");
            work += units;
        }
    }
    private Color4 AnimationLitColor(AnimationNodePose pose, Color4 color, AnimationFrame frame, bool effectLighting)
    {
        Vector3 rgb = new(color.Red, color.Green, color.Blue);
        if (effectLighting)
            foreach (var light in frame.Lights)
            {
                animationToken.ThrowIfCancellationRequested(); AnimationLightVisits++;
                if (!light.Active || !(light.Range > 0)) continue;
                float falloff = Math.Clamp(1 - Vector3.Distance(pose.Transform.Translation, light.Position) / light.Range, 0, 1);
                rgb = Vector3.Lerp(rgb, Vector3.Max(rgb * .65f, light.Color), Math.Clamp(light.Intensity * falloff, 0, 1));
            }
        // Do not clamp before fog: out-of-range authored colors previously
        // blended with fog first, then clamped at the material boundary.
        return new(rgb.X, rgb.Y, rgb.Z, pose.Opacity * color.Alpha);
    }
    private Color4 AnimationFogColor(AnimationNodePose pose, Color4 lit, AnimationFrame frame, bool effectLighting)
    {
        Vector3 rgb = new(lit.Red, lit.Green, lit.Blue);
        if (effectLighting && frame.Fog is { Enabled: true } fog && viewport.Camera is ProjectionCamera camera)
        {
            var p = camera.Position;
            float distance = Vector3.Distance(pose.Transform.Translation, new((float)p.X, (float)p.Y, (float)p.Z));
            rgb = Vector3.Lerp(rgb, fog.Color, Math.Clamp((distance - fog.Start) / Math.Max(.001f, fog.End - fog.Start), 0, 1));
        }
        rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
        return new(rgb.X, rgb.Y, rgb.Z, lit.Alpha);
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
        if (animationFrame is not { } frame || animationContext == null || updatingAnimation || animationToken.IsCancellationRequested) return;
        var scene = animationContext.Scene;
        // Camera motion changes only facade orientation and fog. It must not
        // rebuild poses, replace instances, morph geometry or sample textures.
        foreach (var pose in frame.Nodes)
        {
            // Camera and hit-test callbacks must quietly stop when their
            // preview lifetime ends, rather than throw into WPF rendering.
            if (animationToken.IsCancellationRequested) return;
            if (!animatedMeshes.TryGetValue(pose.Id, out var items)) continue;
            var model = scene.Models[pose.Model];
            bool facade = pose.Texture != null || model.Metadata.Int("model_type") == 1;
            foreach (var item in items)
            {
                if (animationToken.IsCancellationRequested) return;
                if (facade)
                {
                    item.Mesh.Transform = new MatrixTransform3D(ToWpf(HorizonTransform(pose.SourceNode, CameraFacingTransform(pose, model))));
                    if (updateHitTests) item.Mesh.SceneNode.UpdateAllTransformMatrix();
                }
                if (animationEffectLighting && frame.Fog is { Enabled: true })
                {
                    SetAnimationColor(item, AnimationFogColor(pose, item.LitColor, frame, true));
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
        animationGeometryScratch.Clear(); animationGeometryCopies.Clear(); animationPossibleParts.Clear(); animationPoses.Clear(); animationGeometryBytes = animationMeshBytes = 0;
        animationGeometryReservation?.Dispose(); animationGeometryReservation = null;
        animationMeshReservation?.Dispose(); animationMeshReservation = null;
        animationTextureModels.Clear(); animationTextureReferences.Clear(); animationTextureCycles.Clear(); animationTextureNameUnits = 0; animationTextureLookupUnits = 0;
        animationBaseTextures.Clear();
        animationTextureCycleMembers = animationTextureCycleMemberVisits = 0; animationTextureLimitReported = false; animationTextureNotices = 0;
        AnimationLightingWork = AnimationLightVisits = 0;
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
        public MeshPart Part { get; set; } = part;
        public Color4? VertexTint { get; set; }
        public Color4 LitColor { get; set; }
    }
}
