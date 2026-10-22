using System.Numerics;
using System.Windows;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;

namespace zStudio.Rendering.Direct3D;

/// <summary>
/// Move arrows and rotation rings around a target group, drawn at <see cref="SizeScale"/> world units per handle unit and centred
/// on the target's translation. Handle geometry (handle-local, along +X): arrow shaft radius .02 from 0 to 1.5, cone of base
/// radius .08 from 1.48 to the tip at 1.8; ring radius 1 in the YZ plane, tube radius .03, 32 segments. The Y and Z handles are
/// the X handle turned onto their axes. Colours red, green (0,128,0) and blue; a translation drag turns its arrow yellow until it
/// ends. The gizmo itself is never hit by ray picking: callers pick handles in screen space.
/// <para>
/// A translation drag (<see cref="BeginTranslation"/>) moves the target along the handle's world axis: the pointer ray meets the
/// plane through the hit point whose normal is cross(cross(look, axis), axis), and the change of that point projected onto the
/// axis moves <see cref="Target"/>'s transform (raising <see cref="GroupNode.TransformChanged"/>).
/// </para>
/// </summary>
internal sealed class TransformGizmo : Node
{
    private readonly Func<MeshNode?, bool> canBegin;
    private bool beginning, began;
    private GroupNode? target;

    /// <param name="canBeginTranslation">Asked when a translation drag would start on a handle; false refuses it.</param>
    public TransformGizmo(Func<MeshNode?, bool> canBeginTranslation) : this(new Manipulator(), canBeginTranslation) { }
    private TransformGizmo(Manipulator manipulator, Func<MeshNode?, bool> canBeginTranslation) : base(manipulator)
    {
        canBegin = canBeginTranslation;
        manipulator.Owner = this;
        manipulator.EnableScaling = false; manipulator.EnableXRayGrid = false;
        List<MeshNode> handles = [];
        void Collect(Element3D element)
        {
            if (element is MeshGeometryModel3D mesh) handles.Add(new MeshNode(mesh));
            else if (element is GroupElement3D group) foreach (var child in group.Children) Collect(child);
        }
        Collect(manipulator);
        Handles = handles;
    }
    private Manipulator Backend => (Manipulator)HelixElement;
    public bool EnableTranslation { get => Backend.EnableTranslation; set => Backend.EnableTranslation = value; }
    public bool EnableRotation { get => Backend.EnableRotation; set => Backend.EnableRotation = value; }
    public bool EnableRotationX { get => Backend.EnableRotationX; set => Backend.EnableRotationX = value; }
    public bool EnableRotationZ { get => Backend.EnableRotationZ; set => Backend.EnableRotationZ = value; }
    public double SizeScale { get => Backend.SizeScale; set => Backend.SizeScale = value; }
    /// <summary>The group the gizmo is placed on and moves; assigning it (again) re-reads its transform.</summary>
    public GroupNode? Target { get => target; set { target = value; Backend.Target = value?.HelixElement; } }
    /// <summary>
    /// Every handle mesh in drawing order: translation X, Y, Z, rotation X, Y, Z, then the (unused) scale handles. The disabled
    /// ones have <see cref="Node.IsRendering"/> false. Their materials, transforms and cull modes belong to the caller.
    /// </summary>
    public IReadOnlyList<MeshNode> Handles { get; }

    /// <summary>Starts a translation drag on <paramref name="handle"/> hit at <paramref name="hit"/> under <paramref name="pointer"/> (DIPs).</summary>
    public bool BeginTranslation(MeshNode handle, Vector3 hit, Point pointer)
    {
        if (View?.HelixViewport is not { } viewport) return false;
        beginning = true; began = false;
        try { handle.HelixElement.RaiseEvent(new MouseDown3DEventArgs(handle.HelixElement, Hit(handle, hit), pointer, viewport)); }
        finally { beginning = false; }
        return began;
    }
    public void MoveTranslation(MeshNode handle, Vector3 hit, Point pointer)
    {
        if (View?.HelixViewport is { } viewport) handle.HelixElement.RaiseEvent(new MouseMove3DEventArgs(handle.HelixElement, Hit(handle, hit), pointer, viewport));
    }
    public void EndTranslation(MeshNode handle, Vector3 hit, Point pointer)
    {
        if (View?.HelixViewport is { } viewport) handle.HelixElement.RaiseEvent(new MouseUp3DEventArgs(handle.HelixElement, Hit(handle, hit), pointer, viewport));
    }
    private static HitTestResult Hit(MeshNode handle, Vector3 hit) => new() { ModelHit = handle.HelixElement, PointHit = hit, IsValid = true, Distance = 0 };

    private sealed class Manipulator : TransformManipulator3D
    {
        internal TransformGizmo? Owner { get; set; }
        // Only drags this facade starts; the backend's own pointer routing never reaches the (unpickable) gizmo.
        protected override bool CanBeginTransform(MouseDown3DEventArgs? e)
        {
            if (Owner is not { beginning: true } owner) return false;
            owner.began = owner.canBegin(Node.FromHelix(e?.HitTestResult?.ModelHit) as MeshNode);
            return owner.began;
        }
    }
}
