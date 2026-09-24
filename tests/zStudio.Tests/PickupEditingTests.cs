using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public async Task PickupMoveLinksAllDifficultiesAndChangesOnlyCoordinateBytes()
    {
        await WithMissionArchiveAsync(new() {
            ["puppies.zrd"] = Zrd(PickupList(PickupRow(), PickupRow("NANITE"))),
            ["puppies_easy.zrd"] = Zrd(PickupList(PickupRow("NANITE", 20), PickupRow(amount: 5))),
            ["puppies_hard.zrd"] = Zrd(PickupList(PickupRow(amount: 10), PickupRow("NANITE", 100))),
            ["unrelated.bin"] = [1, 2, 99, 81, 33]
        }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            string path = Assert.Single(edits.ArchivePaths); byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(original, edits.EncodeArchive(path));
            var selected = edits.Records.Single(r => r.Type == "NANITE" && r.Difficulties.Contains(MissionDifficulty.Medium));
            var scope = edits.Scope(selected.Source); Assert.Equal(3, scope.Sources.Count);
            Assert.False(edits.MoveTo(selected.Source, selected.OriginalPosition)); Assert.False(edits.CanUndo);
            var position = selected.OriginalPosition + new Vector3(2.25f, -7, 15);
            Assert.True(edits.MoveTo(selected.Source, position)); Assert.True(edits.IsDirty);
            foreach (var source in scope.Sources) Assert.Equal(position, edits.Position(source));
            foreach (var other in edits.Records.Where(r => r.Type != "NANITE")) Assert.Equal(other.OriginalPosition, edits.Position(other.Source));
            byte[] encoded = edits.EncodeArchive(path); var archive = FormatRegistry.Default.OpenBytes(path, original);
            HashSet<int> permitted = [];
            foreach (var source in scope.Sources)
            {
                var asset = archive.Assets.Single(a => a.Index == source.AssetIndex);
                var tree = ZrdDecoder.Decode(archive.Slice(asset.Offset, asset.Length));
                var xyz = tree["children"]![0]!["children"]![source.RecordIndex]!["children"]![2]!["children"]!.AsArray();
                foreach (var component in xyz)
                {
                    int offset = (int)asset.Offset + int.Parse(component!.Text("offset").AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    for (int i = 4; i < 8; i++) permitted.Add(offset + i);
                }
            }
            Assert.Equal(original.Length, encoded.Length);
            Assert.All(Enumerable.Range(0, original.Length).Where(i => original[i] != encoded[i]), i => Assert.Contains(i, permitted));
            edits.Undo(); Assert.False(edits.IsDirty); Assert.False(edits.CanUndo); Assert.Equal(original, edits.EncodeArchive(path));
            edits.Redo(); Assert.True(edits.IsDirty); Assert.Equal(encoded, edits.EncodeArchive(path));
        });
    }
    [Fact]
    public async Task PickupFallbackIsMovedOnceAndAmbiguousCounterpartsAreSkipped()
    {
        await WithMissionArchiveAsync(new() {
            ["puppies.zrd"] = Zrd(PickupList(PickupRow())),
            ["puppies_easy.zrd"] = Zrd(PickupList(PickupRow(), PickupRow(amount: 8)))
        }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            var selected = edits.Records.Single(r => r.Difficulties.Contains(MissionDifficulty.Medium));
            Assert.Contains(MissionDifficulty.Hard, selected.Difficulties);
            Assert.Single(edits.Scope(selected.Source).Sources); Assert.Contains("ambiguous", edits.Scope(selected.Source).Description);
            edits.MoveTo(selected.Source, selected.OriginalPosition + Vector3.UnitY);
            Assert.Equal(selected.OriginalPosition + Vector3.UnitY, edits.Position(selected.Source));
            Assert.All(edits.Records.Where(r => r.Difficulties.Contains(MissionDifficulty.Easy)), r => Assert.Equal(r.OriginalPosition, edits.Position(r.Source)));
        });
    }
    [Fact]
    public async Task PickupMatchingUsesOriginalPoseAcrossRepeatedEditsAndNotRecordIndex()
    {
        var different = PickupRow(); different["children"]![2]!["children"]![0]!["value"] = 40f;
        await WithMissionArchiveAsync(new() {
            ["puppies.zrd"] = Zrd(PickupList(PickupRow())),
            ["puppies_easy.zrd"] = Zrd(PickupList(different, PickupRow())),
            ["puppies_hard.zrd"] = Zrd(PickupList(PickupRow(yaw: 1)))
        }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            var selected = edits.Records.Single(r => r.Difficulties.Contains(MissionDifficulty.Medium));
            var scope = edits.Scope(selected.Source); Assert.Equal(2, scope.Sources.Count);
            Assert.Contains(scope.Sources, s => s.RecordIndex == 1);
            edits.MoveTo(selected.Source, new(20, 30, 40)); edits.MoveTo(selected.Source, new(50, 60, 70));
            foreach (var source in scope.Sources) Assert.Equal(new(50, 60, 70), edits.Position(source));
            edits.Undo(); foreach (var source in scope.Sources) Assert.Equal(new(20, 30, 40), edits.Position(source));
            Assert.Throws<InvalidDataException>(() => edits.MoveTo(selected.Source, new(float.NaN, 0, 0)));
        });
    }
    [Fact]
    public async Task IntegerCoordinatesBecomeFloatsOnlyWhenEditedAndMalformedSiblingsAreRetained()
    {
        var integer = PickupRow(); integer["children"]![2]!["children"]![2] = new JsonObject { ["type"] = "int", ["value"] = 712 };
        await WithMissionArchiveAsync(new() {
            ["puppies.zrd"] = Zrd(PickupList(PickupRow("UNKNOWN"), integer, Arr(Str("bad"))))
        }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            var record = Assert.Single(edits.Records); Assert.Equal(1, record.Source.RecordIndex); Assert.Equal(2, edits.Diagnostics.Count);
            string path = Assert.Single(edits.ArchivePaths); byte[] before = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(before, edits.EncodeArchive(path));
            edits.MoveTo(record.Source, record.OriginalPosition with { Z = 712.5f });
            var result = await edits.SaveAsync(token: TestContext.Current.CancellationToken); Assert.Empty(result.Errors); Assert.False(edits.IsDirty);
            using var fresh = new AssetResolver(Path.GetDirectoryName(path)!);
            var reopened = await PickupPlacementEditSession.LoadAsync(world.Path, fresh, TestContext.Current.CancellationToken);
            Assert.Equal(712.5f, Assert.Single(reopened.Records).OriginalPosition.Z);
            edits.Undo(); Assert.Equal(before, edits.EncodeArchive(path));
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickupSaveOverwritesOnlyWithMatchingBaselineAndOptionalBackup(bool backup)
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            string path = Assert.Single(edits.ArchivePaths); byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var record = Assert.Single(edits.Records); edits.MoveTo(record.Source, new(99, 88, 77));
            byte[] expected = edits.EncodeArchive(path); var result = await edits.SaveAsync(createBackup: backup, token: TestContext.Current.CancellationToken);
            Assert.Empty(result.Errors); Assert.Single(result.SavedPaths); Assert.False(edits.IsDirty); Assert.False(edits.HasExternalChanges());
            Assert.Equal(expected, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "*.bak");
            Assert.Equal(backup ? 1 : 0, backups.Length);
            if (backup) Assert.Equal(original, await File.ReadAllBytesAsync(backups[0], TestContext.Current.CancellationToken));
            edits.Undo(); Assert.True(edits.IsDirty); edits.Redo(); Assert.False(edits.IsDirty);
            edits.Undo(); await edits.SaveAsync(token: TestContext.Current.CancellationToken); Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".zstudio-pickups-*.tmp"));
        });
    }
    [Fact]
    public async Task PickupSaveAsRetargetsFutureSavesAndNeverOverwritesAnUnrelatedFile()
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            string path = Assert.Single(edits.ArchivePaths), copy = Path.Combine(Path.GetDirectoryName(path)!, "copy.zbd");
            byte[] original = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
            var record = Assert.Single(edits.Records); edits.MoveTo(record.Source, new(1, 2, 3));
            var destinations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = copy };
            var result = await edits.SaveAsync(destinations, token: TestContext.Current.CancellationToken); Assert.Empty(result.Errors); Assert.Equal(copy, edits.TargetPath(path));
            edits.MoveTo(record.Source, new(4, 5, 6)); await edits.SaveAsync(token: TestContext.Current.CancellationToken);
            Assert.Equal(original, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(edits.EncodeArchive(path), await File.ReadAllBytesAsync(copy, TestContext.Current.CancellationToken));
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = path }, token: TestContext.Current.CancellationToken));
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PickupSaveAsRejectsCurrentTargetWithoutChangingBytesOrSavedState(bool retargeted)
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken);
            string source = Assert.Single(edits.ArchivePaths);
            var record = Assert.Single(edits.Records);
            if (retargeted)
            {
                edits.MoveTo(record.Source, new(11, 22, 33));
                await edits.SaveAsync(new Dictionary<string, string> { [source] = Path.Combine(Path.GetDirectoryName(source)!, "copy.zbd") }, token: TestContext.Current.CancellationToken);
            }
            string target = edits.TargetPath(source);
            byte[] before = await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken);
            edits.MoveTo(record.Source, new(44, 55, 66));
            foreach (string destination in new[] { target, Path.Combine(Path.GetDirectoryName(target)!, ".", Path.GetFileName(target)) })
            {
                var error = await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(new Dictionary<string, string> { [source] = destination }, createBackup: true, token: TestContext.Current.CancellationToken));
                Assert.Contains("Save As requires a new file", error.Message);
                Assert.Equal(before, await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
                Assert.Equal(target, edits.TargetPath(source)); Assert.True(edits.IsDirty); Assert.True(edits.CanUndo);
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, ".zstudio-pickups-*.tmp"));
                Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(target)!, "*.bak"));
            }
            // Ordinary Save must still replace the active target after the refused Save As.
            var saved = await edits.SaveAsync(token: TestContext.Current.CancellationToken);
            Assert.Empty(saved.Errors); Assert.Equal(target, Assert.Single(saved.SavedPaths)); Assert.False(edits.IsDirty);
            Assert.Equal(edits.EncodeArchive(source), await File.ReadAllBytesAsync(target, TestContext.Current.CancellationToken));
        });
    }

    [Fact]
    public async Task ExternalChangesAndProtectedDestinationsRetainUnsavedPickupEdits()
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, resolver) => {
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, TestContext.Current.CancellationToken); var record = Assert.Single(edits.Records);
            string path = Assert.Single(edits.ArchivePaths); edits.MoveTo(record.Source, new(44, 55, 66));
            byte[] external = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken); external[12] ^= 1;
            await File.WriteAllBytesAsync(path, external, TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: TestContext.Current.CancellationToken)); Assert.True(edits.IsDirty);
            Assert.Equal(external, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
            string forbidden = Path.Combine(Path.GetDirectoryName(path)!, "zbd_1999", "copy.zbd");
            await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [path] = forbidden }, token: TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(Path.GetDirectoryName(forbidden))); Assert.True(edits.IsDirty);
        });
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiplePickupArchivesStageBeforeCommitAndReportPartialReplacement(bool lockSecond)
    {
        await WithMissionArchiveAsync(new() { ["puppies.zrd"] = Zrd(PickupList(PickupRow())) }, async (world, resolver) => {
            string firstPath = Path.Combine(Path.GetDirectoryName(world.Path)!, "resources.zbd");
            string secondPath = Path.Combine(Path.GetDirectoryName(world.Path)!, "other.zbd");
            byte[] firstBytes = await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken);
            byte[] secondBytes = (byte[])firstBytes.Clone();
            int nameOffset = secondBytes.Length - 8 - 148 + 8;
            secondBytes.AsSpan(nameOffset, 64).Clear(); System.Text.Encoding.Latin1.GetBytes("puppies_hard.zrd").CopyTo(secondBytes, nameOffset);
            await File.WriteAllBytesAsync(secondPath, secondBytes, TestContext.Current.CancellationToken);
            var first = await FormatRegistry.Default.OpenAsync(firstPath, TestContext.Current.CancellationToken);
            var second = await FormatRegistry.Default.OpenAsync(secondPath, TestContext.Current.CancellationToken);
            var edits = PickupPlacementEditSession.Create([new(MissionDifficulty.Medium, first, first.Assets[0]), new(MissionDifficulty.Hard, second, second.Assets[0])], token: TestContext.Current.CancellationToken);
            edits.MoveTo(edits.Records[0].Source, new(111, 222, 333));
            if (lockSecond)
            {
                using (var held = new FileStream(secondPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var result = await edits.SaveAsync(token: TestContext.Current.CancellationToken);
                    Assert.Equal(firstPath, Assert.Single(result.SavedPaths)); Assert.Single(result.Errors);
                    Assert.False(edits.IsArchiveDirty(firstPath)); Assert.True(edits.IsArchiveDirty(secondPath));
                }
                var remaining = await edits.SaveAsync(token: TestContext.Current.CancellationToken);
                Assert.Equal(secondPath, Assert.Single(remaining.SavedPaths)); Assert.Empty(remaining.Errors); Assert.False(edits.IsDirty);
            }
            else
            {
                secondBytes[12] ^= 1; await File.WriteAllBytesAsync(secondPath, secondBytes, TestContext.Current.CancellationToken);
                await Assert.ThrowsAsync<IOException>(() => edits.SaveAsync(token: TestContext.Current.CancellationToken));
                Assert.Equal(firstBytes, await File.ReadAllBytesAsync(firstPath, TestContext.Current.CancellationToken));
                Assert.Equal(secondBytes, await File.ReadAllBytesAsync(secondPath, TestContext.Current.CancellationToken));
                Assert.True(edits.IsArchiveDirty(firstPath)); Assert.True(edits.IsArchiveDirty(secondPath));
            }
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(world.Path)!, ".zstudio-pickups-*.tmp"));
        });
    }
}
