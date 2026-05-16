'use strict';

// Ship-music tab. Renders one card per vanilla shanty slot; each card
// hosts a file picker for a single audio file (wav/mp3/ogg/flac/m4a/
// aac/opus, validated + transcoded server-side via ffmpeg.exe) plus
// per-slot upload status. The slot catalog comes from GET
// /api/profiles/{id}/ship-music so backend and frontend always agree
// on what's available.

// File extensions the backend's AudioPreprocessor accepts. Keep in
// sync with AudioPreprocessor.SupportedExtensions on the C# side - the
// server validates the same list, this is just so the OS file picker
// pre-filters out obviously-wrong files.
const SHIPMUSIC_AUDIO_EXTS = ['.wav', '.mp3', '.ogg', '.flac', '.m4a', '.aac', '.opus'];

function shipmusicAcceptList() {
    // Comma-separated for the file picker's accept attribute. Include
    // generic audio/* so phones / tablets still show their audio picker
    // when the browser doesn't know our extension list.
    return SHIPMUSIC_AUDIO_EXTS.join(',') + ',audio/*';
}

function shipmusicIsSupportedFile(name) {
    if (!name) return false;
    const lower = name.toLowerCase();
    for (const ext of SHIPMUSIC_AUDIO_EXTS) {
        if (lower.endsWith(ext)) return true;
    }
    return false;
}

function shipmusicProfileId() {
    return state.current && state.current.id ? state.current.id : null;
}

function formatBytes(n) {
    if (!n || n <= 0) return '0 B';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / (1024 * 1024)).toFixed(1) + ' MB';
}

// Renders the slot list inside the host card. Called from
// applyShipMusicToUI() on profile load and after every successful
// upload / delete to keep on-disk truth + UI in sync.
async function refreshShipMusicSlots() {
    const host = document.getElementById('shipmusic-slot-list');
    if (!host) return;
    const id = shipmusicProfileId();
    if (!id) {
        host.innerHTML = '<p class="hint">No profile loaded.</p>';
        return;
    }
    let data;
    try {
        data = await api('GET', '/api/profiles/' + encodeURIComponent(id) + '/ship-music');
    } catch (ex) {
        host.innerHTML = '<p class="hint" style="color: var(--accent);">'
            + 'Failed to load shanty slots: ' + (ex && ex.message ? ex.message : ex)
            + '</p>';
        return;
    }
    if (!data || !Array.isArray(data.slots) || data.slots.length === 0) {
        host.innerHTML = '<p class="hint">No shanty slots returned by the server.</p>';
        return;
    }

    host.innerHTML = '';
    for (const slot of data.slots) {
        host.appendChild(renderShipMusicSlot(slot));
    }
}

function renderShipMusicSlot(slot) {
    const row = document.createElement('div');
    row.className = 'shipmusic-slot';
    row.dataset.stem = slot.stem;

    const titleLine = document.createElement('div');
    titleLine.className = 'shipmusic-slot-title';
    const titleSpan = document.createElement('strong');
    titleSpan.textContent = slot.title;
    titleLine.appendChild(titleSpan);

    const stateBadge = document.createElement('span');
    stateBadge.className = 'shipmusic-state shipmusic-state-' + slot.state;
    if (slot.state === 'custom') {
        stateBadge.textContent = 'Custom';
    } else if (slot.state === 'broken') {
        stateBadge.textContent = 'WAV missing';
    } else {
        stateBadge.textContent = 'Vanilla';
    }
    titleLine.appendChild(stateBadge);
    row.appendChild(titleLine);

    // Filename + size line (only when the slot is overridden).
    if (slot.state !== 'vanilla') {
        const meta = document.createElement('div');
        meta.className = 'shipmusic-slot-meta hint';
        const parts = [];
        if (slot.originalFilename) parts.push(slot.originalFilename);
        if (slot.wavBytes) parts.push('(' + formatBytes(slot.wavBytes) + ' wav)');
        meta.textContent = parts.join(' ');
        row.appendChild(meta);
    }

    const controls = document.createElement('div');
    controls.className = 'shipmusic-slot-controls';

    // Hidden file input (we trigger it programmatically from the
    // visible "Browse..." button so we can dress the button up like
    // other buttons instead of using the ugly browser default).
    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.accept = shipmusicAcceptList();
    fileInput.style.display = 'none';
    const browseBtn = document.createElement('button');
    browseBtn.type = 'button';
    browseBtn.className = 'btn';
    browseBtn.textContent = 'Browse...';

    fileInput.addEventListener('change', () => {
        const originalText = browseBtn.textContent;
        browseBtn.disabled = true;
        browseBtn.textContent = 'Uploading...';
        uploadShipMusicSlot(slot, fileInput.files).catch(ex => {
            alert('Upload failed: ' + (ex && ex.message ? ex.message : ex));
        }).finally(() => {
            fileInput.value = '';
            browseBtn.disabled = false;
            browseBtn.textContent = originalText;
        });
    });
    row.appendChild(fileInput);

    browseBtn.addEventListener('click', () => fileInput.click());
    controls.appendChild(browseBtn);

    if (slot.state !== 'vanilla') {
        const resetBtn = document.createElement('button');
        resetBtn.type = 'button';
        resetBtn.className = 'btn btn-secondary';
        resetBtn.textContent = 'Reset';
        resetBtn.addEventListener('click', () => {
            resetShipMusicSlot(slot).catch(ex => {
                alert('Reset failed: ' + (ex && ex.message ? ex.message : ex));
            });
        });
        controls.appendChild(resetBtn);
    }

    row.appendChild(controls);
    return row;
}

