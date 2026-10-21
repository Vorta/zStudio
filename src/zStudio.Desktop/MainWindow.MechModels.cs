using System.Text.Json.Nodes;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static MechAssembly MechMember(DocumentModel doc, Guid member)
    {
        var resources = ResourceSession(doc);
        int index = resources.Members.ToList().FindIndex(m => m.Id == member);
        return index >= 0 && resources.Document.Assets[index].Content is MechAssembly assembly ? assembly : throw new StudioCommandException("unsupported", "Choose a decoded mech assembly member.");
    }
    private void RegisterMechCommands(StudioCommands registry)
    {
        Register(registry, "mech_models", "Inspect member-local models, shared materials or node references. Model rows preview at most 32 nodes/material indices with totals and truncation flags; use section nodes/materials and localModel for complete paged lists. Node names may repeat. Queries filter model labels, node names or material labels before pagination.", false,
            [DocumentParameter, MemberParameter, P("section", "string", "List section; default models.", false, "models", "materials", "nodes"), P("localModel", "integer", "Member-local model index, required for nodes; optionally restrict models/materials."), .. PageParameters], a =>
        {
            var doc = TargetDocument(a); var member = MechMember(doc, GuidArg(a, "member")); var scene = doc.Document.Scene!;
            int? localModel = a.ContainsKey("localModel") ? Int(a, "localModel") : null;
            if (localModel is int local && (local < 0 || local >= member.ModelCount)) throw new StudioCommandException("invalid_argument", "localModel must identify a model in this member.");
            string section = Text(a, "section", "models");
            if (section == "materials")
            {
                var used = localModel is int selected ? scene.Models[member.FirstModel + selected].Polygons.Select(p => p.MaterialIndex).ToHashSet() : null;
                // Match the GUI label: "Material N" plus its texture name.
                string Texture(JsonObject material) => material.Int("texture_index", -1) is >= 0 and int t && t < scene.Textures.Count ? scene.Textures[t].Text("name") : "solid color";
                return Result(new { doc.Revision, materials = Page(scene.Materials.Select((m, i) => (Material: m, Index: i)).Where(m => used == null || used.Contains(m.Index)), a,
                    // Return the GUI picker label the query matched, bounded like other inspection labels.
                    project: m => { string texture = Texture(m.Material); return new { index = m.Index, label = $"Material {m.Index} · {texture[..Math.Min(256, texture.Length)]}",
                        textureCharacters = texture.Length, textureTruncated = texture.Length > 256, fields = m.Material }; },
                    matches: (m, query) => $"Material {m.Index}".Contains(query, StringComparison.OrdinalIgnoreCase) || Texture(m.Material).Contains(query, StringComparison.OrdinalIgnoreCase)).Data });
            }
            var nodes = scene.Nodes.Skip(member.RootNode).Take(member.NodeCount);
            if (section == "nodes")
            {
                if (localModel is not int selected) throw new StudioCommandException("invalid_argument", "Specify localModel when paging node references.");
                int sceneModel = scene.Models[member.FirstModel + selected].Index;
                return Result(new { doc.Revision, member.MemberIndex, localModel, sceneModel, nodes = Page(nodes.Where(n => n.ModelIndex == sceneModel), a, n => n.Name,
                    n => new { localNode = n.Index - member.RootNode, sceneNode = n.Index, n.Name }).Data });
            }
            var byModel = nodes.ToLookup(n => n.ModelIndex);
            return Result(new { doc.Revision, member.MemberIndex, member.RootNode, member.NodeCount,
                models = Page(scene.Models.Skip(member.FirstModel).Take(member.ModelCount).Select((m, i) => (Model: m, Index: i)).Where(m => localModel == null || m.Index == localModel), a,
                    // Match the labels users see: "Model N" and every referenced node name, without composing one search string.
                    matches: (row, query) => $"Model {row.Index}".Contains(query, StringComparison.OrdinalIgnoreCase) || byModel[row.Model.Index].Any(n => n.Name.Contains(query, StringComparison.OrdinalIgnoreCase)),
                    project: row =>
                    {
                        var m = row.Model; var references = byModel[m.Index]; int nodeCount = references.Count();
                        var materials = m.Polygons.Select(p => p.MaterialIndex).Distinct().ToArray();
                        return new { localModel = row.Index, sceneModel = m.Index, vertices = m.Vertices.Length, polygons = m.Polygons.Length,
                            materials = materials.Take(32).ToArray(), materialCount = materials.Length, materialsTruncated = materials.Length > 32,
                            nodes = references.Take(32).Select(n => new { localNode = n.Index - member.RootNode, n.Name }).ToArray(), nodeCount, nodesTruncated = nodeCount > 32 };
                    }).Data });
        });
    }
}
