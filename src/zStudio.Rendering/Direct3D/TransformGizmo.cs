using System.Numerics;
using System.Windows;

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
internal sealed class TransformGizmo : Node, INodeOwner
{
    private readonly Func<MeshNode?, bool> canBegin;
    private GroupNode? target;
    private bool translation = true, rotation = true, rotationX = true, rotationZ = true;
    private double sizeScale = 1;
    private Vector3 translationVector;
    private Matrix4x4 targetScale = Matrix4x4.Identity, targetRotation = Matrix4x4.Identity;
    private int axis = -1;
    private MeshNode? dragged;
    private Color4 draggedColor;
    private Vector3 dragOrigin, dragNormal, dragHit;
    private bool captured;

    /// <param name="canBeginTranslation">Asked when a translation drag would start on a handle; false refuses it.</param>
    public TransformGizmo(Func<MeshNode?, bool> canBeginTranslation)
    {
        canBegin = canBeginTranslation;
        List<MeshNode> handles = [];
        Control = new GroupNode();
        Control.Owner = this;
        Matrix4x4[] turns = [Matrix4x4.Identity, Matrix4x4.CreateRotationZ(MathF.PI / 2), Matrix4x4.CreateRotationY(-MathF.PI / 2)];
        Color4[] colors = [new(1, 0, 0, 1), new(0, 128 / 255f, 0, 1), new(0, 0, 1, 1)];
        foreach (var geometry in new[] { GizmoGeometry.Arrow, GizmoGeometry.Ring, GizmoGeometry.Scale })
            for (int i = 0; i < 3; i++)
            {
                var material = PreviewMaterial.CreatePlain(); material.DiffuseColor = colors[i];
                var handle = new MeshNode { Geometry = geometry, Material = material, CullMode = CullMode.Back, Transform = i == 0 ? null : turns[i] };
                Control.Children.Add(handle); handles.Add(handle);
            }
        Handles = handles;
        ApplyEnabled(); UpdateControl();
    }
    /// <summary>The handles' group, scaled by <see cref="SizeScale"/> and moved to the target.</summary>
    internal GroupNode Control { get; }
    RenderView? INodeOwner.View => View;
    public bool EnableTranslation { get => translation; set { translation = value; ApplyEnabled(); } }
    public bool EnableRotation { get => rotation; set { rotation = value; ApplyEnabled(); } }
    public bool EnableRotationX { get => rotationX; set { rotationX = value; ApplyEnabled(); } }
    public bool EnableRotationZ { get => rotationZ; set { rotationZ = value; ApplyEnabled(); } }
    public double SizeScale { get => sizeScale; set { sizeScale = value; UpdateControl(); } }
    /// <summary>The group the gizmo is placed on and moves; assigning it (again) re-reads its transform.</summary>
    public GroupNode? Target
    {
        get => target;
        set
        {
            target = value;
            if (value == null) { targetScale = targetRotation = Matrix4x4.Identity; translationVector = Vector3.Zero; }
            else
            {
                var m = value.Transform ?? Matrix4x4.Identity;
                if (Matrix4x4.Decompose(m, out var scale, out var turn, out _))
                { targetScale = Matrix4x4.CreateScale(scale); targetRotation = Matrix4x4.CreateFromQuaternion(turn); }
                translationVector = m.Translation;
            }
            UpdateControl();
        }
    }
    /// <summary>
    /// Every handle mesh in drawing order: translation X, Y, Z, rotation X, Y, Z, then the (unused) scale handles. The disabled
    /// ones have <see cref="Node.IsRendering"/> false. Their materials, transforms and cull modes belong to the caller.
    /// </summary>
    public IReadOnlyList<MeshNode> Handles { get; }

    private void ApplyEnabled()
    {
        bool[] enabled = [translation, translation, translation, rotation && rotationX, rotation, rotation && rotationZ, false, false, false];
        for (int i = 0; i < Handles.Count; i++) Handles[i].IsRendering = enabled[i];
    }
    // The handles' frame: SizeScale on the diagonal, the target's translation as offset.
    private void UpdateControl()
    {
        var m = Matrix4x4.CreateTranslation(translationVector);
        m.M11 = m.M22 = m.M33 = (float)sizeScale;
        Control.Transform = m;
    }

