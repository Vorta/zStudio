using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public void CollisionAudioDependenciesValidateFlagIndexAndRecordBounds()
    {
        var package = Fixture(); var entry = package.Entries[0];
        entry.References[4].Add(new(new byte[36]));
        var sample = new AnimationRecord(new byte[36]); sample.SetText(0, "impact"); entry.References[4].Add(sample);
        var ev = AnimationCatalog.Create(10); ev.SetInt(12, 0x1001); ev.SetShort(242, 1); entry.Primary.Events.Add(ev);
        Assert.Contains("impact", AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Names);
        ev.SetInt(12, 1); Assert.Empty(AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Names);
        ev.SetInt(12, 0x1001); ev.SetShort(242, 100);
        Assert.Single(AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Diagnostics);
        entry.Primary.Events.Add(new AnimationEvent([10]));
        Assert.Equal(2, AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Diagnostics.Count);
    }
    [Fact]
    public void AudioDependenciesIncludeCleanupBranchesAndRecursiveChildrenWithoutEditingBytes()
    {
        var package = Fixture();
        var root = package.Entries[0];
        var child = root.Clone(); child.SetText(0, "child"); child.Primary.Events.Clear(); child.Sequences[0].Events.Clear();
        package.Entries.Add(child);
        var sample = new AnimationRecord(new byte[36]); sample.SetText(0, "sample"); root.References[4].Add(sample);
        var play = AnimationCatalog.Create(1); play.SetShort(12, 0); root.Sequences[0].Events.Add(play);
        root.Primary.Events.Add(SoundNode("cleanup"));
        root.Sequences[0].Events.Add(AnimationCatalog.Create(31)); // Both possible branches must preload.
        root.Sequences[0].Events.Add(SoundNode("branch"));
        root.Sequences[0].Events.Add(Child(19, "wrong-name", 1)); // The stored entry index wins.
        child.Sequences[0].Events.Add(SoundNode("child-sound"));
        child.Sequences[0].Events.Add(Child(24, root.Name, 0)); // Cycle must terminate.
        byte[] before = AnimationWriter.Write(package, TestContext.Current.CancellationToken);
        var result = AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "branch", "child-sound", "cleanup", "sample" }, result.Names.Order());
        Assert.Empty(result.Diagnostics);
        Assert.Equal(before, AnimationWriter.Write(package, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void AudioDependenciesMatchDuplicateNameFallbackAndIgnoreStopOnlySounds()
    {
        var package = Fixture();
        foreach (string sound in new[] { "first", "second" })
        {
            var entry = package.Entries[0].Clone(); entry.SetText(0, "duplicate");
            entry.Sequences[0].Events.Add(SoundNode(sound)); package.Entries.Add(entry);
        }
        var events = package.Entries[0].Sequences[0].Events;
        events.Add(Child(24, "duplicate", -1));
        var stop = SoundNode("stop-only"); stop.SetInt(52, 0); events.Add(stop);
        Assert.Equal(["first"], AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Names);
        events.Add(Child(19, "duplicate", 2));
        Assert.Equal(new[] { "first", "second" }, AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken).Names.Order());
    }

    [Fact]
    public void AudioDependencyErrorsAreBoundedAndDoNotHideValidSounds()
    {
        var package = Fixture(); var events = package.Entries[0].Sequences[0].Events;
        events.Add(new AnimationEvent([1])); events.Add(new AnimationEvent([]));
        var sample = AnimationCatalog.Create(1); sample.SetShort(12, -1); events.Add(sample);
        events.Add(Child(19, "absent", -1)); events.Add(SoundNode("valid"));
        var result = AnimationAudioDependencies.Collect(package, 0, TestContext.Current.CancellationToken);
        Assert.Equal(["valid"], result.Names); Assert.Equal(3, result.Diagnostics.Count);
        Assert.Throws<OperationCanceledException>(() => AnimationAudioDependencies.Collect(package, 0, new CancellationToken(true)));
        Assert.Throws<ArgumentOutOfRangeException>(() => AnimationAudioDependencies.Collect(package, 99, TestContext.Current.CancellationToken));
    }

    private static AnimationEvent SoundNode(string name)
    { var ev = AnimationCatalog.Create(2); ev.SetText(12, name); ev.SetInt(52, 1); return ev; }
    private static AnimationEvent Child(byte type, string name, short index)
    { var ev = AnimationCatalog.Create(type); ev.SetText(type == 19 ? 16 : 12, name, type == 19 ? 32 : 20); ev.SetShort(48, index); return ev; }
}
