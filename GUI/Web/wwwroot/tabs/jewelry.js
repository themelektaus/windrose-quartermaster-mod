'use strict';

// Registry of all 21 CurveTable rows in CT_JewelryGEValues. The `row` key is
// the exact row name in the cooked asset; it is also the key in the profile
// globals.jewelry.overrides dictionary.
const JEWELRY_STATS = [
    // Rings (15)
    { row: 'Ring_CritChance_StatValue',     name: 'Crit Chance',          category: 'Rings' },
    { row: 'Ring_CritDamage_StatValue',     name: 'Crit Damage',          category: 'Rings' },
    { row: 'Ring_MeleeDmg_StatValue',       name: 'Melee Damage',         category: 'Rings' },
    { row: 'Ring_RangeDmg_StatValue',       name: 'Ranged Damage',        category: 'Rings' },
    { row: 'Ring_SlashDmg_StatValue',       name: 'Slash Damage',         category: 'Rings' },
    { row: 'Ring_PierceDmg_StatValue',      name: 'Pierce Damage',        category: 'Rings' },
    { row: 'Ring_CrudeDmg_StatValue',       name: 'Blunt Damage',         category: 'Rings' },
    { row: 'Ring_DmgResist_StatValue',      name: 'Damage Resist',        category: 'Rings' },
    { row: 'Ring_CrptTickResist_StatValue', name: 'Corruption Resist',    category: 'Rings' },
    { row: 'Ring_FeintDmg_StatValue',       name: 'Feint Damage',         category: 'Rings' },
    { row: 'Ring_Fisherman_StatValue',      name: 'Fisherman',            category: 'Rings' },
    { row: 'Ring_Lightfoot_StatValue',      name: 'Lightfoot',            category: 'Rings' },
    { row: 'Ring_Posture_StatValue',        name: 'Posture',              category: 'Rings' },
    { row: 'Ring_ResStmnCons_StatValue',    name: 'Stamina Consumption',  category: 'Rings' },
    { row: 'Ring_WpnStmnCons_StatValue',    name: 'Weapon Stamina',       category: 'Rings' },
    // Necklaces (6)
    { row: 'Necklace_Strength_BaseStatValue',  name: 'Strength',  category: 'Necklaces' },
    { row: 'Necklace_Agility_BaseStatValue',   name: 'Agility',   category: 'Necklaces' },
    { row: 'Necklace_Endurance_BaseStatValue',  name: 'Endurance', category: 'Necklaces' },
    { row: 'Necklace_Precision_BaseStatValue',  name: 'Precision', category: 'Necklaces' },
    { row: 'Necklace_Vitality_BaseStatValue',   name: 'Vitality',  category: 'Necklaces' },
    { row: 'Necklace_Mastery_BaseStatValue',    name: 'Mastery',   category: 'Necklaces' },
];

const JEWELRY_MIN = 0.1;
const JEWELRY_MAX = 10.0;
const JEWELRY_STEP = 0.1;

// ---- State helpers (mirror the Lighting pattern) ----

function getJewelryState() {
    if (!state.current) return null;
    state.current.globals = state.current.globals || {};
    return state.current.globals.jewelry || null;
}

function getJewelryOverall() {
    const jw = getJewelryState();
    if (!jw) return 1.0;
    const m = jw.overallMultiplier;
    return (typeof m === 'number' && isFinite(m)) ? m : 1.0;
}

function getJewelryOverride(row) {
    const jw = getJewelryState();
    if (!jw || !jw.overrides) return null;
    const m = jw.overrides[row];
    return (typeof m === 'number' && isFinite(m)) ? m : null;
}

function resolveEffectiveJewelryMultiplier(row) {
    const overall = getJewelryOverall();
    const override = getJewelryOverride(row);
    if (override != null && Math.abs(override - 1.0) > 1e-9) return override;
    return overall;
}

function setJewelryOverall(mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const jw = state.current.globals.jewelry || {};
    const safeMul = (typeof mul === 'number' && isFinite(mul)) ? mul : 1.0;
    if (Math.abs(safeMul - 1.0) < 1e-9) {
        delete jw.overallMultiplier;
    } else {
        jw.overallMultiplier = safeMul;
    }
    pruneJewelryGlobal(jw);
    if (Object.keys(jw).length === 0) {
        delete state.current.globals.jewelry;
    } else {
        state.current.globals.jewelry = jw;
    }
    if (!(opts && opts.skipRefresh)) {
        syncJewelryOverallReadout();
        renderJewelryRows();
        syncJewelryMiscCard();
    }
    markDirty();
}

function setJewelryOverride(row, mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const jw = state.current.globals.jewelry || {};
    const safeMul = (typeof mul === 'number' && isFinite(mul)) ? mul : 1.0;
    if (!jw.overrides) jw.overrides = {};
    if (Math.abs(safeMul - 1.0) < 1e-9) {
        delete jw.overrides[row];
    } else {
        jw.overrides[row] = safeMul;
    }
    pruneJewelryGlobal(jw);
    if (Object.keys(jw).length === 0) {
        delete state.current.globals.jewelry;
    } else {
        state.current.globals.jewelry = jw;
    }
    if (!(opts && opts.skipRefresh)) {
        renderJewelryRows();
        syncJewelryMiscCard();
    }
    markDirty();
}

function pruneJewelryGlobal(jw) {
    if (jw.overrides && Object.keys(jw.overrides).length === 0) delete jw.overrides;
    if (jw.overallMultiplier != null && Math.abs(jw.overallMultiplier - 1.0) < 1e-9)
        delete jw.overallMultiplier;
}

// ---- UI rendering ----