    /// <summary>Starts a translation drag on <paramref name="handle"/> hit at <paramref name="hit"/> under <paramref name="pointer"/> (DIPs).</summary>
    public bool BeginTranslation(MeshNode handle, Vector3 hit, Point pointer)
    {
        int index = Handles.Count >= 3 ? IndexOf(handle) : -1;
        if (target == null || index is < 0 or > 2 || !canBegin(handle)) return false;
        axis = index; dragged = handle;
        if (handle.Material is { } material) { draggedColor = material.DiffuseColor; material.DiffuseColor = new(1, 1, 0, 1); }
        captured = false;
        if (View is not { Camera: { } camera } view) return true;
        var direction = Axis(index);
        var look = Vector3.Normalize(RenderCamera.ToFloat(camera.LookDirection));
        dragOrigin = hit; dragNormal = Vector3.Cross(Vector3.Cross(look, direction), direction);
        if (PlanePoint(view, pointer, out var point)) { dragHit = point; captured = true; }
        return true;
    }
    public void MoveTranslation(MeshNode handle, Vector3 hit, Point pointer)
    {
        if (!captured || View is not { } view || !PlanePoint(view, pointer, out var point)) return;
        var move = point - dragHit; dragHit = point;
        translationVector += axis switch { 0 => new(move.X, 0, 0), 1 => new(0, move.Y, 0), _ => new(0, 0, move.Z) };
        UpdateControl();
        if (target != null) target.Transform = targetScale * targetRotation * Matrix4x4.CreateTranslation(translationVector);
    }
    public void EndTranslation(MeshNode handle, Vector3 hit, Point pointer)
    {
        if (captured && dragged?.Material is { } material) material.DiffuseColor = draggedColor;
        captured = false; dragged = null; axis = -1;
    }
    private int IndexOf(MeshNode handle) { for (int i = 0; i < 3; i++) if (ReferenceEquals(Handles[i], handle)) return i; return -1; }
    private static Vector3 Axis(int index) => index == 0 ? Vector3.UnitX : index == 1 ? Vector3.UnitY : Vector3.UnitZ;
    // The pointer's eye ray on the drag plane.
    private bool PlanePoint(RenderView view, Point pointer, out Vector3 point)
    {
        point = default;
        var (origin, direction) = view.UnProject(pointer);
        float denominator = Vector3.Dot(dragNormal, direction);
        if (MathF.Abs(denominator) < 1e-12f || !float.IsFinite(denominator)) return false;
        float t = Vector3.Dot(dragNormal, dragOrigin - origin) / denominator;
        point = origin + direction * t;
        return float.IsFinite(point.X) && float.IsFinite(point.Y) && float.IsFinite(point.Z);
    }

    public override void Dispose()
    {
        foreach (var handle in Handles) handle.Dispose();
        Control.Dispose();
        base.Dispose();
    }
}

/// <summary>The gizmo's handle geometry.</summary>
internal static class GizmoGeometry
{
    /// <summary>Move arrow along +X: shaft radius .02 from 0 to 1.5; cone of base radius .08 from 1.48 to 1.8 (with a radius-.04 sliver from 1.48 to 1.5).</summary>
    public static MeshData Arrow { get; } = Build(b =>
    {
        b.AddArrow(new(1.5f, 0, 0), new(1.8f, 0, 0), .08f, 4, 12);
        b.AddCylinder(Vector3.Zero, new(1.5f, 0, 0), .04f, 12);
    });
    /// <summary>Rotation ring: radius 1 in the YZ plane, tube radius .03, 8 sides, 32 segments.</summary>
    public static MeshData Ring { get; } = Build(b => b.AddRing(1, .03f, 32, 8));
    /// <summary>Scale handle (never shown): a cube of side .15 at x = .8 on a radius-.01 shaft.</summary>
    public static MeshData Scale { get; } = Build(b =>
    {
        b.AddBox(new(.8f, 0, 0), .15f);
        b.AddCylinder(Vector3.Zero, new(.8f, 0, 0), .02f, 4);
    });
    private static MeshData Build(Action<SimpleMeshBuilder> build)
    {
        var builder = new SimpleMeshBuilder();
        build(builder);
        return builder.ToMesh();
    }
}

