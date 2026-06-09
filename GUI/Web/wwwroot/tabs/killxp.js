'use strict';

// "XP for Kills": flat XP granted per enemy kill via the dxgi DLL (qm_killxp) -
// NOT a pak feature, so it only drives the per-profile sidecar
// qm_killxp_onkill_<profile>.txt. Profile shape:
//   globals.killXp = {
//     defaultXp?: number,                // flat XP for unmatched enemies; 0/absent = vanilla
//     keywords?: { [keyword]: number }   // flat XP per keyword (case-insensitive substring
//                                         // of the killed pawn's UClass name; longest wins)
//   }
// The keyword catalog is generated from the vanilla pak (/api/kill-xp/catalog).
// A keyword absent from the map follows defaultXp; a keyword present (incl. 0) pins
// that enemy's XP. The Basic-tab card and this tab's sidebar both edit defaultXp.

const KILLXP_DEFAULT_MAX = 100;     // the Basic-card + sidebar default slider range
const KILLXP_KEYWORD_MAX = 100000;  // per-keyword numeric input clamp (DLL allows up to 1e6)

// Lazily-fetched catalog (array of {keyword, label, category, suggestedXp, matchesPawns}).
let killXpCatalog = null;
let killXpCatalogLoading = null;

const KILLXP_CATEGORY_ORDER =
    ['Wildlife', 'Undead', 'Blackbeard', 'Crew', 'Senkamati', 'Giant', 'Quest', 'Other'];

function getKillXpState() {
    if (!state.current) return null;
    state.current.globals = state.current.globals || {};
    return state.current.globals.killXp || null;
}

function getKillXpDefault() {
    const kx = getKillXpState();
    if (!kx) return 0;
    const v = kx.defaultXp;
    return (typeof v === 'number' && isFinite(v) && v > 0) ? Math.round(v) : 0;
}

// null = keyword not pinned (follows default); a number (incl. 0) = pinned.
function getKillXpKeyword(keyword) {
    const kx = getKillXpState();
    if (!kx || !kx.keywords) return null;
    const v = kx.keywords[keyword];
    return (typeof v === 'number' && isFinite(v) && v >= 0) ? Math.round(v) : null;
}

function pruneKillXpGlobal(kx) {
    if (!kx) return;
    if (kx.defaultXp == null || !isFinite(kx.defaultXp) || kx.defaultXp <= 0) delete kx.defaultXp;
    if (kx.keywords && Object.keys(kx.keywords).length === 0) delete kx.keywords;
}

function commitKillXpGlobal(kx) {
    pruneKillXpGlobal(kx);
    state.current.globals = state.current.globals || {};
    if (Object.keys(kx).length === 0) delete state.current.globals.killXp;
    else state.current.globals.killXp = kx;
}

function killXpClampInt(v, max) {
    if (!isFinite(v)) return 0;
    v = Math.round(v);
    if (v < 0) v = 0;
    if (v > max) v = max;
    return v;
}

function setKillXpDefault(val, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const kx = state.current.globals.killXp || {};
    const v = killXpClampInt(val, KILLXP_DEFAULT_MAX);
    if (v <= 0) delete kx.defaultXp; else kx.defaultXp = v;
    commitKillXpGlobal(kx);
    if (!(opts && opts.skipRefresh)) {
        syncKillXpDefaultReadouts();
        renderKillXpRows();   // per-row "follows default (N)" readouts depend on it
    }
    markDirty();
}

// val null/'' = clear (follow default); otherwise pin (0 allowed = explicit "no XP").
function setKillXpKeyword(keyword, val, opts) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const kx = state.current.globals.killXp || {};
    const keywords = kx.keywords || {};
    if (val == null || val === '' || (typeof val === 'number' && !isFinite(val))) {
        delete keywords[keyword];
    } else {
        keywords[keyword] = killXpClampInt(val, KILLXP_KEYWORD_MAX);
    }
    if (Object.keys(keywords).length === 0) delete kx.keywords;
    else kx.keywords = keywords;
    commitKillXpGlobal(kx);
    if (!(opts && opts.skipRefresh)) renderKillXpRows();
    markDirty();
}

function resetAllKillXpKeywords() {
    if (!state.current) return;
    const kx = state.current.globals && state.current.globals.killXp;
    if (!kx) return;
    delete kx.keywords;
    commitKillXpGlobal(kx);
    syncKillXpDefaultReadouts();
    renderKillXpRows();
    markDirty();
}

// Both the Basic-tab card (killxp-default) and the tab sidebar (killxp-overall)
// edit the same default, so keep both in sync.
function syncKillXpDefaultReadouts() {
    const def = getKillXpDefault();
    const text = def <= 0 ? 'off' : (def + ' XP');
    for (const id of ['killxp-default', 'killxp-overall']) {
        const slider = document.getElementById(id);
        if (slider) slider.value = def;
        const pill = document.getElementById(id + '-value');
        if (pill) pill.textContent = text;
    }
}

async function ensureKillXpCatalog() {
    if (killXpCatalog) return killXpCatalog;
    if (killXpCatalogLoading) return killXpCatalogLoading;
    killXpCatalogLoading = (async () => {
        try {
            const data = await api('GET', '/api/kill-xp/catalog');
            killXpCatalog = Array.isArray(data) ? data : [];
        } catch (err) {
            killXpCatalog = [];
            console.error('Kill XP catalog load failed:', err);
        }
        killXpCatalogLoading = null;
        return killXpCatalog;
    })();
    return killXpCatalogLoading;
}

