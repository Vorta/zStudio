using System.IO;

namespace Recoil.Zbd.Desktop;

/// <summary>A textual transaction. Failed validation never becomes the committed baseline.</summary>
internal sealed class FieldDraft(string value, Action<string> commit, Func<string, Task>? asyncCommit = null)
{
    private Task<bool>? work;
    public bool IsAsync => asyncCommit != null;
    public bool IsCommitting => work is { IsCompleted: false };
    public string Committed { get; private set; } = value;
    public string Text { get; set; } = value;
    public string? Error { get; private set; }
    public bool IsPending => Text != Committed;
    public bool Commit()
    {
        if (!IsPending) { Error = null; return true; }
        if (IsAsync) return false;
        try { commit(Text); Committed = Text; Error = null; return true; }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException or ArgumentException or InvalidOperationException)
        { Error = ex.Message; return false; }
    }
    public Task<bool> CommitAsync()
    {
        if (!IsAsync) return Task.FromResult(Commit());
        if (IsCommitting) return work!;
        return work = Run();
        async Task<bool> Run()
        {
            if (!IsPending) { Error = null; return true; }
            string value = Text;
            try { await asyncCommit!(value); Committed = value; Error = null; return true; }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException or FormatException or OverflowException or ArgumentException or InvalidOperationException or OperationCanceledException or System.Text.Json.JsonException or Recoil.Zbd.Automation.StudioCommandException)
            { Error = ex.Message; return false; }
        }
    }
    public void Discard() { Text = Committed; Error = null; }
    public void Refresh(string value)
    {
        if (IsPending) return;
        Text = Committed = value; Error = null;
    }
}
