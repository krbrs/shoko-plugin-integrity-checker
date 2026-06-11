# Shoko Integrity Checker

A Shoko Server plugin that walks every file in your managed folders, force-recomputes its hash, and flags any file
whose on-disk contents no longer match what Shoko has stored. Mismatched files get detached from their release info
and left as unrecognized — exactly what happens today when you manually "Rehash" a single file — just run in bulk
across your whole collection, with a small dashboard to kick it off and review results.

## Project layout

```
ShokoIntegrityChecker/
├── ShokoIntegrityChecker.csproj   Plugin project (net10.0, references Shoko.Abstractions)
├── IntegrityCheckerPlugin.cs      IPlugin entry point + DI registration
├── PluginConstants.cs             Shared route-prefix constant
├── Models/
│   └── IntegrityCheckModels.cs    Status / mismatch DTOs returned by the API
├── Services/
│   ├── IIntegrityCheckService.cs  Service contract
│   └── IntegrityCheckService.cs   Drives the bulk re-hash, tracks progress & results
├── Controllers/
│   └── IntegrityCheckController.cs   /run, /cancel, /status endpoints + serves the dashboard
└── Dashboard/                     Static front-end (vanilla HTML/CSS/JS)
    ├── index.html
    ├── css/style.css
    └── js/app.js
```

## How it works

1. `IntegrityCheckerPlugin` registers `IIntegrityCheckService` with the host's DI container and advertises a
   dashboard page via `IPlugin.GetPages()` (shown up under Settings → Plugins in the WebUI, embedded in an iframe).
2. `IntegrityCheckController` exposes `POST /api/plugin/integritychecker/run`, `POST .../cancel`,
   `GET .../status`, and `GET .../folders`, plus serves the static dashboard at `GET .../dashboard`.
3. The dashboard's **Scope** picker (backed by `GET .../folders`) lets you check every managed folder — the
   default — or narrow a run down to just the ones you select; your choice is sent as `managedFolderIDs` in the
   `POST .../run` body and only files under those folders are walked. Pure drop-source ("import") folders are
   left out of the picker entirely — they're a transient staging area files pass through on their way to a real
   home, so re-hashing them is pointless and could race with an in-progress import.
4. `IntegrityCheckService` snapshots every available file across the selected managed folders (`IVideoService`),
   then enqueues each file through `IVideoHashingService.ScheduleGetHashesForFile(..., useExistingHashes: false, ...)`
   so Shoko's own queue processor performs the re-hash work using the same force-rehash path as the built-in
   "Rehash" action. The plugin listens for `VideoFileHashed` completions, compares the freshly computed hash to the
   previous one, and records a hash change if they differ (or if a new video record was spun up); by then Shoko's
   hashing service has already detached the file from its old release info. Whether the new video record ends up
   matched to a release (`IVideo.ReleaseInfo is not null`) determines how the dashboard labels it: **Updated match**
   (Shoko's release search picked it back up under the corrected hash, no action needed) or **Needs verification**
   (genuinely unrecognized now, same as after a single-file rescan, so compare it to the source before re-matching).
5. The dashboard polls `/status` every 1.5s while a run is active and renders progress plus a results table. Users can
   also download the current/most recent snapshot as JSON straight from the WebUI.
6. `IntegrityCheckService` checkpoints its current state to `results.json` under the plugin's per-plugin configuration
   directory (`{Shoko data dir}/configuration/{plugin GUID}/`, the same location the host purges on uninstall) while a
   run is in progress, then writes a final completed snapshot when it ends. That way long runs survive a server restart
   or plugin reload instead of resetting to "no run yet".

> [!NOTE]
> A changed hash usually means the file got corrupted. Less often, Shoko computed a wrong hash. Either way, compare
> it to the original source — if it checks out, re-match it; if not, you'd just be re-matching a corrupted file.

## Building

Build it locally:

```bash
cd "ShokoIntegrityChecker"
dotnet build
```

It targets `net10.0` and references the `Shoko.Abstractions` NuGet package (pinned to `6.0.0-alpha.46` to match the
version in your `ShokoServer_fork` checkout — bump it if your server is on a different alpha). `ExcludeAssets="runtime"`
keeps the package compile-only, since the host process supplies the actual assembly at runtime.

If that prerelease version isn't resolvable from your NuGet feeds, either:
- point `nuget.config` at the feed that publishes Shoko's prereleases, or
- temporarily swap the `PackageReference` for a `ProjectReference` to your local
  `ShokoServer_fork/Shoko.Abstractions/Shoko.Abstractions.csproj` while developing, then switch back to the
  `PackageReference` (with `ExcludeAssets="runtime"`) before distributing — a `ProjectReference` would otherwise embed
  your own copy of the abstractions assembly alongside the host's.

## Installing

Publish and drop the output into Shoko's `plugins/` directory, in its own subfolder:

```bash
dotnet publish ShokoIntegrityChecker/ShokoIntegrityChecker.csproj -c Release -o publish
```

then copy `publish/` to `<Shoko data dir>/plugins/ShokoIntegrityChecker/` and restart the server. The dashboard then
appears under Settings → Plugins in the WebUI (or directly at `/api/plugin/integritychecker/dashboard`).

## Adding the plugin repository to Shoko

This repo is set up to be installed straight from Shoko's plugin browser, the same way
[`dotnet-shoko-plugin-offline-importer`](https://github.com/revam/dotnet-shoko-plugin-offline-importer) and friends are:

- `manifest.json` at the repo root describes the plugin (id, name, overview, tags, releases) following the schema from
  [`revam/dotnet-shoko-plugin-manifest`](https://github.com/revam/dotnet-shoko-plugin-manifest).
- `.github/workflows/release.yml` builds the project whenever a GitHub Release is published, zips the plugin's output
  (DLL + `dashboard/`), attaches it to the release, then rewrites `manifest.json` with the new version's download URL
  and SHA-256 checksum and pushes that to `main` — mirroring revam's release-driven update pattern.

To install it in Shoko:

1. Open the WebUI and go to **Settings → Plugins → Repositories**.
2. Add a new repository pointing at the *raw* manifest URL for this plugin:
   `https://raw.githubusercontent.com/krbrs/shoko-plugin-integrity-checker/main/manifest.json`
3. Browse the repository's plugin list, select **Integrity Checker**, and install it.
4. Restart Shoko Server. The dashboard then appears under **Settings → Plugins**.

### Cutting a release

Once pushed to GitHub:

1. Tag and publish a GitHub Release (e.g. tag `v0.1.0`).
2. The `Release Build` workflow builds the project, zips `ShokoIntegrityChecker.dll` + `dashboard/` as
   `ShokoIntegrityChecker-<tag>-any.zip`, uploads it to the release, computes its checksum, and prepends a new entry to
   `manifest.json`'s `releases` array — then commits and pushes that to `main`.
3. Anyone with the repository already added in Shoko will see the new version show up to install/update.

The workflow needs no extra secrets — it uses the default `GITHUB_TOKEN`, but that token needs permission to push to
`main` (enable "Read and write permissions" for *Workflow permissions* under **Settings → Actions → General**, or
branch-protect `main` to allow the `github-actions[bot]` actor).

## Possible follow-ups

- **Recurring schedule**: register the check via `RecurringJobRegistry` so it runs automatically (e.g. weekly)
  instead of only on demand.
- **Settings page**: expose `MaxAutoScanAttemptsPerVideo`-style throttling or concurrency limits if large libraries
  make a full re-hash too heavy to run during normal usage.
