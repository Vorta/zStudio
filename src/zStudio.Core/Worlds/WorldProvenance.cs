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
    /// <summary>A scene root of the mission database (the load after GameGenSetWorld), which joins the world itself.</summary>
    public bool Database { get; internal set; }
    /// <summary>The instruction that created the node (LoadGameGen for a load's root, NewObject3D, LightNew, NewCamera …).</summary>
    public SourceInstruction? Created { get; internal set; }
    /// <summary>The last AddChild that attached the node.</summary>
    public SourceInstruction? Attached { get; internal set; }
    /// <summary>Per command, the last instruction that changed the node (Object3DTranslate, SetIntersectSurface, WorldSetFogColor …).</summary>
    public Dictionary<string, SourceInstruction> Writers { get; } = new(StringComparer.Ordinal);
}
