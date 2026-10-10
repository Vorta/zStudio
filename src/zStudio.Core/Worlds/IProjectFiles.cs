namespace Recoil.Zbd.Core.Worlds;

/// <summary>Project inputs. A provider must apply admission before materializing, hashing or cloning a payload.</summary>
public interface IProjectFiles
{
    bool Exists(string relative);
    byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits);
    byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
}
