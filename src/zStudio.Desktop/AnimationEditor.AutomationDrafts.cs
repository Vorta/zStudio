using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class AnimationEditor
{
    private bool HeightPending => !float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value != scenario.Height;
    internal bool HasAutomationDrafts => HasPendingDrafts || HeightPending;
    internal override string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { fields = base.DraftToken, height = PreviewHeight.Text }))));
    internal override object DescribeDrafts() => new
    {
        token = DraftToken, fields = base.DescribeDrafts(),
        settings = new[] { new { name = "height", text = PreviewHeight.Text, pending = HeightPending, original = scenario.Height.ToString(CultureInfo.InvariantCulture) } }.Where(s => s.pending).ToArray()
    };
    internal override void ResolveAutomationDrafts(string expectedToken, bool apply)
    {
        if (expectedToken != DraftToken) throw new StudioCommandException("draft_conflict", "Drafts changed. Read them again.");
        float? approved = null;
        if (apply && HeightPending)
        {
            if (!float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !float.IsFinite(height) || height is < -999 or > 999)
                throw new StudioCommandException("invalid_draft", "Height is invalid. Input was retained.");
            approved = height;
        }
        base.ResolveAutomationDrafts(expectedToken, apply);
        if (approved is float accepted) optionWork = ApplyHeightDraftAsync(accepted);
        else if (!apply) { changing = true; try { PreviewHeight.Text = scenario.Height.ToString(CultureInfo.InvariantCulture); } finally { changing = false; } }
    }
    private async Task ApplyHeightDraftAsync(float height)
    {
        if (disposed || context == null || LoadingPanel.Visibility == System.Windows.Visibility.Visible)
            throw new StudioCommandException("not_ready", "Wait for the animation preview to load before applying preview settings. Input was retained.");
        long revision = settingsInputRevision;
        long requested = seekGeneration + 1;
        previewOperationFailure = null;
        scenario = scenario with { Height = height }; ScenarioChanged?.Invoke(scenario);
        resetSimulation = true; pendingPlay |= playing;
        await SeekAsync(frame?.Time ?? 0, preservePlayhead: true);
        PreviewOperation.Current.ThrowIfCancellationRequested();
        if (settingsInputRevision != revision)
            throw new StudioCommandException("draft_conflict", "New preview input was retained while the approved settings were applied. Read drafts again.");
        if (previewOperationFailure != null) throw new StudioCommandException("preview_unavailable", previewOperationFailure);
        if (disposed || seekGeneration != requested || publishedSeekGeneration != requested)
            throw new StudioCommandException("context_changed", "The animation rebuild for the approved settings was superseded.");
    }
    internal async Task AwaitOptionWorkAsync()
    {
        Task current;
        do { current = optionWork; await current; } while (current != optionWork);
    }
}
