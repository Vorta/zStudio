using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private MotionEditor? motion;
    private static MotionClip ValidateMotionTarget(DocumentModel doc, Guid member, int? part = null, int? frame = null)
    {
        var snapshot = ResourceSession(doc).Current;
        int index = snapshot.Members.ToList().FindIndex(m => m.Id == member);
        if (index < 0 || snapshot.Document.Assets[index].Content is not MotionClip clip) throw new StudioCommandException("unsupported", "Choose a decoded version 4 motion member.");
        if (part is int p && (p < 0 || p >= clip.Parts.Count) || frame is int f && (f < 0 || f >= clip.FrameCount)) throw new StudioCommandException("invalid_argument", "Part or frame is outside the current motion clip.");
        return clip;
    }
    private Task ApplyMotionAsync(DocumentModel doc, Guid member, string action, int part, int frame, MotionFrame? value, float? time, CancellationToken token)
    {
        _ = ValidateMotionTarget(doc, member, action == "set" ? part : null, action == "timing" ? null : frame);
        return ApplyResourceAsync(doc, ct => ResourceSession(doc).PrepareMotionAsync(member, action, part, frame, value, time, ct), doc.Revision, token);
    }
    private void RegisterMotionCommands(StudioCommands registry)
    {
        Register(registry, "motion_records", "Inspect a version 4 motion clip by stable archive member identity. Omit part for tracks; supply part for paged authored frames including the retained closing sample.", false,
            [DocumentParameter, MemberParameter, P("part", "integer", "Optional zero-based part index."), .. PageParameters], a =>
        {
            var doc = TargetDocument(a); var clip = ValidateMotionTarget(doc, GuidArg(a, "member"), a.ContainsKey("part") ? Int(a, "part") : null);
            object rows = a.ContainsKey("part") ? Page(clip.Parts[Int(a, "part")].Frames.Select((f, i) => new { index = i, closing = i == clip.FrameCount, seconds = i * (double)clip.LoopTime / clip.FrameCount,
                translation = new[] { f.Translation.X, f.Translation.Y, f.Translation.Z }, quaternionWxyz = new[] { f.Rotation.W, f.Rotation.X, f.Rotation.Y, f.Rotation.Z } }), a).Data :
                Page(clip.Parts.Select((p, i) => new { index = i, p.Name, p.Flags, closingSampleMatches = p.Frames[0] == p.Frames[^1] }), a, p => p.Name).Data;
            return Result(new { doc.Revision, clip.LoopTime, clip.FrameCount, partCount = clip.Parts.Count, rows });
        });
        RegisterJob(registry, "motion_edit", "Edit authored motion timing or a frame, or insert/delete a frame across all parts. One shared archive undo transaction. Refreshes the active sampler while retaining camera/playback. Does not save; use the existing verified document save operation.",
            [DocumentParameter, RevisionParameter, MemberParameter, P("action", "string", "Edit operation.", true, "timing", "set", "insert", "delete"),
                P("part", "integer", "Part index required for set."), P("frame", "integer", "Frame index required for set/insert/delete; insert occurs after it."),
                new("loopSeconds", "number", "Positive finite loop duration for timing."),
                new("translation", "array", "XYZ for set.", Items: P("component", "number", "Finite component."), MinItems: 3, MaxItems: 3),
                new("quaternionWxyz", "array", "Nonzero finite WXYZ quaternion for set.", Items: P("component", "number", "Finite component."), MinItems: 4, MaxItems: 4)], false, async (a, token) =>
        {
            var doc = TargetDocument(a, true); string action = Text(a, "action"); MotionFrame? value = null;
            if (action == "set")
            {
                var xyz = Triple(a, "translation");
                if (a["quaternionWxyz"] is not JsonArray q || q.Count != 4) throw new StudioCommandException("invalid_argument", "Specify quaternionWxyz.");
                float[] rotation = q.Select(v => v!.GetValue<float>()).ToArray();
                value = new(new Vector3((float)xyz[0], (float)xyz[1], (float)xyz[2]), new Quaternion(rotation[1], rotation[2], rotation[3], rotation[0]));
            }
            await ApplyMotionAsync(doc, GuidArg(a, "member"), action, Int(a, "part", -1), Int(a, "frame", -1), value, a["loopSeconds"]?.GetValue<float>(), token);
            return Result(DocumentState(doc));
        });
        RegisterJob(registry, "motion_preview", "Control the visible motion viewer: play/pause, seek, choose an explicit library member or LOD, frame the model, or inspect available bindings. Shares GUI playback and camera. Superseded assembly loads retain playback intent; an explicit play/pause during loading takes precedence. State distinguishes actual playing, loading and playbackRequested.",
            [PreviewParameter, P("action", "string", "Preview operation.", true, "state", "play", "pause", "seek", "assembly", "lod", "frame"), P("seconds", "number", "Time within the clip for seek."), P("memberIndex", "integer", "Library member index from motion preview state."), P("lod", "integer", "Available LOD rank.")], false, async (a, token) =>
        {
            RequirePreview(a); var editor = motion ?? throw new StudioCommandException("unsupported", "Select a motion clip first.");
            if (Text(a, "action") != "state") RequireNoDrafts();
            switch (Text(a, "action"))
            {
                case "play": editor.Play(); break;
                case "pause": editor.Pause(); break;
                case "seek": editor.Seek(a["seconds"]?.GetValue<double>() ?? throw new StudioCommandException("invalid_argument", "Specify seconds.")); break;
                case "assembly": await editor.SelectAssemblyAsync(Int(a, "memberIndex", -1), token); break;
                case "lod": editor.SetLod(Int(a, "lod", -1)); break;
                case "frame": editor.Viewport.FrameAll(); break;
            }
            RequirePreview(a); return Result(editor.State);
        });
    }
}
