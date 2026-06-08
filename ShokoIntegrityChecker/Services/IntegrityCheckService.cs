using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Shoko.Abstractions.Core.Services;
using Shoko.Abstractions.Video.Services;
using ShokoIntegrityChecker.Models;

namespace ShokoIntegrityChecker.Services;

/// <inheritdoc cref="IIntegrityCheckService" />
public sealed class IntegrityCheckService : IIntegrityCheckService
{
    private readonly IVideoService _videoService;

    private readonly IVideoHashingService _hashingService;

    private readonly ISystemService _systemService;

    private readonly ILogger<IntegrityCheckService> _logger;

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
    /// <param name="logger">Logger for this service.</param>
    public IntegrityCheckService(
        IVideoService videoService,
        IVideoHashingService hashingService,
        ISystemService systemService,
        ILogger<IntegrityCheckService> logger)
    {
        _videoService = videoService;
        _hashingService = hashingService;
        _systemService = systemService;
        _logger = logger;
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
        }
    }
}
