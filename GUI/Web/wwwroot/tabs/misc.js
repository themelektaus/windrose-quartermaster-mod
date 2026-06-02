'use strict';

const STACK_SIZE_SETS = [
    { name: 'ssmode',      mult: 'ss-mult',   cap: 'ss-cap',   abs: 'ss-abs'   },
    { name: 'ssmode-misc', mult: 'm-ss-mult', cap: 'm-ss-cap', abs: 'm-ss-abs' },
];

function syncStackSizeUIFromState() {
    const ss = (state.current && state.current.globals && state.current.globals.stackSize) || {};
    const mode = ss.absolute != null ? 'absolute'
              : ss.multiplier != null ? 'multiplier'
              : 'none';
    for (const set of STACK_SIZE_SETS) {
        const radio = document.querySelector('input[name="' + set.name + '"][value="' + mode + '"]');
        if (radio) radio.checked = true;
        document.getElementById(set.mult).value = ss.multiplier == null ? 4   : ss.multiplier;
        document.getElementById(set.cap).value  = ss.cap        == null ? 0   : ss.cap;
        document.getElementById(set.abs).value  = ss.absolute   == null ? 999 : ss.absolute;
    }
}

function syncStackSizeInputsState() {
    const checked = document.querySelector('input[name="ssmode"]:checked');
    const mode = checked ? checked.value : 'none';
    for (const set of STACK_SIZE_SETS) {
        document.getElementById(set.mult).disabled = mode !== 'multiplier';
        document.getElementById(set.cap).disabled  = mode !== 'multiplier';
        document.getElementById(set.abs).disabled  = mode !== 'absolute';
        for (const r of document.querySelectorAll('input[name="' + set.name + '"]')) {
            r.disabled = false;
        }
    }
}