function syncJewelryOverallReadout() {
    const slider = document.getElementById('jewelry-overall-multiplier');
    const value = document.getElementById('jewelry-overall-multiplier-value');
    if (!slider) return;
    const overall = getJewelryOverall();
    slider.value = overall;
    if (value) value.textContent = overall.toFixed(1) + 'x';
}

function renderJewelryRows() {
    const host = document.getElementById('jewelry-list');
    if (!host) return;
    host.replaceChildren();
    const categories = [];
    const byCat = {};
    for (const s of JEWELRY_STATS) {
        if (!byCat[s.category]) { byCat[s.category] = []; categories.push(s.category); }
        byCat[s.category].push(s);
    }
    for (const cat of categories) {
        const groupTitle = document.createElement('div');
        groupTitle.className = 'jewelry-group-title';
        groupTitle.textContent = cat;
        host.appendChild(groupTitle);

        for (const s of byCat[cat]) {
            const row = document.createElement('div');
            row.className = 'jewelry-row';
            row.dataset.row = s.row;

            const nameEl = document.createElement('div');
            nameEl.className = 'jewelry-row-name';
            nameEl.textContent = s.name;
            row.appendChild(nameEl);

            const sliderWrap = document.createElement('div');
            sliderWrap.className = 'jewelry-row-slider';
            const slider = document.createElement('input');
            slider.type = 'range';
            slider.min = String(JEWELRY_MIN);
            slider.max = String(JEWELRY_MAX);
            slider.step = String(JEWELRY_STEP);
            const override = getJewelryOverride(s.row);
            slider.value = override != null ? override : 1.0;
            sliderWrap.appendChild(slider);
            row.appendChild(sliderWrap);

            const multEl = document.createElement('span');
            multEl.className = 'jewelry-row-mult';
            row.appendChild(multEl);

            const readoutEl = document.createElement('span');
            readoutEl.className = 'jewelry-row-readout';
            row.appendChild(readoutEl);

            const resetBtn = document.createElement('button');
            resetBtn.type = 'button';
            resetBtn.className = 'jewelry-row-reset';
            resetBtn.textContent = 'Reset';
            row.appendChild(resetBtn);

            const refresh = () => {
                const cur = parseFloat(slider.value);
                const safe = isFinite(cur) ? cur : 1.0;
                const isFollowing = Math.abs(safe - 1.0) < 1e-9;
                multEl.classList.toggle('is-following', isFollowing);
                multEl.textContent = safe.toFixed(1) + 'x';
                const effective = resolveEffectiveJewelryMultiplier(s.row);
                readoutEl.textContent = 'effective: ' + effective.toFixed(1) + 'x';
            };
            refresh();

            slider.addEventListener('input', () => {
                const mul = parseFloat(slider.value);
                setJewelryOverride(s.row, mul, { skipRefresh: true });
                refresh();
                syncJewelryMiscCard();
            });
            resetBtn.addEventListener('click', () => {
                slider.value = '1';
                setJewelryOverride(s.row, 1.0, { skipRefresh: true });
                refresh();
                syncJewelryMiscCard();
            });

            host.appendChild(row);
        }
    }
}

// ---- Misc tab mirror card ----

function syncJewelryMiscCard() {
    const slider = document.getElementById('jewelry-misc-multiplier');
    if (!slider) return;
    const v = getJewelryOverall();
    slider.value = String(v);
    const el = document.getElementById('jewelry-misc-multiplier-value');
    if (el) el.textContent = v.toFixed(1) + 'x';
}

function setJewelryFromMisc() {
    const slider = document.getElementById('jewelry-misc-multiplier');
    if (!slider) return;
    const mul = parseFloat(slider.value);
    if (!isFinite(mul)) return;
    setJewelryOverall(mul);
    // Sync dedicated tab sidebar
    const overall = document.getElementById('jewelry-overall-multiplier');
    if (overall) overall.value = String(mul);
    syncJewelryOverallReadout();
}

// ---- Apply / Bind (called from app.js) ----

function applyJewelryToUI() {
    const overall = getJewelryOverall();
    const slider = document.getElementById('jewelry-overall-multiplier');
    if (slider) slider.value = String(overall);
    syncJewelryOverallReadout();
    renderJewelryRows();
    syncJewelryMiscCard();
}

function bindJewelryHandlers() {
    const overall = document.getElementById('jewelry-overall-multiplier');
    if (overall) {
        overall.addEventListener('input', () => {
            const mul = parseFloat(overall.value);
            setJewelryOverall(mul);
        });
    }
    const resetAll = document.getElementById('jewelry-reset-all');
    if (resetAll) {
        resetAll.addEventListener('click', function() {
            if (!state.current) return;
            state.current.globals = state.current.globals || {};
            const jw = state.current.globals.jewelry;
            if (jw) delete jw.overrides;
            renderJewelryRows();
            syncJewelryMiscCard();
            markDirty();
        });
    }
    const miscSlider = document.getElementById('jewelry-misc-multiplier');
    if (miscSlider) {
        miscSlider.addEventListener('input', function() {
            const el = document.getElementById('jewelry-misc-multiplier-value');
            if (el) el.textContent = (parseFloat(this.value) || 1).toFixed(1) + 'x';
            setJewelryFromMisc();
        });
    }
}

function jewelryTabHasMods() {
    const jw = getJewelryState();
    if (!jw) return false;
    if (typeof jw.overallMultiplier === 'number'
        && Math.abs(jw.overallMultiplier - 1.0) > 1e-9) return true;
    if (jw.overrides && Object.keys(jw.overrides).length > 0) return true;
    return false;
}
