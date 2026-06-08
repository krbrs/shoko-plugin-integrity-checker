using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Video.Services;
using ShokoIntegrityChecker.Models;

namespace ShokoIntegrityChecker.Services;

/// <inheritdoc cref="IIntegrityCheckService" />
public sealed class IntegrityCheckService : IIntegrityCheckService
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    private readonly IVideoService _videoService;

    private readonly IVideoHashingService _hashingService;

    private readonly ISystemService _systemService;

    private readonly ILogger<IntegrityCheckService> _logger;

    /// <summary>
    /// Where the most recent run's results are persisted as JSON, so they
    /// survive a server restart or plugin reload. Lives at
    /// <c>{DataPath}/configuration/{PluginID}/results.json</c> — the same
    /// per-plugin directory convention the host uses (and purges on uninstall).
    /// </summary>
    private readonly string _resultsFilePath;

    private readonly Lock _stateLock = new();

    private readonly ConcurrentBag<IntegrityCheckIssue> _mismatches = [];

    private CancellationTokenSource? _cancellationTokenSource;

    private Task? _runningTask;

    private bool _isRunning;

    private DateTime? _startedAt;

    private DateTime? _completedAt;

    private int _totalFiles;

    private int _processedFiles;

    private int _skippedFiles;

    private string? _currentFile;

    private string? _lastError;

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
                _startedAt = persisted.StartedAt;
                _completedAt = persisted.CompletedAt;
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
    /// Writes the current run's results to disk so they survive a restart or
    /// plugin reload. Writes to a temporary file first and then replaces the
    /// real one, so a crash mid-write can't corrupt the persisted results.
    /// </summary>
    private void PersistResult()
    {
        try
        {
            PersistedIntegrityCheckResult snapshot;
            lock (_stateLock)
            {
                snapshot = new()
                {
                    StartedAt = _startedAt,
                    CompletedAt = _completedAt,
                    TotalFiles = _totalFiles,
                    ProcessedFiles = _processedFiles,
                    SkippedFiles = _skippedFiles,
                    Mismatches = [.. _mismatches.OrderByDescending(issue => issue.DetectedAt)],
                    LastError = _lastError,
                };
            }

            var json = JsonSerializer.Serialize(snapshot, SerializerOptions);
            var tempPath = _resultsFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _resultsFilePath, overwrite: true);
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
                StartedAt = _startedAt,
                CompletedAt = _completedAt,
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
    public bool StartCheck()
    {
        lock (_stateLock)
        {
            if (_isRunning)
                return false;

            _isRunning = true;
            _startedAt = DateTime.Now;
            _completedAt = null;
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

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!_systemService.IsStarted)
            {
                _logger.LogWarning("Integrity check requested before the server finished starting up; aborting");
                lock (_stateLock)
                    _lastError = "The server has not finished starting up yet. Try again once it's running.";
                return;
            }

            // Snapshot the file list up-front so the progress total is stable
            // for the duration of the run.
            var files = _videoService.GetAllManagedFolders()
                .SelectMany(folder => _videoService.GetVideoFilesInManagedFolder(folder))
                .Where(file => file.IsAvailable)
                .ToList();

            lock (_stateLock)
                _totalFiles = files.Count;

            _logger.LogInformation("Starting integrity check across {FileCount} file(s)", files.Count);

            foreach (var file in files)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation("Integrity check cancelled after {Processed}/{Total} file(s)", _processedFiles, _totalFiles);
                    break;
                }

                lock (_stateLock)
                    _currentFile = file.FileName;

                if (!file.IsAvailable)
                {
                    lock (_stateLock)
                        _skippedFiles++;
                    continue;
                }

                var previousVideo = file.Video;
                var previousVideoId = previousVideo.ID;
                var previousHash = previousVideo.ED2K;

                try
                {
                    // Mirrors the manual "Rehash" action (useExistingHashes: false
                    // forces recomputation), just run in bulk. If the contents
                    // changed, the hashing service itself detaches the file from
                    // its old release info and spins up a new video record,
                    // leaving the file unrecognized for re-matching — exactly
                    // what a single-file rescan does.
                    var result = await _hashingService.GetHashesForFile(
                        file,
                        useExistingHashes: false,
                        skipFindRelease: false,
                        skipMylist: false,
                        cancellationToken: cancellationToken);

                    var newHash = result.Video.ED2K;
                    if (result.IsNewVideo || !string.Equals(previousHash, newHash, StringComparison.OrdinalIgnoreCase))
                    {
                        var issue = new IntegrityCheckIssue
                        {
                            PreviousVideoID = previousVideoId,
                            NewVideoID = result.Video.ID,
                            FileID = file.ID,
                            FileName = file.FileName,
                            RelativePath = file.RelativePath,
                            ManagedFolderName = file.ManagedFolder.Name,
                            PreviousHash = previousHash,
                            NewHash = newHash,
                            DetectedAt = DateTime.Now,
                        };
                        _mismatches.Add(issue);

                        _logger.LogWarning(
                            "Integrity check: hash mismatch for \"{FileName}\" in \"{ManagedFolder}\" — was {OldHash}, now {NewHash}",
                            file.FileName,
                            file.ManagedFolder.Name,
                            previousHash,
                            newHash);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Integrity check: failed to re-hash \"{FileName}\"", file.FileName);
                }

                lock (_stateLock)
                    _processedFiles++;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Integrity check run failed unexpectedly");
            lock (_stateLock)
                _lastError = ex.Message;
        }
        finally
        {
            lock (_stateLock)
            {
                _isRunning = false;
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
            PersistResult();
        }
    }
}
