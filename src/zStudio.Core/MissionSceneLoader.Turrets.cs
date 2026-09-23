using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core;

public static partial class MissionSceneLoader
{
    // Retail turret.cpp 0x437AC0 / 0x4367A0 calls 0x45D6B0 with the actual
    // turret root. That callback runs cleanup, not the destruction sequence.
    private static void InitializeTurrets(GameScene scene, AnimationPreviewContext? context, JsonNode? ai,
        List<string> diagnostics, HashSet<int> positioned, CancellationToken token)
    {
        if (ai == null) { diagnostics.Add("Mission turrets: ai.zrd is unavailable; turret initialization could not be recovered."); return; }
        var definitions = Records(ai).ToArray();
        var turrets = definitions.FirstOrDefault(p => p.Name == "TURRET").Value;
        if (turrets == null) return;
        if (context == null) { diagnostics.Add("Mission turrets: animation data is unavailable; stored turret states are retained."); return; }
        string defaultName = FirstString(definitions.FirstOrDefault(p => p.Name == "DESTROY_ANIM").Value);
        AnimationEntry? Entry(string name) => name.Length == 0 ? null : context.Package.Entries.FirstOrDefault(e => e.Name == name);
        var defaultEntry = Entry(defaultName);
        foreach (var (pattern, definition) in Records(turrets))
        {
            token.ThrowIfCancellationRequested();
            int stars = pattern.Count(c => c == '*');
            if (stars is < 1 or > 5)
            { diagnostics.Add($"Mission turrets: ai.zrd pattern '{pattern}' requires one to five numeric wildcards."); continue; }
            var matches = scene.Nodes.Where(n => n.Class == "object3d" && NumericPatternMatches(pattern, n.Name))
                .OrderBy(n => n.Name, StringComparer.Ordinal).ThenBy(n => n.Index).ToArray();
            if (matches.Length == 0) continue; // Shared definitions can describe turrets absent from this map.
            var fields = Records(definition).ToArray();
            string explicitName = FirstString(fields.FirstOrDefault(p => p.Name == "DESTROY_ANIM").Value);
            var explicitEntry = Entry(explicitName);
            if (explicitName.Length > 0 && explicitEntry == null)
                diagnostics.Add($"Mission turrets: ai.zrd '{pattern}' animation '{explicitName}' is unavailable; using the named/default reset when available.");
            string effect = FirstString(fields.FirstOrDefault(p => p.Name == "EFFECT").Value);
            string[] parts = Strings(fields.FirstOrDefault(p => p.Name == "PARTS").Value).ToArray();
            // PARTS: barrel/firepoint; base/barrel/firepoint; or base/barrel/firepoint1/firepoint2.
            var firePoints = parts.Length is >= 2 and <= 4 ? parts.Skip(parts.Length == 2 ? 1 : 2).ToArray() : [];
            foreach (var turret in matches)
            {
                token.ThrowIfCancellationRequested();
                foreach (string helper in firePoints.Append(effect).Where(n => n.Length > 0))
                {
                    int node = context.FindBelow(turret.Index, helper);
                    if (node >= 0) scene.Nodes[node].Metadata["flags"] = scene.Nodes[node].Metadata.UInt("flags") & ~4u;
                }
                var reset = explicitEntry ?? Entry(turret.Name) ?? defaultEntry;
                if (reset == null)
                { diagnostics.Add($"Mission turrets: ai.zrd '{pattern}', node #{turret.Index} '{turret.Name}' has no resolved reset animation; stored state retained."); continue; }
                try
                {
                    List<string> resetDiagnostics = [];
                    positioned.UnionWith(AnimationPlayer.ApplyInitialization(context, [(reset, true, turret.Index)], resetDiagnostics, token));
                    diagnostics.AddRange(resetDiagnostics.Select(d => $"Mission turret #{turret.Index} '{turret.Name}' ({reset.Name}): {d}"));
                    turret.Metadata["preview_turret_pattern"] = pattern;
                    turret.Metadata["preview_turret_reset"] = reset.Name;
                }
                catch (InvalidDataException ex) { diagnostics.Add($"Mission turret #{turret.Index} '{turret.Name}', {reset.Name}: {ex.Message}"); }
            }
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
