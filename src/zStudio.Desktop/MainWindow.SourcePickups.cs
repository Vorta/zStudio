using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private async Task<(DocumentModel Document, IReadOnlyList<SourceAddedPickup> Added)> AddSourcePickupAsync(
        DocumentModel document, SourcePickupAddition addition, CancellationToken token)
    {
        SourcePickupEdits.Validate(addition);
        if (document.Document is not SourceSceneSnapshot snapshot)
            throw new StudioCommandException("unsupported", "Open a source map before adding pickups.");
        IReadOnlyList<SourceAddedPickup> added = [];
        var next = await PrepareSourceWorldEditAsync(document, $"Adding {addition.Type}", (workspace, ct) =>
        {
            added = SourcePickupEdits.Add(workspace, snapshot, addition, ct);
            return null;
        }, token, verifyPrepared: (prepared, ct) => SourcePickupEdits.Verify(prepared, added, addition, ct));
        return (next, added);
    }

    private async Task AddSourcePickupInteractiveAsync(DocumentModel? target = null)
    {
        var document = target ?? ViewModel.SelectedDocument;
        if (document is not { SourceWorld: { } session })
            throw new StudioCommandException("unsupported", "Open a source map before adding pickups.");
        // Refuse what the edit would refuse before the user fills in the dialog.
        if (document.IsDisposed || session.Owner != document) throw new StudioCommandException("stale_document", "The world was rebuilt or closed.");
        RequireSourceWorldIdle(session);
        RequireNoDrafts(document);
        if (operation != null) throw new StudioCommandException("busy", "An export, validation or source operation is already running.");
        long revision = document.Revision;
        SourcePickupDialog dialog = new(this, session.Mission);
        if (dialog.ShowDialog() != true || dialog.Result is not { } addition) return;
        if (document.IsDisposed || session.Owner != document || document.Revision != revision)
            throw new StudioCommandException("stale_document", "The map changed while Add pickup was open. Reopen it for the current map.");
        await AddSourcePickupAsync(document, addition, CancellationToken.None);
    }

    private async void AddSourcePickupClick(object sender, RoutedEventArgs e) =>
        await RunUi(() => AddSourcePickupInteractiveAsync(sender == SceneAddPickupMenu ? sceneContextDocument : null));

    private void RegisterSourcePickupCommands(StudioCommands commands)
    {
        RegisterJob(commands, "source_pickup_add",
            "Add one pickup to a source map as an undoable transaction. All appends once to each effective difficulty resource; a single difficulty materializes missing variants as needed to leave other layouts unchanged. Existing records and text are preserved. The template must actually spawn with a copyable bvol. Position is explicit (stored with one decimal, rotation in radians with four); no automatic terrain snapping. Save writes owning sources.",
            [DocumentParameter, RevisionParameter,
                P("type", "string", "Pickup catalog type.", true, [.. MissionPickupType.Catalog.Select(t => t.Name)]),
                new("position", "object", "Authored world position.", true, Properties:
                    [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
                P("difficulty", "string", "Layouts receiving the pickup.", true, ["All", "Easy", "Medium", "Hard"]),
                new("rotationDegrees", "object", "Euler XYZ degrees; default zero.", Properties:
                    [P("x", "number", "X.", true), P("y", "number", "Y.", true), P("z", "number", "Z.", true)]),
                new("amount", "integer", "Stored amount; default the catalog amount. Zero uses the game's default.", Minimum: 0, Maximum: int.MaxValue),
                new("respawnDelay", "number", "Stored respawn delay; default zero.", NumberMinimum: 0, NumberMaximum: SourceWorlds.MaximumCoordinate)], true,
            async (a, token) =>
            {
                var document = TargetDocument(a, true);
                Vector3 ReadVector(JsonObject value) => new(Coordinate(value, "x"), Coordinate(value, "y"), Coordinate(value, "z"));
                var addition = new SourcePickupAddition(Text(a, "type"), ReadVector((JsonObject)a["position"]!), Text(a, "difficulty"),
                    a["rotationDegrees"] is JsonObject rotation ? ReadVector(rotation) : Vector3.Zero,
                    a["amount"] == null ? null : Int(a, "amount"), a["respawnDelay"] == null ? 0 : Coordinate(a, "respawnDelay"));
                try
                {
                    var result = await AddSourcePickupAsync(document, addition, token);
                    return Result(new { document = DocumentState(result.Document), added = result.Added });
                }
                catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            });
    }
}
