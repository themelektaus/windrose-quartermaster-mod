'use strict';

async function loadMods() {
    const dirEl = document.getElementById('mods-dir');
    dirEl.textContent = 'Loading...';
    try {
        const r = await fetch('/api/mods');
        const data = await r.json();
        if (!r.ok || data.error) {
            state.mods.loaded = true;
            state.mods.modsDir = data && data.modsDir ? data.modsDir : null;
            state.mods.files = [];
            state.mods.error = (data && data.error) || ('HTTP ' + r.status);
        } else {
            state.mods.loaded = true;
            state.mods.modsDir = data.modsDir;
            state.mods.files = data.files || [];
            state.mods.error = null;
        }
    } catch (e) {
        state.mods.loaded = true;
        state.mods.error = 'Network error: ' + e.message;
        state.mods.files = [];
        state.mods.modsDir = null;
    }
    renderMods();
    renderModsStatus();
}

function renderMods() {
    const dirEl = document.getElementById('mods-dir');
    dirEl.textContent = state.mods.modsDir || '(unknown)';

    const errEl = document.getElementById('mods-error');
    if (state.mods.error) {
        errEl.textContent = state.mods.error;
        errEl.hidden = false;
    } else {
        errEl.hidden = true;
    }

    const list  = document.getElementById('mods-list');
    const filtered = filterMods();

    if (filtered.length === 0) {
        const msg = state.mods.files.length === 0
            ? 'No .pak files in this folder yet. Build a profile to drop one here.'
            : 'No mods match the current filter.';
        const li = document.createElement('li');
        li.className = 'mods-empty';
        li.textContent = msg;
        list.replaceChildren(li);
    } else {
        const frag = document.createDocumentFragment();
        for (const f of filtered) frag.appendChild(buildModRowNode(f));
        list.replaceChildren(frag);
    }

    document.getElementById('mods-count').textContent =
        filtered.length + ' / ' + state.mods.files.length + ' mods';
}

function filterMods() {
    const q = (document.getElementById('mods-filter').value || '').trim().toLowerCase();
    const src = document.getElementById('mods-filter-source').value;
    const out = [];
    for (const f of state.mods.files) {
        if (src === 'owned'   && !f.isQuartermaster) continue;
        if (src === 'foreign' &&  f.isQuartermaster) continue;
        if (q) {
            const hay = (f.filename + ' ' + (f.displayName || '')).toLowerCase();
            if (!hay.includes(q)) continue;
        }
        out.push(f);
    }
    return out;
}

function buildModRowNode(f) {
    const row = cloneTemplate('tpl-mod-row');
    row.className = f.isQuartermaster ? 'mod owned' : 'mod foreign';

    const marker = row.querySelector('.mod-marker');
    marker.textContent = f.isQuartermaster ? 'Q' : '*';
    marker.title = f.isQuartermaster ? 'Built by Quartermaster' : 'External mod';

    const nameEl = row.querySelector('.mod-name');
    if (f.isQuartermaster) {
        const disp = document.createElement('span');
        disp.className = 'display';
        disp.textContent = f.displayName || f.filename;
        nameEl.appendChild(disp);
    }
    const fname = document.createElement('span');
    fname.className = 'filename';
    fname.textContent = f.filename;
    nameEl.appendChild(fname);

    row.querySelector('.mod-size').textContent = (f.sizeBytes / 1024).toFixed(1) + ' KB';
    row.querySelector('.mod-when').textContent = formatModifiedDate(f.modifiedUtc);

    const actions = row.querySelector('.mod-actions');
    if (f.isQuartermaster) {
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.className = 'danger';
        btn.dataset.deleteMod = f.filename;
        btn.title = 'Move to recycle bin';
        btn.textContent = 'Delete';
        actions.appendChild(btn);
    } else {
        const lock = document.createElement('span');
        lock.className = 'lock';
        lock.title = 'Foreign mod - managed externally';
        lock.textContent = 'read-only';
        actions.appendChild(lock);
    }
    return row;
}

function formatModifiedDate(iso) {
    if (!iso) return '';
    try {
        const d = new Date(iso);
        if (isNaN(d.getTime())) return '';
        const pad = n => String(n).padStart(2, '0');
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate())
             + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    } catch (e) {
        return '';
    }
}

