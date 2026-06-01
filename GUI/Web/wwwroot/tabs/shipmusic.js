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

function shipmusicHintParagraph(text) {
    const p = document.createElement('p');
    p.className = 'hint';
    p.textContent = text;
    return p;
}

// Mirrors a slider value into state.current so the next global Save
// (PUT /api/profiles/<id>) ships it to disk. Creates the Songs entry
// on the fly when the user pre-tunes a slot before uploading audio,
// matching the backend's on-demand creation of ShipMusicSlotOverride.
// Pairs with markDirty() in the slider's onChange so the user sees the
// UNSAVED badge and the Save button activates.
function syncShipMusicSlotVolumeIntoProfile(stem, mul) {
    if (!state.current) return;
    if (!state.current.globals) state.current.globals = {};
    if (!state.current.globals.shipMusic) state.current.globals.shipMusic = {};
    if (!state.current.globals.shipMusic.songs) state.current.globals.shipMusic.songs = {};
    const songs = state.current.globals.shipMusic.songs;
    const existing = songs[stem] || {};
    existing.volume = mul;
    songs[stem] = existing;
}

// Same pattern for added tracks - locate the track entry by key,
// update its volume, leave originalFilename / title alone.
function syncShipMusicAddedTrackVolumeIntoProfile(trackKey, mul) {
    if (!state.current) return;
    if (!state.current.globals) state.current.globals = {};
    if (!state.current.globals.shipMusicAdd) state.current.globals.shipMusicAdd = {};
    if (!Array.isArray(state.current.globals.shipMusicAdd.tracks)) {
        state.current.globals.shipMusicAdd.tracks = [];
    }
    const arr = state.current.globals.shipMusicAdd.tracks;
    for (let i = 0; i < arr.length; i++) {
        if (arr[i] && arr[i].trackKey === trackKey) {
            arr[i].volume = mul;
            return;
        }
    }
}

// Volume slider helpers. UI works in 0..100 (% absolute VolumeMultiplier),
// backend stores the absolute value as 0.0..1.0. We clamp+round here so
// the GUI never sends out a degenerate value like 0.4500001 (which
// would trigger an unnecessary cue extraction).
function shipmusicSliderPctFromMul(mul) {
    if (!isFinite(mul)) mul = 0.45;
    let p = Math.round(mul * 100);
    if (p < 0) p = 0;
    if (p > 100) p = 100;
    return p;
}
function shipmusicMulFromSliderPct(pct) {
    let p = parseInt(pct, 10);
    if (!isFinite(p)) p = 45;
    if (p < 0) p = 0;
    if (p > 100) p = 100;
    return p / 100;
}

