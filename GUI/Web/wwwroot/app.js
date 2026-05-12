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
    activeTab: 'misc',

    // Windrose ~mods/ folder snapshot (lazy-loaded the first time the
    // Mods tab is shown, then refreshed after every successful build and
    // on explicit user action via the Refresh button).
    mods: {
        loaded: false,
        modsDir: null,
        files: [],     // [{filename, sizeBytes, modifiedUtc, isQuartermaster, displayName}]
        error: null,   // human-readable error string from the GET, or null
    },

    // Vanilla buyer rosters (lazy-loaded the first time the Buyers tab is
    // shown). Each item: { id, faction, label, slot, entries:[...] }.
    // Phase 1 is read-only; we cache once and never refetch on tab switch.
    buyers: {
        loaded: false,
        list: [],
        error: null,
    },

    // Vanilla seller (vendor) rosters - same shape as buyers, parsed from
    // the PlayerBuys RecipeLists. Independent lazy-load + cache so opening
    // one tab doesn't pull data for the other.
    sellers: {
        loaded: false,
        list: [],
        error: null,
    },

    // Currently-open picker dropdown. Shared between the Loot tab (add-entry
    // autocomplete) and the Buyers tab (sold-item / pay-item inputs). The
    // `source` discriminator picks which dispatcher onPickerClick uses; the
    // other fields are source-specific:
    //   loot:  { input, source:'loot',  type:'item'|'table'|'nodrop', ltId, addedIndex }
    //   buyer: { input, source:'buyer', type:'item', buyerId, recipeId, buyerField }
    // null when no picker is open.
    picker: null,
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
    // Skip the [KIND] prefix when the upstream line already carries its own
    // bracket marker (e.g. IconExtractor emits "[OK] Oodle DLL: ..."), so we
    // don't end up with "[OK] [OK] ...". Step markers ([step:start ...]) use
    // a different prefix style and stay as-is for visual scanability.
    const hasOwnPrefix = /^\[(?:OK|ok|X|!|\.\.|skip)\b/.test(line);
    const prefix = kind && !hasOwnPrefix ? '[' + kind.toUpperCase() + '] ' : '';
    span.textContent = prefix + line + '\n';
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
    document.getElementById('setup-close').addEventListener('click', () => {
        // Manual re-open: app data is already loaded, just dismiss.
        hideSetupOverlay();
        resetSetupButtons();
    });
}

// Reset all setup-action button visibility to the first-run defaults so a
// second manual open doesn't inherit leftover state from a previous run.
function resetSetupButtons() {
    document.getElementById('setup-run').hidden      = false;
    document.getElementById('setup-continue').hidden = true;
    document.getElementById('setup-force').hidden    = true;
    document.getElementById('setup-close').hidden    = true;
    clearSetupLog();
}

// Manually re-open the setup overlay (from the Mods tab "Open setup" button).
// Differs from the first-run path: app data is already loaded, so we offer
// a Close button to dismiss without re-running anything.
async function openSetupManually() {
    resetSetupButtons();
    try {
        const status = await api('GET', '/api/setup/status');
        showSetupOverlay(status);
    } catch (err) {
        // Fall back to a synthetic "everything failed" status so the user
        // at least sees the dialog and the error context.
        showSetupOverlay({
            isReady: false,
            hasVanillaPak: false,
            vanillaPakError: err.message,
        });
    }
    // Manual mode: always offer a Close so the user isn't trapped if the
    // mod root is already healthy and they don't want to re-run anything.
    document.getElementById('setup-close').hidden = false;
    // Re-run is also useful from a manual open (e.g. after a game update);
    // surface it without waiting for a failed run to reveal it.
    document.getElementById('setup-force').hidden = false;
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
    if (tab === 'buyers') {
        // Same lazy-load pattern as the Mods tab: first view fetches,
        // subsequent views just re-render from the cached state.
        if (!state.buyers.loaded) {
            loadBuyers();
        } else {
            renderBuyers();
            renderBuyersStatus();
        }
    }
    if (tab === 'sellers') {
        // Same lazy-load pattern as Buyers / Mods.
        if (!state.sellers.loaded) {
            loadSellers();
        } else {
            renderSellers();
            renderSellersStatus();
        }
    }
}

// ---------- Profile loading ---------------------------------------------

async function loadProfile(id) {
    state.current = await api('GET', '/api/profiles/' + encodeURIComponent(id));
    state.current.globals       = state.current.globals || {};
    state.current.overrides     = state.current.overrides || {};
    state.current.lootOverrides = state.current.lootOverrides || {};
    // Buyers tab CRUD state: per-recipe edits (global - same recipe id
    // patched everywhere it's referenced) + per-list add/remove of recipe
    // refs. Initialised to {} so the renderers don't have to null-check.
    state.current.buyerRecipes  = state.current.buyerRecipes  || {};
    state.current.buyerLists    = state.current.buyerLists    || {};
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
    if (state.activeTab === 'buyers' && state.buyers.loaded) {
        renderBuyers();
        renderBuyersStatus();
    }
    updateButtons();
    setBuildLog([{ kind: 'info', msg: 'Profile loaded: ' + state.current.name }]);
}

function applyProfileToUI() {
    const p = state.current;
    syncStackSizeUIFromState();
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

// The stack-size controls exist twice in the DOM: once on the Items tab
// (the original "left rail" fieldset) and once as a card on the Misc tab.
// Both edit the same globals.stackSize - we keep them in sync by writing
// to both whenever state changes, and by reading from whichever set fired
// the event. Each entry maps the four bits we need (radio group name, the
// three number inputs).
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
    // Mode is the same on both sets (writes are mirrored), so read from the
    // primary set and fan out the disabled flags to all sets.
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
    document.getElementById('minimap-multiplier-value').innerHTML = mul.toFixed(1) + 'x<!--&times;-->';
    const footDist  = 25 * mul / 10;
    const shipDist  = 75 * mul / 10;
    document.getElementById('minimap-foot-readout').textContent = footDist.toFixed(1) + ' m';
    document.getElementById('minimap-ship-readout').textContent = shipDist.toFixed(1) + ' m';
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
    document.getElementById('bonfire-multiplier-value').innerHTML = mul.toFixed(1) + 'x<!--&times;-->';
    document.getElementById('bonfire-radius-readout').textContent = (mul * 50).toFixed(0) + ' m'
    document.getElementById('bonfire-height-readout').textContent = (mul * 30).toFixed(0) + ' m'
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
    state.picker = { input, source: 'loot', type: mode, ltId, addedIndex };
    populatePicker(input.value);
    // Unhide BEFORE positioning so getBoundingClientRect() reports real
    // dimensions (the [hidden] CSS rule sets display:none -> rect would be 0).
    document.getElementById('picker-dropdown').hidden = false;
    positionPicker(input);
}

