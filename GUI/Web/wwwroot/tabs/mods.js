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
        list.innerHTML = '<li class="mods-empty">' + esc(msg) + '</li>';
    } else {
        list.innerHTML = filtered.map(buildModRowHtml).join('');
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

function buildModRowHtml(f) {
    const cls = f.isQuartermaster ? 'mod owned' : 'mod foreign';
    const marker = f.isQuartermaster ? 'Q' : '*';
    const sizeKb = (f.sizeBytes / 1024).toFixed(1);
    const when = formatModifiedDate(f.modifiedUtc);
    const nameBlock = f.isQuartermaster
        ? ('<span class="display">' + esc(f.displayName || f.filename) + '</span>'
           + '<span class="filename">' + esc(f.filename) + '</span>')
        : ('<span class="filename">' + esc(f.filename) + '</span>');
    const actions = f.isQuartermaster
        ? '<button type="button" class="danger" data-delete-mod="' + esc(f.filename) + '" title="Move to recycle bin">Delete</button>'
        : '<span class="lock" title="Foreign mod - managed externally">read-only</span>';
    return '<li class="' + cls + '">'
         +   '<span class="mod-marker" title="' + (f.isQuartermaster ? 'Built by Quartermaster' : 'External mod') + '">' + marker + '</span>'
         +   '<div class="mod-name">' + nameBlock + '</div>'
         +   '<div class="mod-meta"><span>' + esc(sizeKb) + ' KB</span><span>' + esc(when) + '</span></div>'
         +   '<div class="mod-actions">' + actions + '</div>'
         + '</li>';
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
    document.getElementById('mods-list').addEventListener('click', e => {
        const t = e.target;
        if (t && t.dataset && t.dataset.deleteMod) {
            deleteMod(t.dataset.deleteMod);
        }
    });
    // Status-Probe beim Tab-Wechsel auf "Mods": ist verlustfrei (HEAD-style),
    // wird auch beim ersten Rendern ausgeloest.
    loadExportStatus();
}

// Liest /api/export/status und zeigt im Status-DIV wie viele Assets bereits
// extrahiert wurden. Schnell (nur Directory-Counts), kein CUE4Parse-Init.
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

// Streamt /api/export/building als SSE und schreibt Log-Lines ins globale
// Footer-Log (#build-log), das via setFooterCollapsed(false) automatisch
// aufgeklappt wird. Mirrors die SSE-Consumption aus app.js::runSetup.
function runBuildingExport() {
    const btn = document.getElementById('btn-export-building');
    const statusEl = document.getElementById('export-status');
    const logEl = document.getElementById('build-log');
    if (!btn || !statusEl || !logEl) return;

    btn.disabled = true;
    btn.textContent = 'Exporting...';
    statusEl.textContent = 'Export running...';

    // Footer aufklappen + Log leeren - exakt das Verhalten von onBuild,
    // damit ein laufender Export visuell genauso "live" wie ein Build
    // wirkt (auch wenn das Tab-Pane oben den Status weiter anzeigt).
    setFooterCollapsed(false);
    logEl.innerHTML = '';

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
