using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video;
using Shoko.Abstractions.Video.Events;
using Shoko.Abstractions.Video.Enums;
using Shoko.Abstractions.Video.Services;
using ShokoIntegrityChecker.Models;

namespace ShokoIntegrityChecker.Services;

/// <inheritdoc cref="IIntegrityCheckService" />
public sealed class IntegrityCheckService : IIntegrityCheckService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private static readonly TimeSpan PersistInterval = TimeSpan.FromSeconds(5);
    private const int PersistEveryProcessedFiles = 25;
    private const int QueueSubmissionWindow = 16;

    private readonly IVideoService _videoService;

    private readonly IVideoHashingService _hashingService;

    private readonly ISystemService _systemService;

    private readonly ILogger<IntegrityCheckService> _logger;

    /// <summary>
    /// Where the most recent run's results and in-progress checkpoints are
    /// persisted as JSON, so they survive a server restart or plugin reload. Lives at
    /// <c>{DataPath}/configuration/{PluginID}/results.json</c> — the same
    /// per-plugin directory convention the host uses (and purges on uninstall).
    /// </summary>
    private readonly string _resultsFilePath;

    private readonly Lock _stateLock = new();

    private readonly ConcurrentBag<IntegrityCheckIssue> _mismatches = [];

    private readonly ConcurrentDictionary<int, PendingHashWork> _pendingHashOperations = [];

    private CancellationTokenSource? _cancellationTokenSource;

    private Task? _runningTask;

    private bool _isRunning;

    private DateTime? _startedAt;

    private DateTime? _completedAt;

    private bool _wasCompleted;

    private IReadOnlyList<string> _scopedFolderNames = [];

    private IReadOnlyList<int>? _scopedFolderIDs;

    private int _totalFiles;

    private int _processedFiles;

    private int _skippedFiles;

    private string? _currentFile;

    private string? _lastError;

    private DateTime _lastPersistedAtUtc = DateTime.MinValue;

    private int _lastPersistedProcessedFiles = -1;

    private int _lastPersistedMismatchCount = -1;

    /// <summary>
    /// Initializes a new instance of <see cref="IntegrityCheckService"/>.
    /// </summary>
    /// <param name="videoService">Provides access to managed folders and video files.</param>
    /// <param name="hashingService">Used to force-recompute file hashes.</param>
    /// <param name="systemService">Used to confirm the server has finished starting up.</param>
    /// <param name="applicationPaths">Used to locate the plugin's per-plugin data directory.</param>
    /// <param name="logger">Logger for this service.</param>
    public IntegrityCheckService(
        IVideoService videoService,
        IVideoHashingService hashingService,
        ISystemService systemService,
        IApplicationPaths applicationPaths,
        ILogger<IntegrityCheckService> logger)
    {
        _videoService = videoService;
        _hashingService = hashingService;
        _systemService = systemService;
        _logger = logger;
        _hashingService.VideoFileHashed += OnVideoFileHashed;

        var pluginDataDirectory = Path.Combine(applicationPaths.ConfigurationsPath, IntegrityCheckerPlugin.StaticID.ToString());
        _resultsFilePath = Path.Combine(pluginDataDirectory, "results.json");

        LoadPersistedResult(pluginDataDirectory);
    }

    /// <summary>
    /// Restores the most recently persisted run's results, if any, so the
    /// dashboard has something to show immediately after a restart instead of
    /// appearing as if no check had ever been run.
    /// </summary>
    private void LoadPersistedResult(string pluginDataDirectory)
    {
        try
        {
            Directory.CreateDirectory(pluginDataDirectory);

            if (!File.Exists(_resultsFilePath))
                return;

            var json = File.ReadAllText(_resultsFilePath);
            var persisted = JsonSerializer.Deserialize<PersistedIntegrityCheckResult>(json, SerializerOptions);
            if (persisted is null)
                return;

            lock (_stateLock)
            {
                _wasCompleted = persisted.WasCompleted;
                _startedAt = persisted.StartedAt;
                _completedAt = persisted.CompletedAt;
                _scopedFolderNames = persisted.ScopedFolderNames;
                _totalFiles = persisted.TotalFiles;
                _processedFiles = persisted.ProcessedFiles;
                _skippedFiles = persisted.SkippedFiles;
                _lastError = persisted.LastError;
            }

            foreach (var issue in persisted.Mismatches)
                _mismatches.Add(issue);

            _logger.LogInformation(
                "Restored a previous integrity check run from disk: {Mismatches} mismatch(es) recorded as of {CompletedAt}",
                persisted.Mismatches.Count,
                persisted.CompletedAt);
        }
        catch (Exception ex)
        {
            // Corrupt or unreadable results file shouldn't prevent the plugin
            // from loading — just start fresh and log it.
            _logger.LogWarning(ex, "Failed to restore persisted integrity check results from \"{Path}\"; starting fresh", _resultsFilePath);
        }
    }

    /// <summary>
    /// Writes the current run's results/checkpoint to disk so they survive a
    /// restart or plugin reload. Writes to a temporary file first and then
    /// replaces the real one, so a crash mid-write can't corrupt the
    /// persisted results.
    /// </summary>
    private PersistedIntegrityCheckResult CreateSnapshot()
    {
        lock (_stateLock)
        {
            return new()
            {
                WasCompleted = _wasCompleted,
                StartedAt = _startedAt,
                CompletedAt = _completedAt,
                ScopedFolderNames = _scopedFolderNames,
                TotalFiles = _totalFiles,
                ProcessedFiles = _processedFiles,
                SkippedFiles = _skippedFiles,
                Mismatches = [.. _mismatches.OrderByDescending(issue => issue.DetectedAt)],
                LastError = _lastError,
            };
        }
    }

    private void PersistResult(bool force)
    {
        try
        {
            var snapshot = CreateSnapshot();
            var mismatchCount = snapshot.Mismatches.Count;
            var nowUtc = DateTime.UtcNow;

            if (!force)
            {
                var nothingChanged = snapshot.ProcessedFiles == _lastPersistedProcessedFiles
                    && mismatchCount == _lastPersistedMismatchCount
                    && snapshot.WasCompleted == false;
                var intervalNotElapsed = nowUtc - _lastPersistedAtUtc < PersistInterval;
                var processedThresholdNotReached =
                    snapshot.ProcessedFiles < _lastPersistedProcessedFiles + PersistEveryProcessedFiles;
                var mismatchCountUnchanged = mismatchCount == _lastPersistedMismatchCount;

                if (nothingChanged || (intervalNotElapsed && processedThresholdNotReached && mismatchCountUnchanged))
                    return;
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var tempPath = _resultsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _resultsFilePath, overwrite: true);

            _lastPersistedAtUtc = nowUtc;
            _lastPersistedProcessedFiles = snapshot.ProcessedFiles;
            _lastPersistedMismatchCount = mismatchCount;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist integrity check results to \"{Path}\"", _resultsFilePath);
        }
    }

    /// <inheritdoc />
    public IntegrityCheckStatus GetStatus()
    {
        lock (_stateLock)
        {
            return new()
            {
                IsRunning = _isRunning,
                IsCancellationRequested = _cancellationTokenSource?.IsCancellationRequested ?? false,
                WasCompleted = _wasCompleted,
                StartedAt = _startedAt,
                CompletedAt = _completedAt,
                ScopedFolderNames = _scopedFolderNames,
                TotalFiles = _totalFiles,
                ProcessedFiles = _processedFiles,
                SkippedFiles = _skippedFiles,
                CurrentFile = _currentFile,
                Mismatches = [.. _mismatches.OrderByDescending(issue => issue.DetectedAt)],
                LastError = _lastError,
            };
        }
    }

    /// <inheritdoc />
    public PersistedIntegrityCheckResult GetExport()
        => CreateSnapshot();

    private void OnVideoFileHashed(object? sender, VideoFileHashedEventArgs args)
    {
        if (_pendingHashOperations.TryRemove(args.File.ID, out var pending))
            pending.Completion.TrySetResult(args);
    }

    /// <summary>
    /// Returns the managed folders that are eligible for integrity checking —
    /// same filter used by <see cref="GetManagedFolders"/> and the scan loop.
    /// </summary>
    private IEnumerable<IManagedFolder> GetEligibleManagedFolders()
        => _videoService.GetAllManagedFolders()
            // Pure drop-source ("import") folders are a transient staging area —
            // files land there only briefly before being relocated into their
            // real home, so re-hashing them is pointless and could race with an
            // in-progress import. Folders that are *also* a destination (i.e.
            // `Both`) are excluded from this filter since they double as a
            // permanent home for files.
            .Where(folder => folder.DropFolderType != DropFolderType.Source);

    /// <inheritdoc />
    public IReadOnlyList<ManagedFolderInfo> GetManagedFolders()
        => [.. GetEligibleManagedFolders()
            .Select(folder => new ManagedFolderInfo { ManagedFolderID = folder.ID, Name = folder.Name, Path = folder.Path })
            .OrderBy(folder => folder.Name, StringComparer.OrdinalIgnoreCase)];

    /// <inheritdoc />
    public bool StartCheck(IReadOnlyList<int>? managedFolderIDs = null)
    {
        // Resolve the requested IDs against the real managed folders up front
        // so an unknown/stale ID can't silently shrink the scope, and so the
        // dashboard has friendly names to display for the run it kicked off.
        var requestedIDs = managedFolderIDs is { Count: > 0 } ? new HashSet<int>(managedFolderIDs) : null;
        IReadOnlyList<int>? scopedIDs = null;
        IReadOnlyList<string> scopedNames = [];
        if (requestedIDs is not null)
        {
            var matched = _videoService.GetAllManagedFolders()
                .Where(folder => requestedIDs.Contains(folder.ID))
                .ToList();

            scopedIDs = [.. matched.Select(folder => folder.ID)];
            scopedNames = [.. matched.Select(folder => folder.Name).OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];
        }

        lock (_stateLock)
        {
            if (_isRunning)
                return false;

            _isRunning = true;
            _wasCompleted = false;
            _startedAt = DateTime.Now;
            _completedAt = null;
            _scopedFolderIDs = scopedIDs;
            _scopedFolderNames = scopedNames;
            _totalFiles = 0;
            _processedFiles = 0;
            _skippedFiles = 0;
            _currentFile = null;
            _lastError = null;
            _mismatches.Clear();
            _cancellationTokenSource = new();
        }

        var cancellationToken = _cancellationTokenSource!.Token;
        _runningTask = Task.Run(() => RunAsync(cancellationToken), CancellationToken.None);
        return true;
    }

    /// <inheritdoc />
    public bool RequestCancellation()
    {
        lock (_stateLock)
        {
            if (!_isRunning || _cancellationTokenSource is null)
                return false;

            _cancellationTokenSource.Cancel();
            return true;
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> if any registered
    /// <see cref="IManagedFolderIgnoreRule"/> says this file should be skipped.
    /// Mirrors the filter Shoko applies during import scanning so the integrity
    /// check doesn't rehash files that Shoko itself treats as invisible.
    /// </summary>
    private static bool IsIgnoredByRules(IVideoFile file, IReadOnlyList<IManagedFolderIgnoreRule> rules)
    {
        if (rules.Count == 0)
            return false;

        var fileInfo = new System.IO.FileInfo(file.Path);
        foreach (var rule in rules)
        {
            try
            {
                if (rule.ShouldIgnore(file.ManagedFolder, fileInfo))
                    return true;
            }
            catch
            {
                // A misbehaving rule shouldn't abort the whole scan — skip it.
            }
        }

        return false;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var completedNormally = false;
        try
        {
            if (!_systemService.IsStarted)
            {
                _logger.LogWarning("Integrity check requested before the server finished starting up; aborting");
                lock (_stateLock)
                    _lastError = "The server has not finished starting up yet. Try again once it's running.";
                return;
            }

            IReadOnlyList<int>? scopedFolderIDs;
            lock (_stateLock)
                scopedFolderIDs = _scopedFolderIDs;

            // Use the same folder filter as the picker — drop-source folders are
            // excluded both from the UI and from the actual scan, so a "scan
            // everything" run (null scope) and a run with all folders explicitly
            // selected produce identical results.
            var eligibleFolders = GetEligibleManagedFolders().ToList();
            if (scopedFolderIDs is { Count: > 0 })
            {
                var scopedSet = new HashSet<int>(scopedFolderIDs);
                eligibleFolders = [.. eligibleFolders.Where(folder => scopedSet.Contains(folder.ID))];
            }

            // Snapshot the ignore rules once so we don't re-query them per file.
            var ignoreRules = _videoService.IgnoreRules;

            // Snapshot the file list up-front so the progress total is stable
            // for the duration of the run.
            var files = eligibleFolders
                .SelectMany(folder => _videoService.GetVideoFilesInManagedFolder(folder)
                    .Where(file => file.IsAvailable && !IsIgnoredByRules(file, ignoreRules)))
                .ToList();

            lock (_stateLock)
                _totalFiles = files.Count;

            PersistResult(force: true);

            if (scopedFolderIDs is { Count: > 0 })
            {
                _logger.LogInformation(
                    "Starting integrity check across {FileCount} file(s) in {FolderCount} selected managed folder(s)",
                    files.Count,
                    eligibleFolders.Count);
            }
            else
            {
                _logger.LogInformation("Starting integrity check across {FileCount} file(s)", files.Count);
            }

            var pendingByFileID = new Dictionary<int, PendingHashWork>();
            var remainingFiles = new Queue<IVideoFile>(files);

            while (remainingFiles.Count > 0 || pendingByFileID.Count > 0)
            {
                while (!cancellationToken.IsCancellationRequested
                    && remainingFiles.Count > 0
                    && pendingByFileID.Count < QueueSubmissionWindow)
                {
                    var file = remainingFiles.Dequeue();

                    lock (_stateLock)
                        _currentFile = file.FileName;

                    if (!file.IsAvailable)
                    {
                        lock (_stateLock)
                            _skippedFiles++;
                        continue;
                    }

                    var previousVideo = file.Video;
                    var pending = new PendingHashWork(file, previousVideo.ID, previousVideo.ED2K);
                    _pendingHashOperations[file.ID] = pending;

                    try
                    {
                        // Use Shoko's queue instead of hashing inline so the server can
                        // schedule the actual hashing work according to its own policy.
                        // The bounded submission window keeps that queue fed without
                        // making cancellation meaningless by enqueueing the whole library
                        // up front.
                        await _hashingService.ScheduleGetHashesForFile(
                            file,
                            useExistingHashes: false,
                            skipFindRelease: false,
                            skipMylist: false,
                            prioritize: false);
                        pendingByFileID[file.ID] = pending;
                    }
                    catch (Exception ex)
                    {
                        _pendingHashOperations.TryRemove(file.ID, out _);
                        _logger.LogError(ex, "Integrity check: failed to queue re-hash for \"{FileName}\"", file.FileName);
                        lock (_stateLock)
                            _processedFiles++;
                        PersistResult(force: false);
                    }
                }

                if (pendingByFileID.Count == 0)
                {
                    if (cancellationToken.IsCancellationRequested)
                        break;

                    continue;
                }

                Task<VideoFileHashedEventArgs> completedTask;
                try
                {
                    completedTask = await Task.WhenAny(pendingByFileID.Values.Select(pending => pending.Completion.Task));
                    var hashed = await completedTask;
                    var pending = pendingByFileID.Values.First(candidate => candidate.Completion.Task == completedTask);
                    pendingByFileID.Remove(pending.File.ID);

                    var newHash = hashed.Video.ED2K;
                    if (hashed.IsNewVideo || !string.Equals(pending.PreviousHash, newHash, StringComparison.OrdinalIgnoreCase))
                    {
                        // A video with release info has already been picked back up by
                        // Shoko's release search under its corrected hash — recognized,
                        // no action needed. One without is genuinely unrecognized and
                        // needs to be re-matched manually, same as after a single-file
                        // rescan. We still record both so the hash change itself stays
                        // visible (it's useful to know a stored hash was wrong even when
                        // Shoko fixed the match on its own), but the dashboard surfaces
                        // them differently so "updated match" files don't read as if
                        // they need attention.
                        var isRecognized = hashed.Video.ReleaseInfo is not null;
                        var issue = new IntegrityCheckIssue
                        {
                            PreviousVideoID = pending.PreviousVideoID,
                            NewVideoID = hashed.Video.ID,
                            FileID = hashed.File.ID,
                            FileName = hashed.File.FileName,
                            RelativePath = hashed.RelativePath,
                            ManagedFolderName = hashed.ManagedFolder.Name,
                            PreviousHash = pending.PreviousHash,
                            NewHash = newHash,
                            IsRecognized = isRecognized,
                            DetectedAt = DateTime.Now,
                        };
                        _mismatches.Add(issue);

                        if (isRecognized)
                        {
                            _logger.LogInformation(
                                "Integrity check: hash changed for \"{FileName}\" in \"{ManagedFolder}\" (was {OldHash}, now {NewHash}) "
                                + "but it was automatically re-matched to a release — no action needed",
                                hashed.File.FileName,
                                hashed.ManagedFolder.Name,
                                pending.PreviousHash,
                                newHash);
                        }
                        else
                        {
                            _logger.LogWarning(
                                "Integrity check: hash mismatch for \"{FileName}\" in \"{ManagedFolder}\" — was {OldHash}, now {NewHash}, "
                                + "and the file is now unrecognized",
                                hashed.File.FileName,
                                hashed.ManagedFolder.Name,
                                pending.PreviousHash,
                                newHash);
                        }
                    }

                    lock (_stateLock)
                        _processedFiles++;
                }
                catch (OperationCanceledException)
                {
                    break;
                }

                PersistResult(force: false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                _logger.LogInformation("Integrity check cancelled after {Processed}/{Total} file(s)", _processedFiles, _totalFiles);
            }

            completedNormally = !cancellationToken.IsCancellationRequested;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integrity check run failed unexpectedly");
            lock (_stateLock)
                _lastError = ex.Message;
        }
        finally
        {
            foreach (var pending in _pendingHashOperations.ToArray())
            {
                if (_pendingHashOperations.TryRemove(pending.Key, out var removed))
                    removed.Completion.TrySetCanceled();
            }

            lock (_stateLock)
            {
                _isRunning = false;
                _wasCompleted = completedNormally;
                _completedAt = DateTime.Now;
                _currentFile = null;
            }

            _logger.LogInformation(
                "Integrity check finished: {Processed}/{Total} file(s) checked, {Mismatches} mismatch(es) found, {Skipped} skipped",
                _processedFiles,
                _totalFiles,
                _mismatches.Count,
                _skippedFiles);

            // Persist so the dashboard still has these results after a
            // restart or plugin reload, instead of resetting to "no run yet".
            PersistResult(force: true);
        }
    }

    private sealed class PendingHashWork
    {
        public PendingHashWork(IVideoFile file, int previousVideoID, string previousHash)
        {
            File = file;
            PreviousVideoID = previousVideoID;
            PreviousHash = previousHash;
            Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public IVideoFile File { get; }

        public int PreviousVideoID { get; }

        public string PreviousHash { get; }

        public TaskCompletionSource<VideoFileHashedEventArgs> Completion { get; }
    }
}
