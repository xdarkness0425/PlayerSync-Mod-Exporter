using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using PlayerSyncModExporter.Core.Analysis;
using PlayerSyncModExporter.Core.Hashing;
using PlayerSyncModExporter.Core.IO;
using PlayerSyncModExporter.Core.Meta;
using PlayerSyncModExporter.Core.Metadata;
using PlayerSyncModExporter.Core.Model;
using PlayerSyncModExporter.Core.Packaging;
using PlayerSyncModExporter.Core.Safety;
using PlayerSyncModExporter.Core.SourceQuality;
using PlayerSyncModExporter.Interop;

namespace PlayerSyncModExporter.Services;

public enum SessionPhase
{
    Idle,
    Analyzing,
    Ready,
    DownloadingOriginals,
    Exporting,
}

/// <summary>
/// UI-independent workflow state: select -> capture -> analyze -> review -> confirm -> export.
/// Everything that touches game or plugin objects runs on the framework thread; hashing and file I/O run on the thread pool.
/// </summary>
public sealed class ExportSession : IDisposable
{
    private const int MaxLogLines = 300;

    /// <summary>A source-quality download that has not finished by then is cancelled.</summary>
    public static readonly TimeSpan SourceQualityTimeout = TimeSpan.FromMinutes(15);

    private readonly PlayerSyncAdapter _playerSync;
    private readonly PenumbraAdapter _penumbra;
    private readonly IObjectTable _objects;
    private readonly IPluginLog _log;
    private readonly Configuration _config;
    private readonly FileHashCache _hashCache = new();
    private readonly List<string> _logLines = [];
    private readonly object _lock = new();

    private CancellationTokenSource? _cts;
    private object? _capturedDataReference;
    private DateTime _lastPairRefresh = DateTime.MinValue;
    private OriginalsStaging? _originals;
    private SourceQualityDownloadResult? _originalsResult;
    private string? _originalsRequestId;
    private DateTime _originalsDeadline;

    public ExportSession(PlayerSyncAdapter playerSync, PenumbraAdapter penumbra, IObjectTable objects, IPluginLog log, Configuration config)
    {
        _playerSync = playerSync;
        _penumbra = penumbra;
        _objects = objects;
        _log = log;
        _config = config;
    }

    public SessionPhase Phase { get; private set; }
    public IReadOnlyList<SyncedCharacter> Pairs { get; private set; } = [];
    public SyncedCharacter? Selected { get; private set; }
    public AnalysisResult? Analysis { get; private set; }
    public AnalysisProgress? AnalysisProgress { get; private set; }
    public ExportProgress? ExportProgress { get; private set; }
    public ExportResult? LastExport { get; private set; }
    public string? LastError { get; private set; }
    public string? LiveCrossCheck { get; private set; }
    public bool AppearanceChangedSinceCapture { get; private set; }
    public ExportConsent Consent { get; set; } = new();
    public string PackageName { get; set; } = string.Empty;

    /// <summary>Originals finished and requested so far while <see cref="SessionPhase.DownloadingOriginals"/>.</summary>
    public (int Finished, int Requested)? SourceQualityProgress { get; private set; }

