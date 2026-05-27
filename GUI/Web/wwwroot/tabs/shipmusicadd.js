'use strict';

// Ship-music ADD tab. Adds shanties beyond the vanilla 10 by extending
// each ship-type DA's Shanty.Cues array. Backend exposes:
//   GET    /api/profiles/{id}/ship-music-add           -> list
//   POST   /api/profiles/{id}/ship-music-add           -> add/replace
//   DELETE /api/profiles/{id}/ship-music-add/{trackKey} -> remove
//
// State: tracks live in profile.Globals.ShipMusicAdd.Tracks; on-disk
// audio.wav lives under Profiles/<id>/ShipMusicAdd/<trackKey>/.
// Save-button isn't involved - upload/delete persist via the endpoint
// immediately, just like the regular ship-music tab.

const SHIPMUSICADD_AUDIO_EXTS = ['.wav', '.mp3', '.ogg', '.flac', '.m4a', '.aac', '.opus'];

function shipmusicaddProfileId() {
    return state.current && state.current.id ? state.current.id : null;
}

function shipmusicaddIsSupportedFile(name) {
    if (!name) return false;
    const lower = name.toLowerCase();
    for (const ext of SHIPMUSICADD_AUDIO_EXTS) {
        if (lower.endsWith(ext)) return true;
    }
    return false;
}

function shipmusicaddFormatBytes(n) {
    if (!n || n <= 0) return '0 B';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / (1024 * 1024)).toFixed(1) + ' MB';
}

async function refreshShipMusicAddTracks() {
    const host = document.getElementById('shipmusicadd-track-list');
    if (!host) return;
    const id = shipmusicaddProfileId();
    if (!id) {
        host.innerHTML = '<p class="hint">No profile loaded.</p>';
        return;
    }
    let data;
    try {
        data = await api('GET', '/api/profiles/' + encodeURIComponent(id) + '/ship-music-add');
    } catch (ex) {
        host.innerHTML = '<p class="hint" style="color: var(--accent);">'
            + 'Failed to load added tracks: ' + (ex && ex.message ? ex.message : ex)
            + '</p>';
        return;
    }
    const tracks = data && Array.isArray(data.tracks) ? data.tracks : [];
    if (tracks.length === 0) {
        host.innerHTML = '<p class="hint">No added tracks yet. Use the form above to add your first.</p>';
        return;
    }
    host.innerHTML = '';
    for (const t of tracks) {
        host.appendChild(renderShipMusicAddTrack(t));
    }
}

function renderShipMusicAddTrack(track) {
    const row = document.createElement('div');
    row.className = 'shipmusicadd-track';
    row.dataset.trackKey = track.trackKey;

    const titleLine = document.createElement('div');
    titleLine.className = 'shipmusicadd-track-title';

    const idxBadge = document.createElement('span');
    idxBadge.className = 'shipmusicadd-track-index';
    idxBadge.textContent = '#' + (track.newIndex || '?');
    titleLine.appendChild(idxBadge);

    const titleSpan = document.createElement('strong');
    titleSpan.textContent = track.title || track.trackKey;
    titleLine.appendChild(titleSpan);

    if (track.title && track.title !== track.trackKey) {
        const keySpan = document.createElement('span');
        keySpan.className = 'shipmusicadd-track-meta';
        keySpan.textContent = '(' + track.trackKey + ')';
        titleLine.appendChild(keySpan);
    }

    const stateBadge = document.createElement('span');
    stateBadge.className = 'shipmusicadd-state shipmusicadd-state-' + track.state;
    stateBadge.textContent = track.state === 'ready' ? 'Ready' : 'WAV missing';
    titleLine.appendChild(stateBadge);
    row.appendChild(titleLine);

    const meta = document.createElement('div');
    meta.className = 'shipmusicadd-track-meta';
    const parts = [];
    if (track.originalFilename) parts.push(track.originalFilename);
    if (track.wavBytes) parts.push('(' + shipmusicaddFormatBytes(track.wavBytes) + ' wav)');
    if (parts.length > 0) {
        meta.textContent = parts.join(' ');
        row.appendChild(meta);
    }

    const controls = document.createElement('div');
    controls.className = 'shipmusicadd-track-controls';

    const replaceInput = document.createElement('input');
    replaceInput.type = 'file';
    replaceInput.accept = SHIPMUSICADD_AUDIO_EXTS.join(',') + ',audio/*';
    replaceInput.style.display = 'none';
    const replaceBtn = document.createElement('button');
    replaceBtn.type = 'button';
    replaceBtn.className = 'btn';
    replaceBtn.textContent = 'Replace audio';

    replaceInput.addEventListener('change', () => {
        const original = replaceBtn.textContent;
        replaceBtn.disabled = true;
        replaceBtn.textContent = 'Uploading...';
        uploadShipMusicAddTrack(track.trackKey, track.title || '', replaceInput.files).catch(ex => {
            alert('Upload failed: ' + (ex && ex.message ? ex.message : ex));
        }).finally(() => {
            replaceInput.value = '';
            replaceBtn.disabled = false;
            replaceBtn.textContent = original;
        });
    });
    row.appendChild(replaceInput);
    replaceBtn.addEventListener('click', () => replaceInput.click());
    controls.appendChild(replaceBtn);

    const delBtn = document.createElement('button');
    delBtn.type = 'button';
    delBtn.className = 'btn btn-secondary';
    delBtn.textContent = 'Remove';
    delBtn.addEventListener('click', () => {
        if (!confirm('Remove track "' + (track.title || track.trackKey) + '"?')) return;
        deleteShipMusicAddTrack(track.trackKey).catch(ex => {
            alert('Delete failed: ' + (ex && ex.message ? ex.message : ex));
        });
    });
    controls.appendChild(delBtn);

    row.appendChild(controls);
    return row;
}

