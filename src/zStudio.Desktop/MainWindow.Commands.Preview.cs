using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using NAudio.Wave;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static readonly StudioParameter PreviewParameter = P("preview", "string", "Current preview lifetime ID from zstudio_state.", true);
    private void RequirePreview(JsonObject a)
    {
        if (Text(a, "preview") != previewId.ToString() || shownDocument == null) throw new StudioCommandException("stale_preview", "Preview changed. Read zstudio_state.");
    }
    private SceneViewport TargetViewport(JsonObject a)
    {
        RequirePreview(a);
        if (EmptyPreview.Visibility == Visibility.Visible) throw new StudioCommandException("not_ready", EmptyPreview.Text);
        return motion?.Viewport ?? animation?.Viewport ?? (SceneHost.Visibility == Visibility.Visible ? scene : null) ?? throw new StudioCommandException("unsupported", "No 3D viewport is active.");
    }
    private AnimationEditor TargetAnimation(JsonObject a)
    { RequirePreview(a); return animation ?? throw new StudioCommandException("unsupported", "Select an animation first."); }
    private static double[] Triple(JsonObject a, string key)
    {
        var array = a[key] as JsonArray;
        if (array?.Count != 3) throw new StudioCommandException("invalid_argument", key + " requires three finite numbers.");
        var values = array.Select(n => n!.GetValue<double>()).ToArray();
        if (values.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e12)) throw new StudioCommandException("invalid_argument", "Coordinates must be finite and within ±1e12.");
        return values;
    }
    private void RegisterPreviewCommands(StudioCommands r)
    {
        Register(r, "texture_palette", "Read the current edited or mirrored texture header and paged RGB565 palette entries, matching the GUI palette inspector.", false, [.. AssetParameters, .. PageParameters], a =>
        {
            var d = TargetDocument(a); var asset = TargetAsset(d, a);
            if (asset.Content is not TextureInfo t) throw new StudioCommandException("unsupported", "Choose a texture.");
            List<object> colors = [];
            if (t.PaletteOffset >= 0)
            {
                var bytes = d.PreviewDocument.Slice(t.PaletteOffset, t.PaletteLength).Span;
                for (int i = 0; i < bytes.Length / 2; i++)
                {
                    ushort value = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
                    int red = value >> 11, green = (value >> 5) & 63, blue = value & 31;
                    colors.Add(new { index = i, rgb565 = value, rgb = new[] { (red << 3) | (red >> 2), (green << 2) | (green >> 4), (blue << 3) | (blue >> 2) } });
                }
            }
            return Result(new { header = asset.Metadata, palette = Page(colors, a, color => System.Text.Json.JsonSerializer.Serialize(color)).Data });
        });
        Register(r, "preview_state", "Read active preview options, camera and playback state.", false, [PreviewParameter], a =>
        {
            RequirePreview(a); return Result(new { preview = previewId, asset = shownAsset?.Id, animation = animation?.PreviewState(), motion = motion?.State,
                camera = motion?.Viewport.CaptureView() ?? animation?.Viewport.CaptureView() ?? (SceneHost.Visibility == Visibility.Visible ? scene?.CaptureView() : null),
                framingSelection = motion?.Viewport.FramingSelection ?? animation?.Viewport.FramingSelection ?? (SceneHost.Visibility == Visibility.Visible ? scene?.FramingSelection : null),
                ai = AiPreviewState(),
                lod = motion?.Lod ?? animation?.PreviewLod ?? (SceneHost.Visibility == Visibility.Visible ? (int?)LodCombo.SelectedIndex : null), difficulty = MissionWorldPath != null || motion != null ? null : ViewModel.Difficulty.ToString(),
                mission = animation?.MissionArchive ?? scene?.Mission?.Layout.MissionArchive,
                texturePacks = animation == null && SceneHost.Visibility == Visibility.Visible ? TexturePackCombo.Items.Cast<PackChoice>().ToArray() : [],
                textured = animation != null ? true : SceneHost.Visibility == Visibility.Visible ? TexturesEnabled.IsChecked : null,
                wireframe = animation != null ? false : SceneHost.Visibility == Visibility.Visible ? Wireframe.IsChecked : null,
                bounds = animation != null ? false : SceneHost.Visibility == Visibility.Visible ? BoundsEnabled.IsChecked : null,
                highlight = animation == null && shownAsset?.Kind == AssetKind.World && SceneHost.Visibility == Visibility.Visible && scene != null ? WorldHighlightName(scene.HighlightMode) : null,
                horizon = animation?.Options.Horizon ?? (SceneHost.Visibility == Visibility.Visible ? BackdropEnabled.IsChecked : null),
                texture = decoded == null ? null : new { decoded.Width, decoded.Height, zoom = ZoomSlider.Value, channel = ChannelCombo.SelectedIndex, smooth = SmoothImage.IsChecked },
                sound = wave == null ? null : new { seconds = wave.CurrentTime.TotalSeconds, duration = wave.TotalTime.TotalSeconds, playing = player?.PlaybackState == PlaybackState.Playing } });
        });
        RegisterCameraCommand(r);
        Register(r, "scene_nodes", "List active assembled scene nodes by index, including instance metadata.", false, [PreviewParameter, .. PageParameters], a =>
        {
            var viewport = TargetViewport(a); return Page((viewport.PreviewScene?.Nodes ?? []).Where(n => n.Name.Contains(Text(a,"query"),StringComparison.OrdinalIgnoreCase)), a,
                project: n => new { n.Index,n.Name,n.Class,n.Metadata, actor = viewport.ActorAt(n.Index) });
        });
        Register(r, "scene_selection", "Select/inspect a scene node. Isolate includes its descendants; isolate and show_all are available only in static model/Whole world previews, matching the GUI.", true, [PreviewParameter, P("action","string","Selection operation; isolate/show_all require a static model or Whole world preview.",true,"select","isolate","show_all"), P("node","integer","Node index.")], a =>
        {
            RequireNoDrafts(); var viewport = TargetViewport(a); string action = Text(a,"action");
            if ((animation != null || motion != null) && action is "isolate" or "show_all")
                throw new StudioCommandException("unsupported", "Isolation is available only in model and Whole world previews. Animation scenes remain fully visible.");
            if (viewport.IsPickupDragging || viewport.IsFlyActive)
                throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before changing scene selection.");
            if (action == "show_all") { viewport.Isolate(null); isolatedNode = null; }
            else
            {
                int node = Int(a,"node",-1); if (node < 0 || viewport.PreviewScene == null || node >= viewport.PreviewScene.Nodes.Count) throw new StudioCommandException("stale_record","Scene node unavailable.");
                if (animation == null)
                {
                    // A picked pickup mesh represents its whole placed instance,
                    // matching the GUI's selection and subsequent Isolate command.
                    node = viewport.PickupAt(node)?.Root ?? node;
                    InspectNode(node);
                }
                if (action == "select")
                {
                    // Clearing an unrelated geometry card is part of selecting
                    // this source, not an explicit deselection of the source row.
                    bool previous = changingSceneTree; changingSceneTree = true;
                    try { if (!viewport.SelectInspectionNode(node)) viewport.SelectInspection(null, false); }
                    finally { changingSceneTree = previous; }
                    RevealSceneNode(node);
                }
                viewport.SelectFramingNode(node);
                if (action == "isolate") { viewport.Isolate(node); isolatedNode = node; }
                return Result(viewport.PreviewScene!.Nodes[node].Metadata);
            }
            return Result(new { visible = "all" });
        });
        RegisterJob(r, "scene_options", "Set static model/world options: lod(integer), difficulty(Easy/Medium/Hard), textures/wireframe/bounds/horizon(boolean), texturePack(path or empty for automatic), highlight(none/nonDefaultSoils/canModify/clipTo), aiNodes/aiThroughGeometry(boolean), aiNetwork(all or ID with aiSnapshot). Highlights and AI are Whole world only.", [PreviewParameter,SceneChanges], false, async (a, token) =>
        {
            RequirePreview(a); RequireNoDrafts(shownDocument); if (animation != null || motion != null) throw new StudioCommandException("unsupported","Use animation_options or motion_preview.");
            TargetViewport(a); var doc = shownDocument!; var asset = shownAsset!;
            var changes = (JsonObject)a["changes"]!;
            bool changesAi = changes.Any(p => p.Key is "aiNodes" or "aiThroughGeometry" or "aiNetwork" or "aiSnapshot");
            string? requestedAiNetwork = changes.ContainsKey("aiNetwork") ? Text(changes, "aiNetwork") == "all" ? null : Text(changes, "aiNetwork") : aiNetworkFilter;
            long aiGeneration = aiOptionsGeneration;
            string? expectedAiSnapshot = scene?.AiNetworks.Id;
            if (changesAi)
            {
                if (!IsAiWorld) throw new StudioCommandException("unsupported", "AI visualization is available only in Whole world.");
                if ((changes.ContainsKey("aiSnapshot") || changes.ContainsKey("aiNetwork") && requestedAiNetwork != null) && Text(changes, "aiSnapshot") != expectedAiSnapshot)
                    throw new StudioCommandException("stale_snapshot", "Supply the current aiSnapshot from preview_state.ai.");
                if (requestedAiNetwork != null && !scene!.AiNetworks.Networks.Any(n => n.Id == requestedAiNetwork))
                    throw new StudioCommandException("stale_record", "AI network unavailable.");
            }
            if (changes.ContainsKey("highlight") && asset.Kind != AssetKind.World)
                throw new StudioCommandException("unsupported", "Surface highlighting is available only in Whole world.");
            // Schema validation covers types/enums. Validate current-view constraints
            // for the entire batch before any control, preference or renderer changes.
            int? requestedLod = changes.ContainsKey("lod") ? Int(changes, "lod") : null;
            if (requestedLod is int rank && (rank < 0 || rank >= LodCombo.Items.Count))
                throw new StudioCommandException("invalid_argument", "LOD outside available range.");
            PackChoice? requestedPack = null;
            if (changes.ContainsKey("texturePack"))
                requestedPack = TexturePackCombo.Items.Cast<PackChoice>().FirstOrDefault(p => (p.Path ?? "").Equals(Text(changes, "texturePack"), StringComparison.OrdinalIgnoreCase))
                    ?? throw new StudioCommandException("invalid_argument", "Choose an available texture variant.");
            long highlightGeneration = worldHighlightGeneration;
            foreach (var (name,value) in changes)
            {
                token.ThrowIfCancellationRequested();
                if (shownDocument != doc || shownAsset != asset) throw new StudioCommandException("context_changed", "The user selected another preview.");
                Task<Guid?>? refresh = null;
                switch(name)
                {
                    // Publish the highlight only after every requested refresh succeeds.
                    case "highlight": break;
                    case "aiNodes": case "aiThroughGeometry": case "aiNetwork": case "aiSnapshot": break;
                    case "textures": TexturesEnabled.IsChecked = value!.GetValue<bool>(); ApplySceneOptions(); break;
                    case "wireframe": Wireframe.IsChecked = value!.GetValue<bool>(); ApplySceneOptions(); break;
                    case "bounds": BoundsEnabled.IsChecked = value!.GetValue<bool>(); ApplySceneOptions(); break;
                    case "difficulty":
                        if (!Enum.TryParse<MissionDifficulty>(value!.GetValue<string>(),out var difficulty) || !Enum.IsDefined(difficulty)) throw new StudioCommandException("invalid_argument","Use Easy, Medium or Hard.");
                        bool changedDifficulty = ViewModel.Difficulty != difficulty;
                        long expectedRefreshGeneration = staticRefreshGeneration + (changedDifficulty && asset.Kind == AssetKind.World ? 1 : 0);
                        ViewModel.Difficulty = difficulty;
                        token.ThrowIfCancellationRequested();
                        // Binding/status callbacks can start another refresh before
                        // the setter returns. Never adopt that request's task as ours.
                        if (staticRefreshGeneration != expectedRefreshGeneration || ViewModel.Difficulty != difficulty)
                            throw new StudioCommandException("context_changed", "The requested difficulty was superseded. Read zstudio_state before retrying.");
                        if (asset.Kind == AssetKind.World && (changedDifficulty || staticRefresh != null))
                        { refresh = staticRefreshWork; await previewWork; }
                        break;
                    case "lod": updating = true; LodCombo.SelectedIndex = requestedLod!.Value; updating = false; refresh = RefreshStaticSceneAsync(doc,asset); await (previewWork = refresh); break;
                    case "horizon": updating = true; BackdropEnabled.IsChecked = value!.GetValue<bool>(); updating = false; refresh = RefreshStaticSceneAsync(doc,asset); await (previewWork = refresh); break;
                    case "texturePack": updating = true; TexturePackCombo.SelectedItem = requestedPack; updating = false; refresh = RefreshStaticSceneAsync(doc,asset); await (previewWork = refresh); break;
                    default: throw new StudioCommandException("unknown_option",name);
                }
                token.ThrowIfCancellationRequested();
                if (shownDocument != doc || shownAsset != asset || refresh != null && staticRefreshWork != refresh)
                    throw new StudioCommandException("context_changed", "The scene refresh was superseded. Read zstudio_state before retrying.");
                if (refresh != null)
                {
                    var published = await refresh;
                    if (published == null) throw new StudioCommandException("preview_unavailable", "The requested scene was not published. " + ViewModel.Status);
                    if (previewId != published) throw new StudioCommandException("context_changed", "The published preview was replaced. Read zstudio_state before retrying.");
                }
            }
            if (changesAi && (aiGeneration != aiOptionsGeneration || expectedAiSnapshot != scene?.AiNetworks.Id))
                throw new StudioCommandException("context_changed", "AI visualization or its source graph changed during this request.");
            if (changes.ContainsKey("highlight"))
            {
                if (worldHighlightGeneration != highlightGeneration)
                    throw new StudioCommandException("context_changed", "The user changed the highlight during this request. Read zstudio_state before retrying.");
                SetWorldHighlightMode(Text(changes, "highlight") switch
                {
                    "nonDefaultSoils" => WorldHighlightMode.NonDefaultSoils,
                    "canModify" => WorldHighlightMode.CanModify,
                    "clipTo" => WorldHighlightMode.ClipTo,
                    _ => WorldHighlightMode.None
                });
            }
            if (changesAi) SetAiOptions(Flag(changes, "aiNodes", aiVisible), Flag(changes, "aiThroughGeometry", aiThroughGeometry), requestedAiNetwork);
            return Result(new { preview = previewId, ViewModel.Status });
        });
        RegisterJob(r, "animation_options", "Set animation options: map/grid/collision/horizon/followCamera/effects/replay/mute/followLog(bool), height(-999..999), lod, difficulty, speed, volume, phase(runtime/cleanup), seed, condition(0/1/2), range/traceRange(seconds), autoRange/fitTrace(true), problemFilter(0..4), worldPath, root, activationOrigin/activationTarget(XYZ text or blank).", [PreviewParameter,AnimationChanges], false, async (a, _) =>
        {
            var editor = TargetAnimation(a); RequireNoDrafts(shownDocument);
            foreach(var (name,value) in (JsonObject)a["changes"]!) { RequirePreview(a); await editor.SetPreviewOptionAsync(name, value ?? throw new StudioCommandException("invalid_argument","Use an explicit value.")); }
            return Result(editor.PreviewState());
        });
        RegisterJob(r, "animation_transport", "Play/pause/stop, frame-step or seek the current animation.", [PreviewParameter,P("action","string","Transport action.",true,"play","pause","stop","previous","next","seek"),P("seconds","number","Seek time in seconds.")], false, async (a, _) =>
        { var editor = TargetAnimation(a); RequireNoDrafts(shownDocument); await editor.TransportAsync(Text(a,"action"),Number(a,"seconds")); return Result(editor.PreviewState()); });
        Register(r, "animation_runtime", "Read preview status, sequence runtime, dispatched event occurrences, problems or bound scene nodes. Timing thresholds and observed dispatches remain distinct.", false,
            [PreviewParameter,P("section","string","Data section.",true,"status","sequences","events","problems","scene"),.. PageParameters], a => Result(TargetAnimation(a).RuntimeData(Text(a,"section"),Int(a,"offset"),Int(a,"limit",100),Text(a,"query"))));
        Register(r, "animation_select", "Select an authored sequence/event without changing playback phase or retargeting Properties.", true,
            [PreviewParameter,P("sequence","string","Sequence GUID; omit for entry."),P("event","string","Event GUID; omit for sequence.")], a =>
        {
            var editor = TargetAnimation(a); RequireNoDrafts(shownDocument);
            Guid sequence=GuidArg(a,"sequence"), ev=GuidArg(a,"event");
            var entry=shownDocument!.AnimationEdits!.Package.Entries[editor.EntryIndex];
            if (sequence != Guid.Empty && !entry.AllSequences.Any(s => s.Id==sequence && (ev==Guid.Empty || s.Events.Any(e=>e.Id==ev))) || sequence==Guid.Empty && ev!=Guid.Empty)
                throw new StudioCommandException("stale_record","Authored sequence/event is unavailable.");
            editor.SelectSource(sequence,ev); return Result(editor.PropertySelection);
        });
        Register(r, "texture_view", "Inspect or set texture presentation. Channels: 0 RGBA, 1 RGB, 2 Alpha, 3 Red, 4 Green, 5 Blue. Coordinates are texture pixels.", true,
            [PreviewParameter,P("zoom","number","Zoom factor."),P("fit","boolean","Fit texture to viewer."),P("channel","integer","Channel index 0–5."),P("smooth","boolean","Use smooth scaling."),P("panX","number","Horizontal scroll offset in DIP."),P("panY","number","Vertical scroll offset in DIP."),P("x","integer","Pixel X to inspect."),P("y","integer","Pixel Y to inspect.")], a =>
        {
            RequirePreview(a); if(decoded == null) throw new StudioCommandException("unsupported","Select a texture first.");
            if(a.ContainsKey("zoom")) { double zoom=Number(a,"zoom"); if(zoom<ZoomSlider.Minimum || zoom>ZoomSlider.Maximum) throw new StudioCommandException("invalid_argument","Zoom outside slider range."); ZoomSlider.Value=zoom; }
            if(a.ContainsKey("channel")) { int channel=Int(a,"channel"); if(channel<0 || channel>=ChannelCombo.Items.Count) throw new StudioCommandException("invalid_argument","Unknown channel."); ChannelCombo.SelectedIndex=channel; }
            if(a.ContainsKey("smooth")) SmoothImage.IsChecked=Flag(a,"smooth"); if(Flag(a,"fit")) FitImage(); UpdateImage();
            if(a.ContainsKey("panX")) ImageScroll.ScrollToHorizontalOffset(Number(a,"panX")); if(a.ContainsKey("panY")) ImageScroll.ScrollToVerticalOffset(Number(a,"panY"));
            int x=Int(a,"x"),y=Int(a,"y"); if(x<0 || y<0 || x>=decoded.Width || y>=decoded.Height) throw new StudioCommandException("invalid_argument","Pixel outside texture."); int at=(y*decoded.Width+x)*4;
            return Result(new { decoded.Width,decoded.Height,x,y,rgba=decoded.Rgba.AsSpan(at,4).ToArray().Select(b=>(int)b),zoom=ZoomSlider.Value, metadata=shownAsset?.Metadata });
        });
        Register(r, "sound_transport", "Control the selected sound preview and read waveform/cue metadata.", true,
            [PreviewParameter,P("action","string","Playback action.",true,"read","play","pause","stop","seek"),P("seconds","number","Seek seconds.")], a =>
        {
            RequirePreview(a); if(wave==null) throw new StudioCommandException("unsupported","Select a sound first.");
            switch(Text(a,"action")) { case "play": PlaySound(); break; case "pause": player?.Pause(); break; case "stop": player?.Stop(); wave.Position=0; break; case "seek": SeekAudio(Number(a,"seconds")); break; }
            UpdateAudioPosition(); return Result(new { seconds=wave.CurrentTime.TotalSeconds,duration=wave.TotalTime.TotalSeconds,state=(player?.PlaybackState ?? PlaybackState.Stopped).ToString(),waveInfo });
        });
        Register(r, "capture", "Capture the current preview or a zStudio-owned window as PNG; never captures other applications. 3D images fit within width/height without distortion or resizing the viewport. Texture/window captures keep native dimensions.", false,
            [P("target","string","Capture target.",true,"preview","window","properties"),P("preview","string","Current preview lifetime ID from zstudio_state; required for target=preview."),P("width","integer","Maximum 3D image width, 1–4096; default 1280."),P("height","integer","Maximum 3D image height, 1–4096; default 720.")], a =>
        {
            string target=Text(a,"target"); BitmapSource image;
            if (target == "preview") RequirePreview(a);
            if(target=="window" || target=="properties") image=StudioCapture.Window(target=="window" ? this : propertiesWindow ?? throw new StudioCommandException("not_ready","Properties is closed."));
            else if(decoded!=null) image=MakeBitmap(decoded,ChannelCombo.SelectedIndex);
            else { int w=Int(a,"width",1280),h=Int(a,"height",720); if(w is <1 or >4096 || h is <1 or >4096) throw new StudioCommandException("invalid_argument","Capture dimensions must be 1–4096."); image=(motion?.Viewport ?? animation?.Viewport ?? (SceneHost.Visibility == Visibility.Visible ? scene : null) ?? throw new StudioCommandException("not_ready","No image/3D preview is loaded. Use target=window for other viewers.")).RenderImage(w,h,preserveAspect:true); }
            using MemoryStream bytes=new(); PngBitmapEncoder encoder=new(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(bytes);
            return new(Result(new { image.PixelWidth,image.PixelHeight,mimeType="image/png", preview = target == "preview" ? (Guid?)previewId : null, asset = target == "preview" ? shownAsset?.Id : null }).Data,bytes.ToArray());
        });
        RegisterWorkspacePresentation(r);
    }
}
