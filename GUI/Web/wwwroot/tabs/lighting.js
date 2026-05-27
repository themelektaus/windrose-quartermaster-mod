'use strict';

// Registry of supported light sources. Must stay in sync with
// LightingPatcher.Lights in QuartermasterCore (C# side). Vanilla
// AttenuationRadius values are in centimeters; the readout converts
// to meters for display.
// Display names reflect the SET of placed actors that actually use each
// light component (reverse-engineered from vanilla actor BP refs), not the
// component's own filename. For example BP_PointLight_Candelier is used by
// Signal Fire + wall/hook lamps; placed Chandeliers actually reference
// BP_PointLight_TorchFire. This mirrors what gets brighter when you move the
// slider, which is what the user cares about.
const LIGHT_SOURCES = [
    { stem: 'BP_PointLight_Candle',    name: 'Candle Lamp',              vanillaCm:  300, category: 'Lamp'    },
    { stem: 'BP_PointLight_Lantern',   name: 'Standing Lantern',         vanillaCm:  550, category: 'Lamp'    },
    { stem: 'BP_PointLight_Candelier', name: 'Wall Lamp + Signal Fire',  vanillaCm:  550, category: 'Lamp'    },
    { stem: 'BP_PointLight_TorchFire', name: 'Torch + Chandelier',       vanillaCm:  800, category: 'Fire'    },
    { stem: 'BP_PointLight_WildFire',  name: 'Building Center Fire',     vanillaCm: 1100, category: 'Fire'    },
    { stem: 'BP_BeltLanternLight',     name: 'Belt Lantern',             vanillaCm:  850, category: 'Carried' },
];

const LIGHTING_MIN = 0.1;
const LIGHTING_MAX = 10.0;
const LIGHTING_STEP = 0.1;

function getLightingState() {
    if (!state.current) return null;
    state.current.globals = state.current.globals || {};
    return state.current.globals.lighting || null;
}

function getLightingOverall() {
    const lg = getLightingState();
    if (!lg) return 1.0;
    const m = lg.overallMultiplier;
    return (typeof m === 'number' && isFinite(m)) ? m : 1.0;
}

function getLightingOverride(stem) {
    const lg = getLightingState();
    if (!lg || !lg.overrides) return null;
    const m = lg.overrides[stem];
    return (typeof m === 'number' && isFinite(m)) ? m : null;
}

// Returns the multiplier the build pipeline would use for the given light,
// applying the same precedence rule as ResolveLightingMultiplierFor in C#:
//   per-light override != null AND != 1.0 -> override
//   otherwise -> overall
function resolveEffectiveLightingMultiplier(stem) {
    const overall = getLightingOverall();
    const override = getLightingOverride(stem);
    if (override != null && Math.abs(override - 1.0) > 1e-9) return override;
    return overall;
}

function setLightingOverall(mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const lg = state.current.globals.lighting || {};
    const safeMul = (typeof mul === 'number' && isFinite(mul)) ? mul : 1.0;
    if (Math.abs(safeMul - 1.0) < 1e-9) {
        delete lg.overallMultiplier;
    } else {
        lg.overallMultiplier = safeMul;
    }
    pruneLightingGlobal(lg);
    if (Object.keys(lg).length === 0) {
        delete state.current.globals.lighting;
    } else {
        state.current.globals.lighting = lg;
    }
    if (!(opts && opts.skipRefresh)) {
        syncLightingOverallReadout();
        renderLightingRows();
    }
    markDirty();
}

function setLightingOverride(stem, mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const lg = state.current.globals.lighting || {};
    const overrides = lg.overrides || {};
    const safeMul = (typeof mul === 'number' && isFinite(mul)) ? mul : 1.0;
    if (Math.abs(safeMul - 1.0) < 1e-9) {
        delete overrides[stem];
    } else {
        overrides[stem] = safeMul;
    }
    if (Object.keys(overrides).length === 0) {
        delete lg.overrides;
    } else {
        lg.overrides = overrides;
    }
    pruneLightingGlobal(lg);
    if (Object.keys(lg).length === 0) {
        delete state.current.globals.lighting;
    } else {
        state.current.globals.lighting = lg;
    }
    if (!(opts && opts.skipRefresh)) {
        renderLightingRows();
    }
    markDirty();
}

function pruneLightingGlobal(lg) {
    if (!lg) return;
    if (lg.overallMultiplier != null) {
        const m = lg.overallMultiplier;
        if (!isFinite(m) || Math.abs(m - 1.0) < 1e-9) {
            delete lg.overallMultiplier;
        }
    }
    if (lg.overrides && Object.keys(lg.overrides).length === 0) {
        delete lg.overrides;
    }
}

function resetAllLightingOverrides() {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const lg = state.current.globals.lighting;
    if (!lg) return;
    delete lg.overrides;
    pruneLightingGlobal(lg);
    if (Object.keys(lg).length === 0) {
        delete state.current.globals.lighting;
    }
    syncLightingOverallReadout();
    renderLightingRows();
    syncLightingMiscCard();
    markDirty();
}

