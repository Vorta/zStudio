using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

[CollectionDefinition("Prepared project root aliases", DisableParallelization = true)]
public sealed class PreparedRootAliasCollection { }

[Collection("Prepared project root aliases")]
public sealed partial class ImportRound23PreparedRootTests
{
    [Fact]
    public void AFilesystemRootResolvesOrdinarySourcesAndStillRejectsRootedOrParentPaths()
    {
        string root = Path.GetPathRoot(Path.GetTempPath())!;
        Assert.Equal(Path.Combine(root, "gamegen", "m1.gs"), SourceProject.Resolve(root, "gamegen/m1.gs"));
        Assert.Equal(Path.Combine(root, "data", "m1", "model.gltf"), SourceProject.Resolve(root, "data/m1/model.gltf"));
        Assert.Throws<InvalidDataException>(() => SourceProject.Resolve(root, "../gamegen/m1.gs"));
        Assert.Throws<InvalidDataException>(() => SourceProject.Resolve(root, Path.Combine(root, "gamegen", "m1.gs")));
        Assert.Throws<InvalidDataException>(() => SourceProject.Resolve(root, "gamegen//m1.gs"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeldPreparedFilesDoNotAuthorizeARemappedProjectRoot(bool remapInGuard)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DOS drive alias regression requires Windows.");
        using Temporary tree = new();
        string first = tree.Project("first", "Quit 1\n"), second = tree.Project("second", "Quit 2\n");
        using TestAlias alias = TestAlias.Create(first);
        SourceWorkspace workspace = new(alias.Root);
        var prepared = workspace.BeginPreparedEdit();
        prepared.Workspace.Apply("Change command", [("gamegen/m1.gs", Encoding.ASCII.GetBytes("Quit 3\n"))], TestContext.Current.CancellationToken);
        using var verified = workspace.VerifyPreparedEdit(prepared, TestContext.Current.CancellationToken);
        Assert.Equal(new FileInfo(Path.Combine(first, "gamegen/m1.gs")).Length, new FileInfo(Path.Combine(second, "gamegen/m1.gs")).Length);
        Assert.Equal(File.GetLastWriteTimeUtc(Path.Combine(first, "gamegen/m1.gs")), File.GetLastWriteTimeUtc(Path.Combine(second, "gamegen/m1.gs")));
        if (remapInGuard) workspace.EditGuard = _ => { alias.Remap(second); return null; };
        else alias.Remap(second);
        Assert.Equal(remapInGuard ? "Quit 1\n" : "Quit 2\n", File.ReadAllText(Path.Combine(alias.Root, "gamegen/m1.gs")));
        Assert.Throws<SourceFileChangedException>(() => workspace.AcceptPreparedEdit(prepared, TestContext.Current.CancellationToken, verified));
        Assert.Equal(0, workspace.Revision);
        Assert.Empty(workspace.DirtyFiles);
        Assert.Equal("Quit 1\n", File.ReadAllText(Path.Combine(first, "gamegen/m1.gs")));
        Assert.Equal("Quit 2\n", File.ReadAllText(Path.Combine(second, "gamegen/m1.gs")));
        workspace.EditGuard = null;
        alias.Remap(first);
        Assert.NotNull(workspace.AcceptPreparedEdit(prepared, TestContext.Current.CancellationToken, verified));
    }

    private sealed class Temporary : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-prepared-root-" + Guid.NewGuid().ToString("N"));
        public string Project(string name, string text)
        {
            string project = Path.Combine(root, name);
            Directory.CreateDirectory(Path.Combine(project, "data")); Directory.CreateDirectory(Path.Combine(project, "gamegen"));
            string file = Path.Combine(project, "gamegen/m1.gs");
            File.WriteAllText(file, text, Encoding.ASCII);
            File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            return project;
        }
        public void Dispose() => Directory.Delete(root, true);
    }

    // The nonparallel collection avoids competing with other temporary drive-alias fixtures. Only this
    // fixture's exact mapping is removed; no existing drive or machine policy is changed.
    private sealed partial class TestAlias(string drive, string target) : IDisposable
    {
        private string? currentTarget = target;
        public string Root => drive + @"\";
        public static TestAlias Create(string directory)
        {
            char[] names = new char[32768];
            for (char letter = 'Z'; letter >= 'D'; letter--)
            {
                string drive = letter + ":";
                if (QueryDosDevice(drive, names, (uint)names.Length) != 0) continue;
                int error = Marshal.GetLastPInvokeError();
                if (error != 2) throw new Win32Exception(error);
                string target = @"\??\" + directory;
                if (!DefineDosDevice(0x1 | 0x8, drive, target)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                return new(drive, target);
            }
            throw new IOException("No unused drive letter is available for the temporary prepared-root fixture.");
        }
        public void Remap(string directory)
        {
            Dispose();
            string target = @"\??\" + directory;
            if (!DefineDosDevice(0x1 | 0x8, drive, target)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            currentTarget = target;
        }
        public void Dispose()
        {
            if (currentTarget == null) return;
            if (!DefineDosDevice(0x1 | 0x2 | 0x4 | 0x8, drive, currentTarget)) throw new Win32Exception(Marshal.GetLastPInvokeError());
            currentTarget = null;
        }
        [LibraryImport("kernel32.dll", EntryPoint = "QueryDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial uint QueryDosDevice(string device, [Out] char[] buffer, uint length);
        [LibraryImport("kernel32.dll", EntryPoint = "DefineDosDeviceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DefineDosDevice(uint flags, string device, string target);
    }
}
