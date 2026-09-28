using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static MechAssembly MechMember(DocumentModel doc, Guid member)
    {
        var snapshot = ResourceSession(doc).Current;
        int index = snapshot.Members.ToList().FindIndex(m => m.Id == member);
        return index >= 0 && snapshot.Document.Assets[index].Content is MechAssembly assembly ? assembly : throw new StudioCommandException("unsupported", "Choose a decoded mech assembly member.");
    }
    private async Task ReplaceMechModelAsync(DocumentModel doc, Guid member, int model, int material, string path, long revision, CancellationToken token)
    {
        _ = MechMember(doc, member);
        var edits = ResourceSession(doc);
        await ApplyResourceAsync(doc, async ct =>
        {
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("OBJ exceeds the 16 MiB input limit.");
            using StreamReader reader = new(input); string text = await reader.ReadToEndAsync(ct);
            var mesh = await Task.Run(() => ModelImport.ReadObj(text, ct), ct);
            return await edits.PrepareMechModelAsync(member, model, mesh, material, ct);
        }, revision, token);
    }
    private async Task ReplaceMechModelDialogAsync(DocumentModel doc)
    {
        if (!await ResolvePropertiesDraftsAsync(doc) || doc.SelectedAsset?.ResourceId is not Guid member) return;
        long revision = doc.Revision; var assembly = MechMember(doc, member); var scene = doc.PreviewDocument.Scene!;
        StackPanel body = new() { Margin = new(16) };
        body.Children.Add(new TextBlock { Text = "Replace a member-local mesh with a triangulated OBJ. Choose an existing shared material. Edit its texture through the texture pack editor.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) });
        var models = Enumerable.Range(0, assembly.ModelCount).Select(i => new { Index = i, Label = $"Model {i} · " + string.Join(", ", scene.Nodes.Skip(assembly.RootNode).Take(assembly.NodeCount).Where(n => n.ModelIndex == assembly.FirstModel + i).Select(n => n.Name).Distinct()) }).ToArray();
        ComboBox model = new() { ItemsSource = models, DisplayMemberPath = "Label", SelectedValuePath = "Index", SelectedIndex = 0, Margin = new(0, 4, 0, 12) };
        body.Children.Add(new TextBlock { Text = "Mesh part" }); body.Children.Add(model);
        var materials = scene.Materials.Select((m, i) => new { Index = i, Label = $"Material {i} · " + (m.Int("texture_index", -1) is >= 0 and int t && t < scene.Textures.Count ? scene.Textures[t].Text("name") : "solid color") }).ToArray();
        ComboBox material = new() { ItemsSource = materials, DisplayMemberPath = "Label", SelectedValuePath = "Index", MaxDropDownHeight = 300, Margin = new(0, 4, 0, 12) };
        material.SelectedValue = assembly.ModelCount > 0 ? scene.Models[assembly.FirstModel].Polygons.FirstOrDefault()?.MaterialIndex ?? 0 : 0;
        body.Children.Add(new TextBlock { Text = "Shared material" }); body.Children.Add(material);
        var dialog = ResourceDialog("Replace mech part mesh", body); DialogButtons(dialog, body);
        if (dialog.ShowDialog() != true) return;
        if (model.SelectedValue is not int local || material.SelectedValue is not int mat) throw new InvalidDataException("Select a mesh part and shared material.");
        OpenFileDialog input = new() { Title = "Choose a local-coordinate triangulated OBJ", Filter = "Wavefront OBJ|*.obj" };
        if (input.ShowDialog(this) != true) return;
        await ReplaceMechModelAsync(doc, member, local, mat, input.FileName, revision, CancellationToken.None);
    }
    private void RegisterMechCommands(StudioCommands registry)
    {
        Register(registry, "mech_models", "Inspect a mech archive member's local model identities and shared materials. Node names may repeat across members. Replacement retains the hierarchy and uses explicit member/local model indices.", false,
            [DocumentParameter, MemberParameter, P("section", "string", "List section; default models.", false, "models", "materials"), .. PageParameters], a =>
        {
            var doc = TargetDocument(a); var member = MechMember(doc, GuidArg(a, "member")); var scene = doc.PreviewDocument.Scene!;
            if (Text(a, "section") == "materials") return Result(new { doc.Revision, materials = Page(scene.Materials.Select((m, i) => new { index = i, fields = m }), a).Data });
            return Result(new { doc.Revision, member.MemberIndex, member.RootNode, member.NodeCount,
                models = Page(scene.Models.Skip(member.FirstModel).Take(member.ModelCount).Select((m, i) => new { localModel = i, sceneModel = m.Index,
                    vertices = m.Vertices.Length, polygons = m.Polygons.Length, materials = m.Polygons.Select(p => p.MaterialIndex).Distinct().ToArray(),
                    nodes = scene.Nodes.Skip(member.RootNode).Take(member.NodeCount).Where(n => n.ModelIndex == m.Index).Select(n => new { localNode = n.Index - member.RootNode, n.Name }).ToArray() }), a).Data });
        });
        RegisterJob(registry, "mech_model_replace", "Replace one member-local mech mesh from a triangulated local-coordinate OBJ with UVs/normals and optional RGB vertex colors. Explicit shared material index; textures use texture_import. One archive undo step, with hierarchy and unrelated members preserved. Save through save_document.",
            [DocumentParameter, RevisionParameter, MemberParameter, P("localModel", "integer", "Local model index from mech_models.", true), P("material", "integer", "Shared material index from mech_models materials.", true), P("path", "string", "OBJ file path.", true)], false,
            async (a, token) => { var doc = TargetDocument(a, true); await ReplaceMechModelAsync(doc, GuidArg(a, "member"), Int(a, "localModel"), Int(a, "material"), Text(a, "path"), doc.Revision, token); return Result(DocumentState(doc)); });
    }
}
