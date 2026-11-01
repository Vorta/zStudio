using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using Xunit;

namespace zStudio.Tests;

public sealed partial class AnimationTests
{
    // A normal source export can put the same member in common and mission archives. The compiled preview and
    // placement store must agree on the runtime winner while retaining the shadowed AIV records for inspection.
    [Theory]
    [InlineData("common")]
    [InlineData("mission")]
    [InlineData("broader")]
    public async Task CommonResourcesWinCollisionsAndMissionOnlyVariantsRemainAvailable(string rootScope)
    {
        string root = Path.Combine(Path.GetTempPath(), "zbd-resource-order-" + Guid.NewGuid().ToString("N"));
        string commonDirectory = Path.Combine(root, "zbd");
        string mission = Directory.CreateDirectory(Path.Combine(commonDirectory, "m1")).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(commonDirectory, "image.zbd"), ModelFixture.Texture());
            string common = Path.Combine(commonDirectory, "zrdr.zbd"), local = Path.Combine(mission, "zrdr.zbd");
            File.WriteAllBytes(common, ResourceReaderTests.Archive(
                ("aiv.zrd", Zrd(Arr(Str("animated_01"), Spawn(10, 2, 3, 0), Str("other_01"), Spawn(20, 2, 3, 0)))),
                ("vehicle.zrd", Zrd(Arr(Str("animated"), Arr()))),
                ("puppies.zrd", Zrd(PickupList(PickupRow()))),
                ("net_01.zrd", Zrd(Arr(Str("node_00"), AiCoordinateNode(1))))));
            var localPickup = PickupRow("FREON_AMMO"); localPickup["children"]![2]!["children"]![0]!["value"] = 112f;
            File.WriteAllBytes(local, ResourceReaderTests.Archive(
                ("aiv.zrd", Zrd(Arr(Str("animated_03"), Spawn(90, 2, 3, 0)))),
                ("vehicle.zrd", Zrd(Arr(Str("other"), Arr()))),
                ("puppies.zrd", Zrd(PickupList(localPickup))),
                ("aiv_easy.zrd", Zrd(Arr(Str("other_02"), Spawn(40, 2, 3, 0)))),
                ("vehicle_easy.zrd", Zrd(Arr(Str("other"), Arr()))),
                ("puppies_easy.zrd", Zrd(PickupList(localPickup.DeepClone()))),
                ("net_01.zrd", Zrd(Arr(Str("node_01"), AiCoordinateNode(91)))),
                ("net_02.zrd", Zrd(Arr(Str("node_02"), AiCoordinateNode(92))))));
            var source = (AssetDocument)MissionFixture(false).World;
            var scene = source.Scene!;
            AddTemplate("other"); AddTemplate("pu001", pickup: true); AddTemplate("pu003", pickup: true);
            var world = new AssetDocument(Path.Combine(mission, "gamez.zbd"), source.Stamp, source.Probe, source.Bytes) { Scene = scene };
            using AssetResolver resolver = new(rootScope switch { "mission" => mission, "broader" => root, _ => commonDirectory });
            var token = TestContext.Current.CancellationToken;
            var medium = await MissionSceneLoader.LoadAsync(world, resolver, token: token);
            var easy = await MissionSceneLoader.LoadAsync(world, resolver, token: token, difficulty: MissionDifficulty.Easy);
            var hard = await MissionSceneLoader.LoadAsync(world, resolver, token: token, difficulty: MissionDifficulty.Hard);