// Buyer-tab variant. Same dropdown, different payload: we remember which
// recipe + field the user is editing so the dispatcher in onPickerClick can
// route the chosen id back through setBuyerEntryField. The picker only does
// items for buyers (no sub-tables / nodrop), so `type` is hardcoded to 'item'.
//
// Opens with an empty query so the user sees the full item catalog on first
// focus - then types to filter. Pre-filling from input.value would only
// match the one currently-selected item, which defeats the picker. The
// input value itself stays as-is on screen but is selected so the first
// keystroke replaces it cleanly.
function openBuyerPicker(input, buyerId, recipeId, buyerField) {
    closePicker();
    state.picker = { input, source: 'buyer', type: 'item', buyerId, recipeId, buyerField };
    populatePicker('');
    document.getElementById('picker-dropdown').hidden = false;
    positionPicker(input);
    if (input.value) {
        // Select all so typing replaces. Wrap in try/catch because some
        // browsers throw on input.select() during the focus event sequence.
        try { input.select(); } catch (_) { /* ignore */ }
    }
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

// `srcEvt` is the change/input event from whichever set the user touched.
// We pick that set as the source of truth, write to state, and then mirror
// the canonical state back to all sets so the twin UI stays in sync. If
// called without an event (defensive paths), we fall back to the primary
// set so behaviour matches the pre-mirror version.
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
        buyerRecipes: p.buyerRecipes,
        buyerLists: p.buyerLists,
    };
    const updated = await api('PUT', '/api/profiles/' + encodeURIComponent(p.id), body);
    state.current = updated;
    state.current.globals       = state.current.globals       || {};
    state.current.overrides     = state.current.overrides     || {};
    state.current.lootOverrides = state.current.lootOverrides || {};
    state.current.buyerRecipes  = state.current.buyerRecipes  || {};
    state.current.buyerLists    = state.current.buyerLists    || {};
    state.isDirty = false;
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    document.getElementById('profile-select').value = p.id;
    renderProfileMeta();
    updateButtons();
}