// Builds a labeled volume slider (5%-steps, 0..100%). `initialMul` is the
// absolute value loaded from the server (e.g. 0.45 = "vanilla VoicePlayer
// baseline" for shanties). `onChange` is called on every slider move with
// the new absolute value - the caller is expected to mirror it into
// state.current + markDirty(). The value is NOT persisted here; it ships
// to the server with the next global Save (PUT /api/profiles/<id>), same
// as every other profile field, so a pending slider edit can be discarded
// via the global "Discard changes" flow without leaving a stale per-slot
// POST in the way.
//
// The `url` parameter is retained for ABI stability (callers still pass it)
// but ignored - the per-slot/per-track volume POST endpoints exist on the
// backend for API completeness but the GUI no longer calls them.
function buildShipMusicVolumeNode(initialMul, url, onChange) {
    const wrap = cloneTemplate('tpl-shipmusic-volume');

    const slider = wrap.querySelector('.shipmusic-volume-slider');
    slider.value = String(shipmusicSliderPctFromMul(initialMul));

    const valueLbl = wrap.querySelector('.shipmusic-volume-value');
    valueLbl.textContent = slider.value + '%';

    slider.addEventListener('input', () => {
        valueLbl.textContent = slider.value + '%';
        const mul = shipmusicMulFromSliderPct(slider.value);
        if (typeof onChange === 'function') onChange(mul);
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
        if (host) host.replaceChildren(shipmusicHintParagraph('No profile loaded.'));
        const host2 = document.getElementById('shipmusic-added-list');
        if (host2) host2.replaceChildren(shipmusicHintParagraph('No profile loaded.'));
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
            const p = shipmusicHintParagraph(
                'Failed to load shanty data: ' + (ex && ex.message ? ex.message : ex));
            p.style.color = 'var(--accent)';
            host.replaceChildren(p);
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
        host.replaceChildren(shipmusicHintParagraph('No shanty slots returned by the server.'));
        return;
    }
    host.replaceChildren();
    for (const slot of shipmusicCache.slots) {
        host.appendChild(buildShipMusicSlotNode(slot));
    }
}

function buildShipMusicSlotNode(slot) {
    const row = cloneTemplate('tpl-shipmusic-slot');
    if (slot.excluded) row.classList.add('excluded');
    row.dataset.stem = slot.stem;

    const titleLine = row.querySelector('.shipmusic-slot-title');
    titleLine.querySelector('.shipmusic-slot-name').textContent = slot.title;

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
    // audio. When the slider sits at 45% the build pipeline skips
    // cue patching entirely (only the SWAV is overridden, the cue
    // keeps vanilla VolumeMultiplier - 0.45 for VoicePlayer, 0.5 for
    // NoPlayer).
    const id = shipmusicProfileId();
    if (id) {
        const initialMul = typeof slot.volume === 'number' ? slot.volume : 0.45;
        const volRow = buildShipMusicVolumeNode(
            initialMul,
            '/api/profiles/' + encodeURIComponent(id)
                + '/ship-music/' + encodeURIComponent(slot.stem) + '/volume',
            (mul) => {
                slot.volume = mul;
                // Mirror into state.current so the next global Save
                // (PUT /api/profiles/<id>) ships the new value to disk,
                // and flip the dirty badge so the user knows it's
                // pending - the slider does not auto-save, the user
                // must hit Save (or Discard via the unsaved-changes
                // guard) like with any other profile field.
                syncShipMusicSlotVolumeIntoProfile(slot.stem, mul);
                if (typeof markDirty === 'function') markDirty();
            }
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
    // Server-side endpoint mutates the profile JSON directly and we then
    // reload it with loadProfile(), which would silently overwrite any
    // pending in-memory edits. Guard the user.
    if (!await confirmDiscardUnsavedChanges('uploading the override')) return;

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
    if (!await confirmDiscardUnsavedChanges('resetting the slot')) return;
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
    if (!await confirmDiscardUnsavedChanges('excluding the slot')) return;
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
    if (!await confirmDiscardUnsavedChanges('including the slot')) return;
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
        host.replaceChildren(shipmusicHintParagraph('No added tracks yet. Use the form below to add your first.'));
        return;
    }
    host.replaceChildren();
    for (const t of shipmusicCache.added) {
        host.appendChild(buildShipMusicAddedTrackNode(t));
    }
}

function buildShipMusicAddedTrackNode(track) {
    const row = cloneTemplate('tpl-shipmusic-added');
    row.dataset.trackKey = track.trackKey;

    const titleLine = row.querySelector('.shipmusic-added-title');
    titleLine.querySelector('.shipmusic-added-index').textContent = '#' + (track.newIndex || '?');
    titleLine.querySelector('.shipmusic-added-name').textContent = track.title || track.trackKey;

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
    delBtn.addEventListener('click', async () => {
        if (!await confirm('Remove track "' + (track.title || track.trackKey) + '"?')) return;
        deleteShipMusicAddedTrack(track.trackKey).catch(ex => {
            alert('Delete failed: ' + (ex && ex.message ? ex.message : ex));
        });
    });
    controls.appendChild(delBtn);

    row.appendChild(controls);

    // Volume slider per added track. Default is 0.45 (= absolute
    // VolumeMultiplier matching the vanilla VoicePlayer baseline) so
    // new uploads come in at parity with a typical vanilla shanty.
    const id = shipmusicProfileId();
    if (id) {
        const initialMul = typeof track.volume === 'number' ? track.volume : 0.45;
        const volRow = buildShipMusicVolumeNode(
            initialMul,
            '/api/profiles/' + encodeURIComponent(id)
                + '/ship-music-add/' + encodeURIComponent(track.trackKey) + '/volume',
            (mul) => {
                track.volume = mul;
                syncShipMusicAddedTrackVolumeIntoProfile(track.trackKey, mul);
                if (typeof markDirty === 'function') markDirty();
            }
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
    if (!await confirmDiscardUnsavedChanges('adding the track')) return;

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
    if (!await confirmDiscardUnsavedChanges('deleting the track')) return;
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
