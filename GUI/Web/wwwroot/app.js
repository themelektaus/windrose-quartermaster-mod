'use strict';

// Windrose Quartermaster - vanilla JS, no framework.
// State lives in a single object; the few mutators that change it call the
// affected render functions explicitly.

const state = {
    items: [],            // [{id, vanillaStack, itemClass, rarity, category, icon, meta}]
    itemsById: new Map(), // id -> item, for quick LT-entry lookups
    lootTables: [],       // [{id, category, type, entries:[{index,min,max,weight,lootItemId,lootItemPath,lootTableId,lootTablePath}]}]
    lootById: new Map(),  // ltId -> lt
    lootCategories: [],   // sorted list of distinct categories with counts: [{name, count}]
    lootTypes: [],        // sorted distinct LT types
    itemPathsByItemId: new Map(),    // itemId -> lootItemPath (mined from LT entries; only items that show up in LTs)
    tablePathsByLtId:  new Map(),    // ltId    -> lootTablePath
    expandedLts: new Set(),          // ltIds currently expanded in the loot view

    profiles: [],    // summaries from /api/profiles
    current: null,   // full profile from /api/profiles/{id}
    isDirty: false,
    activeTab: 'items',

    // Windrose ~mods/ folder snapshot (lazy-loaded the first time the
    // Mods tab is shown, then refreshed after every successful build and
    // on explicit user action via the Refresh button).
    mods: {
        loaded: false,
        modsDir: null,
        files: [],     // [{filename, sizeBytes, modifiedUtc, isQuartermaster, displayName}]
        error: null,   // human-readable error string from the GET, or null
    },

    // Currently-open picker dropdown (loot add-entry autocomplete).
    // null when no picker is open. Holds the input element so we can reposition
    // on scroll / write the chosen id back, plus the address of the added entry
    // (ltId + addedIndex) and the picker mode ('item' | 'table').
    picker: null,    // { input, ltId, addedIndex, type } or null
};

// ---------- API helpers --------------------------------------------------

async function api(method, path, body) {
    const opts = { method, headers: {} };
    if (body !== undefined) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(body);
    }
    const r = await fetch(path, opts);
    if (r.status === 204) return null;
    if (!r.ok) {
        let err = { error: r.statusText };
        try { err = await r.json(); } catch (e) { /* keep statusText */ }
        throw new Error(method + ' ' + path + ': ' + (err.error || r.status));
    }
    return await r.json();
}

const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
}[c]));

// ---------- Boot ---------------------------------------------------------

async function loadAppData() {
    const [profiles, items, lootTables] = await Promise.all([
        api('GET', '/api/profiles'),
        api('GET', '/api/items'),
        api('GET', '/api/loot-tables'),
    ]);
    state.profiles = profiles;
    state.items = items
        .filter(i => typeof i.maxCountInSlot === 'number')
        .map(i => Object.assign({}, i, { vanillaStack: i.maxCountInSlot }));
    state.itemsById = new Map(state.items.map(i => [i.id, i]));

    state.lootTables = lootTables || [];
    state.lootById = new Map(state.lootTables.map(lt => [lt.id, lt]));
    indexLootCrossReferences();

    populateProfileSelect();
    populateValueFilter('filter-class',  'itemClass', 'All classes');
    populateValueFilter('filter-rarity', 'rarity',    'All rarities');
    populateLootCategoryFilter();

    if (state.profiles.length > 0) {
        await loadProfile(state.profiles[0].id);
    } else {
        updateButtons();
    }
}

// Build lookup maps so the loot view can:
//   * resolve a lootItemId -> the canonical UE asset path used in vanilla
//     (needed when a user adds a new entry: we want them to be able to type
//     "Banana" and have us emit /R5BusinessRules/.../Banana_T01.Banana_T01)
//   * resolve a lootTableId -> the canonical UE sub-table asset path (for
//     reuse when adding sub-table refs).
//   * enumerate distinct categories + types for the filter dropdowns and
//     the per-category multiplier rows in the globals panel.
//
// Item paths are seeded from /api/items (item.path is derived server-side
// from the on-disk source layout, so every item has one). Vanilla LT
// entries provide a redundant fallback for items predating the path field.
function indexLootCrossReferences() {
    state.itemPathsByItemId = new Map();
    state.tablePathsByLtId  = new Map();
    for (const item of state.items) {
        if (item.path) state.itemPathsByItemId.set(item.id, item.path);
    }
    const categoryCounts = new Map();
    const types = new Set();
    for (const lt of state.lootTables) {
        if (lt.category) categoryCounts.set(lt.category, (categoryCounts.get(lt.category) || 0) + 1);
        if (lt.type) types.add(lt.type);
        for (const e of lt.entries || []) {
            if (e.lootItemId  && e.lootItemPath  && !state.itemPathsByItemId.has(e.lootItemId)) {
                state.itemPathsByItemId.set(e.lootItemId, e.lootItemPath);
            }
            if (e.lootTableId && e.lootTablePath && !state.tablePathsByLtId.has(e.lootTableId)) {
                state.tablePathsByLtId.set(e.lootTableId, e.lootTablePath);
            }
        }
    }
    state.lootCategories = Array.from(categoryCounts.entries())
        .map(([name, count]) => ({ name, count }))
        .sort((a, b) => a.name.localeCompare(b.name));
    state.lootTypes = Array.from(types).sort();
}

async function boot() {
    // Bind setup-overlay handlers up-front so the user can interact with it
    // even if the first /api/setup/status fails entirely.
    bindSetupHandlers();
    bindHandlers();

    // Probe the mod root. If both Sources/Vanilla and Icons are populated
    // we go straight into the configurator; otherwise the setup overlay
    // takes over and the user clicks "Run setup" themselves.
    const status = await api('GET', '/api/setup/status');
    if (status.isReady) {
        await loadAppData();
        return;
    }

    showSetupOverlay(status);
    // No auto-run - the user explicitly clicks "Run setup". The Run button
    // is enabled when prerequisites are satisfied (see canAutoRunSetup).
}

document.addEventListener('DOMContentLoaded', () => {
    boot().catch(err => {
        document.body.innerHTML =
            '<pre style="color:#e16464;padding:2em;white-space:pre-wrap;">' +
            esc('Init failed: ' + err.message + '\n\n' + (err.stack || '')) +
            '</pre>';
    });
});

// ---------- Setup overlay ------------------------------------------------

function showSetupOverlay(status) {
    document.getElementById('setup-overlay').hidden = false;
    renderSetupChecks(status);
    renderSetupError(status);
    syncSetupRunEnabled(status);
}

function syncSetupRunEnabled(status) {
    // Run button only useful when prereqs are present; otherwise the user
    // needs to fix something first and click Re-check.
    document.getElementById('setup-run').disabled = !canAutoRunSetup(status);
}

function hideSetupOverlay() {
    document.getElementById('setup-overlay').hidden = true;
}

function renderSetupChecks(status) {
    const ul = document.getElementById('setup-checks');
    const rows = [
        ['hasVanillaPak',     'Windrose install detected via Steam',
                              status.vanillaPakPath || status.vanillaPakError],
        ['hasUsmap',          'UE5 mappings file (.usmap) in mod root',
                              status.usmapPath || 'Drop a Ctrl+Num6 dump into the mod root.'],
        ['hasVanillaSources', 'Vanilla item JSONs extracted',
                              'Sources/Vanilla - produced by the dump step.'],
        ['hasIcons',          'Item icons extracted',
                              status.iconsDir + ' - produced by the icons step.'],
    ];
    ul.innerHTML = rows.map(([key, label, detail]) => {
        const ok = !!status[key];
        return '<li class="' + (ok ? 'ok' : 'bad') + '">' +
            '<div><b>' + esc(label) + '</b>' +
            (detail ? '<br><small>' + esc(detail) + '</small>' : '') +
            '</div></li>';
    }).join('');
}

function renderSetupError(status) {
    const out = document.getElementById('setup-error');
    if (!status.hasVanillaPak) {
        out.hidden = false;
        out.textContent =
            'Cannot find a Windrose install: ' + (status.vanillaPakError || '(no detail)') +
            '\nInstall Windrose via Steam, then click Re-check.';
        return;
    }
    if (!status.hasUsmap) {
        out.hidden = false;
        out.textContent =
            'No .usmap file found in the mod root.\n' +
            'Generate one via UE4SS Keybinds (DumpUSMAP - Ctrl+Num6 by default), ' +
            'copy it next to the mod, then click Re-check.';
        return;
    }
    out.hidden = true;
    out.textContent = '';
}

function canAutoRunSetup(status) {
    return status.hasVanillaPak && status.hasUsmap && !status.isRunning;
}

function appendSetupLog(line, kind) {
    const out = document.getElementById('setup-log');
    const span = document.createElement('span');
    if (kind) span.className = kind;
    span.textContent = (kind ? '[' + kind.toUpperCase() + '] ' : '') + line + '\n';
    out.appendChild(span);
    out.scrollTop = out.scrollHeight;
}

function clearSetupLog() {
    document.getElementById('setup-log').innerHTML = '';
}

function setSetupButtonsDisabled(disabled) {
    document.getElementById('setup-run').disabled     = disabled;
    document.getElementById('setup-force').disabled   = disabled;
    document.getElementById('setup-recheck').disabled = disabled;
}

function bindSetupHandlers() {
    document.getElementById('setup-run').addEventListener('click', () => runSetup(false));
    document.getElementById('setup-force').addEventListener('click', () => runSetup(true));
    document.getElementById('setup-recheck').addEventListener('click', recheckSetup);
    document.getElementById('setup-continue').addEventListener('click', async () => {
        hideSetupOverlay();
        await loadAppData();
    });
}

async function recheckSetup() {
    const status = await api('GET', '/api/setup/status');
    if (status.isReady) {
        hideSetupOverlay();
        await loadAppData();
        return;
    }
    renderSetupChecks(status);
    renderSetupError(status);
    syncSetupRunEnabled(status);
}

