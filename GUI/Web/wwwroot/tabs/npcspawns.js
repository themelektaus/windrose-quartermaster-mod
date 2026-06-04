'use strict';

// Vanilla "standard" respawn ceiling in minutes (7200s). Spawners above this are
// boss / rare timers and the global respawn multiplier skips them unless opted in.
const NPC_STANDARD_MAX_MIN = 120;

function npcGlobal() {
    const g = state.current && state.current.globals;
    return (g && g.npcSpawn) || null;
}

function npcGlobalActive() {
    const ns = npcGlobal();
    return !!(ns && ns.enabled);
}

function npcRespawnMult() {
    const ns = npcGlobal();
    const v = ns && typeof ns.respawnMultiplier === 'number' ? ns.respawnMultiplier : 1.0;
    return v > 0 ? v : 1.0;
}

function npcCountMult() {
    const ns = npcGlobal();
    const v = ns && typeof ns.countMultiplier === 'number' ? ns.countMultiplier : 1.0;
    return v > 0 ? v : 1.0;
}

function fmtMult(v) {
    return (Math.round(v * 100) / 100) + 'x';
}

// ---- effective (post-global) vanilla-derived values, used as input placeholders ----
function npcEffRespawnMin(s) {
    if (!s.hasRespawn) return null;
    const ns = npcGlobal();
    if (npcGlobalActive() && s.kind === 'npc') {
        const m = npcRespawnMult();
        const includeSpecial = !!(ns && ns.includeSpecialTimers);
        if (Math.abs(m - 1.0) > 1e-9 && (includeSpecial || s.respawnMinutes <= NPC_STANDARD_MAX_MIN)) {
            return Math.max(s.respawnMinutes > 0 ? 1 : 0, Math.round(s.respawnMinutes * m));
        }
    }
    return s.respawnMinutes;
}

function npcEffCount(s, vanillaVal) {
    // Unique NPCs (named characters / town citizens) are exempt from the count
    // multiplier - doubling would spawn two innkeepers / two of a merchant.
    if (npcGlobalActive() && s.kind === 'npc' && !s.isUniqueNpc) {
        const m = npcCountMult();
        if (Math.abs(m - 1.0) > 1e-9) {
            return Math.max(vanillaVal > 0 ? 1 : 0, Math.round(vanillaVal * m));
        }
    }
    return vanillaVal;
}

// ---- global card ----
// The NPC global is driven from two places: the NPC Spawns tab's own card and a
// mirror card on the Misc tab (m-* ids). Both share the same state, so every
// render / setter iterates over these id-sets and the cards stay in sync.
const NPC_GLOBAL_CARDS = [
    {
        enabled: 'npc-enabled', body: 'npc-global-body',
        respawn: 'npc-respawn-mult', respawnVal: 'npc-respawn-mult-value', respawnRead: 'npc-respawn-readout',
        count: 'npc-count-mult', countVal: 'npc-count-mult-value',
        special: 'npc-include-special',
    },
    {
        enabled: 'm-npc-enabled', body: 'm-npc-global-body',
        respawn: 'm-npc-respawn-mult', respawnVal: 'm-npc-respawn-mult-value', respawnRead: 'm-npc-respawn-readout',
        count: 'm-npc-count-mult', countVal: 'm-npc-count-mult-value',
        special: 'm-npc-include-special',
    },
];

// First existing slider value for a given multiplier (cards mirror each other,
// so any present slider holds the same value). Falls back to 1.0.
function npcFirstSliderValue(which) {
    for (const c of NPC_GLOBAL_CARDS) {
        const el = document.getElementById(which === 'respawn' ? c.respawn : c.count);
        if (el) { const v = parseFloat(el.value); if (isFinite(v) && v > 0) return v; }
    }
    return 1.0;
}

