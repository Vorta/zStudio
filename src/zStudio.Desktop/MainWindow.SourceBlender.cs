using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// The Blender round trip of a source project: Edit in Blender copies a model into the project's zstudio/export folder;
/// Update from Blender export reads what Blender exported there and changes the project's sources as one undoable edit,
/// rebuilding the world. Nothing Blender writes is used until the user asks for the update.
/// </summary>
public partial class MainWindow
{
    /// <summary>The model a source world's object comes from: its own glTF node, or the file its script loaded.</summary>
    private string? SourceObjectModel(DocumentModel doc, int node)
    {
        var origin = DescribeSourceObject(doc, node).Origin;
        if (origin.ModelFile != null) return origin.ModelFile;
        // A load's root: the model the LoadGameGen read is the file of its children.
        return doc.SourceBuild!.Provenance.Values.FirstOrDefault(p => p.Load != null && origin.Created != null && p.Load.Script == origin.Created.Script && p.Load.Line == origin.Created.Line && p.ModelFile != null)?.ModelFile;
    }

    private BlenderCheckout CheckoutForBlender(string model)
    {
        string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
        var workspace = SourceWorkspaceFor(root);
        try { return SourceBlender.Checkout(workspace, model); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { throw new StudioCommandException("io_failed", ex.Message); }
    }

    /// <summary>Plans an export's changes and applies them to the workspace, rebuilding <paramref name="doc"/>'s world.</summary>
    private async Task<(DocumentModel Document, BlenderUpdatePlan Plan)> UpdateFromBlenderAsync(DocumentModel doc, string checkoutId, string? export, CancellationToken token)
    {
        var session = doc.SourceWorld ?? throw new StudioCommandException("unsupported", "Open a mission world of the source project to update it from Blender.");
        BlenderCheckout checkout;
        try { checkout = SourceBlender.Find(session.Root, checkoutId); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        BlenderUpdatePlan? plan = null;
        var next = await EditSourceWorldAsync(doc, $"Updating {Path.GetFileName(checkout.Model)} from Blender", workspace =>
        {
            plan = SourceBlender.PlanUpdate(workspace, checkout, export, token);
            return workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token) is { } t ? () => workspace.Retract(t) : null;
        }, token, fromBuild: false);
        foreach (string note in plan?.Notes ?? []) ViewModel.AddProblem(Bounded($"{session.Label}: {note}"), "Warning", Path.Combine(session.Root, checkout.Model.Replace('/', Path.DirectorySeparatorChar)));
        return (next, plan!);
    }

    private static object CheckoutResult(BlenderCheckout checkout) => new
    {
        id = checkout.Id, folder = checkout.Folder, model = checkout.Model, input = checkout.Input, outbox = checkout.Outbox, created = checkout.CreatedUtc,
        files = checkout.Files.Take(64).Select(f => new { project = f.Project, checkout = f.Checkout }).ToArray(), fileCount = checkout.Files.Count,
        exports = SourceBlender.Exports(checkout).Take(16).Select(e => new { path = e.Relative, written = e.WrittenUtc, bytes = e.Bytes }).ToArray()
    };

    /// <summary>GUI: Edit in Blender for the object selected in a source world (its model), then show the checkout folder.</summary>
    private void EditInBlenderClick(object sender, RoutedEventArgs e) => _ = RunUi(() =>
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: not null } doc) throw new StudioCommandException("unsupported", "Open a mission world of the source project and select an object first.");
        if (selectedNode is not int node || SourceObjectNode(node) is not int sourceNode) throw new StudioCommandException("not_ready", "Select an object of the world first.");
        string model = SourceObjectModel(doc, sourceNode) ?? throw new StudioCommandException("unsupported", "The selected object was not loaded from a model file.");
        var checkout = CheckoutForBlender(model);
        string message = $"{model} is checked out for Blender.\n\n1. In Blender, import:\n{checkout.Input}\n2. Export it as glTF 2.0, format glTF Separate (.gltf + .bin + textures), with Custom Properties, into:\n{checkout.Outbox}\n3. Choose Tools → Update from Blender export.\n\nOpen the checkout folder now?";
        if (MessageBox.Show(this, message, "Edit in Blender", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
            Process.Start(new ProcessStartInfo { FileName = checkout.Folder, UseShellExecute = true });
        return Task.CompletedTask;
    });

