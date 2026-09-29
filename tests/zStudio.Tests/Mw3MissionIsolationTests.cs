using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class Mw3MissionIsolationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UnreadableArchivesAreReportedWithoutHidingOtherMissions()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string reader = Path.Combine(fixture.Folder, "readerm2.zbd"), shared = Path.Combine(fixture.Folder, "shared.zbd");
        File.WriteAllBytes(reader, fixture.ReaderBytes); File.WriteAllBytes(shared, Archive(("fx.zrd", Zrd())));
        // Another process holds write access: the probe succeeds, but opening a consistent snapshot fails.
        using (new FileStream(reader, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        using (new FileStream(shared, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var missions = await MissionSceneLoader.Mw3MissionsAsync(fixture.World.Path, fixture.Resolver, Token);
            Assert.Equal(fixture.ReaderPath, Assert.Single(missions).Archive, ignoreCase: true);
            var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
            Assert.Equal("actor_01", Assert.Single(mission.Actors).Name);
            Assert.Contains(mission.Diagnostics, d => d.Contains("readerm2.zbd", StringComparison.Ordinal));
            Assert.Contains(mission.Diagnostics, d => d.Contains("shared.zbd", StringComparison.Ordinal));
        }
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
    }

    [Fact]
    public async Task RememberedMissionThatNoLongerQualifiesFallsBackWithADiagnostic()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        fixture.Resolver.SelectMission(fixture.World.Path, second);
        // The reader still exists, but its AIV member was renamed and saved.
        File.WriteAllBytes(second, Archive(("old_aiv.zrd", Zrd())));
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Equal("actor_01", Assert.Single(mission.Actors).Name);
        Assert.Contains(mission.Diagnostics, d => d.Contains("readerm2.zbd", StringComparison.Ordinal) && d.Contains("no longer available", StringComparison.Ordinal));
        Assert.Equal(fixture.ReaderPath, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
    }

    [Fact]
    public async Task ExactUnavailableRequestsFailAndOlderFallbacksNeverReplaceANewerChoice()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string stale = Path.Combine(fixture.Folder, "readerm2.zbd"), newer = Path.Combine(fixture.Folder, "readerm3.zbd");
        File.WriteAllBytes(newer, fixture.ReaderBytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token, mission: stale, exactMission: true));
        Assert.Null(fixture.Resolver.SelectedMission(fixture.World.Path));
        // A refresh captured the stale choice before the user selected readerm3.
        fixture.Resolver.SelectMission(fixture.World.Path, newer);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token, mission: stale);
        Assert.Equal(fixture.ReaderPath, mission.Layout.MissionArchive, ignoreCase: true);
        Assert.Equal(stale, mission.Layout.UnavailableMission, ignoreCase: true);
        Assert.Equal(newer, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
    }

    [Fact]
    public async Task RawMemberSharingTheAivNameDoesNotMakeTheMissionAmbiguous()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        byte[] aiv = Member(fixture.ReaderBytes, 0);
        File.WriteAllBytes(fixture.ReaderPath, Archive(("aiv.zrd", aiv), ("aiv.zrd", [0xFF, 0xFF, 0xFF, 0xFF])));
        Assert.Single(await MissionSceneLoader.Mw3MissionsAsync(fixture.World.Path, fixture.Resolver, Token));
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Equal("actor_01", Assert.Single(mission.Actors).Name);
    }

    [Fact]
    public async Task CoordinateEditsPublishAndCheckOnlyTheArchivesTheyEdited()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        var edits = await PickupPlacementEditSession.LoadAsync(fixture.World.Path, fixture.Resolver, Token);
        var first = Record(edits, fixture.ReaderPath);
        Assert.True(edits.MoveTo(first.Source, new(1, 2, 3)));
        Assert.Equal(fixture.ReaderPath, Assert.Single(edits.WorkingArchives(Token)).Path, ignoreCase: true);
        // An unrelated reader saved elsewhere is not an external change to these edits.
        File.WriteAllBytes(second, Rewritten(fixture.ReaderBytes, heading: 45));
        Assert.False(edits.HasExternalChanges()); Assert.False(edits.HasSourceChanges());
        Assert.True(edits.HasStaleBaselines(_ => null));
    }

    [Fact]
    public async Task CoordinateAndResourceDocumentsEditDifferentReadersInEitherOrder()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        var resolver = fixture.Resolver; ResourceEditOwnership ownership = new(); Guid worldOwner = Guid.NewGuid(), readerOwner = Guid.NewGuid();

        // The reader document edits and publishes readerm2 before the map loads its coordinates.
        var readerDoc = await FormatRegistry.Default.OpenAsync(second, Token); var reader = new ResourceEditSession(readerDoc);
        reader.BeforeEdit += () => ownership.Acquire(readerOwner, "reader", [second, reader.TargetPath]);
        reader.Changed += () => resolver.SetWorkspaceSnapshots(readerOwner, [reader.Current.Document]);
        await SetHeading(reader, 30);

        var edits = await PickupPlacementEditSession.LoadAsync(fixture.World.Path, resolver, Token);
        Wire(edits, resolver, ownership, worldOwner);
        Assert.Equal(30, Record(edits, second).Rotation.Y);
        var first = Record(edits, fixture.ReaderPath);
        Assert.True(edits.MoveTo(first.Source, new(1, 2, 3)));
        Assert.Equal(new[] { fixture.ReaderPath }, edits.EditedArchivePaths, StringComparer.OrdinalIgnoreCase);

        // Same-archive edits from two documents remain rejected without partial history.
        var owned = Record(edits, second).Source;
        Assert.Throws<InvalidOperationException>(() => edits.MoveTo(owned, new(9, 9, 9)));
        Assert.False(edits.IsArchiveEdited(second)); Assert.True(edits.CanUndo); edits.Undo(); edits.Redo();

        // The reader saves; its published copy continues to be the served baseline.
        await reader.SaveAsync(token: Token); byte[] readerSaved = File.ReadAllBytes(second);
        Assert.False(edits.HasExternalChanges()); Assert.False(edits.HasStaleBaselines(Published(resolver, worldOwner)));
        await SetHeading(reader, 60); await reader.SaveAsync(token: Token); readerSaved = File.ReadAllBytes(second);
        Assert.True(edits.HasStaleBaselines(Published(resolver, worldOwner)));
        Assert.Throws<InvalidOperationException>(() => edits.MoveTo(owned, new(8, 8, 8))); // Stale and owned elsewhere.
        edits.RebaseUntouched(await PickupPlacementEditSession.LoadAsync(fixture.World.Path, resolver, Token));
        Assert.False(edits.HasStaleBaselines(Published(resolver, worldOwner)));
        Assert.Equal(60, Record(edits, second).Rotation.Y);
        Assert.Equal(new Vector3(1, 2, 3), edits.Position(first.Source)); Assert.True(edits.CanUndo);

        // The map saves only its own reader; the other document's saved bytes stay intact.
        Assert.True(edits.MoveTo(first.Source, new(4, 5, 6)));
        var result = await edits.SaveAsync(token: Token);
        Assert.Empty(result.Errors); Assert.Equal(fixture.ReaderPath, Assert.Single(result.SavedPaths), ignoreCase: true);
        Assert.Equal(readerSaved, File.ReadAllBytes(second));
        AssertOnlyPositionChanged(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath), new(4, 5, 6));

        // After the reader closes, the map may edit it from the saved file, not the retired copy.
        resolver.SetWorkspaceSnapshots(readerOwner, []); ownership.Release(readerOwner);
        Assert.True(edits.HasStaleBaselines(Published(resolver, worldOwner)));
        edits.RebaseUntouched(await PickupPlacementEditSession.LoadAsync(fixture.World.Path, resolver, Token));
        Assert.True(edits.MoveTo(owned, new(7, 7, 7)));
        Assert.Empty((await edits.SaveAsync(token: Token)).Errors);
        AssertOnlyPositionChanged(readerSaved, File.ReadAllBytes(second), new(7, 7, 7));
        edits.Undo(); edits.Undo(); Assert.True(edits.IsDirty); // History spans both saves.
    }

    [Fact]
    public async Task ResourceDocumentMayEditAnotherReaderAfterCoordinateEdits()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        var resolver = fixture.Resolver; ResourceEditOwnership ownership = new(); Guid worldOwner = Guid.NewGuid(), readerOwner = Guid.NewGuid();
        var edits = await PickupPlacementEditSession.LoadAsync(fixture.World.Path, resolver, Token);
        Wire(edits, resolver, ownership, worldOwner);
        Assert.True(edits.MoveTo(Record(edits, fixture.ReaderPath).Source, new(1, 2, 3)));
        foreach (var (path, owner) in new[] { (second, readerOwner), (fixture.ReaderPath, Guid.NewGuid()) })
        {
            var doc = await FormatRegistry.Default.OpenAsync(path, Token); var resources = new ResourceEditSession(doc);
            resources.BeforeEdit += () => ownership.Acquire(owner, "reader", [path, resources.TargetPath]);
            resources.Changed += () => resolver.SetWorkspaceSnapshots(owner, [resources.Current.Document]);
            if (path == second) { await SetHeading(resources, 15); await resources.SaveAsync(token: Token); }
            else await Assert.ThrowsAsync<InvalidOperationException>(() => SetHeading(resources, 15)); // The map owns readerm1.
        }
        Assert.Equal(fixture.ReaderPath, Assert.Single(edits.WorkingArchives(Token)).Path, ignoreCase: true);
        Assert.False(edits.HasExternalChanges());
        Assert.Empty((await edits.SaveAsync(token: Token)).Errors);
        AssertOnlyPositionChanged(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath), new(1, 2, 3));
    }

    private static Func<string, ZbdDocument?> Published(AssetResolver resolver, Guid owner) => path => resolver.WorkspaceSnapshot(path, owner);
    // Mirrors DocumentModel: claim changed archives only, after checking their served baseline.
    private static void Wire(PickupPlacementEditSession edits, AssetResolver resolver, ResourceEditOwnership ownership, Guid owner)
    {
        edits.BeforeEdit += archives =>
        {
            if (edits.HasStaleBaselines(Published(resolver, owner), archives)) throw new InvalidOperationException("Refresh the map before editing this archive.");
            ownership.Acquire(owner, "map", archives.Concat(archives.Select(edits.TargetPath)));
        };
        edits.Changed += () => resolver.SetWorkspaceSnapshots(owner, edits.WorkingArchives(Token));
    }
    private static MissionCoordinateRecord Record(PickupPlacementEditSession edits, string archive) =>
        edits.OtherCoordinates.Single(c => c.Source.ArchivePath.Equals(Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase));
    private static async Task SetHeading(ResourceEditSession resources, float heading)
    {
        var member = resources.Current.Members[0]; var node = resources.Tree(member, Token).Children[1].Children[2];
        resources.Accept(await resources.PrepareZrdAsync(member.Id, node.Id, "set", value: heading.ToString("R", System.Globalization.CultureInfo.InvariantCulture), token: Token));
    }
    private static void AssertOnlyPositionChanged(byte[] before, byte[] after, Vector3 expected)
    {
        Assert.Equal(before.Length, after.Length);
        var tree = ZrdDecoder.Read(Member(after, 0), Token);
        var position = tree.Children[1].Children[1].Children.Select(n => BitConverter.UInt32BitsToSingle(n.Bits)).ToArray();
        Assert.Equal(new[] { expected.X, expected.Y, expected.Z }, position);
        int changed = Enumerable.Range(0, before.Length).Count(i => before[i] != after[i]);
        Assert.InRange(changed, 1, 3 * 8); // Only the three typed position scalars.
    }
    private static byte[] Rewritten(byte[] reader, float heading)
    {
        var tree = ZrdDecoder.Read(Member(reader, 0), Token); var rows = tree.Children.ToArray(); var data = rows[1].Children.ToArray();
        data[2] = data[2] with { Bits = BitConverter.SingleToUInt32Bits(heading) }; rows[1] = rows[1] with { Children = data };
        return Archive(("aiv.zrd", ZrdWriter.Write(tree with { Children = rows })));
    }
    [Fact]
    public async Task RememberedMissionThatCannotBeReadKeepsItsSelectionForLaterRecovery()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        fixture.Resolver.SelectMission(fixture.World.Path, second);
        // A transient lock is not evidence that the authored mission disappeared.
        using (new FileStream(second, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
            Assert.Equal(fixture.ReaderPath, mission.Layout.MissionArchive, ignoreCase: true);
            Assert.Null(mission.Layout.UnavailableMission);
            Assert.Contains(mission.Diagnostics, d => d.Contains("readerm2.zbd", StringComparison.Ordinal) && d.Contains("could not be read", StringComparison.Ordinal));
            Assert.Equal(second, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
        }
        var recovered = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Equal(second, recovered.Layout.MissionArchive, ignoreCase: true);
    }

    [Fact]
    public async Task PartiallyParsedMissionReadersAreReportedAndNeverOffered()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        string damaged = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(damaged, WithBrokenMember(fixture.ReaderBytes));
        var parsed = await fixture.Resolver.OpenCachedAsync(damaged, Token);
        Assert.Contains(parsed.Assets, a => a.Kind == AssetKind.Zrd && a.Name == "aiv.zrd"); // The typed AIV still decodes before the damaged member.
        Assert.Contains(parsed.Diagnostics, d => d.Severity == "Error");
        var catalog = await MissionSceneLoader.Mw3MissionCatalogAsync(fixture.World.Path, fixture.Resolver, Token);
        Assert.Equal(fixture.ReaderPath, Assert.Single(catalog.Missions).Archive, ignoreCase: true);
        Assert.Contains(catalog.Diagnostics, d => d.Contains("readerm2.zbd", StringComparison.Ordinal) && d.Contains("broken.zrd", StringComparison.Ordinal));
        Assert.Contains(Path.GetFullPath(damaged), catalog.Unreadable);
        // A remembered damaged reader is shown through the visible fallback, never as a seemingly valid preview.
        fixture.Resolver.SelectMission(fixture.World.Path, damaged);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token);
        Assert.Equal(fixture.ReaderPath, mission.Layout.MissionArchive, ignoreCase: true);
        Assert.Contains(mission.Diagnostics, d => d.Contains("readerm2.zbd", StringComparison.Ordinal) && d.Contains("broken.zrd", StringComparison.Ordinal));
        // An explicit request for it fails instead of loading another reader.
        await Assert.ThrowsAsync<InvalidDataException>(() => MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token, mission: damaged, exactMission: true));
    }
    [Fact]
    public async Task ExactAnimationMissionRequestsNeverLoadAFallbackReader()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        fixture.Resolver.SetWorkspaceSnapshots(Guid.NewGuid(), [fixture.World]);
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new Recoil.Zbd.Core.Animation.AnimationPackage { Prefix = prefix, Tail = [] };
        string animation = Path.Combine(fixture.Folder, "anim.zbd"), vanished = Path.Combine(fixture.Folder, "readerm2.zbd");
        var context = await Recoil.Zbd.Core.Animation.AnimationPreviewContext.LoadAsync(package, animation, fixture.Resolver, fixture.World.Path, Token, exactMission: fixture.ReaderPath);
        Assert.Equal(fixture.ReaderPath, context.Mission!.Layout.MissionArchive, ignoreCase: true);
        // The requested reader disappeared after the picker listed it: fail before publishing another reader.
        fixture.Resolver.SelectMission(fixture.World.Path, vanished);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => Recoil.Zbd.Core.Animation.AnimationPreviewContext.LoadAsync(package, animation, fixture.Resolver, fixture.World.Path, Token, exactMission: vanished));
        Assert.Contains("readerm2.zbd", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.WithDifficultyAsync(fixture.Resolver, MissionDifficulty.Medium, Token, vanished));
        // A remembered (non-exact) selection still falls back visibly.
        var fallback = await Recoil.Zbd.Core.Animation.AnimationPreviewContext.LoadAsync(package, animation, fixture.Resolver, fixture.World.Path, Token);
        Assert.Equal(fixture.ReaderPath, fallback.Mission!.Layout.MissionArchive, ignoreCase: true);
        Assert.Equal(vanished, fallback.Mission.Layout.UnavailableMission, ignoreCase: true);
    }
    private static byte[] WithBrokenMember(byte[] archive)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(archive.Length - 4)), table = archive.Length - 8 - count * 148;
        byte[] record = new byte[148]; BinaryPrimitives.WriteInt32LittleEndian(record, 0x7FFF_0000); BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(4), 16);
        Encoding.Latin1.GetBytes("broken.zrd").CopyTo(record, 8);
        byte[] trailer = new byte[8]; BinaryPrimitives.WriteInt32LittleEndian(trailer, 1); BinaryPrimitives.WriteInt32LittleEndian(trailer.AsSpan(4), count + 1);
        return [.. archive.AsSpan(0, archive.Length - 8), .. record, .. trailer];
    }

    [Fact]
    public async Task UnreadableArchivesDoNotHideTheMotionLibrary()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-motion-library-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string library = Path.Combine(root, "mechlib.zbd"), locked = Path.Combine(root, "other.zbd");
            File.WriteAllBytes(library, Mw3ReplacementConventionTests.MechLibrary(true)); File.WriteAllBytes(locked, Archive(("fx.zrd", Zrd())));
            using AssetResolver resolver = new(root); List<string> skipped = [];
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var found = await MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver, Token, skipped);
                Assert.Equal(library, found.Path, ignoreCase: true);
                Assert.Contains(skipped, s => s.StartsWith("other.zbd:", StringComparison.Ordinal));
                File.Delete(library); await resolver.InvalidateAsync([library], Token);
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver, Token));
                Assert.Contains("could not be read", error.Message);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    private static byte[] Zrd() => ZrdWriter.Write(ZrdNode.Create(ZrdKind.Array) with { Children = [ZrdNode.Create(ZrdKind.Int) with { Bits = 1 }] });
    private static byte[] Member(byte[] archive, int index)
    {
        int count = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(archive.Length - 4)), table = archive.Length - 8 - count * 148;
        int offset = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(table + index * 148)), length = BinaryPrimitives.ReadInt32LittleEndian(archive.AsSpan(table + index * 148 + 4));
        return archive.AsSpan(offset, length).ToArray();
    }
    private static byte[] Archive(params (string Name, byte[] Data)[] members)
    {
        using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream, Encoding.Latin1);
        List<int> offsets = [];
        foreach (var member in members) { offsets.Add((int)stream.Position); writer.Write(member.Data); }
        for (int i = 0; i < members.Length; i++)
        {
            writer.Write(offsets[i]); writer.Write(members[i].Data.Length);
            byte[] entry = new byte[140]; Encoding.Latin1.GetBytes(members[i].Name).CopyTo(entry, 0); writer.Write(entry);
        }
        writer.Write(1); writer.Write(members.Length); writer.Flush(); return stream.ToArray();
    }
}
