'use strict';

// Registry of the ship motor-force curves. Must stay in sync with
// ShipSpeedPatcher.Curves in QuartermasterCore (C# side): same stems, same
// vanilla peak values. Each curve is a UCurveFloat whose key values we scale;
// vanillaMax is the curve's peak output (top-throttle thrust), shown in the
// per-row readout so the slider's effect is legible.
const SHIP_TYPES = ['ShallowBoat', 'Brig', 'Cutter', 'Frigate', 'Ketch'];

const SHIP_TYPE_LABELS = {
    ShallowBoat: 'Shallow Boat',
    Brig: 'Brig',
    Cutter: 'Cutter',
    Frigate: 'Frigate',
    Ketch: 'Ketch',
};

const SHIP_SPEED_CURVES = [
    // ShallowBoat
    { stem: 'CRV_ShallowBoatMotor',        ship: 'ShallowBoat', role: 'Player',           vanillaMax: 7 },
    { stem: 'CRV_ShallowBoatServiceMotor', ship: 'ShallowBoat', role: 'Service',          vanillaMax: 0.15 },

    // Brig
    { stem: 'CRV_BrigMotor',            ship: 'Brig', role: 'Player',           vanillaMax: 1950 },
    { stem: 'CRV_BrigMotor_BlackBeard', ship: 'Brig', role: 'BlackBeard (boss)',vanillaMax: 2300 },
    { stem: 'CRV_BrigMotor_Brethren',   ship: 'Brig', role: 'Brethren (faction)', vanillaMax: 1600 },
    { stem: 'CRV_BrigServiceMotor',     ship: 'Brig', role: 'Service',          vanillaMax: 450 },
    { stem: 'CRV_AI_BrigMotor',         ship: 'Brig', role: 'AI / Enemy',       vanillaMax: 3300 },
    { stem: 'CRV_AI_BrigServiceMotor',  ship: 'Brig', role: 'AI Service',       vanillaMax: 500 },

    // Cutter
    { stem: 'CRV_CutterMotor',           ship: 'Cutter', role: 'Player',     vanillaMax: 3000 },
    { stem: 'CRV_CutterServiceMotor',    ship: 'Cutter', role: 'Service',    vanillaMax: 200 },
    { stem: 'CRV_AI_CutterMotor',        ship: 'Cutter', role: 'AI / Enemy', vanillaMax: 2000 },
    { stem: 'CRV_AI_CutterServiceMotor', ship: 'Cutter', role: 'AI Service', vanillaMax: 200 },

    // Frigate
    { stem: 'CRV_FrigateMotor',            ship: 'Frigate', role: 'Player',           vanillaMax: 2150 },
    { stem: 'CRV_FrigateMotor_BlackBeard', ship: 'Frigate', role: 'BlackBeard (boss)',vanillaMax: 2600 },
    { stem: 'CRV_FrigateMotor_Brethren',   ship: 'Frigate', role: 'Brethren (faction)', vanillaMax: 1750 },
    { stem: 'CRV_FrigateServiceMotor',     ship: 'Frigate', role: 'Service',          vanillaMax: 1300 },
    { stem: 'CRV_AI_FrigateMotor',         ship: 'Frigate', role: 'AI / Enemy',       vanillaMax: 4300 },
    { stem: 'CRV_AI_FrigateServiceMotor',  ship: 'Frigate', role: 'AI Service',       vanillaMax: 1500 },

    // Ketch
    { stem: 'CRV_KetchMotor',            ship: 'Ketch', role: 'Player',           vanillaMax: 1150 },
    { stem: 'CRV_KetchMotor_BlackBeard', ship: 'Ketch', role: 'BlackBeard (boss)',vanillaMax: 1400 },
    { stem: 'CRV_KetchMotor_Brethren',   ship: 'Ketch', role: 'Brethren (faction)', vanillaMax: 900 },
    { stem: 'CRV_KetchServiceMotor',     ship: 'Ketch', role: 'Service',          vanillaMax: 240 },
    { stem: 'CRV_AI_KetchMotor',         ship: 'Ketch', role: 'AI / Enemy',       vanillaMax: 2100 },
    { stem: 'CRV_AI_KetchServiceMotor',  ship: 'Ketch', role: 'AI Service',       vanillaMax: 270 },
];

const SHIPSPEED_MIN = 0.1;
const SHIPSPEED_MAX = 10.0;
const SHIPSPEED_STEP = 0.1;

