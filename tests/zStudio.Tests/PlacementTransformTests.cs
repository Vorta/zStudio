using System.Numerics;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed partial class AnimationTests
{
    [Fact]
    public async Task CombinedPickupTransformPreviewsWithoutHistoryAndSavesOnlyVerifiedScalars()
    {
        var row = PickupRow(yaw: .3f);
        row["children"]![3]!["children"]![0] = CoordinateInt(0);
        await WithMissionArchiveAsync(new()
        {
            ["puppies.zrd"] = Zrd(PickupList(row)),
            ["puppies_easy.zrd"] = Zrd(PickupList(row.DeepClone())),
            ["unrelated.bin"] = [33, 77, 88]
        }, async (world, resolver) =>
        {
            var token = TestContext.Current.CancellationToken;
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
            var record = edits.Records.Single(r => r.Difficulties.Contains(MissionDifficulty.Medium));
            string path = Assert.Single(edits.ArchivePaths);
            byte[] original = edits.EncodeArchive(path);
            var before = edits.Transform(record.Source);
            var after = new PlacementTransform(before.Position + new Vector3(3, -2, 7), new(.125f, .8f, -.2f));
            var preview = edits.PreviewTransform(record.Source, after);
            Assert.Equal(2, preview.Count); Assert.All(preview.Values, t => Assert.Equal(after, t));
            Assert.False(edits.IsDirty); Assert.False(edits.CanUndo); Assert.Equal(original, edits.EncodeArchive(path));
            Assert.True(edits.TransformTo(record.Source, after)); Assert.False(edits.TransformTo(record.Source, after));
            byte[] encoded = edits.EncodeArchive(path);
            var archive = FormatRegistry.Default.OpenBytes(path, original);
            HashSet<long> permitted = [];
            foreach (var source in preview.Keys)
            {
                var asset = archive.Assets.Single(a => a.Index == source.AssetIndex);
                var placement = ZrdDecoder.Read(archive.Slice(asset.Offset, asset.Length), token).Children[0].Children[source.RecordIndex];
                foreach (var scalar in placement.Children[2].Children.Concat(placement.Children[3].Children))
                    for (int i = 0; i < 8; i++) permitted.Add(asset.Offset + scalar.SourceOffset + i);
            }
            Assert.All(Enumerable.Range(0, original.Length).Where(i => original[i] != encoded[i]), i => Assert.Contains((long)i, permitted));
            edits.Undo(); Assert.False(edits.CanUndo); Assert.Equal(original, edits.EncodeArchive(path));
            edits.Redo(); Assert.All(preview.Keys, s => Assert.Equal(after, edits.Transform(s)));
            Assert.Empty((await edits.SaveAsync(token: token)).Errors); Assert.False(edits.IsDirty);
            using var fresh = new AssetResolver(Path.GetDirectoryName(path)!);
            var reopened = await PickupPlacementEditSession.LoadAsync(world.Path, fresh, token);
            Assert.All(reopened.Records, r => Assert.Equal(after, reopened.Transform(r.Source)));
            edits.Undo(); Assert.True(edits.IsDirty); Assert.Equal(original, edits.EncodeArchive(path));
        });
    }

    [Fact]
    public async Task HeadingUsesNativeDegreesAndRejectsUnsupportedAxesWithoutAnEdit()
    {
        await WithMissionArchiveAsync(new()
        {
            ["vehicle.zrd"] = Zrd(Arr(Str("animated"), Arr())),
            ["aiv.zrd"] = Zrd(Arr(Str("animated_01"), Spawn(1, 2, 3, 45))),
            ["aiv_hard.zrd"] = Zrd(Arr(Str("animated_02"), Spawn(1, 2, 3, 45))),
            ["net_01.zrd"] = Zrd(Arr(Str("node_00"), AiCoordinateNode()))
        }, async (world, resolver) =>
        {
            var token = TestContext.Current.CancellationToken;
            var edits = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token); edits.BindCoordinateTemplates(world.Scene!);
            var tank = edits.OtherCoordinates.Single(r => r.Kind == "tank" && r.Difficulties.Contains(MissionDifficulty.Medium));
            var ai = edits.OtherCoordinates.Single(r => r.Kind == "ai");
            var before = edits.Transform(tank.Source);
            Assert.Equal(PlacementRotationKind.HeadingDegrees, edits.RotationKind(tank.Source));
            Assert.Equal(PlacementRotationKind.None, edits.RotationKind(ai.Source));
            Assert.Throws<InvalidDataException>(() => edits.TransformTo(ai.Source, edits.Transform(ai.Source) with { Rotation = Vector3.UnitY }));
            Assert.Throws<InvalidDataException>(() => edits.TransformTo(tank.Source, before with { Rotation = Vector3.UnitX }));
            Assert.Throws<InvalidDataException>(() => edits.TransformTo(tank.Source, before with { Rotation = new(0, float.NaN, 0) }));
            Assert.False(edits.CanUndo);
            var after = before with { Position = new(9, 8, 7), Rotation = new(0, 132.5f, 0) };
            edits.TransformTo(tank.Source, after);
            Assert.All(edits.Scope(tank.Source).Sources, s => Assert.Equal(after, edits.Transform(s)));
            Assert.Empty((await edits.SaveAsync(token: token)).Errors);
            using var fresh = new AssetResolver(Path.GetDirectoryName(world.Path)!);
            var reopened = await PickupPlacementEditSession.LoadAsync(world.Path, fresh, token);
            Assert.All(reopened.OtherCoordinates.Where(r => r.Kind == "tank"), r => Assert.Equal(after, reopened.Transform(r.Source)));
            edits.Undo(); Assert.Equal(before, edits.Transform(tank.Source)); Assert.False(edits.CanUndo);
        });
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1.5707963f, .3f, -.4f)]
    [InlineData(-1.5707963f, -.8f, .7f)]
    [InlineData(1.5707f, 1.2f, -.6f)]
    [InlineData(.2f, -.7f, 1.1f)]
    public void RotationHandlesPreserveOrientationAtPolesAndKeepScaledChildrenAtPlacementPivot(float x, float y, float z)
    {
        var before = new PlacementTransform(new(100, -20, 73), new(x, y, z));
        const PlacementRotationKind kind = PlacementRotationKind.EulerRadians;
        var basis = Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(kind, before.Rotation));
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
        {
            var after = before.RotateWorld(kind, axis, .35f);
            var expected = basis * Matrix4x4.CreateFromAxisAngle(axis, .35f);
            var actual = Matrix4x4.CreateFromQuaternion(PlacementTransform.Orientation(kind, after.Rotation));
            foreach (var v in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
                Assert.True(Vector3.Distance(Vector3.TransformNormal(v, expected), Vector3.TransformNormal(v, actual)) < .0001f);
            var authored = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(7, 8, 9) * basis * Matrix4x4.CreateTranslation(before.Position);
            var transformed = authored * after.DeltaFrom(before, kind);
            var desired = Matrix4x4.CreateScale(2, 3, 4) * Matrix4x4.CreateTranslation(7, 8, 9) * expected * Matrix4x4.CreateTranslation(before.Position);
            Assert.True(Vector3.Distance(Vector3.Transform(new(3, 4, 5), transformed), Vector3.Transform(new(3, 4, 5), desired)) < .0001f);
            Assert.Equal(Matrix4x4.Identity, after.DeltaFrom(after, kind)); // Refreshed baselines do not rotate twice.
        }
        Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 3), (before with { Position = before.Position + new Vector3(1, 2, 3) }).DeltaFrom(before, kind));
    }
}