async function uploadShipMusicSlot(slot, fileList) {
    const id = shipmusicProfileId();
    if (!id) {
        alert('No profile is loaded.');
        return;
    }
    if (!fileList || fileList.length === 0) return;

    // Find the first supported audio file in the picked list. The
    // input is single-select but we guard defensively for users who
    // drag multiple files in via a future drag-drop handler.
    let audio = null;
    for (const f of fileList) {
        if (shipmusicIsSupportedFile(f.name)) { audio = f; break; }
    }
    if (!audio) {
        alert('Pick an audio file. Supported formats: '
            + SHIPMUSIC_AUDIO_EXTS.join(', ') + '.');
        return;
    }

    const form = new FormData();
    // Send under "audio" (new key) - the endpoint also accepts the
    // legacy "wav" key for back-compat.
    form.append('audio', audio, audio.name);
    form.append('filename', audio.name);

    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music/' + encodeURIComponent(slot.stem);
    const res = await fetch(url, { method: 'POST', body: form });
    if (!res.ok) {
        // The endpoint returns clean JSON errors with the failure
        // reason verbatim (ffmpeg stderr, format mismatch, oversize,
        // ...); show those instead of the raw HTTP status when we can.
        let msg = 'HTTP ' + res.status;
        try {
            const j = await res.json();
            if (j && j.error) msg = j.error;
        } catch (_) {
            try {
                const t = await res.text();
                if (t) msg += ' ' + t;
            } catch (_) { /* fall through */ }
        }
        throw new Error(msg);
    }
    await refreshShipMusicSlots();
    // Reload profile to pull the new Songs entry into state.current so
    // the BUILD button + summary reflects it immediately, mirroring how
    // the icon upload endpoint re-reads after writing.
    await loadProfile(id);
}

async function resetShipMusicSlot(slot) {
    const id = shipmusicProfileId();
    if (!id) return;
    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music/' + encodeURIComponent(slot.stem);
    const res = await fetch(url, { method: 'DELETE' });
    if (!res.ok && res.status !== 204) {
        const txt = await res.text().catch(() => '');
        throw new Error('HTTP ' + res.status + ' ' + txt);
    }
    await refreshShipMusicSlots();
    await loadProfile(id);
}

// Called from applyProfileToUI() during profile load. Triggers a slot
// list refresh from the backend - the local profile already carries
// the Songs metadata, but the on-disk audio-file state (whether a
// slot's .wav actually exists) lives on the server and needs an HTTP
// roundtrip to surface.
function applyShipMusicToUI() {
    refreshShipMusicSlots().catch(ex => {
        console.warn('refreshShipMusicSlots failed:', ex);
    });
}

// Tab has no inline event-driven setters (uploads are async via the
// per-slot Browse button), so bindShipMusicHandlers is a no-op kept
// for symmetry with the other tabs' bind pattern.
function bindShipMusicHandlers() {
    // Slot cards rebind their own handlers each refresh.
}
