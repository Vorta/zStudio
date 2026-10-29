using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    IReadOnlyList<SectionEntry> ISourceSectionState.AnimationChildren(SectionEntry section, int maximum)
    {
        if (maximum < 2) return [new NoteEntry("Animation tree limit reached (4,096 rows).")];
        if (shownDocument?.Document.Animations == null) return [];
        var plan = GetWorldAnimationStructurePlan();
        int limit = Math.Min(AnimationStructurePlan.MaximumPageSize, maximum - 1);
        // A definition with several bindings lists them (each opens on its own binding); calls belong to Animation mode.
        switch (section)
        {
            case DefinitionEntry definition when definition.Definition.Entries.Count > 1:
                return Bindings(definition, 0);
            case AnimationPageEntry page:
                return Bindings(page.Definition, page.Offset);
            default: return [];
        }

        IReadOnlyList<SectionEntry> Bindings(DefinitionEntry definition, int offset)
        {
            var entries = definition.Definition.Entries;
            List<SectionEntry> rows = [];
            for (int i = offset; i < entries.Count && rows.Count < limit; i++)
                if (entries[i] >= 0 && entries[i] < plan.EntryCount) rows.Add(new AnimationBindingEntry(definition, plan.Entry(entries[i])));
            int next = offset + limit;
            if (next < entries.Count) rows.Add(new AnimationPageEntry(definition, next));
            return rows;
        }
    }


    private static DefinitionEntry? WorldAnimationTreeDefinition(SourceMapSections sections, int entry)
    {
        var definition = sections.Definitions.Definitions.FirstOrDefault(d => d.Entries.Contains(entry));
        var listing = definition == null ? null : sections.Definitions.Listings.FirstOrDefault(l => l.Index == definition.Listing);
        return definition == null || listing == null ? null : new DefinitionEntry(definition, listing,
            sections.DefinitionRoots.GetValueOrDefault(definition.Index)?.Length ?? 0, sections.Definitions.BindingKnown);
    }

    private DefinitionEntry? SelectedAnimationDefinition() => SectionSelection?.Section switch
    {
        DefinitionEntry definition => definition,
        AnimationBindingEntry binding => binding.Definition,
        _ => null,
    };

    private async Task ShowWorldAnimationTreeCardAsync(DocumentModel document, SceneTreeItem row)
    {
        int entry = row.Section switch
        {
            AnimationBindingEntry binding => binding.Binding.Entry,
            _ => -1,
        };
        var definition = SelectedAnimationDefinition();
        if (entry < 0 || definition == null) return;
        sourceAnimationEntry = entry;
        // Retire the old definition's editable card before the asynchronous source read, just as a definition-row click does.
        if (!SectionCard().ShowsAnimation(document, definition))
        {
            var projection = sceneTree?.Projection;
            int[] roots = sceneTree?.Sections?.DefinitionRoots.GetValueOrDefault(definition.Definition.Index) ?? [];
            var named = roots.Select(root => (root, projection?.DisplayName(root) ?? root.ToString(System.Globalization.CultureInfo.InvariantCulture),
                projection?.Parent(root) is int parent ? projection.DisplayName(parent) : projection?.PassOf(root)?.Label ?? "")).ToArray();
            var notes = document.Document is SourceSceneSnapshot built ? built.Notes.Where(n => n.StartsWith(definition.Definition.File, StringComparison.OrdinalIgnoreCase)).Take(8).Select(n => zStudio.Core.JsonData.ShownText(n, 400)).ToArray() : [];
            SectionCard().ShowDefinition(document, definition, named, notes, sceneTree?.Sections?.Definitions.Rejection,
                Math.Min(roots.Length, zStudio.Rendering.SceneViewport.MaximumOutlinedNodes));
        }
        await LoadAnimationCardCoreAsync(document, definition, document.Lifetime.Token);
        if (sceneTree?.Selected != row || document != shownDocument) return;
        RefreshAnimationInspector();
        RefreshRowEyes();
    }
}
