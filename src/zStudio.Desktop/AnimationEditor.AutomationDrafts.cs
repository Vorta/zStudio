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
        if (apply && (!float.TryParse(PreviewHeight.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out float height) || !float.IsFinite(height) || height is < -999 or > 999 ||
            !int.TryParse(Seed.Text, CultureInfo.InvariantCulture, out _) || !double.TryParse(EndTime.Text, CultureInfo.InvariantCulture, out double end) || !double.IsFinite(end) || end < 1d / 60 || end > 3600))
            throw new StudioCommandException("invalid_draft", "Height, seed or range is invalid. Input was retained.");
        base.ResolveAutomationDrafts(expectedToken, apply);
        if (apply) optionWork = ApplySettingDraftsAsync();
        else
        {
            PreviewHeight.Text = appliedHeight.ToString(CultureInfo.InvariantCulture);
            Seed.Text = appliedSeed.ToString(CultureInfo.InvariantCulture);
            EndTime.Text = SeekSlider.Maximum.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
    private async Task ApplySettingDraftsAsync()
    {
        if (HeightPending) await HeightChangedAsync(this, new(System.Windows.Controls.TextBox.TextChangedEvent, System.Windows.Controls.UndoAction.None));
        if (SeedPending) await SeedChangedAsync(this, new());
        if (RangePending) { customRange = true; ApplyRange(Math.Ceiling(double.Parse(EndTime.Text, CultureInfo.InvariantCulture) * 60) / 60); await SeekAsync(frame?.Time ?? 0); }
    }
    internal async Task AwaitOptionWorkAsync()
    {
        Task current;
        do { current = optionWork; await current; } while (current != optionWork);
    }
}
