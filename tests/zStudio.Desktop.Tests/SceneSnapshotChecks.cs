using System.IO;
using System.Buffers.Binary;
using System.Text;
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
                ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
                ZrdNode F() => ZrdNode.Create(ZrdKind.Float);
                var row = A(ZrdNode.Create(ZrdKind.String, "\"HEMORTAR_AMMO\""), ZrdNode.Create(ZrdKind.Int, "1"), A(F(), F(), F()), A(F(), F(), F()), F());
                await File.WriteAllBytesAsync(worldPath, ModelFixture.GameZ(), token);
                await File.WriteAllBytesAsync(texturePath, ModelFixture.Texture(), token);
                byte[] data = ZrdWriter.Write(A(A(row)), token), archiveBytes = new byte[data.Length + 156]; data.CopyTo(archiveBytes, 0);
                BinaryPrimitives.WriteInt32LittleEndian(archiveBytes.AsSpan(data.Length + 4), data.Length);
                Encoding.Latin1.GetBytes("puppies.zrd").CopyTo(archiveBytes, data.Length + 8);
                BinaryPrimitives.WriteInt32LittleEndian(archiveBytes.AsSpan(archiveBytes.Length - 8), 1);
                BinaryPrimitives.WriteInt32LittleEndian(archiveBytes.AsSpan(archiveBytes.Length - 4), 1);
                await File.WriteAllBytesAsync(archivePath, archiveBytes, token);
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
    }
}
