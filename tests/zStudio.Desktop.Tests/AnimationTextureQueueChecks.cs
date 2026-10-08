using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Windows;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Rendering;
using Xunit;
using DiffuseMaterial = HelixToolkit.Wpf.SharpDX.DiffuseMaterial;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Runs on the existing STA application, through the real two-material frame/queue path.</summary>
internal static class AnimationTextureQueueChecks
{
    internal static async Task Run()
    {
        await Check("sample", null, accepted: true);
        // Path-prefixed names are valid basename aliases; they never become an OS file path.
        string largeAlias = new string('x', 600_000) + "/sample";
        await Check(largeAlias, null, accepted: true);
        await Check(largeAlias, new string('y', 600_000) + "/unused", accepted: true);
        await Check(new string('z', 1_000_001) + "/sample", null, accepted: false);
    }

    private static async Task Check(string firstName, string? rejectedName, bool accepted)
    {
        var token = TestContext.Current.CancellationToken;
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-animation-texture-queue-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            byte[] bytes = Recoil.Zbd.Tests.ContentFixture.Texture(2, 2, false);
            bytes.AsSpan(64, 32).Clear(); Encoding.ASCII.GetBytes("bar").CopyTo(bytes, 64);
            File.WriteAllBytes(Path.Combine(root, "texture1.zbd"), bytes);
            using var resolver = new AssetResolver(root); using var preview = new SceneViewport();
            List<string> notices = []; preview.Information += notices.Add;
            GameScene scene = new();
            for (int i = 0; i < 2; i++)
            {
                scene.Materials.Add(new() { ["alpha"] = 255, ["texture_index"] = i });
                scene.Textures.Add(new() { ["name"] = i == 0 ? firstName : "bar" });
                scene.Models.Add(new(i, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [],
                    [new(i, 3, [0, 1, 2], [], [], [])], []));
            }
            var context = new AnimationPreviewContext
            {
                Package = new() { Prefix = [], Tail = [] },
                World = new(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue),
                    new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene }
            };
            context.MaterialCycles[0] = new(rejectedName == null ? [firstName] : [firstName, rejectedName], 0, false);
            context.MaterialCycles[1] = new(["bar"], 0, false);
            var frame = new AnimationFrame(0,
                [new(1, 0, 0, Matrix4x4.Identity, true, 1, 0, 0, 0), new(2, 1, 1, Matrix4x4.Identity, true, 1, 0, 0, 0)],
                [], [], [], null, null, default, default, [], [], []);

            // UpdateAnimationFrame queues every cycle member and then its selected
            // member again, before reaching the second material's short valid name.
            await preview.ShowAnimationAsync(context, frame, resolver, false, token);
            for (int i = 0; i < 3; i++) preview.UpdateAnimationFrame(frame);
            var loads = Field<System.Collections.IDictionary>("animationTextureSlots");
            Assert.Equal(accepted ? 2 : 1, loads.Count); Assert.True(loads.Contains("bar"));
            Assert.Equal(accepted, loads.Contains(firstName));
            if (rejectedName != null) Assert.False(loads.Contains(rejectedName));
            Assert.Equal((accepted ? firstName.Length + 1L : 0) + 4, Field<long>("animationTextureNameUnits"));
            var references = Field<System.Collections.IDictionary>("animationTextureReferences");
            Assert.Equal(loads.Count, references.Count);

            var surface = (Viewport3DX)preview.RenderSurface;
            var meshes = surface.Items.OfType<SortingGroupModel3D>().Single().Children.OfType<MeshGeometryModel3D>().ToArray();
            Assert.Equal(2, meshes.Length);
            Assert.Equal(accepted ? Visibility.Visible : Visibility.Collapsed, meshes[0].Visibility);
            Assert.Equal(Visibility.Visible, meshes[1].Visibility);
            Assert.NotNull(((DiffuseMaterial)meshes[1].Material!).DiffuseMap);
            if (accepted) Assert.NotNull(((DiffuseMaterial)meshes[0].Material!).DiffuseMap);
            var limits = notices.Where(n => n.Contains("allowance", StringComparison.Ordinal) || n.Contains("request limit", StringComparison.Ordinal)).ToArray();
            if (accepted && rejectedName == null) Assert.Empty(limits); else Assert.Single(limits);

            if (firstName == "sample")
            {
                // Equal-content references share a live slot and are each remembered once.
                for (int i = 0; i < 8; i++)
                {
                    context.MaterialCycles[0] = new([new string(firstName.AsSpan())], 0, false);
                    preview.UpdateAnimationFrame(frame);
                }
                Assert.Equal(2, loads.Count); Assert.Equal(10, references.Count);
                Assert.Equal(11, Field<long>("animationTextureNameUnits"));
                Assert.Equal(Visibility.Visible, meshes[0].Visibility);
                var stable = frame with { Nodes = [frame.Nodes[0] with { Texture = new string(firstName.AsSpan()) }, frame.Nodes[1]] };
                preview.UpdateAnimationFrame(stable);
                long lookup = Field<long>("animationTextureLookupUnits"), visits = Field<long>("animationTextureCycleMemberVisits");
                for (int i = 0; i < 256; i++) preview.UpdateAnimationFrame(stable);
                Assert.Equal(lookup, Field<long>("animationTextureLookupUnits"));
                Assert.Equal(visits, Field<long>("animationTextureCycleMemberVisits"));
                Assert.Equal(Visibility.Visible, meshes[0].Visibility);
            }
            long cycleVisits = Field<long>("animationTextureCycleMemberVisits"), lookupUnits = Field<long>("animationTextureLookupUnits");
            for (int i = 0; i < 128; i++) preview.UpdateAnimationFrame(frame);
            Assert.Equal(cycleVisits, Field<long>("animationTextureCycleMemberVisits"));
            Assert.Equal(lookupUnits, Field<long>("animationTextureLookupUnits"));
            preview.Clear(); Assert.Empty(references);
            Assert.Equal(0, Field<long>("animationTextureNameUnits")); Assert.Equal(0, Field<long>("animationTextureLookupUnits"));
            T Field<T>(string name) => (T)typeof(SceneViewport).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(preview)!;
        }
        finally { Directory.Delete(root, true); }
    }
}