// Streams /api/setup/run via Server-Sent Events. Each "log" event becomes
// a line in the setup log; the terminal "done" event carries success/error.
function runSetup(force) {
    return new Promise(resolve => {
        const url = '/api/setup/run' + (force ? '?force=true' : '');
        clearSetupLog();
        setSetupButtonsDisabled(true);
        // Re-hide the success-only Continue button if we're restarting.
        document.getElementById('setup-continue').hidden = true;
        document.getElementById('setup-run').hidden = false;
        appendSetupLog((force ? 'Force re-running ' : 'Running ') + 'setup...', 'step');

        // Native EventSource only supports GET; we want POST to keep the
        // semantics clear (this mutates state). Use fetch() + ReadableStream
        // and parse SSE manually - straightforward for the small frame set
        // we emit.
        fetch(url, { method: 'POST' }).then(async resp => {
            if (!resp.ok) {
                const text = await resp.text().catch(() => resp.statusText);
                appendSetupLog('HTTP ' + resp.status + ': ' + text, 'err');
                setSetupButtonsDisabled(false);
                document.getElementById('setup-force').hidden = false;
                return resolve();
            }
            const reader = resp.body.getReader();
            const dec = new TextDecoder();
            let buf = '';

            while (true) {
                const { value, done } = await reader.read();
                if (done) break;
                buf += dec.decode(value, { stream: true });
                // SSE frames end with a blank line.
                let idx;
                while ((idx = buf.indexOf('\n\n')) >= 0) {
                    const frame = buf.slice(0, idx);
                    buf = buf.slice(idx + 2);
                    handleSseFrame(frame);
                }
            }
            // Stream ended without an explicit "done" event.
            setSetupButtonsDisabled(false);
            resolve();
        }).catch(err => {
            appendSetupLog('Network error: ' + err.message, 'err');
            setSetupButtonsDisabled(false);
            document.getElementById('setup-force').hidden = false;
            resolve();
        });

        function handleSseFrame(frame) {
            let event = 'message', data = '';
            for (const line of frame.split('\n')) {
                if      (line.startsWith('event: ')) event = line.slice(7).trim();
                else if (line.startsWith('data: '))  data  = line.slice(6);
            }
            if (event === 'log') {
                const cls = classifyLogLine(data);
                appendSetupLog(data, cls);
            } else if (event === 'done') {
                let payload = {};
                try { payload = JSON.parse(data); } catch (e) { /* keep empty */ }
                if (payload.success) {
                    appendSetupLog('Setup complete. Click "Continue" to open the configurator.', 'ok');
                    setSetupButtonsDisabled(false);
                    document.getElementById('setup-run').hidden = true;
                    document.getElementById('setup-force').hidden = false;
                    document.getElementById('setup-continue').hidden = false;
                    resolve();
                } else {
                    appendSetupLog('Setup failed: ' + (payload.error || 'unknown'), 'err');
                    setSetupButtonsDisabled(false);
                    document.getElementById('setup-force').hidden = false;
                    resolve();
                }
            }
        }
    });
}

// Cosmetic: highlight [step:start ...] / [OK] / [X] etc. in the log feed.
function classifyLogLine(line) {
    if (line.startsWith('[step:start ') || line.startsWith('[step:end ')) return 'step';
    if (line.startsWith('[skip] ')) return 'step';
    if (line.startsWith('[OK]') || line.startsWith('[ok]')) return 'ok';
    if (line.startsWith('[X]')  || line.startsWith('[!]'))  return 'err';
    return null;
}

// ---------- Tabs ---------------------------------------------------------

function setActiveTab(tab) {
    state.activeTab = tab;
    for (const b of document.querySelectorAll('.tab')) {
        const isActive = b.dataset.tab === tab;
        b.classList.toggle('active', isActive);
        b.setAttribute('aria-selected', String(isActive));
    }
    for (const p of document.querySelectorAll('.tab-page')) {
        p.hidden = p.dataset.tab !== tab;
    }
    if (tab === 'loot') {
        renderLootGlobals();
        renderLootTables();
        renderLootStatus();
    }
    if (tab === 'mods') {
        // First view: kick off a load. Subsequent visits show the cached
        // snapshot immediately and the user can hit Refresh to re-fetch.
        if (!state.mods.loaded) {
            loadMods();
        } else {
            renderMods();
            renderModsStatus();
        }
    }
}

// ---------- Profile loading ---------------------------------------------

async function loadProfile(id) {
    state.current = await api('GET', '/api/profiles/' + encodeURIComponent(id));
    state.current.globals       = state.current.globals || {};
    state.current.overrides     = state.current.overrides || {};
    state.current.lootOverrides = state.current.lootOverrides || {};
    state.isDirty = false;
    document.getElementById('profile-select').value = id;
    applyProfileToUI();
    renderItems();
    renderStatus();
    if (state.activeTab === 'loot') {
        renderLootGlobals();
        renderLootTables();
        renderLootStatus();
    }
    updateButtons();
    setBuildLog([{ kind: 'info', msg: 'Profile loaded: ' + state.current.name }]);
}

function applyProfileToUI() {
    const p = state.current;
    const ss = (p.globals && p.globals.stackSize) || {};
    const mode = ss.absolute != null ? 'absolute'
              : ss.multiplier != null ? 'multiplier'
              : 'none';
    document.querySelector('input[name="ssmode"][value="' + mode + '"]').checked = true;
    document.getElementById('ss-mult').value = ss.multiplier == null ? 4 : ss.multiplier;
    document.getElementById('ss-cap').value  = ss.cap        == null ? 0 : ss.cap;
    document.getElementById('ss-abs').value  = ss.absolute   == null ? 999 : ss.absolute;
    // Pickup-radius: a checkbox toggles the whole subtree, the slider
    // sets globals.pickupRadius.multiplier (1.0 - 10.0).
    const pr = (p.globals && p.globals.pickupRadius) || null;
    const pickupMul = pr && pr.multiplier != null ? pr.multiplier : null;
    const pickupOn = pickupMul != null && Math.abs(pickupMul - 1.0) > 1e-9;
    document.getElementById('pickup-enabled').checked = pickupOn;
    // Carry the previous multiplier as the slider's default when re-enabled,
    // so toggling off-and-on doesn't snap back to 2.0 mid-edit. 2.0 is the
    // "first time the user enables it" default.
    document.getElementById('pickup-multiplier').value =
        pickupOn ? pickupMul : 2.0;
    syncPickupReadout();
    // Fast-travel bell caps: two number inputs. Vanilla = 10 / 3; null
    // in the profile means "use vanilla" so the input snaps to vanilla
    // for legibility. Empty string would also render as 0 in some
    // browsers, which would be misleading.
    const ftb = (p.globals && p.globals.fastTravelBells) || null;
    document.getElementById('bell-cap').value =
        ftb && ftb.bellCap != null ? ftb.bellCap : 10;
    document.getElementById('signal-fire-cap').value =
        ftb && ftb.signalFireCap != null ? ftb.signalFireCap : 3;
    // Building-stability single-toggle. Treated as off whenever the
    // profile doesn't have it set (matches the build pipeline's
    // ResolveStabilityEnabled, which folds null/missing into false).
    const bs = (p.globals && p.globals.buildingStability) || null;
    document.getElementById('building-stability-enabled').checked =
        !!(bs && bs.enabled === true);
    // No-Smoke per-category toggles. Same null-folds-to-off semantics
    // as building-stability; each flag is independent.
    const ns = (p.globals && p.globals.noSmoke) || null;
    document.getElementById('nosmoke-campfire').checked = !!(ns && ns.campfire === true);
    document.getElementById('nosmoke-furnace').checked  = !!(ns && ns.furnace === true);
    document.getElementById('nosmoke-kiln').checked     = !!(ns && ns.kiln === true);
    // Minimap-range: checkbox + slider, same pattern as pickup-radius
    // (1.0 / null collapses to off; off-and-on remembers the last value).
    const mr = (p.globals && p.globals.minimapRange) || null;
    const minimapMul = mr && mr.multiplier != null ? mr.multiplier : null;
    const minimapOn = minimapMul != null && Math.abs(minimapMul - 1.0) > 1e-9;
    document.getElementById('minimap-enabled').checked = minimapOn;
    document.getElementById('minimap-multiplier').value =
        minimapOn ? minimapMul : 2.0;
    syncMinimapReadout();
    // Bonfire-radius: same pattern as minimap. 1.0 / null collapses to
    // off; off-and-on remembers the last value. Default multiplier 3.0
    // matches the reference-mod baseline (15000 cm / 9000 cm).
    const br = (p.globals && p.globals.bonfireRadius) || null;
    const bonfireMul = br && br.multiplier != null ? br.multiplier : null;
    const bonfireOn = bonfireMul != null && Math.abs(bonfireMul - 1.0) > 1e-9;
    document.getElementById('bonfire-enabled').checked = bonfireOn;
    document.getElementById('bonfire-multiplier').value =
        bonfireOn ? bonfireMul : 2.0;
    syncBonfireReadout();
    syncStackSizeInputsState();
    syncPickupInputState();
    syncBellInputState();
    syncBuildingStabilityInputState();
    syncNoSmokeInputState();
    syncMinimapInputState();
    syncBonfireInputState();
    renderProfileMeta();
}

function renderProfileMeta() {
    const p = state.current;
    const out = document.getElementById('profile-meta');
    if (!p) { out.innerHTML = ''; return; }
    let html = '';
    if (state.isDirty) html += '<span class="dirty-badge">UNSAVED</span>';
    html += esc(p.description) || `&nbsp;`;
    out.innerHTML = html;
}

function syncStackSizeInputsState() {
    const mode = document.querySelector('input[name="ssmode"]:checked').value;
    document.getElementById('ss-mult').disabled = mode !== 'multiplier';
    document.getElementById('ss-cap').disabled  = mode !== 'multiplier';
    document.getElementById('ss-abs').disabled  = mode !== 'absolute';
    for (const r of document.querySelectorAll('input[name="ssmode"]')) {
        r.disabled = false;
    }
}

