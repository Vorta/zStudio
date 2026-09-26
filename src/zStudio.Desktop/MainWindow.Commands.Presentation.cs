using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private bool automationCloseRequested;
    private void RegisterWorkspacePresentation(StudioCommands r)
    {
        Register(r,"workspace_view","Read or set presentation preferences. Supports theme, density, preset, navigator/inspector/tools visibility, pane dimensions and tab indices. Does not commit drafts.",true,
            [WorkspaceChanges],a=>
        {
            // Validate all tab requests before changing preferences. A rejected
            // request must not leave its earlier theme/layout changes applied.
            foreach (var (key, value) in a["changes"] as JsonObject ?? new())
                if (key is "navigatorTab" or "inspectorTab" or "toolsTab")
                    ValidateTab(key == "navigatorTab" ? NavigationTabs : key == "inspectorTab" ? InspectorTabs : ToolTabs, value!.GetValue<int>());
            foreach(var (key,value) in a["changes"] as JsonObject ?? new())
            {
                switch(key)
                {
                    case "resetLayout": if(value!.GetValue<bool>()) ResetLayoutClick(this,new()); break;
                    case "theme": string theme=value!.GetValue<string>(); if(theme is not ("System" or "Light" or "Dark")) throw new StudioCommandException("invalid_argument","Unknown theme."); ApplyTheme(theme); ViewModel.Settings.Theme=theme; break;
                    case "density": string density=value!.GetValue<string>(); if(density is not ("Compact" or "Comfortable")) throw new StudioCommandException("invalid_argument","Unknown density."); Layout.Density=density; ApplyDensity(); break;
                    case "preset": string preset=value!.GetValue<string>(); if(preset is not ("Inspect" or "Edit" or "Debug" or "Focus preview")) throw new StudioCommandException("invalid_argument","Unknown preset."); PresetClick(new MenuItem { Header=preset },new()); break;
                    case "navigator": Layout.NavigatorVisible=value!.GetValue<bool>(); break;
                    case "inspector": Layout.InspectorVisible=value!.GetValue<bool>(); break;
                    case "tools": Layout.ToolsVisible=value!.GetValue<bool>(); break;
                    case "toolsMaximized": toolsMaximized=value!.GetValue<bool>(); break;
                    case "navigatorWidth": Layout.NavigatorWidth=value!.GetValue<double>(); break;
                    case "inspectorWidth": Layout.InspectorWidth=value!.GetValue<double>(); break;
                    case "toolsHeight": Layout.ToolsHeight=value!.GetValue<double>(); break;
                    case "navigatorTab": SetTab(NavigationTabs,value!.GetValue<int>()); break;
                    case "inspectorTab": SetTab(InspectorTabs,value!.GetValue<int>()); break;
                    case "toolsTab": SetTab(ToolTabs,value!.GetValue<int>()); break;
                    case "backupOnSave": ViewModel.Settings.CreateBackupOnSave=value!.GetValue<bool>(); BackupOnSave.IsChecked=ViewModel.Settings.CreateBackupOnSave; break;
                    default: throw new StudioCommandException("unknown_option",key);
                }
            }
            if (a.ContainsKey("changes")) { Layout.Normalize(); ArrangeWorkspace(); SaveWorkspacePreferences(); ViewModel.Settings.Save(); }
            return Result(new { theme=ViewModel.Settings.Theme,layout=Layout,ViewModel.Settings.CreateBackupOnSave });
        });
        Register(r,"window","Read or change zStudio window state; close requires clean documents and resolved drafts.",true,
            [P("action","string","Window action.",true,"read","activate","minimize","maximize","restore","close")],a=>
        {
            string action=Text(a,"action");
            switch(action)
            {
                case "activate": Activate(); break;
                case "minimize": WindowState=WindowState.Minimized; break;
                case "maximize": WindowState=WindowState.Maximized; break;
                case "restore": WindowState=WindowState.Normal; break;
                case "close": RequireNoDrafts(); if(ViewModel.Documents.Any(d=>d.IsDirty)) throw new StudioCommandException("unsaved_changes","Save or explicitly close dirty documents first."); _=CloseAfterResponseAsync(); break;
            }
            return Result(new { state=WindowState.ToString(),Width,Height,closing=action=="close" });
        });
        Register(r,"properties_open","Open the reusable Properties window for an explicit asset or animation sequence/event; current pending drafts must be resolved first.",true,
            [..AssetParameters,P("sequence","string","Animation sequence GUID."),P("event","string","Animation event GUID.")],async (a,token)=>
        {
            if(propertiesWindow?.HasPendingDrafts==true) throw new StudioCommandException("pending_drafts","Resolve Properties drafts before retargeting.");
            var doc=TargetDocument(a); var asset=TargetAsset(doc,a);
            PropertiesWindow? opened;
            if(asset.Kind==Core.AssetKind.Animation)
            {
                Guid sequence = GuidArg(a,"sequence"), ev = GuidArg(a,"event");
                var entry = doc.AnimationEdits?.Package.Entries.ElementAtOrDefault(asset.Index) ?? throw new StudioCommandException("unsupported", "Animation properties are unavailable.");
                if (sequence == Guid.Empty && ev != Guid.Empty || sequence != Guid.Empty && !entry.AllSequences.Any(s => s.Id == sequence && (ev == Guid.Empty || s.Events.Any(e => e.Id == ev))))
                    throw new StudioCommandException("stale_record", "Animation sequence/event is unavailable.");
                opened = OpenAnimationProperties(doc,asset.Index,sequence,ev);
            }
            else opened = await OpenAssetPropertiesAsync(doc,asset,token,automation: true);
            token.ThrowIfCancellationRequested();
            if (opened == null || propertiesWindow != opened || opened.Document != doc || !opened.IsVisible)
                throw new StudioCommandException("context_changed", "The requested Properties target was not published.");
            return Result(new { document=doc.SessionId,asset=asset.Id,title=opened.Title,properties=opened.CurrentJson });
        });
        Register(r,"properties_close","Close the Properties window after drafts have been explicitly resolved.",true,[],_=>
        {
            if(propertiesWindow?.HasPendingDrafts==true) throw new StudioCommandException("pending_drafts","Resolve Properties drafts first.");
            ++propertyRequest; propertiesWindow?.CloseResolved(); return Result(new { closed=true });
        });
        Register(r,"properties_state","Read the pinned Properties window identity, content and current editable fields.",false,[],_ =>
            Result(new { open=propertiesWindow != null, document=propertiesWindow?.Document?.SessionId, content=propertiesWindow?.CurrentJson, fields=((FieldEditor?)propertiesWindow?.AnimationFields ?? propertiesWindow?.PickupFields)?.DescribeAutomationFields() }));
        Register(r,"scene_properties","Inspect a scene node or open its Properties window, including editable mission pickups.",true,
            [PreviewParameter,P("node","integer","Scene node index.",true),P("open","boolean","Open the pinned Properties window.")],a=>
        {
            var viewport=TargetViewport(a); int node=Int(a,"node"); var data=viewport.PreviewScene;
            if(data == null || node<0 || node>=data.Nodes.Count) throw new StudioCommandException("stale_record","Scene node unavailable.");
            if(Flag(a,"open"))
            {
                RequireNoDrafts(); ++propertyRequest; var w=GetPropertiesWindow(); var actor=viewport.PickupAt(node);
                bool opened=actor?.Pickup is { } pickup && shownDocument!.PickupEdits?.Find(pickup.Source) != null
                    ? w.SetPickup(shownDocument!,pickup.Source,data.Nodes[node].Name,data.Nodes[node].Metadata)
                    : w.SetReadOnly(shownDocument!,data.Nodes[node].Name,data.Nodes[node].Metadata);
                PresentProperties(w,opened);
            }
            return Result(new { data.Nodes[node].Index,data.Nodes[node].Name,data.Nodes[node].Metadata });
        });
    }
    private static void SetTab(TabControl tabs,int index)
    {
        ValidateTab(tabs, index);
        tabs.SelectedIndex=index;
    }
    private static void ValidateTab(TabControl tabs, int index)
    {
        if(index<0 || index>=tabs.Items.Count || tabs.Items[index] is not TabItem { Visibility:Visibility.Visible,IsEnabled:true }) throw new StudioCommandException("unavailable_tab","This tab is unavailable in the current workspace.");
    }
    private async Task CloseAfterResponseAsync() { await Task.Delay(250); if (!IsLoaded) return; automationCloseRequested = true; Close(); }
}
