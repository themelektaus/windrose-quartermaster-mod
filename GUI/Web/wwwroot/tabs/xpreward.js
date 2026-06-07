'use strict';

// "XP Reward Multiplier": two overall multipliers (Quest + POI chest) plus
// per-entry overrides, mirroring the C# XpRewardPatcher. Profile shape:
//   globals.xpReward = {
//     questMultiplier?: number,   // 1.0 / absent = vanilla
//     poiMultiplier?:   number,
//     overrides?: { [stem]: number }   // per-entry, keyed by DataAsset stem
//   }
// The per-entry override list is populated from /api/xp-rewards/catalog (the
// vanilla quest/POI rewards with their ExperienceCount). Resolution per entry:
// an override that differs from vanilla wins; otherwise the entry follows its
// dimension's overall (POI vs quest). A 1.0 value (overall or override) is
// vanilla = no-op (pruned on write).

const XPREWARD_MIN = 0.1;
const XPREWARD_MAX = 10.0;
const XPREWARD_STEP = 0.1;

// Lazily-fetched catalog of reward entries (array of
// {stem, isPoi, category, group, displayName, vanillaXp}). null until loaded.
let xpRewardCatalog = null;
let xpRewardCatalogLoading = null;

function xpFmtMul(m) {
    return m.toFixed(2).replace(/\.?0+$/, '') + 'x';
}

function xpFmtXp(v) {
    return String(Math.round(v));
}

function getXpRewardState() {
    if (!state.current) return null;
    state.current.globals = state.current.globals || {};
    return state.current.globals.xpReward || null;
}

function getXpQuestOverall() {
    const xp = getXpRewardState();
    if (!xp) return 1.0;
    const m = xp.questMultiplier;
    return (typeof m === 'number' && isFinite(m) && m > 0) ? m : 1.0;
}

function getXpPoiOverall() {
    const xp = getXpRewardState();
    if (!xp) return 1.0;
    const m = xp.poiMultiplier;
    return (typeof m === 'number' && isFinite(m) && m > 0) ? m : 1.0;
}

function getXpOverall(isPoi) {
    return isPoi ? getXpPoiOverall() : getXpQuestOverall();
}

function getXpOverride(stem) {
    const xp = getXpRewardState();
    if (!xp || !xp.overrides) return null;
    const m = xp.overrides[stem];
    return (typeof m === 'number' && isFinite(m) && m > 0) ? m : null;
}

// Mirrors ResolveEffective in C#: per-entry override (!= 1.0) wins, else the
// entry follows its dimension's overall.
function resolveEffectiveXp(stem, isPoi) {
    const override = getXpOverride(stem);
    if (override != null && Math.abs(override - 1.0) > 1e-9) return override;
    return getXpOverall(isPoi);
}

function pruneXpRewardGlobal(xp) {
    if (!xp) return;
    for (const key of ['questMultiplier', 'poiMultiplier']) {
        const m = xp[key];
        if (m == null) continue;
        if (!isFinite(m) || Math.abs(m - 1.0) < 1e-9) delete xp[key];
    }
    if (xp.overrides && Object.keys(xp.overrides).length === 0) delete xp.overrides;
}

function commitXpRewardGlobal(xp) {
    pruneXpRewardGlobal(xp);
    state.current.globals = state.current.globals || {};
    if (Object.keys(xp).length === 0) {
        delete state.current.globals.xpReward;
    } else {
        state.current.globals.xpReward = xp;
    }
}

function setXpOverall(isPoi, mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const xp = state.current.globals.xpReward || {};
    const safe = (typeof mul === 'number' && isFinite(mul) && mul > 0) ? mul : 1.0;
    const key = isPoi ? 'poiMultiplier' : 'questMultiplier';
    if (Math.abs(safe - 1.0) < 1e-9) delete xp[key];
    else xp[key] = safe;
    commitXpRewardGlobal(xp);
    if (!(opts && opts.skipRefresh)) {
        syncXpOverallReadouts();
        renderXpRewardRows();
        syncXpRewardMiscCard();
    }
    markDirty();
}

function setXpOverride(stem, mul, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const xp = state.current.globals.xpReward || {};
    const overrides = xp.overrides || {};
    const safe = (typeof mul === 'number' && isFinite(mul) && mul > 0) ? mul : 1.0;
    if (Math.abs(safe - 1.0) < 1e-9) delete overrides[stem];
    else overrides[stem] = safe;
    if (Object.keys(overrides).length === 0) delete xp.overrides;
    else xp.overrides = overrides;
    commitXpRewardGlobal(xp);
    if (!(opts && opts.skipRefresh)) renderXpRewardRows();
    markDirty();
}

