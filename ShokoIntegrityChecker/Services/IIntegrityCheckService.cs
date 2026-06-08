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
    /// Starts a new integrity check run in the background.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if a new run was started; <see langword="false"/>
    /// if a run was already in progress.
    /// </returns>
    bool StartCheck();

    /// <summary>
    /// Requests cancellation of the currently running check, if any.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if a running check was signalled to cancel;
    /// <see langword="false"/> if no check is currently running.
    /// </returns>
    bool RequestCancellation();
}