function renderNpcGlobals() {
    const enabled = npcGlobalActive();
    const rm = npcRespawnMult();
    const cm = npcCountMult();
    const ns = npcGlobal();
    const special = !!(ns && ns.includeSpecialTimers);

    for (const c of NPC_GLOBAL_CARDS) {
        const en = document.getElementById(c.enabled);
        if (en) en.checked = enabled;
        const body = document.getElementById(c.body);
        if (body) body.classList.toggle('disabled', !enabled);
        const rmSlider = document.getElementById(c.respawn);
        if (rmSlider) rmSlider.value = rm;
        const cmSlider = document.getElementById(c.count);
        if (cmSlider) cmSlider.value = cm;
        const rmVal = document.getElementById(c.respawnVal);
        if (rmVal) rmVal.textContent = fmtMult(rm);
        const cmVal = document.getElementById(c.countVal);
        if (cmVal) cmVal.textContent = fmtMult(cm);
        const rmRead = document.getElementById(c.respawnRead);
        if (rmRead) rmRead.textContent = Math.round(NPC_STANDARD_MAX_MIN * rm) + ' min';
        const sp = document.getElementById(c.special);
        if (sp) sp.checked = special;
    }
}

function renderNpcStatus() {
    let npc = 0, other = 0;
    for (const s of state.npcSpawners) {
        if (s.kind === 'npc') npc++; else other++;
    }
    const ovr = (state.current && state.current.npcSpawnOverrides)
        ? Object.keys(state.current.npcSpawnOverrides).length : 0;

    let modified = 0;
    for (const s of state.npcSpawners) if (npcSpawnerChanged(s)) modified++;

    setText('npc-stat-npc', npc);
    setText('npc-stat-other', other);
    setText('npc-stat-overrides', ovr);
    setText('npc-stat-modified', modified);
}

function setText(id, v) { const el = document.getElementById(id); if (el) el.textContent = v; }

// A spawner is "changed" if it has an override, or the active NPC-only global
// would move any of its values away from vanilla.
function npcSpawnerChanged(s) {
    const o = state.current && state.current.npcSpawnOverrides && state.current.npcSpawnOverrides[s.id];
    if (o && (o.respawnMinutes != null || o.countMin != null || o.countMax != null)) return true;
    if (!npcGlobalActive() || s.kind !== 'npc') return false;
    if (s.hasRespawn && npcEffRespawnMin(s) !== s.respawnMinutes) return true;
    if (npcEffCount(s, s.countMin) !== s.countMin) return true;
    if (npcEffCount(s, s.countMax) !== s.countMax) return true;
    return false;
}

// ---- list ----
function filterNpcSpawners() {
    const q = (document.getElementById('npc-filter').value || '').trim().toLowerCase();
    const cat = document.getElementById('npc-filter-category').value || '';
    const kind = document.getElementById('npc-filter-kind').value || 'npc';
    const changed = document.getElementById('npc-filter-changed').value || 'all';

    return state.npcSpawners.filter(s => {
        if (kind !== 'all' && s.kind !== kind) return false;
        if (cat && s.category !== cat) return false;
        if (changed === 'overridden') {
            const o = state.current && state.current.npcSpawnOverrides && state.current.npcSpawnOverrides[s.id];
            if (!o) return false;
        }
        if (q) {
            const hay = (s.id + ' ' + s.name + ' ' + s.category + ' ' + (s.mobs || []).join(' ')).toLowerCase();
            if (hay.indexOf(q) < 0) return false;
        }
        return true;
    });
}

function renderNpcSpawners() {
    const ul = document.getElementById('npc-list');
    if (!ul) return;
    const rows = filterNpcSpawners();
    const frag = document.createDocumentFragment();
    const LIMIT = 1500;
    for (let i = 0; i < rows.length && i < LIMIT; i++) frag.appendChild(buildNpcRow(rows[i]));
    ul.replaceChildren(frag);

    const c = document.getElementById('npc-count');
    if (c) {
        c.textContent = rows.length > LIMIT
            ? (LIMIT + ' of ' + rows.length + ' shown - narrow the search')
            : (rows.length + ' spawner' + (rows.length === 1 ? '' : 's'));
    }
}

function npcBadge(text, cls) {
    const b = document.createElement('span');
    b.className = 'npc-badge ' + (cls || '');
    b.textContent = text;
    return b;
}