function syncPickupInputState() {
    const enabled = document.getElementById('pickup-enabled');
    const slider  = document.getElementById('pickup-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncPickupReadout() {
    const slider = document.getElementById('pickup-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('pickup-multiplier-value').textContent =
        mul.toFixed(1) + 'x';
    document.getElementById('pickup-range').textContent =
        (4.0 * mul).toFixed(1) + ' m';
}

function syncBellInputState() {
    document.getElementById('bell-cap').disabled = false;
    document.getElementById('signal-fire-cap').disabled = false;
}

function syncBuildingStabilityInputState() {
    document.getElementById('building-stability-enabled').disabled = false;
}

function syncNoSmokeInputState() {
    document.getElementById('nosmoke-campfire').disabled = false;
    document.getElementById('nosmoke-furnace').disabled  = false;
    document.getElementById('nosmoke-kiln').disabled     = false;
}

function syncMinimapInputState() {
    const enabled = document.getElementById('minimap-enabled');
    const slider  = document.getElementById('minimap-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncMinimapReadout() {
    const slider = document.getElementById('minimap-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('minimap-multiplier-value').textContent = mul.toFixed(1) + 'x';
    const footDist  = 25 * mul / 10;
    const shipDist  = 75 * mul / 10;
    document.getElementById('minimap-foot-readout').textContent = footDist.toFixed(1) + ' m';
    document.getElementById('minimap-ship-readout').textContent = shipDist.toFixed(1) + ' m';
}

function syncBonfireInputState() {
    const enabled = document.getElementById('bonfire-enabled');
    const slider  = document.getElementById('bonfire-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncBonfireReadout() {
    const slider = document.getElementById('bonfire-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('bonfire-multiplier-value').textContent = mul.toFixed(1) + 'x';
    document.getElementById('bonfire-radius-readout').textContent = (mul * 50).toFixed(0) + ' m'
    document.getElementById('bonfire-height-readout').textContent = (mul * 30).toFixed(0) + ' m'
}

function syncPickaxeInputState() {
    const enabled = document.getElementById('pickaxe-enabled');
    const slider  = document.getElementById('pickaxe-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncPickaxeReadout() {
    const slider = document.getElementById('pickaxe-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('pickaxe-multiplier-value').textContent = mul.toFixed(1) + 'x';
    const pct = (mul - 1.0) * 100.0;
    const sign = pct >= 0 ? '+' : '';
    document.getElementById('pickaxe-readout').textContent = sign + pct.toFixed(0) + '%';
}

function setStackSizeFromUI(srcEvt) {
    if (!state.current) return;
    let src = STACK_SIZE_SETS[0];
    if (srcEvt && srcEvt.target) {
        const t = srcEvt.target;
        const found = STACK_SIZE_SETS.find(s =>
            t.name === s.name || t.id === s.mult || t.id === s.cap || t.id === s.abs);
        if (found) src = found;
    }
    const checked = document.querySelector('input[name="' + src.name + '"]:checked');
    const mode = checked ? checked.value : 'none';
    const mult = parseInt(document.getElementById(src.mult).value, 10);
    const cap  = parseInt(document.getElementById(src.cap).value,  10);
    const abs  = parseInt(document.getElementById(src.abs).value,  10);

    state.current.globals = state.current.globals || {};
    if (mode === 'none') {
        state.current.globals.stackSize = null;
    } else if (mode === 'multiplier') {
        state.current.globals.stackSize = {
            multiplier: isFinite(mult) && mult >= 1 ? mult : 1,
            cap:        isFinite(cap)  && cap  > 0 ? cap  : null,
            absolute:   null,
        };
    } else {
        state.current.globals.stackSize = {
            multiplier: null, cap: null,
            absolute:   isFinite(abs) && abs >= 0 ? abs : 0,
        };
    }
    syncStackSizeUIFromState();
    syncStackSizeInputsState();
    markDirty();
    renderStatus();
    renderItems();
}

function setPickupRadiusFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('pickup-enabled').checked;
    const slider  = document.getElementById('pickup-multiplier');
    const mul     = parseFloat(slider.value) || 1.0;
    state.current.globals = state.current.globals || {};
    if (enabled && Math.abs(mul - 1.0) > 1e-9) {
        state.current.globals.pickupRadius = { multiplier: mul };
    } else {
        delete state.current.globals.pickupRadius;
    }
    syncPickupReadout();
    syncPickupInputState();
    markDirty();
}

function setBellLimitsFromUI() {
    if (!state.current) return;
    const bellRaw   = document.getElementById('bell-cap').value;
    const signalRaw = document.getElementById('signal-fire-cap').value;
    const bell   = parseInt(bellRaw,   10);
    const signal = parseInt(signalRaw, 10);
    if (!isFinite(bell) || !isFinite(signal)) return;

    state.current.globals = state.current.globals || {};
    const isVanillaBell   = bell === 10;
    const isVanillaSignal = signal === 3;
    if (isVanillaBell && isVanillaSignal) {
        delete state.current.globals.fastTravelBells;
    } else {
        state.current.globals.fastTravelBells = {
            bellCap: bell,
            signalFireCap: signal,
        };
    }
    markDirty();
}

function syncEquipmentSlotsReadout() {
    const ring = parseInt(document.getElementById('ring-slots').value, 10);
    const neck = parseInt(document.getElementById('necklace-slots').value, 10);
    document.getElementById('ring-slots-value').textContent = isFinite(ring) ? ring : 1;
    document.getElementById('necklace-slots-value').textContent = isFinite(neck) ? neck : 1;
}

function setEquipmentSlotsFromUI() {
    if (!state.current) return;
    const ring = parseInt(document.getElementById('ring-slots').value, 10);
    const neck = parseInt(document.getElementById('necklace-slots').value, 10);
    if (!isFinite(ring) || !isFinite(neck)) return;
    syncEquipmentSlotsReadout();
    state.current.globals = state.current.globals || {};
    if (ring === 1 && neck === 1) {
        delete state.current.globals.equipmentSlots;
    } else {
        state.current.globals.equipmentSlots = {
            ringSlots: ring,
            necklaceSlots: neck,
        };
    }
    markDirty();
}

// ---------------------------------------------------------------------------
// UI Scale. Unlike the other Misc sliders this is NOT profile-bound: it edits
// the per-user UE config directly (%LOCALAPPDATA%\R5\Saved\Config\Windows\
// Engine.ini -> [/Script/Engine.UserInterfaceSettings] ApplicationScale). So
// it never touches state.current / markDirty(); the slider mirrors the live
// file (GET on load) and an explicit Apply button writes it back (POST).
// ---------------------------------------------------------------------------
function syncUiScaleReadout() {
    const slider = document.getElementById('ui-scale');
    if (!slider) return;
    const v = parseFloat(slider.value);
    const pct = isFinite(v) ? Math.round(v * 100) : 100;
    document.getElementById('ui-scale-value').textContent = pct + '%';
}

function uiScaleSetStatus(msg) {
    const el = document.getElementById('ui-scale-status');
    if (el) el.textContent = msg || '';
}

async function loadUiScale() {
    const slider = document.getElementById('ui-scale');
    if (!slider) return;
    try {
        const res = await api('GET', '/api/uiscale');
        if (!res || !res.supported) {
            uiScaleSetStatus('No Windrose Engine.ini found yet - launch the game once.');
            syncUiScaleReadout();
            return;
        }
        let v = parseFloat(res.scale);
        if (!isFinite(v)) v = 1.0;
        if (v < 0.5) v = 0.5;
        if (v > 1.1) v = 1.1;
        slider.value = String(v);
        syncUiScaleReadout();
        uiScaleSetStatus(res.isSet
            ? 'Current: ' + Math.round(v * 100) + '%.'
            : 'Not set yet (vanilla 100%).');
    } catch (e) {
        uiScaleSetStatus('Could not read current UI scale: ' + e.message);
        syncUiScaleReadout();
    }
}

async function uiScaleApply() {
    const slider = document.getElementById('ui-scale');
    if (!slider) return;
    const v = parseFloat(slider.value) || 1.0;
    uiScaleSetStatus('Applying ' + Math.round(v * 100) + '%...');
    try {
        const res = await api('POST', '/api/uiscale', { scale: v });
        const pct = Math.round((res.scale != null ? res.scale : v) * 100);
        uiScaleSetStatus('UI scale set to ' + pct
            + '%. Close Windrose first if it is running, then launch to see it.');
    } catch (e) {
        uiScaleSetStatus('Apply failed: ' + e.message);
    }
}

function setBuildingStabilityFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('building-stability-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.buildingStability = { enabled: true };
    } else {
        delete state.current.globals.buildingStability;
    }
    markDirty();
}

function setMinimapRangeFromUI() {
    if (!state.current) return;
    syncMinimapReadout();
    syncMinimapInputState();
    const enabled = document.getElementById('minimap-enabled').checked;
    const mul = parseFloat(document.getElementById('minimap-multiplier').value);
    state.current.globals = state.current.globals || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete state.current.globals.minimapRange;
    } else {
        state.current.globals.minimapRange = { multiplier: mul };
    }
    markDirty();
}

function setBonfireRadiusFromUI() {
    if (!state.current) return;
    syncBonfireReadout();
    syncBonfireInputState();
    const enabled = document.getElementById('bonfire-enabled').checked;
    const mul = parseFloat(document.getElementById('bonfire-multiplier').value);
    state.current.globals = state.current.globals || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete state.current.globals.bonfireRadius;
    } else {
        state.current.globals.bonfireRadius = { multiplier: mul };
    }
    markDirty();
}

function setPickaxeRangeFromUI() {
    if (!state.current) return;
    syncPickaxeReadout();
    syncPickaxeInputState();
    const enabled = document.getElementById('pickaxe-enabled').checked;
    const mul = parseFloat(document.getElementById('pickaxe-multiplier').value);
    state.current.globals = state.current.globals || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete state.current.globals.pickaxeRange;
    } else {
        state.current.globals.pickaxeRange = { multiplier: mul };
    }
    markDirty();
}

function setNoSmokeFromUI() {
    if (!state.current) return;
    const c = document.getElementById('nosmoke-campfire').checked;
    const f = document.getElementById('nosmoke-furnace').checked;
    const k = document.getElementById('nosmoke-kiln').checked;
    state.current.globals = state.current.globals || {};
    if (!c && !f && !k) {
        delete state.current.globals.noSmoke;
    } else {
        const ns = {};
        if (c) ns.campfire = true;
        if (f) ns.furnace = true;
        if (k) ns.kiln = true;
        state.current.globals.noSmoke = ns;
    }
    markDirty();
}

// ---------------------------------------------------------------------------
// Bonfire / building-center hearth-music ("The Hearth") replacement.
//
// Single-slot SWAV swap. The card on the Misc tab carries:
//   - A file-input (multi-format accept) that POSTs to
//     /api/profiles/{id}/bonfire-music with the audio bytes.
//   - A status line that reflects the on-disk + profile state:
//       * "vanilla"   - no upload yet, vanilla "The Hearth" plays
//       * "ready"     - WAV on disk + filename in profile; build picks it up
//       * "broken"    - filename in profile but WAV missing on disk
//   - A "Clear" link that DELETEs the upload.
//
// Unlike the volume / multiplier cards on this tab, this one auto-saves
// the audio bytes server-side (the upload POST persists the WAV +
// updates BonfireMusicGlobal.OriginalFilename atomically). The status
// refresh is best-effort and never marks the profile dirty: the
// underlying bytes ARE the persistence, the in-memory state.current is
// just a mirror.
// ---------------------------------------------------------------------------
function bonfireMusicClearButton() {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn-link danger';
    btn.id = 'bonfire-music-clear';
    btn.textContent = 'Clear';
    return btn;
}

function buildBonfireMusicStatus(meta) {
    const frag = document.createDocumentFragment();
    const span = document.createElement('span');
    let withClear = true;
    if (!meta || meta.state === 'vanilla') {
        span.className = 'bonfire-music-vanilla';
        span.textContent = 'No custom audio - vanilla "The Hearth" plays.';
        withClear = false;
    } else if (meta.state === 'muted-vanilla') {
        // No upload, slider at 0%: the build synthesizes a silence SWAV.
        span.className = 'bonfire-music-muted';
        span.textContent = 'Vanilla "The Hearth" will be muted on next build (Volume 0%).';
    } else if (meta.state === 'broken') {
        span.className = 'bonfire-music-broken';
        span.textContent = 'Filename "' + (meta.originalFilename || '')
            + '" is set on the profile but its audio.wav is missing - re-upload or clear.';
    } else {
        let sizeNote = '';
        if (typeof meta.wavBytes === 'number' && meta.wavBytes > 0) {
            sizeNote = ' (' + Math.round(meta.wavBytes / 1024) + ' KB)';
        }
        span.className = 'bonfire-music-ready';
        span.textContent = 'Custom: ' + (meta.originalFilename || '') + sizeNote;
    }
    frag.appendChild(span);
    if (withClear) {
        frag.appendChild(document.createTextNode(' '));
        frag.appendChild(bonfireMusicClearButton());
    }
    return frag;
}

// Renders the status line + binds the Clear handler. Called from
// applyProfileToUI() on profile load and from upload/clear handlers
// after they mutate server-side.
function renderBonfireMusicStatus(meta) {
    const el = document.getElementById('bonfire-music-status');
    if (!el) return;
    el.replaceChildren(buildBonfireMusicStatus(meta));
    const clearBtn = document.getElementById('bonfire-music-clear');
    if (clearBtn) {
        clearBtn.addEventListener('click', clearBonfireMusic);
    }
}

// Pulls fresh meta from the server. Best-effort: a failure just leaves
// the existing status line in place (the user can re-upload to fix any
// stale rendering).
async function refreshBonfireMusicStatus() {
    if (!state.current || !state.current.id) {
        renderBonfireMusicStatus(null);
        return;
    }
    try {
        const meta = await api('GET',
            '/api/profiles/' + encodeURIComponent(state.current.id) + '/bonfire-music');
        renderBonfireMusicStatus(meta);
    } catch (_) {
        // Fall back to whatever the profile carries inline so the user
        // sees something rather than a blank card on a transient error.
        const bm = state.current.globals && state.current.globals.bonfireMusic;
        renderBonfireMusicStatus(bm
            ? { state: 'broken', originalFilename: bm.originalFilename }
            : null);
    }
}

async function onBonfireMusicFileChange(e) {
    const t = e.target;
    if (!t || t.tagName !== 'INPUT' || t.type !== 'file') return;
    if (!t.files || t.files.length === 0) return;
    const file = t.files[0];
    if (!state.current || !state.current.id) {
        await alert('Save the profile first so it has an id, then upload.');
        try { t.value = ''; } catch (_) {}
        return;
    }
    const statusEl = document.getElementById('bonfire-music-status');
    if (statusEl) {
        const em = document.createElement('em');
        em.textContent = 'Uploading + transcoding ' + file.name + '...';
        statusEl.replaceChildren(em);
    }
    try {
        const form = new FormData();
        form.append('audio', file, file.name);
        const url = '/api/profiles/' + encodeURIComponent(state.current.id) + '/bonfire-music';
        const resp = await fetch(url, { method: 'POST', body: form });
        if (!resp.ok) {
            let err = 'HTTP ' + resp.status;
            try { const j = await resp.json(); if (j && j.error) err = j.error; } catch (_) {}
            throw new Error(err);
        }
        const dto = await resp.json();
        // Mirror the server-side mutation into state.current so a
        // subsequent loadProfile() round-trip would be a no-op. The
        // upload itself is auto-saved, so we do NOT call markDirty().
        state.current.globals = state.current.globals || {};
        state.current.globals.bonfireMusic = {
            originalFilename: dto.originalFilename,
            volume: (state.current.globals.bonfireMusic
                  && state.current.globals.bonfireMusic.volume) || null,
        };
        renderBonfireMusicStatus({
            state: 'custom',
            originalFilename: dto.originalFilename,
            wavBytes: dto.wavBytes,
        });
    } catch (err) {
        await alert('Bonfire-music upload failed: ' + (err && err.message ? err.message : err));
        // Reload meta to recover the displayed state.
        refreshBonfireMusicStatus();
    } finally {
        try { t.value = ''; } catch (_) {}
    }
}

// Reads the current bonfire-music volume from state.current and parks
// it on the slider. Default 100% (= 1.0 absolute, vanilla baseline) when
// the profile carries no Volume yet. Range exposed to the UI: 0..100,
// where 0 = digital silence (mute, baked into the SWAV samples at build
// time) and 100 = unchanged. Called from applyProfileToUI() so a
// profile switch / load repaints the slider to its persisted value.
function syncBonfireMusicVolumeFromState() {
    const slider = document.getElementById('bonfire-music-volume');
    if (!slider) return;
    const bm = state.current && state.current.globals
        && state.current.globals.bonfireMusic;
    const v = (bm && typeof bm.volume === 'number') ? bm.volume : 1.0;
    let pct = Math.round(v * 100);
    if (!isFinite(pct) || pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    slider.value = String(pct);
    syncBonfireMusicVolumeReadout();
}

function syncBonfireMusicVolumeReadout() {
    const slider = document.getElementById('bonfire-music-volume');
    const out = document.getElementById('bonfire-music-volume-value');
    if (!slider || !out) return;
    out.textContent = (parseFloat(slider.value) || 0).toFixed(0) + '%';
}

// Slider onInput / onChange: mirror the new value into
// state.current.globals.bonfireMusic.volume + markDirty(). The value
// rides in the global PUT body alongside every other profile field and
// gets applied at build time as a pre-encode ffmpeg PCM gain. No POST
// here - we deliberately stay consistent with the ship-music sliders
// (Discard would otherwise have nothing to roll back to).
function setBonfireMusicVolumeFromUI() {
    if (!state.current) return;
    syncBonfireMusicVolumeReadout();
    const slider = document.getElementById('bonfire-music-volume');
    if (!slider) return;
    let pct = parseFloat(slider.value);
    if (!isFinite(pct) || pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    const mul = pct / 100.0;
    state.current.globals = state.current.globals || {};
    // Volume is meaningful even when no audio.wav is uploaded yet (it
    // would silence vanilla "The Hearth" once the user does upload),
    // so we materialize the BonfireMusic node when the slider moves
    // away from the default. At exactly 1.0 we leave the node null /
    // intact to keep "vanilla" profiles minimal in the JSON.
    if (Math.abs(mul - 1.0) < 1e-6) {
        if (state.current.globals.bonfireMusic) {
            state.current.globals.bonfireMusic.volume = null;
        }
    } else {
        if (!state.current.globals.bonfireMusic) {
            state.current.globals.bonfireMusic = {
                originalFilename: null,
                volume: mul,
            };
        } else {
            state.current.globals.bonfireMusic.volume = mul;
        }
    }
    // Re-render the status line client-side so the user sees the
    // muted-vanilla / vanilla transition immediately when dragging the
    // slider to / away from 0% (instead of having to Save first and
    // wait for the GET to return the new state). Mirrors the state
    // machine the server uses in /api/profiles/{id}/bonfire-music GET.
    refreshBonfireMusicStatusLocally();
    markDirty();
}

// Computes a status meta object from the in-memory state.current and
// renders it. Used after slider drags so the muted-vanilla / vanilla /
// custom transitions show up immediately without an HTTP round-trip.
function refreshBonfireMusicStatusLocally() {
    const bm = state.current && state.current.globals
        && state.current.globals.bonfireMusic;
    if (!bm) {
        renderBonfireMusicStatus({ state: 'vanilla' });
        return;
    }
    const hasFilename = !!bm.originalFilename;
    const vol = (typeof bm.volume === 'number') ? bm.volume : 1.0;
    if (hasFilename) {
        // Don't downgrade a "custom"-rendered card with a real upload
        // to "broken" just because we lack the wavBytes here - the next
        // server refresh will reconcile. Render as custom with no size.
        renderBonfireMusicStatus({
            state: 'custom',
            originalFilename: bm.originalFilename,
        });
        return;
    }
    if (vol <= 1e-4) {
        renderBonfireMusicStatus({ state: 'muted-vanilla' });
        return;
    }
    renderBonfireMusicStatus({ state: 'vanilla' });
}

async function clearBonfireMusic() {
    if (!state.current || !state.current.id) return;
    const ok = await confirm('Remove custom bonfire music and revert to vanilla "The Hearth"?');
    if (!ok) return;
    try {
        const url = '/api/profiles/' + encodeURIComponent(state.current.id) + '/bonfire-music';
        const resp = await fetch(url, { method: 'DELETE' });
        if (!resp.ok && resp.status !== 204) {
            let err = 'HTTP ' + resp.status;
            try { const j = await resp.json(); if (j && j.error) err = j.error; } catch (_) {}
            throw new Error(err);
        }
        if (state.current.globals) state.current.globals.bonfireMusic = null;
        renderBonfireMusicStatus(null);
    } catch (err) {
        await alert('Could not clear bonfire music: ' + (err && err.message ? err.message : err));
    }
}

function bindMiscHandlers() {
    for (const set of STACK_SIZE_SETS) {
        for (const r of document.querySelectorAll('input[name="' + set.name + '"]')) {
            r.addEventListener('change', setStackSizeFromUI);
        }
        document.getElementById(set.mult).addEventListener('input', setStackSizeFromUI);
        document.getElementById(set.cap).addEventListener('input',  setStackSizeFromUI);
        document.getElementById(set.abs).addEventListener('input',  setStackSizeFromUI);
    }
    document.getElementById('pickup-enabled').addEventListener('change', setPickupRadiusFromUI);
    document.getElementById('pickup-multiplier').addEventListener('input', setPickupRadiusFromUI);
    document.getElementById('bell-cap').addEventListener('input', setBellLimitsFromUI);
    document.getElementById('signal-fire-cap').addEventListener('input', setBellLimitsFromUI);
    document.getElementById('ring-slots').addEventListener('input', setEquipmentSlotsFromUI);
    document.getElementById('necklace-slots').addEventListener('input', setEquipmentSlotsFromUI);
    document.getElementById('building-stability-enabled').addEventListener('change',
        setBuildingStabilityFromUI);
    document.getElementById('nosmoke-campfire').addEventListener('change', setNoSmokeFromUI);
    document.getElementById('nosmoke-furnace').addEventListener('change',  setNoSmokeFromUI);
    document.getElementById('nosmoke-kiln').addEventListener('change',     setNoSmokeFromUI);
    document.getElementById('minimap-enabled').addEventListener('change', setMinimapRangeFromUI);
    document.getElementById('minimap-multiplier').addEventListener('input', setMinimapRangeFromUI);
    document.getElementById('bonfire-enabled').addEventListener('change', setBonfireRadiusFromUI);
    document.getElementById('bonfire-multiplier').addEventListener('input', setBonfireRadiusFromUI);
    document.getElementById('pickaxe-enabled').addEventListener('change', setPickaxeRangeFromUI);
    document.getElementById('pickaxe-multiplier').addEventListener('input', setPickaxeRangeFromUI);
    const bmInput = document.getElementById('bonfire-music-upload');
    if (bmInput) bmInput.addEventListener('change', onBonfireMusicFileChange);
    const bmVol = document.getElementById('bonfire-music-volume');
    if (bmVol) bmVol.addEventListener('input', setBonfireMusicVolumeFromUI);
    const uiScale = document.getElementById('ui-scale');
    if (uiScale) uiScale.addEventListener('input', syncUiScaleReadout);
    const uiScaleApplyBtn = document.getElementById('ui-scale-apply');
    if (uiScaleApplyBtn) uiScaleApplyBtn.addEventListener('click', uiScaleApply);
    // UI scale is install-global (not profile-bound): read the live value once.
    loadUiScale();
}
