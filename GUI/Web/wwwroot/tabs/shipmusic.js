'use strict';

// Sea Shanties tab (merged from former Ship Music + Ship Music+).
// Owns three cards in one pane:
//   1. Vanilla Shanty Slots - 10 vanilla shanties, per row:
//      - Browse...: upload an audio override (SWAV replace)
//      - Reset:     drop the override, fall back to vanilla
//      - Exclude:   toggle slot off the rotation (Include re-adds)
//   2. Added Tracks - user-supplied shanties beyond vanilla 10
//   3. Add Track    - form to upload a new track
//
// Backend endpoints used:
//   GET    /api/profiles/{id}/ship-music                       slot list (incl. excluded flag)
//   POST   /api/profiles/{id}/ship-music/{slotStem}            upload override audio
//   DELETE /api/profiles/{id}/ship-music/{slotStem}            drop override
//   POST   /api/profiles/{id}/ship-music/{slotStem}/exclude    exclude slot from rotation
//   DELETE /api/profiles/{id}/ship-music/{slotStem}/exclude    re-include slot
//   GET    /api/profiles/{id}/ship-music-add                   added-track list
//   POST   /api/profiles/{id}/ship-music-add                   add/replace track audio
//   DELETE /api/profiles/{id}/ship-music-add/{trackKey}        remove track

// File extensions the backend's AudioPreprocessor accepts.
const SHIPMUSIC_AUDIO_EXTS = ['.wav', '.mp3', '.ogg', '.flac', '.m4a', '.aac', '.opus'];

function shipmusicAcceptList() {
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

function shipmusicFormatBytes(n) {
    if (!n || n <= 0) return '0 B';
    if (n < 1024) return n + ' B';
    if (n < 1024 * 1024) return (n / 1024).toFixed(1) + ' KB';
    return (n / (1024 * 1024)).toFixed(1) + ' MB';
}

// Volume slider helpers. UI works in 0..200 (% of vanilla loudness),
// backend stores the multiplier as 0.0..2.0. We clamp+round here so
// the GUI never sends out a degenerate value like 1.0000001 (which
// would trigger an unnecessary cue extraction).
function shipmusicSliderPctFromMul(mul) {
    if (!isFinite(mul)) mul = 1.0;
    let p = Math.round(mul * 100);
    if (p < 0) p = 0;
    if (p > 200) p = 200;
    return p;
}
function shipmusicMulFromSliderPct(pct) {
    let p = parseInt(pct, 10);
    if (!isFinite(p)) p = 100;
    if (p < 0) p = 0;
    if (p > 200) p = 200;
    return p / 100;
}

// Debounce wrapper so dragging the slider does not spam the backend.
function shipmusicDebounce(fn, ms) {
    let timer = null;
    return function () {
        const args = arguments;
        if (timer) clearTimeout(timer);
        timer = setTimeout(() => { timer = null; fn.apply(null, args); }, ms);
    };
}

// Builds a labeled volume slider (5%-steps, 0..200%) that POSTs the
// effective multiplier to `url`. `initialMul` is the multiplier
// returned by the server (e.g. 1.0 for "vanilla", 0.8 for "added
// track default"). `onSaved` is called after a successful save with
// the new multiplier (so the caller can refresh local state if it
// wants to).
function buildShipMusicVolumeSlider(initialMul, url, onSaved) {
    const wrap = document.createElement('div');
    wrap.className = 'shipmusic-volume-row';

    const label = document.createElement('span');
    label.className = 'hint shipmusic-volume-label';
    label.textContent = 'Volume';
    wrap.appendChild(label);

    const slider = document.createElement('input');
    slider.type = 'range';
    slider.min = '0';
    slider.max = '200';
    slider.step = '5';
    slider.value = String(shipmusicSliderPctFromMul(initialMul));
    slider.className = 'shipmusic-volume-slider';
    wrap.appendChild(slider);

    const valueLbl = document.createElement('span');
    valueLbl.className = 'hint shipmusic-volume-value';
    valueLbl.textContent = slider.value + '%';
    wrap.appendChild(valueLbl);

    // PUT-on-pause: debounced 250ms so we don't hammer the endpoint
    // while the user drags. The slot card doesn't re-render on save
    // because the volume change doesn't affect any other UI element
    // (state, file name, badge); skipping the refresh keeps focus on
    // the slider so the user can keep tweaking.
    const saveDebounced = shipmusicDebounce(async (pct) => {
        const mul = shipmusicMulFromSliderPct(pct);
        try {
            const res = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ volume: mul }),
            });
            if (!res.ok) {
                let msg = 'HTTP ' + res.status;
                try {
                    const j = await res.json();
                    if (j && j.error) msg = j.error;
                } catch (_) { /* fall through */ }
                throw new Error(msg);
            }
            if (typeof onSaved === 'function') onSaved(mul);
        } catch (ex) {
            console.warn('volume save failed:', ex);
        }
    }, 250);

    slider.addEventListener('input', () => {
        valueLbl.textContent = slider.value + '%';
        saveDebounced(slider.value);
    });

    return wrap;
}

