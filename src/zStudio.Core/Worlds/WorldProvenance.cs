namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// One executed script instruction: the script (project path), its 1-based physical line, the command, the tokens as
/// written (command first) and the arguments after macro expansion.
/// </summary>
public sealed record SourceInstruction(string Script, int Line, string Command, IReadOnlyList<string> Tokens, IReadOnlyList<string> Args);

/// <summary>
/// A place in a node a glTF file places under several parents (an instance): the instance itself (its number in the
/// file), or a child at <see cref="Child"/> (its position among its parent's children) of a place in it.
/// </summary>
public sealed class InstancePlace
{
    internal InstancePlace(int number, InstancePlace? parent = null, int child = 0, string name = "", int occurrence = 0) { Number = number; Parent = parent; Child = child; Name = name; Occurrence = occurrence; }
    /// <summary>The instance's number in its file (the <c>instance</c> mark its copies share).</summary>
    public int Number { get; }
    /// <summary>The place of the node's parent in the instance, or null for the instance itself.</summary>
    public InstancePlace? Parent { get; }
    /// <summary>The node's position among its parent's children in the file.</summary>
    public int Child { get; }
    /// <summary>
    /// The child's name as the build names it. With <see cref="Occurrence"/> it tells the child apart from its siblings
    /// whatever their positions: a copy inserted beside one of them (named as no node is) leaves the others' places as
    /// they were, while their positions move.
    /// </summary>
    public string Name { get; }
    /// <summary>How many earlier children of the parent in the file have <see cref="Name"/>.</summary>
    public int Occurrence { get; }
    /// <summary>The instance's number and the child positions from it, such as 3/0/1.</summary>
    public override string ToString()
    {
        List<int> path = [];
        for (var at = this; at.Parent is { } up && path.Count < 256; at = up) path.Add(at.Child);
        path.Reverse();
        return string.Join("/", path.Prepend(Number));
    }
}

/// <summary>
/// Where a world node came from and which instructions last changed it: the glTF node it was imported from (with the load
/// that read the file), or the instruction that created it, and per command the last instruction that changed it. Kept
/// beside the world while it is built; nothing of it is written to the world or the project.
/// </summary>
public sealed class WorldNodeProvenance
{
    /// <summary>The project path of the glTF file the node was imported from, or null for a node a script created.</summary>
    public string? ModelFile { get; internal set; }
    /// <summary>The node's index in <see cref="ModelFile"/>.</summary>
    public int ModelNode { get; internal set; } = -1;
    /// <summary>The glTF node's name in <see cref="ModelFile"/>, which an edit checks before changing node <see cref="ModelNode"/>.</summary>
    public string? ModelNodeName { get; internal set; }
    /// <summary>
    /// Whether the glTF node had a transform of its own when built. Its matrix is then authored: script Object3DRotate and
    /// Object3DScale are ignored, while Object3DTranslate still sets its translation (the matrix's last row).
    /// </summary>
    public bool ModelTransformAuthored { get; internal set; }
    /// <summary>
    /// For a node of a file another node references (a model file, or a part of the mission database), the referencing
    /// node's provenance: copies of one file are told apart by it.
    /// </summary>
    public WorldNodeProvenance? ReferencedBy { get; internal set; }
    /// <summary>
    /// For a node its file places under several parents (an instance) or a node inside one, where it is in the instance.
    /// The copies are one node, read from the file's first copy (<see cref="ModelNode"/> is that copy's), so this names the
    /// node whichever copy comes first.
    /// </summary>
    public InstancePlace? Instance { get; internal set; }
    /// <summary>The LoadGameGen that read the node's file.</summary>
    public SourceInstruction? Load { get; internal set; }
    /// <summary>
    /// A node of the mission database (the load after GameGenSetWorld, whose objects join the world): of its file or of one
    /// of its parts, not of a model file it references.
    /// </summary>
    public bool Database { get; internal set; }
    /// <summary>
    /// A node of a part of the mission database: a file of its own that the database (or another part) references with a
    /// group, holding world objects and groups (<see cref="ModelFile"/>). Every reference to the part copies it, so an edit
    /// of the part applies to each copy.
    /// </summary>
    public bool Part { get; internal set; }
    /// <summary>For a piece a terrain recipe compiled to: the recipe's project path, the surface and the cell (−1 outside the grid).</summary>
    public string? Terrain { get; internal set; }
    public string? TerrainSurface { get; internal set; }
    public (int Column, int Row) TerrainCell { get; internal set; } = (-1, -1);
    /// <summary>For the root a LoadGameGen created: the project path of the glTF file it loaded.</summary>
    public string? LoadedFile { get; internal set; }
    /// <summary>The instruction that created the node (LoadGameGen for a load's root, NewObject3D, LightNew, NewCamera …).</summary>
    public SourceInstruction? Created { get; internal set; }
    /// <summary>The last AddChild that attached the node.</summary>
    public SourceInstruction? Attached { get; internal set; }
    /// <summary>Per command, the last instruction that changed the node (Object3DTranslate, SetIntersectSurface, WorldSetFogColor …).</summary>
    public Dictionary<string, SourceInstruction> Writers { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// Every instruction that ran while the node was the current node and acted on it, in order: its property and flag
    /// commands, the AddChild and DeleteChild of its children, and commands the build does not apply. Selections
    /// (FindNode) and creations are not included: they change the current node without using it.
    /// </summary>
    public List<SourceInstruction> Applied { get; } = [];
    /// <summary>Instructions that found the node by name to act on it as something other than the current node: AddChild, DeleteTree, DeleteChild, WorldAddLight, CameraSetWorld, CameraSetWindow, CameraSetHorizon and CameraSetHorizonXZ.</summary>
    public List<SourceInstruction> Named { get; } = [];
}
