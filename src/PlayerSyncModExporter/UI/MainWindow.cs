using System.Diagnostics;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using PlayerSyncModExporter.Core.Safety;
using PlayerSyncModExporter.Interop;
using PlayerSyncModExporter.Services;

namespace PlayerSyncModExporter.UI;

public sealed class MainWindow : Window
{
    private const string ConflictPopup = "Package already exists##psme";

    private static readonly Vector4 Green = new(0.45f, 0.85f, 0.45f, 1f);
    private static readonly Vector4 Orange = new(1f, 0.7f, 0.3f, 1f);
    private static readonly Vector4 Red = new(1f, 0.4f, 0.4f, 1f);
    private static readonly Vector4 Grey = new(0.6f, 0.6f, 0.6f, 1f);
    private static readonly Vector4 Yellow = new(1f, 0.9f, 0.4f, 1f);

    private readonly ExportSession _session;
    private readonly PlayerSyncAdapter _playerSync;
    private readonly PenumbraAdapter _penumbra;
    private readonly Configuration _config;
    private readonly Action _saveConfig;
    private readonly ITargetManager _targets;
    private readonly FileDialogManager _fileDialog = new();
    private readonly string _version;
    private bool _showOnlyProblems;
    private string _filter = string.Empty;

    public MainWindow(ExportSession session, PlayerSyncAdapter playerSync, PenumbraAdapter penumbra, Configuration config,
        Action saveConfig, ITargetManager targets, string version)
        : base("PlayerSync Mod Exporter###PlayerSyncModExporterMain")
    {
        _session = session;
        _playerSync = playerSync;
        _penumbra = penumbra;
        _config = config;
        _saveConfig = saveConfig;
        _targets = targets;
        _version = version;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(820, 520), MaximumSize = new Vector2(4000, 4000) };
    }

    public override void Draw()
    {
        _session.Tick();
        DrawStatusBar();
        ImGui.Separator();

        var avail = ImGui.GetContentRegionAvail();
        if (ImGui.BeginChild("##left", new Vector2(240, avail.Y), true, ImGuiWindowFlags.None))
            DrawCharacterList();
        ImGui.EndChild();
        ImGui.SameLine();
        if (ImGui.BeginChild("##right", new Vector2(0, avail.Y), false, ImGuiWindowFlags.None))
            DrawDetails();
        ImGui.EndChild();

        _fileDialog.Draw();
    }

    private void DrawStatusBar()
    {
        var status = _playerSync.Status;
        var (color, text) = status.State switch
        {
            PlayerSyncState.Ready => (Green, $"PlayerSync {status.Version}: connected"),
            PlayerSyncState.NotInstalled => (Red, "PlayerSync is not installed"),
            PlayerSyncState.NotLoaded => (Orange, $"PlayerSync {status.Version}: disabled"),
            _ => (Red, $"PlayerSync {status.Version}: incompatible"),
        };
        ImGui.TextColored(color, text);
        if (status.Messages.Count > 0 && ImGui.IsItemHovered())
            ImGui.SetTooltip(string.Join("\n", status.Messages));
        ImGui.SameLine();
        ImGui.TextColored(_penumbra.IsAvailable ? Green : Red, _penumbra.IsAvailable ? "  Penumbra: available" : "  Penumbra: unavailable");
        ImGui.SameLine();
        ImGui.TextDisabled("  Only export characters whose owner has given you permission.");
    }

    private void DrawCharacterList()
    {
        ImGui.Text("Visible PlayerSync pairs");
        if (ImGui.Button("Refresh", new Vector2(-1, 0)))
        {
            _playerSync.Reset();
            _session.Tick(force: true);
        }

        if (ImGui.Button("Select my target", new Vector2(-1, 0)))
        {
            var target = _targets.Target;
            var match = target == null ? null : _session.Pairs.FirstOrDefault(p => p.Address == target.Address);
            if (match != null)
                _session.Select(match);
            else
                _session.Log("Your target is not a visible PlayerSync pair.");
        }

        ImGui.Separator();
        if (!_playerSync.Status.IsReady)
        {
            ImGui.TextWrapped("PlayerSync must be installed, enabled and compatible to list characters.");
            foreach (var m in _playerSync.Status.Messages)
                ImGui.TextWrapped("- " + m);
            return;
        }

        if (_session.Pairs.Count == 0)
            ImGui.TextWrapped("No paired characters are visible right now. Characters appear here when PlayerSync is applying their appearance near you.");

        foreach (var pair in _session.Pairs)
        {
            var label = pair.PlayerName + (pair.IsPaused ? " (paused)" : !pair.HasData ? " (no data yet)" : string.Empty);
            if (ImGui.Selectable($"{label}##{pair.Address}", ReferenceEquals(_session.Selected?.PairHandle, pair.PairHandle), ImGuiSelectableFlags.None, Vector2.Zero))
                _session.Select(pair);
        }
    }

    private void DrawDetails()
    {
        if (_session.Selected is not { } selected)
        {
            ImGui.TextWrapped("Select a character on the left. Nothing is captured until you press Capture.");
            DrawLog();
            return;
        }

        ImGui.Text($"Selected: {selected.PlayerName}");
        var busy = _session.Phase is SessionPhase.Analyzing or SessionPhase.Exporting;
        ImGui.BeginDisabled(busy || selected.IsPaused || !selected.HasData);
        if (ImGui.Button(_session.Analysis == null ? "Capture and analyze" : "Recapture and analyze", Vector2.Zero))
            _session.CaptureAndAnalyze();
        ImGui.EndDisabled();
        if (selected.IsPaused)
        {
            ImGui.SameLine();
            ImGui.TextColored(Orange, "Paused in PlayerSync: capture disabled.");
        }

        if (_session.AppearanceChangedSinceCapture)
        {
            ImGui.SameLine();
            ImGui.TextColored(Yellow, "Their appearance changed since this capture. Recapture to export the current state.");
        }

        if (_session.LastError is { } error)
            ImGui.TextColored(Red, error);

        if (_session.Phase == SessionPhase.Analyzing)
        {
            var p = _session.AnalysisProgress;
            var fraction = p == null || p.Total == 0 ? 0 : p.Completed / (float)p.Total;
            ImGui.ProgressBar(fraction, new Vector2(-90, 0), p == null ? "Analyzing..." : $"Verifying {p.Completed}/{p.Total}");
            ImGui.SameLine();
            if (ImGui.Button("Cancel##analysis", Vector2.Zero))
                _session.Cancel();
        }

        if (_session.Analysis is { } analysis)
        {
            DrawSummary(analysis);
            DrawAssetTable(analysis);
            DrawExportPanel(analysis);
        }

        DrawLog();
    }

    private void DrawSummary(Core.Analysis.AnalysisResult analysis)
    {
        ImGui.Separator();
        int Count(AssetStatus s) => analysis.Count(s);
        var available = Count(AssetStatus.Available);
        var compressed = analysis.Assets.Count(a => a.Status == AssetStatus.Available && !a.IsOriginalContent);
        ImGui.Text($"{analysis.Assets.Count} unique files: {available} available, {Count(AssetStatus.Missing)} missing, "
                   + $"{Count(AssetStatus.HashMismatch) + Count(AssetStatus.Unreadable)} corrupt/unreadable, "
                   + $"{Count(AssetStatus.NotSynced)} not synced, {Count(AssetStatus.ForbiddenByServer)} forbidden, {Count(AssetStatus.InvalidMetadata)} invalid. "
                   + $"{analysis.FileSwaps.Count} file swaps.");
        if (!analysis.AllRequiredAvailable || compressed > 0)
        {
            ImGui.PushStyleColor(ImGuiCol.Text, Orange);
            ImGui.TextWrapped("INCOMPLETE: not every file of this appearance is available with its original content. "
                              + "An export will be marked Partial and will not fully reproduce the look. "
                              + (compressed > 0 ? $"{compressed} texture(s) are only available as PlayerSync's compressed versions. " : string.Empty));
            ImGui.PopStyleColor();
        }

        if (_session.LiveCrossCheck is { } live)
            ImGui.TextDisabled(live);
        foreach (var w in analysis.Warnings.Take(3))
            ImGui.TextColored(Yellow, w);
        if (analysis.Warnings.Count > 3)
            ImGui.TextDisabled($"... {analysis.Warnings.Count - 3} more warnings in the log and manifest.");
    }

    private void DrawAssetTable(Core.Analysis.AnalysisResult analysis)
    {
        var editable = _session.Phase == SessionPhase.Ready;
        ImGui.BeginDisabled(!editable);
        if (ImGui.Button("Select all available", Vector2.Zero))
            foreach (var a in analysis.Assets.Where(a => a.IsExportable && (_config.IncludeNonPlayerObjects || a.Kinds.Contains(SyncObjectKind.Player))))
                a.Include = true;
        ImGui.SameLine();
        if (ImGui.Button("Select none", Vector2.Zero))
            foreach (var a in analysis.Assets)
                a.Include = false;
        ImGui.SameLine();
        if (ImGui.Button("Exclude compressed versions", Vector2.Zero))
            foreach (var a in analysis.Assets.Where(a => !a.IsOriginalContent))
                a.Include = false;
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Checkbox("Only problems", ref _showOnlyProblems);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Filter##assets", ref _filter, 128, ImGuiInputTextFlags.None, (ImGui.ImGuiInputTextCallbackDelegate?)null);

        var flags = ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.ScrollY | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp;
        if (!ImGui.BeginTable("##assets", 6, flags, new Vector2(0, 260), 0))
            return;
        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("Use", ImGuiTableColumnFlags.WidthFixed, 32, 0);
        ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 110, 0);
        ImGui.TableSetupColumn("Game path", ImGuiTableColumnFlags.WidthStretch, 4, 0);
        ImGui.TableSetupColumn("Object", ImGuiTableColumnFlags.WidthFixed, 80, 0);
        ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 70, 0);
        ImGui.TableSetupColumn("Source", ImGuiTableColumnFlags.WidthFixed, 110, 0);
        ImGui.TableHeadersRow();

        foreach (var asset in analysis.Assets)
        {
            if (_showOnlyProblems && asset.Status == AssetStatus.Available && asset.IsOriginalContent)
                continue;
            if (_filter.Length > 0 && !asset.GamePaths.Any(p => p.GamePath.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                && !asset.Hash.Contains(_filter, StringComparison.OrdinalIgnoreCase))
                continue;

            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.BeginDisabled(!editable || !asset.IsExportable);
            var include = asset.Include;
            if (ImGui.Checkbox($"##inc{asset.Hash}", ref include))
                asset.Include = include && asset.IsExportable;
            ImGui.EndDisabled();

            ImGui.TableNextColumn();
            var (color, text) = StatusText(asset);
            ImGui.TextColored(color, text);
            if (asset.Note != null && ImGui.IsItemHovered())
                ImGui.SetTooltip(asset.Note);

            ImGui.TableNextColumn();
            ImGui.Text(asset.DisplayName + (asset.GamePaths.Count > 1 ? $"  (+{asset.GamePaths.Count - 1})" : string.Empty));
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip($"Hash {asset.Hash}\n{string.Join("\n", asset.GamePaths.Take(20).Select(p => p.GamePath))}"
                                 + (asset.SourcePath != null ? $"\nLocal: {asset.SourcePath}" : string.Empty)
                                 + $"\nSharing status: {asset.Sharing}");

            ImGui.TableNextColumn();
            ImGui.Text(string.Join(",", asset.Kinds.Select(k => k == SyncObjectKind.MinionOrMount ? "Mount" : k.ToString())));
            ImGui.TableNextColumn();
            ImGui.Text(asset.Size > 0 ? FormatBytes(asset.Size) : "-");
            ImGui.TableNextColumn();
            ImGui.Text(asset.Provenance switch
            {
                AssetProvenance.PlayerSyncCache => "PlayerSync cache",
                AssetProvenance.PenumbraModDirectory => "Your Penumbra",
                _ => "-",
            });
        }

        ImGui.EndTable();
    }

    private void DrawExportPanel(Core.Analysis.AnalysisResult analysis)
    {
        ImGui.Separator();
        var editable = _session.Phase == SessionPhase.Ready;
        var plan = _session.PreviewPlan();
        if (plan != null)
            ImGui.Text($"Will include {plan.Files.Count} files ({FormatBytes(plan.TotalBytes)}), {plan.Redirections.Count} game paths and {plan.FileSwaps.Count} file swaps.");

        ImGui.BeginDisabled(!editable);
        var changed = false;
        var pmp = _config.WritePmp;
        changed |= ImGui.Checkbox("Penumbra mod (.pmp)", ref pmp);
        ImGui.SameLine();
        var mcdf = _config.WriteMcdf;
        changed |= ImGui.Checkbox("PlayerSync character file (.mcdf)", ref mcdf);
        ImGui.SameLine();
        var appearance = _config.IncludeAppearanceData;
        changed |= ImGui.Checkbox("Glamourer/Customize+/meta data", ref appearance);
        var nonPlayer = _config.IncludeNonPlayerObjects;
        changed |= ImGui.Checkbox("Include minion, mount and pet files", ref nonPlayer);
        if (changed)
        {
            _config.WritePmp = pmp;
            _config.WriteMcdf = mcdf;
            _config.IncludeAppearanceData = appearance;
            _config.IncludeNonPlayerObjects = nonPlayer;
            _saveConfig();
        }

        var dir = _config.ExportDirectory;
        ImGui.SetNextItemWidth(-120);
        if (ImGui.InputText("##dest", ref dir, 512, ImGuiInputTextFlags.None, (ImGui.ImGuiInputTextCallbackDelegate?)null))
        {
            _config.ExportDirectory = dir;
            _saveConfig();
        }

        ImGui.SameLine();
        if (ImGui.Button("Choose folder", Vector2.Zero))
            _fileDialog.OpenFolderDialog("Choose export folder", (ok, path) =>
            {
                if (!ok)
                    return;
                _config.ExportDirectory = path;
                _saveConfig();
            }, string.IsNullOrEmpty(_config.ExportDirectory) ? null : _config.ExportDirectory, false);

        var name = _session.PackageName;
        ImGui.SetNextItemWidth(-120);
        if (ImGui.InputText("Package name", ref name, 120, ImGuiInputTextFlags.None, (ImGui.ImGuiInputTextCallbackDelegate?)null))
            _session.PackageName = name;
        if (_session.ValidateDestination() is { } destError)
            ImGui.TextColored(Orange, destError);

        DrawConsent();
        ImGui.EndDisabled();

        var canExport = editable && _session.Consent.IsComplete && _session.ValidateDestination() == null
                        && plan is { } p && (p.Files.Count > 0 || p.FileSwaps.Count > 0) && (_config.WritePmp || _config.WriteMcdf);
        ImGui.BeginDisabled(!canExport);
        if (ImGui.Button("Export", new Vector2(160, 0)))
        {
            if (_session.DestinationExists())
                ImGui.OpenPopup(ConflictPopup, ImGuiPopupFlags.None);
            else
                _session.StartExport(ExistingPackagePolicy.Fail, _version);
        }

        ImGui.EndDisabled();
        DrawConflictPopup();

        if (_session.Phase == SessionPhase.Exporting && _session.ExportProgress is { } ep)
        {
            ImGui.SameLine();
            var fraction = ep.BytesTotal == 0 ? 0 : ep.BytesDone / (float)ep.BytesTotal;
            ImGui.ProgressBar(fraction, new Vector2(-90, 0), $"{ep.Stage}: {FormatBytes(ep.BytesDone)} / {FormatBytes(ep.BytesTotal)}");
            ImGui.SameLine();
            if (ImGui.Button("Cancel##export", Vector2.Zero))
                _session.Cancel();
        }

        if (_session.LastExport is { } result)
        {
            var complete = result.Manifest.Completeness.Status == CompletenessStatus.Complete;
            ImGui.TextColored(complete ? Green : Orange, $"Exported ({result.Manifest.Completeness.Status}) and verified: {result.PackageDirectory}");
            foreach (var reason in result.Manifest.Completeness.Reasons)
                ImGui.TextColored(Orange, "- " + reason);
            if (ImGui.Button("Open folder", Vector2.Zero))
                OpenFolder(result.PackageDirectory);
        }
    }

    private void DrawConsent()
    {
        ImGui.Spacing();
        ImGui.TextColored(Yellow, "Permission (required for every export)");
        ImGui.TextWrapped("Being able to see these files in your cache does not mean you may share them. The exporter cannot know each mod's license.");
        var c = _session.Consent;
        bool permission = c.PermissionConfirmed, redistribution = c.RedistributionAcknowledged, unknown = c.UnknownSharingStatusAcknowledged;
        var changed = ImGui.Checkbox(ExportConsent.PermissionStatement, ref permission);
        ImGui.PushTextWrapPos(ImGui.GetContentRegionAvail().X);
        changed |= ImGui.Checkbox("I understand that mod files remain the work of their creators, and I will not redistribute paid or restricted mods against their terms.", ref redistribution);
        changed |= ImGui.Checkbox("I understand that every file's sharing status is unknown, and I have excluded anything I am not allowed to share.", ref unknown);
        ImGui.PopTextWrapPos();
        if (changed)
            _session.Consent = c with
            {
                PermissionConfirmed = permission, RedistributionAcknowledged = redistribution, UnknownSharingStatusAcknowledged = unknown,
            };
    }

    private void DrawConflictPopup()
    {
        if (!ImGui.BeginPopupModal(ConflictPopup, ImGuiWindowFlags.AlwaysAutoResize))
            return;
        ImGui.Text("A folder with this package name already exists.");
        if (ImGui.Button("Replace previous export", Vector2.Zero))
        {
            _session.StartExport(ExistingPackagePolicy.ReplaceExisting, _version);
            ImGui.CloseCurrentPopup();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Only allowed if that folder is an earlier export from this tool.");
        ImGui.SameLine();
        if (ImGui.Button("Keep both", Vector2.Zero))
        {
            _session.StartExport(ExistingPackagePolicy.CreateNumberedCopy, _version);
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel", Vector2.Zero))
            ImGui.CloseCurrentPopup();
        ImGui.EndPopup();
    }

    private void DrawLog()
    {
        ImGui.Spacing();
        if (!ImGui.CollapsingHeader("Log", ImGuiTreeNodeFlags.None))
            return;
        if (ImGui.BeginChild("##log", new Vector2(0, 140), true, ImGuiWindowFlags.HorizontalScrollbar))
            foreach (var line in _session.LogLines)
                ImGui.TextUnformatted(line);
        ImGui.EndChild();
    }

    private static (Vector4, string) StatusText(AssetEntry a) => a.Status switch
    {
        AssetStatus.Available when !a.IsOriginalContent => (Yellow, "Compressed"),
        AssetStatus.Available => (Green, "Available"),
        AssetStatus.Missing => (Orange, "Missing"),
        AssetStatus.HashMismatch => (Red, "Corrupt"),
        AssetStatus.Unreadable => (Red, "Unreadable"),
        AssetStatus.NotSynced => (Grey, "Not synced"),
        AssetStatus.ForbiddenByServer => (Red, "Forbidden"),
        _ => (Red, "Invalid"),
    };

    private static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.00} GiB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MiB",
        >= 1L << 10 => $"{bytes / 1024d:0.0} KiB",
        _ => $"{bytes} B",
    };

    private static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception)
        {
            // Opening Explorer is a convenience; the path is shown above.
        }
    }
}
