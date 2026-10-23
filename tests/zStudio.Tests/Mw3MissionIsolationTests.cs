using System.Buffers.Binary;
using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed class Mw3MissionIsolationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task RepeatedActorMetadataRefusesTheWholeLoadBeforeItsSmallAllowanceIsExceeded()
    {
        using var fixture = new Mw3MissionFixture(Enumerable.Repeat("actor", 8).ToArray());
        var original = fixture.World.Scene!;
        int[] parents = Enumerable.Repeat(0, 128).ToArray();
        original.Nodes[0] = original.Nodes[0] with { Children = Enumerable.Repeat(1, 128).ToArray() };
        original.Nodes[1] = original.Nodes[1] with { Parents = parents };
        original.Nodes[1].Metadata["parent_indices"] = JsonData.Integers(parents, Token);
        string metadata = original.Nodes[1].Metadata.ToJsonString(), data = original.Nodes[1].Data.ToJsonString();
        const long allowance = 96 * 1024;
        MissionPlacementBudget budget = new(Token, maximumCloneBytes: allowance);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => MissionSceneLoader.LoadMw3WithBudgetAsync(
            fixture.World, fixture.Resolver, null, false, budget, Token));
        Assert.Contains("placements copy more than 98,304 bytes", error.Message, StringComparison.Ordinal);
        Assert.True(budget.Exhausted); Assert.InRange(budget.CloneBytes, 1, allowance);
        Assert.Equal(2, original.Nodes.Count); Assert.Equal(parents, original.Nodes[1].Parents);
        Assert.Equal(metadata, original.Nodes[1].Metadata.ToJsonString()); Assert.Equal(data, original.Nodes[1].Data.ToJsonString());

        // The identical small input succeeds with a fresh ordinary allowance, retaining each placement's provenance.
        // Discovery and loading revisit the same archive, so one document's raw allowance is sufficient.
        var mission = await MissionSceneLoader.LoadMw3WithBudgetAsync(fixture.World, fixture.Resolver, null, false,
            new MissionPlacementBudget(Token), Token, new AssetReadBudget(maximumBytes: fixture.ReaderBytes.Length));
        Assert.Equal(8, mission.Actors.Count); Assert.Equal(9, mission.Scene.Nodes.Count);
        Assert.Equal(Enumerable.Range(1, 8), mission.Actors.Select(a => a.Root));
        Assert.All(mission.Actors, a => Assert.Equal(1, a.SourceRoot));
        Assert.Equal(Enumerable.Range(0, 8), mission.Actors.Select(a => a.CoordinateSource!.RecordIndex));
        Assert.Equal(metadata, original.Nodes[1].Metadata.ToJsonString()); Assert.Equal(data, original.Nodes[1].Data.ToJsonString());
        Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));

        // Another valid reader exceeds that allowance: an incomplete catalog must not publish a fallback.
        File.WriteAllBytes(Path.Combine(fixture.Folder, "readerm2.zbd"), fixture.ReaderBytes);
        AssetReadBudget reads = new(maximumBytes: fixture.ReaderBytes.Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => MissionSceneLoader.LoadMw3WithBudgetAsync(
            fixture.World, fixture.Resolver, null, false, new MissionPlacementBudget(Token), Token, reads));
        Assert.True(reads.Exhausted);
        Assert.Equal(fixture.ReaderPath, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
    }

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
    public async Task WorldsWithoutMissionReadersReportNoDifficultyLayout()
    {
        using var fixture = new Mw3MissionFixture("actor_01");
        File.Delete(fixture.ReaderPath);
        // The shared RECOIL preference neither selects nor labels an MW3 layout.
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: Token, difficulty: MissionDifficulty.Hard);
        Assert.Null(mission.Layout.MissionArchive); Assert.False(mission.Layout.DifficultyApplies); Assert.Empty(mission.Actors);
        Assert.Equal("Stored world layout", mission.Layout.Label);
        Assert.DoesNotContain("Medium", mission.Layout.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("aiv.zrd", mission.Layout.Description, StringComparison.Ordinal);
        Assert.True(MissionLayoutSelection.For(MissionDifficulty.Hard).DifficultyApplies);
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
        File.WriteAllBytes(fixture.World.Path, GameZBoundsTests.World(27, 1, 0)); // The context opens the world from disk.
        byte[] prefix = new byte[80]; BinaryPrimitives.WriteUInt32LittleEndian(prefix, 0x08170616); BinaryPrimitives.WriteInt32LittleEndian(prefix.AsSpan(4), 39);
        var package = new zStudio.Core.Animation.AnimationPackage { Prefix = prefix, Tail = [] };
        string animation = Path.Combine(fixture.Folder, "anim.zbd"), vanished = Path.Combine(fixture.Folder, "readerm2.zbd");
        var context = await zStudio.Core.Animation.AnimationPreviewContext.LoadAsync(package, animation, fixture.Resolver, fixture.World.Path, Token, exactMission: fixture.ReaderPath);
        Assert.Equal(fixture.ReaderPath, context.Mission!.Layout.MissionArchive, ignoreCase: true);
        // The requested reader disappeared after the picker listed it: fail before publishing another reader.
        fixture.Resolver.SelectMission(fixture.World.Path, vanished);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => zStudio.Core.Animation.AnimationPreviewContext.LoadAsync(package, animation, fixture.Resolver, fixture.World.Path, Token, exactMission: vanished));
        Assert.Contains("readerm2.zbd", error.Message);
        await Assert.ThrowsAsync<InvalidDataException>(() => context.WithDifficultyAsync(fixture.Resolver, MissionDifficulty.Medium, Token, vanished));
        // A remembered (non-exact) selection still falls back visibly.
        var fallback = await zStudio.Core.Animation.AnimationPreviewContext.LoadAsync(package, animation, fixture.Resolver, fixture.World.Path, Token);
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
            File.WriteAllBytes(library, MechLibrary()); File.WriteAllBytes(locked, Archive(("fx.zrd", Zrd())));
            using AssetResolver resolver = new(root); List<string> skipped = [];
            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var found = await MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver, Token, skipped);
                Assert.Equal(library, found.Path, ignoreCase: true);
                Assert.Contains(skipped, s => s.StartsWith("other.zbd:", StringComparison.Ordinal));

                // A warm library is still input to this operation; exhaustion cannot be swallowed as an unreadable file.
                AssetReadBudget reads = new(maximumBytes: 0);
                var limited = await Assert.ThrowsAsync<InvalidDataException>(() => MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver,
                    reads, new CompiledInventory(Token), Token));
                Assert.True(reads.Exhausted); Assert.Contains("byte allowance", limited.Message, StringComparison.Ordinal);
                await Assert.ThrowsAsync<CompiledInventoryCapacityException>(() => MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver,
                    new AssetReadBudget(), new CompiledInventory(1024 * 1024, 1, Token), Token));
                var retried = await MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver,
                    new AssetReadBudget(), new CompiledInventory(Token), Token);
                Assert.Equal(library, retried.Path, ignoreCase: true);

                string second = Path.Combine(root, "second.zbd"); File.WriteAllBytes(second, File.ReadAllBytes(library));
                var ambiguous = await Assert.ThrowsAsync<InvalidDataException>(() => MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver, Token));
                Assert.Contains("Multiple mech libraries", ambiguous.Message, StringComparison.Ordinal);
                File.Delete(second);
                File.Delete(library);
                var error = await Assert.ThrowsAsync<InvalidDataException>(() => MotionLibrary.LoadAsync(Path.Combine(root, "motion.zbd"), resolver, Token));
                Assert.Contains("could not be read", error.Message);
            }
        }
        finally { Directory.Delete(root, true); }
    }
    /// <summary>Version-27 mech library archive: version, format, one textured material and one single-node member with one polygon.</summary>
    private static byte[] MechLibrary()
    {
        using MemoryStream s = new(); using BinaryWriter w = new(s); List<(string Name, long Offset, long Length)> members = [];
        void Member(string name, Action write) { long start = s.Position; write(); members.Add((name, start, s.Position - start)); }
        Member("version", () => w.Write(27)); Member("format", () => w.Write(1));
        Member("materials", () =>
        {
            w.Write(1); byte[] m = new byte[40]; m[0] = 255; m[1] = 0x11; BinaryPrimitives.WriteInt32LittleEndian(m.AsSpan(16), 0x0BADF00D); w.Write(m);
            w.Write(4); w.Write("mtex"u8);
        });
        Member("mech_test.flt", () =>
        {
            byte[] node = new byte[208]; Encoding.ASCII.GetBytes("root").CopyTo(node, 0);
            BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(36), 0x300); BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(52), 5); BinaryPrimitives.WriteInt32LittleEndian(node.AsSpan(60), 0x4000);
            for (int box = 116; box <= 140; box += 24) for (int k = 0; k < 6; k++) BinaryPrimitives.WriteSingleLittleEndian(node.AsSpan(box + k * 4), k < 3 ? -3 : 3);
            w.Write(node); byte[] object3d = new byte[144]; BinaryPrimitives.WriteInt32LittleEndian(object3d, 8); w.Write(object3d);
            byte[] header = new byte[92]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), 1); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), 3);
            BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(52), 123); BinaryPrimitives.WriteSingleLittleEndian(header.AsSpan(84), 2); w.Write(header);
            foreach (var v in ModelFixture.Positions) { w.Write(v.X); w.Write(v.Y); w.Write(v.Z); }
            w.Write(3); w.Write(4u); w.Write(111); w.Write(0); w.Write(222); w.Write(0); w.Write(0x1234ABCDu); w.Write(0); w.Write(0xFFFF0201u);
            w.Write(0); w.Write(1); w.Write(2); foreach (var uv in ModelFixture.Uvs) { w.Write(uv.X); w.Write(uv.Y); }
        });
        foreach (var (name, offset, length) in members)
        {
            byte[] record = new byte[148]; BinaryPrimitives.WriteUInt32LittleEndian(record, (uint)offset); BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4), (uint)length);
            Encoding.Latin1.GetBytes(name).CopyTo(record, 8); w.Write(record);
        }
        w.Write(1); w.Write(members.Count);
        return s.ToArray();
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