async function onNew() {
    const name = await prompt('New profile name?', 'My Profile');
    if (!name) return;
    const created = await api('POST', '/api/profiles', {
        name,
        description: '',
        globals: {},
        overrides: {},
        lootOverrides: {},
        buyerRecipes: {},
        buyerLists: {},
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

async function onRename() {
    if (!state.current) return;
    const newName = await prompt('New name?', state.current.name);
    if (!newName || newName === state.current.name) return;
    state.current.name = newName;
    markDirty();
    populateProfileSelect();  // dropdown text needs refresh
    document.getElementById('profile-select').value = state.current.id;
}

async function onDelete() {
    if (!state.current) return;
    if (!await confirm('Delete profile "' + state.current.name + '"?')) return;
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
        if (await confirm('Save unsaved changes before building?')) {
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
    if (!await confirm('Move "' + filename + '" to the recycle bin?')) return;
    try {
        const r = await fetch('/api/mods/' + encodeURIComponent(filename), { method: 'DELETE' });
        const data = await r.json();
        if (!r.ok || !data.success) {
            await alert('Delete failed: ' + (data.error || ('HTTP ' + r.status)));
            return;
        }
    } catch (e) {
        await alert('Network error: ' + e.message);
        return;
    }
    await loadMods();
}

// ---------- Buyers tab (read-only PlayerSells roster browser) ------------
//
// /api/buyers returns a list of BuyerDto entries. Each entry is one
// R5BLRecipeList JSON whose filename matches *_PlayerSells* in vanilla
// (8 such lists, 2 per Trade* faction). Phase 1 is purely display:
// item id, count, what the NPC pays in (typically piastres), and the
// reputation gate if any. Editing follows in a later iteration.

async function loadBuyers() {
    try {
        const data = await api('GET', '/api/buyers');
        state.buyers.loaded = true;
        state.buyers.list = data || [];
        state.buyers.error = null;
        populateBuyerFactionFilter();
    } catch (e) {
        state.buyers.loaded = true;
        state.buyers.list = [];
        state.buyers.error = 'Failed to load buyers: ' + e.message;
    }
    renderBuyers();
    renderBuyersStatus();
}

// Build the faction-filter <select> from whatever factions the current
// dataset surfaces, so a future addition (e.g. modded Tortuga faction) is
// picked up automatically without touching the HTML.
function populateBuyerFactionFilter() {
    const sel = document.getElementById('buyers-filter-faction');
    if (!sel) return;
    const seen = new Set();
    for (const b of state.buyers.list) {
        if (b.faction) seen.add(b.faction);
    }
    const factions = Array.from(seen).sort();
    // Preserve current selection across re-population.
    const prev = sel.value;
    sel.innerHTML = '<option value="">All factions</option>'
        + factions.map(f => '<option value="' + esc(f) + '">' + esc(f) + '</option>').join('');
    if (prev && factions.includes(prev)) sel.value = prev;
}

function filterBuyers() {
    const q = (document.getElementById('buyers-filter').value || '').trim().toLowerCase();
    const faction = document.getElementById('buyers-filter-faction').value;
    const out = [];
    for (const b of state.buyers.list) {
        if (faction && b.faction !== faction) continue;
        if (q) {
            // Match on label / id / any entry's item or recipe id so users
            // can find "who buys Rum Bottles?" by typing the item id.
            let hay = (b.label + ' ' + b.id + ' ' + b.faction).toLowerCase();
            for (const e of b.entries) {
                hay += ' ' + (e.itemId || '') + ' ' + (e.recipeId || '');
            }
            if (!hay.includes(q)) continue;
        }
        out.push(b);
    }
    return out;
}

function renderBuyers() {
    const errEl = document.getElementById('buyers-error');
    if (state.buyers.error) {
        errEl.textContent = state.buyers.error;
        errEl.hidden = false;
    } else {
        errEl.hidden = true;
    }

    const list = document.getElementById('buyers-list');
    const filtered = filterBuyers();

    if (filtered.length === 0) {
        const msg = state.buyers.list.length === 0
            ? 'No PlayerSells RecipeLists in vanilla yet. Re-run setup to extract them.'
            : 'No buyers match the current filter.';
        list.innerHTML = '<li class="buyers-empty">' + esc(msg) + '</li>';
    } else {
        list.innerHTML = filtered.map(buildBuyerCardHtml).join('');
    }

    document.getElementById('buyers-count').textContent =
        filtered.length + ' / ' + state.buyers.list.length + ' lists';
}

function buildBuyerCardHtml(b) {
    const listOvr = (state.current && state.current.buyerLists
                     && state.current.buyerLists[b.id]) || null;
    const removedSet = listOvr && listOvr.removedRecipeIds
        ? new Set(listOvr.removedRecipeIds)
        : new Set();
    const addedIds = (listOvr && listOvr.addedRecipeIds) || [];

    const entries = b.entries || [];
    const vanillaRows = entries.map(e => buildBuyerEntryRowHtml(b.id, e, removedSet.has(e.recipeId)));
    const addedRows = addedIds.map(id => buildBuyerAddedRowHtml(b.id, id));
    const allRows = vanillaRows.concat(addedRows);

    const rows = allRows.length === 0
        ? '<tr><td colspan="5" class="buyer-empty-row">(no entries)</td></tr>'
        : allRows.join('');

    const editedCount = countEditedInBuyer(b);
    const removedCount = removedSet.size;
    const addedCount = addedIds.length;
    const changeBadge = (editedCount + removedCount + addedCount) === 0
        ? ''
        : ' <span class="buyer-change-badge">'
        +   (editedCount  ? '<span class="badge edited">'  + editedCount  + ' edited</span>'  : '')
        +   (removedCount ? '<span class="badge removed">' + removedCount + ' removed</span>' : '')
        +   (addedCount   ? '<span class="badge added">'   + addedCount   + ' added</span>'   : '')
        + '</span>';

    const sub = b.id + (b.entries ? '  -  ' + b.entries.length + ' entries' : '');
    return '<li class="buyer-card" data-buyer-id="' + esc(b.id) + '">'
         +   '<header class="buyer-header">'
         +     '<div class="buyer-title">'
         +       '<span class="buyer-faction">' + esc(b.faction || '(other)') + '</span>'
         +       '<span class="buyer-label">' + esc(b.label || b.id) + '</span>'
         +       changeBadge
         +     '</div>'
         +     '<span class="buyer-sub">' + esc(sub) + '</span>'
         +   '</header>'
         +   '<table class="buyer-table">'
         +     '<thead><tr>'
         +       '<th>Item</th>'
         +       '<th class="num">Qty</th>'
         +       '<th>Pay item</th>'
         +       '<th class="num">Pay qty</th>'
         +       '<th class="buyer-row-actions">&nbsp;</th>'
         +     '</tr></thead>'
         +     '<tbody>' + rows + '</tbody>'
         +   '</table>'
         +   '<div class="buyer-card-footer">'
         +     '<button class="btn-secondary buyer-add-btn" data-buyer-add="' + esc(b.id) + '">+ Add entry</button>'
         +   '</div>'
         + '</li>';
}

// Counts how many of this buyer's vanilla entries currently have a recipe-
// level edit in the profile. Used to drive the per-card "N edited" badge.
function countEditedInBuyer(b) {
    if (!state.current || !state.current.buyerRecipes) return 0;
    const recipes = state.current.buyerRecipes;
    let n = 0;
    for (const e of (b.entries || [])) {
        if (e.recipeId && recipes[e.recipeId]) n++;
    }
    return n;
}

// A vanilla entry can be in one of three render states:
//   * removed:  user toggled it off - greyed-out row with a Restore button
//   * edited:   buyerRecipes[recipeId] has at least one field set - inputs
//               show the overridden values, a Reset button reverts
//   * pristine: no override - inputs show vanilla, edits create an override
//
// We always render full inputs so the user can switch state by typing into
// them (matches the loot-table edit pattern).
function buildBuyerEntryRowHtml(buyerId, e, removed) {
    if (!e.resolved) {
        return '<tr class="buyer-row unresolved">'
             +   '<td colspan="5" class="buyer-unresolved">'
             +     '<span class="buyer-recipe">' + esc(e.recipeId || '(unknown)') + '</span>'
             +     ' <span class="hint">(recipe not found in vanilla extract)</span>'
             +   '</td>'
             + '</tr>';
    }

    const ovr = (state.current && state.current.buyerRecipes
                 && state.current.buyerRecipes[e.recipeId]) || null;
    const rowClass = removed
        ? 'buyer-row removed'
        : (ovr ? 'buyer-row edited' : 'buyer-row');
    const itemId    = (ovr && ovr.itemPath)    ? assetPathToId(ovr.itemPath)    : e.itemId;
    const itemCount = (ovr && ovr.itemCount   != null) ? ovr.itemCount   : (e.itemCount   || 0);
    const payItemId = (ovr && ovr.payItemPath) ? assetPathToId(ovr.payItemPath) : e.payItemId;
    const payCount  = (ovr && ovr.payCount    != null) ? ovr.payCount    : (e.payCount    || 0);

    const actionBtn = removed
        ? '<button class="btn-link buyer-restore" data-buyer-restore="' + esc(buyerId)
            + '|' + esc(e.recipeId) + '" title="Restore">&#x21BA;</button>'
        : (ovr
            ? '<button class="btn-link buyer-reset" data-buyer-reset="' + esc(e.recipeId)
                + '" title="Reset to vanilla">&#x21B6;</button>'
                + '<button class="btn-link buyer-delete" data-buyer-delete="' + esc(buyerId)
                + '|' + esc(e.recipeId) + '" title="Remove from list">&#x2715;</button>'
            : '<button class="btn-link buyer-delete" data-buyer-delete="' + esc(buyerId)
                + '|' + esc(e.recipeId) + '" title="Remove from list">&#x2715;</button>');

    const disabledAttr = removed ? ' disabled' : '';
    return '<tr class="' + rowClass + '" data-recipe-id="' + esc(e.recipeId) + '">'
         +   buildBuyerEditableItemCellHtml(e.recipeId, 'item', itemId, disabledAttr)
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(itemCount)) + '"'
         +       ' data-buyer-field="itemCount" data-recipe-id="' + esc(e.recipeId) + '"'
         +       disabledAttr + '>'
         +   '</td>'
         +   buildBuyerEditableItemCellHtml(e.recipeId, 'payItem', payItemId, disabledAttr)
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(payCount)) + '"'
         +       ' data-buyer-field="payCount" data-recipe-id="' + esc(e.recipeId) + '"'
         +       disabledAttr + '>'
         +   '</td>'
         +   '<td class="buyer-row-actions">' + actionBtn + '</td>'
         + '</tr>';
}

// Renders one "added" row - these are custom recipes the user appended to
// the list. They live ONLY in the profile (no vanilla baseline) and are
// fully editable; the trash button hard-deletes them.
function buildBuyerAddedRowHtml(buyerId, recipeId) {
    const ovr = (state.current && state.current.buyerRecipes
                 && state.current.buyerRecipes[recipeId]) || null;
    if (!ovr) {
        // The added id is in buyerLists but the recipe override vanished -
        // a corrupted profile, but surface the orphan so the user can fix
        // it manually rather than silently dropping it.
        return '<tr class="buyer-row added orphan" data-recipe-id="' + esc(recipeId) + '">'
             +   '<td colspan="5" class="buyer-unresolved">'
             +     '<span class="buyer-recipe">' + esc(recipeId) + '</span>'
             +     ' <span class="hint">(added recipe has no edit-spec - profile corrupted)</span>'
             +   '</td>'
             + '</tr>';
    }
    const itemId    = ovr.itemPath    ? assetPathToId(ovr.itemPath)    : '';
    const payItemId = ovr.payItemPath ? assetPathToId(ovr.payItemPath) : '';
    const itemCount = ovr.itemCount != null ? ovr.itemCount : 0;
    const payCount  = ovr.payCount  != null ? ovr.payCount  : 0;
    return '<tr class="buyer-row added" data-recipe-id="' + esc(recipeId) + '">'
         +   buildBuyerEditableItemCellHtml(recipeId, 'item', itemId, '')
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(itemCount)) + '"'
         +       ' data-buyer-field="itemCount" data-recipe-id="' + esc(recipeId) + '">'
         +   '</td>'
         +   buildBuyerEditableItemCellHtml(recipeId, 'payItem', payItemId, '')
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(payCount)) + '"'
         +       ' data-buyer-field="payCount" data-recipe-id="' + esc(recipeId) + '">'
         +   '</td>'
         +   '<td class="buyer-row-actions">'
         +     '<button class="btn-link buyer-delete" data-buyer-delete-added="'
         +       esc(buyerId) + '|' + esc(recipeId) + '" title="Delete added entry">&#x2715;</button>'
         +   '</td>'
         + '</tr>';
}