    /// <summary>GUI: Update from Blender export — the newest export of the newest checkout, after confirmation.</summary>
    private void UpdateFromBlenderClick(object sender, RoutedEventArgs e) => _ = RunUi(async () =>
    {
        if (ViewModel.SelectedDocument is not { SourceWorld: { } session } doc) throw new StudioCommandException("unsupported", "Open a mission world of the source project first.");
        var candidates = SourceBlender.Checkouts(session.Root).Select(c => (Checkout: c, Export: SourceBlender.Exports(c).FirstOrDefault())).Where(c => c.Export != null).ToArray();
        if (candidates.Length == 0) throw new StudioCommandException("not_ready", "No Blender export was found in the project's zstudio/export folder. Use Edit in Blender first.");
        var (checkout, export) = candidates.OrderByDescending(c => c.Export!.WrittenUtc).First();
        if (MessageBox.Show(this, $"Update {checkout.Model} from the Blender export {export!.Relative} ({export.WrittenUtc.ToLocalTime():g})?\n\nThe model, its buffer and changed textures are replaced in the project's unsaved edits; Save writes them.",
            "Update from Blender export", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        var (_, plan) = await UpdateFromBlenderAsync(doc, checkout.Id, export.Relative, CancellationToken.None);
        ViewModel.Status = $"Updated {checkout.Model} from Blender: {plan.Changes.Count} file{(plan.Changes.Count == 1 ? "" : "s")} changed" + (plan.Notes.Count > 0 ? $"; {plan.Notes.Count} notes in Problems" : "");
    });

    private void RegisterSourceBlenderCommands(StudioCommands r)
    {
        Register(r, "source_blender_checkout", "Check a project model out for Blender: its glTF, buffers and textures are copied into the project's zstudio/export/<id>/input folder as a self-contained glTF to import in Blender. Export the edited model as glTF Separate (.gltf + .bin + textures, Custom Properties on) into the checkout's outbox, then call source_blender_update. Give the model path, or a source world document and scene node to check out the model that object comes from.", true,
            [P("model", "string", "Project path of a .gltf model, for example data/m2/models/bft/ltank.gltf."), DocumentParameter with { Required = false }, new("node", "integer", "Scene node of the document whose model to check out.", Minimum: 0, Maximum: int.MaxValue)], a =>
        {
            string model;
            if (a["model"] != null) { if (a["node"] != null) throw new StudioCommandException("invalid_argument", "Give a model or a node, not both."); model = Text(a, "model"); }
            else
            {
                var d = TargetDocument(a); if (a["node"] == null) throw new StudioCommandException("invalid_argument", "Give a model path or a node.");
                model = SourceObjectModel(d, Int(a, "node")) ?? throw new StudioCommandException("unsupported", "That object was not loaded from a model file.");
            }
            return Result(CheckoutResult(CheckoutForBlender(model)));
        });
        Register(r, "source_blender_checkouts", "List the open source project's Blender checkouts (newest first) with the exports found in each outbox (newest first).", false, [], _ =>
        {
            string root = SourceProjectRoot ?? throw new StudioCommandException("no_project", "Open a source project (a folder with data and gamegen) first.");
            return Result(new { checkouts = SourceBlender.Checkouts(root).Take(32).Select(CheckoutResult).ToArray() });
        });
        RegisterJob(r, "source_blender_update", "Update a checked-out model from what Blender exported into its outbox, as one undoable change of the project's workspace: the export is sealed (copied while checking it is complete), read as a build reads models, and becomes the model's glTF and buffer, with each new or changed texture PNG; a texture other models use changes for them too (reported in notes). The source world rebuilds and the result is its replacement document; an export the world cannot be built with is taken back. Nothing is written until save_document.",
            [DocumentParameter, RevisionParameter, P("checkout", "string", "Checkout id from source_blender_checkout or source_blender_checkouts.", true), P("export", "string", "Export path relative to the outbox; default the newest.")], true,
            async (a, token) =>
            {
                var d = TargetDocument(a, true);
                var (next, plan) = await UpdateFromBlenderAsync(d, Text(a, "checkout"), a["export"] == null ? null : Text(a, "export"), token);
                return Result(new { document = DocumentState(next), files = plan.Changes.Select(c => c.Relative).ToArray(), notes = plan.Notes.Take(32).ToArray(), @sealed = plan.Sealed });
            });
    }
}
