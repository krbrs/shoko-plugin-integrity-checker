namespace ShokoIntegrityChecker.Models;

/// <summary>
/// A managed folder available to be scoped into (or out of) an integrity
/// check run, returned by the <c>/folders</c> endpoint so the dashboard can
/// offer a picker.
/// </summary>
public sealed class ManagedFolderInfo
{
    /// <summary>
    /// The managed folder's ID, as passed back in <see cref="RunRequest.ManagedFolderIDs"/>.
    /// </summary>
    /// <remarks>
    /// Named <c>ManagedFolderID</c> rather than a bare <c>ID</c> — Newtonsoft serializes the
    /// property name as-is, and the dashboard's naive camelCase normalizer (which only
    /// lowercases the first character) would otherwise turn <c>"ID"</c> into <c>"iD"</c>
    /// instead of the expected <c>"id"</c>, leaving <c>folder.id</c> undefined client-side.
    /// </remarks>
    public required int ManagedFolderID { get; init; }

    /// <summary>
    /// The managed folder's friendly name.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// The managed folder's absolute path on disk.
    /// </summary>
    public required string Path { get; init; }
}

/// <summary>
/// Request body for <c>POST /run</c>, letting the caller scope the check to a
/// subset of managed folders instead of the whole library.
/// </summary>
public sealed class RunRequest
{
    /// <summary>
    /// IDs of the managed folders to check. <see langword="null"/> or empty
    /// means "check every managed folder" (the previous, unscoped behaviour).
    /// </summary>
    public IReadOnlyList<int>? ManagedFolderIDs { get; init; }
}

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
    /// Names of the managed folders the current/most recent run was scoped to.
    /// Empty means it covered every managed folder.
    /// </summary>
    public required IReadOnlyList<string> ScopedFolderNames { get; init; }

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
    /// Names of the managed folders the persisted run was scoped to. Empty
    /// means it covered every managed folder.
    /// </summary>
    public IReadOnlyList<string> ScopedFolderNames { get; init; } = [];

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
    /// Whether the file ended up matched to a release after the rehash.
    /// <see langword="true"/> means Shoko's release search immediately picked
    /// the file back up under its new hash (same or different release) — no
    /// action needed. <see langword="false"/> means the file is now
    /// unrecognized and needs to be re-matched manually, the same as after a
    /// single-file rescan.
    /// </summary>
    public required bool IsRecognized { get; init; }

    /// <summary>
    /// When the mismatch was detected.
    /// </summary>
    public required DateTime DetectedAt { get; init; }
}
