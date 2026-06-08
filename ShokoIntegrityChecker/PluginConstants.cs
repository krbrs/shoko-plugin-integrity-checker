namespace ShokoIntegrityChecker;

/// <summary>
/// Shared constants for the plugin — kept in one place so the plugin
/// metadata, controller routes, and dashboard all agree on the same values.
/// </summary>
public static class PluginConstants
{
    /// <summary>
    /// Route segment under <c>/api/plugin/</c> that this plugin's controller
    /// and dashboard are served from, e.g. <c>/api/plugin/integritychecker/dashboard</c>.
    /// </summary>
    public const string RoutePrefix = "integritychecker";
}
