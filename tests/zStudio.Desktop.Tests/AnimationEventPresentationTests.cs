using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class AnimationEventPresentationTests
{
    [Fact]
    public void ChildNamesRespectTheirStoredWidthsAndWaitingDoesNotBecomeATimestamp()
    {
        var entry = Entry(); var child = AnimationCatalog.Create(24);
        child.SetText(12, "vtol_destruction1", 20); child.SetInt(32, 0x41414141);
        child.SetShort(46, 16); child.StartMode = 3; child.Threshold = .1f;
        byte[] before = child.Bytes.ToArray();
        var row = AnimationEventPresentation.Create(entry, child);
        Assert.Equal("Animation name: vtol_destruction1 · Wait for child to finish", row.Summary);
        Assert.Equal("After previous event ≥ 0.1 s", row.Timing);
        Assert.Contains("not duration or observed dispatch time", row.Description);
        Assert.Equal(before, child.Bytes);
        var between = AnimationCatalog.Create(19); between.SetText(16, "a_name_longer_than_twenty_chars");
        Assert.Equal("Animation name: a_name_longer_than_twenty_chars", AnimationEventPresentation.Create(entry, between).Summary);
        var stop = AnimationCatalog.Create(25); stop.SetText(12, "vtol_engines");
        Assert.Equal("Animation name: vtol_engines", AnimationEventPresentation.Create(entry, stop).Summary);
    }

    [Fact]
    public void ReferenceNamesKeepIndicesAndExposeMissingReservedAndSpecialSlots()
    {
        var entry = Entry(); entry.References[4].AddRange([Reference("reserved"), Reference("impact"), Reference("impact")]);
        var sound = AnimationCatalog.Create(1); sound.SetShort(12, 2);
        Assert.Equal("Sample: impact (#2)", AnimationEventPresentation.Create(entry, sound).Summary);
        entry.References[4][2].SetText(0, "new_impact");
        Assert.Equal("Sample: new_impact (#2)", AnimationEventPresentation.Create(entry, sound).Summary);
        sound.SetShort(12, 0); Assert.Contains("none / reserved (#0)", AnimationEventPresentation.Create(entry, sound).Summary);
        sound.SetShort(12, 50); Assert.Contains("missing reference #50", AnimationEventPresentation.Create(entry, sound).Summary);
        entry.References[4].Add(new(new byte[3])); sound.SetShort(12, 3);
        Assert.Contains("incomplete reference #3", AnimationEventPresentation.Create(entry, sound).Summary);
        var active = AnimationCatalog.Create(6); active.SetShort(16, -100);
        Assert.Contains("bound root (−100)", AnimationEventPresentation.Create(entry, active).Summary);
        active.SetShort(16, -200); Assert.Contains("activation reference", AnimationEventPresentation.Create(entry, active).Summary);
    }

    [Fact]
    public void ConditionalDurationsAndIntegerBitFieldsAreNotMisrepresented()
    {
        var entry = Entry(); var motion = AnimationCatalog.Create(10);
        motion.SetFloat(248, 10);
        Assert.DoesNotContain("Duration:", AnimationEventPresentation.Create(entry, motion).Summary);
        motion.SetInt(12, 0x400 | 0x800000);
        var row = AnimationEventPresentation.Create(entry, motion);
        Assert.Contains("Duration: 10 s", row.Summary); Assert.Contains("unknown 0x800000", row.Summary);
        var loop = AnimationCatalog.Create(30); loop.SetInt(16, 0x10003);
        Assert.Equal("Iteration limit: 3", AnimationEventPresentation.Create(entry, loop).Summary);
        loop.SetInt(16, -1); Assert.Equal("Iterations: unlimited", AnimationEventPresentation.Create(entry, loop).Summary);
        var condition = AnimationCatalog.Create(31); condition.SetInt(12, 4); condition.SetInt(20, 2);
        Assert.Contains("Effects level ≥ 2", AnimationEventPresentation.Create(entry, condition).Summary);
        condition.SetInt(12, 1); condition.SetFloat(20, .5f);
        Assert.Contains("Random threshold: 0.5", AnimationEventPresentation.Create(entry, condition).Summary);
    }

    [Fact]
    public void UnknownTruncatedAndMalformedEventsHaveSafeDescriptions()
    {
        var entry = Entry();
        Assert.Equal("Timing unavailable", AnimationEventPresentation.Create(entry, new([])).Timing);
        var unknown = new AnimationEvent(new byte[12]); unknown.Bytes[0] = 0xff;
        Assert.Contains("Unsupported", AnimationEventPresentation.Create(entry, unknown).Timing);
        foreach (var spec in AnimationCatalog.Events)
        {
            var ev = AnimationCatalog.Create(spec.Type); byte[] before = ev.Bytes.ToArray();
            Assert.NotEmpty(AnimationEventPresentation.Create(entry, ev).Description); Assert.Equal(before, ev.Bytes);
            if (spec.Size > 12) Assert.Contains("Incomplete record", AnimationEventPresentation.Create(entry, new(ev.Bytes[..(spec.Size - 1)])).Summary);
        }
        var malformed = AnimationCatalog.Create(12); malformed.SetInt(32, 7);
        Assert.Contains("Malformed keyframe data", AnimationEventPresentation.Create(entry, malformed).Summary);
        var tiny = AnimationCatalog.Create(25); tiny.Threshold = .00001f;
        Assert.DoesNotContain("≥ 0 s", AnimationEventPresentation.Create(entry, tiny).Timing);
    }

    private static AnimationEntry Entry() => new(new byte[308], 1, -1);
    private static AnimationRecord Reference(string name) { var record = new AnimationRecord(new byte[40]); record.SetText(0, name); return record; }
}
