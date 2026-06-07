'use strict';

// "Level Rewards": two hybrid multipliers (talent / stat points per level-up)
// plus per-level ABSOLUTE overrides, mirroring the C# LevelingPatcher. Profile
// shape:
//   globals.levelingRework = {
//     talentMultiplier?: number,   // 1.0 / absent = vanilla
//     statMultiplier?:   number,
//     overrides?: { [level]: { talent?: number, stat?: number } }
//   }
// Per-level rows come from /api/leveling/catalog (the vanilla DA_HeroLevels
// table). Resolution per level/dimension: an explicit override pins an exact
// reward, winning over the multiplier; otherwise the vanilla value is run
// through the hybrid multiplier (boosting ADDS the multiplier to the 0/1-point
// levels so they still grant points, and scales the rest). The level-1 /
// Exp==0 starting row is never modified.

const LEVELING_MIN = 1;
const LEVELING_MAX = 10;
const LEVELING_STEP = 1;

// Lazily-fetched per-level catalog (array of
// {level, exp, vanillaTalent, vanillaStat, isStarting}). null until loaded.
let levelingCatalog = null;
let levelingCatalogLoading = null;

function levFmtMul(m) {
    return (Number.isInteger(m) ? String(m) : m.toFixed(2).replace(/\.?0+$/, '')) + 'x';
}

// Mirrors LevelingPatcher.ApplyHybrid in C#. The effective value is non-negative
// here, so JS Math.round (half-up) matches C#'s MidpointRounding.AwayFromZero.
function applyHybridJs(vanilla, mul) {
    if (!(typeof mul === 'number' && isFinite(mul) && mul > 0)) return vanilla;
    if (Math.abs(mul - 1.0) < 1e-9) return vanilla;
    const effective = (mul > 1.0 && vanilla < 2) ? vanilla + mul : vanilla * mul;
    const v = Math.round(effective);
    return v < 0 ? 0 : v;
}

function getLevelingState() {
    if (!state.current) return null;
    state.current.globals = state.current.globals || {};
    return state.current.globals.levelingRework || null;
}

function getLevTalentOverall() {
    const lr = getLevelingState();
    if (!lr) return 1.0;
    const m = lr.talentMultiplier;
    return (typeof m === 'number' && isFinite(m) && m > 0) ? m : 1.0;
}

function getLevStatOverall() {
    const lr = getLevelingState();
    if (!lr) return 1.0;
    const m = lr.statMultiplier;
    return (typeof m === 'number' && isFinite(m) && m > 0) ? m : 1.0;
}

function getLevOverall(isStat) {
    return isStat ? getLevStatOverall() : getLevTalentOverall();
}

// Per-level override field ('talent' | 'stat'); null if unset.
function getLevelOverrideField(level, dim) {
    const lr = getLevelingState();
    if (!lr || !lr.overrides) return null;
    const entry = lr.overrides[String(level)];
    if (!entry) return null;
    const v = entry[dim];
    return (typeof v === 'number' && isFinite(v) && v >= 0) ? v : null;
}

function pruneLevelingGlobal(lr) {
    if (!lr) return;
    for (const key of ['talentMultiplier', 'statMultiplier']) {
        const m = lr[key];
        if (m == null) continue;
        if (!isFinite(m) || Math.abs(m - 1.0) < 1e-9) delete lr[key];
    }
    if (lr.overrides) {
        for (const k of Object.keys(lr.overrides)) {
            const e = lr.overrides[k];
            if (!e || (e.talent == null && e.stat == null)) delete lr.overrides[k];
        }
        if (Object.keys(lr.overrides).length === 0) delete lr.overrides;
    }
}

function commitLevelingGlobal(lr) {
    pruneLevelingGlobal(lr);
    state.current.globals = state.current.globals || {};
    if (Object.keys(lr).length === 0) {
        delete state.current.globals.levelingRework;
    } else {
        state.current.globals.levelingRework = lr;
    }
}

function setLevOverall(isStat, mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const lr = state.current.globals.levelingRework || {};
    const safe = (typeof mul === 'number' && isFinite(mul) && mul > 0) ? mul : 1.0;
    const key = isStat ? 'statMultiplier' : 'talentMultiplier';
    if (Math.abs(safe - 1.0) < 1e-9) delete lr[key];
    else lr[key] = safe;
    commitLevelingGlobal(lr);
    if (!(opts && opts.skipRefresh)) {
        syncLevOverallReadouts();
        renderLevelingRows();
        syncLevelingMiscCard();
    }
    markDirty();
}

