using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static readonly StudioParameter[] AnimationTargetParameters = [DocumentParameter, P("entry", "integer", "Animation entry index.", true), P("sequence", "string", "Sequence GUID; omit for entry properties."), P("event", "string", "Event GUID; omit for sequence properties."), P("segment", "integer", "Keyframe segment index; default 0.")];
    private static Guid GuidArg(JsonObject a, string name) => a[name] == null ? Guid.Empty : Guid.TryParse(Text(a, name), out var id) ? id : throw new StudioCommandException("invalid_argument", "Invalid GUID: " + name);
    private static AnimationEntry TargetEntry(DocumentModel doc, JsonObject a)
        => doc.AnimationEdits?.Package.Entries.ElementAtOrDefault(Int(a, "entry", -1)) ?? throw new StudioCommandException("unsupported", "No editable animation entry at that index.");
    private AnimationPropertiesEditor PropertyAdapter(DocumentModel doc, JsonObject a)
    {
        if (GuidArg(a,"sequence") == Guid.Empty && GuidArg(a,"event") != Guid.Empty) throw new StudioCommandException("invalid_argument","An event requires its owning sequence UUID.");
        _ = TargetEntry(doc, a); var adapter = new AnimationPropertiesEditor(doc, Int(a, "entry"), GuidArg(a, "sequence"), GuidArg(a, "event"));
        try
        {
            if (!adapter.TargetAvailable) throw new StudioCommandException("stale_record", "Sequence/event no longer exists.");
            adapter.SelectAutomationSegment(Int(a, "segment")); return adapter;
        }
        catch { adapter.Dispose(); throw; }
    }
    private void RegisterEditCommands(StudioCommands r)
    {
        Register(r, "animation_records", "List authored sequence identities; supply sequence to page its events with full edited fields. Cleanup is listed separately from runtime sequences.", false, [DocumentParameter, P("entry", "integer", "Entry index.", true), P("sequence", "string", "Optional sequence UUID for event details."), .. PageParameters], a =>
        {
            var d = TargetDocument(a); var e = TargetEntry(d, a);
            Guid id = GuidArg(a,"sequence");
            if (id != Guid.Empty)
            {
                var s = e.AllSequences.SingleOrDefault(s=>s.Id==id) ?? throw new StudioCommandException("stale_record","Sequence is unavailable.");
                return Result(new { d.Revision, sequence=s.Id, events=Page(s.Events.Select(v=>new { id=v.Id, data=v.ToJson() }),a, v => System.Text.Json.JsonSerializer.Serialize(v.data)).Data });
            }
            return Result(new { d.Revision, entry = new { e.Index,e.Name,e.RootName,e.AttachName,e.SourceOffset,e.SourceLength,headerHex=Convert.ToHexString(e.Bytes) },
                sequences = Page(e.AllSequences.Select(s => new { id = s.Id, s.Name, cleanup = s == e.Primary, s.IsEditable, eventCount=s.Events.Count,s.SourceOffset,headerHex=Convert.ToHexString(s.Bytes) }),a, s => s.Name).Data });
        });
        Register(r,"references","List authored reference table slots, including raw bytes and reserved/unverified status.",false,
            [DocumentParameter,P("entry","integer","Animation entry index.",true),P("table","integer","Reference table 0–7.",true),..PageParameters],a=>
        {
            var d=TargetDocument(a); var e=TargetEntry(d,a); int table=Int(a,"table");
            if(table is <0 or >7) throw new StudioCommandException("invalid_argument","Table must be 0–7.");
            return Result(new { d.Revision,table,records=Page(e.References[table].Select((v,i)=>new { index=i,name=v.Text(0,Math.Min(32,v.Bytes.Length)),readOnly=table is 0 or 6 or 7 || i==0,hex=Convert.ToHexString(v.Bytes) }),a, v => v.name).Data });
        });
        Register(r, "event_catalog", "Describe supported event types, fields and preview limitations.", false, [], _ => Result(AnimationCatalog.Events.Select(e => new { e.Type, e.Name, e.Size, e.Support, fields = e.Fields.Select(f => new { f.Name, f.Kind, f.Offset, f.Size, f.ReadOnly, f.ReferenceTable, f.Hint }) })));
        Register(r, "property_fields", "Read the same fields, choices, validation hints and actions as animation Properties, without opening or retargeting its window. IDs apply to this target/segment/revision.", false, AnimationTargetParameters, a =>
        {
            var d = TargetDocument(a); using var fields = PropertyAdapter(d, a); return Result(new { d.Revision, fields = fields.DescribeAutomationFields() });
        });
        Register(r, "property_edit", "Edit one Properties field using its current field ID and invariant editor text. Components use the delimiter shown in the current value. One undoable edit.", true,
            [.. AnimationTargetParameters, RevisionParameter, P("field", "string", "Field ID from property_fields.", true), P("value", "string", "New editor value.", true)], a =>
        {
            var d = TargetDocument(a, true); using var fields = PropertyAdapter(d, a); fields.WriteAutomationField(Text(a, "field"), Text(a, "value")); return Result(DocumentState(d));
        });
        Register(r, "property_action", "Run a currently available Properties action, including add/copy/delete/reorder keyframe segments.", true,
            [.. AnimationTargetParameters, RevisionParameter, P("action", "string", "Action ID from property_fields.", true)], a =>
        {
            var d = TargetDocument(a, true); using var fields = PropertyAdapter(d, a); fields.InvokeAutomationAction(Text(a, "action")); return Result(DocumentState(d));
        });
        Register(r, "animation_structure", "Add, duplicate, delete or reorder authored sequences/events. Cleanup is not an extra runtime sequence.", true,
            [.. AnimationTargetParameters, RevisionParameter, P("action", "string", "Structural operation.", true, "add_sequence", "add_event", "duplicate", "delete", "up", "down"), P("eventType", "integer", "Catalog event type for add_event.")], a =>
        {
            var d = TargetDocument(a, true); var target = TargetEntry(d, a); var edits = d.AnimationEdits!; int entry = Int(a, "entry"); Guid sequence = GuidArg(a, "sequence"), ev = GuidArg(a, "event"); string action = Text(a, "action");
            if (action != "add_sequence" && !target.AllSequences.Any(s => s.Id == sequence && (ev == Guid.Empty || s.Events.Any(v => v.Id == ev))))
                throw new StudioCommandException("stale_record", "Sequence/event is unavailable; read animation_records again.");
            if (action == "add_sequence") edits.AddSequence(entry);
            else if (action == "add_event") { int type = Int(a, "eventType", -1); if (type < 0 || type > 255 || AnimationCatalog.Find((byte)type) == null) throw new StudioCommandException("invalid_argument", "Choose an event type from event_catalog."); edits.InsertEvent(entry, sequence, (byte)type, ev); }
            else if (ev != Guid.Empty) edits.ChangeEventStructure(entry, sequence, ev, action);
            else if (action == "duplicate") edits.AddSequence(entry, sequence);
            else if (action == "delete") edits.DeleteSequence(entry, sequence);
            else edits.MoveSequence(entry, sequence, action == "up" ? -1 : 1);
            return Result(DocumentState(d));
        });
        Register(r, "reference_edit", "Retarget a verified stored reference name, preserving table indices. Reserved/unverified slots remain read-only.", true,
            [DocumentParameter, RevisionParameter, P("entry", "integer", "Entry index.", true), P("table", "integer", "Reference table index, 1–5.", true), P("index", "integer", "Nonreserved reference index.", true), P("name", "string", "New Latin-1 name.", true)], a =>
        {
            var d = TargetDocument(a, true); var e = TargetEntry(d, a); int table = Int(a, "table"), index = Int(a, "index");
            if (table is < 1 or > 5 || index <= 0 || index >= e.References[table].Count) throw new StudioCommandException("read_only", "Reference is reserved, unavailable or unverified.");
            d.AnimationEdits!.RetargetReference(e.Index, table, index, Text(a, "name")); return Result(DocumentState(d));
        });
        RegisterJob(r, "pickups", "Load/list all authored mission pickup placements and owning archive identities, including difficulty counterparts.", [DocumentParameter, .. PageParameters], false, async (a, token) =>
        {
            var d = TargetDocument(a); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, d.Lifetime.Token); var edits = await d.GetPickupEditsAsync(ViewModel.Resolver ?? throw new StudioCommandException("no_workspace", "Open a root first."), cancellation.Token);
            return Page(edits.Records.Select(p => new { source = p.Source, p.Type, position = edits.Position(p.Source), p.OriginalPosition, scope = edits.Scope(p.Source).Description, target = edits.TargetPath(p.Source.ArchivePath) }), a, p => p.Type + " " + p.source.ResourceName + " " + p.target);
        });
        Register(r, "pickup_lock", "Set this document's pickup editing lock; new documents are locked by default.", true, [DocumentParameter, RevisionParameter, P("locked", "boolean", "Whether placements are locked.", true)], a =>
        {
            var d = TargetDocument(a, true); d.PickupsLocked = Flag(a, "locked"); if (pickupDocument == d) { updating = true; PickupLocked.IsChecked = d.PickupsLocked; updating = false; scene?.SetPickupLocked(d.PickupsLocked); } return Result(DocumentState(d));
        });
        Register(r, "pickup_move", "Move a pickup to exact coordinates as one undoable operation, including unambiguous difficulty counterparts. Requires unlocked placements.", true,
            [DocumentParameter, RevisionParameter, new("source", "object", "Exact source identity returned by pickups; field names are case-sensitive.", true, Properties:
                [P("ArchivePath", "string", "Owning archive path returned by pickups.", true),
                 new("AssetIndex", "integer", "Resource asset index returned by pickups.", true, Minimum: 0, Maximum: int.MaxValue),
                 P("ResourceName", "string", "Resource name returned by pickups.", true),
                 new("RecordIndex", "integer", "Placement record index returned by pickups.", true, Minimum: 0, Maximum: int.MaxValue)]),
             P("x", "number", "World X.", true), P("y", "number", "World Y.", true), P("z", "number", "World Z.", true)], a =>
        {
            var d = TargetDocument(a, true); if (d.PickupsLocked) throw new StudioCommandException("locked", "Unlock pickup editing first.");
            var source = System.Text.Json.JsonSerializer.Deserialize<MissionPickupSource>(a["source"]!.ToJsonString()) ?? throw new StudioCommandException("invalid_argument", "Missing pickup identity.");
            var edits = d.PickupEdits ?? throw new StudioCommandException("not_ready", "Load pickups first.");
            if (edits.Find(source) == null) throw new StudioCommandException("stale_record", "Pickup source no longer exists.");
            var position = new Vector3((float)Number(a, "x"), (float)Number(a, "y"), (float)Number(a, "z"));
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) throw new StudioCommandException("invalid_argument", "Coordinates must fit finite game floats.");
            edits.MoveTo(source, position); return Result(DocumentState(d));
        });
        Register(r, "drafts", "Read pending GUI drafts and their conflict token without committing or changing focus.", false, [P("target", "string", "Draft owner.", true, "properties", "preview")], a =>
        {
            var fields = DraftOwner(Text(a, "target")); return Result(new { document = Text(a, "target") == "properties" ? propertiesWindow?.Document?.SessionId : shownDocument?.SessionId, drafts = fields?.DescribeDrafts() });
        });
        Register(r, "resolve_drafts", "Explicitly apply or discard current GUI drafts. Invalid input is retained and returned as an error, without modal dialogs.", true,
            [DocumentParameter, P("target", "string", "Draft owner.", true, "properties", "preview"), P("token", "string", "Current draft token.", true), P("action", "string", "Resolution.", true, "apply", "discard")], async (a, _) =>
        {
            var d = TargetDocument(a); string target = Text(a, "target");
            if ((target == "properties" ? propertiesWindow?.Document : shownDocument) != d) throw new StudioCommandException("context_changed", "Draft owner changed.");
            var owner = DraftOwner(target) ?? throw new StudioCommandException("not_ready", "No field editor is open."); owner.ResolveAutomationDrafts(Text(a, "token"), Text(a, "action") == "apply");
            if (owner is AnimationEditor editor) await editor.AwaitOptionWorkAsync();
            return Result(DocumentState(d));
        });
    }
    private FieldEditor? DraftOwner(string target) => target == "preview" ? animation : (FieldEditor?)propertiesWindow?.AnimationFields ?? propertiesWindow?.PickupFields;
}