function resetAllXpRewardOverrides() {
    if (!state.current) return;
    const xp = state.current.globals && state.current.globals.xpReward;
    if (!xp) return;
    delete xp.overrides;
    commitXpRewardGlobal(xp);
    syncXpOverallReadouts();
    renderXpRewardRows();
    syncXpRewardMiscCard();
    markDirty();
}

function syncXpOverallReadouts() {
    const q = getXpQuestOverall();
    const p = getXpPoiOverall();
    const qs = document.getElementById('xpreward-quest-overall');
    const qv = document.getElementById('xpreward-quest-overall-value');
    const ps = document.getElementById('xpreward-poi-overall');
    const pv = document.getElementById('xpreward-poi-overall-value');
    if (qs) qs.value = q;
    if (qv) qv.textContent = xpFmtMul(q);
    if (ps) ps.value = p;
    if (pv) pv.textContent = xpFmtMul(p);
}

function xpOverrideCount() {
    const xp = getXpRewardState();
    return xp && xp.overrides ? Object.keys(xp.overrides).length : 0;
}

// The Basic-tab compact card mirrors both overall sliders + an override count.
function syncXpRewardMiscCard() {
    const q = getXpQuestOverall();
    const p = getXpPoiOverall();
    const qs = document.getElementById('xpreward-quest-multiplier');
    const qv = document.getElementById('xpreward-quest-multiplier-value');
    const ps = document.getElementById('xpreward-poi-multiplier');
    const pv = document.getElementById('xpreward-poi-multiplier-value');
    const readout = document.getElementById('xpreward-readout');
    if (qs) qs.value = q;
    if (qv) qv.textContent = xpFmtMul(q);
    if (ps) ps.value = p;
    if (pv) pv.textContent = xpFmtMul(p);
    if (readout) {
        const n = xpOverrideCount();
        readout.textContent = n === 0
            ? 'overall only'
            : (n + ' per-entry override' + (n === 1 ? '' : 's'));
    }
}

function setXpRewardFromMiscUI(isPoi) {
    const id = isPoi ? 'xpreward-poi-multiplier' : 'xpreward-quest-multiplier';
    const slider = document.getElementById(id);
    if (!slider) return;
    setXpOverall(isPoi, parseFloat(slider.value));
    syncXpRewardMiscCard();
}

// Pretty category labels for the grouped section headers.
const XP_CATEGORY_LABELS = {
    POIChest: 'POI Treasure Chests',
    MainQuest: 'Main Quests',
    SideQuest: 'Side Quests',
    FactionQuests: 'Faction Quests',
    LocalEventQuests: 'Local Event Quests',
};

function xpCategoryLabel(cat) {
    return XP_CATEGORY_LABELS[cat] || cat;
}

async function ensureXpRewardCatalog() {
    if (xpRewardCatalog) return xpRewardCatalog;
    if (xpRewardCatalogLoading) return xpRewardCatalogLoading;
    xpRewardCatalogLoading = (async () => {
        try {
            const data = await api('GET', '/api/xp-rewards/catalog');
            xpRewardCatalog = Array.isArray(data) ? data : [];
        } catch (err) {
            xpRewardCatalog = [];
            console.error('XP reward catalog load failed:', err);
        }
        xpRewardCatalogLoading = null;
        return xpRewardCatalog;
    })();
    return xpRewardCatalogLoading;
}

function xpRowMatchesFilter(entry, needle) {
    if (!needle) return true;
    const hay = (entry.displayName + ' ' + entry.group + ' ' + entry.category + ' ' + entry.stem)
        .toLowerCase();
    return hay.indexOf(needle) >= 0;
}

