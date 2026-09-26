using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

public partial class FieldEditor
{
    internal sealed record AutomationChoice(int Value, string Label);
    private sealed record AutomationField(string Id, string Scope, string Label, string Kind, Func<string> Read, Action<string>? Write, string[]? Components, string? Hint, AutomationChoice[]? Choices);
    private sealed record AutomationAction(string Id, string Scope, string Label, Action Run);
    private readonly List<AutomationField> automationFields = [];
    private readonly List<AutomationAction> automationActions = [];
    protected void ClearAutomationFields(string scope)
    { automationFields.RemoveAll(f => f.Scope == scope); automationActions.RemoveAll(f => f.Scope == scope); }
    private void AddAutomationField(string label, string kind, Func<string> read, Action<string>? write, string[]? components = null, string? hint = null, AutomationChoice[]? choices = null)
        => automationFields.Add(new(inputScope + "/" + automationFields.Count(f => f.Scope == inputScope), inputScope, label, kind, read, write, components, hint, choices));
    private void AddAutomationAction(string label, Action action)
        => automationActions.Add(new(inputScope + "/action/" + automationActions.Count(f => f.Scope == inputScope), inputScope, label, action));
    internal object DescribeAutomationFields() => new
    {
        fields = automationFields.Select(f => new { f.Id, f.Label, f.Kind, value = f.Read(), readOnly = f.Write == null, f.Components, f.Hint, f.Choices }).ToArray(),
        actions = automationActions.Select(a => new { a.Id, a.Label }).ToArray()
    };
    internal void WriteAutomationField(string id, string value) => RunAutomationEdit(() =>
    {
        var field = automationFields.SingleOrDefault(f => f.Id == id) ?? throw new StudioCommandException("unknown_field", "Read the current field list.");
        if (field.Write == null) throw new StudioCommandException("read_only", field.Label + " is read-only.");
        field.Write(value);
    });
    internal void InvokeAutomationAction(string id) => RunAutomationEdit(() =>
        (automationActions.SingleOrDefault(a => a.Id == id) ?? throw new StudioCommandException("unknown_action", "Read the current action list.")).Run());
    private void RunAutomationEdit(Action edit)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve unfinished input first.");
        committingDraft = true;
        try { edit(); } finally { committingDraft = false; RefreshProperties(); }
    }
    internal virtual string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(draftInputs.Select((d, i) => new { i, d.Scope, d.Draft.Committed, d.Draft.Text })))));
    internal virtual object DescribeDrafts() => new { token = DraftToken, fields = draftInputs.Select((d, i) => new { id = i, d.Scope, original = d.Draft.Committed, text = d.Draft.Text, pending = d.Draft.IsPending, error = d.Draft.Error }).Where(d => d.pending).ToArray() };
    internal virtual void ResolveAutomationDrafts(string expectedToken, bool apply)
    {
        if (expectedToken != DraftToken) throw new StudioCommandException("draft_conflict", "Drafts changed. Read them again.");
        foreach (var input in draftInputs.Where(d => d.Draft.IsPending).ToArray())
        {
            if (apply) { if (!CommitInput(input)) throw new StudioCommandException("invalid_draft", input.Draft.Error ?? "Invalid input; draft retained."); }
            else { input.Draft.Discard(); input.Display(); }
        }
        RefreshProperties();
    }
}