// Cache of the last GET response so cross-card guards (e.g. "exclude
// would leave zero active tracks") can read both slot and added-track
// counts without a refetch.
let shipmusicCache = { slots: [], added: [] };

// Refresh both cards. Called from applyShipMusicToUI() on profile load
// and after every state-mutating action (upload, reset, exclude, add,
// remove) to keep on-disk truth + UI in sync.
async function refreshShipMusicAll() {
    const id = shipmusicProfileId();
    if (!id) {
        const host = document.getElementById('shipmusic-slot-list');
        if (host) host.innerHTML = '<p class="hint">No profile loaded.</p>';
        const host2 = document.getElementById('shipmusic-added-list');
        if (host2) host2.innerHTML = '<p class="hint">No profile loaded.</p>';
        shipmusicCache = { slots: [], added: [] };
        return;
    }

    // Run both fetches in parallel - they hit different endpoints and
    // we want both rendered as soon as possible.
    let slotData, addedData;
    try {
        [slotData, addedData] = await Promise.all([
            api('GET', '/api/profiles/' + encodeURIComponent(id) + '/ship-music'),
            api('GET', '/api/profiles/' + encodeURIComponent(id) + '/ship-music-add'),
        ]);
    } catch (ex) {
        const host = document.getElementById('shipmusic-slot-list');
        if (host) {
            host.innerHTML = '<p class="hint" style="color: var(--accent);">'
                + 'Failed to load shanty data: ' + (ex && ex.message ? ex.message : ex)
                + '</p>';
        }
        return;
    }

    shipmusicCache = {
        slots: Array.isArray(slotData?.slots) ? slotData.slots : [],
        added: Array.isArray(addedData?.tracks) ? addedData.tracks : [],
    };
    renderShipMusicSlots();
    renderShipMusicAdded();
}

// Computes whether ONE more vanilla exclude would leave zero active
// tracks (active vanilla + added). If true, the Exclude button on
// every still-active slot is disabled with a tooltip - we never want
// the user to ship a DA with an empty Cues array (engine crashes).
function shipmusicCanExcludeMore() {
    const activeVanilla = shipmusicCache.slots.filter(s => !s.excluded).length;
    const added = shipmusicCache.added.length;
    // After ONE more exclude, would we still have >= 1 playable track?
    return (activeVanilla - 1) + added >= 1;
}

function renderShipMusicSlots() {
    const host = document.getElementById('shipmusic-slot-list');
    if (!host) return;
    if (shipmusicCache.slots.length === 0) {
        host.innerHTML = '<p class="hint">No shanty slots returned by the server.</p>';
        return;
    }
    host.innerHTML = '';
    for (const slot of shipmusicCache.slots) {
        host.appendChild(renderShipMusicSlot(slot));
    }
}