function fmtMul(m) {
    return m.toFixed(2).replace(/\.?0+$/, '') + 'x';
}

function fmtSpeed(v) {
    // Trim to at most 2 decimals, strip trailing zeros.
    return (Math.round(v * 100) / 100).toString();
}

function getShipSpeedState() {
    if (!state.current) return null;
    state.current.globals = state.current.globals || {};
    return state.current.globals.shipSpeed || null;
}

function getShipSpeedOverall() {
    const ss = getShipSpeedState();
    if (!ss) return 1.0;
    const m = ss.overallMultiplier;
    return (typeof m === 'number' && isFinite(m)) ? m : 1.0;
}

function getShipSpeedOverride(stem) {
    const ss = getShipSpeedState();
    if (!ss || !ss.overrides) return null;
    const m = ss.overrides[stem];
    return (typeof m === 'number' && isFinite(m)) ? m : null;
}

// Mirrors ResolveShipSpeedMultiplierFor in C#:
//   per-curve override != null AND != 1.0 -> override
//   otherwise -> overall
function resolveEffectiveShipSpeedMultiplier(stem) {
    const overall = getShipSpeedOverall();
    const override = getShipSpeedOverride(stem);
    if (override != null && Math.abs(override - 1.0) > 1e-9) return override;
    return overall;
}

function setShipSpeedOverall(mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const ss = state.current.globals.shipSpeed || {};
    const safeMul = (typeof mul === 'number' && isFinite(mul)) ? mul : 1.0;
    if (Math.abs(safeMul - 1.0) < 1e-9) {
        delete ss.overallMultiplier;
    } else {
        ss.overallMultiplier = safeMul;
    }
    pruneShipSpeedGlobal(ss);
    if (Object.keys(ss).length === 0) {
        delete state.current.globals.shipSpeed;
    } else {
        state.current.globals.shipSpeed = ss;
    }
    if (!(opts && opts.skipRefresh)) {
        syncShipSpeedOverallReadout();
        renderShipSpeedRows();
    }
    markDirty();
}

function setShipSpeedOverride(stem, mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const ss = state.current.globals.shipSpeed || {};
    const overrides = ss.overrides || {};
    const safeMul = (typeof mul === 'number' && isFinite(mul)) ? mul : 1.0;
    if (Math.abs(safeMul - 1.0) < 1e-9) {
        delete overrides[stem];
    } else {
        overrides[stem] = safeMul;
    }
    if (Object.keys(overrides).length === 0) {
        delete ss.overrides;
    } else {
        ss.overrides = overrides;
    }
    pruneShipSpeedGlobal(ss);
    if (Object.keys(ss).length === 0) {
        delete state.current.globals.shipSpeed;
    } else {
        state.current.globals.shipSpeed = ss;
    }
    if (!(opts && opts.skipRefresh)) {
        renderShipSpeedRows();
    }
    markDirty();
}

function pruneShipSpeedGlobal(ss) {
    if (!ss) return;
    if (ss.overallMultiplier != null) {
        const m = ss.overallMultiplier;
        if (!isFinite(m) || Math.abs(m - 1.0) < 1e-9) {
            delete ss.overallMultiplier;
        }
    }
    if (ss.overrides && Object.keys(ss.overrides).length === 0) {
        delete ss.overrides;
    }
}

function resetAllShipSpeedOverrides() {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const ss = state.current.globals.shipSpeed;
    if (!ss) return;
    delete ss.overrides;
    pruneShipSpeedGlobal(ss);
    if (Object.keys(ss).length === 0) {
        delete state.current.globals.shipSpeed;
    }
    syncShipSpeedOverallReadout();
    renderShipSpeedRows();
    syncShipSpeedMiscCard();
    markDirty();
}

function syncShipSpeedOverallReadout() {
    const slider = document.getElementById('shipspeed-overall-multiplier');
    const value = document.getElementById('shipspeed-overall-multiplier-value');
    if (!slider) return;
    const overall = getShipSpeedOverall();
    slider.value = overall;
    if (value) value.textContent = fmtMul(overall);
}

