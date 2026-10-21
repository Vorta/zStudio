namespace zStudio.Core;

/// <summary>The protected reference corpora (<c>zbd_1998</c>, <c>zbd_1999</c>), which nothing may write into.</summary>
public static class ProtectedPaths
{
    /// <summary>Checks both the supplied and resolved destination. Throws if its location cannot be verified.</summary>
    public static bool IsProtected(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return HasProtectedComponent(fullPath) ||
            (OperatingSystem.IsWindows() && HasProtectedComponent(WindowsSavePath.ResolveExistingParent(fullPath)));
    }
    private static bool HasProtectedComponent(string path) => path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        .Any(p => p.Equals("zbd_1998", StringComparison.OrdinalIgnoreCase) || p.Equals("zbd_1999", StringComparison.OrdinalIgnoreCase));
}