// Renders one of the two item cells (sold-item OR pay-item) as an editable
// id-input that opens the shared custom picker (#picker-dropdown) on focus.
// The picker shows icon + display name + subtitle for every vanilla item,
// matching the loot-table add-entry UX. The 'field' arg tells the picker
// dispatcher which path to update on the recipe override - we store it on
// the input via data-buyer-field. The buyerId is derived from the closest
// .buyer-card[data-buyer-id] in the focus / change handlers.
function buildBuyerEditableItemCellHtml(recipeId, field, itemId, disabledAttr) {
    const item = itemId ? state.itemsById.get(itemId) : null;
    const name = (item && item.meta && item.meta.name) || itemId || '(no item)';
    const iconHtml = (item && item.icon)
        ? '<img class="buyer-icon" src="' + esc(item.icon) + '" alt="" loading="lazy">'
        : '<span class="buyer-icon buyer-icon-empty"></span>';
    return '<td class="buyer-item">'
         +   iconHtml
         +   '<div class="buyer-item-edit">'
         +     '<span class="buyer-item-name">' + esc(name) + '</span>'
         +     '<input type="text" class="buyer-item-input"'
         +       ' value="' + esc(itemId || '') + '"'
         +       ' data-buyer-picker-target="1"'
         +       ' data-buyer-field="' + esc(field) + '"'
         +       ' data-recipe-id="' + esc(recipeId) + '"'
         +       ' placeholder="Search items by name or id..."'
         +       ' autocomplete="off"'
         +       disabledAttr + '>'
         +   '</div>'
         + '</td>';
}

// "/R5BusinessRules/.../DA_DID_X.DA_DID_X" -> "DA_DID_X". Used to project
// an asset path back to the short id the UI deals with everywhere else.
function assetPathToId(assetPath) {
    if (!assetPath) return '';
    const s = assetPath;
    const dot = s.lastIndexOf('.');
    const slash = s.lastIndexOf('/');
    const cut = Math.max(dot, slash);
    return cut >= 0 && cut < s.length - 1 ? s.substring(cut + 1) : s;
}

// Inverse of assetPathToId: looks up the canonical full path from the
// vanilla items registry. Used when the user types an item id into one of
// the inline-edit inputs - we store the FULL asset path on the override
// (the patcher needs it that way) but the UI talks in ids.
function itemIdToAssetPath(itemId) {
    if (!itemId) return null;
    return state.itemPathsByItemId.get(itemId) || null;
}

// ---------- Buyers: CRUD on the per-profile overlay ----------------------
//
// The Buyers tab is rendered from two layers: the read-only vanilla list
// (state.buyers.list) and the per-profile overrides (state.current.
// buyerRecipes + buyerLists). Mutations only ever touch the overlay;
// re-rendering merges the two at display time. Save persists the overlay
// to disk; Build feeds the overlay to the BuyerPatcher which produces
// patched RecipeData / RecipeList JSONs.

// Re-renders exactly one buyer card from current state. Used by every
// CRUD helper instead of touching the DOM directly - keeps the render code
// in one place and avoids drift between the initial render and post-edit
// updates.
function refreshBuyerCard(buyerId) {
    const list = document.getElementById('buyers-list');
    const old = list && list.querySelector('.buyer-card[data-buyer-id="' + cssEsc(buyerId) + '"]');
    if (!old) return;
    const buyer = state.buyers.list.find(b => b.id === buyerId);
    if (!buyer) return;
    const wrap = document.createElement('div');
    wrap.innerHTML = buildBuyerCardHtml(buyer);
    const fresh = wrap.firstElementChild;
    if (fresh) old.replaceWith(fresh);
}

// Returns the buyerRecipes[recipeId] entry, creating it on the fly if the
// caller intends to set a field. Seed with vanilla values from `vanillaEntry`
// so a partial edit (e.g. only Qty) still serializes a complete override -
// the patcher only mutates fields it sees on the override, so leaving the
// others null would mean "keep vanilla" anyway, but storing them makes the
// JSON self-documenting and survives a future vanilla rename.
function getOrCreateBuyerRecipeOverride(recipeId, vanillaEntry) {
    if (!state.current) return null;
    state.current.buyerRecipes = state.current.buyerRecipes || {};
    let ovr = state.current.buyerRecipes[recipeId];
    if (!ovr) {
        ovr = {
            itemPath:    vanillaEntry && vanillaEntry.itemPath    ? vanillaEntry.itemPath    : null,
            itemCount:   vanillaEntry && vanillaEntry.itemCount   != null ? vanillaEntry.itemCount   : 0,
            payItemPath: vanillaEntry && vanillaEntry.payItemPath ? vanillaEntry.payItemPath : null,
            payCount:    vanillaEntry && vanillaEntry.payCount    != null ? vanillaEntry.payCount    : 0,
            isCustom:    false,
        };
        state.current.buyerRecipes[recipeId] = ovr;
    }
    return ovr;
}

