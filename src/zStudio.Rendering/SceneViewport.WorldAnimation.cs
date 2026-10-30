using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Rendering.Direct3D;

namespace zStudio.Rendering;

public sealed partial class SceneViewport
{
    private int worldSceneGeneration;
    private WorldAnimationAttachment? worldAnimation;
    /// <summary>Identity of the published world, unchanged by animation frames or presentation options.</summary>
    public int SceneGeneration => worldSceneGeneration;
    public bool WorldAnimationCameraFollowing => worldAnimation?.FreeCamera != null;

    /// <summary>Attaches a prepared animation context to the existing world without rebuilding or reframing it.</summary>
    public Guid AttachWorldAnimation(AnimationPreviewContext context, AssetResolver resolver, int expectedSceneGeneration, CancellationToken lifetime)
    {
        viewport.Dispatcher.VerifyAccess();
        lifetime.ThrowIfCancellationRequested();
        if (disposed || expectedSceneGeneration != worldSceneGeneration || PreviewScene == null)
            throw new InvalidOperationException("The world changed while its animation was being prepared.");
        if (worldAnimation is { } previous) DetachWorldAnimation(previous.Id);
        if (animationContext != null)
            throw new InvalidOperationException("A standalone animation already occupies this viewport.");
        if (context.Scene.Nodes.Count < PreviewScene.Nodes.Count || context.Scene.Models.Count < PreviewScene.Models.Count ||
            !StringComparer.OrdinalIgnoreCase.Equals(context.World.Path, InspectionSourcePath))
            throw new InvalidOperationException("The animation context does not belong to this world.");
        var state = new WorldAnimationAttachment(Guid.NewGuid(), worldSceneGeneration, Mission, PreviewScene, InspectionNodes, InspectionSourcePath, CancellationTokenSource.CreateLinkedTokenSource(lifetime));
        worldAnimation = state;
        animationContext = context; animationResolver = resolver; animationToken = state.Lifetime.Token;
        // Contexts retain the initialized world's index prefix, appending only effect templates.
        // The baseline's inspection scope and mission ownership remain authoritative.
        PreviewScene = context.Scene;
        animationGroup = sceneAlphaGroup ?? new SortingGroupNode { SortingEnabled = true };
        if (sceneAlphaGroup == null) view.Items.Add(animationGroup);
        levelMin = sceneMin; levelMax = sceneMax;
        return state.Id;
    }

    /// <summary>Publishes one complete evaluated frame. Obsolete sessions cannot touch a newer attachment.</summary>
    public bool UpdateWorldAnimation(Guid attachment, AnimationFrame frame, bool followCamera = false, bool effectLighting = true)
    {
        viewport.Dispatcher.VerifyAccess();
        if (worldAnimation is not { } state || state.Id != attachment || state.Generation != worldSceneGeneration || state.Lifetime.IsCancellationRequested) return false;
        try
        {
            if (!followCamera)
            {
                RestoreWorldAnimationCamera(state);
                state.FollowRequested = false; state.FollowSuppressed = false;
            }
            else if (!state.FollowRequested) { state.FollowRequested = true; state.FollowSuppressed = false; }
            bool follow = followCamera && !state.FollowSuppressed && frame.Camera != null;
            if (follow && state.FreeCamera == null)
            {
                state.FreeCamera = CaptureView();
                StopNavigationMotion();
            }
            UpdateAnimationFrame(frame, follow, effectLighting);
            RefreshWorldAnimationInspection();
            return true;
        }
        catch
        {
            DetachWorldAnimation(attachment);
            throw;
        }
    }

    /// <summary>Removes only this attachment's resources, retaining the world and current user presentation choices.</summary>
    public bool DetachWorldAnimation(Guid attachment)
    {
        viewport.Dispatcher.VerifyAccess();
        if (worldAnimation is not { } state || state.Id != attachment) return false;
        worldAnimation = null;
        state.Lifetime.Cancel();
        ClearAnimationResources();
        Mission = state.Mission; PreviewScene = state.Scene; InspectionNodes = state.InspectionNodes; InspectionSourcePath = state.Path;
        ApplyNodeVisibility(); RefreshHorizon();
        RestoreWorldAnimationCamera(state);
        RefreshWorldAnimationInspection();
        state.Lifetime.Dispose();
        return true;
    }

    private void RestoreWorldAnimationCamera(WorldAnimationAttachment state)
    {
        if (state.FreeCamera is not { } pose) return;
        state.FreeCamera = null;
        authoredCameraPose = false;
        RestoreView(pose);
    }

    private void DisengageWorldAnimationCamera()
    {
        if (worldAnimation is not { } state || !state.FollowRequested) return;
        // Manual navigation starts at the camera the user sees; it must not jump back.
        state.FreeCamera = null; state.FollowSuppressed = true; authoredCameraPose = false;
    }

    private void DiscardWorldAnimation()
    {
        if (worldAnimation is not { } state) return;
        worldAnimation = null; state.Lifetime.Cancel(); state.Lifetime.Dispose();
    }

    private void RefreshWorldAnimationInspection()
    {
        if (SelectedInspection is { Node: >= 0 } selected)
        {
            var current = InspectTarget(selected.Target);
            if (current?.Active != true && (selected.RuntimeInstance == null || selected.RuntimeInstance.Value >> 32 == 0))
            {
                foreach (var (mesh, meta) in inspectionMeshes)
                {
                    if (meta.Runtime is { } runtime)
                    {
                        if (runtime >> 32 == 0 && meta.Node == selected.Node && InspectMesh(mesh, meta, 0) is { Active: true } animated) { current = animated; break; }
                    }
                    else if (placements.TryGetValue(mesh, out var rows))
                        for (int i = 0; i < rows.Length; i++)
                            if (rows[i].NodeIndex == selected.Node && InspectMesh(mesh, meta, i) is { Active: true } baseline) { current = baseline; break; }
                    if (current?.Active == true) break;
                }
            }
            SelectedInspection = current != null ? current with { OwnerNode = selected.ExactSelection ? selected.OwnerNode : current.OwnerNode, ExactSelection = selected.ExactSelection }
                : selected with { Active = false, Origin = null, Surface = null, Normal = null };
        }
        HoverInspection = null; ++inspectionSerial;
        RefreshPickupSelection(); InspectionChanged?.Invoke();
    }

    private sealed class WorldAnimationAttachment(Guid id, int generation, MissionSceneContext? mission, GameScene scene,
        IReadOnlySet<int>? inspectionNodes, string? path, CancellationTokenSource lifetime)
    {
        public Guid Id { get; } = id;
        public int Generation { get; } = generation;
        public MissionSceneContext? Mission { get; } = mission;
        public GameScene Scene { get; } = scene;
        public IReadOnlySet<int>? InspectionNodes { get; } = inspectionNodes;
        public string? Path { get; } = path;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public ViewPose? FreeCamera { get; set; }
        public bool FollowRequested { get; set; }
        public bool FollowSuppressed { get; set; }
    }
}