function renderShipSpeedRows() {
    const list = document.getElementById('shipspeed-list');
    if (!list) return;
    list.replaceChildren();

    for (const ship of SHIP_TYPES) {
        const curves = SHIP_SPEED_CURVES.filter(c => c.ship === ship);
        if (curves.length === 0) continue;

        const groupTitle = document.createElement('div');
        groupTitle.className = 'shipspeed-group-title';
        groupTitle.textContent = SHIP_TYPE_LABELS[ship] || ship;
        list.appendChild(groupTitle);

        for (const curve of curves) {
            const row = document.createElement('div');
            row.className = 'shipspeed-row';
            row.dataset.stem = curve.stem;

            const nameEl = document.createElement('div');
            nameEl.className = 'shipspeed-row-name';
            nameEl.textContent = curve.role;
            row.appendChild(nameEl);

            // Per-curve slider.
            const sliderWrap = document.createElement('div');
            sliderWrap.className = 'shipspeed-row-slider';
            const slider = document.createElement('input');
            slider.type = 'range';
            slider.min = String(SHIPSPEED_MIN);
            slider.max = String(SHIPSPEED_MAX);
            slider.step = String(SHIPSPEED_STEP);
            const override = getShipSpeedOverride(curve.stem);
            slider.value = override != null ? override : 1.0;
            sliderWrap.appendChild(slider);
            row.appendChild(sliderWrap);

            const multEl = document.createElement('span');
            multEl.className = 'shipspeed-row-mult';
            row.appendChild(multEl);

            const readoutEl = document.createElement('span');
            readoutEl.className = 'shipspeed-row-readout';
            row.appendChild(readoutEl);

            const resetBtn = document.createElement('button');
            resetBtn.type = 'button';
            resetBtn.className = 'shipspeed-row-reset';
            resetBtn.textContent = 'Reset';
            row.appendChild(resetBtn);

            const refresh = () => {
                const cur = parseFloat(slider.value);
                const safe = isFinite(cur) ? cur : 1.0;
                const isFollowing = Math.abs(safe - 1.0) < 1e-9;
                multEl.classList.toggle('is-following', isFollowing);
                multEl.textContent = fmtMul(safe);
                const effective = resolveEffectiveShipSpeedMultiplier(curve.stem);
                readoutEl.textContent =
                    fmtSpeed(curve.vanillaMax) + ' → '
                    + fmtSpeed(curve.vanillaMax * effective);
            };
            refresh();

            slider.addEventListener('input', () => {
                const mul = parseFloat(slider.value);
                setShipSpeedOverride(curve.stem, mul, { skipRefresh: true });
                refresh();
                syncShipSpeedMiscCard();
            });
            resetBtn.addEventListener('click', () => {
                slider.value = '1';
                setShipSpeedOverride(curve.stem, 1.0, { skipRefresh: true });
                refresh();
                syncShipSpeedMiscCard();
            });

            list.appendChild(row);
        }
    }
}

// Bridges the misc-tab compact slider to the same shipSpeed global so the two
// tabs stay in sync. Called from setShipSpeedFromMiscUI and whenever the Ship
// Speed tab's overall slider changes.
function syncShipSpeedMiscCard() {
    const slider = document.getElementById('shipspeed-multiplier');
    const value = document.getElementById('shipspeed-multiplier-value');
    const readout = document.getElementById('shipspeed-readout');
    const overall = getShipSpeedOverall();
    if (slider) slider.value = overall;
    if (value) value.textContent = fmtMul(overall);
    if (readout) {
        const overrides = (getShipSpeedState() && getShipSpeedState().overrides) || {};
        const n = Object.keys(overrides).length;
        readout.textContent = n === 0
            ? 'overall only'
            : (n + ' per-ship override' + (n === 1 ? '' : 's'));
    }
}

function setShipSpeedFromMiscUI() {
    const slider = document.getElementById('shipspeed-multiplier');
    if (!slider) return;
    const mul = parseFloat(slider.value);
    setShipSpeedOverall(mul);
    syncShipSpeedMiscCard();
}

function applyShipSpeedToUI() {
    syncShipSpeedOverallReadout();
    renderShipSpeedRows();
    syncShipSpeedMiscCard();
}

function bindShipSpeedHandlers() {
    const overall = document.getElementById('shipspeed-overall-multiplier');
    if (overall) {
        overall.addEventListener('input', () => {
            const mul = parseFloat(overall.value);
            setShipSpeedOverall(mul);
        });
    }
    const resetAll = document.getElementById('shipspeed-reset-all');
    if (resetAll) {
        resetAll.addEventListener('click', () => resetAllShipSpeedOverrides());
    }
    const miscSlider = document.getElementById('shipspeed-multiplier');
    if (miscSlider) {
        miscSlider.addEventListener('input', setShipSpeedFromMiscUI);
    }
}
