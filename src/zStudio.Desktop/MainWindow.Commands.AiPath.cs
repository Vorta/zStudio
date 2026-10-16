using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RegisterAiPathCommands(StudioCommands registry)
    {
        RegisterJob(registry, "ai_path", "Edit nodes in one authored AI path. Enter/exit Edit mode; replace/add/toggle a bounded selection or box-select visible nodes; begin a move/rotate/scale draft, set numeric input, then confirm one undo step or cancel. World XYZ constraints and selection-centre pivot match the GUI. Links and other fields are preserved. Source-world confirmation updates source tokens in memory, evaluates the scene and returns the accepted document/revision, exiting Edit mode. Save writes the owning ZRD sources; no archives are created. Selection and draft state are in preview_state.ai.path. Properties pins common values (mixed where different), without following later selection. No physical input is captured.",
            [PreviewParameter, AiSnapshotParameter, DocumentParameter, RevisionParameter,
                P("action", "string", "Path action.", true, "enter", "exit", "select", "box", "begin", "set", "confirm", "cancel", "properties"),
                P("network", "string", "Network ID for enter; otherwise the selected marker or network filter is used."),
                new("nodes", "array", "Node IDs from this network. Empty replace clears selection.", Items: new("", "string", "Snapshot-scoped node ID."), MaxItems: AiPathTransforms.MaximumNodes),
                P("selection", "string", "Selection combination; default replace.", false, "replace", "add", "toggle"),
                new("rectangle", "array", "Viewport DIP [left,top,width,height] within its bounds; uses the same depth/visibility rules as GUI box selection.", Items: new("", "number", "Coordinate or size.", NumberMinimum: 0), MinItems: 4, MaxItems: 4),
                P("mode", "string", "Transform kind for begin.", false, "move", "rotate", "scale"),
                P("axis", "string", "World constraint; all uses XYZ move/scale input and Y rotation. Pointer scale is uniform.", false, "all", "x", "y", "z"),
                P("token", "string", "Current draft token for set/confirm/cancel."),
                new("input", "array", "Three invariant numeric strings, at most 64 characters each. Move XYZ deltas; scale XYZ factors; rotation uses first value in degrees. Invalid/incomplete drafts remain visible and cannot be confirmed.", Items: new("", "string", "Numeric draft."), MinItems: 3, MaxItems: 3)], true, async (a, token) =>
        {
            TargetAiGraph(a);
            var document = TargetDocument(a);
            if (document != shownDocument) throw new StudioCommandException("context_changed", "This is not the displayed world.");
            if (a["revision"]!.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
            string action = Text(a, "action");
            if (action is not ("set" or "confirm" or "cancel")) RequireNoDrafts();
            if (action == "enter") EnterAiPath(a["network"]?.GetValue<string>());
            else if (action == "exit") { scene!.SetAiPathEditing(null); UpdateAiPathPanel(); }
            else if (action is "select" or "box")
            {
                RequireAiPathEditable(); bool selected;
                if (action == "select")
                {
                    if (a["nodes"] is not JsonArray nodes) throw new StudioCommandException("invalid_argument", "Supply the node selection.");
                    selected = scene!.SetAiPathSelection(nodes.Select(n => n!.GetValue<string>()), Text(a, "selection", "replace"));
                }
                else
                {
                    if (a["rectangle"] is not JsonArray r) throw new StudioCommandException("invalid_argument", "Supply a viewport rectangle.");
                    selected = scene!.SelectAiPathBox(new Rect(r[0]!.GetValue<double>(), r[1]!.GetValue<double>(), r[2]!.GetValue<double>(), r[3]!.GetValue<double>()), Text(a, "selection", "replace"));
                }
                if (!selected) throw new StudioCommandException("not_ready", "Selection is unavailable or another draft is active.");
            }
            else if (action == "begin") BeginAiPathTransform(Text(a, "mode", "move"));
            else if (action == "properties") await OpenAiPathPropertiesAsync();
            else
            {
                if (aiPathDraft == null || Text(a, "token") != aiPathDraft.Token) throw new StudioCommandException("stale_draft", "Read the current AI path draft token.");
                if (action == "cancel") CancelAiPathDraft();
                else if (action == "confirm") document = await ApplyAiPathAsync(token);
                else
                {
                    var draft = RequireAiPathDraft(Text(a, "token"));
                    if (a["input"] is JsonArray input)
                    {
                        var values = input.Select(n => n!.GetValue<string>()).ToArray();
                        if (values.Any(s => s.Length > 64)) throw new StudioCommandException("invalid_argument", "Numeric inputs may contain at most 64 characters.");
                        draft.Input = values;
                    }
                    draft.Axis = Text(a, "axis", draft.Axis); scene!.SetAiPathPointerTransform(false);
                    PreviewAiPathDraft(); UpdateAiPathPanel();
                }
            }
            return Result(new { path = AiPathState(), document = DocumentState(document) });
        });
    }
}