// Returns the buyerLists[buyerId] entry, creating an empty {added,removed}
// pair on demand. Used by add/remove operations.
function getOrCreateBuyerListOverride(buyerId) {
    if (!state.current) return null;
    state.current.buyerLists = state.current.buyerLists || {};
    let lo = state.current.buyerLists[buyerId];
    if (!lo) {
        lo = { addedRecipeIds: [], removedRecipeIds: [] };
        state.current.buyerLists[buyerId] = lo;
    }
    if (!lo.addedRecipeIds)   lo.addedRecipeIds = [];
    if (!lo.removedRecipeIds) lo.removedRecipeIds = [];
    return lo;
}

// Drops empty containers so the persisted profile JSON doesn't keep
// "buyerLists": { "X": { addedRecipeIds: [], removedRecipeIds: [] } }
// after every undo. Mirrors how loot overrides are pruned.
function pruneBuyerListOverride(buyerId) {
    if (!state.current || !state.current.buyerLists) return;
    const lo = state.current.buyerLists[buyerId];
    if (!lo) return;
    const emptyAdd = !lo.addedRecipeIds || lo.addedRecipeIds.length === 0;
    const emptyRem = !lo.removedRecipeIds || lo.removedRecipeIds.length === 0;
    if (emptyAdd && emptyRem) {
        delete state.current.buyerLists[buyerId];
    }
    if (Object.keys(state.current.buyerLists).length === 0) {
        delete state.current.buyerLists;
    }
}

// User typed into one of the qty / payQty / item / payItem inputs of a
// vanilla entry. Resolves the recipeId back to its vanilla snapshot so the
// override can be seeded with the current vanilla baseline, then writes
// just the changed field. The number-input path is shared with the added
// rows since they look identical - the only difference is "no vanilla
// snapshot" which the override already has via its IsCustom flag.
function setBuyerEntryField(buyerId, recipeId, field, rawValue) {
    if (!state.current) return;
    // Locate the vanilla entry for snapshot seeding. Added rows have no
    // vanilla baseline so we pass null - getOrCreateBuyerRecipeOverride
    // returns the existing IsCustom=true override unchanged in that case.
    const buyer = state.buyers.list.find(b => b.id === buyerId);
    const vanilla = buyer && buyer.entries
        ? buyer.entries.find(e => e.recipeId === recipeId)
        : null;
    const ovr = getOrCreateBuyerRecipeOverride(recipeId, vanilla);
    if (!ovr) return;

    if (field === 'itemCount' || field === 'payCount') {
        const n = parseInt(rawValue, 10);
        if (!isFinite(n) || n < 0) return; // ignore garbage; input stays on screen
        ovr[field] = n;
    } else if (field === 'item' || field === 'payItem') {
        const id = (rawValue || '').trim();
        const targetField = field === 'item' ? 'itemPath' : 'payItemPath';
        if (!id) {
            ovr[targetField] = null;
        } else {
            const path = itemIdToAssetPath(id);
            if (!path) {
                // Unknown id - keep the input value so the user can fix it
                // but DON'T persist garbage. Patcher would crash on an
                // unresolvable asset ref at build time.
                return;
            }
            ovr[targetField] = path;
        }
    }

    markDirty();
}

// Vanilla entries: toggle into the removedRecipeIds set. Re-rendering the
// card shows the row as greyed out with a Restore button instead of the
// usual delete + reset combo.
function toggleRemoveBuyerEntry(buyerId, recipeId) {
    const lo = getOrCreateBuyerListOverride(buyerId);
    if (!lo) return;
    const idx = lo.removedRecipeIds.indexOf(recipeId);
    if (idx >= 0) {
        lo.removedRecipeIds.splice(idx, 1);
    } else {
        lo.removedRecipeIds.push(recipeId);
    }
    pruneBuyerListOverride(buyerId);
    markDirty();
    refreshBuyerCard(buyerId);
}

// Drops a recipe-level edit so the entry shows vanilla values again.
function resetBuyerRecipeOverride(buyerId, recipeId) {
    if (!state.current || !state.current.buyerRecipes) return;
    delete state.current.buyerRecipes[recipeId];
    if (Object.keys(state.current.buyerRecipes).length === 0) {
        delete state.current.buyerRecipes;
    }
    markDirty();
    refreshBuyerCard(buyerId);
}

// Hard-removes an "added" entry from the list. The recipe override goes
// with it (added recipes are NEVER referenced from any other list - we
// generate a fresh QM_Custom_* id every time the user clicks Add - so the
// orphan check is unnecessary but cheap).
function removeAddedBuyerEntry(buyerId, recipeId) {
    const lo = state.current && state.current.buyerLists
        && state.current.buyerLists[buyerId];
    if (lo && lo.addedRecipeIds) {
        const idx = lo.addedRecipeIds.indexOf(recipeId);
        if (idx >= 0) lo.addedRecipeIds.splice(idx, 1);
    }
    pruneBuyerListOverride(buyerId);
    if (state.current && state.current.buyerRecipes) {
        delete state.current.buyerRecipes[recipeId];
        if (Object.keys(state.current.buyerRecipes).length === 0) {
            delete state.current.buyerRecipes;
        }
    }
    markDirty();
    refreshBuyerCard(buyerId);
}

// Appends a new (empty) custom recipe entry to the given buyer's list.
// The id is server-style "QM_Custom_<8 hex>" so the patcher recognises it
// as synthetic (file goes to Recipes/Custom/<id>.json instead of editing
// a vanilla one). User fills in the four fields inline afterwards.
function addBuyerEntry(buyerId) {
    if (!state.current) return;
    const lo = getOrCreateBuyerListOverride(buyerId);
    if (!lo) return;
    const id = 'QM_Custom_' + randomHex(8);
    state.current.buyerRecipes = state.current.buyerRecipes || {};
    state.current.buyerRecipes[id] = {
        itemPath: null,
        itemCount: 1,
        payItemPath: null,
        payCount: 1,
        isCustom: true,
    };
    lo.addedRecipeIds.push(id);
    markDirty();
    refreshBuyerCard(buyerId);
}

