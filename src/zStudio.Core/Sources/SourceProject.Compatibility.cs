namespace zStudio.Core.Sources;

public static partial class SourceProject
{
    public const string LegacyFormatMessage = "This source project uses the obsolete external map-zone format (meta/zones.json). Re-extract the original retail ZBD files into a new source project with the current version of zStudio. Existing project files have not been changed; automatic migration is not supported.";

    /// <summary>Refuse old reconstructed projects before opening, editing or building them. A hand-authored
    /// project with embedded glTF attributes needs no version marker. Never follow links while checking.</summary>
    public static void RequireCurrentFormat(string root, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        RejectNestedLinks(root, DataFolder);
        foreach (string mission in MissionFolders(root, token))
        {
            string relative = $"data/{mission}/meta/zones.json";
            RejectNestedLinks(root, relative);
            if (SourceRead.FileExists(Resolve(root, relative))) throw new InvalidDataException(LegacyFormatMessage);
        }
    }
}
