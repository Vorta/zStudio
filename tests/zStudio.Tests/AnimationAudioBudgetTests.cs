using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Theory]
    [InlineData((byte)19)]
    [InlineData((byte)24)]
    public void AudioMissingChildLookupAllocationsGrowWithEntriesNotTheirProduct(byte type)
    {
        AnimationPackage Package(int count)
        {
            AnimationPackage package = new() { Prefix = [], Tail = [] };
            for (int i = 0; i < count; i++)
            {
                AnimationEntry entry = new(new byte[308], i, -1);
                entry.SetText(0, $"anim{i}"); package.Entries.Add(entry);
            }
            for (int i = 0; i < 1000; i++) package.Entries[0].Primary.Events.Add(Child(type, "missing", 0));
            return package;
        }
        var small = Package(200); var large = Package(2000);
        _ = AnimationAudioDependencies.Collect(small, 0, TestContext.Current.CancellationToken);
        long Measure(AnimationPackage package)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            var result = AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Assert.Empty(result.Names); Assert.NotEmpty(result.Diagnostics);
            return allocated;
        }
        long smallBytes = Measure(small), largeBytes = Measure(large);
        Assert.True(largeBytes < smallBytes + 2_000_000, $"200 entries allocated {smallBytes:N0}; 2,000 entries allocated {largeBytes:N0} bytes.");
    }

    [Fact]
    public void AudioDiagnosticExhaustionDisclosesOmissionAndKeepsLateReachableSounds()
    {
        var package = Fixture(); var root = package.Entries[0];
        for (int i = 0; i < 5000; i++)
        {
            var ev = AnimationCatalog.Create(1); ev.SetShort(12, (short)i); root.Primary.Events.Add(ev);
        }
        root.Primary.Events.Add(SoundNode("late-root"));
        var child = root.Clone(TestContext.Current.CancellationToken); child.SetText(0, "late-child");
        child.Primary.Events.Clear(); child.Primary.Events.Add(SoundNode("late-child-sound"));
        child.Primary.Events.Add(Child(19, root.Name, 0)); // Closure remains finite after diagnostics are full.
        package.Entries.Add(child); root.Primary.Events.Add(Child(24, child.Name, 1));
        var result = AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "late-child-sound", "late-root" }, result.Names.Order());
        Assert.InRange(result.Diagnostics.Count, 2, BoundedDiagnostics.MaximumMessages);
        Assert.All(result.Diagnostics, message => Assert.InRange(message.Length, 1, BoundedDiagnostics.MaximumMessageCharacters));
        Assert.InRange(result.Diagnostics.Sum(message => message.Length), 1, BoundedDiagnostics.MaximumRetainedCharacters);
        Assert.Equal(BoundedDiagnostics.OmissionNotice, result.Diagnostics[^1]);
        Assert.Single(result.Diagnostics, message => message == BoundedDiagnostics.OmissionNotice);
    }

    [Fact]
    public void ChildLookupPreservesCacheAndOrdinalFirstMatchIncludingEmptyNames()
    {
        AnimationPackage package = new() { Prefix = [], Tail = [] };
        foreach (string name in new[] { "", "Duplicate", "Duplicate", "duplicate", "éclair" })
        {
            AnimationEntry entry = new(new byte[308], 100 + package.Entries.Count, -1);
            entry.SetText(0, name); package.Entries.Add(entry);
        }
        AnimationEntryLookup lookup = new(package);
        var token = TestContext.Current.CancellationToken;
        Assert.Same(package.Entries[0], lookup.ResolveChild(Child(19, "", 0), token));
        Assert.Same(package.Entries[1], lookup.ResolveChild(Child(24, "Duplicate", -1), token));
        Assert.Same(package.Entries[3], lookup.ResolveChild(Child(19, "duplicate", 99), token));
        Assert.Same(package.Entries[4], lookup.ResolveChild(Child(24, "éclair", 0), token));
        Assert.Same(package.Entries[2], lookup.ResolveChild(Child(19, "unrelated", 2), token));
        Assert.Null(lookup.ResolveChild(Child(24, "DUPLICATE", 0), token));
    }

    [Fact]
    public void ResetAndNewAudioCollectionUseEditedEntryNames()
    {
        var package = Fixture(); var root = package.Entries[0];
        foreach (string name in new[] { "target", "other" })
        {
            var child = root.Clone(TestContext.Current.CancellationToken); child.SetText(0, name);
            child.Sequences[0].Events.Add(SoundNode(name + "-sound")); package.Entries.Add(child);
        }
        root.Sequences[0].Events.Add(Child(24, "target", 0));
        var player = new AnimationPlayer(Context(package), 0);
        Assert.Contains("Unresolved sound: target-sound", player.EvaluateForTest(.1).Diagnostics);
        Assert.Equal(["target-sound"], AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Names);

        package.Entries[1].SetText(0, "old-target"); package.Entries[2].SetText(0, "target");
        player.Reset();
        var after = player.EvaluateForTest(.1);
        Assert.Contains("Unresolved sound: other-sound", after.Diagnostics);
        Assert.DoesNotContain("Unresolved sound: target-sound", after.Diagnostics);
        Assert.Equal(["other-sound"], AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Names);
    }
}
