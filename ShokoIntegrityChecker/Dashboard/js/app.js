(() => {
  "use strict";

  // The dashboard is served from the same controller as the API, so relative
  // URLs resolve correctly whether it's opened directly or embedded in an
  // iframe by the WebUI.
  const API_BASE = "../";
  const POLL_INTERVAL_MS = 1500;

  const els = {
    runBtn: document.getElementById("run-btn"),
    cancelBtn: document.getElementById("cancel-btn"),
    scopeToggleBtn: document.getElementById("scope-toggle-btn"),
    foldersLoading: document.getElementById("folders-loading"),
    foldersLoadingText: document.getElementById("folders-loading-text"),
    foldersList: document.getElementById("folders-list"),
    statusPill: document.getElementById("status-pill"),
    progressWrap: document.getElementById("progress-wrap"),
    progressBar: document.getElementById("progress-bar"),
    progressText: document.getElementById("progress-text"),
    currentFile: document.getElementById("current-file"),
    summary: document.getElementById("summary"),
    errorBanner: document.getElementById("error-banner"),
    resultsEmpty: document.getElementById("results-empty"),
    resultsTable: document.getElementById("results-table"),
    resultsBody: document.getElementById("results-body"),
  };

  let pollHandle = null;
  let folders = [];
  const selectedFolderIDs = new Set();

  async function apiPost(path, body) {
    const init = { method: "POST" };
    if (body !== undefined) {
      init.headers = { "Content-Type": "application/json" };
      init.body = JSON.stringify(body);
    }
    const response = await fetch(`${API_BASE}${path}`, init);
    if (!response.ok && response.status !== 409) {
      throw new Error(`Request to ${path} failed (${response.status})`);
    }
    return response;
  }

  // Shoko's v3 API serializes with Newtonsoft's DefaultContractResolver, which
  // preserves the C# (PascalCase) property names as-is. Normalize to
  // camelCase here so the rest of the dashboard can stay framework-agnostic.
  function camelize(value) {
    if (Array.isArray(value)) return value.map(camelize);
    if (value !== null && typeof value === "object") {
      const result = {};
      for (const [key, inner] of Object.entries(value)) {
        const camelKey = key.length > 0 ? key[0].toLowerCase() + key.slice(1) : key;
        result[camelKey] = camelize(inner);
      }
      return result;
    }
    return value;
  }

  async function fetchStatus() {
    const response = await fetch(`${API_BASE}status`, { cache: "no-store" });
    if (!response.ok) throw new Error(`Failed to fetch status (${response.status})`);
    return camelize(await response.json());
  }

  async function fetchFolders() {
    const response = await fetch(`${API_BASE}folders`, { cache: "no-store" });
    if (!response.ok) throw new Error(`Failed to fetch managed folders (${response.status})`);
    return camelize(await response.json());
  }

  function updateScopeToggleLabel() {
    els.scopeToggleBtn.textContent = selectedFolderIDs.size >= folders.length ? "Select none" : "Select all";
  }

  function renderFolders() {
    if (folders.length === 0) {
      els.foldersLoading.hidden = false;
      els.foldersLoading.classList.remove("empty-state--loading");
      els.foldersLoadingText.textContent = "No managed folders found.";
      els.foldersList.hidden = true;
      return;
    }

    els.foldersLoading.hidden = true;
    els.foldersList.hidden = false;
    els.foldersList.innerHTML = folders
      .map((folder) => {
        const checked = selectedFolderIDs.has(folder.managedFolderID) ? "checked" : "";
        return `
          <label class="folder-list__item">
            <input type="checkbox" data-folder-id="${folder.managedFolderID}" ${checked} />
            <span>
              <span class="folder-list__name">${escapeHtml(folder.name)}</span>
              <span class="folder-list__path mono">${escapeHtml(folder.path)}</span>
            </span>
          </label>
        `;
      })
      .join("");

    for (const checkbox of els.foldersList.querySelectorAll("input[type=checkbox]")) {
      checkbox.addEventListener("change", () => {
        const id = Number(checkbox.dataset.folderId);
        if (checkbox.checked) selectedFolderIDs.add(id);
        else selectedFolderIDs.delete(id);
        updateScopeToggleLabel();
      });
    }

    updateScopeToggleLabel();
  }

  async function loadFolders(status) {
    try {
      folders = await fetchFolders();
      selectedFolderIDs.clear();

      // If a scoped run is currently in progress (e.g. the dashboard was
      // reloaded mid-run), mirror *its* selection instead of defaulting to
      // "everything selected" — otherwise the greyed-out picker shows a
      // different selection than the run that's actually executing, which
      // looks like the user's choice got reset. Matched by name since that's
      // all the status exposes; managed folder names are unique.
      const runningScope = status?.isRunning ? status.scopedFolderNames || [] : [];
      if (runningScope.length > 0) {
        const scopedNames = new Set(runningScope);
        for (const folder of folders) {
          if (scopedNames.has(folder.name)) selectedFolderIDs.add(folder.managedFolderID);
        }
      } else {
        // Otherwise default to "everything selected" so the picker mirrors
        // the previous, unscoped behaviour until the user narrows it down.
        for (const folder of folders) selectedFolderIDs.add(folder.managedFolderID);
      }

      renderFolders();
    } catch (err) {
      console.error(err);
      els.foldersLoading.hidden = false;
      els.foldersLoading.classList.remove("empty-state--loading");
      els.foldersLoadingText.textContent = "Couldn't load managed folders — see the browser console for details.";
      els.foldersList.hidden = true;
    }
  }

  function setStatusPill(status) {
    els.statusPill.classList.remove(
      "status-pill--idle",
      "status-pill--running",
      "status-pill--cancelling",
      "status-pill--done",
    );

    if (status.isRunning && status.isCancellationRequested) {
      els.statusPill.textContent = "Cancelling…";
      els.statusPill.classList.add("status-pill--cancelling");
    } else if (status.isRunning) {
      els.statusPill.textContent = "Running";
      els.statusPill.classList.add("status-pill--running");
    } else if (status.completedAt) {
      els.statusPill.textContent = "Done";
      els.statusPill.classList.add("status-pill--done");
    } else {
      els.statusPill.textContent = "Idle";
      els.statusPill.classList.add("status-pill--idle");
    }
  }

  function renderProgress(status) {
    if (!status.isRunning && !status.completedAt) {
      els.progressWrap.hidden = true;
      return;
    }

    els.progressWrap.hidden = false;
    const total = status.totalFiles || 0;
    const processed = status.processedFiles || 0;
    const pct = total > 0 ? Math.min(100, Math.round((processed / total) * 100)) : 0;
    els.progressBar.style.width = `${pct}%`;
    els.progressText.textContent = `${processed} / ${total}`;
    els.currentFile.textContent = status.isRunning ? (status.currentFile || "") : "";
  }

  function renderSummary(status) {
    if (status.isRunning || !status.completedAt) {
      els.summary.hidden = true;
      return;
    }

    const started = status.startedAt ? new Date(status.startedAt) : null;
    const completed = new Date(status.completedAt);
    let durationText = "";
    if (started) {
      const seconds = Math.max(0, Math.round((completed - started) / 1000));
      const mins = Math.floor(seconds / 60);
      const secs = seconds % 60;
      durationText = mins > 0 ? ` in ${mins}m ${secs}s` : ` in ${secs}s`;
    }

    const mismatches = status.mismatches || [];
    const needsRematch = mismatches.filter((issue) => !issue.isRecognized).length;
    const autoRematched = mismatches.length - needsRematch;
    let changesText = `${mismatches.length} hash change(s)`;
    if (mismatches.length > 0) {
      changesText += ` (${needsRematch} need re-matching, ${autoRematched} auto re-matched)`;
    }

    const scopedNames = status.scopedFolderNames || [];
    const scopeText = scopedNames.length > 0
      ? ` Scoped to: ${scopedNames.join(", ")}.`
      : "";

    els.summary.hidden = false;
    els.summary.textContent =
      `Last run finished ${completed.toLocaleString()}${durationText}: ` +
      `${status.processedFiles} checked, ${changesText}, ` +
      `${status.skippedFiles} skipped (unavailable on disk).${scopeText}`;
  }

  function renderError(status) {
    if (status.lastError) {
      els.errorBanner.hidden = false;
      els.errorBanner.textContent = status.lastError;
    } else {
      els.errorBanner.hidden = true;
      els.errorBanner.textContent = "";
    }
  }

  function renderResults(status) {
    const mismatches = status.mismatches || [];
    if (mismatches.length === 0) {
      els.resultsEmpty.hidden = false;
      els.resultsTable.hidden = true;
      els.resultsBody.innerHTML = "";
      return;
    }

    els.resultsEmpty.hidden = true;
    els.resultsTable.hidden = false;
    els.resultsBody.innerHTML = mismatches
      .map((issue) => {
        const detected = new Date(issue.detectedAt);
        const badge = issue.isRecognized
          ? `<span class="badge badge--ok">Auto re-matched</span>`
          : `<span class="badge badge--attention">Needs re-matching</span>`;
        return `
          <tr>
            <td>
              <div>${escapeHtml(issue.fileName)}</div>
              <div class="mono" style="color: var(--muted); font-size: 0.75rem;">${escapeHtml(issue.relativePath)}</div>
            </td>
            <td>${escapeHtml(issue.managedFolderName)}</td>
            <td class="mono">${escapeHtml(issue.previousHash)}</td>
            <td class="mono">${escapeHtml(issue.newHash)}</td>
            <td>${badge}</td>
            <td>${detected.toLocaleString()}</td>
          </tr>
        `;
      })
      .join("");
  }

  function escapeHtml(value) {
    const div = document.createElement("div");
    div.textContent = value ?? "";
    return div.innerHTML;
  }

  function applyStatus(status) {
    setStatusPill(status);
    renderProgress(status);
    renderSummary(status);
    renderError(status);
    renderResults(status);

    els.runBtn.disabled = status.isRunning;
    els.cancelBtn.hidden = !status.isRunning;
    els.cancelBtn.disabled = status.isCancellationRequested;

    // The scope only matters for the next run — lock it while one's in
    // progress so it can't look like it's affecting the current run.
    els.scopeToggleBtn.disabled = status.isRunning;
    for (const checkbox of els.foldersList.querySelectorAll("input[type=checkbox]"))
      checkbox.disabled = status.isRunning;
  }

  function startPolling() {
    if (pollHandle) return;
    pollHandle = setInterval(refresh, POLL_INTERVAL_MS);
  }

  function stopPollingIfIdle(status) {
    if (!status.isRunning && pollHandle) {
      clearInterval(pollHandle);
      pollHandle = null;
    }
  }

  async function refresh() {
    try {
      const status = await fetchStatus();
      applyStatus(status);
      stopPollingIfIdle(status);
      if (status.isRunning) startPolling();
      return status;
    } catch (err) {
      console.error(err);
      return null;
    }
  }

  els.scopeToggleBtn.addEventListener("click", () => {
    if (selectedFolderIDs.size >= folders.length) {
      selectedFolderIDs.clear();
    } else {
      selectedFolderIDs.clear();
      for (const folder of folders) selectedFolderIDs.add(folder.managedFolderID);
    }
    renderFolders();
  });

  els.runBtn.addEventListener("click", async () => {
    if (folders.length > 0 && selectedFolderIDs.size === 0) {
      els.errorBanner.hidden = false;
      els.errorBanner.textContent = "Select at least one managed folder to check.";
      return;
    }

    els.runBtn.disabled = true;
    try {
      // Sending every folder's ID is equivalent to sending none (both mean
      // "check everything"), but being explicit keeps the dashboard's
      // selection and the run's actual scope visibly in sync.
      const managedFolderIDs = selectedFolderIDs.size === folders.length ? null : [...selectedFolderIDs];
      await apiPost("run", { managedFolderIDs });
      startPolling();
      await refresh();
    } catch (err) {
      console.error(err);
      els.errorBanner.hidden = false;
      els.errorBanner.textContent = "Couldn't start the integrity check — see the browser console for details.";
      els.runBtn.disabled = false;
    }
  });

  els.cancelBtn.addEventListener("click", async () => {
    els.cancelBtn.disabled = true;
    try {
      await apiPost("cancel");
      await refresh();
    } catch (err) {
      console.error(err);
    }
  });

  // Fetch the current status *before* loading the folder picker, so that if a
  // scoped run is already in progress (e.g. the dashboard was just reloaded),
  // the picker can mirror that run's actual selection — see loadFolders().
  (async () => {
    const status = await refresh();
    await loadFolders(status);
  })();
})();