// Cheap-enough random id source for "QM_Custom_*" recipe ids. Doesn't have
// to be cryptographically strong - just unique within one profile.
function randomHex(n) {
    let s = '';
    while (s.length < n) s += Math.floor(Math.random() * 0x100000000).toString(16);
    return s.substring(0, n);
}

// Delegated click handler on the Buyers list. Dispatches based on the
// data-attribute the target carries: each action embeds the buyerId and
// (where applicable) the recipeId in its own attribute so the handler
// stays simple. data-buyer-* attributes are uniform across button kinds.
function onBuyersListClick(e) {
    const t = e.target.closest && e.target.closest(
        '[data-buyer-add],[data-buyer-delete],[data-buyer-delete-added],'
        + '[data-buyer-restore],[data-buyer-reset]');
    if (!t) return;
    if (t.dataset.buyerAdd) {
        addBuyerEntry(t.dataset.buyerAdd);
        return;
    }
    if (t.dataset.buyerDelete) {
        const [buyerId, recipeId] = t.dataset.buyerDelete.split('|');
        toggleRemoveBuyerEntry(buyerId, recipeId);
        return;
    }
    if (t.dataset.buyerDeleteAdded) {
        const [buyerId, recipeId] = t.dataset.buyerDeleteAdded.split('|');
        removeAddedBuyerEntry(buyerId, recipeId);
        return;
    }
    if (t.dataset.buyerRestore) {
        const [buyerId, recipeId] = t.dataset.buyerRestore.split('|');
        toggleRemoveBuyerEntry(buyerId, recipeId); // toggle removes the entry from removedRecipeIds
        return;
    }
    if (t.dataset.buyerReset) {
        // Walk up to the card so we know which buyer to re-render.
        const card = t.closest('.buyer-card');
        const buyerId = card && card.dataset.buyerId;
        if (buyerId) resetBuyerRecipeOverride(buyerId, t.dataset.buyerReset);
        return;
    }
}

// Delegated change handler. Fires when a buyer-input loses focus or the
// user presses Enter - this matches the loot-list edit pattern so focus
// survives mid-typing instead of being yanked by every keystroke.
//
// Picker-target inputs (item / payItem) are NOT committed via change: the
// picker confirms the pick on mousedown via onPickerClick. Letting `change`
// fire here would persist whatever text the user happened to type even if
// they then clicked a different picker row - so we skip them.
function onBuyersListChange(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.buyerField) return;
    if (t.dataset.buyerPickerTarget) return;  // picker handles its own commit
    const recipeId = t.dataset.recipeId;
    const card = t.closest('.buyer-card');
    const buyerId = card && card.dataset.buyerId;
    if (!buyerId || !recipeId) return;
    setBuyerEntryField(buyerId, recipeId, t.dataset.buyerField, t.value);
    // Re-render the card so the edited/added badges + change-class
    // highlighting reflect the new state. Card-scoped re-render preserves
    // focus on inputs the user wasn't editing (we left this input on
    // change, so focus is fine).
    refreshBuyerCard(buyerId);
}

// Focusing one of the item / pay-item inputs opens the shared custom picker
// underneath it - identical UX to the loot-table add-entry picker (icon +
// name + subtitle rows). We resolve buyerId from the closest card so callers
// can't lie about which row the input belongs to.
function onBuyersListFocusIn(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.buyerPickerTarget) return;
    if (t.disabled) return;  // .removed rows have disabled inputs
    const recipeId = t.dataset.recipeId;
    const field    = t.dataset.buyerField;
    const card     = t.closest('.buyer-card');
    const buyerId  = card && card.dataset.buyerId;
    if (!buyerId || !recipeId || !field) return;
    openBuyerPicker(t, buyerId, recipeId, field);
}

// Typing in a picker-target input refreshes the dropdown contents live.
// Other buyer inputs (qty / payQty) go through onBuyersListChange on blur
// instead - they don't talk to the picker.
function onBuyersListInput(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.buyerPickerTarget) return;
    if (!state.picker || state.picker.input !== t) return;
    populatePicker(t.value);
    // Content height likely changed -> re-evaluate flip-up / overflow.
    positionPicker(t);
}

// Render an item id as an icon + display-name + raw-id cell. Used for both
// the sold item (left) and the pay item (right); pay items are typically
// CoinPiastre / CoinGuinea but can also be barter goods (Drowned/Senkamati
// "Stuff" tiers), so we don't hardcode "currency" semantics.
function buildBuyerItemCellHtml(itemId) {
    if (!itemId) {
        return '<td class="buyer-item">'
             +   '<span class="buyer-icon buyer-icon-empty"></span>'
             +   '<span class="buyer-item-name">(no item)</span>'
             + '</td>';
    }
    const item = state.itemsById.get(itemId);
    const name = (item && item.meta && item.meta.name) || itemId;
    const iconHtml = item && item.icon
        ? '<img class="buyer-icon" src="' + esc(item.icon) + '" alt="" loading="lazy">'
        : '<span class="buyer-icon buyer-icon-empty"></span>';
    return '<td class="buyer-item">'
         +   iconHtml
         +   '<span class="buyer-item-name">' + esc(name) + '</span>'
         +   '<span class="buyer-item-id">' + esc(itemId) + '</span>'
         + '</td>';
}

function renderBuyersStatus() {
    const total = state.buyers.list.length;
    let entries = 0;
    for (const b of state.buyers.list) {
        entries += (b.entries ? b.entries.length : 0);
    }
    document.getElementById('buyers-stat-total').textContent   = total;
    document.getElementById('buyers-stat-entries').textContent = entries;
}

// ---------- Sellers tab (read-only PlayerBuys roster browser) -----------
//
// /api/sellers returns a list of SellerDto entries. Each entry is one
// R5BLRecipeList JSON whose filename matches *_PlayerBuys* in vanilla
// (the player buys from the NPC = NPC is a vendor). Vanilla ships the
// 8 Trade* faction rosters plus 3 Handyman vendors. The DTO shape mirrors
// BuyerEntryDto - itemId always refers to the "main" item being traded
// (= what the NPC sells), payItemId is the currency the player pays in.
// Read-only for now; CRUD follows in a later iteration.
//
// We deliberately reuse the .buyer-* / .buyers-list CSS classes from the
// Buyers tab - both pages render identical-looking cards + tables, so the
// visual styles are shared rather than duplicated. The class names being
// "buyer-foo" inside the sellers markup is an internal naming wart that
// the user never sees; if we ever diverge visually we'll split them.

async function loadSellers() {
    try {
        const data = await api('GET', '/api/sellers');
        state.sellers.loaded = true;
        state.sellers.list = data || [];
        state.sellers.error = null;
        populateSellerFactionFilter();
    } catch (e) {
        state.sellers.loaded = true;
        state.sellers.list = [];
        state.sellers.error = 'Failed to load sellers: ' + e.message;
    }
    renderSellers();
    renderSellersStatus();
}

