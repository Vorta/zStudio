using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ExportDirectoryLifetimeTests
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
            ZbdDocument document = new(Path.Combine(source, "input.zbd"), new(2, DateTime.UtcNow), new(FormatFamily.Unknown, null, Recognition.Unknown, "fixture"), new byte[] { 10, 20 });
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

    private sealed class OnReport(Action<ExportProgress> action) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => action(value);
    }
}
