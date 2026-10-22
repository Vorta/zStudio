using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// The player tank's preview state in a source map (docs/agents/preview.md, player tank): its card sections, the
/// empty pane's Player tank row, the status bar item and the MCP fields share one choice per open map
/// (<see cref="SourceWorldSession.PlayerTank"/>), which the shared mission builder draws. A change rebuilds the view as a
/// difficulty change does; it creates no edit, draft or undo step.
/// </summary>
public partial class MainWindow
{
    private PlayerTankPanel? playerTankPanel;
    private PlayerTankPanel PlayerTankSectionsPanel
    {
        get
        {
            if (playerTankPanel != null) return playerTankPanel;
            playerTankPanel = new();
            playerTankPanel.Chosen += next => _ = ReportUi(() => ChoosePlayerTankAsync(next));
            return playerTankPanel;
        }
    }
    /// <summary>The newest choice the view does not show yet (its rebuild is running), or null.</summary>
    private PlayerTankState? posingTank;
    private long playerTankGeneration;
    /// <summary>The last pose failure and sub-mode weapon switch of the shown map, as the card says them.</summary>
    private string? playerTankFailure, playerTankSubstitution;

    /// <summary>The shown world's player tank (a source map's or a compiled world's), or null.</summary>
    private MissionPlayerTank? ShownPlayerTank => animation == null && motion == null && SceneHost.Visibility == Visibility.Visible && shownAsset?.Kind == AssetKind.World
        ? scene?.Mission?.PlayerTank : null;
    /// <summary>Whether the shown view takes a player tank choice: a source map's Whole world.</summary>
    private bool PlayerTankAdjustable => sourceChromeActive && shownDocument?.SourceWorld != null && ShownPlayerTank != null;
    private bool PlayerTankRebuilding => sourceWorkspaceBusy || shownDocument?.SourceWorld?.IsRebuilding == true || retiringScene != null;
    /// <summary>The mission folder of the shown world (m1 …), as the notes and reasons name it.</summary>
    private string ShownMissionName => shownDocument?.SourceWorld?.Mission
        ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(shownDocument?.Path ?? "")) ?? "";

    /// <summary>
    /// A published scene's tank: the map's choice becomes what the view draws (Tracked after a failed or withdrawn type, the
    /// weapon the game switches to in sub), with the failure listed in Problems (Diagnostics opens).
    /// </summary>
    private void AdoptPlayerTank(DocumentModel doc, MissionSceneContext? mission)
    {
        if (doc.SourceWorld is { } session && mission?.PlayerTank is { } tank)
        {
            session.PlayerTank = tank.Shown;
            if (publishedStaticOptions != null && ReferenceEquals(doc, shownDocument)) publishedStaticOptions = publishedStaticOptions with { Tank = tank.Shown };
            playerTankFailure = tank.Failure == null ? null
                : $"Couldn't show {PlayerTankPresentation.Label(tank.Requested.Type)}: {tank.Failure} (see Diagnostics). The view shows {PlayerTankPresentation.Label(tank.Shown.Type)}.";
            playerTankSubstitution = tank.SecondarySubstituted
                ? $"In sub the game switches {PlayerTankPresentation.ShortName(tank.Requested.Secondary)} to {PlayerTankPresentation.ShortName(tank.Shown.Secondary)}." : null;
            if (tank.Failure != null && sourceChromeActive) SetToolsVisible(true, problems: true);
        }
        RefreshPlayerTankViews();
    }

    /// <summary>The card's sections, the empty pane's row and the status bar follow the choice.</summary>
    private void RefreshPlayerTankViews()
    {
        if (!sourceChromeActive) return;
        // Only a card that shows the tank (its sections, or the empty pane's row) has anything to update.
        if (CurrentInspectionCard is { } card && (card.Selection == null || playerTankPanel?.Parent != null)) card.Refresh();
        RefreshSourceStatus();
    }

    /// <summary>The player tank's sections for a selection of its card, or null for any other selection.</summary>
    private FrameworkElement? PlayerTankSections(SceneViewport viewport, SceneInspection selected)
    {
        if (!sourceChromeActive || viewport != scene || shownDocument?.SourceWorld is not { } session || !PlayerTankAdjustable || ShownPlayerTank is not { } tank) return null;
        if (selected.AiNode != null || viewport.ActorAt(selected.SelectionNode >= 0 ? selected.SelectionNode : selected.Node)?.Root != tank.Actor.Root) return null;
        var state = posingTank ?? session.PlayerTank;
        var panel = PlayerTankSectionsPanel;
        panel.Show(new(tank, state, session.Mission, posingTank != null && posingTank.Type != tank.Shown.Type,
            posingTank != null && (posingTank.Primary != tank.Shown.Primary || posingTank.Secondary != tank.Shown.Secondary),
            PlayerTankRebuilding, playerTankFailure, playerTankSubstitution));
        return panel;
    }

    /// <summary>The empty pane's Player tank row: the tank and the choice, or why the map has none on this difficulty.</summary>
    private SceneInspectionCard.PlayerTankRow? PlayerTankRowSummary()
    {
        if (!sourceChromeActive || shownDocument?.SourceWorld is not { } session || shownAsset?.Kind != AssetKind.World || scene?.Mission is not { } mission) return null;
        if (mission.PlayerTank is not { } tank)
            return new("", null, $"No vehicle spawns on {mission.Layout.Difficulty}, so this map has no player tank there.");
        return new(tank.Actor.Name, PlayerTankPresentation.Summary(posingTank ?? session.PlayerTank), null);
    }
    /// <summary>Selects the player tank as its World tree row does, which opens its card.</summary>
    private void PickPlayerTank()
    {
        if (ShownPlayerTank is not { } tank) return;
        try
        {
            RefreshSceneTree();
            if (sceneTree?.Reveal(tank.Actor.Root) is not { Selectable: true } row) throw new StudioCommandException("stale_record", "The player tank is not in the World tree.");
            if (!ResolveSelectionDrafts(sceneTreeDocument)) return;
            SelectSceneTreeRow(row);
        }
        catch (StudioCommandException ex) { Report(ex); }
    }
    /// <summary>The status bar's item while the choice is not the defaults.</summary>
    private void RefreshPlayerTankStatus()
    {
        var state = PlayerTankAdjustable ? posingTank ?? shownDocument!.SourceWorld!.PlayerTank : null;
        bool shown = state is { IsDefault: false };
        StatusPlayerTankGroup.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        StatusPlayerTank.Text = shown ? PlayerTankPresentation.StatusText(state!) : "";
    }

    /// <summary>
    /// Checks a requested choice against the shown map: a source map's Whole world with a player tank, the type its data
    /// offers, a secondary sub mode allows (when <paramref name="secondaryChanged"/>), and no rebuild in progress.
    /// </summary>
    private (DocumentModel Document, AssetRecord Asset, MissionPlayerTank Tank) RequirePlayerTankChange(PlayerTankState next, bool secondaryChanged)
    {
        if (!sourceChromeActive || shownDocument?.SourceWorld is not { } session || shownAsset is not { Kind: AssetKind.World } asset || scene?.Mission is not { } mission)
            throw new StudioCommandException("unsupported", "The player tank's type and weapons are a source map preview.");
        if (mission.PlayerTank is not { } tank)
            throw new StudioCommandException("unsupported", $"{session.Mission} has no player tank on {mission.Layout.Difficulty}.");
        if (PlayerTankRebuilding || staticRefresh != null && posingTank == null)
            throw new StudioCommandException("busy", "The source scene is being evaluated; change the player tank when it is ready.");
        if (!next.IsValid) throw new StudioCommandException("invalid_argument", "Use a known type, a primary weapon (RFPG, ERFPG) and a secondary weapon.");
        if (PlayerTankPresentation.Reason(session.Mission, tank, next.Type) is { } reason) throw new StudioCommandException("unsupported", reason);
        if (secondaryChanged && next.Type == PlayerTankType.Sub && !tank.Model(next.Secondary).AllowedInSub)
            throw new StudioCommandException("unsupported", $"{PlayerTankPresentation.ManualName(next.Secondary)} is not available in sub mode: the game allows no beam or fire weapon there.");
        return (shownDocument, asset, tank);
    }

    /// <summary>A radio's choice: rebuilds the view with it (a newer choice supersedes an older one still posing).</summary>
    private async Task ChoosePlayerTankAsync(PlayerTankState next)
    {
        var current = posingTank ?? shownDocument?.SourceWorld?.PlayerTank;
        try
        {
            var (doc, asset, _) = RequirePlayerTankChange(next, next.Secondary != current?.Secondary);
            await (previewWork = StartPlayerTankRefresh(doc, asset, next));
        }
        catch (StudioCommandException ex)
        {
            // The radios return to the choice the view shows.
            RefreshPlayerTankViews();
            if (ex.Code != "context_changed") Notice(ex.Message);
        }
    }

    /// <summary>Takes <paramref name="next"/> as the map's choice and rebuilds the view; a refused or failed rebuild restores the shown one.</summary>
    private Task<Guid?> StartPlayerTankRefresh(DocumentModel doc, AssetRecord asset, PlayerTankState next)
    {
        doc.SourceWorld!.PlayerTank = next;
        posingTank = next; long generation = ++playerTankGeneration;
        RefreshPlayerTankViews();
        var refresh = RefreshStaticSceneAsync(doc, asset);
        _ = EndPosingAsync();
        return refresh;

        async Task EndPosingAsync()
        {
            try { await refresh; }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { }
            if (generation != playerTankGeneration) return;
            posingTank = null;
            // A rebuild that did not publish leaves the map's choice at what the view shows.
            if (doc.SourceWorld is { } session && ReferenceEquals(doc, shownDocument) && ShownPlayerTank is { } tank && staticRefresh == null) session.PlayerTank = tank.Shown;
            RefreshPlayerTankViews();
        }
    }

    /// <summary>preview_state.bft: what the view draws on the player tank, the choices and why some are unavailable; null without a tank.</summary>
    private object? PlayerTankPreviewState()
    {
        if (ShownPlayerTank is not { } tank) return null;
        bool adjustable = PlayerTankAdjustable;
        var state = adjustable ? posingTank ?? shownDocument!.SourceWorld!.PlayerTank : tank.Shown;
        string mission = ShownMissionName;
        var secondary = tank.Model(state.Secondary);
        object Describe(PlayerTankState s) => new { type = PlayerTankPresentation.Id(s.Type), primary = s.Primary, secondary = s.Secondary };
        return new
        {
            vehicle = tank.Actor.Name, node = tank.Actor.Root, adjustable,
            type = PlayerTankPresentation.Id(state.Type), primary = state.Primary, secondary = state.Secondary,
            shown = Describe(tank.Shown), defaults = Describe(PlayerTankState.Default),
            types = tank.Types.Select(t => new { id = PlayerTankPresentation.Id(t.Type), available = t.Available, transitions = t.Transitions.Count == 0 ? null : t.Transitions,
                reason = PlayerTankPresentation.Reason(mission, tank, t.Type) }).ToArray(),
            secondaries = tank.Weapons.Where(w => !w.Weapon.IsPrimary).Select(w => new { id = w.Weapon.Id, key = w.Weapon.Key, model = w.Model, present = w.Present,
                allowedInSub = w.AllowedInSub, available = !(tank.Shown.Type == PlayerTankType.Sub && !w.AllowedInSub) }).ToArray(),
            gun = new { drawn = tank.Drawn, model = secondary.Model, missing = secondary.Listed && secondary.Model != null && !secondary.Present ? secondary.Model : null,
                sharedWith = PlayerTankPresentation.SharedWith(tank, state.Secondary), line = PlayerTankPresentation.ModelLine(tank, state.Secondary) },
            posing = adjustable && posingTank != null, rebuilding = adjustable && PlayerTankRebuilding,
            failure = adjustable ? playerTankFailure : tank.Failure, substitution = adjustable ? playerTankSubstitution : null,
            hiddenAtStart = tank.HiddenAtStart, note = PlayerTankPresentation.Note(mission, tank),
        };
    }
}