function populateSellerFactionFilter() {
    const sel = document.getElementById('sellers-filter-faction');
    if (!sel) return;
    const seen = new Set();
    for (const s of state.sellers.list) {
        if (s.faction) seen.add(s.faction);
    }
    const factions = Array.from(seen).sort();
    const prev = sel.value;
    sel.innerHTML = '<option value="">All factions</option>'
        + factions.map(f => '<option value="' + esc(f) + '">' + esc(f) + '</option>').join('');
    if (prev && factions.includes(prev)) sel.value = prev;
}

function filterSellers() {
    const q = (document.getElementById('sellers-filter').value || '').trim().toLowerCase();
    const faction = document.getElementById('sellers-filter-faction').value;
    const out = [];
    for (const s of state.sellers.list) {
        if (faction && s.faction !== faction) continue;
        if (q) {
            // Match on label / id / any entry's item or recipe id so users
            // can find "who sells Rum Bottles?" by typing the item id.
            let hay = (s.label + ' ' + s.id + ' ' + s.faction).toLowerCase();
            for (const e of s.entries) {
                hay += ' ' + (e.itemId || '') + ' ' + (e.recipeId || '');
            }
            if (!hay.includes(q)) continue;
        }
        out.push(s);
    }
    return out;
}

function renderSellers() {
    const errEl = document.getElementById('sellers-error');
    if (state.sellers.error) {
        errEl.textContent = state.sellers.error;
        errEl.hidden = false;
    } else {
        errEl.hidden = true;
    }

    const list = document.getElementById('sellers-list');
    const filtered = filterSellers();

    if (filtered.length === 0) {
        const msg = state.sellers.list.length === 0
            ? 'No PlayerBuys RecipeLists in vanilla yet. Re-run setup to extract them.'
            : 'No sellers match the current filter.';
        list.innerHTML = '<li class="buyers-empty">' + esc(msg) + '</li>';
    } else {
        list.innerHTML = filtered.map(buildSellerCardHtml).join('');
    }

    document.getElementById('sellers-count').textContent =
        filtered.length + ' / ' + state.sellers.list.length + ' lists';
}

function buildSellerCardHtml(s) {
    const entries = s.entries || [];
    const rows = entries.length === 0
        ? '<tr><td colspan="5" class="buyer-empty-row">(no entries)</td></tr>'
        : entries.map(buildSellerEntryRowHtml).join('');

    const sub = s.id + (s.entries ? '  -  ' + s.entries.length + ' entries' : '');
    return '<li class="buyer-card">'
         +   '<header class="buyer-header">'
         +     '<div class="buyer-title">'
         +       '<span class="buyer-faction">' + esc(s.faction || '(other)') + '</span>'
         +       '<span class="buyer-label">' + esc(s.label || s.id) + '</span>'
         +     '</div>'
         +     '<span class="buyer-sub">' + esc(sub) + '</span>'
         +   '</header>'
         +   '<table class="buyer-table">'
         +     '<thead><tr>'
         +       '<th>Item</th>'
         +       '<th class="num">Qty</th>'
         +       '<th>Pay item</th>'
         +       '<th class="num">Pay qty</th>'
         +       '<th>Requirement</th>'
         +     '</tr></thead>'
         +     '<tbody>' + rows + '</tbody>'
         +   '</table>'
         + '</li>';
}

function buildSellerEntryRowHtml(e) {
    if (!e.resolved) {
        return '<tr class="buyer-row unresolved">'
             +   '<td colspan="5" class="buyer-unresolved">'
             +     '<span class="buyer-recipe">' + esc(e.recipeId || '(unknown)') + '</span>'
             +     ' <span class="hint">(recipe not found in vanilla extract)</span>'
             +   '</td>'
             + '</tr>';
    }

    // Same layout as buyer rows. The backend already swapped Cost/Result
    // semantics so itemId = the item the NPC sells, payItemId = currency.
    // We reuse buildBuyerItemCellHtml since it's a generic id -> cell
    // renderer (no buy/sell semantics in the helper itself).
    //
    // Unlike buyers, sellers regularly have a CraftRequirement set (faction
    // reputation gate, e.g. "DA_Requirement_Smugglers_1" = Smugglers Rep 1).
    // The 5th column surfaces that; empty means "no reputation needed".
    const req = shortenSellerRequirement(e.craftRequirement);
    const reqCell = req
        ? '<td class="buyer-req">' + esc(req) + '</td>'
        : '<td class="buyer-req buyer-req-none">-</td>';
    return '<tr class="buyer-row">'
         +   buildBuyerItemCellHtml(e.itemId)
         +   '<td class="num">' + esc(String(e.itemCount || 0)) + '</td>'
         +   buildBuyerItemCellHtml(e.payItemId)
         +   '<td class="num">' + esc(String(e.payCount || 0)) + '</td>'
         +   reqCell
         + '</tr>';
}

// Turns a CraftRequirement asset path like
// ".../Reputation/DA_Requirement_Smugglers_1" into "Smugglers Rep 1".
// Returns '' for empty / unrecognised input so callers can fall back to a
// neutral placeholder.
function shortenSellerRequirement(reqPath) {
    if (!reqPath) return '';
    const s = String(reqPath);
    const m = s.match(/DA_Requirement_([A-Za-z]+)_(\d+)/);
    if (m) return m[1] + ' Rep ' + m[2];
    // Fallback: last path segment, strip the DA_Requirement_ prefix if any
    const last = s.split('/').pop().split('.').pop();
    return last.replace(/^DA_Requirement_/, '').replace(/_/g, ' ');
}

function renderSellersStatus() {
    const total = state.sellers.list.length;
    let entries = 0;
    for (const s of state.sellers.list) {
        entries += (s.entries ? s.entries.length : 0);
    }
    document.getElementById('sellers-stat-total').textContent   = total;
    document.getElementById('sellers-stat-entries').textContent = entries;
}

function setFooterCollapsed(collapsed) {
    const footer = document.getElementById('footer');
    const btn    = document.getElementById('footer-toggle');
    footer.classList.toggle('collapsed', collapsed);
    btn.setAttribute('aria-expanded', String(!collapsed));
}

// ---------- Bindings -----------------------------------------------------

