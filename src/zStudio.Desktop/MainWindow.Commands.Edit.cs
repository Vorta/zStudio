using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static Guid GuidArg(JsonObject a, string name) => a[name] == null ? Guid.Empty : Guid.TryParse(Text(a, name), out var id) ? id : throw new StudioCommandException("invalid_argument", "Invalid GUID: " + name);
    /// <summary>An entry of a compiled animation pack; a source map's animations are its definitions, inspected through the scene.</summary>
    private static AnimationEntry TargetEntry(DocumentModel doc, JsonObject a)
        => (doc.SourceWorld == null ? doc.Document.Animations?.Entries.ElementAtOrDefault(Int(a, "entry", -1)) : null)
            ?? throw new StudioCommandException("unsupported", "No compiled animation entry at that index.");
    private void RegisterEditCommands(StudioCommands r)
    {
        Register(r, "animation_records", "A compiled animation's stored records, read only. list (default): sequence identities; with sequence, its events paged with decoded scalar fields and a 256-byte raw preview (raw_hex_truncated). Keyframe streams are omitted (keyframes_omitted); use JSON export for them. Event queries match these bounded displayed fields before pagination. Cleanup is listed separately from runtime sequences. bytes: the original source bytes of the entry, a sequence (sequence) or an event (event) as hex, from offset (relative to the record) for length bytes, at most 4,096 (the Inspector's Bytes section), with the record's source offset and length and nextOffset.", false,
            [DocumentParameter, P("entry", "integer", "Entry index.", true), P("action", "string", "list (default) or bytes.", false, "list", "bytes"), P("sequence", "string", "Optional sequence UUID: its events (list) or its bytes (bytes)."),
             P("event", "string", "bytes: an event UUID (its sequence is found)."), new("length", "integer", "bytes: how many bytes, 1–4096 (default 4096).", Minimum: 1, Maximum: CompiledByteLimit), .. PageParameters], a =>
        {
            var d = TargetDocument(a); var e = TargetEntry(d, a);
            if (Text(a, "action", "list") == "bytes")
            {
                Guid sequenceId = GuidArg(a, "sequence"), eventId = GuidArg(a, "event");
                long recordOffset = e.SourceOffset, recordLength = e.SourceLength; string record = $"entry #{e.Index} · {e.Name}";
                if (eventId != Guid.Empty)
                {
                    var owner = e.AllSequences.FirstOrDefault(s => (sequenceId == Guid.Empty || s.Id == sequenceId) && s.Events.Any(v => v.Id == eventId)) ?? throw new StudioCommandException("stale_record", "Event is unavailable.");
                    var ev = owner.Events.First(v => v.Id == eventId);
                    (recordOffset, recordLength, record) = (ev.SourceOffset, ev.Bytes.Length, $"event {ev.Name} in {(owner == e.Primary ? "Cleanup" : owner.Name)}");
                }
                else if (sequenceId != Guid.Empty)
                {
                    var owner = e.AllSequences.FirstOrDefault(s => s.Id == sequenceId) ?? throw new StudioCommandException("stale_record", "Sequence is unavailable.");
                    (recordOffset, recordLength, record) = (owner.SourceOffset, SequenceLength(owner), "sequence " + (owner == e.Primary ? "Cleanup" : owner.Name));
                }
                int start = Int(a, "offset"), count = Int(a, "length", CompiledByteLimit);
                if (start < 0 || count is < 1 or > CompiledByteLimit) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and length 1–4096.");
                if (recordLength > 0 && start >= recordLength) throw new StudioCommandException("invalid_argument", "The offset is past the record's end.");
                var bytes = CompiledSourceBytes(d, recordOffset, recordLength, start, count);
                return Result(new
                {
                    d.Revision, label = "original source bytes", record, sourceOffset = recordOffset, recordLength, offset = start, length = bytes.Length,
                    hex = Convert.ToHexString(bytes.Span), nextOffset = start + bytes.Length < recordLength ? (long?)(start + bytes.Length) : null,
                });
            }
            Guid id = GuidArg(a,"sequence");
            if (id != Guid.Empty)
            {
                var s = e.AllSequences.SingleOrDefault(s=>s.Id==id) ?? throw new StudioCommandException("stale_record","Sequence is unavailable.");
                return Result(new { d.Revision, sequence=s.Id, events=Page(s.Events,a, v => v.ToPreviewJson().ToJsonString(), v => new { id=v.Id, data=v.ToPreviewJson() }).Data });
            }
            return Result(new { d.Revision, entry = new { e.Index,e.Name,e.RootName,e.AttachName,e.SourceOffset,e.SourceLength,headerHex=Convert.ToHexString(e.Bytes) },
                sequences = Page(e.AllSequences, a, s => s.Name, s => new { id = s.Id, s.Name, cleanup = s == e.Primary, s.IsEditable, eventCount=s.Events.Count,s.SourceOffset,headerHex=Convert.ToHexString(s.Bytes) }).Data });
        });
        Register(r,"references","List authored reference table slots, including raw bytes and whether a slot is reserved or unverified.",false,
            [DocumentParameter,P("entry","integer","Animation entry index.",true),P("table","integer","Reference table 0–7.",true),..PageParameters],a=>
        {
            var d=TargetDocument(a); var e=TargetEntry(d,a); int table=Int(a,"table");
            if(table is <0 or >7) throw new StudioCommandException("invalid_argument","Table must be 0–7.");
            string Name(AnimationRecord v) => v.Text(0,Math.Min(32,v.Bytes.Length));
            return Result(new { d.Revision,table,records=Page(e.References[table].Select((v,i)=>(Value:v,Index:i)),a, x => Name(x.Value),
                x => new { index=x.Index,name=Name(x.Value),reserved=table is 0 or 6 or 7 || x.Index==0,hex=Convert.ToHexString(x.Value.Bytes) }).Data });
        });
        Register(r, "event_catalog", "Describe supported event types, fields and preview limitations for the requested animation format version.", false,
            [P("version", "integer", "Animation version: 28 (Recoil, default) or 39 (MechWarrior 3).")], a =>
        {
            int version = Int(a, "version", 28);
            if (version is not (28 or 39)) throw new StudioCommandException("unsupported", "Supported animation versions are 28 and 39.");
            return Result(AnimationCatalog.ForVersion((uint)version).Select(e => new { e.Type, e.Name, e.Size, e.Support, fields = e.Fields.Select(f => new { f.Name, f.Kind, f.Offset, f.Size, f.ReadOnly, f.ReferenceTable, f.Hint }) }));
        });
        RegisterJob(r, "pickups", "Load/list all authored mission pickup placements and owning resource identities, including difficulty counterparts. Full identities are retained; long or escaped paths shorten pages, so follow nextOffset. Combined query text is bounded before copying; omit query and page if that allowance is exceeded.", [DocumentParameter, .. PageParameters], false, async (a, token) =>
        {
            var d = TargetDocument(a); using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, d.Lifetime.Token); var edits = await d.GetPickupEditsAsync(ViewModel.Resolver ?? throw new StudioCommandException("no_workspace", "Open a root first."), cancellation.Token);
            token.ThrowIfCancellationRequested();
            if (d.IsDisposed || !ViewModel.Documents.Contains(d)) throw new StudioCommandException("stale_document", "The pickup document is no longer open. Read zstudio_state before retrying.");
            cancellation.Token.ThrowIfCancellationRequested();
            InspectionResultBudget.Search search = new();
            return Page(edits.Records, a,
                project: p => new { source = p.Source, p.Type, position = edits.Position(p.Source), rotationRadians = edits.Rotation(p.Source), p.OriginalPosition, scope = edits.Scope(p.Source).Description },
                matches: (p, query) => search.Pickup(p, query),
                maximumRowBytes: p => InspectionResultBudget.Pickup(p, edits.Scope(p.Source).Description));
        });
        Register(r, "pickup_move", "Move a source map's pickup to exact coordinates as one undoable operation, including unambiguous difficulty counterparts. The move accepts changed coordinate tokens of the resource sources (data/mN/zrdr/puppies*.zrd) in memory, stored with one decimal (unchanged values keep their text), and updates the scene. The result is the accepted document/revision. Save writes the sources; no archives are created. Compiled worlds are read only.", true,
            [DocumentParameter, RevisionParameter, new("source", "object", "Exact source identity returned by pickups; field names are case-sensitive.", true, Properties:
                [P("ArchivePath", "string", "Owning archive path returned by pickups.", true),
                 new("AssetIndex", "integer", "Resource asset index returned by pickups.", true, Minimum: 0, Maximum: int.MaxValue),
                 P("ResourceName", "string", "Resource name returned by pickups.", true),
                 new("RecordIndex", "integer", "Placement record index returned by pickups.", true, Minimum: 0, Maximum: int.MaxValue)]),
             P("x", "number", "World X.", true), P("y", "number", "World Y.", true), P("z", "number", "World Z.", true)], async (a, token) =>
        {
            var d = TargetDocument(a, true); if (d.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
            var source = System.Text.Json.JsonSerializer.Deserialize<MissionPickupSource>(a["source"]!.ToJsonString()) ?? throw new StudioCommandException("invalid_argument", "Missing pickup identity.");
            var edits = d.PickupEdits ?? throw new StudioCommandException("not_ready", "Load pickups first.");
            if (edits.Find(source) == null) throw new StudioCommandException("stale_record", "Pickup source no longer exists.");
            var position = new Vector3((float)Number(a, "x"), (float)Number(a, "y"), (float)Number(a, "z"));
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) throw new StudioCommandException("invalid_argument", "Coordinates must fit finite game floats.");
            return Result(DocumentState(await MoveSourcePlacementAsync(d, source, edits.Transform(source) with { Position = position }, token)));
        });
        Register(r, "drafts", "Read pending GUI drafts and their conflict token without committing or changing focus. section is the source map's zone, AI network or animation card (its label, header fields or animation draft; source_animation apply and cancel resolve the last too).", false, [P("target", "string", "Draft owner.", true, "properties", "preview", "scene", "section")], a =>
        {
            if (Text(a, "target") == "section") return Result(new { document = sectionCard?.Document?.SessionId, drafts = sectionCard?.Entry == null ? null : sectionCard.Describe() });
            if (Text(a, "target") == "scene") return Result(new { document = inspectionDraft?.DraftDocument?.SessionId ?? shownDocument?.SessionId, drafts = (HasInspectionDraft ? inspectionDraft : CurrentInspectionCard)?.DescribeDraft() });
            var fields = DraftOwner(Text(a, "target")); return Result(new { document = Text(a, "target") == "properties" ? propertiesWindow?.Document?.SessionId : shownDocument?.SessionId, drafts = fields?.DescribeDrafts() });
        });
        Register(r, "resolve_drafts", "Explicitly apply or discard current GUI drafts. Invalid input is retained and returned as an error, without modal dialogs.", true,
            [DocumentParameter, P("target", "string", "Draft owner.", true, "properties", "preview", "scene", "section"), P("token", "string", "Current draft token.", true), P("action", "string", "Resolution.", true, "apply", "discard")], async (a, token) =>
        {
            var d = TargetDocument(a); string target = Text(a, "target");
            if (target == "section")
            {
                if (sectionCard is not { } card || card.Document != d) throw new StudioCommandException("context_changed", "Section draft owner changed.");
                card.RequireDraft(Text(a, "token"));
                if (Text(a, "action") == "apply") await card.ApplyAsync(); else card.Discard();
                if (card.HasDraft && Text(a, "action") == "apply") throw new StudioCommandException("invalid_argument", "The section card refused its changes; read the draft and its message.");
                return Result(DocumentState(LiveDocument(d)));
            }
            if (target == "scene")
            {
                if (!HasInspectionDraft || inspectionDraft!.DraftDocument != d) throw new StudioCommandException("context_changed", "Scene draft owner changed.");
                inspectionDraft.RequireDraft(Text(a, "token"));
                if (Text(a, "action") == "apply")
                {
                    if (ApplyInspectionEditCore(inspectionDraft, token) is { } rebuilding) return Result(DocumentState(await rebuilding));
                }
                else inspectionDraft.CancelDraft();
                return Result(DocumentState(d));
            }
            if ((target == "properties" ? propertiesWindow?.Document : shownDocument) != d) throw new StudioCommandException("context_changed", "Draft owner changed.");
            var owner = DraftOwner(target) ?? throw new StudioCommandException("not_ready", "No field editor is open."); await owner.ResolveAutomationDraftsAsync(Text(a, "token"), Text(a, "action") == "apply");
            if (owner is AnimationEditor editor) await editor.AwaitOptionWorkAsync();
            ThrowIfSourceRequestCanceled(token);
            // Applying a source world's draft rebuilds the world: the result is the document that shows it now.
            var live = LiveDocument(d);
            if (live.IsDisposed || !ViewModel.Documents.Contains(live))
                throw new StudioCommandException("stale_document", "The draft document was closed during resolution.");
            if (live == d && (DraftOwner(target) != owner || (target == "properties" ? propertiesWindow?.Document : shownDocument) != d))
                throw new StudioCommandException("context_changed", "The draft editor was replaced during resolution.");
            return Result(DocumentState(live));
        });
    }
    private FieldEditor? DraftOwner(string target) => target == "preview" ? animation : (FieldEditor?)propertiesWindow?.PickupFields ?? propertiesWindow?.SourceFields;
}