function buildNpcRow(s) {
    const li = cloneTemplate('tpl-npc-row');
    li.dataset.npcId = s.id;

    li.querySelector('.npc-name').textContent = s.name;

    const badges = li.querySelector('.npc-badges');
    badges.appendChild(npcBadge(s.category, 'cat'));
    if (s.kind !== 'npc') badges.appendChild(npcBadge('other', 'other'));
    else if (s.isUniqueNpc) badges.appendChild(npcBadge('single', 'single'));
    const o = state.current && state.current.npcSpawnOverrides && state.current.npcSpawnOverrides[s.id];
    if (o && (o.respawnMinutes != null || o.countMin != null || o.countMax != null)) {
        badges.appendChild(npcBadge('override', 'ovr'));
    }

    const mobs = li.querySelector('.npc-mobs');
    const vanillaBits = [];
    if (s.hasRespawn) vanillaBits.push('respawn ' + s.respawnMinutes + 'm');
    vanillaBits.push('count ' + s.countMin + '-' + s.countMax);
    mobs.textContent = (s.mobs && s.mobs.length ? s.mobs.join(', ') + '  ·  ' : '') + 'vanilla: ' + vanillaBits.join(', ');

    const respawnInput = li.querySelector('[data-npc-field="respawn"]');
    const minInput = li.querySelector('[data-npc-field="countMin"]');
    const maxInput = li.querySelector('[data-npc-field="countMax"]');

    if (s.hasRespawn) {
        respawnInput.placeholder = String(npcEffRespawnMin(s));
        if (o && o.respawnMinutes != null) respawnInput.value = o.respawnMinutes;
    } else {
        respawnInput.placeholder = '-';
        respawnInput.disabled = true;
    }
    minInput.placeholder = String(npcEffCount(s, s.countMin));
    maxInput.placeholder = String(npcEffCount(s, s.countMax));
    if (o && o.countMin != null) minInput.value = o.countMin;
    if (o && o.countMax != null) maxInput.value = o.countMax;

    return li;
}

// ---- override read / write ----
function getOrCreateNpcOverride(id) {
    state.current.npcSpawnOverrides = state.current.npcSpawnOverrides || {};
    if (!state.current.npcSpawnOverrides[id]) state.current.npcSpawnOverrides[id] = {};
    return state.current.npcSpawnOverrides[id];
}

function pruneNpcOverride(id) {
    const o = state.current.npcSpawnOverrides && state.current.npcSpawnOverrides[id];
    if (!o) return;
    if (o.respawnMinutes == null && o.countMin == null && o.countMax == null) {
        delete state.current.npcSpawnOverrides[id];
    }
    if (state.current.npcSpawnOverrides && Object.keys(state.current.npcSpawnOverrides).length === 0) {
        delete state.current.npcSpawnOverrides;
    }
}

function setNpcOverrideField(id, field, rawValue) {
    if (!state.current) return;
    const trimmed = (rawValue || '').trim();
    const o = getOrCreateNpcOverride(id);
    if (trimmed === '') {
        delete o[field];
    } else {
        const n = parseInt(trimmed, 10);
        if (!isFinite(n) || n < 0) return;
        o[field] = n;
    }
    pruneNpcOverride(id);
    markDirty();
    renderNpcStatus();
}

function resetNpcOverride(id) {
    if (!state.current || !state.current.npcSpawnOverrides) return;
    if (!state.current.npcSpawnOverrides[id]) return;
    delete state.current.npcSpawnOverrides[id];
    if (Object.keys(state.current.npcSpawnOverrides).length === 0) delete state.current.npcSpawnOverrides;
    markDirty();
    renderNpcSpawners();
    renderNpcStatus();
}

// ---- global setters ----
// Setters are source-agnostic: the toggled value comes from the event target
// (whichever card fired), state is the single source of truth, and
// renderNpcGlobals() repaints every mirror card.
function setNpcEnabledFromUI(e) {
    if (!state.current) return;
    const on = e && e.target ? e.target.checked : npcGlobalActive();
    state.current.globals = state.current.globals || {};
    if (on) {
        const ns = state.current.globals.npcSpawn || {};
        ns.enabled = true;
        if (typeof ns.respawnMultiplier !== 'number') ns.respawnMultiplier = npcFirstSliderValue('respawn');
        if (typeof ns.countMultiplier !== 'number') ns.countMultiplier = npcFirstSliderValue('count');
        state.current.globals.npcSpawn = ns;
    } else if (state.current.globals.npcSpawn) {
        delete state.current.globals.npcSpawn;
    }
    markDirty();
    renderNpcGlobals();
    renderNpcSpawners();
    renderNpcStatus();
}