function killXpRowMatchesFilter(entry, needle) {
    if (!needle) return true;
    const hay = (entry.label + ' ' + entry.keyword + ' ' + entry.category).toLowerCase();
    return hay.indexOf(needle) >= 0;
}

function renderKillXpRows() {
    const list = document.getElementById('killxp-list');
    if (!list) return;

    if (!killXpCatalog) {
        const loading = document.createElement('div');
        loading.className = 'killxp-empty';
        loading.textContent = 'Loading enemy catalog from the vanilla pak...';
        list.replaceChildren(loading);
        ensureKillXpCatalog().then(() => renderKillXpRows());
        return;
    }

    if (killXpCatalog.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'killxp-empty';
        empty.textContent = 'Enemy catalog unavailable (could not read the vanilla pak).';
        list.replaceChildren(empty);
        return;
    }

    const search = document.getElementById('killxp-search');
    const needle = search ? search.value.trim().toLowerCase() : '';

    const byCat = new Map();
    for (const e of killXpCatalog) {
        if (!killXpRowMatchesFilter(e, needle)) continue;
        if (!byCat.has(e.category)) byCat.set(e.category, []);
        byCat.get(e.category).push(e);
    }

    const cats = Array.from(byCat.keys()).sort((a, b) => {
        const ia = KILLXP_CATEGORY_ORDER.indexOf(a);
        const ib = KILLXP_CATEGORY_ORDER.indexOf(b);
        return (ia < 0 ? 999 : ia) - (ib < 0 ? 999 : ib);
    });

    if (cats.length === 0) {
        const empty = document.createElement('div');
        empty.className = 'killxp-empty';
        empty.textContent = 'No enemies match the filter.';
        list.replaceChildren(empty);
        return;
    }

    const frag = document.createDocumentFragment();
    for (const cat of cats) {
        const entries = byCat.get(cat);
        const title = document.createElement('div');
        title.className = 'killxp-cat-title';
        title.textContent = cat + ' (' + entries.length + ')';
        frag.appendChild(title);
        for (const entry of entries) frag.appendChild(buildKillXpRow(entry));
    }
    list.replaceChildren(frag);
}

function buildKillXpRow(entry) {
    const row = document.createElement('div');
    row.className = 'killxp-row';
    row.dataset.keyword = entry.keyword;

    const nameEl = document.createElement('div');
    nameEl.className = 'killxp-row-name';
    nameEl.textContent = entry.label;
    const matches = Array.isArray(entry.matchesPawns) ? entry.matchesPawns : [];
    nameEl.title = 'keyword "' + entry.keyword + '"'
        + (matches.length ? '\nmatches: ' + matches.join(', ') : '');
    row.appendChild(nameEl);

    const kwEl = document.createElement('code');
    kwEl.className = 'killxp-row-kw';
    kwEl.textContent = entry.keyword;
    row.appendChild(kwEl);

    const inputWrap = document.createElement('div');
    inputWrap.className = 'killxp-row-input';
    const input = document.createElement('input');
    input.type = 'number';
    input.min = '0';
    input.max = String(KILLXP_KEYWORD_MAX);
    input.step = '1';
    input.placeholder = 'default';
    const cur = getKillXpKeyword(entry.keyword);
    if (cur != null) input.value = String(cur);
    inputWrap.appendChild(input);
    row.appendChild(inputWrap);

    const readoutEl = document.createElement('span');
    readoutEl.className = 'killxp-row-readout';
    row.appendChild(readoutEl);

    const resetBtn = document.createElement('button');
    resetBtn.type = 'button';
    resetBtn.className = 'killxp-row-reset';
    resetBtn.textContent = 'Reset';
    row.appendChild(resetBtn);

    const refresh = () => {
        const ov = getKillXpKeyword(entry.keyword);
        if (ov == null) {
            const def = getKillXpDefault();
            readoutEl.textContent = 'follows default (' + (def <= 0 ? 'off' : def + ' XP') + ')';
            readoutEl.classList.add('is-following');
        } else {
            readoutEl.textContent = ov <= 0 ? 'no XP' : (ov + ' XP / kill');
            readoutEl.classList.remove('is-following');
        }
    };
    refresh();

    input.addEventListener('input', () => {
        const raw = input.value.trim();
        if (raw === '') setKillXpKeyword(entry.keyword, null, { skipRefresh: true });
        else setKillXpKeyword(entry.keyword, parseInt(raw, 10), { skipRefresh: true });
        refresh();
    });
    resetBtn.addEventListener('click', () => {
        input.value = '';
        setKillXpKeyword(entry.keyword, null, { skipRefresh: true });
        refresh();
    });

    return row;
}

function applyKillXpToUI() {
    syncKillXpDefaultReadouts();
    renderKillXpRows();
}

function bindKillXpHandlers() {
    const miscDefault = document.getElementById('killxp-default');
    if (miscDefault) {
        miscDefault.addEventListener('input', () => setKillXpDefault(parseInt(miscDefault.value, 10)));
    }
    const sideDefault = document.getElementById('killxp-overall');
    if (sideDefault) {
        sideDefault.addEventListener('input', () => setKillXpDefault(parseInt(sideDefault.value, 10)));
    }
    const resetAll = document.getElementById('killxp-reset-all');
    if (resetAll) {
        resetAll.addEventListener('click', () => resetAllKillXpKeywords());
    }
    const search = document.getElementById('killxp-search');
    if (search) {
        search.addEventListener('input', () => renderKillXpRows());
    }
}
