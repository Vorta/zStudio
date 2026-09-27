namespace Recoil.Zbd.Core;

/// <summary>Workspace-local ownership, shared by archive editing and specialized writers.</summary>
public sealed class ResourceEditOwnership
{
    private readonly Dictionary<string, (Guid Id, string Label)> owners = new(StringComparer.OrdinalIgnoreCase);
    private static string Key(string path) => OperatingSystem.IsWindows() ? WindowsSavePath.ResolveExistingParent(Path.GetFullPath(path)) : Path.GetFullPath(path);
    public void Acquire(Guid owner, string label, IEnumerable<string> paths)
    {
        string[] keys = paths.Select(Key).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (string key in keys)
            if (owners.TryGetValue(key, out var current) && current.Id != owner)
                throw new InvalidOperationException($"{key} is owned by edits in {current.Label}. Save/discard and close that document before editing here.");
        foreach (string key in keys) owners[key] = (owner, label);
    }
    public void Release(Guid owner) { foreach (string key in owners.Where(p => p.Value.Id == owner).Select(p => p.Key).ToArray()) owners.Remove(key); }
}
