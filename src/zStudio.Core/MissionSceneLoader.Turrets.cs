using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core;

public static partial class MissionSceneLoader
{
    // Retail turret.cpp 0x437AC0 / 0x4367A0 calls 0x45D6B0 with the actual
    // turret root. That callback runs cleanup, not the destruction sequence.
    internal static void InitializeTurrets(GameScene scene, AnimationPreviewContext? context, JsonNode? ai,
        List<string> diagnostics, HashSet<int> positioned, CancellationToken token, AnimationBindingOperation? bindings = null)
    {
        BoundedDiagnostics notes = new(diagnostics);
        if (ai == null) { notes.Add($"Mission turrets: ai.zrd is unavailable; turret initialization could not be recovered."); return; }
        if (context != null) bindings ??= new(context, token);
        var definitions = Fields(ai);
        var turrets = definitions.FirstOrDefault(p => p.Name == "TURRET").Value;
        if (turrets == null) return;
        if (context == null) { notes.Add($"Mission turrets: animation data is unavailable; stored turret states are retained."); return; }
        var work = bindings!;
        string defaultName = FirstString(definitions.FirstOrDefault(p => p.Name == "DESTROY_ANIM").Value);
        Dictionary<string, AnimationEntry>? entries = null;
        AnimationEntry? Entry(string name)
        {
            work.Reserve(1L + name.Length);
            if (name.Length == 0) return null; // The reserved empty entry is never a reset.
            if (entries == null)
            {
                Dictionary<string, AnimationEntry> indexed = new(StringComparer.Ordinal);
                foreach (var entry in context.Package.Entries)
                {
                    work.Reserve(128); // Fixed-width name decode, hash and retained first-entry row.
                    indexed.TryAdd(entry.Name, entry);
                }
                entries = indexed;
            }
            return entries.GetValueOrDefault(name);
        }
        var defaultEntry = Entry(defaultName);
        foreach (var (pattern, definition) in Records(turrets))
        {
            work.Reserve(1L + pattern.Length);
            int stars = pattern.Count(c => c == '*');
            if (stars is < 1 or > 5)
            { notes.Add($"Mission turrets: ai.zrd pattern '{pattern}' requires one to five numeric wildcards."); continue; }
            List<GameNode> matches = [];
            int longest = 0;
            foreach (var node in scene.Nodes)
            {
                // No-match patterns must spend work too, before scanning their authored characters.
                work.Reserve(1L + (node.Class == "object3d" && pattern.Length == node.Name.Length ? pattern.Length : 0));
                if (node.Class != "object3d" || !NumericPatternMatches(pattern, node.Name)) continue;
                work.Reserve(16); matches.Add(node); longest = Math.Max(longest, node.Name.Length);
            }
            if (matches.Count == 0) continue; // Shared definitions can describe turrets absent from this map.
            // Reserve conservative introsort comparisons and its two retained arrays before sorting.
            int levels = System.Numerics.BitOperations.Log2((uint)matches.Count) + 1;
            work.Reserve(16L * matches.Count + 4L * matches.Count * (levels + 1) * (longest + 1L));
            var ordered = matches.OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Index).ToArray();
            work.Reserve(0);
            var fields = Fields(definition);
            string explicitName = FirstString(fields.FirstOrDefault(p => p.Name == "DESTROY_ANIM").Value);
            var explicitEntry = Entry(explicitName);
            if (explicitName.Length > 0 && explicitEntry == null)
                notes.Add($"Mission turrets: ai.zrd '{pattern}' animation '{explicitName}' is unavailable; using the named/default reset when available.");
            string effect = FirstString(fields.FirstOrDefault(p => p.Name == "EFFECT").Value);
            List<string> parts = []; int partCount = 0;
            ReadParts(fields.FirstOrDefault(p => p.Name == "PARTS").Value);
            // PARTS: barrel/firepoint; base/barrel/firepoint; or base/barrel/firepoint1/firepoint2.
            var firePoints = partCount is >= 2 and <= 4 ? parts.Skip(partCount == 2 ? 1 : 2).ToArray() : [];
            foreach (var turret in ordered)
            {
                token.ThrowIfCancellationRequested();
                foreach (string helper in firePoints.Append(effect).Where(n => n.Length > 0))
                {
                    int node = work.First(turret.Index, helper, scene.Nodes.Count);
                    if (node >= 0) scene.Nodes[node].Metadata["flags"] = scene.Nodes[node].Metadata.UInt("flags") & ~4u;
                }
                var reset = explicitEntry ?? Entry(turret.Name) ?? defaultEntry;
                if (reset == null)
                { notes.Add($"Mission turrets: ai.zrd '{pattern}', node #{turret.Index} '{turret.Name}' has no resolved reset animation; stored state retained."); continue; }
                try
                {
                    List<string> resetDiagnostics = [];
                    positioned.UnionWith(AnimationPlayer.ApplyInitialization(context, [(reset, true, turret.Index)], resetDiagnostics, token, work));
                    foreach (string diagnostic in resetDiagnostics)
                        notes.Add($"Mission turret #{turret.Index} '{turret.Name}' ({reset.Name}): {new BoundedDiagnostics.PreparedMessage(diagnostic)}");
                    turret.Metadata["preview_turret_pattern"] = pattern;
                    turret.Metadata["preview_turret_reset"] = reset.Name;
                }
                catch (InvalidDataException ex) { notes.Add($"Mission turret #{turret.Index} '{turret.Name}', {reset.Name}: {ex.Message}"); }
            }

            void ReadParts(JsonNode? value)
            {
                work.Reserve(1);
                if (value.Text("type") == "string")
                {
                    if (partCount++ < 4) { work.Reserve(16); parts.Add(value.Text("value")); }
                }
                if (value?["children"] is JsonArray children)
                    foreach (var child in children) ReadParts(child);
            }
        }

        List<(string Name, JsonNode? Value)> Fields(JsonNode? value)
        {
            List<(string Name, JsonNode? Value)> result = [];
            foreach (var record in Records(value))
            {
                token.ThrowIfCancellationRequested();
                bindings?.Reserve(64L + record.Name.Length);
                result.Add(record);
            }
            return result;
        }
    }

    private static string FirstString(JsonNode? array) => array?["children"] is JsonArray { Count: > 0 } children
        && children[0].Text("type") == "string" ? children[0].Text("value") : "";

    // zRdrInitWildcardPath 0x4A5F90 expands each '*' to exactly one decimal digit.
    private static bool NumericPatternMatches(string pattern, string name)
    {
        if (pattern.Length != name.Length) return false;
        for (int i = 0; i < pattern.Length; i++)
            if (pattern[i] == '*' ? !char.IsAsciiDigit(name[i]) : pattern[i] != name[i]) return false;
        return true;
    }
}
