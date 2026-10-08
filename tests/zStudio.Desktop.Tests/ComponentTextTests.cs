using System.IO;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class ComponentTextTests
{
    [Theory]
    [InlineData(", ")]
    [InlineData("|")]
    public void ExcessComponentsDoNotAllocateAllTokensAndDisplayRetainsTheCompleteDraft(string separator)
    {
        string text = string.Concat(Enumerable.Repeat(separator == "|" ? "0|" : "0 ", 500_000));
        _ = ComponentText.TrySplit("1, 2, 3", ", ", 3, out _);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.False(ComponentText.TrySplit(text, separator, 3, out var parts));
        var display = ComponentText.Display(text, separator, 3);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 16 * 1024);
        Assert.Empty(parts);
        Assert.Same(text, Assert.Single(display));
    }

    [Fact]
    public void NumericDraftRefusalRetainsTextAndOriginalUntilExplicitDiscard()
    {
        string original = "1, 2, 3";
        string received = original;
        FieldDraft draft = new(original, text => { ComponentText.CheckNumeric(text); received = text; });
        string huge = new('0', 1 << 20); draft.Text = huge;
        Assert.False(draft.Commit());
        Assert.Equal(original, draft.Committed);
        Assert.Same(huge, draft.Text);
        Assert.Same(huge, Assert.Single(ComponentText.Display(draft.Text, ", ", 3)));
        Assert.Equal(original, received);
        draft.Discard(); Assert.Equal(original, draft.Text);
    }

    [Fact]
    public void NonnumericPipeComponentsKeepFullValuesAndEmptySlots()
    {
        string name = new('n', 5000);
        Assert.True(ComponentText.TrySplit(name + "||last", "|", 3, out var fields));
        Assert.Equal([name, "", "last"], fields);
        Assert.Equal(["1", "2", "3"], ComponentText.Require(" ,1\t2, 3, ", ", ", 3));
        Assert.Throws<InvalidDataException>(() => ComponentText.Require("1|2|3|4", "|", 3));
    }

    [Fact]
    public void QuaternionAndMotionNumericParsingRetainSeparatorsAndRejectBeforeMaterialization()
    {
        Assert.Equal([1f, 0f, 0f, 0f], ComponentText.ParseFinite("1, 0 0, 0", 4));
        Assert.Equal([1f, 2f, 3f], ComponentText.ParseFinite("\u00A01\u00A0, 2,3", 3, commaOnly: true));
        foreach (string invalid in new[] { "1,2,3,4,5", "1,2,NaN,4", "1\t2\t3\t4" })
            Assert.Throws<InvalidDataException>(() => ComponentText.ParseFinite(invalid, 4));
        Assert.Throws<InvalidDataException>(() => ComponentText.ParseFinite("1,,3", 3, commaOnly: true));
        string huge = string.Concat(Enumerable.Repeat("0 ", 500_000));
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => ComponentText.ParseFinite(huge, 4));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 32 * 1024);
    }
}