// value: a non-negative integer to pin, or null to clear that dimension.
function setLevelOverride(level, dim, value, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const lr = state.current.globals.levelingRework || {};
    const overrides = lr.overrides || {};
    const key = String(level);
    const entry = overrides[key] || {};
    if (value == null) delete entry[dim];
    else entry[dim] = value;
    if (entry.talent == null && entry.stat == null) delete overrides[key];
    else overrides[key] = entry;
    if (Object.keys(overrides).length === 0) delete lr.overrides;
    else lr.overrides = overrides;
    commitLevelingGlobal(lr);
    if (!(opts && opts.skipRefresh)) renderLevelingRows();
    markDirty();
}

function resetLevelOverride(level) {
    if (!state.current) return;
    const lr = state.current.globals && state.current.globals.levelingRework;
    if (!lr || !lr.overrides) return;
    delete lr.overrides[String(level)];
    commitLevelingGlobal(lr);
    markDirty();
}

function resetAllLevelingOverrides() {
    if (!state.current) return;
    const lr = state.current.globals && state.current.globals.levelingRework;
    if (!lr) return;
    delete lr.overrides;
    commitLevelingGlobal(lr);
    syncLevOverallReadouts();
    renderLevelingRows();
    syncLevelingMiscCard();
    markDirty();
}

function syncLevOverallReadouts() {
    const t = getLevTalentOverall();
    const s = getLevStatOverall();
    const ts = document.getElementById('leveling-talent-overall');
    const tv = document.getElementById('leveling-talent-overall-value');
    const ss = document.getElementById('leveling-stat-overall');
    const sv = document.getElementById('leveling-stat-overall-value');
    if (ts) ts.value = t;
    if (tv) tv.textContent = levFmtMul(t);
    if (ss) ss.value = s;
    if (sv) sv.textContent = levFmtMul(s);
}

function levelOverrideCount() {
    const lr = getLevelingState();
    return lr && lr.overrides ? Object.keys(lr.overrides).length : 0;
}

// The Basic-tab compact card mirrors both overall sliders + an override count.
function syncLevelingMiscCard() {
    const t = getLevTalentOverall();
    const s = getLevStatOverall();
    const ts = document.getElementById('leveling-talent-multiplier');
    const tv = document.getElementById('leveling-talent-multiplier-value');
    const ss = document.getElementById('leveling-stat-multiplier');
    const sv = document.getElementById('leveling-stat-multiplier-value');
    const readout = document.getElementById('leveling-readout');
    if (ts) ts.value = t;
    if (tv) tv.textContent = levFmtMul(t);
    if (ss) ss.value = s;
    if (sv) sv.textContent = levFmtMul(s);
    if (readout) {
        const n = levelOverrideCount();
        readout.textContent = n === 0
            ? 'overall only'
            : (n + ' per-level override' + (n === 1 ? '' : 's'));
    }
}

function setLevelingFromMiscUI(isStat) {
    const id = isStat ? 'leveling-stat-multiplier' : 'leveling-talent-multiplier';
    const slider = document.getElementById(id);
    if (!slider) return;
    setLevOverall(isStat, parseFloat(slider.value));
}

async function ensureLevelingCatalog() {
    if (levelingCatalog) return levelingCatalog;
    if (levelingCatalogLoading) return levelingCatalogLoading;
    levelingCatalogLoading = (async () => {
        try {
            const data = await api('GET', '/api/leveling/catalog');
            levelingCatalog = Array.isArray(data) ? data : [];
        } catch (err) {
            levelingCatalog = [];
            console.error('Leveling catalog load failed:', err);
        }
        levelingCatalogLoading = null;
        return levelingCatalog;
    })();
    return levelingCatalogLoading;
}

