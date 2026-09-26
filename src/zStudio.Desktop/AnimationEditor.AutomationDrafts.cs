using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    private bool HeightPending => !float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || value != appliedHeight;
    private bool SeedPending => !int.TryParse(Seed.Text, CultureInfo.InvariantCulture, out int value) || value != appliedSeed;
    private bool RangePending => EndTime.Text != SeekSlider.Maximum.ToString("0.###", CultureInfo.InvariantCulture);
    internal bool HasAutomationDrafts => HasPendingDrafts || HeightPending || SeedPending || RangePending;
    internal override string DraftToken => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { fields = base.DraftToken, height = PreviewHeight.Text, seed = Seed.Text, range = EndTime.Text }))));
    internal override object DescribeDrafts() => new
    {
        token = DraftToken, fields = base.DescribeDrafts(),
        settings = new[]
        {
            new { name = "height", text = PreviewHeight.Text, pending = HeightPending, original = appliedHeight.ToString(CultureInfo.InvariantCulture) },
            new { name = "seed", text = Seed.Text, pending = SeedPending, original = appliedSeed.ToString(CultureInfo.InvariantCulture) },
            new { name = "range", text = EndTime.Text, pending = RangePending, original = SeekSlider.Maximum.ToString("0.###", CultureInfo.InvariantCulture) }
        }.Where(s => s.pending).ToArray()
    };
    internal override void ResolveAutomationDrafts(string expectedToken, bool apply)
    {
        if (expectedToken != DraftToken) throw new StudioCommandException("draft_conflict", "Drafts changed. Read them again.");
        SettingDrafts? approved = null;
        if (apply)
        {
            if (!float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !float.IsFinite(height) || height is < -999 or > 999 ||
                !int.TryParse(Seed.Text, CultureInfo.InvariantCulture, out int seed) || !double.TryParse(EndTime.Text, CultureInfo.InvariantCulture, out double end) || !double.IsFinite(end) || end < 1d / 60 || end > 3600)
                throw new StudioCommandException("invalid_draft", "Height, seed or range is invalid. Input was retained.");
            approved = new(height, seed, end, HeightPending, SeedPending, RangePending);
        }
        base.ResolveAutomationDrafts(expectedToken, apply);
        if (approved != null) optionWork = ApplySettingDraftsAsync(approved);
        else
        {
            PreviewHeight.Text = appliedHeight.ToString(CultureInfo.InvariantCulture);
            Seed.Text = appliedSeed.ToString(CultureInfo.InvariantCulture);
            EndTime.Text = SeekSlider.Maximum.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
    private sealed record SettingDrafts(float Height, int Seed, double Range, bool HeightPending, bool SeedPending, bool RangePending);
    private async Task ApplySettingDraftsAsync(SettingDrafts approved)
    {
        // Accepted settings can outlive a failed or superseded reconstruction.
        // Retrying the same input must finish that work, even when no text differs.
        bool resetSeed = approved.SeedPending || player is { } seedPlayer && seedPlayer.Seed != appliedSeed;
        bool restoreHeight = approved.HeightPending || player is { } heightPlayer && heightPlayer.PreviewHeight != appliedHeight;
        bool rebuild = resetSeed || restoreHeight || approved.RangePending || resetSimulation || contextDirty || previewOperationFailure != null;
        if (rebuild && (disposed || context == null || LoadingPanel.Visibility == System.Windows.Visibility.Visible))
            throw new StudioCommandException("not_ready", "Wait for the animation preview to load before applying preview settings. Input was retained.");
        // Freeze every approved operand before field commits or asynchronous
        // rebuilds can rewrite controls. Apply the batch once, then reconstruct.
        if (approved.HeightPending) { appliedHeight = approved.Height; resetSimulation = true; pendingPlay |= playing; }
        if (approved.SeedPending) { appliedSeed = approved.Seed; resetSimulation = true; }
        resetSimulation |= resetSeed || restoreHeight;
        if (approved.RangePending) { customRange = true; ApplyRange(Math.Ceiling(approved.Range * 60) / 60); }
        if (!rebuild) return;
        long revision = settingsInputRevision;
        long requested = seekGeneration + 1;
        previewOperationFailure = null;
        await SeekAsync(resetSeed ? 0 : frame?.Time ?? 0, preservePlayhead: restoreHeight && !approved.RangePending);
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