            var commonTank = Assert.Single(medium.Actors, a => a.CoordinateSource != null);
            Assert.Equal("animated_01", commonTank.Name); Assert.Equal(new Vector3(10, 2, 3), commonTank.PlacementPosition);
            Assert.Equal(common, commonTank.CoordinateSource!.ArchivePath, ignoreCase: true);
            var commonPickup = Assert.Single(medium.Actors, a => a.Pickup != null).Pickup!;
            Assert.Equal("HEMORTAR_AMMO", commonPickup.LogicalName); Assert.Equal(12, commonPickup.Position.X);
            Assert.Equal(common, commonPickup.Source.ArchivePath, ignoreCase: true);
            var easyTank = Assert.Single(easy.Actors, a => a.CoordinateSource != null);
            Assert.Equal("other_02", easyTank.Name); Assert.Equal(new Vector3(40, 2, 3), easyTank.PlacementPosition);
            Assert.Equal(local, easyTank.CoordinateSource!.ArchivePath, ignoreCase: true);
            var easyPickup = Assert.Single(easy.Actors, a => a.Pickup != null).Pickup!;
            Assert.Equal("FREON_AMMO", easyPickup.LogicalName); Assert.Equal(112, easyPickup.Position.X);
            Assert.Equal(commonTank.CoordinateSource, Assert.Single(hard.Actors, a => a.CoordinateSource != null).CoordinateSource);
            Assert.Equal(commonPickup.Source, Assert.Single(hard.Actors, a => a.Pickup != null).Pickup!.Source);
            var network = Assert.Single(medium.AiNetworks.Networks, n => n.Member.Equals("net_01.zrd", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(common, network.Archive, ignoreCase: true);
            Assert.Equal(0, Assert.Single(network.Nodes).Index); Assert.Equal(1, network.Nodes[0].Position.X);
            Assert.Equal(92, Assert.Single(Assert.Single(medium.AiNetworks.Networks, n => n.Member == "net_02.zrd").Nodes).Position.X);
            // Direct inspection continues to show both members with distinct archive identities.
            var commonArchive = await resolver.OpenCachedAsync(common, token);
            var localArchive = await resolver.OpenCachedAsync(local, token);
            var rawNetworks = MissionAiNetworks.Read(new[] { commonArchive, localArchive }
                .SelectMany(a => a.Assets.Where(m => m.Name == "net_01.zrd").Select(m => (a, m))), token);
            Assert.Equal(2, rawNetworks.Networks.Count); Assert.Equal(2, rawNetworks.Networks.Select(n => n.Id).Distinct().Count());

            var placements = await PickupPlacementEditSession.LoadAsync(world.Path, resolver, token);
            placements.BindCoordinateTemplates(scene);
            var defaultPickup = Assert.Single(placements.Records, r => r.Difficulties.Contains(MissionDifficulty.Medium));
            Assert.Equal(commonPickup.Source, defaultPickup.Source); Assert.Equal("HEMORTAR_AMMO", defaultPickup.Type);
            Assert.Contains(MissionDifficulty.Hard, defaultPickup.Difficulties);
            Assert.Equal(easyPickup.Source, Assert.Single(placements.Records, r => r.Difficulties.Contains(MissionDifficulty.Easy)).Source);
            var defaultTank = Assert.IsType<MissionCoordinateRecord>(placements.Coordinate(commonTank.CoordinateSource!));
            Assert.Equal(new Vector3(10, 2, 3), defaultTank.OriginalPosition);
            Assert.Equal(new[] { MissionDifficulty.Medium, MissionDifficulty.Hard }, defaultTank.Difficulties);
            var shadow = Assert.Single(placements.OtherCoordinates, r => r.Source.ArchivePath.Equals(local, StringComparison.OrdinalIgnoreCase) && r.Source.ResourceName == "AIV.ZRD");
            Assert.Equal(new Vector3(90, 2, 3), shadow.OriginalPosition); Assert.Empty(shadow.Difficulties);
            Assert.Contains("Read-only: shadowed", placements.Scope(shadow.Source).Description);
            Assert.Throws<InvalidDataException>(() => placements.PreviewTransform(shadow.Source, new(Vector3.Zero, shadow.Rotation)));

            void AddTemplate(string name, bool pickup = false)
            {
                int index = scene.Nodes.Count; var template = scene.Nodes[0];
                scene.Nodes.Add(template with { Index = index, Name = name, Parents = [], Children = pickup ? [index + 1] : [],
                    Data = (JsonObject)template.Data.DeepClone(), Metadata = (JsonObject)template.Metadata.DeepClone() });
                if (pickup) scene.Nodes.Add(template with { Index = index + 1, Name = "bvol", Parents = [index], Children = [],
                    Data = (JsonObject)template.Data.DeepClone(), Metadata = (JsonObject)template.Metadata.DeepClone() });
            }
        }
        finally { Directory.Delete(root, true); }
    }

    // Whole-member precedence matters even when every template/alias name differs between the two members.
    [Theory]
    [InlineData("common")]
    [InlineData("mission")]
    [InlineData("broader")]
    public async Task CommonAnimationDefinitionsHideWholeMissionMembersAndAbsentCommonFallsBack(string rootScope)
    {
        string root = Path.Combine(Path.GetTempPath(), "zbd-animation-resource-order-" + Guid.NewGuid().ToString("N"));
        string commonDirectory = Path.Combine(root, "zbd");
        string mission = Directory.CreateDirectory(Path.Combine(commonDirectory, "m1")).FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(commonDirectory, "image.zbd"), ModelFixture.Texture());
            string common = Path.Combine(commonDirectory, "zrdr.zbd"), local = Path.Combine(mission, "zrdr.zbd");
            File.WriteAllBytes(common, ResourceReaderTests.Archive(
                ("EFFECTS.ZRD", Effect("common_effect")), ("SOUNDS.ZRD", Zrd(Arr(Str("common_alias"), Str("physical.wav"))))));
            byte[] wave = new byte[52];
            using (var writer = new BinaryWriter(new MemoryStream(wave)))
            {
                writer.Write("RIFF"u8); writer.Write(44); writer.Write("WAVEfmt "u8); writer.Write(16);
                writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(8000);
                writer.Write((short)1); writer.Write((short)8); writer.Write("data"u8); writer.Write(8);
            }
            File.WriteAllBytes(local, ResourceReaderTests.Archive(
                ("effects.zrd", Effect("mission_effect")), ("sounds.zrd", Zrd(Arr(Str("mission_alias"), Str("physical.wav")))),
                ("physical.wav", wave)));
            string world = Path.Combine(mission, "gamez.zbd"); File.WriteAllBytes(world, ModelFixture.GameZ());
            var package = new AnimationPackage { Prefix = Fixture().Prefix, Tail = [] };
            using AssetResolver resolver = new(rootScope switch { "mission" => mission, "broader" => root, _ => commonDirectory });
            var token = TestContext.Current.CancellationToken;
            var commonContext = await Load();
            Assert.Equal("common_effect", Assert.Single(commonContext.Effects).Key);
            Assert.True(commonContext.Sounds.ContainsKey("common_alias")); Assert.False(commonContext.Sounds.ContainsKey("mission_alias"));
            File.Delete(common);
            var localContext = await Load();
            Assert.Equal("mission_effect", Assert.Single(localContext.Effects).Key);
            Assert.True(localContext.Sounds.ContainsKey("mission_alias")); Assert.False(localContext.Sounds.ContainsKey("common_alias"));

            Task<AnimationPreviewContext> Load() => AnimationPreviewContext.LoadAsync(package, Path.Combine(mission, "anim.zbd"), resolver, world, token);
        }
        finally { Directory.Delete(root, true); }

        static byte[] Effect(string name) => Zrd(Arr(Str("root"), Str("NAME"), Arr(Str(name)), Str("MAPS"), Arr(Str("texture.tif"))));
    }
}
