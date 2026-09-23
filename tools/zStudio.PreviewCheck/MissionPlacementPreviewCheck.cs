using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Media.Imaging;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

internal static class MissionPlacementPreviewCheck
{
    public static int Run(string rootArg)
    {
        string root = Path.GetFullPath(rootArg), output = Path.Combine(Path.GetTempPath(), "zbd-placements-ui-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? saved = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.Left = -12000; window.Width = 1200; window.Height = 800;
            using AssetResolver resolver = new(root); using SceneViewport preview = new(); window.Content = preview;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            try
            {
                var document = await resolver.OpenCachedAsync(Path.Combine(root, "m1", "anim.zbd"), timeout.Token);
                var context = await AnimationPreviewContext.LoadAsync(document.Animations!, document.Path, resolver, token: timeout.Token);
                var worldAsset = context.World.Assets.First(a => a.Kind == AssetKind.World);
                var viewport = (Viewport3DX)preview.Content; viewport.IsInertiaEnabled = false; viewport.ShowViewCube = viewport.ShowCoordinateSystem = false;
                var camera = (HCamera)viewport.Camera!;
                var scene = context.Scene; var baseline = SceneBuilder.Assemble(scene).Placements;
                var sky = new HashSet<int>(); foreach (var horizon in context.Mission!.Horizons) sky.UnionWith(context.Descendants(horizon.Root));
                var withoutPickups = baseline.Where(p => !context.Mission.Actors.Where(a => a.Pickup != null).Any(a => context.Descendants(a.Root).Contains(p.NodeIndex))).ToArray();
                int pickupTriangles = Triangles(baseline) - Triangles(withoutPickups); Require(pickupTriangles > 0, "No pickup geometry in baseline");
                foreach (var difficulty in new[] { MissionDifficulty.Medium, MissionDifficulty.Hard, MissionDifficulty.Easy, MissionDifficulty.Medium })
                {
                    context = await context.WithDifficultyAsync(resolver, difficulty, timeout.Token); scene = context.Scene;
                    await preview.ShowAsync(context.World, worldAsset, resolver, null, 0, timeout.Token, false, context.Mission);
                    Require(context.Mission!.Actors.Count(a => a.Pickup != null) == 64, "Reload lost or duplicated pickups");
                    Require(RenderedTriangles() == Triangles(SceneBuilder.Assemble(scene).Placements.Where(p => !sky.Contains(p.NodeIndex))), "Whole world rendered triangle count differs from the initialized scene");
                }
                var pickup = context.Mission!.Actors.First(a => a.Pickup?.Source.RecordIndex == 0);
                Vector3 pickupPosition = SceneViewport.WorldTransform(scene, pickup.Root).Translation;
                Aim(pickupPosition + new Vector3(0, 1, 0), new(12, 10, 16)); await Capture("whole-world-pickups");
                var turret = scene.Nodes.First(n => n.Name == "tur_102");
                int destroyed = context.FindBelow(turret.Index, "destroyed"), healthy = context.FindBelow(turret.Index, "healthy");
                Require((scene.Nodes[healthy].Metadata.UInt("flags") & 4) != 0 && (scene.Nodes[destroyed].Metadata.UInt("flags") & 4) == 0, "Turret has overlapping variants");
                var turretNodes = context.Descendants(turret.Index).ToHashSet();
                var points = SceneBuilder.Assemble(scene).Placements.Where(p => turretNodes.Contains(p.NodeIndex))
                    .SelectMany(p => scene.Models[p.ModelIndex].Vertices.Select(v => Vector3.Transform(v, p.Transform))).ToArray();
                Vector3 min = points.Aggregate(Vector3.Min), max = points.Aggregate(Vector3.Max), center = (min + max) * .5f;
                Vector3 front = Vector3.Normalize(Vector3.TransformNormal(-Vector3.UnitZ, SceneViewport.WorldTransform(scene, turret.Index)));
                Aim(center, front * 22 + Vector3.UnitY * 14); await Capture("whole-world-turret");
                int destruction = context.Package.Entries.First(e => e.Name == "destroy_turret").Index; context.RootOverrides[destruction] = turret.Index;
                var destructionPlayer = new AnimationPlayer(context, destruction);
                await preview.ShowAnimationAsync(context, destructionPlayer.Frame(), resolver, false, timeout.Token, showHorizon: false);
                await Capture("turret-healthy");
                var destroyedFrame = destructionPlayer.AdvanceTo(.1, true, timeout.Token);
                Require(destroyedFrame.Nodes.Any(n => n.Visible && context.Descendants(destroyed).Contains(n.SourceNode)), "Destruction animation cannot reveal destroyed geometry");
                preview.UpdateAnimationFrame(destroyedFrame); await Capture("turret-destruction");

                int animation = context.Package.Entries.First(e => e.Name == "vtol_destruction1").Index;
                var frame = new AnimationPlayer(context, animation).Frame();
                await preview.ShowAnimationAsync(context, frame, resolver, true, timeout.Token, showHorizon: false);
                var replaced = frame.Nodes.Where(n => n.Id >> 32 == 0).Select(n => n.SourceNode).ToHashSet();
                int expected = Triangles(SceneBuilder.Assemble(scene).Placements.Where(p => !sky.Contains(p.NodeIndex) && !replaced.Contains(p.NodeIndex)))
                    + frame.Nodes.Where(n => n.Visible && !sky.Contains(n.SourceNode)).Sum(n => ModelTriangles(n.Model));
                Require(RenderedTriangles() == expected, $"Animation Map triangle count {RenderedTriangles()} != {expected}");
                Aim(pickupPosition + new Vector3(0, 1, 0), new(12, 10, 16)); await Capture("animation-map-pickups");
                await preview.ShowAnimationAsync(context, frame, resolver, false, timeout.Token, showHorizon: false);
                Require(RenderedTriangles() == frame.Nodes.Where(n => n.Visible && !sky.Contains(n.SourceNode)).Sum(n => ModelTriangles(n.Model)), "Map off retained background geometry");
                await preview.ShowAnimationAsync(context, frame, resolver, true, timeout.Token, showHorizon: false);
                Require(RenderedTriangles() == expected, "Map reload duplicated geometry");
                Console.WriteLine($"PASS: Whole world and animation Map render initialized geometry, including {pickupTriangles} pickup triangles; 64 pickups survive difficulty round trips; turret healthy-only state; Map off/on has no duplicate geometry.");
                Console.WriteLine("Screenshots: " + output);

                int ModelTriangles(int model) => GeometryBuilder.Build(scene.Models[model]).Sum(p => p.Indices.Length / 3);
                int Triangles(IEnumerable<ScenePlacement> placements) => placements.Sum(p => ModelTriangles(p.ModelIndex));
                int RenderedTriangles() => viewport.Items.OfType<MeshGeometryModel3D>().Concat(viewport.Items.OfType<SortingGroupModel3D>().SelectMany(g => g.Children.OfType<MeshGeometryModel3D>()))
                    .Where(m => m.Visibility == Visibility.Visible).Sum(m => m.Geometry!.Indices!.Count / 3 * (m.Instances?.Count ?? 1));
                void Aim(Vector3 target, Vector3 offset)
                { camera.Position = new(target.X + offset.X, target.Y + offset.Y, target.Z + offset.Z); camera.LookDirection = new(-offset.X, -offset.Y, -offset.Z); camera.UpDirection = new(0, 1, 0); }
                async Task Capture(string name)
                {
                    await Task.Delay(300, timeout.Token); var bitmap = preview.RenderImage(1100, 720);
                    PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (saved != null) File.WriteAllBytes(settings, saved); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
}
