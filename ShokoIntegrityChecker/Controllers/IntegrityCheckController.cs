using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using ShokoIntegrityChecker;
using ShokoIntegrityChecker.Models;
using ShokoIntegrityChecker.Services;

namespace ShokoIntegrityChecker.Controllers;

/// <summary>
/// Backend for the Integrity Checker dashboard — exposes endpoints to kick
/// off a bulk re-hash, poll its progress, and review any mismatches it finds,
/// plus serves the dashboard's static front-end.
/// </summary>
[ApiController]
[Route($"api/plugin/{PluginConstants.RoutePrefix}")]
public sealed class IntegrityCheckController : ControllerBase
{
    private static readonly Lazy<IFileProvider?> DashboardFiles = new(() =>
    {
        // AppContext.BaseDirectory resolves to the host's directory inside a
        // plugin AssemblyLoadContext, not this assembly's — use its location instead.
        var assemblyLocation = typeof(IntegrityCheckController).Assembly.Location;
        if (string.IsNullOrEmpty(assemblyLocation))
            return null;

        var pluginDirectory = Path.GetDirectoryName(assemblyLocation);
        if (string.IsNullOrEmpty(pluginDirectory))
            return null;

        var dashboardRoot = Path.Combine(pluginDirectory, "dashboard");
        return Directory.Exists(dashboardRoot) ? new PhysicalFileProvider(dashboardRoot) : null;
    });

    private static readonly IContentTypeProvider ContentTypeProvider = new FileExtensionContentTypeProvider();

    private readonly IIntegrityCheckService _service;

    /// <summary>
    /// Initializes a new instance of <see cref="IntegrityCheckController"/>.
    /// </summary>
    /// <param name="service">The injected integrity-check service.</param>
    public IntegrityCheckController(IIntegrityCheckService service)
    {
        _service = service;
    }

    /// <summary>
    /// Gets the current status of the integrity check — whether it's running,
    /// how far it's gotten, and any mismatches found so far (or from the most
    /// recent completed run).
    /// </summary>
    [HttpGet("status")]
    [ProducesResponseType<IntegrityCheckStatus>(StatusCodes.Status200OK)]
    public ActionResult<IntegrityCheckStatus> GetStatus()
        => Ok(_service.GetStatus());

    /// <summary>
    /// Lists every managed folder the check could be scoped to, so the
    /// dashboard can offer a picker.
    /// </summary>
    [HttpGet("folders")]
    [ProducesResponseType<IReadOnlyList<ManagedFolderInfo>>(StatusCodes.Status200OK)]
    public ActionResult<IReadOnlyList<ManagedFolderInfo>> GetFolders()
        => Ok(_service.GetManagedFolders());

    /// <summary>
    /// Starts a new integrity check run, optionally scoped to a subset of
    /// managed folders (every folder is checked if none are specified). The
    /// run continues in the background; poll <c>/status</c> for progress.
    /// </summary>
    [HttpPost("run")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Run([FromBody] RunRequest? request = null)
    {
        if (!_service.StartCheck(request?.ManagedFolderIDs))
            return Conflict("An integrity check is already running.");

        return Ok();
    }

    /// <summary>
    /// Requests cancellation of the currently running integrity check. Files
    /// already re-hashed before cancellation keep whatever result they got.
    /// </summary>
    [HttpPost("cancel")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public ActionResult Cancel()
    {
        if (!_service.RequestCancellation())
            return Conflict("No integrity check is currently running.");

        return Ok();
    }

    /// <summary>
    /// Serves the dashboard's front-end (a small static HTML/JS page). This is
    /// the URL advertised via <see cref="Shoko.Abstractions.Plugin.Models.PluginPage"/>
    /// and is what the WebUI embeds in an iframe under Settings → Plugins.
    /// </summary>
    [HttpGet("dashboard")]
    [HttpGet("dashboard/{**path}")]
    public IActionResult Dashboard(string? path = null)
    {
        if (DashboardFiles.Value is not { } provider)
            return NotFound("Dashboard assets are missing from the plugin's output directory.");

        var relativePath = string.IsNullOrWhiteSpace(path) ? "index.html" : path;
        var fileInfo = provider.GetFileInfo(relativePath);
        if (!fileInfo.Exists || fileInfo.IsDirectory)
            return NotFound();

        if (!ContentTypeProvider.TryGetContentType(fileInfo.Name, out var contentType))
            contentType = "application/octet-stream";

        // Inject an absolute <base href> computed from the request path so relative
        // asset URLs (css/…, js/…) resolve correctly regardless of trailing slash or
        // sub-path — mirrors ShokoRelay's DashboardController.GetControllerPage.
        if (string.Equals(fileInfo.Name, "index.html", StringComparison.OrdinalIgnoreCase))
        {
            var html = System.IO.File.ReadAllText(fileInfo.PhysicalPath!);
            if (html.IndexOf("<base", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var requestPath = Request.Path.Value ?? string.Empty;
                var dashboardIndex = requestPath.IndexOf("/dashboard", StringComparison.OrdinalIgnoreCase);
                var baseHref = dashboardIndex >= 0
                    ? requestPath[..(dashboardIndex + "/dashboard".Length)].TrimEnd('/') + "/"
                    : "/";
                var baseTag = $"\n  <base href=\"{WebUtility.HtmlEncode(baseHref)}\">";
                html = html.Replace("<head>", "<head>" + baseTag, StringComparison.OrdinalIgnoreCase);
            }

            return Content(html, "text/html");
        }

        return PhysicalFile(fileInfo.PhysicalPath!, contentType);
    }
}
