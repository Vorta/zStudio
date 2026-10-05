using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The order in which the original build tool made (+) and freed (−) nodes while loading a file.</summary>
public sealed class OriginalLoaderTests
{
    private static WorldNode Node(string name, params WorldNode[] children)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D);
        foreach (var child in children) { node.Children.Add(child); child.Parents.Add(node); }
        return node;
    }

    /// <summary>Loads <paramref name="records"/> under a root; <paramref name="content"/> holds each reference's content (the copy of its file).</summary>
    private static List<string> Load(IReadOnlyList<WorldNode> records, Dictionary<WorldNode, IReadOnlyList<WorldNode>> content)
    {
        List<string> events = [];
        OriginalLoader.Load(Node("root"), records, records, new()
        {
            Allocate = n => events.Add("+" + n.Name), Free = n => events.Add("-" + n.Name),
            Content = n => content.GetValueOrDefault(n) ?? [],
            File = n => n.Name,
        });
        return events;
    }

    [Fact]
    public void AReferencesCopyFollowsTheNextRecordAndItsCacheIsFreedAtTheEnd()
    {
        var x = Node("x"); var a = Node("a.flt", x); var b = Node("b");
        var events = Load([a, b], new(ReferenceEqualityComparer.Instance) { [a] = [x] });
        // The file's cache (a load of its own), the root, the reference, the next record, then the copy; the cache last.
        Assert.Equal(["+a.flt", "+x", "+root", "+a.flt", "+b", "+x", "-x", "-a.flt"], events);
    }

    [Fact]
    public void SharedNodesAreMadeOnceBeforeTheRoot()
    {
        // An OpenFlight instance definition under two instance references: made with its subtree before the root, and
        // the references attach it without making nodes.
        var definition = Node("", Node("gate"));
        var g1 = Node("sgate1", definition); var g2 = Node("sgate2"); g2.Children.Add(definition); definition.Parents.Add(g2);
        var events = Load([Node("ground"), Node("startgate", g1, g2)], new(ReferenceEqualityComparer.Instance));
        Assert.Equal(["+", "+gate", "+root", "+ground", "+startgate", "+sgate1", "+sgate2"], events);
    }

    [Fact]
    public void ACachedFilesIdenticalUnnamedSubtreesAreOneDefinition()
    {
        // The copy a reference shows expanded the file's instance along each edge; its cache has one definition, made
        // before the cache's root and freed with the last placement that holds it.
        WorldNode Placement(string name) => Node(name, Node("", Node("g2")));
        var p1 = Placement("s1"); var p2 = Placement("s2");
        var reference = Node("ring.flt", p1, p2);
        var events = Load([reference, Node("next")], new(ReferenceEqualityComparer.Instance) { [reference] = [p1, p2] });
        Assert.Equal([
            "+", "+g2", "+ring.flt", "+s1", "+s2",
            "+root", "+ring.flt", "+next", "+s1", "+", "+g2", "+s2", "+", "+g2",
            "-s1", "-g2", "-", "-s2", "-ring.flt"], events);
    }

    [Fact]
    public void AReferencesOwnRecordsAreCachedToo()
    {
        // A part's reference with a record of its own that references a model: both files are cached, in record order;
        // the part is copied at once, before the record.
        var c = Node("c"); var y = Node("y");
        var model = Node("lamp.flt", y);
        var part = Node("part.flt", c, model);
        var events = Load([part], new(ReferenceEqualityComparer.Instance) { [part] = [c], [model] = [y] });
        Assert.Equal([
            "+part.flt", "+c", "+lamp.flt", "+y",
            "+root", "+part.flt", "+c", "+lamp.flt", "+", "+y", "-",
            "-c", "-part.flt", "-y", "-lamp.flt"], events);
    }
}
