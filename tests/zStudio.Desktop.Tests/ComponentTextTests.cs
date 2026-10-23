using Xunit;

namespace zStudio.Desktop.Tests;

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
    }
}
