using System.Text.Json.Nodes;

namespace zStudio.Core;

/// <summary>Shared rendering and export interpretation of normalized scene material metadata.</summary>
public static class SceneMaterialSemantics
{
    /// <summary>
    /// Returns the bound texture, or -1 for a colour material. Parsed materials retain their raw texture slot even when
    /// the flags byte's textured bit is clear. Metadata without a flags byte already represents a normalized binding.
    /// </summary>
    public static int EffectiveTextureIndex(JsonObject material)
        => material.ContainsKey("flags") && (material.UInt("flags") & 1) == 0 ? -1 : material.Int("texture_index", -1);
}
