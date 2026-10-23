using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class WorldAnimationTreeTests
{
    [Fact]
    public void SearchingDoesNotExpandCallsAndExactBindingSelectionSurvivesRefresh()
    {
        var definition = new SourceAnimationDefinition(0, 0, "data/m1/zrdr/start.zad", 0, "start", "object*", true,
            null, null, null, null, null, null, null, 1, [], 0, 0, [], [0, 1]);
        SourceMapSections sections = new()
        {
            Mission = "m1", Zones = [], Networks = [], DefinitionRoots = new Dictionary<int, int[]> { [0] = [0, 1] },
            Definitions = new([new(0, definition.File, null, false, 0)], [definition]),
        };
        var state = new SceneTreeState();
        var presentation = new TreePresentation();
        var first = Create();
        var definitionRow = first.Roots.Single(r => r.Section is SectionHeader { Section: "definitions" }).Children.Single(r => r.Section is DefinitionScopeEntry).Children.Single();
        Assert.Empty(definitionRow.SearchChildren);
        Assert.Equal(0, presentation.Reads);
        var binding = definitionRow.Children[1];
        Assert.Equal(1, presentation.Reads);
        Assert.Equal(1, Assert.IsType<AnimationBindingEntry>(binding.Section).Binding.Entry);
        Assert.Empty(binding.SearchChildren);
        Assert.Equal(1, presentation.Reads);
        var call = Assert.Single(binding.Children);
        Assert.False(call.AnimationIncluded);
        Assert.True(call.AnimationIncludeEnabled);
        Assert.Empty(call.SearchChildren);
        Assert.Equal(2, presentation.Reads);
        first.Select(binding);
        binding.IsExpanded = true;
        var refreshed = Create();
        Assert.Equal(1, Assert.IsType<AnimationBindingEntry>(refreshed.Selected!.Section).Binding.Entry);
        Assert.True(refreshed.Selected.IsExpanded);

        SceneTreeModel Create()
        {
            GameScene scene = new();
            var source = SourceSelectionIndex.Build(new Dictionary<int, WorldNodeProvenance>(), CancellationToken.None);
            var projection = new SourceMapSelection(scene, null, source, i => new("node:" + i, i, null), CancellationToken.None);
            return new(scene, "fixture", true, state, projection: projection, sections: sections, sectionState: presentation);
        }
    }

    private sealed class TreePresentation : ISourceSectionState
    {
        internal int Reads { get; private set; }
        public bool ZoneOverlayOn => false;
        public bool ZoneOverlayPolygons => false;
        public bool ZoneToolEngaged => false;
        public byte? PaintZone => null;
        public bool ZoneShown(byte zone) => false;
        public bool AnyNetworkShown => false;
        public bool NetworkShown(string network) => false;
        public bool AnimationShown(int definition) => false;
        public bool MasterShown(string section) => false;
        public bool AnimationCanInclude(int rootEntry, string parentPath) => parentPath.Length == 0;
        public IReadOnlyList<SectionEntry> AnimationChildren(SourceMapSections sections, SectionEntry section, int maximum)
        {
            Reads++;
            return section switch
            {
                DefinitionEntry definition => [.. definition.Definition.Entries.Select(e => new AnimationBindingEntry(definition, new(e, "start", "object" + e, "", 1, null)))],
                AnimationBindingEntry binding => [new AnimationCallEntry(binding.Binding.Entry, "", new(binding.Binding.Entry, Guid.NewGuid(), Guid.NewGuid(), null, "missing", true, "/call"), null)],
                _ => [],
            };
        }
    }
}
