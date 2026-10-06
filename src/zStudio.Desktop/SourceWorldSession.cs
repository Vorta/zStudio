using System.Globalization;
using System.IO;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// A mission world edited from its source project. The world shown is a private build of the mission (see
/// <see cref="SourceWorlds.BuildPreviewAsync"/>) in the project's <c>zstudio/cache/worlds</c> folder, zStudio's derived
/// data that builds never read; each build has its own subfolder, removed with the document that shows it. Edits belong to the project's <see cref="Workspace"/>, which every
/// open world of the project shares, and reach the project only when saved. The session passes from document to document
/// as edits rebuild the world.
/// </summary>
internal sealed class SourceWorldSession : IDisposable
{
    public SourceWorkspace Workspace { get; }
    public string Root => Workspace.Root;
    public string Mission { get; }
    public string ScriptPath => $"{SourceProject.GameGenFolder}/{Mission}.gs";
    public string DefinitionsPath => $"{SourceProject.DataFolder}/{Mission}/zrdr/anim{Recoil.Zbd.Core.Animation.AnimationDefinitionSet.Extension}";
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
    internal void SetLookupBaseline(SourceWorldBuild build, ReadOnlyMemory<byte> world) { LookupBaseline = new(world, build.Lookups); LookupBaselinePending = false; }
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
    private readonly string folder;
    private readonly FileStream lockFile;
    private int generation;
    public bool IsDisposed { get; private set; }

    /// <param name="token">Observed while the folders of ended sessions are looked for (see <see cref="SourceWorlds.AbandonedBuilds"/>).</param>
    public SourceWorldSession(SourceWorkspace workspace, string mission, CancellationToken token = default)
    {
        Workspace = workspace; Mission = mission.ToLowerInvariant();
        if (workspace.Read(ScriptPath) == null) throw new InvalidDataException($"The project has no world script {ScriptPath}.");
        string previews = SourceWorlds.PreviewRoot(Root);
        // Never delete or write through a link out of the project: checked before abandoned sessions are removed.
        SourceProject.RejectLinks(previews);
        RemoveAbandoned(previews, token);
        folder = Path.Combine(previews, Guid.NewGuid().ToString("N"));
        SourceProject.RejectLinks(folder);
        Directory.CreateDirectory(folder);
        // Held for the session's lifetime; the system deletes it when the process ends, so an unlocked folder is abandoned.
        lockFile = new FileStream(Path.Combine(folder, ".lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    /// <summary>A new, empty folder for the next build.</summary>
    public string NextFolder()
    {
        ObjectDisposedException.ThrowIf(IsDisposed, this);
        return Path.Combine(folder, (++generation).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Removes one build's files once no document shows them.</summary>
    public static void DeleteBuild(string buildFolder)
    {
        try { SourceProject.RejectLinks(buildFolder); if (Directory.Exists(buildFolder)) Directory.Delete(buildFolder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Folders of sessions whose process ended without removing them (no lock file, older than a minute). A session of
    /// another zStudio on the same project holds its lock file, so its folder stays.
    /// </summary>
    private static void RemoveAbandoned(string previews, CancellationToken token)
    {
        // A folder too full to list is left as it is, like one that cannot be read; a cancellation ends the opening.
        try { foreach (string build in SourceWorlds.AbandonedBuilds(previews, token)) DeleteBuild(build); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (IsDisposed) return;
        IsDisposed = true; Owner = null;
        Building?.Cancel();
        lockFile.Dispose();
        DeleteBuild(folder);
    }
}
