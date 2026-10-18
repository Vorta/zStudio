using System.IO;
using zStudio.Core.Worlds;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// An accepted source mission evaluated in memory. Every open mission shares the project's workspace;
/// edits reach the source files only on Save. Game archives are produced only by export.
/// </summary>
internal sealed class SourceWorldSession : IDisposable
{
    public SourceWorkspace Workspace { get; }
    public string Root => Workspace.Root;
    public string Mission { get; }
    public string ScriptPath => $"{SourceProject.GameGenFolder}/{Mission}.gs";
    public string DefinitionsPath => $"{SourceProject.DataFolder}/{Mission}/zrdr/anim{zStudio.Core.Animation.AnimationDefinitionSet.Extension}";
    public string Label => $"{Mission} world (sources)";
    /// <summary>The document currently showing this world; disposing it ends the session.</summary>
    internal DocumentModel? Owner { get; set; }
    /// <summary>
    /// What the mission looked up by name when the world was opened or last saved: that build's world and lookups. Each
    /// rebuild after an edit is compared with it, so a change to the node a lookup finds is reported until it is saved or taken back.
    /// </summary>
    internal SourceLookupBaseline? LookupBaseline { get; private set; }
    /// <summary>
    /// The project was saved while this world showed a build older than the saved sources: what it looks up as saved is not
    /// known until a build reads none of the unsaved sources, which becomes the baseline. Until then nothing is compared.
    /// </summary>
    internal bool LookupBaselinePending { get; private set; }
    /// <summary>The build shown and its world as the document holds it (the build's files are not read again).</summary>
    internal void SetLookupBaseline(SourceSceneState build, GameZWorld world) { LookupBaseline = new(world, build.Lookups, build.Provenance); LookupBaselinePending = false; }
    internal void ForgetLookupBaseline() { LookupBaseline = null; LookupBaselinePending = true; }
    /// <summary>How many lookups by name the shown build finds another node for than <see cref="LookupBaseline"/>, as Problems lists them.</summary>
    internal int LookupChangeCount { get; set; }
    /// <summary>Cancels the newest build request; a newer request or the session's end supersedes it.</summary>
    internal CancellationTokenSource? Building { get; set; }
    /// <summary>
    /// An edit, undo, redo or reload is rebuilding the world until its replacement is shown. Other edits of the project, saves
    /// and reloads wait for it, so the world shown always matches the workspace and only edits it was built with are saved.
    /// </summary>
    internal bool IsRebuilding { get; set; }
    public bool IsDisposed { get; private set; }

    public SourceWorldSession(SourceWorkspace workspace, string mission, CancellationToken token = default)
    {
        Workspace = workspace; Mission = mission.ToLowerInvariant();
        if (!workspace.Exists(ScriptPath, token)) throw new InvalidDataException($"The project has no world script {ScriptPath}.");
    }

    internal Task<SourceSceneSnapshot> BuildAsync(IReadOnlyDictionary<string, byte[]>? overlay,
        IProgress<SourceProgress>? progress, CancellationToken token, IReadOnlyList<SourceModelAddition>? additions)
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        progress?.Report(new(0, 1, Mission));
        return SourceWorlds.LoadSceneAsync(Root, Mission, overlay, token, additions);
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; Owner = null;
        Building?.Cancel();
    }
}