function renderModsStatus() {
    const total   = state.mods.files.length;
    const owned   = state.mods.files.filter(f => f.isQuartermaster).length;
    const foreign = total - owned;
    document.getElementById('mods-stat-total').textContent   = total;
    document.getElementById('mods-stat-owned').textContent   = owned;
    document.getElementById('mods-stat-foreign').textContent = foreign;
}

async function deleteMod(filename) {
    const file = state.mods.files.find(f => f.filename === filename);
    if (!file || !file.isQuartermaster) return;
    if (!await confirm('Move "' + filename + '" to the recycle bin?')) return;
    try {
        const r = await fetch('/api/mods/' + encodeURIComponent(filename), { method: 'DELETE' });
        const data = await r.json();
        if (!r.ok || !data.success) {
            await alert('Delete failed: ' + (data.error || ('HTTP ' + r.status)));
            return;
        }
    } catch (e) {
        await alert('Network error: ' + e.message);
        return;
    }
    await loadMods();
}

function bindModsHandlers() {
    document.getElementById('mods-filter').addEventListener('input',          renderMods);
    document.getElementById('mods-filter-source').addEventListener('change',  renderMods);
    document.getElementById('mods-refresh').addEventListener('click',         loadMods);
    document.getElementById('btn-open-setup').addEventListener('click',       openSetupManually);
    document.getElementById('btn-export-building').addEventListener('click',  runBuildingExport);
    document.getElementById('btn-configure-game-install').addEventListener('click', () => openGameInstallModal());
    document.getElementById('mods-list').addEventListener('click', e => {
        const t = e.target;
        if (t && t.dataset && t.dataset.deleteMod) {
            deleteMod(t.dataset.deleteMod);
        }
    });
    loadExportStatus();
    loadGameInstallStatus();
}

// Probes /api/game-install (no-throw) and paints the Mods-tab Game-install
// card so the user sees at a glance which path Quartermaster currently
// resolves (override vs Steam auto-detect vs broken).
async function loadGameInstallStatus() {
    const el = document.getElementById('game-install-status');
    if (!el) return;
    try {
        const r = await fetch('/api/game-install');
        const data = await r.json();
        if (!r.ok) {
            el.className = 'game-install-status bad';
            el.textContent = 'Status check failed: HTTP ' + r.status;
            return;
        }
        renderGameInstallStatus(el, data);
    } catch (e) {
        el.className = 'game-install-status bad';
        el.textContent = 'Status check failed: ' + e.message;
    }
}

function renderGameInstallStatus(el, data) {
    if (data.isResolved) {
        const isOverride = data.overrideSet && data.overrideValid;
        el.className = 'game-install-status ' + (isOverride ? 'ok' : 'steam');
        const labelText = isOverride ? 'Manual override' : 'Steam auto-detect';
        const span = document.createElement('span');
        span.className = 'label';
        span.textContent = labelText;
        el.replaceChildren(span, document.createTextNode(data.effectiveGameRoot || data.effectiveVanillaPak || '(unknown)'));
    } else {
        el.className = 'game-install-status bad';
        const span = document.createElement('span');
        span.className = 'label';
        span.textContent = 'Not configured';
        el.replaceChildren(span, document.createTextNode(data.effectiveError || data.overrideError || data.steamError || 'No game install found.'));
    }
}

// Opens the manual game-install configuration modal. If the override is
// unset, attempts a Steam auto-detect and uses that as the default value
// the user can accept or replace. The modal stays open until the user
// either saves a valid path, clears the override, or cancels.
async function openGameInstallModal() {
    let data;
    try {
        const r = await fetch('/api/game-install');
        data = await r.json();
        if (!r.ok) throw new Error('HTTP ' + r.status);
    } catch (e) {
        await alert('Could not load game-install status: ' + e.message);
        return;
    }

    // Pre-fill: prefer the user's stored override, else the Steam auto-detect
    // suggestion, else empty.
    const initial = (data.overrideSet && data.overrideGameRoot)
        ? data.overrideGameRoot
        : (data.steamGameRoot || '');

    const result = await showGameInstallModal({
        initialValue: initial,
        status: data,
    });

    // Refresh the Mods-tab card after the modal closes so the new state
    // (override saved, cleared, or unchanged) shows immediately.
    await loadGameInstallStatus();

    // If the override actually changed, the mods directory may now resolve
    // to a different path - re-run the same fetch the Refresh button does
    // so the user sees the new mods list without an extra click.
    if (result === 'saved' || result === 'cleared') {
        await loadMods();
    }
}
window.openGameInstallModal = openGameInstallModal;

