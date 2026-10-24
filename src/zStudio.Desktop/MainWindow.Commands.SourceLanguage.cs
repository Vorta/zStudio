using System.IO;
using zStudio.Automation;
using zStudio.Core.Language;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RegisterSourceLanguageCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_language", "Read-only GameZ Script language analysis over the current source project's accepted workspace overlay. The same Core service powers VS Code diagnostics, completion, hover, symbols and definition lookup. It cannot access VS Code unsaved buffers. Offsets/spans are UTF-16. validate_project also assembles world context in memory, without exports. Results are paged; symbols are flattened with parent labels.",
            [new("action", "string", "Language operation.", true, Choices: ["diagnostics", "completion", "hover", "symbols", "definition", "validate_project"]),
             P("path", "string", "Project-relative .zad or .zrd file; required except for validate_project."),
             new("position", "integer", "UTF-16 character offset for completion, hover and definition.", Minimum: 0, Maximum: SourceProject.MaximumSourceTextBytes), .. PageParameters], true,
            async (args, token) =>
            {
                string root = ViewModel.RootPath;
                if (!SourceProject.IsProject(root)) throw new StudioCommandException("read_only", "GameZ Script analysis requires a source project.");
                var workspace = sourceWorkspace?.Root.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? sourceWorkspace : null;
                long revision = workspace?.Revision ?? 0, generation = ViewModel.WorkspaceGeneration;
                var overlay = workspace?.Overlay();
                string action = Text(args, "action"), path = Text(args, "path"); int position = Int(args, "position");
                if (action != "validate_project") { _ = SourceProject.Resolve(root, path); if (!LanguageProject.IsLanguageFile(path)) throw new StudioCommandException("invalid_argument", "Choose a .zad or .zrd source."); }
                var project = await Task.Run(() => LanguageProject.Analyze(root, deep: action == "validate_project", token: token, sourceOverlay: overlay), token);
                var currentWorkspace = sourceWorkspace?.Root.Equals(root, StringComparison.OrdinalIgnoreCase) == true ? sourceWorkspace : null;
                if (generation != ViewModel.WorkspaceGeneration || !ReferenceEquals(currentWorkspace, workspace) || workspace != null && workspace.Revision != revision) throw new StudioCommandException("context_changed", "Source workspace changed during language analysis; retry.");
                if (action == "validate_project") return Page(project.Documents.Values.SelectMany(d => d.Diagnostics.Select(issue => new { path = d.Path, issue })), args,
                    search: x => x.path + " " + x.issue.Code + " " + x.issue.Message + " " + x.issue.Mission, maximumRowBytes: x => 16384 + 12L * x.path.Length);
                if (!project.Documents.TryGetValue(path, out var doc)) throw new StudioCommandException("not_found", "The text source is missing or is binary data.");
                if (position < 0 || position > doc.Text.Length) throw new StudioCommandException("invalid_argument", "position is outside the source text.");
                return action switch
                {
                    "diagnostics" => Page(doc.Diagnostics, args, search: x => x.Code + " " + x.Message + " " + x.Mission, maximumRowBytes: _ => 16384),
                    "completion" => Page(project.Complete(doc.Path, position), args, search: x => x.Label, maximumRowBytes: x => 256 + 12L * (x.Label.Length + x.InsertText.Length + x.Detail.Length)),
                    "hover" => Result(new { hover = RecoilLanguage.Hover(doc, position) }),
                    "definition" => Page(project.Definition(doc.Path, position), args, search: x => x.Path + " " + x.Context, maximumRowBytes: x => 256 + 12L * (x.Path.Length + (x.Context?.Length ?? 0))),
                    "symbols" => Page(Flatten(doc.Symbols, ""), args, search: x => x.Name + " " + x.Parent, maximumRowBytes: _ => 16384),
                    _ => throw new StudioCommandException("invalid_argument", "Unknown language operation.")
                };
            });
    }
    private sealed record SourceLanguageSymbolRow(string Name, string Kind, TextSpan Span, TextSpan Selection, string Parent);
    private static IEnumerable<SourceLanguageSymbolRow> Flatten(IReadOnlyList<LanguageSymbol> symbols, string parent)
    {
        foreach (var symbol in symbols)
        {
            yield return new(symbol.Name, symbol.Kind, symbol.Span, symbol.Selection, parent);
            foreach (var child in Flatten(symbol.Children, symbol.Name)) yield return child;
        }
    }
}
