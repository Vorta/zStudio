using System.Globalization;
using System.IO;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// A mission world edited from its source project. The world shown is a private build of the mission (see
/// <see cref="SourceWorlds.BuildPreviewAsync"/>) in a temporary folder outside the project; each build has its own
/// subfolder, removed with the document that shows it. Pending edits live in <see cref="Edits"/> and reach the project
/// only when saved. The session passes from document to document as edits rebuild the world.
/// </summary>
internal sealed class SourceWorldSession : IDisposable
{
    internal static string TemporaryRoot => Path.Combine(Path.GetTempPath(), "zStudio", "source-worlds");
    public string Root => Edits.Root;
    public string Mission => Edits.Mission;
    public SourceWorldEdits Edits { get; }
    public string Label => $"{Mission} world (sources)";
    /// <summary>The document currently showing this world; disposing it ends the session.</summary>
    internal DocumentModel? Owner { get; set; }
    /// <summary>Cancels the newest build request; a newer request or the session's end supersedes it.</summary>
    internal CancellationTokenSource? Building { get; set; }
    /// <summary>
    /// An edit, undo, redo or reload is rebuilding the world until its replacement is shown. Other edits, saves and
    /// reloads wait for it, so the world shown always matches the edits and only edits it was built with are saved.
    /// </summary>
    internal bool IsRebuilding { get; set; }
    private readonly string folder;
    private readonly FileStream lockFile;
    private int generation;
    public bool IsDisposed { get; private set; }

    public SourceWorldSession(string root, string mission)
    {
        Edits = new(root, mission);
        RemoveAbandoned();
        folder = Path.Combine(TemporaryRoot, Guid.NewGuid().ToString("N"));
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
        try { if (Directory.Exists(buildFolder)) Directory.Delete(buildFolder, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Folders of sessions whose process ended without removing them (no lock file, older than a minute).</summary>
    private static void RemoveAbandoned()
    {
        try
        {
            if (!Directory.Exists(TemporaryRoot)) return;
            foreach (var directory in new DirectoryInfo(TemporaryRoot).EnumerateDirectories())
                if (!File.Exists(Path.Combine(directory.FullName, ".lock")) && directory.CreationTimeUtc < DateTime.UtcNow.AddMinutes(-1))
                    DeleteBuild(directory.FullName);
        }
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