function renderXpRewardRows() {
    const list = document.getElementById('xpreward-list');
    if (!list) return;

    if (!xpRewardCatalog) {
        list.replaceChildren();
        const loading = document.createElement('div');
        loading.className = 'xpreward-empty';
        loading.textContent = 'Loading reward catalog...';
        list.appendChild(loading);
        ensureXpRewardCatalog().then(() => renderXpRewardRows());
        return;
    }

    const search = document.getElementById('xpreward-search');
    const needle = search ? search.value.trim().toLowerCase() : '';

    list.replaceChildren();

    // Order: POI first, then quest categories; within a category, by group.
    const cats = [];
    const byCat = new Map();
    for (const e of xpRewardCatalog) {
        if (!xpRowMatchesFilter(e, needle)) continue;
        if (!byCat.has(e.category)) { byCat.set(e.category, []); cats.push(e.category); }
        byCat.get(e.category).push(e);
    }

    if (cats.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'xpreward-empty';
        empty.textContent = 'No entries match the filter.';
        list.appendChild(empty);
        return;
    }

    for (const cat of cats) {
        const entries = byCat.get(cat);
        const catTitle = document.createElement('div');
        catTitle.className = 'xpreward-cat-title';
        catTitle.textContent = xpCategoryLabel(cat) + ' (' + entries.length + ')';
        list.appendChild(catTitle);

        let lastGroup = null;
        for (const entry of entries) {
            if (entry.group && entry.group !== cat && entry.group !== lastGroup) {
                lastGroup = entry.group;
                const groupTitle = document.createElement('div');
                groupTitle.className = 'xpreward-group-title';
                groupTitle.textContent = entry.group;
                list.appendChild(groupTitle);
            }
            list.appendChild(buildXpRewardRow(entry));
        }
    }
}

function buildXpRewardRow(entry) {
    const row = document.createElement('div');
    row.className = 'xpreward-row';
    row.dataset.stem = entry.stem;

    const nameEl = document.createElement('div');
    nameEl.className = 'xpreward-row-name';
    nameEl.textContent = entry.displayName;
    nameEl.title = entry.stem;
    row.appendChild(nameEl);

    const sliderWrap = document.createElement('div');
    sliderWrap.className = 'xpreward-row-slider';
    const slider = document.createElement('input');
    slider.type = 'range';
    slider.min = String(XPREWARD_MIN);
    slider.max = String(XPREWARD_MAX);
    slider.step = String(XPREWARD_STEP);
    const override = getXpOverride(entry.stem);
    slider.value = override != null ? override : 1.0;
    sliderWrap.appendChild(slider);
    row.appendChild(sliderWrap);

    const multEl = document.createElement('span');
    multEl.className = 'xpreward-row-mult';
    row.appendChild(multEl);

    const readoutEl = document.createElement('span');
    readoutEl.className = 'xpreward-row-readout';
    row.appendChild(readoutEl);

    const resetBtn = document.createElement('button');
    resetBtn.type = 'button';
    resetBtn.className = 'xpreward-row-reset';
    resetBtn.textContent = 'Reset';
    row.appendChild(resetBtn);

    const refresh = () => {
        const cur = parseFloat(slider.value);
        const safe = isFinite(cur) ? cur : 1.0;
        const isFollowing = Math.abs(safe - 1.0) < 1e-9;
        multEl.classList.toggle('is-following', isFollowing);
        multEl.textContent = xpFmtMul(safe);
        const effective = resolveEffectiveXp(entry.stem, entry.isPoi);
        readoutEl.textContent =
            xpFmtXp(entry.vanillaXp) + ' → ' + xpFmtXp(entry.vanillaXp * effective);
    };
    refresh();

    slider.addEventListener('input', () => {
        setXpOverride(entry.stem, parseFloat(slider.value), { skipRefresh: true });
        refresh();
        syncXpRewardMiscCard();
    });
    resetBtn.addEventListener('click', () => {
        slider.value = '1';
        setXpOverride(entry.stem, 1.0, { skipRefresh: true });
        refresh();
        syncXpRewardMiscCard();
    });

    return row;
}

function applyXpRewardToUI() {
    syncXpOverallReadouts();
    renderXpRewardRows();
    syncXpRewardMiscCard();
}

function bindXpRewardHandlers() {
    const questOverall = document.getElementById('xpreward-quest-overall');
    if (questOverall) {
        questOverall.addEventListener('input', () => setXpOverall(false, parseFloat(questOverall.value)));
    }
    const poiOverall = document.getElementById('xpreward-poi-overall');
    if (poiOverall) {
        poiOverall.addEventListener('input', () => setXpOverall(true, parseFloat(poiOverall.value)));
    }
    const resetAll = document.getElementById('xpreward-reset-all');
    if (resetAll) {
        resetAll.addEventListener('click', () => resetAllXpRewardOverrides());
    }
    const search = document.getElementById('xpreward-search');
    if (search) {
        search.addEventListener('input', () => renderXpRewardRows());
    }
    const miscQuest = document.getElementById('xpreward-quest-multiplier');
    if (miscQuest) {
        miscQuest.addEventListener('input', () => setXpRewardFromMiscUI(false));
    }
    const miscPoi = document.getElementById('xpreward-poi-multiplier');
    if (miscPoi) {
        miscPoi.addEventListener('input', () => setXpRewardFromMiscUI(true));
    }
}
