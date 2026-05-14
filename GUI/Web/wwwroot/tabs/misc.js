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
    document.getElementById('pickup-multiplier-value').innerHTML =
        mul.toFixed(1) + 'x<!--&times;-->';
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
    document.getElementById('minimap-multiplier-value').innerHTML = mul.toFixed(1) + 'x<!--&times;-->';
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
    document.getElementById('bonfire-multiplier-value').innerHTML = mul.toFixed(1) + 'x<!--&times;-->';
    document.getElementById('bonfire-radius-readout').textContent = (mul * 50).toFixed(0) + ' m'
    document.getElementById('bonfire-height-readout').textContent = (mul * 30).toFixed(0) + ' m'
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
    document.getElementById('building-stability-enabled').addEventListener('change',
        setBuildingStabilityFromUI);
    document.getElementById('nosmoke-campfire').addEventListener('change', setNoSmokeFromUI);
    document.getElementById('nosmoke-furnace').addEventListener('change',  setNoSmokeFromUI);
    document.getElementById('nosmoke-kiln').addEventListener('change',     setNoSmokeFromUI);
    document.getElementById('minimap-enabled').addEventListener('change', setMinimapRangeFromUI);
    document.getElementById('minimap-multiplier').addEventListener('input', setMinimapRangeFromUI);
    document.getElementById('bonfire-enabled').addEventListener('change', setBonfireRadiusFromUI);
    document.getElementById('bonfire-multiplier').addEventListener('input', setBonfireRadiusFromUI);
}