function renderShipMusicSlot(slot) {
    const row = document.createElement('div');
    row.className = 'shipmusic-slot' + (slot.excluded ? ' excluded' : '');
    row.dataset.stem = slot.stem;

    const titleLine = document.createElement('div');
    titleLine.className = 'shipmusic-slot-title';
    const titleSpan = document.createElement('strong');
    titleSpan.textContent = slot.title;
    titleLine.appendChild(titleSpan);

    const stateBadge = document.createElement('span');
    if (slot.excluded) {
        stateBadge.className = 'shipmusic-state shipmusic-state-excluded';
        stateBadge.textContent = 'Excluded';
    } else {
        stateBadge.className = 'shipmusic-state shipmusic-state-' + slot.state;
        if (slot.state === 'custom') {
            stateBadge.textContent = 'Custom';
        } else if (slot.state === 'broken') {
            stateBadge.textContent = 'WAV missing';
        } else {
            stateBadge.textContent = 'Vanilla';
        }
    }
    titleLine.appendChild(stateBadge);
    row.appendChild(titleLine);

    // Filename + size line (only when the slot is overridden).
    if (slot.state !== 'vanilla') {
        const meta = document.createElement('div');
        meta.className = 'shipmusic-slot-meta hint';
        const parts = [];
        if (slot.originalFilename) parts.push(slot.originalFilename);
        if (slot.wavBytes) parts.push('(' + shipmusicFormatBytes(slot.wavBytes) + ' wav)');
        if (slot.excluded) parts.push('(override kept, will resume on Include)');
        meta.textContent = parts.join(' ');
        row.appendChild(meta);
    }

    const controls = document.createElement('div');
    controls.className = 'shipmusic-slot-controls';

    // Hidden file input + visible "Browse..." button.
    const fileInput = document.createElement('input');
    fileInput.type = 'file';
    fileInput.accept = shipmusicAcceptList();
    fileInput.style.display = 'none';
    const browseBtn = document.createElement('button');
    browseBtn.type = 'button';
    browseBtn.className = 'btn';
    browseBtn.textContent = 'Browse...';
    browseBtn.disabled = !!slot.excluded;
    if (slot.excluded) browseBtn.title = 'Slot is excluded. Click Include first to upload an override.';

    fileInput.addEventListener('change', () => {
        const originalText = browseBtn.textContent;
        browseBtn.disabled = true;
        browseBtn.textContent = 'Uploading...';
        uploadShipMusicSlot(slot, fileInput.files).catch(ex => {
            alert('Upload failed: ' + (ex && ex.message ? ex.message : ex));
        }).finally(() => {
            fileInput.value = '';
            browseBtn.disabled = !!slot.excluded;
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
        resetBtn.disabled = !!slot.excluded;
        if (slot.excluded) resetBtn.title = 'Slot is excluded. Click Include first to manage the override.';
        resetBtn.addEventListener('click', () => {
            resetShipMusicSlot(slot).catch(ex => {
                alert('Reset failed: ' + (ex && ex.message ? ex.message : ex));
            });
        });
        controls.appendChild(resetBtn);
    }

    // Exclude / Include toggle. The Include button is always enabled
    // (re-enabling never violates the min-one-active safety); the
    // Exclude button is disabled when one more exclude would leave us
    // at zero active tracks.
    const excludeBtn = document.createElement('button');
    excludeBtn.type = 'button';
    excludeBtn.className = 'btn btn-secondary';
    if (slot.excluded) {
        excludeBtn.textContent = 'Include';
        excludeBtn.addEventListener('click', () => {
            includeShipMusicSlot(slot).catch(ex => {
                alert('Include failed: ' + (ex && ex.message ? ex.message : ex));
            });
        });
    } else {
        excludeBtn.textContent = 'Exclude';
        if (!shipmusicCanExcludeMore()) {
            excludeBtn.disabled = true;
            excludeBtn.title = 'Cannot exclude all - at least one track (vanilla or added) must stay in the rotation.';
        }
        excludeBtn.addEventListener('click', () => {
            excludeShipMusicSlot(slot).catch(ex => {
                alert('Exclude failed: ' + (ex && ex.message ? ex.message : ex));
            });
        });
    }
    controls.appendChild(excludeBtn);

    row.appendChild(controls);

    // Volume slider per vanilla slot. Active even when no override is
    // configured yet - the user can pre-tune the slot before uploading
    // audio. When the slider sits at 100% the build pipeline skips
    // cue patching entirely (only the SWAV is overridden, the cue
    // keeps vanilla VolumeMultiplier).
    const id = shipmusicProfileId();
    if (id) {
        const initialMul = typeof slot.volume === 'number' ? slot.volume : 1.0;
        const volRow = buildShipMusicVolumeSlider(
            initialMul,
            '/api/profiles/' + encodeURIComponent(id)
                + '/ship-music/' + encodeURIComponent(slot.stem) + '/volume',
            (mul) => { slot.volume = mul; }
        );
        row.appendChild(volRow);
    }

    return row;
}

async function uploadShipMusicSlot(slot, fileList) {
    const id = shipmusicProfileId();
    if (!id) {
        alert('No profile is loaded.');
        return;
    }
    if (!fileList || fileList.length === 0) return;

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
    form.append('audio', audio, audio.name);
    form.append('filename', audio.name);

    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music/' + encodeURIComponent(slot.stem);
    const res = await fetch(url, { method: 'POST', body: form });
    if (!res.ok) {
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
    await refreshShipMusicAll();
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
    await refreshShipMusicAll();
    await loadProfile(id);
}

async function excludeShipMusicSlot(slot) {
    const id = shipmusicProfileId();
    if (!id) return;
    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music/' + encodeURIComponent(slot.stem) + '/exclude';
    const res = await fetch(url, { method: 'POST' });
    if (!res.ok && res.status !== 204) {
        let msg = 'HTTP ' + res.status;
        try {
            const j = await res.json();
            if (j && j.error) msg = j.error;
        } catch (_) { /* fall through */ }
        throw new Error(msg);
    }
    await refreshShipMusicAll();
    await loadProfile(id);
}

async function includeShipMusicSlot(slot) {
    const id = shipmusicProfileId();
    if (!id) return;
    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music/' + encodeURIComponent(slot.stem) + '/exclude';
    const res = await fetch(url, { method: 'DELETE' });
    if (!res.ok && res.status !== 204) {
        const txt = await res.text().catch(() => '');
        throw new Error('HTTP ' + res.status + ' ' + txt);
    }
    await refreshShipMusicAll();
    await loadProfile(id);
}

// ---- Added Tracks ----

function renderShipMusicAdded() {
    const host = document.getElementById('shipmusic-added-list');
    if (!host) return;
    if (shipmusicCache.added.length === 0) {
        host.innerHTML = '<p class="hint">No added tracks yet. Use the form below to add your first.</p>';
        return;
    }
    host.innerHTML = '';
    for (const t of shipmusicCache.added) {
        host.appendChild(renderShipMusicAddedTrack(t));
    }
}

function renderShipMusicAddedTrack(track) {
    const row = document.createElement('div');
    row.className = 'shipmusic-added';
    row.dataset.trackKey = track.trackKey;

    const titleLine = document.createElement('div');
    titleLine.className = 'shipmusic-added-title';

    const idxBadge = document.createElement('span');
    idxBadge.className = 'shipmusic-added-index';
    idxBadge.textContent = '#' + (track.newIndex || '?');
    titleLine.appendChild(idxBadge);

    const titleSpan = document.createElement('strong');
    titleSpan.textContent = track.title || track.trackKey;
    titleLine.appendChild(titleSpan);

    if (track.title && track.title !== track.trackKey) {
        const keySpan = document.createElement('span');
        keySpan.className = 'shipmusic-added-meta';
        keySpan.textContent = '(' + track.trackKey + ')';
        titleLine.appendChild(keySpan);
    }

    const stateBadge = document.createElement('span');
    stateBadge.className = 'shipmusic-added-state shipmusic-added-state-' + track.state;
    stateBadge.textContent = track.state === 'ready' ? 'Ready' : 'WAV missing';
    titleLine.appendChild(stateBadge);
    row.appendChild(titleLine);

    const meta = document.createElement('div');
    meta.className = 'shipmusic-added-meta';
    const parts = [];
    if (track.originalFilename) parts.push(track.originalFilename);
    if (track.wavBytes) parts.push('(' + shipmusicFormatBytes(track.wavBytes) + ' wav)');
    if (parts.length > 0) {
        meta.textContent = parts.join(' ');
        row.appendChild(meta);
    }

    const controls = document.createElement('div');
    controls.className = 'shipmusic-added-controls';

    const replaceInput = document.createElement('input');
    replaceInput.type = 'file';
    replaceInput.accept = SHIPMUSIC_AUDIO_EXTS.join(',') + ',audio/*';
    replaceInput.style.display = 'none';
    const replaceBtn = document.createElement('button');
    replaceBtn.type = 'button';
    replaceBtn.className = 'btn';
    replaceBtn.textContent = 'Replace audio';

    replaceInput.addEventListener('change', () => {
        const original = replaceBtn.textContent;
        replaceBtn.disabled = true;
        replaceBtn.textContent = 'Uploading...';
        uploadShipMusicAddedTrack(track.trackKey, track.title || '', replaceInput.files).catch(ex => {
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
        deleteShipMusicAddedTrack(track.trackKey).catch(ex => {
            alert('Delete failed: ' + (ex && ex.message ? ex.message : ex));
        });
    });
    controls.appendChild(delBtn);

    row.appendChild(controls);

    // Volume slider per added track. Default is 0.8 (= 80% of vanilla
    // 0.45/0.5 loudness) so new uploads come in a touch quieter than
    // vanilla and don't surprise the listener on first add.
    const id = shipmusicProfileId();
    if (id) {
        const initialMul = typeof track.volume === 'number' ? track.volume : 0.8;
        const volRow = buildShipMusicVolumeSlider(
            initialMul,
            '/api/profiles/' + encodeURIComponent(id)
                + '/ship-music-add/' + encodeURIComponent(track.trackKey) + '/volume',
            (mul) => { track.volume = mul; }
        );
        row.appendChild(volRow);
    }

    return row;
}

async function uploadShipMusicAddedTrack(trackKey, title, fileList) {
    const id = shipmusicProfileId();
    if (!id) { alert('No profile is loaded.'); return; }
    if (!trackKey) { alert('TrackKey is required.'); return; }
    if (!fileList || fileList.length === 0) return;

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
    await refreshShipMusicAll();
    await loadProfile(id);
}

async function deleteShipMusicAddedTrack(trackKey) {
    const id = shipmusicProfileId();
    if (!id) return;
    const url = '/api/profiles/' + encodeURIComponent(id)
              + '/ship-music-add/' + encodeURIComponent(trackKey);
    const res = await fetch(url, { method: 'DELETE' });
    if (!res.ok && res.status !== 204) {
        const txt = await res.text().catch(() => '');
        throw new Error('HTTP ' + res.status + ' ' + txt);
    }
    await refreshShipMusicAll();
    await loadProfile(id);
}

// ---- Lifecycle hooks ----

// Called from applyProfileToUI() during profile load. Triggers a
// fetch+rerender of both vanilla slots and added tracks - on-disk
// audio-file state lives on the server and needs an HTTP roundtrip.
function applyShipMusicToUI() {
    refreshShipMusicAll().catch(ex => {
        console.warn('refreshShipMusicAll failed:', ex);
    });
}

// Bind the Add Track form's submit handler. Idempotent (re-runs of
// app-startup don't double-bind).
function bindShipMusicHandlers() {
    const form = document.getElementById('shipmusic-add-form');
    if (!form || form.dataset.bound === '1') return;
    form.dataset.bound = '1';
    form.addEventListener('submit', ev => {
        ev.preventDefault();
        const keyEl = document.getElementById('shipmusic-add-trackkey');
        const titleEl = document.getElementById('shipmusic-add-title');
        const audioEl = document.getElementById('shipmusic-add-audio');
        const statusEl = document.getElementById('shipmusic-add-status');
        const btn = document.getElementById('shipmusic-add-btn');
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
        uploadShipMusicAddedTrack(trackKey, title, files).then(() => {
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
