using System.IO;
using System.Numerics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Desktop;
using Xunit;

namespace zStudio.Desktop.Tests;

// Invoked from the existing single-Application STA fixture; never creates a competing dispatcher or changes clipboard.
internal static class ComponentFieldChecks
{
    internal static void Run()
    {
        AnimationRecord record = new(new byte[12]);
        new AnimationField("", 0, AnimationFieldKind.Vector).Write(record, "1, 2, 3");
        var editor = new Editor("1, 2, 3", text => new AnimationField("", 0, AnimationFieldKind.Vector).Write(record, text));
        var boxes = Descendants(editor.Panel).OfType<ValueTextBox>().ToArray(); Assert.Equal(3, boxes.Length);
        string huge = string.Concat(Enumerable.Repeat("0 ", 500_000));
        var largePaste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, huge), false, DataFormats.UnicodeText);
        boxes[0].RaiseEvent(largePaste);
        Assert.False(largePaste.CommandCancelled); // Native single-box paste remains available; the handler does not split it.
        Assert.Equal(["1", "2", "3"], boxes.Select(b => b.Text));
        boxes[1].Text = huge; boxes[1].CaretIndex = 123; // Keep the native paste's component and caret after refusal.
        string draft = editor.DraftText;
        Assert.False(editor.CommitNow());
        Assert.Equal(draft, editor.DraftText);
        Assert.Equal(["1", huge, "3"], boxes.Select(b => b.Text));
        Assert.Equal(123, boxes[1].CaretIndex);
        Assert.Equal(new Vector3(1, 2, 3), record.Vector(0));
        editor.DiscardNow(); Assert.Equal(["1", "2", "3"], boxes.Select(b => b.Text));
        var ordinaryPaste = new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText, "4, 5, 6"), false, DataFormats.UnicodeText);
        boxes[0].RaiseEvent(ordinaryPaste);
        Assert.True(ordinaryPaste.CommandCancelled);
        Assert.Equal(["4", "5", "6"], boxes.Select(b => b.Text));
        Assert.True(editor.CommitNow()); Assert.Equal(new Vector3(4, 5, 6), record.Vector(0));
    }

private sealed class Editor : FieldEditor
    {
        internal StackPanel Panel { get; } = new();
        internal Editor(string value, Action<string> write)
        {
            Content = Panel;
            Input(Panel, "Vector", value, write, components: ["X", "Y", "Z"]);
        }
        internal string DraftText => draftInputs[0].Draft.Text;
        internal bool CommitNow() => CommitInput(draftInputs[0]);
        internal void DiscardNow() { draftInputs[0].Draft.Discard(); draftInputs[0].Display(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
