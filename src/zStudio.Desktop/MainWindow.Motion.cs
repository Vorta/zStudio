using zStudio.Automation;
using zStudio.Core;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private MotionEditor? motion;
    private static MotionClip ValidateMotionTarget(DocumentModel doc, Guid member, int? part = null, int? frame = null)
    {
        var resources = ResourceSession(doc);
        int index = resources.Members.ToList().FindIndex(m => m.Id == member);
        if (index < 0 || resources.Document.Assets[index].Content is not MotionClip clip) throw new StudioCommandException("unsupported", "Choose a decoded version 4 motion member.");
        if (part is int p && (p < 0 || p >= clip.Parts.Count) || frame is int f && (f < 0 || f >= clip.FrameCount)) throw new StudioCommandException("invalid_argument", "Part or frame is outside the current motion clip.");
        return clip;
    }
    private void RegisterMotionCommands(StudioCommands registry)
    {
        Register(registry, "motion_records", "Inspect a version 4 motion clip by stable archive member identity. Omit part for tracks (512-character name preview, nameCharacters/nameTruncated; queries match full names); supply part for paged authored frames including the retained closing sample (query applies to tracks only). JSON export retains complete names.", false,
            [DocumentParameter, MemberParameter, P("part", "integer", "Optional zero-based part index."), .. PageParameters], a =>
        {
            var doc = TargetDocument(a); var clip = ValidateMotionTarget(doc, GuidArg(a, "member"), a.ContainsKey("part") ? Int(a, "part") : null);
            if (a.ContainsKey("part") && !string.IsNullOrEmpty(a["query"]?.GetValue<string>())) throw new StudioCommandException("invalid_argument", "Frame pages are positional; omit query when part is supplied.");
            // Frame pages are positional over a known count: construct only the requested rows.
            var frames = a.ContainsKey("part") ? clip.Parts[Int(a, "part")].Frames : null;
            object rows = frames != null ? PageRange(frames.Count, a, i => { var f = frames[i]; return new { index = i, closing = i == clip.FrameCount, seconds = i * (double)clip.LoopTime / clip.FrameCount,
                translation = new[] { f.Translation.X, f.Translation.Y, f.Translation.Z }, quaternionWxyz = new[] { f.Rotation.W, f.Rotation.X, f.Rotation.Y, f.Rotation.Z } }; }).Data :
                Page(clip.Parts.Select((p, i) => (Part: p, Index: i)), a, row => row.Part.Name,
                    row => new { index = row.Index, Name = row.Part.Name[..Math.Min(512, row.Part.Name.Length)], nameCharacters = row.Part.Name.Length, nameTruncated = row.Part.Name.Length > 512,
                        row.Part.Flags, closingSampleMatches = row.Part.Frames[0] == row.Part.Frames[^1] }).Data;
            return Result(new { doc.Revision, clip.LoopTime, clip.FrameCount, partCount = clip.Parts.Count, rows });
        });
        RegisterJob(registry, "motion_preview", "Control visible motion playback, seek, assembly, LOD and framing. Superseded assembly loads retain playback intent; explicit play/pause during loading takes precedence. State distinguishes playing/loading/playbackRequested; previews 32 diagnostics (512 characters each) and 32 assemblies with diagnosticCount/diagnosticsTruncated and assemblyCount/assembliesTruncated. Use action assemblies for complete paged bindings; motion_records pages authored tracks with bounded name previews.",
            [PreviewParameter, P("action", "string", "Preview operation; assemblies pages/filter bindings by name without changing selection.", true, "state", "assemblies", "play", "pause", "seek", "assembly", "lod", "frame"), P("seconds", "number", "Time within the clip for seek."), P("memberIndex", "integer", "Library member index from motion preview state or assemblies."), P("lod", "integer", "Available LOD rank."), .. PageParameters], false, async (a, token) =>
        {
            RequirePreview(a); var editor = motion ?? throw new StudioCommandException("unsupported", "Select a motion clip first.");
            if (Text(a, "action") == "assemblies") return Page(editor.Assemblies, a, row => row.Name, row => new { member = row.Index, row.Name });
            if (Text(a, "action") != "state") RequireNoDrafts();
            switch (Text(a, "action"))
            {
                case "play": editor.Play(); break;
                case "pause": editor.Pause(); break;
                case "seek": editor.Seek(a["seconds"]?.GetValue<double>() ?? throw new StudioCommandException("invalid_argument", "Specify seconds.")); break;
                case "assembly":
                    int requestedMember = Int(a, "memberIndex", -1); await editor.SelectAssemblyAsync(requestedMember, token);
                    // A superseded load returns without publishing; never report another request's binding as this one.
                    if (motion != editor || editor.AssemblyMember != requestedMember) throw new StudioCommandException("context_changed", "The assembly request was superseded. Read motion_preview state before retrying.");
                    break;
                case "lod": editor.SetLod(Int(a, "lod", -1)); break;
                case "frame": editor.Viewport.FrameAll(); break;
            }
            if (Text(a, "action") is "seek" or "lod" && !await editor.PresentationWork.WaitAsync(token))
                throw new StudioCommandException("context_changed", "The requested motion frame was superseded or unavailable. Inspect preview status and retry.");
            RequirePreview(a); return Result(editor.State);
        });
    }
}