async function uploadShipMusicAddTrack(trackKey, title, fileList) {
    const id = shipmusicaddProfileId();
    if (!id) { alert('No profile is loaded.'); return; }
    if (!trackKey) { alert('TrackKey is required.'); return; }
    if (!fileList || fileList.length === 0) return;

    let audio = null;
    for (const f of fileList) {
        if (shipmusicaddIsSupportedFile(f.name)) { audio = f; break; }
    }
    if (!audio) {
        alert('Pick an audio file. Supported formats: '
            + SHIPMUSICADD_AUDIO_EXTS.join(', ') + '.');
        return;
    }

    const form = new FormData();
    form.append('trackKey', trackKey);
    if (title) form.append('title', title);
    form.append('audio', audio, audio.name);
    form.append('filename', audio.name);

    const url = '/api/profiles/' + encodeURIComponent(id) + '/ship-music-add';
    const res = await fetch(url, { method: 'POST', body: form });
    if (!res.ok) {
        let msg = 'HTTP ' + res.status;
        try {
            const j = await res.json();
            if (j && j.error) msg = j.error;
        } catch (_) { /* fall through */ }
        throw new Error(msg);
    }
    await refreshShipMusicAddTracks();
    await loadProfile(id);
}

async function deleteShipMusicAddTrack(trackKey) {
    const id = shipmusicaddProfileId();
    if (!id) return;
    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music-add/' + encodeURIComponent(trackKey);
    const res = await fetch(url, { method: 'DELETE' });
    if (!res.ok && res.status !== 204) {
        const txt = await res.text().catch(() => '');
        throw new Error('HTTP ' + res.status + ' ' + txt);
    }
    await refreshShipMusicAddTracks();
    await loadProfile(id);
}

function applyShipMusicAddToUI() {
    refreshShipMusicAddTracks().catch(ex => {
        console.warn('refreshShipMusicAddTracks failed:', ex);
    });
}

function bindShipMusicAddHandlers() {
    const form = document.getElementById('shipmusicadd-add-form');
    if (!form || form.dataset.bound === '1') return;
    form.dataset.bound = '1';
    form.addEventListener('submit', ev => {
        ev.preventDefault();
        const keyEl = document.getElementById('shipmusicadd-trackkey');
        const titleEl = document.getElementById('shipmusicadd-title');
        const audioEl = document.getElementById('shipmusicadd-audio');
        const statusEl = document.getElementById('shipmusicadd-add-status');
        const btn = document.getElementById('shipmusicadd-add-btn');
        const trackKey = (keyEl?.value || '').trim();
        const title = (titleEl?.value || '').trim();
        if (!trackKey) { alert('Track Key is required.'); return; }
        if (!/^[A-Za-z0-9_]+$/.test(trackKey)) {
            alert('Track Key may only contain letters, digits and underscore.');
            return;
        }
        const files = audioEl?.files;
        if (!files || files.length === 0) {
            alert('Pick an audio file.');
            return;
        }
        btn.disabled = true;
        if (statusEl) statusEl.textContent = 'Uploading...';
        uploadShipMusicAddTrack(trackKey, title, files).then(() => {
            keyEl.value = '';
            titleEl.value = '';
            audioEl.value = '';
            if (statusEl) statusEl.textContent = 'Added.';
        }).catch(ex => {
            if (statusEl) statusEl.textContent = '';
            alert('Add failed: ' + (ex && ex.message ? ex.message : ex));
        }).finally(() => {
            btn.disabled = false;
        });
    });
}
