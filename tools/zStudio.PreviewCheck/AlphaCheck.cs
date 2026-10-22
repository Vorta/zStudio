using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Desktop;
using zStudio.Rendering;

internal static class AlphaCheck
{
    public static int Run(string root)
    {
        string settings = zStudio.Desktop.StudioSettings.SettingsPath;
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            using var resolver = new AssetResolver(Path.GetFullPath(root)); using var preview = new SceneViewport();
            try
            {
                window.Content = preview;
                window.UpdateLayout(); await Task.Delay(400);
                var scene = new GameScene();
                scene.Materials.Add(new JsonObject { ["alpha"] = 255 });
                scene.Models.Add(new(0, [new(-1,-1,0), new(1,-1,0), new(1,1,0), new(-1,1,0)], [], [], [new(0, 4, [0,1,2,3], [], [Vector2.Zero, Vector2.UnitX, Vector2.One, Vector2.UnitY], [])], []));
                var context = new AnimationPreviewContext { Package = new() { Prefix = [], Tail = [] }, World = new AssetDocument("alpha", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, ""), ReadOnlyMemory<byte>.Empty) { Scene = scene } };
                AnimationFrame frame = new(0, [new(1,0,0,Matrix4x4.Identity,true,1,-1,0,0), new(2,1,0,Matrix4x4.CreateTranslation(0,0,-.1f),true,1,-1,0,0)], [], [], [], null,null,Vector4.Zero,Vector4.Zero,[],[],[]);
                await preview.ShowAnimationAsync(context, frame, resolver, false, CancellationToken.None);
                preview.SetNavigationAids(false, false);
                var camera = preview.DebugCamera; camera.Position = new(0,0,3); camera.LookDirection = new(0,0,-3); camera.UpDirection = new(0,1,0);
                var surfaces = preview.EnumerateRenderMeshes().Where(m => m.Role == SceneViewport.RenderMeshRole.Animation).ToArray();
                foreach (var mesh in surfaces) mesh.IsTransparent = true;
                var front = surfaces[0]; var back = surfaces[1];
                back.SetTexture([0,0,255,128], 1,1);
                // Force the transparent front surface first. Its alpha-zero pixels must
                // leave the depth buffer untouched so the later back surface survives.
                preview.SetTransparentSorting(false);
                front.SetTexture([255,0,0,0], 1,1);
                await Task.Delay(200); var clearPixel = Pixel(preview.RenderImage(128,128));
                if (preview.RenderException != null) throw new InvalidOperationException("Renderer failed.", preview.RenderException);
                if (clearPixel[0] < 140 || clearPixel[2] > 20) throw new InvalidDataException($"Alpha-zero texels mask the surface behind: BGR {string.Join(',',clearPixel)}.");
                preview.SetTransparentSorting(true);
                front.SetTexture([255,0,0,128], 1,1);
                await Task.Delay(200); var halfPixel = Pixel(preview.RenderImage(128,128));
                if (Math.Abs(halfPixel[2]-134)>3 || Math.Abs(halfPixel[1]-8)>3 || Math.Abs(halfPixel[0]-73)>3) throw new InvalidDataException($"Half-alpha composition incorrect: BGR {string.Join(',',halfPixel)}.");
                Console.WriteLine($"PASS: alpha zero preserves rear geometry; half-alpha red over half-alpha blue renders RGB ({halfPixel[2]},{halfPixel[1]},{halfPixel[0]}).");
                var model = scene.Models[0];
                scene.Models[0] = model with { Polygons = [model.Polygons[0] with { Colors = Enumerable.Repeat(new Vector3(127.5f), 4).ToArray() }] };
                await preview.ShowAnimationAsync(context, frame with { Nodes = [frame.Nodes[0]] }, resolver, false, CancellationToken.None);
                camera.Position = new(0,0,3); camera.LookDirection = new(0,0,-3); camera.UpDirection = new(0,1,0);
                var tinted = preview.EnumerateRenderMeshes().Single(m => m.Role == SceneViewport.RenderMeshRole.Animation);
                tinted.SetTexture([255,128,64,255], 1,1);
                await Task.Delay(200); var tintPixel = Pixel(preview.RenderImage(128,128));
                if (Math.Abs(tintPixel[2]-128)>3 || Math.Abs(tintPixel[1]-64)>3 || Math.Abs(tintPixel[0]-32)>3)
                    throw new InvalidDataException($"Vertex colors did not modulate the texture: BGR {string.Join(',',tintPixel)}.");
                Console.WriteLine("PASS: authored vertex tint multiplies diffuse texture RGB without replacing the texture.");
                // Exercise the actual material bindings from oblique views,
                // including a texture paired with the shaded-mode material flag.
                foreach (bool unlit in new[] { true, false })
                {
                    tinted.MaterialUnlit = unlit;
                    camera.Position = new(3 * Math.Sqrt(3), 0, 3); camera.LookDirection = new(-3 * Math.Sqrt(3), 0, -3);
                    await Task.Delay(200); var angledPixel = Pixel(preview.RenderImage(128,128));
                    if (Math.Abs(angledPixel[2]-128)>3 || Math.Abs(angledPixel[1]-64)>3 || Math.Abs(angledPixel[0]-32)>3)
                        throw new InvalidDataException($"Camera angle changed textured vertex tint (unlit={unlit}): BGR {string.Join(',',angledPixel)}.");
                }
                tinted.MaterialUnlit = true;
                camera.Position = new(0,0,3); camera.LookDirection = new(0,0,-3);
                Console.WriteLine("PASS: textured vertex RGB stays unchanged at oblique camera angles with either material lighting flag.");
                var fogFrame = frame with { Nodes = [frame.Nodes[0]], Fog = new(true, Vector3.UnitZ, 3, 6) };
                preview.UpdateAnimationFrame(fogFrame);
                tinted.SetTexture([255,128,64,255], 1,1);
                await Task.Delay(200); var nearPixel = Pixel(preview.CapturePresentedFrame());
                camera.Position = new(0,0,6); camera.LookDirection = new(0,0,-6);
                await Task.Delay(200); var fogPixel = Pixel(preview.CapturePresentedFrame());
                if (Math.Abs(nearPixel[2]-128)>3 || fogPixel[2]>3 || fogPixel[1]>3 || Math.Abs(fogPixel[0]-32)>3)
                    throw new InvalidDataException($"Paused camera fog did not retint the presented geometry: near BGR {string.Join(',',nearPixel)}; far BGR {string.Join(',',fogPixel)}.");
                camera.Position = new(0,0,3); camera.LookDirection = new(0,0,-3);
                await Task.Delay(200); var restored = Pixel(preview.CapturePresentedFrame());
                if (!restored.SequenceEqual(nearPixel)) throw new InvalidDataException("Paused camera fog did not restore the authored tint.");
                Console.WriteLine("PASS: paused camera fog retints textured vertex colors and restores them without advancing animation or forcing rendering.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static byte[] Pixel(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image,PixelFormats.Bgra32,null,0); byte[] pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(bitmap.PixelWidth/2,bitmap.PixelHeight/2,1,1),pixel,4,0); return pixel;
    }
}
