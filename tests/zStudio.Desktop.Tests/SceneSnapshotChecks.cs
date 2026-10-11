using System.IO;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Numerics;
using System.Security.Cryptography;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SceneSnapshotChecks
{
    internal static async Task Run()
    {
        foreach (bool modelFirst in new[] { false, true })
        {
            string folder = Path.Combine(Path.GetTempPath(), "zstudio-mixed-scene-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var token = deadline.Token;
                string worldPath = Path.Combine(folder, "gamez.zbd"), texturePath = Path.Combine(folder, "texture2.zbd"), archivePath = Path.Combine(folder, "zrdr.zbd");
                await File.WriteAllBytesAsync(worldPath, ModelFixture.GameZ(), token);
                await File.WriteAllBytesAsync(texturePath, ModelFixture.Texture(), token);
                await File.WriteAllBytesAsync(archivePath, PickupArchive(token), token);
                using var resolver = new AssetResolver(folder);
                using var doc = new DocumentModel(await resolver.OpenCachedAsync(worldPath, token)); doc.AttachResolver(resolver);
                using var archive = new DocumentModel(await resolver.OpenCachedAsync(archivePath, token));
                Assert.Null(archive.ModelEdits); Assert.NotNull(archive.ResourceEdits); // zrdr.zbd never hosts a model-edit session.
                var models = doc.ModelEdits!;
                var batch = new ModelImportBatch(Convert.ToHexString(SHA256.HashData(doc.Document.Bytes.Span)), "new_shell", new(1, 1, [64, 128, 32, 255]), new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh });
                var prepared = await models.PrepareAsync(batch, resolver, token);
                Assert.All(prepared.Textures.Values, d => Assert.Equal(FormatFamily.TexturePack, d.Probe.Family));
                // Even a caller injecting a forged prepared snapshot cannot
                // publish a ZAR through the model service or mutate history.
                Assert.Throws<InvalidDataException>(() => models.Accept(prepared with { Textures = new Dictionary<string, ZbdDocument> { [archivePath] = archive.Document } }));
                Assert.False(doc.IsDirty); Assert.False(doc.CanUndoScene); Assert.Equal(0, doc.Revision);
                if (modelFirst) models.Accept(prepared);
                var placements = await doc.GetPickupEditsAsync(resolver, token); var source = Assert.Single(placements.Records).Source;
                placements.TransformTo(source, new(new(1, 2, 3), new(.1f, .2f, .3f)));
                if (!modelFirst) models.Accept(prepared);
                await Verify();
                doc.UndoScene(false); await Verify(); doc.UndoScene(false); await Verify(); Assert.False(doc.IsDirty);
                doc.UndoScene(true); await Verify(); doc.UndoScene(true); await Verify();
                Assert.Empty((await placements.SaveAsync(token: token)).Errors);
                Assert.Empty((await models.SaveAsync(token: token)).Errors); Assert.False(doc.IsDirty);
                await Verify();
                async Task Verify()
                {
                    var published = models.Documents.Concat(placements.WorkingArchives(token)).ToArray();
                    Assert.Equal(3, published.Length); Assert.Equal(3, published.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
                    foreach (var snapshot in published)
                        Assert.Equal(snapshot.Bytes.ToArray(), (await resolver.OpenCachedAsync(snapshot.Path, token)).Bytes.ToArray());
                }
            }
            finally { Directory.Delete(folder, true); }
        }
        await RetiredModelHistory();
        await ReplacedPickupSession();
    }
    // A model import publishes new snapshots, so the next map refresh replaces the clean pickup session. Pinned Properties
    // must move the placement in the document's current session; the replaced one accepts no move and never reaches the
    // shared history, so Save, closing and Undo all see the edit. A session saved as a copy keeps that copy as the target
    // of later saves instead, following an external change to its original.
    private static async Task ReplacedPickupSession()
    {
        foreach (bool savedAs in new[] { false, true })
        {
            string folder = Path.Combine(Path.GetTempPath(), "zstudio-replaced-pickups-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            try
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = deadline.Token;
                string worldPath = Path.Combine(folder, "gamez.zbd"), archivePath = Path.Combine(folder, "zrdr.zbd"), copyPath = Path.Combine(folder, "copy", "zrdr.zbd");
                await File.WriteAllBytesAsync(worldPath, ModelFixture.GameZ(), token);
                await File.WriteAllBytesAsync(Path.Combine(folder, "texture2.zbd"), ModelFixture.Texture(), token);
                byte[] archiveBytes = PickupArchive(token); await File.WriteAllBytesAsync(archivePath, archiveBytes, token);
                async Task Replace(byte[] bytes, int minutes)
                {
                    await File.WriteAllBytesAsync(archivePath, bytes, token);
                    File.SetLastWriteTimeUtc(archivePath, DateTime.UtcNow.AddMinutes(minutes)); // A distinct stamp, whatever the clock resolution.
                }
                using var resolver = new AssetResolver(folder);
                using var doc = new DocumentModel(await resolver.OpenCachedAsync(worldPath, token)); doc.AttachResolver(resolver);
                var models = doc.ModelEdits!; var opened = models.Current;
                var replaced = await doc.GetPickupEditsAsync(resolver, token); var source = Assert.Single(replaced.Records).Source;
                var original = replaced.Position(source);
                if (savedAs) Assert.Empty((await replaced.SaveAsync(new Dictionary<string, string> { [source.ArchivePath] = copyPath }, token: token)).Errors);
                doc.PickupsLocked = false;
                using var fields = new PickupPropertiesEditor(doc, source, "HEMORTAR_AMMO", new JsonObject());
                if (!savedAs)
                {
                    // Another change removes the placement: the clean session no longer holds the pinned record, which then
                    // reads as gone and accepts no edit until the record is back.
                    await Replace(PickupArchive(token, removed: true), 1);
                    Assert.Same(replaced, await doc.GetPickupEditsAsync(resolver, token)); Assert.Null(replaced.Find(source));
                    Assert.NotNull(fields.Json["placement_unavailable"]);
                    Assert.ThrowsAny<Exception>(() => fields.WriteAutomationField("properties/0", "7, 8, 9"));
                    await Replace(archiveBytes, 2);
                    Assert.Same(replaced, await doc.GetPickupEditsAsync(resolver, token)); Assert.Equal(original, replaced.Position(source));
                }
                models.Accept(await models.PrepareAsync(new(Convert.ToHexString(SHA256.HashData(doc.Document.Bytes.Span)), "new_shell",
                    new(1, 1, [64, 128, 32, 255]), new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh }), resolver, token));
                var current = await doc.GetPickupEditsAsync(resolver, token);
                byte[] sourceBytes = archiveBytes;
                if (savedAs)
                {
                    // The original then changes outside zStudio: the kept session without history follows it and still saves to the copy.
                    await Replace(sourceBytes = PickupArchive(token, x: "4"), 1);
                    Assert.Same(current, await doc.GetPickupEditsAsync(resolver, token)); Assert.Equal(new Vector3(4, 0, 0), current.Position(source));
                }
                fields.WriteAutomationField("properties/0", "7, 8, 9");
                if (savedAs)
                {
                    Assert.Empty((await current.SaveAsync(token: token)).Errors);
                    Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(archivePath, token));
                    Assert.NotEqual(archiveBytes, await File.ReadAllBytesAsync(copyPath, token));
                    continue;
                }
                Assert.NotSame(replaced, current);
                Assert.Equal(new Vector3(7, 8, 9), current.Position(source)); Assert.Equal(original, replaced.Position(source));
                Assert.True(current.IsDirty); Assert.True(doc.IsDirty);
                Assert.Throws<InvalidOperationException>(() => replaced.MoveTo(source, new(4, 5, 6)));
                doc.UndoScene(false); Assert.Equal(original, current.Position(source));
                doc.UndoScene(false); Assert.Same(opened, models.Current);
                Assert.False(doc.CanUndoScene); Assert.False(doc.IsDirty);
            }
            finally { Directory.Delete(folder, true); }
        }
    }
    // Model steps the session retires leave the shared history with them, and each new edit ends every session's redo:
    // no Undo/Redo step does nothing or brings back an abandoned import.
    private static async Task RetiredModelHistory()
    {
        string folder = Path.Combine(Path.GetTempPath(), "zstudio-retired-scene-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = deadline.Token;
            string worldPath = Path.Combine(folder, "gamez.zbd");
            await File.WriteAllBytesAsync(worldPath, ModelFixture.GameZ(16), token);
            // Every import retains another 512 KiB pack, so the small allowance keeps only a few undo steps.
            byte[] texture = new byte[512 * 1024]; ModelFixture.Texture().CopyTo(texture, 0);
            await File.WriteAllBytesAsync(Path.Combine(folder, "texture2.zbd"), texture, token);
            await File.WriteAllBytesAsync(Path.Combine(folder, "zrdr.zbd"), PickupArchive(token), token);
            using var resolver = new AssetResolver(folder);
            var world = await resolver.OpenCachedAsync(worldPath, token);
            var models = new ModelEditSession(world, 9L * 512 * 1024, 4L * 1024 * 1024);
            using var doc = new DocumentModel(new PreparedDocument(world, null, models)); doc.AttachResolver(resolver);
            var placements = await doc.GetPickupEditsAsync(resolver, token); var source = Assert.Single(placements.Records).Source;
            var original = placements.Transform(source); PlacementTransform moved = new(new(1, 2, 3), new(.1f, .2f, .3f));
            placements.TransformTo(source, original with { Position = new(4, 5, 6) });
            List<ModelEditSnapshot> states = [models.Current];
            for (int i = 1; i <= 7; i++) { await Import(i); states.Add(models.Current); }
            doc.UndoScene(false); Assert.Same(states[6], models.Current);
            placements.TransformTo(source, moved);
            Assert.False(models.CanRedo); Assert.False(doc.CanRedoScene);
            int undone = 0;
            for (; doc.CanUndoScene; undone++) Step(false);
            Assert.InRange(undone, 3, 8); // Older model steps retired, but the first pickup edit and the opened file stay reachable.
            Assert.Same(states[0], models.Current); Assert.Equal(original, placements.Transform(source)); Assert.False(doc.IsDirty);
            for (int redone = 0; doc.CanRedoScene; redone++) { Step(true); Assert.True(redone < undone); }
            Assert.Same(states[6], models.Current); Assert.Equal(moved, placements.Transform(source));
            doc.UndoScene(false); Assert.True(placements.CanRedo);
            await Import(8); Assert.False(placements.CanRedo); Assert.False(doc.CanRedoScene);
            async Task Import(int i) => models.Accept(await models.PrepareAsync(new(Convert.ToHexString(SHA256.HashData(models.Current.World.Bytes.Span)), "shell" + i,
                new(1, 1, [64, 128, 32, 255]), new Dictionary<int, ImportedMesh> { [1] = ModelFixture.Mesh }), resolver, token));
            void Step(bool redo)
            {
                var model = models.Current; var transform = placements.Transform(source);
                doc.UndoScene(redo);
                Assert.False(ReferenceEquals(model, models.Current) && transform == placements.Transform(source), "An Undo/Redo step changed nothing.");
            }
        }
        finally { Directory.Delete(folder, true); }
    }
    private static byte[] PickupArchive(CancellationToken token, string x = "0", bool removed = false)
    {
        ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
        ZrdNode F(string value = "0") => ZrdNode.Create(ZrdKind.Float, value);
        var row = A(ZrdNode.Create(ZrdKind.String, "\"HEMORTAR_AMMO\""), ZrdNode.Create(ZrdKind.Int, "1"), A(F(x), F(), F()), A(F(), F(), F()), F());
        byte[] data = ZrdWriter.Write(A(removed ? A() : A(row)), token), archive = new byte[data.Length + 156]; data.CopyTo(archive, 0);
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(data.Length + 4), data.Length);
        Encoding.Latin1.GetBytes("puppies.zrd").CopyTo(archive, data.Length + 8);
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(archive.Length - 4), 1);
        return archive;
    }
}