async function loadExportStatus() {
    const statusEl = document.getElementById('export-status');
    if (!statusEl) return;
    try {
        const r = await fetch('/api/export/status');
        const data = await r.json();
        if (!r.ok || data.error) {
            statusEl.textContent = 'Status check failed: ' + ((data && data.error) || ('HTTP ' + r.status));
            return;
        }
        if (data.isRunning) {
            statusEl.textContent = 'An export is currently running...';
            return;
        }
        const total = data.totalUassetCount || 0;
        if (total === 0) {
            statusEl.textContent = 'No assets extracted yet';
        } else {
            const parts = [];
            if (data.gameplay && data.gameplay.uassetCount > 0)
                parts.push(data.gameplay.uassetCount + ' gameplay');
            if (data.environment && data.environment.uassetCount > 0)
                parts.push(data.environment.uassetCount + ' environment');
            if (data.audio && data.audio.uassetCount > 0)
                parts.push(data.audio.uassetCount + ' audio');
            statusEl.textContent = total + ' .uasset files on disk (' + parts.join(', ') + ')';
        }
    } catch (e) {
        statusEl.textContent = 'Status check failed: ' + e.message;
    }
}

function runBuildingExport() {
    const btn = document.getElementById('btn-export-building');
    const statusEl = document.getElementById('export-status');
    const logEl = document.getElementById('build-log');
    if (!btn || !statusEl || !logEl) return;

    btn.disabled = true;
    btn.textContent = 'Exporting...';
    statusEl.textContent = 'Export running...';

    setFooterCollapsed(false);
    logEl.replaceChildren();

    const append = (line, kind) => {
        const span = document.createElement('span');
        if (kind) span.className = kind;
        span.textContent = line;
        logEl.appendChild(span);
        logEl.appendChild(document.createTextNode('\n'));
        logEl.scrollTop = logEl.scrollHeight;
    };

    fetch('/api/export/building', { method: 'POST' }).then(async resp => {
        if (!resp.ok) {
            const text = await resp.text().catch(() => resp.statusText);
            append('HTTP ' + resp.status + ': ' + text, 'err');
            btn.disabled = false;
            btn.textContent = 'Export building assets';
            return;
        }
        const reader = resp.body.getReader();
        const dec = new TextDecoder();
        let buf = '';
        let finalPayload = null;

        while (true) {
            const { value, done } = await reader.read();
            if (done) break;
            buf += dec.decode(value, { stream: true });
            let idx;
            while ((idx = buf.indexOf('\n\n')) >= 0) {
                const frame = buf.slice(0, idx);
                buf = buf.slice(idx + 2);
                let event = 'message', data = '';
                for (const ln of frame.split('\n')) {
                    if      (ln.startsWith('event: ')) event = ln.slice(7).trim();
                    else if (ln.startsWith('data: '))  data  = ln.slice(6);
                }
                if (event === 'log') {
                    append(data, classifyLogLine(data));
                } else if (event === 'done') {
                    try { finalPayload = JSON.parse(data); } catch (e) { /* keep null */ }
                }
            }
        }

        btn.disabled = false;
        btn.textContent = 'Export building assets';
        if (finalPayload && finalPayload.success) {
            const w = finalPayload.filesWritten || 0;
            const s = finalPayload.filesSkippedExisting || 0;
            const f = finalPayload.filesFailed || 0;
            append('', null);
            append('Done. ' + w + ' written, ' + s + ' skipped, ' + f + ' failed.', 'ok');
        } else if (finalPayload && finalPayload.error) {
            append('', null);
            append('Export failed: ' + finalPayload.error, 'err');
        }
        loadExportStatus();
    }).catch(err => {
        append('Network error: ' + err.message, 'err');
        btn.disabled = false;
        btn.textContent = 'Export building assets';
        statusEl.textContent = 'Export failed';
    });
}
