namespace ShokoIntegrityChecker.Models;

/// <summary>
/// The current state of the integrity check run, returned by the
/// <c>/status</c> endpoint and polled by the dashboard.
/// </summary>
public sealed class IntegrityCheckStatus
{
    /// <summary>
    /// Whether a check is currently in progress.
    /// </summary>
    public required bool IsRunning { get; init; }

    /// <summary>
    /// Whether a cancellation has been requested for the running check.
    /// </summary>
    public required bool IsCancellationRequested { get; init; }

    /// <summary>
    /// When the current (or most recent) run started, if any.
    /// </summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>
    /// When the most recent run finished, if it has finished.
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Total number of files queued for the current/most recent run.
    /// </summary>
    public required int TotalFiles { get; init; }

    /// <summary>
    /// Number of files that have been re-hashed so far in the current/most
    /// recent run.
    /// </summary>
    public required int ProcessedFiles { get; init; }

    /// <summary>
    /// Number of files skipped because they were unavailable on disk at the
    /// time they were checked.
    /// </summary>
    public required int SkippedFiles { get; init; }

    /// <summary>
    /// The name of the file currently being processed, if a check is running.
    /// </summary>
    public string? CurrentFile { get; init; }

    /// <summary>
    /// Files whose freshly-computed hash no longer matches the hash Shoko had
    /// on record — these have been detached from their release info and now
    /// show up as unrecognized, the same as a manual single-file rescan would
    /// produce.
    /// </summary>
    public required IReadOnlyList<IntegrityCheckIssue> Mismatches { get; init; }

    /// <summary>
    /// A short message describing the last error encountered, if the run
    /// failed outright (as opposed to individual file failures, which are
    /// logged but do not stop the run).
    /// </summary>
    public string? LastError { get; init; }
}

/// <summary>
/// A serializable snapshot of the most recent completed run, written to disk
/// so results survive a server restart or plugin reload. Deliberately omits
/// the runtime-only fields of <see cref="IntegrityCheckStatus"/> (<c>IsRunning</c>,
/// <c>IsCancellationRequested</c>, <c>CurrentFile</c>) since those never make
/// sense to restore from a previous process.
/// </summary>
public sealed class PersistedIntegrityCheckResult
{
    /// <summary>
    /// When the persisted run started.
    /// </summary>
    public DateTime? StartedAt { get; init; }

    /// <summary>
    /// When the persisted run finished.
    /// </summary>
    public DateTime? CompletedAt { get; init; }

    /// <summary>
    /// Total number of files that were queued for the persisted run.
    /// </summary>
    public int TotalFiles { get; init; }

    /// <summary>
    /// Number of files that had been re-hashed by the time the persisted run ended.
    /// </summary>
    public int ProcessedFiles { get; init; }

    /// <summary>
    /// Number of files skipped because they were unavailable on disk during the persisted run.
    /// </summary>
    public int SkippedFiles { get; init; }

    /// <summary>
    /// Mismatches found during the persisted run.
    /// </summary>
    public IReadOnlyList<IntegrityCheckIssue> Mismatches { get; init; } = [];

    /// <summary>
    /// The last error message recorded for the persisted run, if any.
    /// </summary>
    public string? LastError { get; init; }
}

/// <summary>
/// A single file that failed its integrity check — its on-disk contents no
/// longer hash to the value Shoko has stored for it.
/// </summary>
public sealed class IntegrityCheckIssue
{
    /// <summary>
    /// The ID of the (now detached) video record that previously matched this file.
    /// </summary>
    public required int PreviousVideoID { get; init; }

    /// <summary>
    /// The ID of the new video record created for the file's new contents.
    /// </summary>
    public required int NewVideoID { get; init; }

    /// <summary>
    /// The ID of the video file location that was checked.
    /// </summary>
    public required int FileID { get; init; }

    /// <summary>
    /// The file's name, e.g. <c>"[Group] Show - 01.mkv"</c>.
    /// </summary>
    public required string FileName { get; init; }

    /// <summary>
    /// The file's path relative to its managed folder.
    /// </summary>
    public required string RelativePath { get; init; }

    /// <summary>
    /// The friendly name of the managed folder the file lives in.
    /// </summary>
    public required string ManagedFolderName { get; init; }

    /// <summary>
    /// The ED2K hash Shoko had stored for this file before the recheck.
    /// </summary>
    public required string PreviousHash { get; init; }

    /// <summary>
    /// The ED2K hash computed from the file's current on-disk contents.
    /// </summary>
    public required string NewHash { get; init; }

    /// <summary>
    /// When the mismatch was detected.
    /// </summary>
    public required DateTime DetectedAt { get; init; }
}