/// <summary>Triangle meshes for the gizmo and the navigation aids: revolved arrows and cylinders, tubes and cube faces.</summary>
internal sealed class SimpleMeshBuilder
{
    public List<Vector3> Positions { get; } = [];
    public List<Vector3> Normals { get; } = [];
    public List<Vector2> TextureCoordinates { get; } = [];
    public List<int> Indices { get; } = [];
    /// <summary>The mesh, shared by every view that draws it (nodes never release its device copies).</summary>
    public MeshData ToMesh(Color4[]? colors = null) => new([.. Positions], [.. Indices], [.. Normals], [.. TextureCoordinates], colors) { IsShared = true };

    // Unit circle points (−cos, sin).
    private static Vector2[] Circle(int divisions)
    {
        var circle = new Vector2[divisions];
        float angle = MathF.PI * 2f / divisions;
        for (int i = 0; i < divisions; i++) circle[i] = new(-MathF.Cos(i * angle), MathF.Sin(i * angle));
        return circle;
    }
    private static Vector3 Perpendicular(Vector3 value)
    {
        var n = Vector3.Normalize(value);
        var u = Vector3.Cross(Vector3.UnitY, n);
        if (u.LengthSquared() < 1e-3f) u = Vector3.Cross(Vector3.UnitX, n);
        return u;
    }
    /// <summary>A profile (x along <paramref name="direction"/>, y radius) revolved around the axis.</summary>
    public void AddRevolved(IReadOnlyList<Vector2> points, Vector3 origin, Vector3 direction, int divisions)
    {
        direction = Vector3.Normalize(direction);
        var u = Perpendicular(direction);
        var v = Vector3.Normalize(Vector3.Cross(direction, u));
        u = Vector3.Normalize(u);
        var circle = Circle(divisions);
        int index0 = Positions.Count, n = points.Count, totalNodes = (n - 1) * 2 * divisions, rowNodes = (n - 1) * 2;
        for (int i = 0; i < divisions; i++)
        {
            var w = v * circle[i].X + u * circle[i].Y;
            for (int j = 0; j + 1 < n; j++)
            {
                Positions.Add(origin + direction * points[j].X + w * points[j].Y);
                Positions.Add(origin + direction * points[j + 1].X + w * points[j + 1].Y);
                float tx = points[j + 1].X - points[j].X, ty = points[j + 1].Y - points[j].Y;
                var normal = Vector3.Normalize(-direction * ty + w * tx);
                Normals.Add(normal); Normals.Add(normal);
                TextureCoordinates.Add(new((float)i / (divisions - 1), (float)j / (n - 1)));
                TextureCoordinates.Add(new((float)i / (divisions - 1), (float)(j + 1) / (n - 1)));
                int i0 = index0 + i * rowNodes + j * 2, i1 = i0 + 1, i2 = index0 + ((i + 1) * rowNodes + j * 2) % totalNodes, i3 = i2 + 1;
                Indices.AddRange([i1, i0, i2, i1, i2, i3]);
            }
        }
    }
    public void AddArrow(Vector3 point1, Vector3 point2, float diameter, float headLength, int divisions)
    {
        var direction = point2 - point1;
        float length = direction.Length(), r = diameter / 2;
        AddRevolved([new(0, 0), new(0, r), new(length - diameter * headLength, r), new(length - diameter * headLength, r * 2), new(length, 0)], point1, direction, divisions);
    }
    public void AddCylinder(Vector3 point1, Vector3 point2, float diameter, int divisions)
    {
        var direction = point2 - point1;
        AddRevolved([new(0, diameter / 2), new(direction.Length(), diameter / 2)], point1, Vector3.Normalize(direction), divisions);
    }
    /// <summary>
    /// A closed tube of <paramref name="sides"/> sides and radius <paramref name="tube"/> along a <paramref name="segments"/>-gon of
    /// <paramref name="radius"/> in the YZ plane (sections perpendicular to each segment, carried along by the rotation between
    /// segments and projected onto the bisecting planes).
    /// </summary>
    public void AddRing(float radius, float tube, int segments, int sides)
    {
        var circle = Circle(segments);
        var path = new Vector3[segments];
        for (int i = 0; i < segments; i++) path[i] = new Vector3(0, circle[i].X, circle[i].Y) * radius;
        var section = Circle(sides);
        // The default section axis: perpendicular to the first segment.
        var first = path[1] - path[0];
        var left = new Vector3(first.Y, first.Z, first.X);
        if (left == first) left = Vector3.UnitZ;
        var xAxis = Vector3.Normalize(Vector3.Cross(left, first));
        var currentDir = Vector3.Normalize(path[1] - path[0]);
        var preXAxis = Vector3.Normalize(ProjectOnPlane(xAxis, currentDir));
        var preDir = Vector3.Normalize(path[0] - path[^1]);
        preXAxis = Vector3.Normalize(Vector3.Transform(preXAxis, Between(currentDir, preDir)));
        int index0 = Positions.Count;
        for (int i = 0; i < segments; i++)
        {
            var point = path[i];
            currentDir = Vector3.Normalize((i < segments - 1 ? path[i + 1] : path[0]) - point);
            var planeNormal = Vector3.Normalize(currentDir + preDir);
            var rotatedX = Vector3.Normalize(Vector3.Transform(preXAxis, Between(preDir, currentDir)));
            var v = Vector3.Normalize(Vector3.Cross(currentDir, rotatedX));
            var u = Vector3.Normalize(Vector3.Cross(v, currentDir));
            for (int j = 0; j < sides; j++)
            {
                var onSection = point + section[j].X * u * tube + section[j].Y * v * tube;
                // Along the segment direction onto the bisecting plane through the path point.
                float along = Vector3.Dot(planeNormal, onSection - point) / Vector3.Dot(planeNormal, currentDir);
                var position = onSection - currentDir * along;
                Positions.Add(position);
                Normals.Add(Vector3.Normalize(position - point));
                TextureCoordinates.Add(new((float)i / segments, (float)j / sides));
            }
            preDir = currentDir; preXAxis = rotatedX;
        }
        for (int i = 0; i < segments; i++)
            for (int j = 0; j < sides; j++)
            {
                int i00 = index0 + i * sides + j, i01 = index0 + i * sides + (j + 1) % sides;
                int i10 = index0 + (i + 1) % segments * sides + j, i11 = index0 + (i + 1) % segments * sides + (j + 1) % sides;
                Indices.AddRange([i00, i11, i01, i11, i00, i10]);
            }
    }
    private static Vector3 ProjectOnPlane(Vector3 point, Vector3 normal) { normal = Vector3.Normalize(normal); return point - Vector3.Dot(normal, point) * normal; }
    // The shortest rotation taking one direction to another.
    private static Quaternion Between(Vector3 source, Vector3 target)
    {
        float norms = MathF.Sqrt(source.LengthSquared() * target.LengthSquared());
        float real = norms + Vector3.Dot(source, target);
        var result = real < 1e-6f * norms
            ? MathF.Abs(source.X) > MathF.Abs(source.Z) ? new Quaternion(-source.Y, source.X, 0, 0) : new Quaternion(0, -source.Z, source.Y, 0)
            : new Quaternion(Vector3.Cross(source, target), real);
        return Quaternion.Normalize(result);
    }
    public void AddBox(Vector3 center, float size)
    {
        foreach (var (normal, up) in new[] { (Vector3.UnitX, Vector3.UnitY), (-Vector3.UnitX, Vector3.UnitY), (Vector3.UnitY, Vector3.UnitZ), (-Vector3.UnitY, -Vector3.UnitZ), (Vector3.UnitZ, Vector3.UnitY), (-Vector3.UnitZ, Vector3.UnitY) })
            AddCubeFace(center, normal, up, size, size, size);
    }
    /// <summary>One square face of a box (texture (1,1) at its bottom right as seen from outside).</summary>
    public void AddCubeFace(Vector3 center, Vector3 normal, Vector3 up, float distance, float width, float height)
    {
        var right = Vector3.Cross(normal, up);
        var n = normal * distance / 2;
        up *= height / 2; right *= width / 2;
        int i0 = Positions.Count;
        Positions.AddRange([center + n - up - right, center + n - up + right, center + n + up + right, center + n + up - right]);
        Normals.AddRange([normal, normal, normal, normal]);
        TextureCoordinates.AddRange([new(1, 1), new(0, 1), new(0, 0), new(1, 0)]);
        Indices.AddRange([i0 + 2, i0 + 1, i0, i0, i0 + 3, i0 + 2]);
    }
}
