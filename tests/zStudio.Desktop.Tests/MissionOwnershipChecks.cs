using System.IO;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Map coordinate edits and reader documents (valve/resource edits) own only the archives they change.</summary>
internal static class MissionOwnershipChecks
{
    internal static async Task Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        using var world = new DocumentModel(fixture.World); world.AttachResolver(fixture.Resolver);
        using var reader = new DocumentModel(await FormatRegistry.Default.OpenAsync(second, token)); reader.AttachResolver(fixture.Resolver);
        var edits = await world.GetPickupEditsAsync(fixture.Resolver, token);
        MissionCoordinateRecord Record(string archive) => edits.OtherCoordinates.Single(c => c.Source.ArchivePath.Equals(Path.GetFullPath(archive), StringComparison.OrdinalIgnoreCase));
        Assert.True(edits.MoveTo(Record(fixture.ReaderPath).Source, new(1, 2, 3)));

        // Editing and saving another reader no longer conflicts with the map's history.
        var resources = reader.ResourceEdits!; var member = resources.Current.Members[0];
        var heading = resources.Tree(member, token).Children[1].Children[2];
        resources.Accept(await resources.PrepareZrdAsync(member.Id, heading.Id, "set", value: "45", token: token));
        await resources.SaveAsync(token: token);
        Assert.False(edits.HasExternalChanges());

        // A refresh rebases the unedited reader instead of failing or keeping stale positions.
        Assert.Same(edits, await world.GetPickupEditsAsync(fixture.Resolver, token));
        Assert.Equal(45, Record(second).Rotation.Y);
        Assert.Throws<InvalidOperationException>(() => edits.MoveTo(Record(second).Source, new(5, 5, 5))); // Owned by the reader document.
        Assert.True(edits.MoveTo(Record(fixture.ReaderPath).Source, new(4, 5, 6)));
        Assert.Empty((await edits.SaveAsync(token: token)).Errors);
        Assert.Equal(resources.Current.Document.Bytes.ToArray(), await File.ReadAllBytesAsync(second, token));

        // Same archive, reverse direction: the map owns readerm1 until it closes.
        using var first = new DocumentModel(await FormatRegistry.Default.OpenAsync(fixture.ReaderPath, token)); first.AttachResolver(fixture.Resolver);
        var owned = first.ResourceEdits!; var firstMember = owned.Current.Members[0];
        var node = owned.Tree(firstMember, token).Children[1].Children[2];
        var prepared = await owned.PrepareZrdAsync(firstMember.Id, node.Id, "set", value: "10", token: token);
        Assert.Throws<InvalidOperationException>(() => owned.Accept(prepared));
        Assert.False(first.IsDirty); Assert.True(edits.CanUndo);
    }
}
