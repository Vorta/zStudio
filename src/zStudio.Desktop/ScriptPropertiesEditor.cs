using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public sealed class ScriptPropertiesEditor : FieldEditor, IDisposable
{
    private readonly DocumentModel document;
    private readonly Func<string, string, int, Task> edit;
    private string? form;
    public Guid EntryId { get; }
    public Guid? InstructionId { get; }
    public event Action? Changed;
    private PreparedScriptEntry? Entry => document.ScriptEdits!.Package.Entries.SingleOrDefault(e => e.Id == EntryId);
    private ScriptInstruction? Instruction => Entry?.Instructions.SingleOrDefault(i => i.Id == InstructionId);
    public string TargetLabel => Entry == null ? "Deleted script" : Entry.Name + (InstructionId == null ? "" : Instruction == null ? " · deleted instruction" : " · instruction " + Entry.Instructions.ToList().FindIndex(i => i.Id == InstructionId));
    public JsonObject Json => new() { ["script"] = EntryId.ToString(), ["instruction"] = InstructionId?.ToString(), ["name"] = Entry?.Name,
        ["file_time"] = Entry?.FileTime, ["tokens"] = Instruction is { } i ? new JsonArray(i.Tokens.Take(16).Select(t => (JsonNode?)JsonValue.Create(Preview(t))).ToArray()) : null,
        ["save_destination"] = document.ContentEdits!.TargetPath(document.Path) };
    public ScriptPropertiesEditor(DocumentModel document, Guid entry, Guid? instruction, Func<string, string, int, Task> edit)
    {
        this.document = document; this.edit = edit; EntryId = entry; InstructionId = instruction;
        document.ContentEditsChanged += RefreshProperties; RefreshProperties();
    }
    private static string Preview(string value) => value.Length <= 4096 ? value : value[..4096] + "…";
    private string TokenValue(int index)
    {
        string value = Instruction?.Tokens.ElementAtOrDefault(index) ?? "";
        string text = JsonSerializer.Serialize(value.Length > 16384 ? value[..16384] : value);
        return text.Length > 16384 ? text[..16384] : text;
    }
    private static bool LargeToken(string value) => value.Length > 16384 || JsonSerializer.Serialize(value).Length > 16384;
    protected override void RefreshProperties()
    {
        if (disposed || committingDraft) return;
        var entry = Entry; var instruction = Instruction;
        string next = entry == null || InstructionId != null && instruction == null ? "missing" : InstructionId == null ? "entry" : "tokens:" + instruction!.Tokens.Count + ":" + string.Join(',', instruction.Tokens.Take(16).Select(LargeToken));
        if (next != form && !HasPendingDrafts)
        {
            form = next; draftInputs.Clear(); valueRefresh.Clear(); ClearAutomationFields("properties");
            StackPanel panel = new() { Margin = new(12) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            if (next == "missing") Label(panel, "This record was deleted. Undo restores its identity.");
            else if (InstructionId == null)
            {
                Input(panel, "Name", entry!.Name, _ => { }, getter: () => Entry?.Name ?? "", asyncCommit: value => edit("rename", value, 0));
                Input(panel, "Stored file time", entry.FileTime.ToString(CultureInfo.InvariantCulture), _ => { }, getter: () => Entry?.FileTime.ToString(CultureInfo.InvariantCulture) ?? "", asyncCommit: value => edit("timestamp", value, 0));
                Label(panel, "A newer loose source script can override the stored script in the game. Renaming does not rewrite references.");
            }
            else
            {
                Label(panel, "Tokens use JSON quotes to preserve empty strings, whitespace and escapes. Unknown commands are retained and are never executed here.");
                if (instruction!.Tokens.Count > 16) Label(panel, "This stored instruction exceeds the engine's 16-token limit. Only a read-only prefix is shown. Replace the instruction with bounded tokens to edit it.");
                for (int i = 0; i < Math.Min(16, instruction.Tokens.Count); i++)
                {
                    int index = i; bool large = instruction.Tokens.Count > 16 || LargeToken(instruction.Tokens[i]);
                    Input(panel, i == 0 ? "Command" : "Argument " + i, TokenValue(i), _ => { }, large,
                        hint: large ? "Read-only prefix; replace the instruction with bounded tokens." : "JSON-quoted Latin-1 string without NUL.",
                        getter: () => TokenValue(index), asyncCommit: value => edit("token", value, index));
                    if (i > 0 && instruction.Tokens.Count <= 16)
                    {
                        WrapPanel actions = new(); panel.Children.Add(actions);
                        AsyncButton(actions, "Remove argument " + i, () => edit("remove_argument", "", index));
                        if (i > 1) AsyncButton(actions, "Move argument " + i + " up", () => edit("argument_up", "", index));
                        if (i + 1 < instruction.Tokens.Count) AsyncButton(actions, "Move argument " + i + " down", () => edit("argument_down", "", index));
                    }
                }
                if (instruction.Tokens.Count < PreparedScriptWriter.MaximumTokens) AsyncButton(panel, "Add argument", () => edit("add_argument", "", 0));
            }
            Input(panel, "Save destination", document.ContentEdits!.TargetPath(document.Path), _ => { }, true,
                getter: () => document.ContentEdits!.TargetPath(document.Path));
        }
        else foreach (var refresh in valueRefresh.ToArray()) refresh();
        Changed?.Invoke();
    }
    public void Dispose() { if (disposed) return; disposed = true; document.ContentEditsChanged -= RefreshProperties; Content = null; draftInputs.Clear(); GC.SuppressFinalize(this); }
}
