using System.IO;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class MixedModeCacheTests
{
    [Fact]
    public void LoadedConcurrentWinnerIsAcceptedOnlyWhenItsBytesMatch()
    {
        // A competing cold start can load its published DLL before our writer moves its copy.
        string root = Path.Combine(Path.GetTempPath(), "zstudio-mixed-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] expected = [1, 2, 3, 4];
            string target = Path.Combine(root, "cached.dll"), temporary = Path.Combine(root, "pending.tmp");
            File.WriteAllBytes(target, expected); File.WriteAllBytes(temporary, expected);
            using (var loaded = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                MixedModeAssemblies.Publish(temporary, target, expected);
                Assert.Equal(expected, File.ReadAllBytes(target));
                Assert.True(File.Exists(temporary));
            }
            File.WriteAllBytes(target, [9, 8, 7, 6]);
            using (var loaded = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var error = Record.Exception(() => MixedModeAssemblies.Publish(temporary, target, expected));
                Assert.True(error is IOException or UnauthorizedAccessException);
                Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(target));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