function syncLightingOverallReadout() {
    const slider = document.getElementById('lighting-overall-multiplier');
    const value = document.getElementById('lighting-overall-multiplier-value');
    if (!slider) return;
    const overall = getLightingOverall();
    slider.value = overall;
    if (value) {
        value.innerHTML = overall.toFixed(2).replace(/\.?0+$/, '') + 'x<!--&times;-->';
    }
}

function renderLightingRows() {
    const list = document.getElementById('lighting-list');
    if (!list) return;
    list.innerHTML = '';
    for (const light of LIGHT_SOURCES) {
        const row = document.createElement('div');
        row.className = 'lighting-row';
        row.dataset.stem = light.stem;

        const nameEl = document.createElement('div');
        nameEl.className = 'lighting-row-name';
        nameEl.innerHTML = esc(light.name)
            + '<span class="lighting-row-category">' + esc(light.category) + '</span>';
        row.appendChild(nameEl);

        // Per-light slider.
        const sliderWrap = document.createElement('div');
        sliderWrap.className = 'lighting-row-slider';
        const slider = document.createElement('input');
        slider.type = 'range';
        slider.min = String(LIGHTING_MIN);
        slider.max = String(LIGHTING_MAX);
        slider.step = String(LIGHTING_STEP);
        const override = getLightingOverride(light.stem);
        slider.value = override != null ? override : 1.0;
        sliderWrap.appendChild(slider);
        row.appendChild(sliderWrap);

        const multEl = document.createElement('span');
        multEl.className = 'lighting-row-mult';
        row.appendChild(multEl);

        const readoutEl = document.createElement('span');
        readoutEl.className = 'lighting-row-readout';
        row.appendChild(readoutEl);

        const resetBtn = document.createElement('button');
        resetBtn.type = 'button';
        resetBtn.className = 'lighting-row-reset';
        resetBtn.textContent = 'Reset';
        row.appendChild(resetBtn);

        const refresh = () => {
            const cur = parseFloat(slider.value);
            const safe = isFinite(cur) ? cur : 1.0;
            const isFollowing = Math.abs(safe - 1.0) < 1e-9;
            multEl.classList.toggle('is-following', isFollowing);
            multEl.innerHTML = safe.toFixed(2).replace(/\.?0+$/, '') + 'x<!--&times;-->';
            const effective = resolveEffectiveLightingMultiplier(light.stem);
            const effCm = light.vanillaCm * effective;
            const vanCm = light.vanillaCm;
            readoutEl.textContent =
                (vanCm / 100.0).toFixed(1) + 'm → '
                + (effCm / 100.0).toFixed(1) + 'm';
        };
        refresh();

        slider.addEventListener('input', () => {
            const mul = parseFloat(slider.value);
            setLightingOverride(light.stem, mul, { skipRefresh: true });
            refresh();
            syncLightingMiscCard();
        });
        resetBtn.addEventListener('click', () => {
            slider.value = '1';
            setLightingOverride(light.stem, 1.0, { skipRefresh: true });
            refresh();
            syncLightingMiscCard();
        });

        list.appendChild(row);
    }
}

// Bridges the misc-tab compact slider to the same lighting global so the
// two tabs stay in sync. Called from setLightingFromMiscUI and whenever
// the Lighting tab's overall slider changes.
function syncLightingMiscCard() {
    const slider = document.getElementById('lighting-multiplier');
    const value = document.getElementById('lighting-multiplier-value');
    const readout = document.getElementById('lighting-readout');
    const overall = getLightingOverall();
    if (slider) slider.value = overall;
    if (value) {
        value.innerHTML = overall.toFixed(2).replace(/\.?0+$/, '') + 'x<!--&times;-->';
    }
    if (readout) {
        const overrides = (getLightingState() && getLightingState().overrides) || {};
        const n = Object.keys(overrides).length;
        readout.textContent = n === 0
            ? 'overall only'
            : (n + ' per-light override' + (n === 1 ? '' : 's'));
    }
}

function setLightingFromMiscUI() {
    const slider = document.getElementById('lighting-multiplier');
    if (!slider) return;
    const mul = parseFloat(slider.value);
    setLightingOverall(mul);
    syncLightingMiscCard();
}

function applyLightingToUI() {
    syncLightingOverallReadout();
    renderLightingRows();
    syncLightingMiscCard();
}

function bindLightingHandlers() {
    const overall = document.getElementById('lighting-overall-multiplier');
    if (overall) {
        overall.addEventListener('input', () => {
            const mul = parseFloat(overall.value);
            setLightingOverall(mul);
        });
    }
    const resetAll = document.getElementById('lighting-reset-all');
    if (resetAll) {
        resetAll.addEventListener('click', () => resetAllLightingOverrides());
    }
    const miscSlider = document.getElementById('lighting-multiplier');
    if (miscSlider) {
        miscSlider.addEventListener('input', setLightingFromMiscUI);
    }
}
