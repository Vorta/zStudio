using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed partial class ExportDirectoryLifetimeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AProgressCallbackCannotSplitAnExportAcrossReplacedDirectories(bool jsonOnly)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows directory leases require Windows.");
        string root = Path.Combine(Path.GetTempPath(), "zstudio-export-lifetime-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "exports"); Directory.CreateDirectory(source);
        try
        {
            using AssetResolver resolver = new(source);
            AssetDocument document = new(Path.Combine(source, "input.zbd"), new(2, DateTime.UtcNow), new(FormatFamily.Unknown, null, Recognition.Unknown, "fixture"), new byte[] { 10, 20 });
            document.Add(AssetKind.Raw, 0, "one.bin", 0, 1); document.Add(AssetKind.Raw, 1, "two.bin", 1, 1);
            bool challenged = false;
            var result = await new ExportService(resolver).ExportAsync(document, document.Assets, destination, jsonOnly, progress: new OnReport(p =>
            {
                if (p.Completed != 1) return;
                challenged = true;
                string target = Assert.Single(Directory.GetDirectories(destination));
                Assert.ThrowsAny<IOException>(() => Directory.Move(target, target + "-moved"));
                Assert.ThrowsAny<IOException>(() => Directory.Move(Path.Combine(target, "Raw"), Path.Combine(target, "moved")));
                Assert.ThrowsAny<IOException>(() => Directory.Move(destination, destination + "-moved"));
                Assert.ThrowsAny<IOException>(() => Directory.Move(source, source + "-moved"));
            }), token: TestContext.Current.CancellationToken);
            Assert.True(challenged); Assert.Equal(2, result.Completed); Assert.Empty(result.Errors);
            Assert.True(File.Exists(Path.Combine(result.Directory, "export-report.json")));
            string first = Path.Combine(result.Directory, "Raw", "00000_one.bin"), second = Path.Combine(result.Directory, "Raw", "00001_two.bin");
            if (jsonOnly)
            {
                Assert.Equal("one.bin", JsonNode.Parse(File.ReadAllText(first + ".json"))!["name"]!.GetValue<string>());
                Assert.Equal("two.bin", JsonNode.Parse(File.ReadAllText(second + ".json"))!["name"]!.GetValue<string>());
            }
            else { Assert.Equal(new byte[] { 10 }, File.ReadAllBytes(first)); Assert.Equal(new byte[] { 20 }, File.ReadAllBytes(second)); }
            Directory.Move(result.Directory, result.Directory + "-after"); // The complete operation released its lease.
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("source")]
    [InlineData("zbd_1999")]
    public async Task ModelBundleAndStandardExportsRejectProtectedDestinations(string folder)
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-export-guard-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, folder, "nested", "export");
        using AssetResolver resolver = new(source);
        var token = TestContext.Current.CancellationToken;
        var document = FormatRegistry.Default.OpenBytes(Path.Combine(source, "gamez.zbd"), ModelFixture.GameZ(), token: token);
        var exporter = new ExportService(resolver);
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportModelBundleAsync(document, 0, destination, token: token));
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(document, document.Assets, destination, false, token: token));
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task ALinkedSourceRootRefusesItsPhysicalInputTreeAndExportsElsewhere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows junction regression requires Windows.");
        string root = Path.Combine(Path.GetTempPath(), "zstudio-export-source-link-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "physical-source"), link = Path.Combine(root, "opened-root");
        Directory.CreateDirectory(source);
        try
        {
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{source}\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true }))
            {
                Assert.NotNull(process); await process.WaitForExitAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, process.ExitCode);
            }
            Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint));
            await AssertRootAliasPolicy(source, link, Path.Combine(root, "external"));
        }
        finally
        {
            // Remove this fixture's junction before recursively deleting only its disposable parent.
            if (Directory.Exists(link)) Directory.Delete(link);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task AShortSourceRootRefusesItsPhysicalInputTreeAndExportsElsewhere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows short-directory regression requires Windows.");
        string root = Path.Combine(Path.GetTempPath(), "zstudio-export-source-short-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "physical-source-with-a-long-name"); Directory.CreateDirectory(source);
        try
        {
            char[] buffer = new char[32768];
            uint length = GetShortPathName(source, buffer, (uint)buffer.Length);
            Assert.InRange(length, 1u, (uint)buffer.Length - 1);
            string alias = new(buffer, 0, (int)length);
            Assert.SkipWhen(Path.GetFileName(alias).Equals(Path.GetFileName(source), StringComparison.OrdinalIgnoreCase),
                "The temporary volume does not provide a final-component 8.3 alias for this fixture.");
            await AssertRootAliasPolicy(source, alias, Path.Combine(root, "external"));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static async Task AssertRootAliasPolicy(string source, string alias, string outside)
    {
        var token = TestContext.Current.CancellationToken;
        byte[] bytes = ModelFixture.GameZ();
        string input = Path.Combine(source, "gamez.zbd"); await File.WriteAllBytesAsync(input, bytes, token);
        using AssetResolver resolver = new(alias);
        var document = FormatRegistry.Default.OpenBytes(Path.Combine(alias, "gamez.zbd"), bytes, token: token);
        var exporter = new ExportService(resolver);
        var model = document.Assets.First(a => a.Kind == AssetKind.Model);
        string inside = Path.Combine(source, "export");
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportAsync(document, [model], inside, false, token: token));
        await Assert.ThrowsAsync<IOException>(() => exporter.ExportModelBundleAsync(document, 0, inside, token: token));
        Assert.False(Directory.Exists(inside));
        bool challenged = false;
        var exported = await exporter.ExportAsync(document, [model], outside, false, progress: new OnReport(_ =>
        {
            challenged = true;
            Assert.ThrowsAny<IOException>(() => Directory.Move(source, source + "-moved"));
        }), token: token);
        Assert.True(challenged); Assert.Equal(1, exported.Completed); Assert.Empty(exported.Errors);
        Assert.Single(Directory.GetFiles(exported.Directory, "*.obj", SearchOption.AllDirectories));
        var bundle = await exporter.ExportModelBundleAsync(document, 0, outside, token: token);
        Assert.True(File.Exists(Path.Combine(bundle.Directory, "assembled.obj")));
        Assert.True(File.Exists(bundle.Manifest));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(input, token));
        Assert.Equal([input], Directory.GetFiles(source, "*", SearchOption.AllDirectories));
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetShortPathNameW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial uint GetShortPathName(string path, [Out] char[] buffer, uint length);

    private sealed class OnReport(Action<ExportProgress> action) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => action(value);
    }
}