// Pickup-radius slider is disabled while the checkbox is off so "0.5x range"
// can't accidentally happen while the patch is supposedly off.
function syncPickupInputState() {
    const enabled = document.getElementById('pickup-enabled');
    const slider  = document.getElementById('pickup-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

// Bell-cap inputs: always editable.
function syncBellInputState() {
    document.getElementById('bell-cap').disabled = false;
    document.getElementById('signal-fire-cap').disabled = false;
}

// Building-stability single toggle: always editable.
function syncBuildingStabilityInputState() {
    document.getElementById('building-stability-enabled').disabled = false;
}

// No-Smoke per-category toggles: always editable.
function syncNoSmokeInputState() {
    document.getElementById('nosmoke-campfire').disabled = false;
    document.getElementById('nosmoke-furnace').disabled  = false;
    document.getElementById('nosmoke-kiln').disabled     = false;
}

// Minimap-range slider follows the pickup-radius pattern: disabled while
// the checkbox is off so "0.5x range" can't silently happen.
function syncMinimapInputState() {
    const enabled = document.getElementById('minimap-enabled');
    const slider  = document.getElementById('minimap-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

// Mirrors the multiplier into the readout span ("2.0x ... 500 cm @ 74 brush
// / 1500 cm @ 580 brush"). Vanilla baselines come straight from
// MinimapRangePatcher's constants.
function syncMinimapReadout() {
    const slider = document.getElementById('minimap-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('minimap-multiplier-value').innerHTML =
        mul.toFixed(1) + 'x<!--&times;-->';
    const footBrush = 37  * mul;
    const footDist  = 250 * mul;
    const shipBrush = 290 * mul;
    const shipDist  = 750 * mul;
    document.getElementById('minimap-foot-readout').textContent =
        footDist.toFixed(0) + ' cm @ ' + footBrush.toFixed(0) + ' brush';
    document.getElementById('minimap-ship-readout').textContent =
        shipDist.toFixed(0) + ' cm @ ' + shipBrush.toFixed(0) + ' brush';
}

// Bonfire-radius slider mirrors the minimap / pickup pattern: disabled
// while the checkbox is off so a downscale can't silently happen.
function syncBonfireInputState() {
    const enabled = document.getElementById('bonfire-enabled');
    const slider  = document.getElementById('bonfire-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

// Mirrors the multiplier into the readout span ("3.0x ... 15000 cm
// (~150 m) ... 9000 cm (~90 m)"). Vanilla baselines come from
// BonfireRadiusPatcher's constants (5000 / 3000 cm).
function syncBonfireReadout() {
    const slider = document.getElementById('bonfire-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('bonfire-multiplier-value').innerHTML =
        mul.toFixed(1) + 'x<!--&times;-->';
    const radius = 5000 * mul;
    const height = 3000 * mul;
    document.getElementById('bonfire-radius-readout').textContent =
        radius.toFixed(0) + ' cm (~' + (radius / 100).toFixed(0) + ' m)';
    document.getElementById('bonfire-height-readout').textContent =
        height.toFixed(0) + ' cm (~' + (height / 100).toFixed(0) + ' m)';
}

// Mirror the slider value into the read-out span ("2.0x ... 8.0 m"). Pulled
// out so both the change-event and the initial render can call it.
function syncPickupReadout() {
    const slider = document.getElementById('pickup-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('pickup-multiplier-value').innerHTML =
        mul.toFixed(1) + 'x<!--&times;-->';
    // Vanilla magnet range is 4.0 m; the slider scales it linearly.
    document.getElementById('pickup-range').textContent =
        (4.0 * mul).toFixed(1) + ' m';
}

function populateProfileSelect() {
    const sel = document.getElementById('profile-select');
    state.profiles.sort((a, b) => a.name.localeCompare(b.name));
    sel.innerHTML = '';
    for (const p of state.profiles) {
        const o = document.createElement('option');
        o.value = p.id;
        o.textContent = p.name;
        sel.appendChild(o);
    }
    syncNoProfileState();
}

// Toggles the empty-state UI when /api/profiles is empty: hides the tabs +
// per-profile toolbar buttons + every tab-page (via body.no-profiles in
// app.css) and renders the "create your first profile" hint inside <main>.
// Called from populateProfileSelect() so every code path that mutates
// state.profiles flows through here automatically.
function syncNoProfileState() {
    document.body.classList.toggle('no-profiles', state.profiles.length === 0);
}

function populateValueFilter(elId, key, allLabel) {
    const sel = document.getElementById(elId);
    const values = Array.from(new Set(state.items.map(i => i[key]).filter(x => x))).sort();
    // Keep the placeholder option, append discovered values.
    sel.innerHTML = '<option value="">' + esc(allLabel) + '</option>';
    for (const v of values) {
        const o = document.createElement('option');
        o.value = v; o.textContent = v;
        sel.appendChild(o);
    }
}

function populateLootCategoryFilter() {
    const cat = document.getElementById('lt-filter-category');
    cat.innerHTML = '<option value="">All categories</option>';
    for (const c of state.lootCategories) {
        const o = document.createElement('option');
        o.value = c.name;
        o.textContent = c.name + ' (' + c.count + ')';
        cat.appendChild(o);
    }
    const tp = document.getElementById('lt-filter-type');
    tp.innerHTML = '<option value="">All types</option>';
    for (const t of state.lootTypes) {
        const o = document.createElement('option');
        o.value = t; o.textContent = t;
        tp.appendChild(o);
    }
}

// (The native <datalist> approach was replaced by a custom dropdown -
// see openPicker / populatePicker further down. We need rich rows
// (icon + name + class/category for items; id + category/type/entry count
// for sub-tables) and per-mode data sources, neither of which a datalist
// can do.)

// ---------- Computed target (mirrors StackPatcher.cs) -------------------

function computeTarget(item) {
    const overrides = (state.current && state.current.overrides) || {};
    const ss = (state.current && state.current.globals && state.current.globals.stackSize) || {};
    const v = item.vanillaStack;

    const ov = overrides[item.id];
    if (ov && typeof ov.stackSize === 'number') {
        if (ov.stackSize === v) {
            return { html: '<span class="skip">' + ov.stackSize + '</span>', changed: false, overridden: true, noChange: true, target: v };
        }
        return {
            html: '<b>' + ov.stackSize + '</b>',
            changed: true, overridden: true, target: ov.stackSize,
        };
    }

    // For vanillaStack <= 1, globals only apply if the item is "promotable":
    //   * ItemClass == Consumable, OR
    //   * ItemType.TagName == Inventory.ItemType.Resource (catches Misc-tagged
    //     treasure pieces the game still classifies as resources), OR
    //   * Default+Resource (legacy folder rule).
    // Must stay in sync with StackPatcher.IsPromotable on the server side.
    const isPromotable = item.itemClass === 'Consumable'
        || item.itemType === 'Inventory.ItemType.Resource'
        || (item.itemClass === 'Default' && item.category === 'Resource');
    if (v <= 1 && !isPromotable) {
        return {
            html: '<span class="skip">1</span>',
            changed: false, overridden: false, target: v,
        };
    }

    if (typeof ss.absolute === 'number') {
        if (ss.absolute === v) {
            return { html: '<span class="skip">0</span>', changed: false, overridden: false, noChange: true, target: v };
        }
        return { html: '<b>' + ss.absolute + '</b>', changed: true, overridden: false, target: ss.absolute };
    }
    if (typeof ss.multiplier === 'number') {
        let target = v * ss.multiplier;
        if (typeof ss.cap === 'number' && ss.cap > 0 && target > ss.cap) target = ss.cap;
        if (target === v) {
            return { html: '<span class="skip">0</span>', changed: false, overridden: false, noChange: true, target: v };
        }
        return { html: '<b>' + target + '</b>', changed: true, overridden: false, target };
    }

    return { html: '<span class="skip">' + v + '</span>', changed: false, overridden: false, target: v };
}

// ---------- Item rendering ----------------------------------------------

function filterItems() {
    const q   = document.getElementById('item-filter').value.toLowerCase().trim();
    const fc  = document.getElementById('filter-class').value;
    const fr  = document.getElementById('filter-rarity').value;
    const chg = document.getElementById('filter-changed').value;

    return state.items.filter(item => {
        if (q) {
            const name = (item.meta && item.meta.name) || '';
            if (!item.id.toLowerCase().includes(q) && !name.toLowerCase().includes(q)) return false;
        }
        if (fc && item.itemClass !== fc) return false;
        if (fr && item.rarity    !== fr) return false;
        if (chg !== 'all') {
            const t = computeTarget(item);
            if (chg === 'changed'    && !t.changed)    return false;
            if (chg === 'unchanged'  &&  t.changed)    return false;
            if (chg === 'overridden' && !t.overridden) return false;
        }
        return true;
    });
}

function renderItems() {
    const ul = document.getElementById('item-list');
    const filtered = filterItems();
    document.getElementById('item-count').textContent =
        filtered.length + ' / ' + state.items.length + ' items';

    // Use a DocumentFragment so we only touch the DOM once for ~1000 rows.
    const frag = document.createDocumentFragment();
    for (const item of filtered) frag.appendChild(buildItemRow(item));
    ul.innerHTML = '';
    ul.appendChild(frag);
}

function buildItemRow(item) {
    const li = document.createElement('li');
    li.className = 'item';
    li.dataset.itemId = item.id;

    const target = computeTarget(item);
    if (target.changed)    li.classList.add('changed');
    if (target.overridden) li.classList.add('overridden');

    const displayName = (item.meta && item.meta.name) || item.id;
    const subtitle = (item.itemClass || '')
        + (item.category ? ' · ' + item.category : '')
        + (item.rarity   ? ' · ' + item.rarity   : '');

    const description = item.meta?.description ?? ``
    const ov = state.current && state.current.overrides && state.current.overrides[item.id];
    const ovValue = ov && ov.stackSize != null ? ov.stackSize : '';

    const iconHtml = item.icon
        ? '<img src="' + esc(item.icon) + '" loading="lazy" alt="">'
        : '<div class="placeholder-icon">?</div>';

    li.innerHTML =
        iconHtml +
        '<div class="name">' +
            '<b>' + esc(displayName) + '</b>' +
            '<small>' + esc(subtitle) + '</small>' +
            '<div>' + esc(description) + '</div>' +
        '</div>' +
        '<div class="compute">' + item.vanillaStack + ' → ' + target.html + '</div>' +
        '<input type="number" class="override-input" data-item-id="' + esc(item.id) + '" ' +
               'value="' + esc(ovValue) + '" placeholder="' + target.target + '" min="0" step="1">';
    return li;
}

// In-place row update - avoids losing focus on the override input.
function refreshRowInPlace(itemId) {
    const item = state.items.find(i => i.id === itemId);
    if (!item) return;
    const row = document.querySelector('.item[data-item-id="' + cssEsc(itemId) + '"]');
    if (!row) return;
    const t = computeTarget(item);
    row.classList.toggle('changed',    t.changed);
    row.classList.toggle('overridden', t.overridden);
    row.classList.toggle('noChange',   t.noChange);
    const compute = row.querySelector('.compute');
    if (compute) compute.innerHTML = item.vanillaStack + ' → ' + t.html;
}

function cssEsc(s) {
    // CSS.escape for attribute selectors. Items ids are filename-safe so a
    // basic implementation is enough.
    return s.replace(/(["\\])/g, '\\$1');
}

function renderStatus() {
    const overrides = (state.current && state.current.overrides) || {};
    let overrideCount = 0;
    for (const k in overrides) if (overrides[k] && overrides[k].stackSize != null) overrideCount++;

    let modified = 0, promoted = 0;
    for (const item of state.items) {
        const t = computeTarget(item);
        if (t.changed) {
            modified++;
            if (item.vanillaStack <= 1) promoted++;
        }
    }
    document.getElementById('stat-total').textContent     = state.items.length;
    document.getElementById('stat-overrides').textContent = overrideCount;
    document.getElementById('stat-modified').textContent  = modified;
    document.getElementById('stat-promoted').textContent  = promoted;
}

// ---------- Loot: globals panel -----------------------------------------

function getLootGlobalForCategory(cat) {
    const g = state.current && state.current.globals;
    if (!g || !g.loot || !g.loot.byCategory) return null;
    const v = g.loot.byCategory[cat];
    return typeof v === 'number' ? v : null;
}

function renderLootGlobals() {
    const out = document.getElementById('loot-globals');
    if (!out) return;
    const rows = [];
    for (const c of state.lootCategories) {
        const v = getLootGlobalForCategory(c.name);
        rows.push(
            '<span class="cat">' + esc(c.name) +
                '<span class="cat-count">(' + c.count + ')</span></span>' +
            '<input type="number" min="0" step="0.5" placeholder="1.0" ' +
                'data-loot-cat="' + esc(c.name) + '" ' +
                'value="' + (v != null ? esc(v) : '') + '">' +
            '<button class="reset" type="button" data-reset-cat="' + esc(c.name) + '">x</button>'
        );
    }
    out.innerHTML = rows.join('');
}

function renderLootStatus() {
    const ovrCount = state.current && state.current.lootOverrides
        ? Object.keys(state.current.lootOverrides).length
        : 0;

    let modified = 0;
    for (const lt of state.lootTables) {
        if (computeLtChanged(lt)) modified++;
    }

    const total = document.getElementById('lt-stat-total');
    const ovr   = document.getElementById('lt-stat-overrides');
    const mod   = document.getElementById('lt-stat-modified');
    if (total) total.textContent = state.lootTables.length;
    if (ovr)   ovr.textContent   = ovrCount;
    if (mod)   mod.textContent   = modified;
}

function setLootGlobalFromInput(cat, rawValue) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    state.current.globals.loot = state.current.globals.loot || { byCategory: {} };
    state.current.globals.loot.byCategory = state.current.globals.loot.byCategory || {};

    const trimmed = (rawValue || '').trim();
    if (trimmed === '') {
        delete state.current.globals.loot.byCategory[cat];
    } else {
        const n = parseFloat(trimmed);
        if (!isFinite(n) || n < 0) return;
        state.current.globals.loot.byCategory[cat] = n;
    }
    // Empty out the loot block entirely if the user cleared every category,
    // so we don't persist a noisy "{ byCategory: {} }" sentinel.
    if (Object.keys(state.current.globals.loot.byCategory).length === 0) {
        delete state.current.globals.loot;
    }
    markDirty();
    renderLootStatus();
    renderLootTables();
}

function resetLootGlobalCategory(cat) {
    if (!state.current) return;
    if (state.current.globals && state.current.globals.loot
        && state.current.globals.loot.byCategory) {
        if (!(cat in state.current.globals.loot.byCategory)) return;
        delete state.current.globals.loot.byCategory[cat];
        if (Object.keys(state.current.globals.loot.byCategory).length === 0) {
            delete state.current.globals.loot;
        }
    }
    markDirty();
    renderLootGlobals();
    renderLootStatus();
    renderLootTables();
}

// ---------- Loot: per-LT resolver mirror -------------------------------

// Resolves a vanilla LootData[i] entry under the current profile, mirroring
// LootPatcher.BuildEntry on the server. Returns:
//   {
//     min, max, weight, lootItem, lootTable,
//     edited:    bool (this entry has any edit fields set),
//     removed:   bool (entry is in lootOverrides[ltId].removed),
//     changedByMult: bool (multiplier alters min/max relative to vanilla),
//     vanilla: { min, max, weight, lootItem, lootTable }
//   }
function resolveLootEntry(lt, vanillaEntry) {
    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    const edit = (ovr && ovr.entries && ovr.entries[String(vanillaEntry.index)]) || null;
    const removed = !!(ovr && ovr.removed && ovr.removed.includes(vanillaEntry.index));

    const cat = lt.category;
    const mult = getLootGlobalForCategory(cat);
    const isOrchestrator = !vanillaEntry.lootItemId && !!vanillaEntry.lootTableId;

    const v = {
        min: vanillaEntry.min, max: vanillaEntry.max, weight: vanillaEntry.weight,
        lootItem:  vanillaEntry.lootItemPath  || (vanillaEntry.lootItemId ? null : 'None'),
        lootTable: vanillaEntry.lootTablePath || (vanillaEntry.lootTableId ? null : 'None'),
    };

    let min = v.min, max = v.max;
    if (edit && edit.min != null) min = edit.min;
    else if (!isOrchestrator && mult != null) min = Math.round(v.min * mult);

    if (edit && edit.max != null) max = edit.max;
    else if (!isOrchestrator && mult != null) max = Math.round(v.max * mult);

    const weight = (edit && edit.weight != null) ? edit.weight : v.weight;
    const lootItem  = (edit && edit.lootItem  != null) ? edit.lootItem  : v.lootItem;
    const lootTable = (edit && edit.lootTable != null) ? edit.lootTable : v.lootTable;

    const changedByMult = !isOrchestrator && mult != null && mult !== 1
        && (min !== v.min || max !== v.max);
    const edited = !!edit && (edit.min != null || edit.max != null
        || edit.weight != null || edit.lootItem != null || edit.lootTable != null);

    return { min, max, weight, lootItem, lootTable,
        edited, removed, changedByMult, vanilla: v };
}

// Returns true if any aspect of the LT will change in the build relative to
// vanilla - this drives the "modified" badge and the only-changed filter.
function computeLtChanged(lt) {
    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    if (ovr) {
        if (ovr.added && ovr.added.length > 0) return true;
        if (ovr.removed && ovr.removed.length > 0) return true;
        if (ovr.entries && Object.keys(ovr.entries).length > 0) return true;
    }
    const mult = getLootGlobalForCategory(lt.category);
    if (mult != null && mult !== 1) {
        // Only counts as "changed" if at least one entry is non-orchestrator
        // with non-zero min/max (otherwise the multiplier no-ops the LT).
        for (const e of lt.entries) {
            const isOrchestrator = !e.lootItemId && !!e.lootTableId;
            if (!isOrchestrator && (e.min !== 0 || e.max !== 0)) return true;
        }
    }
    return false;
}

function computeLtOverridden(lt) {
    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    if (!ovr) return false;
    return (ovr.added && ovr.added.length > 0)
        || (ovr.removed && ovr.removed.length > 0)
        || (ovr.entries && Object.keys(ovr.entries).length > 0);
}

// ---------- Loot: list rendering -----------------------------------------

function filterLootTables() {
    const q   = document.getElementById('lt-filter').value.toLowerCase().trim();
    const fc  = document.getElementById('lt-filter-category').value;
    const ft  = document.getElementById('lt-filter-type').value;
    const chg = document.getElementById('lt-filter-changed').value;

    return state.lootTables.filter(lt => {
        if (q && !ltMatchesQuery(lt, q)) return false;
        if (fc && lt.category !== fc) return false;
        if (ft && lt.type !== ft) return false;
        if (chg === 'changed'    && !computeLtChanged(lt))    return false;
        if (chg === 'overridden' && !computeLtOverridden(lt)) return false;
        return true;
    });
}

// LT search matches the table id itself plus what the user actually sees
// rendered inside the table: each entry's item id + display name (so typing
// "banana" finds every chest/foliage/mob that drops it) and each sub-table
// reference id (so typing a sub-table name finds the orchestrator tables
// pulling it in). Added entries from the current profile are searched too,
// so a freshly-added drop is discoverable without rebuilding the index.
function ltMatchesQuery(lt, q) {
    if (lt.id.toLowerCase().includes(q)) return true;
    for (const e of lt.entries || []) {
        if (entryMatchesQuery(e, q)) return true;
    }
    const ovr = state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id];
    if (ovr && ovr.added) {
        for (const a of ovr.added) {
            if (entryMatchesQuery(a, q)) return true;
        }
    }
    return false;
}

function entryMatchesQuery(e, q) {
    if (e.lootItemId && e.lootItemId.toLowerCase().includes(q)) return true;
    if (e.lootTableId && e.lootTableId.toLowerCase().includes(q)) return true;
    if (e.lootItemId) {
        const item = state.itemsById && state.itemsById.get(e.lootItemId);
        const name = item && item.meta && item.meta.name;
        if (name && name.toLowerCase().includes(q)) return true;
    }
    return false;
}

function renderLootTables() {
    const ul = document.getElementById('lt-list');
    if (!ul) return;
    const filtered = filterLootTables();
    document.getElementById('lt-count').textContent =
        filtered.length + ' / ' + state.lootTables.length + ' tables';

    const frag = document.createDocumentFragment();
    for (const lt of filtered) frag.appendChild(buildLtRow(lt));
    ul.innerHTML = '';
    ul.appendChild(frag);
}

function buildLtRow(lt) {
    const li = document.createElement('li');
    li.className = 'lt';
    if (!state.expandedLts.has(lt.id)) li.classList.add('collapsed');
    li.dataset.ltId = lt.id;
    if (computeLtChanged(lt))    li.classList.add('changed');
    if (computeLtOverridden(lt)) li.classList.add('overridden');

    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    const editCount = ovr && ovr.entries ? Object.keys(ovr.entries).length : 0;
    const remCount  = ovr && ovr.removed ? ovr.removed.length : 0;
    const addCount  = ovr && ovr.added   ? ovr.added.length   : 0;
    const mult      = getLootGlobalForCategory(lt.category);

    const badges = [];
    if (mult != null && mult !== 1) badges.push('×' + mult);
    if (editCount > 0) badges.push('<span class="lt-badge edited">' + editCount + ' edited</span>');
    if (remCount  > 0) badges.push('<span class="lt-badge removed">' + remCount + ' removed</span>');
    if (addCount  > 0) badges.push('<span class="lt-badge added">' + addCount + ' added</span>');

    const headerHtml =
        '<div class="lt-header" data-toggle="' + esc(lt.id) + '">' +
            '<span class="chevron"></span>' +
            '<span class="lt-id">' + esc(lt.id) + '</span>' +
            '<span class="lt-meta">' + esc(lt.type || '') + ' · ' + (lt.entries ? lt.entries.length : 0) + ' entries</span>' +
            '<span class="lt-meta">' + badges.join(' ') + '</span>' +
        '</div>';

    li.innerHTML = headerHtml + '<div class="lt-body"></div>';
    if (state.expandedLts.has(lt.id)) {
        renderLtBody(li, lt);
    }
    return li;
}

// Renders the per-entry editor block inside an expanded LT card. Called on
// expansion, not at initial list render - 1500 LTs * ~5 entries each would
// otherwise blow up the DOM.
function renderLtBody(li, lt) {
    const body = li.querySelector('.lt-body');
    if (!body) return;

    const rows = [];

    // Vanilla entries (with edits/removal markers).
    for (const e of lt.entries) {
        rows.push(buildLtEntryRowHtml(lt, e, false));
    }

    // Added (custom) entries.
    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    if (ovr && ovr.added) {
        for (let i = 0; i < ovr.added.length; i++) {
            rows.push(buildLtAddedRowHtml(lt, ovr.added[i], i, false));
        }
    }

    // Add-entry button.
    rows.push(
        '<div class="lt-add-row">' +
            '<button type="button" class="add-btn" data-add-entry="' + esc(lt.id) + '">+ Add entry</button>' +
        '</div>');

    body.innerHTML = rows.join('');
}

function buildLtEntryRowHtml(lt, e, isReadonly) {
    const r = resolveLootEntry(lt, e);
    const classes = ['lt-entry'];
    if (r.removed) classes.push('removed');
    if (r.edited)  classes.push('edited');

    const isItem  = !!e.lootItemId;
    const isTable = !!e.lootTableId;
    const item    = isItem ? state.itemsById.get(e.lootItemId) : null;
    const targetHtml = buildEntryTargetHtml(e, item);

    const ovr  = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    const edit = (ovr && ovr.entries && ovr.entries[String(e.index)]) || null;
    const minVal    = edit && edit.min    != null ? edit.min    : '';
    const maxVal    = edit && edit.max    != null ? edit.max    : '';
    const weightVal = edit && edit.weight != null ? edit.weight : '';

    const minPh = r.changedByMult ? r.min : e.min;
    const maxPh = r.changedByMult ? r.max : e.max;

    const removeBtn = '<button type="button" class="danger" data-toggle-remove="' + esc(lt.id) + '" data-index="' + e.index + '"' +
        (isReadonly ? ' disabled' : '') + '>' + (r.removed ? 'undo' : 'x') + '</button>';

    return '<div class="' + classes.join(' ') + '" data-lt-id="' + esc(lt.id) + '" data-vanilla-index="' + e.index + '">' +
        targetHtml +
        '<input type="number" class="num" placeholder="' + minPh + '" value="' + esc(minVal) +
            '" data-edit-field="min" data-lt-id="' + esc(lt.id) + '" data-index="' + e.index + '"' +
            (isReadonly || r.removed ? ' disabled' : '') + '>' +
        '<input type="number" class="num" placeholder="' + maxPh + '" value="' + esc(maxVal) +
            '" data-edit-field="max" data-lt-id="' + esc(lt.id) + '" data-index="' + e.index + '"' +
            (isReadonly || r.removed ? ' disabled' : '') + '>' +
        '<input type="number" class="num" placeholder="' + e.weight + '" value="' + esc(weightVal) +
            '" data-edit-field="weight" data-lt-id="' + esc(lt.id) + '" data-index="' + e.index + '"' +
            (isReadonly || r.removed ? ' disabled' : '') + '>' +
        '<span class="vanilla-hint">vanilla ' + e.min + '-' + e.max + (e.weight ? ' w' + e.weight : '') + '</span>' +
        '<div class="row-actions">' + removeBtn + '</div>' +
    '</div>';
}

function buildEntryTargetHtml(e, item) {
    const isItem  = !!e.lootItemId;
    const isTable = !!e.lootTableId;
    if (isItem) {
        const name = (item && item.meta && item.meta.name) || e.lootItemId;
        const iconHtml = item && item.icon
            ? '<img src="' + esc(item.icon) + '" alt="" loading="lazy">'
            : '<div class="placeholder-icon">?</div>';
        return iconHtml +
            '<div class="target">' +
                '<b>' + esc(name) + '</b>' +
                '<small>' + esc(e.lootItemId) + '</small>' +
            '</div>';
    }
    if (isTable) {
        return '<div class="placeholder-icon">▦</div>' +
            '<div class="target subtable">' +
                '<b>' + esc(e.lootTableId) + '</b>' +
                '<small>(sub-table)</small>' +
            '</div>';
    }
    // No-drop slot (typical in Weight tables).
    return '<div class="placeholder-icon">·</div>' +
        '<div class="target"><b>(no drop)</b><small></small></div>';
}

function buildLtAddedRowHtml(lt, addedEntry, addedIndex, isReadonly) {
    const a = addedEntry || {};
    const isItem    = !!(a.lootItem  && a.lootItem  !== 'None');
    const isTable   = !!(a.lootTable && a.lootTable !== 'None');
    // Both fields explicitly 'None' = the user picked "No drop" via the form.
    // Both fields missing = the form hasn't been filled out yet -> show form.
    const isNoDrop  = a.lootItem === 'None' && a.lootTable === 'None';
    const inferredItemId  = isItem  ? lastSegment(a.lootItem)        : null;
    const inferredTableId = isTable ? lootTablePathToId(a.lootTable) : null;
    const item = inferredItemId ? state.itemsById.get(inferredItemId) : null;

    let targetHtml;
    if (isItem) {
        const name = (item && item.meta && item.meta.name) || inferredItemId || '(item)';
        const iconHtml = item && item.icon
            ? '<img src="' + esc(item.icon) + '" alt="" loading="lazy">'
            : '<div class="placeholder-icon">+</div>';
        targetHtml = iconHtml +
            '<div class="target">' +
                '<b>' + esc(name) + '</b>' +
                '<small>' + esc(a.lootItem) + '</small>' +
            '</div>';
    } else if (isTable) {
        targetHtml = '<div class="placeholder-icon">▦</div>' +
            '<div class="target subtable">' +
                '<b>' + esc(inferredTableId || a.lootTable) + '</b>' +
                '<small>(added sub-table)</small>' +
            '</div>';
    } else if (isNoDrop) {
        targetHtml = '<div class="placeholder-icon">·</div>' +
            '<div class="target">' +
                '<b>(no drop)</b>' +
                '<small>added empty slot</small>' +
            '</div>';
    } else {
        // Empty added entry - user clicked "+ Add entry" but hasn't picked
        // a target yet. Render the picker form inline.
        return buildLtAddedFormHtml(lt, a, addedIndex, isReadonly);
    }

    return '<div class="lt-entry added" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '">' +
        targetHtml +
        '<input type="number" class="num" value="' + esc(a.min || 1) +
            '" data-added-field="min" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
            (isReadonly ? ' disabled' : '') + '>' +
        '<input type="number" class="num" value="' + esc(a.max || 1) +
            '" data-added-field="max" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
            (isReadonly ? ' disabled' : '') + '>' +
        '<input type="number" class="num" value="' + esc(a.weight || 0) +
            '" data-added-field="weight" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
            (isReadonly ? ' disabled' : '') + '>' +
        '<span class="vanilla-hint">added</span>' +
        '<div class="row-actions">' +
            '<button type="button" class="danger" data-delete-added="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>x</button>' +
        '</div>' +
    '</div>';
}

// Inline picker form for an added entry whose lootItem/lootTable hasn't been
// selected yet. The user picks Item / Sub-Table / No drop, types the id
// (autocompleted via the custom picker dropdown - see openPicker), and
// confirmAddedEntry resolves the id into the canonical UE asset path.
//
// "No drop" is the engine's term for an empty slot in a Weight table - it
// reserves probability mass for nothing. The picker hides the target input
// since there's nothing to type.
function buildLtAddedFormHtml(lt, a, addedIndex, isReadonly) {
    // Default the form to "Item" mode every time it's rendered. We don't
    // persist mid-form state across re-renders - if the user wanted a
    // sub-table they'll just flip the select.
    return '<div class="lt-entry added" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '">' +
        '<div class="lt-add-form">' +
            '<div class="picker-row">' +
                '<select class="picker-type" data-add-form-type="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                    (isReadonly ? ' disabled' : '') + '>' +
                    '<option value="item">Item</option>' +
                    '<option value="table">Sub-Table</option>' +
                    '<option value="nodrop">No drop</option>' +
                '</select>' +
                '<input type="text" class="picker-target" placeholder="Search items by name or id..."' +
                    ' data-add-form-target="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                    ' data-picker-mode="item"' +
                    ' autocomplete="off" spellcheck="false"' +
                    (isReadonly ? ' disabled' : '') + '>' +
            '</div>' +
            '<input type="number" class="num" value="' + esc(a.min || 1) +
                '" data-added-field="min" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>' +
            '<input type="number" class="num" value="' + esc(a.max || 1) +
                '" data-added-field="max" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>' +
            '<input type="number" class="num" value="' + esc(a.weight || 0) +
                '" data-added-field="weight" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>' +
            '<span class="vanilla-hint">new</span>' +
            '<div class="row-actions">' +
                '<button type="button" class="danger" data-delete-added="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                    (isReadonly ? ' disabled' : '') + '>x</button>' +
            '</div>' +
        '</div>' +
    '</div>';
}

// "/R5BusinessRules/InventoryItems/.../Banana_T01.Banana_T01" -> "Banana_T01"
function lastSegment(s) {
    if (!s) return null;
    const dot = s.lastIndexOf('.');
    const slash = s.lastIndexOf('/');
    const cut = Math.max(dot, slash);
    return cut >= 0 && cut < s.length - 1 ? s.substring(cut + 1) : s;
}

function lootTablePathToId(p) {
    if (!p) return null;
    const PREFIX = '/R5BusinessRules/LootTables/';
    if (!p.startsWith(PREFIX)) return lastSegment(p);
    let s = p.substring(PREFIX.length);
    const dot = s.lastIndexOf('.');
    if (dot < 0) return s;
    return s.substring(0, dot);
}

// ---------- Loot: mutations ---------------------------------------------

function getOrCreateLootOverride(ltId) {
    state.current.lootOverrides = state.current.lootOverrides || {};
    if (!state.current.lootOverrides[ltId]) {
        state.current.lootOverrides[ltId] = { entries: {}, removed: [], added: [] };
    }
    const o = state.current.lootOverrides[ltId];
    o.entries = o.entries || {};
    o.removed = o.removed || [];
    o.added   = o.added   || [];
    return o;
}

function pruneLootOverrideIfEmpty(ltId) {
    const o = state.current.lootOverrides && state.current.lootOverrides[ltId];
    if (!o) return;
    const empty = Object.keys(o.entries || {}).length === 0
        && (!o.removed || o.removed.length === 0)
        && (!o.added   || o.added.length   === 0);
    if (empty) delete state.current.lootOverrides[ltId];
    if (Object.keys(state.current.lootOverrides).length === 0) {
        delete state.current.lootOverrides;
    }
}

function setLootEntryFieldFromInput(ltId, index, field, rawValue) {
    if (!state.current) return;
    const ovr = getOrCreateLootOverride(ltId);
    const key = String(index);
    const cur = ovr.entries[key] || {};
    const trimmed = (rawValue || '').trim();
    if (trimmed === '') {
        delete cur[field];
    } else {
        const n = parseInt(trimmed, 10);
        if (!isFinite(n) || n < 0) return;
        cur[field] = n;
    }
    if (Object.keys(cur).length === 0) {
        delete ovr.entries[key];
    } else {
        ovr.entries[key] = cur;
    }
    pruneLootOverrideIfEmpty(ltId);
    markDirty();
    refreshLtRow(ltId);
    renderLootStatus();
}

function toggleLootEntryRemoved(ltId, index) {
    if (!state.current) return;
    const ovr = getOrCreateLootOverride(ltId);
    const i = ovr.removed.indexOf(index);
    if (i >= 0) ovr.removed.splice(i, 1);
    else        ovr.removed.push(index);
    pruneLootOverrideIfEmpty(ltId);
    markDirty();
    refreshLtRow(ltId);
    renderLootStatus();
}

function addLootEntry(ltId) {
    if (!state.current) return;
    const ovr = getOrCreateLootOverride(ltId);
    // Stub entry; the picker form lets the user pick item-or-table and id.
    ovr.added.push({ min: 1, max: 1, weight: 0 });
    markDirty();
    refreshLtRow(ltId);
    renderLootStatus();
}

function setAddedEntryField(ltId, addedIndex, field, rawValue) {
    if (!state.current) return;
    const ovr = getOrCreateLootOverride(ltId);
    const a = ovr.added[addedIndex];
    if (!a) return;
    const trimmed = (rawValue || '').trim();
    const n = parseInt(trimmed, 10);
    if (!isFinite(n) || n < 0) return;
    a[field] = n;
    markDirty();
    // No re-render: the input is already showing the new value; just bump
    // the status counters.
    renderLootStatus();
}

// Resolves a typed item-id or sub-table-id into its full UE asset path,
// then writes it back into the added entry. Returns false (and shows a
// transient hint) if the id wasn't recognized.
//
// type === 'nodrop' is special: it doesn't need a target - both fields
// are pinned to 'None' and the entry serializes as a plain empty slot.
function confirmAddedEntry(ltId, addedIndex, type, target) {
    if (!state.current) return false;
    const ovr = getOrCreateLootOverride(ltId);
    const a = ovr.added[addedIndex];
    if (!a) return false;

    if (type === 'nodrop') {
        a.lootItem  = 'None';
        a.lootTable = 'None';
        markDirty();
        refreshLtRow(ltId);
        renderLootStatus();
        return true;
    }

    const id = (target || '').trim();
    if (!id) return false;

    if (type === 'item') {
        const path = state.itemPathsByItemId.get(id);
        if (!path) return false;
        a.lootItem = path;
        a.lootTable = 'None';
    } else if (type === 'table') {
        const path = state.tablePathsByLtId.get(id);
        if (!path) return false;
        a.lootTable = path;
        a.lootItem = 'None';
    } else {
        return false;
    }
    markDirty();
    refreshLtRow(ltId);
    renderLootStatus();
    return true;
}

function deleteAddedEntry(ltId, addedIndex) {
    if (!state.current) return;
    const ovr = state.current.lootOverrides && state.current.lootOverrides[ltId];
    if (!ovr || !ovr.added) return;
    ovr.added.splice(addedIndex, 1);
    pruneLootOverrideIfEmpty(ltId);
    markDirty();
    refreshLtRow(ltId);
    renderLootStatus();
}

// ---------- Loot: custom autocomplete picker ----------------------------

// Open the picker under `input`. mode is 'item' or 'table'. The picker is a
// single shared <ul id="picker-dropdown"> in the body; we position it via
// fixed coords next to the input. Closes on outside click, Escape, or scroll.
function openPicker(input, ltId, addedIndex, mode) {
    closePicker();
    state.picker = { input, ltId, addedIndex, type: mode };
    populatePicker(input.value);
    // Unhide BEFORE positioning so getBoundingClientRect() reports real
    // dimensions (the [hidden] CSS rule sets display:none -> rect would be 0).
    document.getElementById('picker-dropdown').hidden = false;
    positionPicker(input);
}

function closePicker() {
    const dd = document.getElementById('picker-dropdown');
    if (dd) {
        dd.hidden = true;
        dd.innerHTML = '';
    }
    state.picker = null;
}

function positionPicker(input) {
    const dd = document.getElementById('picker-dropdown');
    if (!dd || !input) return;
    const rect = input.getBoundingClientRect();
    dd.style.minWidth = Math.max(rect.width, 320) + 'px';

    // Measure required height now that content + minWidth are set. Reset top
    // first so measurement isn't skewed by a previous position.
    dd.style.top = '0px';
    const ddHeight = dd.getBoundingClientRect().height;

    // Flip up if there's not enough room below the input but more above.
    const spaceBelow = window.innerHeight - rect.bottom - 8;
    const spaceAbove = rect.top - 8;
    const flipUp = ddHeight > spaceBelow && spaceAbove > spaceBelow;
    dd.style.top = (flipUp
        ? Math.max(8, rect.top - ddHeight - 2)
        : rect.bottom + 2) + 'px';

    // Horizontal: anchor under the input, then nudge left if it would
    // overshoot the right viewport edge.
    dd.style.left = rect.left + 'px';
    const ddRect = dd.getBoundingClientRect();
    const overshootRight = ddRect.right - window.innerWidth + 8;
    if (overshootRight > 0) {
        dd.style.left = Math.max(8, rect.left - overshootRight) + 'px';
    }
}

// Fills the picker with rows matching `query`, scoped to the active mode.
// Items: only the ones that already appear in some vanilla LT (we know the
// asset path for those). Sub-tables: every LT in vanilla.
function populatePicker(query) {
    const dd = document.getElementById('picker-dropdown');
    if (!dd || !state.picker) return;
    const q = (query || '').toLowerCase().trim();
    const rows = [];

    if (state.picker.type === 'table') {
        for (const lt of state.lootTables) {
            if (q && !lt.id.toLowerCase().includes(q)) continue;
            const subtitle =
                (lt.category || '') +
                (lt.type ? ' · ' + lt.type : '') +
                (lt.entries ? ' · ' + lt.entries.length + ' entries' : '');
            rows.push(
                '<li class="picker-option" data-pick-id="' + esc(lt.id) + '">' +
                    '<div class="placeholder-icon">▦</div>' +
                    '<div class="info">' +
                        '<b>' + esc(lt.id) + '</b>' +
                        '<small>' + esc(subtitle) + '</small>' +
                    '</div>' +
                '</li>');
        }
    } else {
        // Items: iterate state.items (already sorted as the server returned
        // them). Every item has an asset path (server derives it from the
        // on-disk source layout), so we can serialize any pick the user makes.
        for (const item of state.items) {
            if (!state.itemPathsByItemId.has(item.id)) continue; // safety net for items without a derivable path
            const name = (item.meta && item.meta.name) || '';
            if (q && !item.id.toLowerCase().includes(q) && !name.toLowerCase().includes(q)) continue;
            const displayName = name || item.id;
            const subtitle =
                item.id +
                (item.itemClass ? ' · ' + item.itemClass : '') +
                (item.category  ? ' · ' + item.category  : '') +
                (item.rarity   ? ' · ' + item.rarity   : '');
            const iconHtml = item.icon
                ? '<img src="' + esc(item.icon) + '" loading="lazy" alt="">'
                : '<div class="placeholder-icon">?</div>';
            rows.push(
                '<li class="picker-option" data-pick-id="' + esc(item.id) + '">' +
                    iconHtml +
                    '<div class="info">' +
                        '<b>' + esc(displayName) + '</b>' +
                        '<small>' + esc(subtitle) + '</small>' +
                    '</div>' +
                '</li>');
        }
    }

    if (rows.length === 0) {
        dd.innerHTML = '<li class="picker-empty">No matches</li>';
    } else {
        dd.innerHTML = rows.join('');
    }
}

// Reflects the current picker-type select state into the target input:
// switches data-picker-mode, updates placeholder, and (for nodrop) hides
// the input entirely since there's nothing to type. "No drop" auto-confirms
// the entry on selection - there's nothing to pick from a list.
function syncPickerInputToType(selectEl) {
    const wrap = selectEl.closest('.picker-row');
    if (!wrap) return;
    const input = wrap.querySelector('input[data-add-form-target]');
    if (!input) return;
    const mode = selectEl.value;
    input.dataset.pickerMode = mode;
    input.value = '';

    if (mode === 'nodrop') {
        // Auto-confirm: there's no list to pick from. Close any open picker
        // and write through to the override. This re-renders the row, so
        // we don't need to keep updating the (about-to-be-replaced) input.
        closePicker();
        const ltId = input.dataset.addFormTarget;
        const idx  = parseInt(input.dataset.addedIndex, 10);
        confirmAddedEntry(ltId, idx, 'nodrop', '');
        return;
    }

    input.hidden = false;
    input.placeholder = mode === 'table'
        ? 'Search sub-tables by id...'
        : 'Search items by name or id...';

    // If the dropdown is open for this same input, refresh it; otherwise
    // leave it closed (focusin will reopen).
    if (state.picker && state.picker.input === input) {
        populatePicker(input.value);
        positionPicker(input);
    } else {
        closePicker();
    }
}

// Re-renders just one LT card. Used by all loot mutations to avoid blowing
// up the entire 1500-row list.
function refreshLtRow(ltId) {
    const ul = document.getElementById('lt-list');
    const old = ul && ul.querySelector('.lt[data-lt-id="' + cssEsc(ltId) + '"]');
    if (!old) return;
    const lt = state.lootById.get(ltId);
    if (!lt) return;
    const fresh = buildLtRow(lt);
    old.replaceWith(fresh);
}

// ---------- Mutations ----------------------------------------------------

function setStackSizeFromUI() {
    if (!state.current) return;
    const mode = document.querySelector('input[name="ssmode"]:checked').value;
    const mult = parseInt(document.getElementById('ss-mult').value, 10);
    const cap  = parseInt(document.getElementById('ss-cap').value,  10);
    const abs  = parseInt(document.getElementById('ss-abs').value,  10);

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
    syncStackSizeInputsState();
    markDirty();
    renderStatus();
    renderItems();
}

// Pickup-radius lives in globals.pickupRadius.multiplier. Off (= unchecked
// OR slider at 1.0) drops the whole subtree so the JSON stays clean ({} on
// disk for profiles that don't touch pickup). Called from the checkbox
// (toggle) and the slider (live update) so the readout always matches state.
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

// Fast-travel bell + signal-fire caps. Both vanilla (10 / 3) drops the
// whole subtree so the JSON stays clean for default profiles. Either
// value differing from vanilla writes both fields together (so a future
// game-update change to one cap doesn't silently shift the other).
function setBellLimitsFromUI() {
    if (!state.current) return;
    const bellRaw   = document.getElementById('bell-cap').value;
    const signalRaw = document.getElementById('signal-fire-cap').value;
    const bell   = parseInt(bellRaw,   10);
    const signal = parseInt(signalRaw, 10);
    if (!isFinite(bell) || !isFinite(signal)) return;  // mid-edit garbage

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

// Building-stability single toggle. Off drops the whole subtree (clean
// JSON for default profiles); on writes { enabled: true }. Mirrors the
// pickup pattern of "default = no key in JSON at all".
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

// Minimap-range checkbox+slider. Off OR multiplier ~= 1.0 -> drop the
// whole subtree (same null-collapse pattern as pickup-radius). On -> write
// { multiplier: <slider value> }.
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

// Bonfire-radius checkbox+slider. Same null-collapse semantics as
// minimap / pickup-radius - 1.0 or off drops the whole subtree, on
// writes { multiplier: <slider value> }.
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

// No-Smoke per-category toggles. All three off -> drop the whole noSmoke
// subtree (clean JSON, mirrors the build pipeline's "no source contributes"
// semantics). At least one on -> keep only the active flags as true; off
// flags are omitted (null in the model = treated as false everywhere).
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

function setOverrideFromInput(itemId, rawValue) {
    if (!state.current) return;
    state.current.overrides = state.current.overrides || {};
    const trimmed = (rawValue || '').trim();
    if (trimmed === '') {
        delete state.current.overrides[itemId];
    } else {
        const n = parseInt(trimmed, 10);
        if (!isFinite(n) || n < 0) return;  // ignore garbage
        state.current.overrides[itemId] = { stackSize: n };
    }
    markDirty();
    renderStatus();
    refreshRowInPlace(itemId);
}

function markDirty() {
    state.isDirty = true;
    updateButtons();
    renderProfileMeta();
}

function updateButtons() {
    const p = state.current;
    document.getElementById('btn-save').disabled       = !p || !state.isDirty;
    document.getElementById('btn-rename').disabled     = !p;
    document.getElementById('btn-delete').disabled     = !p;
    document.getElementById('btn-build').disabled      = !p;
    document.getElementById('btn-duplicate').disabled  = !p;
}

// ---------- Buttons ------------------------------------------------------

async function onSave() {
    const p = state.current;
    if (!p) return;
    const body = {
        id: p.id, name: p.name, description: p.description,
        createdAt: p.createdAt,
        globals: p.globals, overrides: p.overrides,
        lootOverrides: p.lootOverrides,
    };
    const updated = await api('PUT', '/api/profiles/' + encodeURIComponent(p.id), body);
    state.current = updated;
    state.current.globals       = state.current.globals       || {};
    state.current.overrides     = state.current.overrides     || {};
    state.current.lootOverrides = state.current.lootOverrides || {};
    state.isDirty = false;
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    document.getElementById('profile-select').value = p.id;
    renderProfileMeta();
    updateButtons();
}

async function onNew() {
    const name = prompt('New profile name?', 'My Stacks');
    if (!name) return;
    const created = await api('POST', '/api/profiles', {
        name,
        description: '',
        globals: {},
        overrides: {},
        lootOverrides: {},
    });
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    await loadProfile(created.id);
}

async function onDuplicate() {
    if (!state.current) return;
    const created = await api('POST',
        '/api/profiles/' + encodeURIComponent(state.current.id) + '/duplicate');
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    await loadProfile(created.id);
}

function onRename() {
    if (!state.current) return;
    const newName = prompt('New name?', state.current.name);
    if (!newName || newName === state.current.name) return;
    state.current.name = newName;
    markDirty();
    populateProfileSelect();  // dropdown text needs refresh
    document.getElementById('profile-select').value = state.current.id;
}

async function onDelete() {
    if (!state.current) return;
    if (!confirm('Delete profile "' + state.current.name + '"?')) return;
    await api('DELETE', '/api/profiles/' + encodeURIComponent(state.current.id));
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    if (state.profiles.length > 0) {
        await loadProfile(state.profiles[0].id);
    } else {
        state.current = null;
        renderItems();
        renderStatus();
        if (state.activeTab === 'loot') {
            renderLootGlobals();
            renderLootTables();
            renderLootStatus();
        }
        updateButtons();
        renderProfileMeta();
    }
}

async function onBuild() {
    if (!state.current) return;
    if (state.isDirty) {
        if (confirm('Save unsaved changes before building?')) {
            await onSave();
        }
    }
    setFooterCollapsed(false);  // build always opens the log so output is visible
    setBuildLog([{ kind: 'info', msg: 'Building (this may take a few seconds)...' }]);
    document.getElementById('btn-build').disabled = true;

    try {
        const r = await fetch('/api/build', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ profileId: state.current.id }),
        });
        const data = await r.json();
        const lines = [];
        for (const m of data.log || []) lines.push({ kind: 'ok', msg: m });
        if (data.success) {
            // Main pak is missing (pakPath==null) only on a pickup-only
            // build - collapse to a single "DONE" line in either case.
            if (data.pakPath) {
                const sizeKb = (data.sizeBytes / 1024).toFixed(1);
                lines.push({ kind: 'ok', msg:
                    'DONE - ' + data.pakPath + ' (' + sizeKb + ' KB, ' + data.fileCount + ' files)' });
            }
            if (data.pickupRadius) {
                const pr = data.pickupRadius;
                // When the main pak is also built, pickup's pakPath is
                // null (the main pak overwrites the stub at the same
                // basename); only the .ucas/.utoc are uniquely pickup.
                // Pickup-only builds report a real pakPath.
                const totalKb = ((pr.pakSize + pr.ucasSize + pr.utocSize) / 1024).toFixed(1);
                const target = pr.pakPath || pr.ucasPath;
                lines.push({ kind: 'ok', msg:
                    'DONE - pickup-radius patch (' + (pr.multiplier || '?').toFixed(1) + 'x, '
                    + 'MagnetRadius=' + pr.magnetRadius + ', ' + totalKb + ' KB) -> '
                    + target });
            }
            if (data.bellLimits && data.bellLimits.written) {
                const bl = data.bellLimits;
                lines.push({ kind: 'ok', msg:
                    'DONE - fast-travel limits patched (bells=' + bl.bellCap
                    + ', signal-fires=' + bl.signalFireCap + '; '
                    + bl.bellsPatched + ' bell + ' + bl.signalFiresPatched
                    + ' signal-fire entries)' });
            }
            if (data.buildingStability && data.buildingStability.enabled) {
                lines.push({ kind: 'ok', msg:
                    'DONE - enhanced building stability bundled (787 DA_BI* assets)' });
            }
            if (data.noSmoke) {
                const ns = data.noSmoke;
                const cats = (ns.categories || []).join(', ') || '?';
                lines.push({ kind: 'ok', msg:
                    'DONE - no-smoke patched (' + cats + '; '
                    + ns.assetCount + ' assets, '
                    + ns.flippedHandles + ' emitter handles silenced)' });
            }
            if (data.minimapRange) {
                const mr = data.minimapRange;
                const mul = (mr.multiplier || 1.0).toFixed(1);
                lines.push({ kind: 'ok', msg:
                    'DONE - minimap range patched (' + mul + 'x; foot '
                    + mr.vanilla.footBrush + '/' + mr.vanilla.footDistance + ' -> '
                    + mr.effective.footBrush + '/' + mr.effective.footDistance
                    + ', ship ' + mr.vanilla.shipBrush + '/' + mr.vanilla.shipDistance + ' -> '
                    + mr.effective.shipBrush + '/' + mr.effective.shipDistance + ')' });
            }
            if (data.bonfireRadius) {
                const bo = data.bonfireRadius;
                const mul = (bo.multiplier || 1.0).toFixed(1);
                lines.push({ kind: 'ok', msg:
                    'DONE - bonfire radius patched (' + mul + 'x; influence '
                    + bo.vanilla.influenceRadius + '/' + bo.vanilla.influenceHeight
                    + ' -> ' + bo.effective.influenceRadius + '/' + bo.effective.influenceHeight
                    + ' cm)' });
            }
            if (!data.pakPath && !data.pickupRadius && !data.buildingStability
                && !data.noSmoke && !data.minimapRange && !data.bonfireRadius) {
                lines.push({ kind: 'err', msg: 'WARNING: build reported success but produced no output paks.' });
            }
        } else {
            lines.push({ kind: 'err', msg: 'ERROR: ' + (data.error || 'unknown') });
        }
        setBuildLog(lines);
        // The pak just landed in (or got overwritten in) ~mods/ - refresh
        // the snapshot so the Mods tab reflects reality next time it's
        // opened (or right now, if it's already the active tab).
        if (data.success) {
            await loadMods();
        }
    } catch (e) {
        setBuildLog([{ kind: 'err', msg: 'NETWORK ERROR: ' + e.message }]);
    } finally {
        updateButtons();
    }
}

function setBuildLog(lines) {
    const out = document.getElementById('build-log');
    out.innerHTML = lines.map(l =>
        '<span class="' + l.kind + '">[' + l.kind.toUpperCase() + ']</span> ' + esc(l.msg)
    ).join('\n');
}

// ---------- Mods page ----------------------------------------------------

async function loadMods() {
    const dirEl = document.getElementById('mods-dir');
    dirEl.textContent = 'Loading...';
    try {
        const r = await fetch('/api/mods');
        const data = await r.json();
        if (!r.ok || data.error) {
            state.mods.loaded = true;
            state.mods.modsDir = data && data.modsDir ? data.modsDir : null;
            state.mods.files = [];
            state.mods.error = (data && data.error) || ('HTTP ' + r.status);
        } else {
            state.mods.loaded = true;
            state.mods.modsDir = data.modsDir;
            state.mods.files = data.files || [];
            state.mods.error = null;
        }
    } catch (e) {
        state.mods.loaded = true;
        state.mods.error = 'Network error: ' + e.message;
        state.mods.files = [];
        state.mods.modsDir = null;
    }
    renderMods();
    renderModsStatus();
}

function renderMods() {
    const dirEl = document.getElementById('mods-dir');
    dirEl.textContent = state.mods.modsDir || '(unknown)';

    const errEl = document.getElementById('mods-error');
    if (state.mods.error) {
        errEl.textContent = state.mods.error;
        errEl.hidden = false;
    } else {
        errEl.hidden = true;
    }

    const list  = document.getElementById('mods-list');
    const filtered = filterMods();

    if (filtered.length === 0) {
        const msg = state.mods.files.length === 0
            ? 'No .pak files in this folder yet. Build a profile to drop one here.'
            : 'No mods match the current filter.';
        list.innerHTML = '<li class="mods-empty">' + esc(msg) + '</li>';
    } else {
        list.innerHTML = filtered.map(buildModRowHtml).join('');
    }

    document.getElementById('mods-count').textContent =
        filtered.length + ' / ' + state.mods.files.length + ' mods';
}

function filterMods() {
    const q = (document.getElementById('mods-filter').value || '').trim().toLowerCase();
    const src = document.getElementById('mods-filter-source').value;
    const out = [];
    for (const f of state.mods.files) {
        if (src === 'owned'   && !f.isQuartermaster) continue;
        if (src === 'foreign' &&  f.isQuartermaster) continue;
        if (q) {
            const hay = (f.filename + ' ' + (f.displayName || '')).toLowerCase();
            if (!hay.includes(q)) continue;
        }
        out.push(f);
    }
    return out;
}

function buildModRowHtml(f) {
    const cls = f.isQuartermaster ? 'mod owned' : 'mod foreign';
    const marker = f.isQuartermaster ? 'Q' : '*';
    const sizeKb = (f.sizeBytes / 1024).toFixed(1);
    const when = formatModifiedDate(f.modifiedUtc);
    const nameBlock = f.isQuartermaster
        ? ('<span class="display">' + esc(f.displayName || f.filename) + '</span>'
           + '<span class="filename">' + esc(f.filename) + '</span>')
        : ('<span class="filename">' + esc(f.filename) + '</span>');
    const actions = f.isQuartermaster
        ? '<button type="button" class="danger" data-delete-mod="' + esc(f.filename) + '" title="Move to recycle bin">Delete</button>'
        : '<span class="lock" title="Foreign mod - managed externally">read-only</span>';
    return '<li class="' + cls + '">'
         +   '<span class="mod-marker" title="' + (f.isQuartermaster ? 'Built by Quartermaster' : 'External mod') + '">' + marker + '</span>'
         +   '<div class="mod-name">' + nameBlock + '</div>'
         +   '<div class="mod-meta"><span>' + esc(sizeKb) + ' KB</span><span>' + esc(when) + '</span></div>'
         +   '<div class="mod-actions">' + actions + '</div>'
         + '</li>';
}

function formatModifiedDate(iso) {
    if (!iso) return '';
    try {
        const d = new Date(iso);
        if (isNaN(d.getTime())) return '';
        // Locale-sensitive but compact: "2026-05-09 17:42"
        const pad = n => String(n).padStart(2, '0');
        return d.getFullYear() + '-' + pad(d.getMonth() + 1) + '-' + pad(d.getDate())
             + ' ' + pad(d.getHours()) + ':' + pad(d.getMinutes());
    } catch (e) {
        return '';
    }
}

function renderModsStatus() {
    const total   = state.mods.files.length;
    const owned   = state.mods.files.filter(f => f.isQuartermaster).length;
    const foreign = total - owned;
    document.getElementById('mods-stat-total').textContent   = total;
    document.getElementById('mods-stat-owned').textContent   = owned;
    document.getElementById('mods-stat-foreign').textContent = foreign;
}

async function deleteMod(filename) {
    // Server-side guard already refuses non-Quartermaster files, but the
    // client shouldn't even ask. Also: we lean on the recycle bin for
    // recoverability (per /api/mods DELETE), so the confirm prompt
    // explicitly says "recycle bin" rather than "delete".
    const file = state.mods.files.find(f => f.filename === filename);
    if (!file || !file.isQuartermaster) return;
    if (!confirm('Move "' + filename + '" to the recycle bin?')) return;
    try {
        const r = await fetch('/api/mods/' + encodeURIComponent(filename), { method: 'DELETE' });
        const data = await r.json();
        if (!r.ok || !data.success) {
            alert('Delete failed: ' + (data.error || ('HTTP ' + r.status)));
            return;
        }
    } catch (e) {
        alert('Network error: ' + e.message);
        return;
    }
    await loadMods();
}

function setFooterCollapsed(collapsed) {
    const footer = document.getElementById('footer');
    const btn    = document.getElementById('footer-toggle');
    footer.classList.toggle('collapsed', collapsed);
    btn.setAttribute('aria-expanded', String(!collapsed));
}

// ---------- Bindings -----------------------------------------------------

function bindHandlers() {
    document.getElementById('profile-select').addEventListener('change', e => {
        if (state.isDirty) {
            if (!confirm('Discard unsaved changes?')) {
                e.target.value = state.current.id;
                return;
            }
        }
        loadProfile(e.target.value);
    });
    document.getElementById('btn-new').addEventListener('click',       onNew);
    // Empty-state mirror of "+ New" - shown only while body.no-profiles is
    // active. Reuses onNew so the prompt + create flow stays single-sourced.
    document.getElementById('btn-no-profile-create').addEventListener('click', onNew);
    document.getElementById('btn-duplicate').addEventListener('click', onDuplicate);
    document.getElementById('btn-rename').addEventListener('click',    onRename);
    document.getElementById('btn-save').addEventListener('click',      onSave);
    document.getElementById('btn-delete').addEventListener('click',    onDelete);
    document.getElementById('btn-build').addEventListener('click',     onBuild);

    document.getElementById('footer-toggle').addEventListener('click', () => {
        const isCollapsed = document.getElementById('footer').classList.contains('collapsed');
        setFooterCollapsed(!isCollapsed);
    });

    for (const r of document.querySelectorAll('input[name="ssmode"]')) {
        r.addEventListener('change', setStackSizeFromUI);
    }
    document.getElementById('ss-mult').addEventListener('input', setStackSizeFromUI);
    document.getElementById('ss-cap').addEventListener('input',  setStackSizeFromUI);
    document.getElementById('ss-abs').addEventListener('input',  setStackSizeFromUI);
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

    document.getElementById('item-filter').addEventListener('input',     renderItems);
    document.getElementById('filter-class').addEventListener('change',   renderItems);
    document.getElementById('filter-rarity').addEventListener('change',  renderItems);
    document.getElementById('filter-changed').addEventListener('change', renderItems);

    // Override-input changes are delegated from the list root.
    document.getElementById('item-list').addEventListener('input', e => {
        if (e.target.classList && e.target.classList.contains('override-input')) {
            setOverrideFromInput(e.target.dataset.itemId, e.target.value);
        }
    });

    // Tabs.
    for (const b of document.querySelectorAll('.tab')) {
        b.addEventListener('click', () => setActiveTab(b.dataset.tab));
    }

    // Loot-globals: per-category multiplier + reset.
    document.getElementById('loot-globals').addEventListener('input', e => {
        const cat = e.target.dataset && e.target.dataset.lootCat;
        if (cat) setLootGlobalFromInput(cat, e.target.value);
    });
    document.getElementById('loot-globals').addEventListener('click', e => {
        const cat = e.target.dataset && e.target.dataset.resetCat;
        if (cat) resetLootGlobalCategory(cat);
    });

    // Loot filters.
    document.getElementById('lt-filter').addEventListener('input',           renderLootTables);
    document.getElementById('lt-filter-category').addEventListener('change', renderLootTables);
    document.getElementById('lt-filter-type').addEventListener('change',     renderLootTables);
    document.getElementById('lt-filter-changed').addEventListener('change',  renderLootTables);

    // Mods page: filter inputs re-render the cached list locally; the
    // refresh button re-fetches from the server. Delete is delegated from
    // the list root so we don't have to re-bind on each render.
    document.getElementById('mods-filter').addEventListener('input',          renderMods);
    document.getElementById('mods-filter-source').addEventListener('change',  renderMods);
    document.getElementById('mods-refresh').addEventListener('click',         loadMods);
    document.getElementById('mods-list').addEventListener('click', e => {
        const t = e.target;
        if (t && t.dataset && t.dataset.deleteMod) {
            deleteMod(t.dataset.deleteMod);
        }
    });

    // Delegated handlers on the LT list - one set covers expand/collapse,
    // per-entry edits, removals, and add-form interactions.
    const ltList = document.getElementById('lt-list');
    ltList.addEventListener('click',   onLtListClick);
    ltList.addEventListener('input',   onLtListInput);
    ltList.addEventListener('change',  onLtListChange);
    // focusin (vs focus) bubbles, so we can delegate from the list root.
    ltList.addEventListener('focusin', onLtListFocusIn);

    // Picker dropdown: pick on click, close on outside-click / scroll / Escape.
    document.getElementById('picker-dropdown').addEventListener('mousedown', onPickerClick);
    document.addEventListener('click',  onDocClickClosePicker);
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') closePicker();
    });
    // Re-position when the LT list scrolls. Cheap (only runs while open
    // since positionPicker no-ops on null state).
    ltList.addEventListener('scroll', () => {
        if (state.picker) positionPicker(state.picker.input);
    }, { passive: true });
    window.addEventListener('resize', () => {
        if (state.picker) positionPicker(state.picker.input);
    });
}

function onLtListClick(e) {
    const t = e.target;
    if (!t || !t.dataset) return;

    // Expand / collapse on header click (but not when clicking the input/button).
    if (t.closest && t.closest('.lt-header') && !t.matches('input, button, select')) {
        const header = t.closest('.lt-header');
        const ltId = header.dataset.toggle;
        if (state.expandedLts.has(ltId)) state.expandedLts.delete(ltId);
        else                              state.expandedLts.add(ltId);
        refreshLtRow(ltId);
        return;
    }

    // Toggle remove on a vanilla entry.
    if (t.dataset.toggleRemove) {
        toggleLootEntryRemoved(t.dataset.toggleRemove, parseInt(t.dataset.index, 10));
        return;
    }

    // Add a stub entry (renders the picker form).
    if (t.dataset.addEntry) {
        addLootEntry(t.dataset.addEntry);
        return;
    }

    // Delete an added entry outright.
    if (t.dataset.deleteAdded) {
        deleteAddedEntry(t.dataset.deleteAdded, parseInt(t.dataset.addedIndex, 10));
        return;
    }
}

function onLtListInput(e) {
    const t = e.target;
    if (!t || !t.dataset) return;

    if (t.dataset.editField) {
        setLootEntryFieldFromInput(
            t.dataset.ltId,
            parseInt(t.dataset.index, 10),
            t.dataset.editField,
            t.value);
        return;
    }
    if (t.dataset.addedField) {
        setAddedEntryField(
            t.dataset.ltId,
            parseInt(t.dataset.addedIndex, 10),
            t.dataset.addedField,
            t.value);
        return;
    }
    // Picker-target input: refresh the autocomplete dropdown live.
    if (t.dataset.addFormTarget && state.picker && state.picker.input === t) {
        populatePicker(t.value);
        // Content height likely changed -> re-evaluate flip-up / overflow.
        positionPicker(t);
    }
}

// Picker-type select changed -> sync the target input (placeholder, mode, hide).
function onLtListChange(e) {
    const t = e.target;
    if (t && t.dataset && t.dataset.addFormType) {
        syncPickerInputToType(t);
    }
}

// Focusing a picker-target input opens the autocomplete dropdown.
function onLtListFocusIn(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.addFormTarget) return;
    const mode = t.dataset.pickerMode || 'item';
    if (mode === 'nodrop') return;  // no input visible
    const ltId = t.dataset.addFormTarget;
    const idx  = parseInt(t.dataset.addedIndex, 10);
    openPicker(t, ltId, idx, mode);
}

// Click on a row in the picker dropdown -> resolve the id directly into the
// canonical UE asset path and persist it. No intermediate "set" step: pick is
// confirm. We use mousedown (not click) so we fire BEFORE the input loses
// focus to the document-level click handler that would otherwise close the
// picker.
function onPickerClick(e) {
    const li = e.target.closest && e.target.closest('.picker-option');
    if (!li || !li.dataset.pickId) return;
    if (!state.picker) return;
    e.preventDefault();
    const { ltId, addedIndex, type } = state.picker;
    closePicker();  // close before confirm; confirm re-renders the row
    confirmAddedEntry(ltId, addedIndex, type, li.dataset.pickId);
}

// Any click outside the picker or its anchor input closes the dropdown.
function onDocClickClosePicker(e) {
    if (!state.picker) return;
    const dd = document.getElementById('picker-dropdown');
    if (dd && dd.contains(e.target)) return;
    if (e.target === state.picker.input) return;
    closePicker();
}