function buildLevelingDimCell(entry, dim) {
    const vanilla = dim === 'talent' ? entry.vanillaTalent : entry.vanillaStat;
    const cell = document.createElement('div');
    cell.className = 'leveling-cell';

    const input = document.createElement('input');
    input.type = 'number';
    input.className = 'leveling-cell-input';
    input.min = '0';
    input.step = '1';
    input.inputMode = 'numeric';

    const readout = document.createElement('span');
    readout.className = 'leveling-cell-readout';

    const refresh = () => {
        if (entry.isStarting) {
            input.value = '';
            input.placeholder = String(vanilla);
            input.disabled = true;
            readout.classList.remove('is-pinned');
            readout.textContent = String(vanilla);
            return;
        }
        const ov = getLevelOverrideField(entry.level, dim);
        const hybrid = applyHybridJs(vanilla, getLevOverall(dim === 'stat'));
        input.placeholder = String(hybrid);
        input.value = ov != null ? String(ov) : '';
        const eff = ov != null ? ov : hybrid;
        readout.classList.toggle('is-pinned', ov != null);
        readout.textContent = vanilla + ' → ' + eff;
    };
    refresh();

    if (!entry.isStarting) {
        input.addEventListener('input', () => {
            const raw = input.value.trim();
            let val = null;
            if (raw !== '') {
                const n = parseInt(raw, 10);
                if (isFinite(n) && n >= 0) val = n;
            }
            setLevelOverride(entry.level, dim, val, { skipRefresh: true });
            refresh();
            syncLevelingMiscCard();
        });
    }

    cell.appendChild(input);
    cell.appendChild(readout);
    return { cell, refresh };
}

function buildLevelingRow(entry) {
    const row = document.createElement('div');
    row.className = 'leveling-row';
    if (entry.isStarting) row.classList.add('is-starting');
    row.dataset.level = entry.level;

    const lvlEl = document.createElement('span');
    lvlEl.className = 'leveling-row-level';
    lvlEl.textContent = 'Lv ' + entry.level;
    row.appendChild(lvlEl);

    const expEl = document.createElement('span');
    expEl.className = 'leveling-row-exp';
    expEl.textContent = entry.isStarting ? 'start' : entry.exp.toLocaleString() + ' XP';
    row.appendChild(expEl);

    const talent = buildLevelingDimCell(entry, 'talent');
    row.appendChild(talent.cell);
    const stat = buildLevelingDimCell(entry, 'stat');
    row.appendChild(stat.cell);

    const resetBtn = document.createElement('button');
    resetBtn.type = 'button';
    resetBtn.className = 'leveling-row-reset';
    resetBtn.textContent = 'Reset';
    if (entry.isStarting) {
        resetBtn.disabled = true;
    } else {
        resetBtn.addEventListener('click', () => {
            resetLevelOverride(entry.level);
            talent.refresh();
            stat.refresh();
            syncLevelingMiscCard();
        });
    }
    row.appendChild(resetBtn);

    return row;
}

function renderLevelingRows() {
    const list = document.getElementById('leveling-list');
    if (!list) return;

    if (!levelingCatalog) {
        list.replaceChildren();
        const loading = document.createElement('div');
        loading.className = 'leveling-empty';
        loading.textContent = 'Loading level table...';
        list.appendChild(loading);
        ensureLevelingCatalog().then(() => renderLevelingRows());
        return;
    }

    list.replaceChildren();

    if (levelingCatalog.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'leveling-empty';
        empty.textContent = 'Level table unavailable (run setup first).';
        list.appendChild(empty);
        return;
    }

    const head = document.createElement('div');
    head.className = 'leveling-row leveling-head';
    for (const text of ['Level', 'Exp', 'Talent (vanilla → effective)', 'Stat (vanilla → effective)', '']) {
        const c = document.createElement('span');
        c.textContent = text;
        head.appendChild(c);
    }
    list.appendChild(head);

    for (const entry of levelingCatalog) {
        list.appendChild(buildLevelingRow(entry));
    }
}

function applyLevelingToUI() {
    syncLevOverallReadouts();
    renderLevelingRows();
    syncLevelingMiscCard();
}

function bindLevelingHandlers() {
    const talentOverall = document.getElementById('leveling-talent-overall');
    if (talentOverall) {
        talentOverall.addEventListener('input', () => setLevOverall(false, parseFloat(talentOverall.value)));
    }
    const statOverall = document.getElementById('leveling-stat-overall');
    if (statOverall) {
        statOverall.addEventListener('input', () => setLevOverall(true, parseFloat(statOverall.value)));
    }
    const resetAll = document.getElementById('leveling-reset-all');
    if (resetAll) {
        resetAll.addEventListener('click', () => resetAllLevelingOverrides());
    }
    const miscTalent = document.getElementById('leveling-talent-multiplier');
    if (miscTalent) {
        miscTalent.addEventListener('input', () => setLevelingFromMiscUI(false));
    }
    const miscStat = document.getElementById('leveling-stat-multiplier');
    if (miscStat) {
        miscStat.addEventListener('input', () => setLevelingFromMiscUI(true));
    }
}