function bindHandlers() {
    document.getElementById('profile-select').addEventListener('change', async e => {
        const nextId = e.target.value;
        if (state.isDirty) {
            // Revert visually right away so the dropdown doesn't show the
            // new selection while the (async) confirm modal is open.
            e.target.value = state.current.id;
            if (!await confirm('Discard unsaved changes?')) return;
        }
        loadProfile(nextId);
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
    document.getElementById('btn-open-setup').addEventListener('click',       openSetupManually);
    document.getElementById('mods-list').addEventListener('click', e => {
        const t = e.target;
        if (t && t.dataset && t.dataset.deleteMod) {
            deleteMod(t.dataset.deleteMod);
        }
    });

    // Buyers page: read-only for now, so we only need filter wiring -
    // Faction dropdown is populated after /api/buyers comes back, so its
    // initial state at bind time is just the "All factions" placeholder.
    document.getElementById('buyers-filter').addEventListener('input',           renderBuyers);
    document.getElementById('buyers-filter-faction').addEventListener('change',  renderBuyers);

    // Buyers CRUD - delegated handlers on the list root. Edits write
    // through to state.current.buyer{Recipes,Lists} via the helpers above
    // and re-render the affected card. Splitting click vs change vs input
    // mirrors the loot-list pattern: text/number inputs commit on `change`
    // (blur/Enter) so focus survives mid-typing, but we still react to
    // `input` to track in-progress values for the per-card status badge.
    const buyersList = document.getElementById('buyers-list');
    if (buyersList) {
        buyersList.addEventListener('click',  onBuyersListClick);
        buyersList.addEventListener('change', onBuyersListChange);
        // Focusing an item / pay-item input opens the shared custom picker
        // (same dropdown the loot-table tab uses). focusin (vs focus) bubbles
        // so we can delegate from the list root.
        buyersList.addEventListener('focusin', onBuyersListFocusIn);
        // Live-filter the picker while the user types in the input. The
        // picker stays open across keystrokes because populatePicker just
        // re-fills #picker-dropdown in place.
        buyersList.addEventListener('input',  onBuyersListInput);
        // Re-position the dropdown when the buyers list scrolls (cheap; the
        // handler no-ops if no picker is open).
        buyersList.addEventListener('scroll', () => {
            if (state.picker) positionPicker(state.picker.input);
        }, { passive: true });
    }

    // Sellers page: same read-only filter wiring as Buyers.
    document.getElementById('sellers-filter').addEventListener('input',          renderSellers);
    document.getElementById('sellers-filter-faction').addEventListener('change', renderSellers);

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
// Also: deferred refresh for edited entry rows. setLootEntryFieldFromInput
// skips refreshLtRow() on every keystroke (it would steal focus); we catch
// up here on blur/Enter/Tab so the edited-badge + .edited CSS settle.
function onLtListChange(e) {
    const t = e.target;
    if (!t || !t.dataset) return;
    if (t.dataset.addFormType) {
        syncPickerInputToType(t);
        return;
    }
    if (t.dataset.editField && t.dataset.ltId) {
        refreshLtRow(t.dataset.ltId);
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
    const picker = state.picker;
    closePicker();  // close before confirm; confirm re-renders the row
    if (picker.source === 'buyer') {
        // Buyer item / pay-item pick. setBuyerEntryField does its own
        // markDirty; the card re-render lets the new icon + display name
        // settle in the cell.
        setBuyerEntryField(picker.buyerId, picker.recipeId, picker.buyerField, li.dataset.pickId);
        refreshBuyerCard(picker.buyerId);
        return;
    }
    // Default / 'loot' source: legacy LT add-entry flow.
    confirmAddedEntry(picker.ltId, picker.addedIndex, picker.type, li.dataset.pickId);
}

// Any click outside the picker or its anchor input closes the dropdown.
function onDocClickClosePicker(e) {
    if (!state.picker) return;
    const dd = document.getElementById('picker-dropdown');
    if (dd && dd.contains(e.target)) return;
    if (e.target === state.picker.input) return;
    closePicker();
}

// ---------- Modal dialogs (alert / confirm / prompt overrides) ----------
//
// The native browser dialogs look out of place against the rest of the app,
// so we override them with styled HTML modals. JS has no real synchronous
// blocking primitive, so the overrides return Promises instead of values -
// every call site has to `await` them.
//
// Keyboard: Esc cancels (returns false / null / undefined depending on kind),
// Enter confirms (returns true / input value / undefined). Each call gets
// its own overlay element, so nested dialogs would just stack.
function showModal({ kind, message, defaultValue }) {
    return new Promise(resolve => {
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        const card = document.createElement('div');
        card.className = 'modal-card';

        const msg = document.createElement('p');
        msg.className = 'modal-message';
        msg.textContent = message;
        card.appendChild(msg);

        let input = null;
        if (kind === 'prompt') {
            input = document.createElement('input');
            input.type = 'text';
            input.className = 'modal-input';
            input.value = defaultValue ?? '';
            card.appendChild(input);
        }

        const actions = document.createElement('div');
        actions.className = 'modal-actions';
        card.appendChild(actions);
        overlay.appendChild(card);

        const cancelValue  = kind === 'prompt' ? null : (kind === 'alert' ? undefined : false);
        const confirmValue = () =>
            kind === 'prompt' ? input.value : (kind === 'alert' ? undefined : true);

        const close = (value) => {
            document.removeEventListener('keydown', onKey, true);
            overlay.remove();
            resolve(value);
        };
        const onKey = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                close(cancelValue);
            } else if (e.key === 'Enter') {
                // For prompts, only Enter inside the input commits - so Tab to
                // a button + Enter still works as expected.
                if (kind === 'prompt' && e.target !== input) return;
                e.preventDefault();
                close(confirmValue());
            }
        };

        if (kind !== 'alert') {
            const cancel = document.createElement('button');
            cancel.type = 'button';
            cancel.textContent = 'Cancel';
            cancel.addEventListener('click', () => close(cancelValue));
            actions.appendChild(cancel);
        }
        const ok = document.createElement('button');
        ok.type = 'button';
        ok.className = 'primary';
        ok.textContent = 'OK';
        ok.addEventListener('click', () => close(confirmValue()));
        actions.appendChild(ok);

        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey, true);

        if (input) {
            input.focus();
            input.select();
        } else {
            ok.focus();
        }
    });
}

window.alert = function(msg) {
    return showModal({ kind: 'alert', message: String(msg ?? '') });
};
window.confirm = function(msg) {
    return showModal({ kind: 'confirm', message: String(msg ?? '') });
};
window.prompt = function(msg, defaultValue) {
    return showModal({
        kind: 'prompt',
        message: String(msg ?? ''),
        defaultValue: defaultValue ?? '',
    });
};
