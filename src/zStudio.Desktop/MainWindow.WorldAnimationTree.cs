using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    IReadOnlyList<SectionEntry> ISourceSectionState.AnimationChildren(SourceMapSections sections, SectionEntry section, int maximum)
    {
        if (maximum < 2) return [new NoteEntry("Animation tree limit reached (4,096 rows).")];
        if (shownDocument?.Document.Animations == null) return [];
        var plan = GetWorldAnimationStructurePlan();
        int limit = Math.Min(AnimationStructurePlan.MaximumPageSize, maximum - 1);
        switch (section)
        {
            case DefinitionEntry definition:
                return Bindings(definition, 0);
            case AnimationBindingEntry binding:
                return Calls(binding.Binding.Entry, binding.Binding.Entry, "", 0);
            case AnimationCallEntry { Call.TargetEntry: int target } call:
                return Calls(call.RootEntry, target, call.Call.Path, 0);
            case AnimationPageEntry { Definition: not null } page:
                return Bindings(page.Definition, page.Offset);
            case AnimationPageEntry page:
                return Calls(page.RootEntry, page.Entry, page.ParentPath, page.Offset);
            default: return [];
        }

        IReadOnlyList<SectionEntry> Bindings(DefinitionEntry definition, int offset)
        {
            var entries = definition.Definition.Entries;
            List<SectionEntry> rows = [];
            for (int i = offset; i < entries.Count && rows.Count < limit; i++)
                if (entries[i] >= 0 && entries[i] < plan.EntryCount) rows.Add(new AnimationBindingEntry(definition, plan.Entry(entries[i])));
            int next = offset + limit;
            if (next < entries.Count) rows.Add(new AnimationPageEntry(definition, -1, -1, "", next));
            return rows;
        }
        IReadOnlyList<SectionEntry> Calls(int root, int entry, string path, int offset)
        {
            if (path.Length > AnimationExecutionPolicy.MaximumPathCharacters - 100)
                return [new NoteEntry("Animation call path limit reached. This branch cannot be expanded further.")];
            var page = plan.Calls(entry, path, offset, limit);
            List<SectionEntry> rows = [.. page.Rows.Select(call => new AnimationCallEntry(root, path, call,
                call.TargetEntry is int target ? WorldAnimationTreeDefinition(sections, target) : null))];
            if (page.NextOffset is int next) rows.Add(new AnimationPageEntry(null, root, entry, path, next));
            return rows;
        }
    }

    bool ISourceSectionState.AnimationIncluded(int rootEntry, string path) => IsWorldAnimationCallIncluded(rootEntry, path);
    bool ISourceSectionState.AnimationCanInclude(int rootEntry, string parentPath) => CanIncludeWorldAnimationCall(rootEntry, parentPath);

    private static DefinitionEntry? WorldAnimationTreeDefinition(SourceMapSections sections, int entry)
    {
        var definition = sections.Definitions.Definitions.FirstOrDefault(d => d.Entries.Contains(entry));
        var listing = definition == null ? null : sections.Definitions.Listings.FirstOrDefault(l => l.Index == definition.Listing);
        return definition == null || listing == null ? null : new DefinitionEntry(definition, listing, false,
            sections.DefinitionRoots.GetValueOrDefault(definition.Index)?.Length ?? 0, sections.Definitions.BindingKnown);
    }

    private DefinitionEntry? SelectedAnimationDefinition() => SectionSelection?.Section switch
    {
        DefinitionEntry definition => definition,
        AnimationBindingEntry binding => binding.Definition,
        AnimationCallEntry call => call.Definition,
        _ => null,
    };

    private void OutlinerAnimationIncludeClick(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not CheckBox { Tag: SceneTreeItem { Section: AnimationCallEntry call } row }) return;
        try
        {
            if (row.Owner != sceneTree) throw new StudioCommandException("stale_record", "The animation tree was replaced.");
            RequireNoDrafts();
            SetWorldAnimationCallIncluded(call.RootEntry, call.Call.Path, !IsWorldAnimationCallIncluded(call.RootEntry, call.Call.Path));
        }
        catch (StudioCommandException error) { Report(error); }
        finally { RefreshRowEyes(); }
    }

    private async Task ShowWorldAnimationTreeCardAsync(DocumentModel document, SceneTreeItem row)
    {
        int entry = row.Section switch
        {
            AnimationBindingEntry binding => binding.Binding.Entry,
            AnimationCallEntry { Call.TargetEntry: int target } => target,
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
        await LoadAnimationCardCoreAsync(document, definition, document.Lifetime.Token, false);
        if (sceneTree?.Selected != row || document != shownDocument) return;
        synchronizingAnimationBinding = true;
        try
        {
            if (sourceAnimationBindings?.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == entry) is { } choice)
                sourceAnimationBindings.SelectedItem = choice;
        }
        finally { synchronizingAnimationBinding = false; }
        SetWorldAnimationCandidate(document, definition.Definition.Index, entry);
        RefreshRowEyes();
    }
}
