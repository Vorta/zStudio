using System.IO;

namespace Recoil.Zbd.Desktop;

/// <summary>A textual transaction. Failed validation never becomes the committed baseline.</summary>
internal sealed class FieldDraft(string value, Action<string> commit)
{
    public string Committed { get; private set; } = value;
    public string Text { get; set; } = value;
    public string? Error { get; private set; }
    public bool IsPending => Text != Committed;
    public bool Commit()
    {
        if (!IsPending) { Error = null; return true; }
        try { commit(Text); Committed = Text; Error = null; return true; }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { Error = ex.Message; return false; }
    }
    public void Discard() { Text = Committed; Error = null; }
    public void Refresh(string value)
    {
        if (IsPending) return;
        Text = Committed = value; Error = null;
    }
}