function setNpcMultFromUI(which, rawValue) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const ns = state.current.globals.npcSpawn || { enabled: true };
    ns.enabled = true;
    const n = parseFloat(rawValue);
    if (isFinite(n) && n > 0) {
        if (which === 'respawn') ns.respawnMultiplier = n;
        else ns.countMultiplier = n;
    }
    state.current.globals.npcSpawn = ns;
    markDirty();
    renderNpcGlobals();
    renderNpcSpawners();
    renderNpcStatus();
}

function setNpcIncludeSpecialFromUI(e) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    const ns = state.current.globals.npcSpawn || { enabled: true };
    ns.enabled = true;
    ns.includeSpecialTimers = e && e.target ? e.target.checked : !!ns.includeSpecialTimers;
    if (!ns.includeSpecialTimers) delete ns.includeSpecialTimers;
    state.current.globals.npcSpawn = ns;
    markDirty();
    renderNpcGlobals();
    renderNpcSpawners();
    renderNpcStatus();
}

function populateNpcCategoryFilter() {
    const sel = document.getElementById('npc-filter-category');
    if (!sel) return;
    sel.replaceChildren(new Option('All categories', ''));
    for (const c of state.npcCategories) {
        const o = document.createElement('option');
        o.value = c.name;
        o.textContent = c.name + ' (' + c.count + ')';
        sel.appendChild(o);
    }
}

function renderNpcTab() {
    renderNpcGlobals();
    populateNpcCategoryFilter();
    renderNpcSpawners();
    renderNpcStatus();
}

function bindNpcSpawnHandlers() {
    // Global card(s): the NPC Spawns tab card and the Misc-tab mirror. Bind
    // whichever are present so both drive the same shared global.
    let bound = false;
    for (const c of NPC_GLOBAL_CARDS) {
        const en = document.getElementById(c.enabled);
        if (!en) continue;
        bound = true;
        en.addEventListener('change', setNpcEnabledFromUI);
        const rs = document.getElementById(c.respawn);
        if (rs) rs.addEventListener('input', e => setNpcMultFromUI('respawn', e.target.value));
        const cs = document.getElementById(c.count);
        if (cs) cs.addEventListener('input', e => setNpcMultFromUI('count', e.target.value));
        const sp = document.getElementById(c.special);
        if (sp) sp.addEventListener('change', setNpcIncludeSpecialFromUI);
    }
    if (!bound) return; // tab html not present

    // Per-spawner list lives only on the NPC Spawns tab.
    const filter = document.getElementById('npc-filter');
    if (!filter) return;
    filter.addEventListener('input', renderNpcSpawners);
    document.getElementById('npc-filter-category').addEventListener('change', renderNpcSpawners);
    document.getElementById('npc-filter-kind').addEventListener('change', renderNpcSpawners);
    document.getElementById('npc-filter-changed').addEventListener('change', renderNpcSpawners);

    const list = document.getElementById('npc-list');
    list.addEventListener('input', e => {
        const t = e.target;
        const row = t.closest && t.closest('.npc-row');
        if (!row || !t.dataset || !t.dataset.npcField) return;
        setNpcOverrideField(row.dataset.npcId, t.dataset.npcField, t.value);
    });
    list.addEventListener('click', e => {
        const t = e.target;
        if (!t.dataset || t.dataset.npcReset === undefined) return;
        const row = t.closest('.npc-row');
        if (row) resetNpcOverride(row.dataset.npcId);
    });
}

// Global is "modded" when active with a non-1 multiplier. Shared by the NPC
// Spawns tab indicator and the Misc-tab indicator (the mirror card surfaces the
// same global, so both tabs light up - mirrors the stack-size precedent).
function npcSpawnGlobalHasMods() {
    if (!npcGlobalActive()) return false;
    return Math.abs(npcRespawnMult() - 1.0) > 1e-9 || Math.abs(npcCountMult() - 1.0) > 1e-9;
}

// NPC tab has mods if the global is modded, or any per-spawner override exists.
function npcSpawnTabHasMods() {
    const o = state.current && state.current.npcSpawnOverrides;
    if (o && Object.keys(o).length > 0) return true;
    return npcSpawnGlobalHasMods();
}