    public IReadOnlyList<string> LogLines
    {
        get
        {
            lock (_lock)
                return _logLines.ToList();
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        DiscardOriginals(); // plugin shutdown: cancel PlayerSync's request and delete this export's staged originals
    }

    /// <summary>Refreshes the pair list at most once a second. Framework thread only.</summary>
    public void Tick(bool force = false)
    {
        if (!force && DateTime.UtcNow - _lastPairRefresh < TimeSpan.FromSeconds(1))
            return;
        _lastPairRefresh = DateTime.UtcNow;

        try
        {
            Pairs = _playerSync.GetVisiblePairs();
        }
        catch (Exception e)
        {
            Pairs = [];
            _log.Warning(e, "Reading PlayerSync pairs failed");
        }

        if (Phase == SessionPhase.DownloadingOriginals)
            PollOriginals();

        if (Selected != null)
        {
            var current = Pairs.FirstOrDefault(p => ReferenceEquals(p.PairHandle, Selected.PairHandle));
            if (current != null && _capturedDataReference != null)
            {
                var data = Core.Model.PlayerSyncDataReader.GetProperty(current.PairHandle, "LastReceivedCharacterData");
                AppearanceChangedSinceCapture = !ReferenceEquals(data, _capturedDataReference);
            }
        }
    }

    public void Select(SyncedCharacter? character)
    {
        if (Phase is SessionPhase.Analyzing or SessionPhase.Exporting or SessionPhase.DownloadingOriginals)
            return;
        if (ReferenceEquals(character?.PairHandle, Selected?.PairHandle))
            return;
        Selected = character;
        DiscardOriginals(); // staged originals belong to one character
        ClearCapture();
    }

    private void ClearCapture()
    {
        Analysis = null;
        LastExport = null;
        LastError = null;
        LiveCrossCheck = null;
        AppearanceChangedSinceCapture = false;
        _capturedDataReference = null;
        Consent = new ExportConsent(); // consent is per capture, never carried over to another character
        Phase = SessionPhase.Idle;
    }

    /// <summary>Captures the selected character and starts analysis. Framework thread only.</summary>
    public void CaptureAndAnalyze()
    {
        if (Selected == null || Phase is SessionPhase.Analyzing or SessionPhase.Exporting or SessionPhase.DownloadingOriginals)
            return;

        ClearCapture();
        var label = BuildLabel(Selected);
        var captured = _playerSync.Capture(Selected, label, out var error, _originalsResult);
        if (captured == null)
        {
            Fail(error ?? "Capture failed.");
            return;
        }

        _capturedDataReference = Core.Model.PlayerSyncDataReader.GetProperty(Selected.PairHandle, "LastReceivedCharacterData");
        PackageName = PathSafety.SanitizeFileName($"{label} {captured.CapturedAtUtc:yyyy-MM-dd HHmm}");
        var objectIndex = FindObjectIndex(Selected.Address);
        var live = objectIndex is { } idx ? _penumbra.GetResolvedFilesFor(idx) : null;
        var locators = _playerSync.CreateLocators(_penumbra.GetModDirectory());
        if (!string.IsNullOrWhiteSpace(_config.AdditionalResourceDirectory)
            && Directory.Exists(_config.AdditionalResourceDirectory))
            locators.Add(new ReadOnlyResourceFolderLocator(_config.AdditionalResourceDirectory));
        if (_originals != null && _originalsResult != null)
            locators.Insert(0, new StagedOriginalsLocator(_originals.Path, _originalsResult));
        Log($"Captured '{label}': {captured.Files.Count} file entries, {captured.FileSwaps.Count} swaps, {captured.NotSyncedFiles.Count} not synced. Sources: {string.Join(", ", locators.Select(l => l.Name))}.");
        foreach (var w in captured.CaptureWarnings)
            Log("Warning: " + w);
        Log("Texture source: " + captured.TextureSource.Describe());

        var cts = ResetCts();
        Phase = SessionPhase.Analyzing;
        var progress = new Progress<AnalysisProgress>(p => AnalysisProgress = p);
        var modDirectory = _penumbra.GetModDirectory();
        Task.Run(async () =>
        {
            try
            {
                // Read-only: installed mods are the only source of original mod names and option groups.
                var library = await ModLibraryIndex.BuildAsync(modDirectory, token: cts.Token).ConfigureAwait(false);
                Log($"Indexed {library.Mods.Count} installed Penumbra mods for name and option recovery"
                    + (library.Problems.Count > 0 ? $" ({library.Problems.Count} could not be read)." : "."));
                var options = new AssetAnalyzerOptions { PreselectNonPlayerObjects = _config.IncludeNonPlayerObjects, ModLibrary = library };
                var result = await new AssetAnalyzer(locators, _hashCache).AnalyzeAsync(captured, options, progress, cts.Token).ConfigureAwait(false);
                if (live != null)
                {
                    var available = result.Assets.Where(a => a.SourcePath != null).ToList();
                    var loaded = available.Count(a => live.Contains(a.SourcePath!));
                    LiveCrossCheck = $"Penumbra currently resolves {loaded} of {available.Count} available files for this actor (animations, sounds and VFX only appear after they play).";
                }

                Analysis = result;
                Phase = SessionPhase.Ready;
                Log($"Analysis: {result.Count(AssetStatus.Available)} available, {result.Count(AssetStatus.Missing)} missing, "
                    + $"{result.Count(AssetStatus.HashMismatch)} corrupt, {result.Count(AssetStatus.NotSynced)} not synced, "
                    + $"{result.Count(AssetStatus.ForbiddenByServer)} forbidden.");
                if (result.Metadata.SourceMods.Count > 0)
                    Log($"Matched files to {result.Metadata.SourceMods.Count} installed mod(s): "
                        + string.Join(", ", result.Metadata.SourceMods.Select(m => m.Mod.DisplayName)) + ".");
            }
            catch (OperationCanceledException)
            {
                Phase = SessionPhase.Idle;
                Log("Analysis cancelled.");
            }
            catch (Exception e)
            {
                _log.Error(e, "Analysis failed");
                Fail("Analysis failed: " + e.Message);
            }
        });
    }

    public void Cancel()
    {
        if (Phase == SessionPhase.DownloadingOriginals)
        {
            DiscardOriginals();
            Phase = Analysis != null ? SessionPhase.Ready : SessionPhase.Idle;
            Log("Download of originals cancelled; nothing was kept.");
        }

        _cts?.Cancel();
    }

    /// <summary>Why "Download originals" cannot be used right now, or null.</summary>
    public string? OriginalsUnavailableReason()
    {
        if (Analysis == null || Selected == null || Phase != SessionPhase.Ready)
            return "Capture the character first.";
        if (!SourceQualityRequestPlan.From(Analysis).IsNeeded)
            return "No compressed textures are in use.";
        if (Selected.IsPaused)
            return "The pair is paused.";
        if (!_playerSync.SourceQualityExportAvailable())
            return "The installed PlayerSync does not provide the source-quality export interface (see docs/SOURCE_QUALITY.md).";
        if (ValidateDestination() is { } destError)
            return destError + " Originals are staged inside it.";
        return null;
    }

    /// <summary>
    /// Asks PlayerSync to download, at source quality, the originals of the compressed textures in this capture into a staging
    /// folder inside the export folder. PlayerSync's settings, the pair's applied appearance and PlayerSync's cache are not
    /// changed. When it finishes the character is captured and analysed again with the staged originals. Framework thread only.
    /// </summary>
    public void StartOriginalsDownload()
    {
        if (OriginalsUnavailableReason() is { } reason)
        {
            Fail(reason);
            return;
        }

        var plan = SourceQualityRequestPlan.From(Analysis!);
        DiscardOriginals();
        try
        {
            OriginalsStaging.CleanupAbandoned(_config.ExportDirectory);
            _originals = OriginalsStaging.Create(_config.ExportDirectory);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Fail("Could not create the staging folder for originals: " + e.Message);
            return;
        }

        var requestId = _playerSync.StartSourceQualityExport(Selected!, _originals.Path, plan.Hashes, out var error);
        if (requestId == null)
        {
            DiscardOriginals();
            Fail("PlayerSync refused the request: " + error);
            return;
        }

        _originalsRequestId = requestId;
        _originalsDeadline = DateTime.UtcNow + SourceQualityTimeout;
        SourceQualityProgress = (0, plan.Hashes.Count);
        LastError = null;
        Phase = SessionPhase.DownloadingOriginals;
        Log($"Asked PlayerSync for the source-quality originals of {plan.Hashes.Count} texture(s) for this export.");
    }

    private void PollOriginals()
    {
        if (_originalsRequestId == null)
            return;
        var status = _playerSync.GetSourceQualityExportStatus(_originalsRequestId);
        SourceQualityProgress = (status.Finished, Math.Max(status.Requested, SourceQualityProgress?.Requested ?? 0));
        if (!status.IsFinished && DateTime.UtcNow < _originalsDeadline)
            return;

        if (!status.IsFinished)
            status = status with { State = "Failed", Error = $"PlayerSync did not finish within {SourceQualityTimeout.TotalMinutes} minutes" };
        Log(status.Describe());
        foreach (var f in status.Files.Where(f => !f.IsAvailable))
            Log($"  {f.Hash[..Math.Min(8, f.Hash.Length)]}: {f.Status}{(f.Message is { Length: > 0 } m ? " - " + m : string.Empty)}");

        Phase = Analysis != null ? SessionPhase.Ready : SessionPhase.Idle;
        SourceQualityProgress = null;
        if (status.State != "Completed")
        {
            DiscardOriginals();
            Fail(status.Describe());
            return;
        }

        _playerSync.CancelSourceQualityExport(_originalsRequestId); // forget the finished request; the staged files stay
        _originalsRequestId = null;
        _originalsResult = status;
        CaptureAndAnalyze();
    }

    /// <summary>Cancels a running request and deletes this export's staged originals (only that folder).</summary>
    private void DiscardOriginals()
    {
        if (_originalsRequestId != null)
            _playerSync.CancelSourceQualityExport(_originalsRequestId);
        _originalsRequestId = null;
        _originalsResult = null;
        SourceQualityProgress = null;
        _originals?.Dispose();
        _originals = null;
    }

    public PackagePlan? PreviewPlan()
        => Analysis == null ? null : PackagePlan.Create(Analysis, _config.IncludeNonPlayerObjects);

    public bool DestinationExists()
        => !string.IsNullOrWhiteSpace(_config.ExportDirectory)
           && Directory.Exists(Path.Combine(_config.ExportDirectory, PathSafety.SanitizeFileName(PackageName, "PlayerSync Export")));

    /// <summary>Returns an error message when the destination would mix the export into PlayerSync's or Penumbra's folders.</summary>
    public string? ValidateDestination()
    {
        var dest = _config.ExportDirectory;
        if (string.IsNullOrWhiteSpace(dest) || !Path.IsPathRooted(dest))
            return "Choose an export folder.";
        foreach (var protectedRoot in new[] { _playerSync.GetCacheFolder(), _penumbra.GetModDirectory(), _config.AdditionalResourceDirectory })
        {
            if (string.IsNullOrWhiteSpace(protectedRoot))
                continue;
            if (PathSafety.IsUnderRoot(protectedRoot, dest) || string.Equals(Path.GetFullPath(protectedRoot).TrimEnd('\\', '/'), Path.GetFullPath(dest).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)
                || PathSafety.IsUnderRoot(dest, protectedRoot))
                return "The export folder must be outside the PlayerSync cache and Penumbra's mod directory.";
        }

        return null;
    }

    public void StartExport(ExistingPackagePolicy policy, string generatorVersion)
    {
        if (Analysis == null || Phase != SessionPhase.Ready)
            return;
        if (!Consent.IsComplete)
        {
            Fail("Confirm permission and the redistribution notice before exporting.");
            return;
        }

        if (ValidateDestination() is { } destError)
        {
            Fail(destError);
            return;
        }

        var request = new ExportRequest
        {
            Analysis = Analysis,
            DestinationRoot = _config.ExportDirectory,
            PackageName = PackageName,
            Consent = Consent with { ConfirmedAtUtc = DateTimeOffset.UtcNow },
            WritePmp = _config.WritePmp,
            WriteMcdf = _config.WriteMcdf,
            IncludeAppearanceData = _config.IncludeAppearanceData,
            IncludeNonPlayerObjects = _config.IncludeNonPlayerObjects,
            ExistingPolicy = policy,
            GeneratorVersion = generatorVersion,
        };

        var cts = ResetCts();
        Phase = SessionPhase.Exporting;
        LastError = null;
        LastExport = null;
        var progress = new Progress<ExportProgress>(p => ExportProgress = p);
        var converter = new ChainedMetaConverter(new Version0MetaConverter(), _penumbra.MetaConverter);
        Task.Run(async () =>
        {
            try
            {
                StagingDirectory.CleanupAbandoned(request.DestinationRoot, TimeSpan.FromHours(1));
                var result = await new ExportPackager(converter).ExportAsync(request, progress, cts.Token).ConfigureAwait(false);
                LastExport = result;
                Log($"Exported to {result.PackageDirectory} ({result.Manifest.Completeness.Status}, {result.Manifest.Summary.IncludedFiles} files).");
                foreach (var note in result.VerificationNotes)
                    Log(note);
                foreach (var reason in result.Manifest.Completeness.Reasons)
                    Log("Incomplete: " + reason);
            }
            catch (OperationCanceledException)
            {
                Log("Export cancelled. No files were kept.");
            }
            catch (ConsentRequiredException e)
            {
                Fail(e.Message);
            }
            catch (ExportFailedException e)
            {
                Fail(e.Message);
            }
            catch (Exception e)
            {
                _log.Error(e, "Export failed");
                Fail("Export failed unexpectedly: " + e.Message);
            }
            finally
            {
                Phase = Analysis != null ? SessionPhase.Ready : SessionPhase.Idle;
            }
        });
    }

    private CancellationTokenSource ResetCts()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        return _cts = new CancellationTokenSource();
    }

    private void Fail(string message)
    {
        LastError = message;
        if (Phase == SessionPhase.Analyzing)
            Phase = SessionPhase.Idle;
        Log("Error: " + message);
    }

    public void Log(string line)
    {
        _log.Information(line);
        lock (_lock)
        {
            _logLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
            if (_logLines.Count > MaxLogLines)
                _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
        }
    }

    private ushort? FindObjectIndex(nint address)
    {
        foreach (var obj in _objects)
            if (obj.Address == address)
                return obj.ObjectIndex;
        return null;
    }

    private string BuildLabel(SyncedCharacter character)
    {
        if (!_config.IncludeWorldInLabel)
            return character.PlayerName;
        foreach (var obj in _objects)
        {
            if (obj.Address != character.Address || obj is not IPlayerCharacter pc)
                continue;
            try
            {
                var world = pc.HomeWorld.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(world))
                    return $"{character.PlayerName}@{world}";
            }
            catch (Exception)
            {
                // World sheet unavailable; fall back to the name.
            }
        }

        return character.PlayerName;
    }
}
