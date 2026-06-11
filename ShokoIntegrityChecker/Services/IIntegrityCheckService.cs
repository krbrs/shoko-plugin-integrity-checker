using ShokoIntegrityChecker.Models;

namespace ShokoIntegrityChecker.Services;

/// <summary>
/// Drives the bulk integrity check: walks every file in every managed folder,
/// forces a re-hash, and records any file whose new hash no longer matches
/// what Shoko had stored for it.
/// </summary>
public interface IIntegrityCheckService
{
    /// <summary>
    /// Gets a snapshot of the current/most recent run's progress and results.
    /// </summary>
    IntegrityCheckStatus GetStatus();

    /// <summary>
    /// Gets a serializable snapshot of the current/most recent run for JSON export.
    /// </summary>
    PersistedIntegrityCheckResult GetExport();

    /// <summary>
    /// Lists every managed folder the check could be scoped to, for the
    /// dashboard's folder picker.
    /// </summary>
    IReadOnlyList<ManagedFolderInfo> GetManagedFolders();

    /// <summary>
    /// Starts a new integrity check run in the background.
    /// </summary>
    /// <param name="managedFolderIDs">
    /// IDs of the managed folders to check, or <see langword="null"/>/empty to
    /// check every managed folder. Unknown IDs are ignored.
    /// </param>
    /// <returns>
    /// <see langword="true"/> if a new run was started; <see langword="false"/>
    /// if a run was already in progress.
    /// </returns>
    bool StartCheck(IReadOnlyList<int>? managedFolderIDs = null);

    /// <summary>
    /// Requests cancellation of the currently running check, if any.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if a running check was signalled to cancel;
    /// <see langword="false"/> if no check is currently running.
    /// </returns>
    bool RequestCancellation();
}
