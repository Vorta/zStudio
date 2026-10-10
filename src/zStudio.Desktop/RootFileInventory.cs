using System.IO;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>A complete, privately staged Files tree. Failed discovery never publishes partial rows.</summary>
internal sealed record RootFileInventory(List<FileEntry> Files, FolderNode Root, Dictionary<string, FolderNode> FileNodes)
{
    internal static RootFileInventory Read(string root, StringComparer displayComparer, CancellationToken token, CompiledInventory? inventory = null)
    {
        inventory ??= new(token); inventory.Path(root.Length);
        root = Path.TrimEndingDirectorySeparator(root);
        List<FileEntry> files = [];
        string previews = Path.DirectorySeparatorChar + SourceWorlds.PreviewFolder.Replace('/', Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string recoveryLock = Path.DirectorySeparatorChar + SourcePublisher.LockFile.Replace('/', Path.DirectorySeparatorChar);
        Dictionary<string, bool> projects = new(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inventory.Entries(root, recurse: true, ignoreInaccessible: true, skip: FileAttributes.ReparsePoint))
        {
            token.ThrowIfCancellationRequested();
            if (entry is not FileInfo) continue;
            string file = entry.FullName;
            int at = file.EndsWith(recoveryLock, StringComparison.OrdinalIgnoreCase) ? file.Length - recoveryLock.Length
                : file.IndexOf(previews, Math.Max(0, root.Length - 1), StringComparison.OrdinalIgnoreCase);
            if (at >= Math.Max(0, root.Length - 1))
            {
                inventory.Path(at);
                string project = file[..at];
                if (!projects.TryGetValue(project, out bool isProject)) projects[project] = isProject = SourceProject.IsProject(project);
                if (isProject) continue;
            }
            inventory.Path(file.Length); inventory.Rows(1);
            files.Add(new(file, Path.GetRelativePath(root, file), FormatRegistry.Probe(file)));
        }
        inventory.Sort(files, (a, b) =>
        {
            int order = displayComparer.Compare(a.RelativePath, b.RelativePath);
            return order != 0 ? order : StringComparer.OrdinalIgnoreCase.Compare(a.RelativePath, b.RelativePath);
        }, f => f.RelativePath);
        FolderNode rootNode = new(Path.GetFileName(root), root);
        Dictionary<string, FolderNode> directories = new(StringComparer.OrdinalIgnoreCase) { [root] = rootNode };
        Dictionary<string, FolderNode> nodes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested(); inventory.Path(file.Path.Length);
            string directory = Path.GetDirectoryName(file.Path)!;
            Stack<string> missing = new();
            while (!directories.TryGetValue(directory, out _))
            {
                inventory.Path(directory.Length); inventory.Rows(1); missing.Push(directory);
                directory = Path.GetDirectoryName(directory) ?? throw new IOException("A scanned file is outside the selected folder.");
            }
            var parent = directories[directory];
            while (missing.TryPop(out string? path))
            {
                inventory.Path(path.Length); inventory.Rows(1);
                FolderNode folder = new(Path.GetFileName(path), path); directories.Add(path, folder); parent.Children.Add(folder); parent = folder;
            }
            inventory.Rows(1);
            FolderNode node = new(file.Name, file.Path, file); nodes[file.Path] = node; parent.Children.Add(node);
        }
        token.ThrowIfCancellationRequested(); return new(files, rootNode, nodes);
    }
}
