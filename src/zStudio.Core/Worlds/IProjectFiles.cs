namespace Recoil.Zbd.Core.Worlds;

/// <summary>Project inputs. A provider must apply admission before materializing, hashing or cloning a payload.</summary>
public interface IProjectFiles
{
    bool Exists(string relative);
    /// <summary>
    /// Whether a project folder exists: what the engine's search-path routine asks of each folder a script names before it
    /// lists it (<c>zRdrAddSearchPaths</c>, retail 0x4a5ce0, with <c>_access</c>; see <see cref="DirectorySearchList"/>).
    /// A provider that cannot tell treats every folder as present, as the original build tree had the folders its scripts
    /// name: a folder that does not exist holds no file, so the searches still find the same files.
    /// </summary>
    bool FolderExists(string relative) => true;
    byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits);
    byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
}
