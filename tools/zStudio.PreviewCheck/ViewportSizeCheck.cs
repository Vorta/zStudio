using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Threading;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Rendering;

internal static class ViewportSizeCheck
{
    public static int Run(string root)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; int result = 0;
        app.Startup += async (_, _) =>
        {
            using var preview = new SceneViewport();
            var window = new Window { Content = preview, Width = 900, Height = 650, Left = -12000, ShowInTaskbar = false };
            bool busy = true;
            // Reproduce a large scene's render queue without relying on GPU speed.
            // Normal work and actual rendering remain available; Background cannot run.
            void RenderQueue() { if (busy) window.Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)RenderQueue); }
            try
            {
                window.Show(); window.UpdateLayout(); RenderQueue();
                using var resolver = new AssetResolver(Path.GetFullPath(root));
                GameScene scene = new(); scene.Materials.Add(new() { ["alpha"] = 255 });
                var model = new GameModel(0, [new(-1,-1,0), new(1,-1,0), new(0,1,0)], [], [], [new(0, 0, [0,1,2], [], [], [])], []);
                scene.Models.Add(model);
                scene.Nodes.Add(new(0, "fixture", "object3d", 0, [], [], new() { ["flags"] = 4 }, []));
                var document = new ZbdDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene };
                await preview.ShowAsync(document, new() { Id = new("fixture", AssetKind.Model, 0), Name = "fixture", Content = model }, resolver, null, 0, default);
                var viewport = (Viewport3DX)preview.RenderSurface;
                await Check("initial load");
                var camera = preview.CaptureView();
                window.Width = 760; window.Height = 820; window.UpdateLayout(); await Check("portrait resize");
                window.Width = 1100; window.Height = 540; window.UpdateLayout(); await Check("wide resize");
                if (preview.CaptureView() != camera) throw new InvalidDataException("Resizing changed the camera pose.");
                Console.WriteLine("PASS: native D3D buffer follows initial layout and portrait/wide resizes while Background is starved; camera retained.");

                async Task Check(string label)
                {
                    await Task.Delay(180);
                    // Inspect the presented GPU surface directly; never force
                    // rendering, capture scaling, or resizing to hide a stale size.
                    var surface = (SharpDX.Direct3D11.Texture2D)viewport.RenderHost!.RenderBuffer!.BackBuffer!.Resource!;
                    int width = (int)((int)viewport.ActualWidth * viewport.RenderHost!.DpiScale);
                    int height = (int)((int)viewport.ActualHeight * viewport.RenderHost.DpiScale);
                    if (Math.Abs(surface.Description.Width - width) > 1 || Math.Abs(surface.Description.Height - height) > 1)
                        throw new InvalidDataException($"{label}: buffer {surface.Description.Width} × {surface.Description.Height}, expected {width} × {height}.");
                    Console.WriteLine($"{label}: {surface.Description.Width} × {surface.Description.Height} presented pixels at DPI scale {viewport.RenderHost.DpiScale}.");
                }
            }
            catch (Exception ex) { result = 1; Console.Error.WriteLine(ex); }
            finally { busy = false; window.Close(); app.Shutdown(result); }
        };
        app.Run(); return result;
    }
}
