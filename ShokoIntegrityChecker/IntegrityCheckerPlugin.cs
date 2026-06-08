using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Plugin;
using Shoko.Abstractions.Plugin.Models;
using ShokoIntegrityChecker.Services;

namespace ShokoIntegrityChecker;

/// <summary>
/// Entry point for the Integrity Checker plugin. Re-hashes files in managed
/// folders and flags any whose contents no longer match the hash Shoko has on
/// record — the same outcome as manually rescanning a single file (a hash
/// mismatch detaches the file from its release info and leaves it
/// unrecognized for re-matching), just applied in bulk across the collection.
/// </summary>
public sealed class IntegrityCheckerPlugin : IPlugin, IPluginServiceRegistration
{
    /// <summary>
    /// Stable plugin identifier. Generated once and kept constant so Shoko
    /// continues to recognize this plugin across updates.
    /// </summary>
    public static readonly Guid StaticID = Guid.Parse("299604c0-f883-577b-a874-52e87f876fc8");

    /// <inheritdoc />
    public Guid ID
        => StaticID;

    /// <inheritdoc />
    public string Name
        => "Integrity Checker";

    /// <inheritdoc />
    public string? Description
        => "Re-hashes the files in your managed folders and flags any whose contents no longer match what Shoko has on "
         + "record, surfacing them as unrecognized files for re-matching — the same behavior as rescanning a single "
         + "file, applied in bulk.";

    /// <inheritdoc />
    public IReadOnlyList<PluginPage> GetPages()
        =>
        [
            new()
            {
                Name = "Integrity Checker",
                Url = $"/api/plugin/{PluginConstants.RoutePrefix}/dashboard",
                CanEmbed = true,
            },
        ];

    /// <summary>
    /// Registers the plugin's services with the host's dependency injection
    /// container. Invoked by <c>PluginManager</c> during start-up.
    /// </summary>
    /// <param name="serviceCollection">The host's service collection.</param>
    /// <param name="applicationPaths">Paths exposed to plugins by the host.</param>
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton<IIntegrityCheckService, IntegrityCheckService>();
    }
}
