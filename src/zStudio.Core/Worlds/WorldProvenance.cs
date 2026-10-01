namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// One executed script instruction: the script (project path), its 1-based physical line, the command, the tokens as
/// written (command first) and the arguments after macro expansion.
/// </summary>
public sealed record SourceInstruction(string Script, int Line, string Command, IReadOnlyList<string> Tokens, IReadOnlyList<string> Args);

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
    /// <summary>The LoadGameGen that read the node's file.</summary>
    public SourceInstruction? Load { get; internal set; }
    /// <summary>A node of the mission database file itself (the load after GameGenSetWorld, whose scene roots join the world), not of a file it references.</summary>
    public bool Database { get; internal set; }
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
    /// <summary>Instructions that found the node by name to act on it as something other than the current node: AddChild, DeleteTree, DeleteChild, WorldAddLight, CameraSetWorld, CameraSetWindow, CameraSetHorizon.</summary>
    public List<SourceInstruction> Named { get; } = [];
}
