using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using HelixToolkit.SharpDX;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;
using HitTestResult = HelixToolkit.SharpDX.HitTestResult;

internal static class PickupEditorCheck
{
    public static int Run(string rootArg)
    {
        string root = Path.GetFullPath(rootArg);
        string output = Path.Combine(Path.GetTempPath(), "zstudio-pickup-editor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.Width = 1560; window.Height = 960; window.Left = -12000;
            ((ColumnDefinition)window.FindName("PropertiesColumn")).Width = new GridLength(300);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5)); var token = timeout.Token;
            try
            {
                window.ViewModel.Difficulty = MissionDifficulty.Medium;
                await window.ViewModel.OpenRootAsync(root);
                var doc = await window.ViewModel.OpenFileAsync(Path.Combine(root, "m1", "gamez.zbd")) ?? throw new InvalidDataException("Missing m1 world");
                var scene = await Ready(MissionDifficulty.Medium);
                var edits = doc.PickupEdits ?? throw new InvalidDataException("Missing pickup editor");
                var viewport = (Viewport3DX)scene.Content; var camera = (HCamera)viewport.Camera!;
                byte[] original = await File.ReadAllBytesAsync(Path.Combine(root, "m1", "zrdr.zbd"), token);
                byte[] worldHash = SHA256.HashData(doc.Document.Bytes.Span);
                var actor = scene.Mission!.Actors.Single(a => a.Pickup?.Source.RecordIndex == 49);
                var pickup = actor.Pickup!; Vector3 originalPosition = pickup.Position;
                var lockBox = (CheckBox)window.FindName("PickupLocked"); Require(lockBox.IsChecked == true, "Map did not start locked");
                viewport.IsInertiaEnabled = false;
                var pickupPoints = SceneBuilder.Assemble(scene.Mission.Scene).Placements.Where(p => scene.PickupAt(p.NodeIndex)?.Root == actor.Root)
                    .SelectMany(p => scene.Mission.Scene.Models[p.ModelIndex].Vertices.Select(v => Vector3.Transform(v, p.Transform))).ToArray();
                var pickupCenter = (pickupPoints.Aggregate(Vector3.Min) + pickupPoints.Aggregate(Vector3.Max)) * .5f;
                Aim(pickupCenter, new(4, 3, 5)); await Task.Delay(300, token);
                await Capture("initial");
                var point = viewport.Project(new Point3D(pickupCenter.X, pickupCenter.Y, pickupCenter.Z));
                HitTestResult? picked = null; Point pickedPoint = default;
                for (int y = -36; y <= 36 && picked == null; y += 2)
                    for (int x = -36; x <= 36 && picked == null; x += 2)
                    {
                        var p = point + new System.Windows.Vector(x, y);
                        var hit = viewport.FindHits(p).OrderBy(h => h.Distance).FirstOrDefault();
                        if (hit?.ModelHit is MeshGeometryModel3D && NearPickup(hit.PointHit, originalPosition)) { picked = hit; pickedPoint = p; }
                    }
                Require(picked != null, "No visible Nanite surface found by hit testing");
                MouseDown((Element3D)picked!.ModelHit!, picked, pickedPoint);
                Require(scene.SelectedPickupRoot == actor.Root, "Click selected the wrong pickup instance");
                var manipulator = Manipulator(); Require(manipulator.Visibility == Visibility.Collapsed, "Locked map exposed movement controls");
                Require(((ContentControl)window.FindName("PickupProperties")).Visibility == Visibility.Visible, "Pickup coordinates are missing");
                await Capture("locked");
                lockBox.IsChecked = false; await Task.Delay(150, token); manipulator = Manipulator();
                Require(manipulator.Visibility == Visibility.Visible, "Unlock did not reveal arrows");
                await Capture("unlocked");
                var placementPanel = (PickupPlacementPanel)((ContentControl)window.FindName("PickupProperties")).Content;
                var inputs = VisualChildren(placementPanel).OfType<ValueTextBox>().ToArray(); Require(inputs.Length == 3, "Missing XYZ inputs");
                var confirmClose = window.ViewModel.ConfirmDiscardAsync; bool prompted = false;
                window.ViewModel.ConfirmDiscardAsync = _ => { prompted = true; return Task.FromResult(false); };
                inputs[0].Text = (originalPosition.X + .125f).ToString("R", System.Globalization.CultureInfo.CurrentCulture);
                var fileMenu = ((DockPanel)window.Content).Children.OfType<Menu>().Single().Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_File"));
                fileMenu.Items.OfType<MenuItem>().Single(m => Equals(m.Header, "_Close tab")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(prompted && doc.IsDirty && window.ViewModel.Documents.Contains(doc), "Close bypassed pending coordinate edits");
                window.ViewModel.ConfirmDiscardAsync = confirmClose; edits.Undo(); Require(!edits.IsDirty, "Pending close input did not undo cleanly");
                var cameraBefore = scene.CaptureView();
                var othersBefore = scene.Mission.Actors.Where(a => a.Pickup != null && a.Root != actor.Root).ToDictionary(a => a.Root, a => scene.PickupPosition(a.Root));
                for (int axis = 0; axis < 3; axis++)
                {
                    await Task.Delay(120, token);
                    var before = scene.PickupPosition(actor.Root); var hit = FindArrow(axis); var target = (Element3D)hit.Hit.ModelHit!;
                    MouseDown(target, hit.Hit, hit.Point); Require(scene.IsPickupDragging, "Arrow did not begin a drag");
                    target.RaiseEvent(new MouseMove3DEventArgs(target, hit.Hit, hit.Point + new System.Windows.Vector(24, -19), viewport));
                    var moved = scene.PickupPosition(actor.Root);
                    Require(Vector3.Distance(before, moved) > .001f, "Arrow did not move the pickup");
                    for (int other = 0; other < 3; other++) if (other != axis) Require(Math.Abs(before[other] - moved[other]) < .001f, "Arrow moved a second axis");
                    viewport.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseUpEvent });
                    Require(!scene.IsPickupDragging && edits.IsDirty, "Mouse release did not commit the move");
                    edits.Undo(); Require(Vector3.Distance(before, scene.PickupPosition(actor.Root)) < .001f, "Undo did not restore the complete drag");
                    edits.Redo(); Require(Vector3.Distance(moved, scene.PickupPosition(actor.Root)) < .001f, "Redo changed the drag result");
                }
                foreach (var (id, position) in othersBefore) Require(position == scene.PickupPosition(id), "Moving one instance moved a shared model");
                Require(cameraBefore == scene.CaptureView(), "Movement changed the camera");
                await Task.Delay(120, token);
                var cancelHit = FindArrow(0); var cancelTarget = (Element3D)cancelHit.Hit.ModelHit!;
                var committed = scene.PickupPosition(actor.Root); var authored = edits.Position(pickup.Source);
                MouseDown(cancelTarget, cancelHit.Hit, cancelHit.Point);
                cancelTarget.RaiseEvent(new MouseMove3DEventArgs(cancelTarget, cancelHit.Hit, cancelHit.Point + new System.Windows.Vector(30, 0), viewport));
                Require(scene.CancelPickupDrag(), "Drag cancellation was not handled");
                Require(scene.PickupPosition(actor.Root) == committed && edits.Position(pickup.Source) == authored, "Canceled drag became an authored edit");
                inputs[0].Text = (committed.X + 1.125f).ToString("R", System.Globalization.CultureInfo.CurrentCulture); placementPanel.CommitPending();
                Require(edits.Position(pickup.Source).X == committed.X + 1.125f, "Numeric input did not update placement");
                inputs[1].Text = "NaN"; placementPanel.CommitPending(); Require(edits.Position(pickup.Source).Y == committed.Y, "Nonfinite input changed authored position");
                placementPanel.Preview(edits.Position(pickup.Source)); edits.Undo();
                Require(scene.PickupPosition(actor.Root) == committed, "Numeric edit was not one undoable action");
                // The tunneling mouse event commits typed coordinates before the native gizmo snapshots its start.
                await Task.Delay(120, token);
                var pendingHit = FindArrow(0); var pendingTarget = (Element3D)pendingHit.Hit.ModelHit!;
                inputs[1].Text = (committed.Y + .125f).ToString("R", System.Globalization.CultureInfo.CurrentCulture);
                MouseDown(pendingTarget, pendingHit.Hit, pendingHit.Point);
                Require(scene.IsPickupDragging && edits.Position(pickup.Source).Y == committed.Y + .125f, "Pointer interaction lost pending numeric input");
                scene.CancelPickupDrag();
                Require(scene.PickupPosition(actor.Root) == edits.Position(pickup.Source), "Canceled drag reverted a preceding numeric edit");
                edits.Undo(); Require(scene.PickupPosition(actor.Root) == committed, "Pending numeric input did not retain independent undo");
                lockBox.IsChecked = true; Require(Manipulator().Visibility == Visibility.Collapsed, "Lock retained arrows"); lockBox.IsChecked = false;
                var pose = scene.CaptureView(); var picker = (ComboBox)window.FindName("WorldDifficulty"); picker.SelectedItem = MissionDifficulty.Easy;
                scene = await Ready(MissionDifficulty.Easy);
                Require(scene.SelectedPickupRoot is int && scene.PickupPosition(scene.SelectedPickupRoot.Value) == committed, "Difficulty lost edited position or counterpart selection");
                Require(scene.CaptureView() == pose, "Difficulty moved camera");
                var lod = (ComboBox)window.FindName("LodCombo"); if (lod.Items.Count > 1) { lod.SelectedIndex = 1; await Ready(MissionDifficulty.Easy); }
                Require(scene.SelectedPickupRoot is int && scene.PickupPosition(scene.SelectedPickupRoot.Value) == committed, "LOD lost pickup edit");
                await Capture("edited-easy");
                window.Width = 1100; await Capture("narrow"); window.Width = 1560;
                string sourcePath = edits.ArchivePaths.Single(); string copy = Path.Combine(output, "working", "zrdr.zbd");
                var saved = await edits.SaveAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [sourcePath] = copy }, token: token);
                Require(saved.Errors.Count == 0, "Save As failed");
                edits.MoveTo(pickup.Source, committed + Vector3.UnitY);
                ((Button)window.FindName("PickupSave")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                while (edits.IsDirty || !window.IsEnabled) await Task.Delay(30, token);
                using var verifyResolver = new AssetResolver(Path.GetDirectoryName(copy)!);
                var reopened = await PickupPlacementEditSession.LoadAsync(Path.Combine(Path.GetDirectoryName(copy)!, "gamez.zbd"), verifyResolver, token);
                Require(reopened.Records.Count(r => r.Type == pickup.LogicalName && r.OriginalPosition == committed + Vector3.UnitY) == 3, "Saved move was not present in every difficulty");
                Require(!Directory.GetFiles(Path.GetDirectoryName(copy)!, "*.bak").Any(), "Default save created a backup");
                Require(SameBytes(original, await File.ReadAllBytesAsync(Path.Combine(root, "m1", "zrdr.zbd"), token)), "Reference pickup archive changed");
                Require(SameBytes(worldHash, SHA256.HashData(await File.ReadAllBytesAsync(doc.Path, token))), "GameZ source changed");
                await CorpusRoundTrips(root, output, token);
                Console.WriteLine("PASS: actual Nanite hit selection; locked/unlocked arrows; three axis drags; one-step undo/redo; cancel; instance isolation; camera; difficulty and LOD retention; Save As and UI Save; three saved difficulty records; original source hashes.");
                Console.WriteLine("Screenshots and working copies: " + output);

                TransformManipulator3D Manipulator() => viewport.Items.OfType<TopMostGroup3D>().Single().Children.OfType<TransformManipulator3D>().Single();
                void Aim(Vector3 center, Vector3 offset) { camera.Position = new(center.X + offset.X, center.Y + offset.Y, center.Z + offset.Z); camera.LookDirection = new(-offset.X, -offset.Y, -offset.Z); camera.UpDirection = new(0, 1, 0); }
                void MouseDown(Element3D target, HitTestResult hit, Point p)
                {
                    scene.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = UIElement.PreviewMouseDownEvent });
                    target.RaiseEvent(new MouseDown3DEventArgs(target, hit, p, viewport, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)));
                }
                (HitTestResult Hit, Point Point) FindArrow(int axis)
                {
                    var m = Manipulator(); var pos = scene.PickupPosition(scene.SelectedPickupRoot!.Value) + m.CenterOffset;
                    var direction = axis == 0 ? Vector3.UnitX : axis == 1 ? Vector3.UnitY : Vector3.UnitZ;
                    for (float distance = .7f; distance <= 1.9f; distance += .08f)
                    {
                        var worldPoint = pos + direction * ((float)m.SizeScale * distance); var p = viewport.Project(new Point3D(worldPoint.X, worldPoint.Y, worldPoint.Z));
                        foreach (var hit in viewport.FindHits(p))
                            if (hit.ModelHit is MeshGeometryModel3D mesh && mesh.PostEffects == "ManipulatorXRayGrid")
                            {
                                var d = (mesh.Transform?.Value ?? Matrix3D.Identity).Transform(new Vector3D(1, 0, 0));
                                if (Math.Abs(axis == 0 ? d.X : axis == 1 ? d.Y : d.Z) > .9) return (hit, p);
                            }
                    }
                    throw new InvalidDataException("No hit-testable arrow for axis " + axis);
                }
                async Task<SceneViewport> Ready(MissionDifficulty difficulty)
                {
                    while (true)
                    {
                        var empty = (TextBlock)window.FindName("EmptyPreview");
                        if (empty.Text.StartsWith("Preview unavailable", StringComparison.Ordinal) && empty.Visibility == Visibility.Visible) throw new InvalidDataException(empty.Text);
                        if (((ContentControl)window.FindName("SceneHost")).Content is SceneViewport current && current.Mission?.Layout.Difficulty == difficulty &&
                            empty.Visibility != Visibility.Visible && ((StackPanel)window.FindName("PickupTools")).Visibility == Visibility.Visible && ((StackPanel)window.FindName("PickupTools")).IsEnabled)
                        { await Task.Delay(150, token); return current; }
                        await Task.Delay(40, token);
                    }
                }
                async Task Capture(string name)
                {
                    await Task.Delay(200, token); Save(scene.RenderImage(1100, 700), Path.Combine(output, name + "-scene.png"));
                    window.UpdateLayout(); var dpi = VisualTreeHelper.GetDpi(window);
                    var image = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    image.Render(window); Save(image, Path.Combine(output, name + "-layout.png"));
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); Console.WriteLine("Output: " + output); exit = 1; }
            finally { foreach (var doc in window.ViewModel.Documents) doc.Dispose(); window.ViewModel.Documents.Clear(); window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings, originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static async Task CorpusRoundTrips(string root, string output, CancellationToken token)
    {
        int maps = 0, records = 0;
        using var resolver = new AssetResolver(root);
        foreach (string world in Directory.GetFiles(root, "gamez.zbd", SearchOption.AllDirectories))
        {
            var edits = await PickupPlacementEditSession.LoadAsync(world, resolver, token);
            if (edits.Records.Count == 0) continue;
            var hashes = edits.ArchivePaths.ToDictionary(p => p, p => SHA256.HashData(File.ReadAllBytes(p)));
            foreach (string path in edits.ArchivePaths) Require(SameBytes(edits.EncodeArchive(path), await File.ReadAllBytesAsync(path, token)), "No-op encoding changed corpus bytes");
            var record = edits.Records.First(); var expected = record.OriginalPosition + new Vector3(.25f, .5f, -.75f); edits.MoveTo(record.Source, expected);
            var destinations = edits.ArchivePaths.ToDictionary(p => p, p => Path.Combine(output, "corpus", Path.GetFileName(Path.GetDirectoryName(world))!, Path.GetFileName(p)), StringComparer.OrdinalIgnoreCase);
            var result = await edits.SaveAsync(destinations, token: token); Require(result.Errors.Count == 0 && !edits.IsDirty, "Corpus save failed");
            foreach (var (path, hash) in hashes) Require(SameBytes(hash, SHA256.HashData(await File.ReadAllBytesAsync(path, token))), "Corpus source changed");
            records += edits.Records.Count; maps++;
        }
        Console.WriteLine($"Corpus: {maps} maps, {records} distinct pickup records; byte-identical no-op output, verified coordinate patches, unchanged sources.");
    }
    private static bool NearPickup(Vector3 hit, Vector3 position) => Vector3.Distance(hit, position) < 3;
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in VisualChildren(child)) yield return nested; }
    }
    private static bool SameBytes(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);
    private static void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
    private static void Save(BitmapSource bitmap, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream); }
}
