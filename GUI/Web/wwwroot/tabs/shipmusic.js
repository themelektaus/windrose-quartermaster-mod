'use strict';

// Ship-music tab. Renders one card per vanilla shanty slot; each card
// hosts a file picker for a single .wav (44.1 kHz / stereo / 16-bit
// PCM, validated server-side), an optional display-name input, and
// per-slot upload status. The slot catalog comes from
// GET /api/profiles/{id}/ship-music so backend and frontend always
// agree on what's available.

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

    // Custom-name + filename line (only when the slot is overridden).
    if (slot.state !== 'vanilla') {
        const meta = document.createElement('div');
        meta.className = 'shipmusic-slot-meta hint';
        const parts = [];
        if (slot.displayName) parts.push(slot.displayName);
        if (slot.originalFilename) parts.push('(' + slot.originalFilename + ')');
        if (slot.wavBytes) parts.push(formatBytes(slot.wavBytes) + ' wav');
        meta.textContent = parts.join(' ');
        row.appendChild(meta);
    }

    // Display-name input - only meaningful with an upload to attach
    // to. When the slot is vanilla we still show the field so the
    // user can pre-fill a name before browsing.
    const nameRow = document.createElement('div');
    nameRow.className = 'shipmusic-slot-controls';

    const nameInput = document.createElement('input');
    nameInput.type = 'text';
    nameInput.placeholder = 'Optional display name (e.g. "My Pirate Banger")';
    nameInput.className = 'shipmusic-name-input';
    nameInput.value = slot.displayName || '';
    nameRow.appendChild(nameInput);

    // Hidden file input (we trigger it programmatically from the
    // visible "Browse..." button so we can dress the button up like
    // other buttons instead of using the ugly browser default).
    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.accept = '.wav,audio/wav,audio/x-wav';
    fileInput.style.display = 'none';
    fileInput.addEventListener('change', () => {
        uploadShipMusicSlot(slot, fileInput.files, nameInput.value).catch(ex => {
            alert('Upload failed: ' + (ex && ex.message ? ex.message : ex));
        }).finally(() => {
            fileInput.value = '';
        });
    });
    row.appendChild(fileInput);

    const browseBtn = document.createElement('button');
    browseBtn.type = 'button';
    browseBtn.className = 'btn';
    browseBtn.textContent = 'Browse...';
    browseBtn.addEventListener('click', () => fileInput.click());
    nameRow.appendChild(browseBtn);

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
        nameRow.appendChild(resetBtn);
    }

    row.appendChild(nameRow);
    return row;
}

async function uploadShipMusicSlot(slot, fileList, displayName) {
    const id = shipmusicProfileId();
    if (!id) {
        alert('No profile is loaded.');
        return;
    }
    if (!fileList || fileList.length === 0) return;

    // Find the .wav in the picked files. The input is set to single
    // selection but we still guard defensively.
    let wav = null;
    for (const f of fileList) {
        const lower = (f.name || '').toLowerCase();
        if (lower.endsWith('.wav')) { wav = f; break; }
    }
    if (!wav) {
        alert('Pick a .wav file. Other audio formats need to be converted '
            + 'first (Audacity, or ffmpeg: '
            + '`ffmpeg -i in.mp3 -ar 44100 -ac 2 -sample_fmt s16 out.wav`).');
        return;
    }

    const form = new FormData();
    form.append('wav', wav, wav.name);
    if (displayName) form.append('name', displayName);
    form.append('filename', wav.name);

    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music/' + encodeURIComponent(slot.stem);
    const res = await fetch(url, { method: 'POST', body: form });
    if (!res.ok) {
        // The endpoint returns clean JSON errors with a "remediate
        // with ffmpeg" hint for the common format-mismatch case; show
        // those verbatim instead of the raw HTTP status when we can.
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
