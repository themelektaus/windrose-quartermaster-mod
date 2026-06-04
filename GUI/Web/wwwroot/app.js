'use strict';

const state = {
    items: [],
    itemsById: new Map(),
    lootTables: [],
    lootById: new Map(),
    lootCategories: [],
    lootTypes: [],
    itemPathsByItemId: new Map(),
    tablePathsByLtId:  new Map(),
    expandedLts: new Set(),

    profiles: [],
    current: null,
    isDirty: false,
    activeTab: 'misc',
    uiScaleModified: false,

    mods: {
        loaded: false,
        modsDir: null,
        files: [],
        error: null,
    },

    // Characters tab: discovered Windrose save characters (existing-character
    // equipment-slot patcher). Loaded on first tab open; re-scanned on demand.
    characters: {
        loaded: false,
        supported: true,
        list: [],
        error: null,
        // Ships (Expanded Naval Tactics save patcher), loaded alongside chars.
        ships: [],
        shipsSupported: true,
        shipsError: null,
    },

    buyers: {
        loaded: false,
        list: [],
        error: null,
    },

    sellers: {
        loaded: false,
        list: [],
        error: null,
    },

    itemTemplates: {
        loaded: false,
        list: [],
        byId: new Map(),
        error: null,
    },

    // Lazy-loaded full catalogs the Building Creator's picker dropdowns
    // filter client-side (same pattern as state.items for loot tables).
    // null until the user first opens a relevant picker.
    vanillaMaterials: null,   // [{displayName, packagePath}] (~1134 entries)
    vanillaResources: null,   // [{stem, packagePath, displayName, iconUrl, itemTag}]
    // Etappe I: ~849 Vanilla R5BuildingItem DAs the user can pick as a
    // parent template for a custom building. Loaded once on first picker
    // open. Each inspection (per picked DA) is cached in
    // state.vanillaBuildingInspections so the recipe pre-fill + the
    // template summary don't re-hit the backend on every render.
    vanillaBuildingTemplates: null,        // [{id, displayName, category, packagePath}]
    vanillaBuildingInspections: new Map(), // id -> VanillaBuildingTemplateInspectDto

    // Active card id for the Building Creator. The tab renders ONE card
    // at a time (picked via the dropdown next to "New Building"); this
    // holds the user's current selection across re-renders / tab visits.
    buildingCreatorActiveId: null,

    picker: null,
};

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

function cloneTemplate(id) {
    const tpl = document.getElementById(id);
    if (!tpl) throw new Error('template #' + id + ' missing');
    return tpl.content.firstElementChild.cloneNode(true);
}

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

    loadItemTemplates();

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
        if (lt.id && !state.tablePathsByLtId.has(lt.id)) {
            state.tablePathsByLtId.set(
                lt.id,
                '/R5BusinessRules/LootTables/' + lt.id + '.' + lastSegment(lt.id));
        }
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

// Hard-break migration for the Etappe G mesh-driven schema.
//
// Pre-G profiles persist CustomBuildingSlot with hardcoded texture
// stems (customAlbedoStem / customNormalStem / customMtrmStem) and
// slot keys named after the template's hardcoded slot names ("Frame"
// / "Canvas"). Post-G profiles use VanillaMaterialParentPath +
// dynamic Scalar/Vector/TextureParams dicts keyed by mesh slot
// index.
//
// On load we detect the old shape (any slot still carrying
// customAlbedoStem etc.) and drop the stale CustomBuildings entirely,
// marking the profile dirty so the next save persists the cleaned
// list. The user is informed via a toast/alert so they don't
// silently lose work - the locked design decision was to NOT
// auto-migrate (clean break, user re-creates the building in the
// new UI in ~3 minutes).
function migrateLegacyCustomBuildings(profile) {
    if (!profile || !Array.isArray(profile.customBuildings)) return;
    const list = profile.customBuildings;
    const droppedNames = [];
    const keep = [];
    for (const b of list) {
        if (!b) continue;
        if (looksLikeLegacySlot(b.slots)) {
            droppedNames.push(b.name || b.id || '<unnamed>');
            continue;
        }
        keep.push(b);
    }
    if (droppedNames.length === 0) return;
    profile.customBuildings = keep;
    state.isDirty = true;
    // Defer so we don't toast before the UI is mounted.
    setTimeout(() => {
        const msg = 'Legacy building schema detected and removed: '
            + droppedNames.join(', ')
            + '. Material slots now come from the mesh - re-create the building(s) using the new per-slot Vanilla MI picker.';
        try { alert(msg); } catch (_) { console.warn(msg); }
    }, 50);
}

function looksLikeLegacySlot(slots) {
    if (!slots || typeof slots !== 'object') return false;
    for (const k of Object.keys(slots)) {
        const v = slots[k];
        if (!v || typeof v !== 'object') continue;
        if ('customAlbedoStem' in v || 'customAlbedoPath' in v
         || 'customNormalStem' in v || 'customMtrmStem' in v) {
            return true;
        }
    }
    return false;
}

function rebuildSavedCustomItemIds() {
    const ids = new Set();
    if (state.current && Array.isArray(state.current.customItems)) {
        for (const c of state.current.customItems) {
            if (c && c.id) ids.add(c.id);
        }
    }
    state.savedCustomItemIds = ids;
}

function syncCustomItemsIntoCatalog() {
    if (!state.items || !state.itemsById || !state.itemPathsByItemId) return;
    for (let i = state.items.length - 1; i >= 0; i--) {
        const it = state.items[i];
        if (it && it.isCustom) {
            state.items.splice(i, 1);
            state.itemsById.delete(it.id);
            state.itemPathsByItemId.delete(it.id);
        }
    }
    if (!state.current) return;
    const profileId = state.current.id;
    const customs = state.current.customItems || [];
    const tplById = (state.itemTemplates && state.itemTemplates.byId) || new Map();
    for (const c of customs) {
        if (!c || !c.id) continue;
        const tpl = tplById.get(c.templateId) || null;
        const path = '/R5BusinessRules/InventoryItems/Custom/' + c.id + '.' + c.id;
        const maxStack = (c.maxCountInSlot != null)
            ? c.maxCountInSlot
            : (tpl ? tpl.defaultMaxCountInSlot : 1);
        const rarity = c.rarity || (tpl ? tpl.defaultRarity : 'Common');
        const trimmedName = (c.name || '').trim();
        const hasCustomIcon = !!(c.iconPath && profileId);
        const iconUrl = hasCustomIcon
            ? '/api/profiles/' + encodeURIComponent(profileId)
                + '/icons/' + encodeURIComponent(c.id)
                + '?t=' + (c._iconCacheBust || 0)
            : (c.templateId ? '/Icons/' + encodeURIComponent(c.templateId) + '.png' : '');
        const entry = {
            id: c.id,
            path,
            isCustom: true,
            meta: { name: trimmedName || c.id, description: c.description || '' },
            icon: iconUrl,
            itemClass: 'Custom',
            category: tpl ? tpl.category : '',
            rarity,
            maxCountInSlot: maxStack,
            vanillaStack: maxStack,
        };
        state.items.push(entry);
        state.itemsById.set(c.id, entry);
        state.itemPathsByItemId.set(c.id, path);
        // Seed the building-recipe display cache so recipe rows referencing
        // this custom item render with the user-chosen name on profile
        // reload (before the user ever opens the recipe picker). The cache
        // lives in tabs/buildings.js but classic-script `const` at module
        // top-level is shared across the realm's global lexical record.
        if (typeof _resourceDisplayCache !== 'undefined') {
            _resourceDisplayCache.set(path, trimmedName || c.id);
        }
    }
}

const TAB_NAMES = ['misc', 'items', 'creator', 'buildings', 'loot', 'buyers', 'sellers', 'cooldowns', 'shipmusic', 'lighting', 'mods', 'characters'];

async function loadTabHtml() {
    const host = document.getElementById('tab-pages');
    if (!host) throw new Error('#tab-pages mount point missing in index.html');
    const fragments = await Promise.all(TAB_NAMES.map(async name => {
        const res = await fetch('tabs/' + name + '.html');
        if (!res.ok) throw new Error('Failed to load tabs/' + name + '.html: HTTP ' + res.status);
        return await res.text();
    }));
    host.innerHTML = fragments.join('\n');
}

async function boot() {
    await loadTabHtml();
    bindSetupHandlers();
    bindHandlers();

    let status = await api('GET', '/api/setup/status');
    if (status.isReady) {
        await loadAppData();
        return;
    }

    // No vanilla pak resolves => Steam auto-detect didn't find Windrose AND
    // no manual override is set (or the override is stale / typoed). Pop the
    // game-install modal first so the user can point us at the right folder
    // before we even render the setup-overlay's checklist. Loop until the
    // user either fixes the path or explicitly dismisses the modal.
    while (!status.hasVanillaPak) {
        const closeKind = await openGameInstallModalForBoot();
        if (closeKind === 'dismissed') break;
        status = await api('GET', '/api/setup/status');
        if (status.isReady) {
            await loadAppData();
            return;
        }
    }

    showSetupOverlay(status);
}

// Boot-time wrapper around showGameInstallModal: prefetches the current
// /api/game-install status (since we know we want to show it) and returns
// 'fixed' / 'dismissed' depending on whether the user saved / cancelled.
async function openGameInstallModalForBoot() {
    let data = null;
    try {
        const r = await fetch('/api/game-install');
        data = await r.json();
    } catch { /* fall through with null data */ }
    const initial = (data && data.overrideSet && data.overrideGameRoot)
        ? data.overrideGameRoot
        : (data && data.steamGameRoot) || '';
    const result = await showGameInstallModal({
        initialValue: initial,
        status: data || {},
    });
    if (result === 'saved' || result === 'cleared') return 'fixed';
    return 'dismissed';
}

document.addEventListener('DOMContentLoaded', () => {
    boot().catch(err => {
        const pre = document.createElement('pre');
        pre.style.cssText = 'color:#e16464;padding:2em;white-space:pre-wrap;';
        pre.textContent = 'Init failed: ' + err.message + '\n\n' + (err.stack || '');
        document.body.replaceChildren(pre);
    });
});

function showSetupOverlay(status) {
    document.getElementById('setup-overlay').hidden = false;
    renderSetupChecks(status);
    renderSetupError(status);
    syncSetupRunEnabled(status);
}

function syncSetupRunEnabled(status) {
    document.getElementById('setup-run').disabled = !canAutoRunSetup(status);
}

function hideSetupOverlay() {
    document.getElementById('setup-overlay').hidden = true;
}

function buildSetupCheckRow(cls, label, detail) {
    const li = cloneTemplate('tpl-setup-check');
    li.className = cls;
    const div = li.querySelector('div');
    div.querySelector('b').textContent = label;
    if (detail) {
        div.appendChild(document.createElement('br'));
        const small = document.createElement('small');
        small.textContent = detail;
        div.appendChild(small);
    }
    return li;
}

function renderSetupChecks(status) {
    const ul = document.getElementById('setup-checks');
    const staticRows = [
        ['hasVanillaPak', 'Windrose install detected via Steam',
                          status.vanillaPakPath || status.vanillaPakError],
        ['hasUsmap',      'UE5 mappings file (.usmap) in mod root',
                          status.usmapPath || 'Missing - see the setup error below for how to restore it.'],
    ];
    const sources = Array.isArray(status.sources) ? status.sources : [];
    // ffmpeg is optional - WAV-only users never need it - so absent renders in
    // a neutral "optional" state, not red. Re-running setup downloads it.
    const ffmpegDetail = status.hasFfmpeg
        ? (status.ffmpegPath || '')
        : 'Optional - one-time ~190 MB download. Only needed if you upload mp3 / ogg / flac / m4a / aac / opus in the Ship Music tab.';

    const frag = document.createDocumentFragment();
    for (const [key, label, detail] of staticRows) {
        frag.appendChild(buildSetupCheckRow(status[key] ? 'ok' : 'bad', label, detail));
    }
    for (const s of sources) {
        frag.appendChild(buildSetupCheckRow(s.ok ? 'ok' : 'bad', s.label || s.key, s.description || s.diskPath || ''));
    }
    frag.appendChild(buildSetupCheckRow(status.hasIcons ? 'ok' : 'bad', 'Item icons extracted',
        (status.iconsDir || '') + ' - produced by the icons step.'));
    frag.appendChild(buildSetupCheckRow(status.hasFfmpeg ? 'ok' : 'optional', 'ffmpeg (audio transcoder)', ffmpegDetail));
    ul.replaceChildren(frag);
}

function renderSetupError(status) {
    const out = document.getElementById('setup-error');
    if (!status.hasVanillaPak) {
        out.hidden = false;
        // Plain text + a "Configure" affordance so the user doesn't have to
        // close the overlay, open Mods, click Configure, etc. The button
        // re-runs the same modal as the boot-time auto-show.
        out.replaceChildren(
            document.createTextNode(
                'Cannot find a Windrose install: ' + (status.vanillaPakError || '(no detail)') +
                '\nInstall Windrose via Steam, set a manual path, then click Re-check.'),
        );
        const wrap = document.createElement('div');
        wrap.style.marginTop = '.6em';
        const btn = document.createElement('button');
        btn.type = 'button';
        btn.textContent = 'Set game install path manually';
        btn.addEventListener('click', async () => {
            await openGameInstallModal();
            await recheckSetup();
        });
        wrap.appendChild(btn);
        out.appendChild(wrap);
        return;
    }
    if (!status.hasUsmap) {
        out.hidden = false;
        out.textContent =
            (status.usmapHint || 'No .usmap file found.') + '\nThen click Re-check.';
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
    const hasOwnPrefix = /^\[(?:OK|ok|X|!|\.\.|skip)\b/.test(line);
    const prefix = kind && !hasOwnPrefix ? '[' + kind.toUpperCase() + '] ' : '';
    span.textContent = prefix + line + '\n';
    out.appendChild(span);
    out.scrollTop = out.scrollHeight;
}

function clearSetupLog() {
    document.getElementById('setup-log').replaceChildren();
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
        hideSetupOverlay();
        resetSetupButtons();
    });
}

function resetSetupButtons() {
    document.getElementById('setup-run').hidden      = false;
    document.getElementById('setup-continue').hidden = true;
    document.getElementById('setup-force').hidden    = true;
    document.getElementById('setup-close').hidden    = true;
    clearSetupLog();
}

async function openSetupManually() {
    resetSetupButtons();
    try {
        const status = await api('GET', '/api/setup/status');
        showSetupOverlay(status);
    } catch (err) {
        showSetupOverlay({
            isReady: false,
            hasVanillaPak: false,
            vanillaPakError: err.message,
        });
    }
    document.getElementById('setup-close').hidden = false;
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

function runSetup(force) {
    return new Promise(resolve => {
        const url = '/api/setup/run' + (force ? '?force=true' : '');
        clearSetupLog();
        setSetupButtonsDisabled(true);
        document.getElementById('setup-continue').hidden = true;
        document.getElementById('setup-run').hidden = false;
        appendSetupLog((force ? 'Force re-running ' : 'Running ') + 'setup...', 'step');

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
                let idx;
                while ((idx = buf.indexOf('\n\n')) >= 0) {
                    const frame = buf.slice(0, idx);
                    buf = buf.slice(idx + 2);
                    handleSseFrame(frame);
                }
            }
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

function classifyLogLine(line) {
    if (line.startsWith('[step:start ') || line.startsWith('[step:end ')) return 'step';
    if (line.startsWith('[skip] ')) return 'step';
    if (line.startsWith('[OK]') || line.startsWith('[ok]')) return 'ok';
    if (line.startsWith('[X]')  || line.startsWith('[!]'))  return 'err';
    return null;
}

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
        if (!state.mods.loaded) {
            loadMods();
        } else {
            renderMods();
            renderModsStatus();
        }
    }
    if (tab === 'buyers') {
        if (!state.buyers.loaded) {
            loadBuyers();
        } else {
            renderBuyers();
            renderBuyersStatus();
        }
    }
    if (tab === 'sellers') {
        if (!state.sellers.loaded) {
            loadSellers();
        } else {
            renderSellers();
            renderSellersStatus();
        }
    }
    if (tab === 'creator') {
        if (!state.itemTemplates.loaded) {
            loadItemTemplates().then(() => {
                renderItemCreator();
                renderItemCreatorStatus();
            });
        } else {
            renderItemCreator();
            renderItemCreatorStatus();
        }
    }
    if (tab === 'buildings') {
        renderBuildingCreator();
        renderBuildingCreatorStatus();
    }
    if (tab === 'characters') {
        if (!state.characters.loaded) {
            loadCharacters();
        } else {
            renderCharacters();
        }
    }
}

async function loadProfile(id) {
    state.current = await api('GET', '/api/profiles/' + encodeURIComponent(id));
    state.current.globals       = state.current.globals || {};
    state.current.overrides     = state.current.overrides || {};
    state.current.lootOverrides = state.current.lootOverrides || {};
    state.current.buyerRecipes  = state.current.buyerRecipes  || {};
    state.current.buyerLists    = state.current.buyerLists    || {};
    state.current.sellerRecipes = state.current.sellerRecipes || {};
    state.current.sellerLists   = state.current.sellerLists   || {};
    state.current.customItems     = state.current.customItems     || [];
    state.current.customBuildings = state.current.customBuildings || [];
    migrateLegacyCustomBuildings(state.current);
    // Reset Building Creator active selection so the picker doesn't try
    // to surface a stale id from a different profile. renderBuildingCreator
    // auto-selects the first entry when the id is null/missing.
    state.buildingCreatorActiveId = null;
    rebuildSavedCustomItemIds();
    syncCustomItemsIntoCatalog();
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
    if (state.activeTab === 'sellers' && state.sellers.loaded) {
        renderSellers();
        renderSellersStatus();
    }
    if (state.activeTab === 'creator' && state.itemTemplates.loaded) {
        renderItemCreator();
        renderItemCreatorStatus();
    }
    if (state.activeTab === 'buildings') {
        renderBuildingCreator();
        renderBuildingCreatorStatus();
    }
    // The Characters tab's per-row "needs patch?" comparison is against the
    // profile's equipment-slot target, so re-render it when the profile changes.
    if (state.activeTab === 'characters' && state.characters.loaded) {
        renderCharacters();
    }
    updateButtons();
    setBuildLog([{ kind: 'info', msg: 'Profile loaded: ' + state.current.name }]);
}

function applyProfileToUI() {
    const p = state.current;
    syncStackSizeUIFromState();
    const pr = (p.globals && p.globals.pickupRadius) || null;
    const pickupMul = pr && pr.multiplier != null ? pr.multiplier : null;
    const pickupOn = pickupMul != null && Math.abs(pickupMul - 1.0) > 1e-9;
    document.getElementById('pickup-enabled').checked = pickupOn;
    document.getElementById('pickup-multiplier').value =
        pickupOn ? pickupMul : 2.0;
    syncPickupReadout();
    const ftb = (p.globals && p.globals.fastTravelBells) || null;
    document.getElementById('bell-cap').value =
        ftb && ftb.bellCap != null ? ftb.bellCap : 10;
    document.getElementById('signal-fire-cap').value =
        ftb && ftb.signalFireCap != null ? ftb.signalFireCap : 3;
    const eqs = (p.globals && p.globals.equipmentSlots) || null;
    document.getElementById('ring-slots').value =
        eqs && eqs.ringSlots != null ? eqs.ringSlots : 1;
    document.getElementById('necklace-slots').value =
        eqs && eqs.necklaceSlots != null ? eqs.necklaceSlots : 1;
    syncEquipmentSlotsReadout();
    const sh = (p.globals && p.globals.shipSlots) || null;
    document.getElementById('ship-cargo-mult').value =
        sh && sh.cargoMultiplier != null ? sh.cargoMultiplier : 1;
    document.getElementById('ship-combat-slots').value =
        sh && sh.combatOrderSlots != null ? sh.combatOrderSlots : 1;
    syncShipSlotsReadout();
    const bs = (p.globals && p.globals.buildingStability) || null;
    document.getElementById('building-stability-enabled').checked =
        !!(bs && bs.enabled === true);
    const ns = (p.globals && p.globals.noSmoke) || null;
    document.getElementById('nosmoke-campfire').checked = !!(ns && ns.campfire === true);
    document.getElementById('nosmoke-furnace').checked  = !!(ns && ns.furnace === true);
    document.getElementById('nosmoke-kiln').checked     = !!(ns && ns.kiln === true);
    const mr = (p.globals && p.globals.minimapRange) || null;
    const minimapMul = mr && mr.multiplier != null ? mr.multiplier : null;
    const minimapOn = minimapMul != null && Math.abs(minimapMul - 1.0) > 1e-9;
    document.getElementById('minimap-enabled').checked = minimapOn;
    document.getElementById('minimap-multiplier').value =
        minimapOn ? minimapMul : 2.0;
    syncMinimapReadout();
    const nofog = (p.globals && p.globals.noFog) || null;
    document.getElementById('nofog-enabled').checked =
        !!(nofog && nofog.enabled === true);
    const lft = (p.globals && p.globals.landFastTravel) || null;
    document.getElementById('land-fast-travel-enabled').checked =
        !!(lft && lft.enabled === true);
    const br = (p.globals && p.globals.bonfireRadius) || null;
    const bonfireMul = br && br.multiplier != null ? br.multiplier : null;
    const bonfireOn = bonfireMul != null && Math.abs(bonfireMul - 1.0) > 1e-9;
    document.getElementById('bonfire-enabled').checked = bonfireOn;
    document.getElementById('bonfire-multiplier').value =
        bonfireOn ? bonfireMul : 2.0;
    syncBonfireReadout();
    const pxr = (p.globals && p.globals.pickaxeRange) || null;
    const pickaxeMul = pxr && pxr.multiplier != null ? pxr.multiplier : null;
    const pickaxeOn = pickaxeMul != null && Math.abs(pickaxeMul - 1.0) > 1e-9;
    document.getElementById('pickaxe-enabled').checked = pickaxeOn;
    document.getElementById('pickaxe-multiplier').value =
        pickaxeOn ? pickaxeMul : 1.4;
    syncPickaxeReadout();
    applyCooldownsToUI();
    applyStationsToUI();
    applyShipMusicToUI();
    applyLightingToUI();
    syncStackSizeInputsState();
    syncPickupInputState();
    syncBellInputState();
    syncBuildingStabilityInputState();
    syncNoSmokeInputState();
    syncMinimapInputState();
    syncBonfireInputState();
    syncPickaxeInputState();
    // Bonfire-music card: fire-and-forget refresh against the server
    // (the on-disk WAV is the source of truth, not state.current).
    if (typeof refreshBonfireMusicStatus === 'function') {
        refreshBonfireMusicStatus();
    }
    // Volume slider lives in state.current.globals.bonfireMusic.volume
    // (rides in the global PUT body, baked into the staged WAV as a
    // pre-encode PCM gain at build time).
    if (typeof syncBonfireMusicVolumeFromState === 'function') {
        syncBonfireMusicVolumeFromState();
    }
    renderProfileMeta();
    updateMiscTabIndicator();
}

function renderProfileMeta() {
    const p = state.current;
    const out = document.getElementById('profile-meta');
    if (!p) { out.replaceChildren(); return; }
    const frag = document.createDocumentFragment();
    if (state.isDirty) {
        const badge = document.createElement('span');
        badge.className = 'dirty-badge';
        badge.textContent = 'UNSAVED';
        frag.appendChild(badge);
    }
    frag.appendChild(document.createTextNode(p.description || ' '));
    out.replaceChildren(frag);
}

function populateProfileSelect() {
    const sel = document.getElementById('profile-select');
    state.profiles.sort((a, b) => a.name.localeCompare(b.name));
    sel.replaceChildren();
    for (const p of state.profiles) {
        const o = document.createElement('option');
        o.value = p.id;
        o.textContent = p.name;
        sel.appendChild(o);
    }
    syncNoProfileState();
}

function syncNoProfileState() {
    document.body.classList.toggle('no-profiles', state.profiles.length === 0);
}

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

function openPicker(input, ltId, addedIndex, mode) {
    closePicker();
    state.picker = { input, source: 'loot', type: mode, ltId, addedIndex };
    populatePicker(input.value);
    document.getElementById('picker-dropdown').hidden = false;
    positionPicker(input);
}

function openBuyerPicker(input, buyerId, recipeId, buyerField) {
    closePicker();
    state.picker = { input, source: 'buyer', type: 'item', buyerId, recipeId, buyerField };
    populatePicker('');
    document.getElementById('picker-dropdown').hidden = false;
    positionPicker(input);
    if (input.value) {
        try { input.select(); } catch (_) { /* ignore */ }
    }
}

function closePicker() {
    const dd = document.getElementById('picker-dropdown');
    if (dd) {
        dd.hidden = true;
        dd.replaceChildren();
    }
    state.picker = null;
}

function positionPicker(input) {
    const dd = document.getElementById('picker-dropdown');
    if (!dd || !input) return;
    const rect = input.getBoundingClientRect();
    dd.style.minWidth = Math.max(rect.width, 320) + 'px';

    dd.style.top = '0px';
    const ddHeight = dd.getBoundingClientRect().height;

    const spaceBelow = window.innerHeight - rect.bottom - 8;
    const spaceAbove = rect.top - 8;
    const flipUp = ddHeight > spaceBelow && spaceAbove > spaceBelow;
    dd.style.top = (flipUp
        ? Math.max(8, rect.top - ddHeight - 2)
        : rect.bottom + 2) + 'px';

    dd.style.left = rect.left + 'px';
    const ddRect = dd.getBoundingClientRect();
    const overshootRight = ddRect.right - window.innerWidth + 8;
    if (overshootRight > 0) {
        dd.style.left = Math.max(8, rect.left - overshootRight) + 'px';
    }
}

function buildPickerOption(pickId, opts) {
    const li = cloneTemplate('tpl-picker-option');
    li.dataset.pickId = pickId;
    if (opts.icon) {
        const img = document.createElement('img');
        img.src = opts.icon;
        img.loading = 'lazy';
        img.alt = '';
        li.querySelector('.placeholder-icon').replaceWith(img);
    } else {
        li.querySelector('.placeholder-icon').textContent = opts.glyph || '?';
    }
    const b = li.querySelector('.info b');
    b.textContent = opts.name;
    if (opts.badge) {
        b.appendChild(document.createTextNode(' '));
        const badge = document.createElement('span');
        badge.className = 'picker-badge custom';
        badge.textContent = opts.badge;
        b.appendChild(badge);
    }
    li.querySelector('.info small').textContent = opts.subtitle || '';
    return li;
}

function populatePicker(query) {
    const dd = document.getElementById('picker-dropdown');
    if (!dd || !state.picker) return;
    const q = (query || '').toLowerCase().trim();
    const rows = [];

    if (state.picker.source === 'vanillaMi') {
        for (const m of state.vanillaMaterials || []) {
            const name = m.displayName || '';
            const path = m.packagePath || '';
            if (q && !name.toLowerCase().includes(q) && !path.toLowerCase().includes(q)) continue;
            rows.push(buildPickerOption(path, { glyph: 'M', name: name, subtitle: path }));
        }
    } else if (state.picker.source === 'recipeResource') {
        // Custom items from the Item Creator first - same package-path
        // shape as vanilla resources (/R5BusinessRules/InventoryItems/
        // Custom/<id>.<id>), so the recipe patcher writes them verbatim
        // and the engine resolves them against the cooked DA in the
        // mod pak. Rendered with a "Custom" badge so they're visually
        // distinct from the vanilla resource catalog.
        for (const item of state.items || []) {
            if (!item || !item.isCustom) continue;
            const name = (item.meta && item.meta.name) || item.id;
            const id   = item.id || '';
            const path = item.path || '';
            if (!path) continue;
            if (q
                && !name.toLowerCase().includes(q)
                && !id.toLowerCase().includes(q)
                && !path.toLowerCase().includes(q)) continue;
            rows.push(buildPickerOption(path, { icon: item.icon || null, name: name, badge: 'Custom', subtitle: id }));
        }
        for (const r of state.vanillaResources || []) {
            const name = r.displayName || r.stem || '';
            const stem = r.stem || '';
            const path = r.packagePath || '';
            if (q
                && !name.toLowerCase().includes(q)
                && !stem.toLowerCase().includes(q)
                && !path.toLowerCase().includes(q)) continue;
            rows.push(buildPickerOption(path, { icon: r.iconUrl || null, name: name, subtitle: stem }));
        }
    } else if (state.picker.source === 'vanillaBuilding') {
        // Vanilla R5BuildingItem DA picker (Etappe I). Filters by both
        // the file stem (displayName) and the package path so users can
        // search by either form ("Bucket" or "BuildingDecoration").
        // Optional category facet via state.picker.category - the
        // building card carries a small dropdown above the search input.
        const wantCat = state.picker.category || '';
        for (const t of state.vanillaBuildingTemplates || []) {
            const name = t.displayName || '';
            const path = t.packagePath || '';
            const cat  = t.category || '';
            if (wantCat && cat !== wantCat) continue;
            if (q
                && !name.toLowerCase().includes(q)
                && !path.toLowerCase().includes(q)
                && !cat.toLowerCase().includes(q)) continue;
            rows.push(buildPickerOption(t.id, { glyph: 'B', name: name, subtitle: cat + ' · ' + path }));
        }
    } else if (state.picker.type === 'table') {
        for (const lt of state.lootTables) {
            if (q && !lt.id.toLowerCase().includes(q)) continue;
            const subtitle =
                (lt.category || '') +
                (lt.type ? ' · ' + lt.type : '') +
                (lt.entries ? ' · ' + lt.entries.length + ' entries' : '');
            rows.push(buildPickerOption(lt.id, { glyph: '▦', name: lt.id, subtitle: subtitle }));
        }
    } else {
        for (const item of state.items) {
            if (!state.itemPathsByItemId.has(item.id)) continue;
            const name = (item.meta && item.meta.name) || '';
            if (q && !item.id.toLowerCase().includes(q) && !name.toLowerCase().includes(q)) continue;
            const displayName = name || item.id;
            const subtitle =
                item.id +
                (item.itemClass ? ' · ' + item.itemClass : '') +
                (item.category  ? ' · ' + item.category  : '') +
                (item.rarity   ? ' · ' + item.rarity   : '');
            rows.push(buildPickerOption(item.id, { icon: item.icon || null, name: displayName, subtitle: subtitle }));
        }
    }

    if (rows.length === 0) {
        const li = document.createElement('li');
        li.className = 'picker-empty';
        li.textContent = 'No matches';
        dd.replaceChildren(li);
    } else {
        const frag = document.createDocumentFragment();
        for (const r of rows) frag.appendChild(r);
        dd.replaceChildren(frag);
    }
}

function syncPickerInputToType(selectEl) {
    const wrap = selectEl.closest('.picker-row');
    if (!wrap) return;
    const input = wrap.querySelector('input[data-add-form-target]');
    if (!input) return;
    const mode = selectEl.value;
    input.dataset.pickerMode = mode;
    input.value = '';

    if (mode === 'nodrop') {
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

    if (state.picker && state.picker.input === input) {
        populatePicker(input.value);
        positionPicker(input);
    } else {
        closePicker();
    }
}

// ---------------------------------------------------------------------------
// Tab modification indicators. A profile can touch many features spread across
// the Basic (misc) tab; this flags the tab button so it is obvious at a glance
// which tabs a profile modifies. Styling (.tab.has-mods) is left to misc.css.
//
// Misc detection is presence-based: every Misc feature deletes its globals key
// when it returns to vanilla (see tabs/misc.js set*FromUI), so the mere presence
// of a key means "this profile modifies it". bonfireMusic is the one exception -
// its node can linger in a vanilla state (no filename, volume back at 1.0), so
// it gets an explicit check. UI scale is also special: it is a machine-wide
// Engine.ini setting (not part of the profile globals), so it is tracked via
// state.uiScaleModified, set in tabs/misc.js loadUiScale/uiScaleApply.
function miscTabHasMods() {
    if (state.uiScaleModified) return true;
    const g = (state.current && state.current.globals) || null;
    if (!g) return false;
    const presenceKeys = [
        'stackSize', 'pickupRadius', 'fastTravelBells', 'equipmentSlots',
        'shipSlots', 'buildingStability', 'noFog', 'landFastTravel',
        'minimapRange', 'bonfireRadius', 'pickaxeRange', 'noSmoke', 'lighting',
    ];
    for (const k of presenceKeys) {
        if (g[k] != null) return true;
    }
    const bm = g.bonfireMusic;
    if (bm && (bm.originalFilename
            || (typeof bm.volume === 'number' && Math.abs(bm.volume - 1.0) > 1e-6))) {
        return true;
    }
    return false;
}

function updateMiscTabIndicator() {
    const btn = document.querySelector('.tab[data-tab="misc"]');
    if (btn) btn.classList.toggle('has-mods', miscTabHasMods());
}

function markDirty() {
    state.isDirty = true;
    updateButtons();
    renderProfileMeta();
    updateMiscTabIndicator();
}

function updateButtons() {
    const p = state.current;
    document.getElementById('btn-save').disabled       = !p || !state.isDirty;
    document.getElementById('btn-rename').disabled     = !p;
    document.getElementById('btn-delete').disabled     = !p;
    document.getElementById('btn-build').disabled      = !p;
    document.getElementById('btn-duplicate').disabled  = !p;
}

async function onSave() {
    const p = state.current;
    if (!p) return;
    const bustById = new Map();
    for (const c of (p.customItems || [])) {
        if (c && c.id && c._iconCacheBust) bustById.set(c.id, c._iconCacheBust);
    }
    const body = {
        id: p.id, name: p.name, description: p.description,
        createdAt: p.createdAt,
        globals: p.globals, overrides: p.overrides,
        lootOverrides: p.lootOverrides,
        buyerRecipes: p.buyerRecipes,
        buyerLists: p.buyerLists,
        sellerRecipes: p.sellerRecipes,
        sellerLists: p.sellerLists,
        customItems: p.customItems,
        customBuildings: p.customBuildings,
    };
    const updated = await api('PUT', '/api/profiles/' + encodeURIComponent(p.id), body);
    state.current = updated;
    state.current.globals       = state.current.globals       || {};
    state.current.overrides     = state.current.overrides     || {};
    state.current.lootOverrides = state.current.lootOverrides || {};
    state.current.buyerRecipes  = state.current.buyerRecipes  || {};
    state.current.buyerLists    = state.current.buyerLists    || {};
    state.current.sellerRecipes = state.current.sellerRecipes || {};
    state.current.sellerLists     = state.current.sellerLists     || {};
    state.current.customItems     = state.current.customItems     || [];
    state.current.customBuildings = state.current.customBuildings || [];
    for (const c of state.current.customItems) {
        if (c && c.id && bustById.has(c.id)) c._iconCacheBust = bustById.get(c.id);
    }
    syncCustomItemsIntoCatalog();
    rebuildSavedCustomItemIds();
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
        customItems: [],
        customBuildings: [],
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
    populateProfileSelect();
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
    setFooterCollapsed(false);
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
            if (data.pakPath) {
                const sizeKb = (data.sizeBytes / 1024).toFixed(1);
                lines.push({ kind: 'ok', msg:
                    'DONE - ' + data.pakPath + ' (' + sizeKb + ' KB, ' + data.fileCount + ' files)' });
            }
            if (data.pickupRadius) {
                const pr = data.pickupRadius;
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
            if (data.noFog && data.noFog.enabled) {
                lines.push({ kind: 'ok', msg:
                    'DONE - fog of war disabled (minimap + world map)' });
            }
            if (data.landFastTravel && data.landFastTravel.enabled) {
                lines.push({ kind: 'ok', msg:
                    'DONE - inland fast-travel placement enabled' });
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
            if (data.pickaxeRange) {
                const px = data.pickaxeRange;
                const mul = (px.multiplier || 1.0).toFixed(2);
                const tierCount = px.tiers ? px.tiers.length : 0;
                let summary = 'DONE - pickaxe range patched (' + mul + 'x; '
                    + tierCount + ' tier' + (tierCount === 1 ? '' : 's');
                if (tierCount > 0) {
                    const sample = px.tiers[0];
                    summary += ', TraceScaleModifier ' + sample.vanilla.toFixed(2)
                        + ' -> ' + sample.effective.toFixed(2);
                }
                summary += ')';
                lines.push({ kind: 'ok', msg: summary });
            }
            if (data.cooldowns) {
                const cd = data.cooldowns;
                const families = cd.families || [];
                for (const fam of families) {
                    const mul = (fam.multiplier || 1.0).toFixed(2);
                    lines.push({ kind: 'ok', msg:
                        'DONE - cooldown patched: ' + fam.family
                        + ' (' + mul + 'x; ' + fam.assetCount + ' asset'
                        + (fam.assetCount === 1 ? '' : 's')
                        + ', ' + fam.vanilla.toFixed(2) + ' -> '
                        + fam.effective.toFixed(2) + ')' });
                }
            }
            if (data.shipMusic) {
                const sm = data.shipMusic;
                const slots = sm.slots || [];
                for (const slot of slots) {
                    const name = slot.displayName || slot.originalFilename || '(unnamed)';
                    const diag = slot.diagnostic ? ' [' + slot.diagnostic + ']' : '';
                    lines.push({ kind: 'ok', msg:
                        'DONE - ship music replaced: ' + slot.title
                        + ' -> ' + name + diag });
                }
            }
            if (data.shipMusicAdd) {
                const sma = data.shipMusicAdd;
                const tracks = sma.tracks || [];
                const excluded = sma.excludedSlots || [];
                if (tracks.length > 0) {
                    lines.push({ kind: 'ok', msg:
                        'DONE - shanty roster extended (' + tracks.length + ' added track'
                        + (tracks.length === 1 ? '' : 's') + ')' });
                    for (const t of tracks) {
                        const dispTitle = t.title || t.trackKey;
                        const dur = t.durationSeconds ? t.durationSeconds.toFixed(1) + 's' : '?';
                        const bink = t.binkBytes ? (t.binkBytes / 1024).toFixed(0) + ' KB bink' : '';
                        const parts = [dur];
                        if (bink) parts.push(bink);
                        lines.push({ kind: 'ok', msg:
                            '  slot #' + (t.newIndex || '?') + ' ' + dispTitle
                            + ' (' + parts.join(', ') + ')' });
                    }
                }
                if (excluded.length > 0) {
                    lines.push({ kind: 'ok', msg:
                        'DONE - ' + excluded.length + ' vanilla shanty slot'
                        + (excluded.length === 1 ? '' : 's') + ' excluded from rotation' });
                    for (const e of excluded) {
                        lines.push({ kind: 'ok', msg:
                            '  excluded: ' + (e.title || e.stem) });
                    }
                }
            }
            if (data.bonfireMusic) {
                const bm = data.bonfireMusic;
                const name = bm.originalFilename || '(unnamed)';
                const diag = bm.diagnostic ? ' [' + bm.diagnostic + ']' : '';
                lines.push({ kind: 'ok', msg:
                    'DONE - bonfire music replaced: ' + (bm.title || 'The Hearth')
                    + ' -> ' + name + diag });
            }
            if (data.lighting) {
                const lg = data.lighting;
                const overall = (lg.overallMultiplier != null ? lg.overallMultiplier : 1.0).toFixed(2);
                const lights = lg.lights || [];
                lines.push({ kind: 'ok', msg:
                    'DONE - lighting patched (' + lights.length + ' light'
                    + (lights.length === 1 ? '' : 's')
                    + '; overall ' + overall + 'x)' });
                for (const light of lights) {
                    const mul = (light.multiplier || 1.0).toFixed(2);
                    const vanM = (light.vanilla / 100.0).toFixed(1);
                    const effM = (light.effective / 100.0).toFixed(1);
                    lines.push({ kind: 'ok', msg:
                        '  ' + light.stem + ' (' + mul + 'x): '
                        + vanM + 'm -> ' + effM + 'm' });
                }
            }
            if (data.cropGrowth) {
                const cg = data.cropGrowth;
                const mul = (cg.multiplier || 1.0).toFixed(2);
                lines.push({ kind: 'ok', msg:
                    'DONE - crop growth patched (' + mul + 'x; '
                    + cg.cropCount + ' crop'
                    + (cg.cropCount === 1 ? '' : 's')
                    + ', sample ' + cg.sampleVanillaTicks
                    + ' -> ' + cg.sampleEffectiveTicks + ' ticks)' });
            }
            if (data.cookingDuration) {
                const cdr = data.cookingDuration;
                const families = cdr.families || [];
                for (const fam of families) {
                    const mul = (fam.multiplier || 1.0).toFixed(2);
                    lines.push({ kind: 'ok', msg:
                        'DONE - cooking duration patched: ' + fam.family
                        + ' (' + mul + 'x; ' + fam.assetCount + ' recipe'
                        + (fam.assetCount === 1 ? '' : 's')
                        + ', avg ' + fam.vanillaAvg.toFixed(0)
                        + 's -> ' + fam.effectiveAvg.toFixed(0) + 's)' });
                }
                if (cdr.mergedWithTrade > 0) {
                    lines.push({ kind: 'ok', msg:
                        '  ' + cdr.mergedWithTrade
                        + ' recipe' + (cdr.mergedWithTrade === 1 ? '' : 's')
                        + ' merged with buyer/seller trade edits' });
                }
            }
            if (data.customBuildings && data.customBuildings.count > 0) {
                const cb = data.customBuildings;
                lines.push({ kind: 'ok', msg:
                    'DONE - ' + cb.count + ' custom building'
                    + (cb.count === 1 ? '' : 's') + ' patched + injected via DLL' });
                for (const b of cb.items || []) {
                    let line = '  ' + b.buildingId + ' (template ' + b.templateId
                        + '): ' + b.stagedFileCount + ' staged file'
                        + (b.stagedFileCount === 1 ? '' : 's');
                    if (b.warningCount > 0) {
                        line += ', ' + b.warningCount + ' warning'
                            + (b.warningCount === 1 ? '' : 's');
                    }
                    lines.push({ kind: b.warningCount > 0 ? 'warn' : 'ok', msg: line });
                    for (const w of b.warnings || []) {
                        lines.push({ kind: 'warn', msg: '    ' + w });
                    }
                }
            }
            if (!data.pakPath && !data.pickupRadius && !data.buildingStability
                && !data.noSmoke && !data.minimapRange && !data.noFog && !data.landFastTravel && !data.bonfireRadius
                && !data.pickaxeRange && !data.cooldowns
                && !data.shipMusic && !data.shipMusicAdd && !data.bonfireMusic && !data.lighting
                && !data.cropGrowth && !data.cookingDuration
                && !(data.customBuildings && data.customBuildings.count > 0)) {
                lines.push({ kind: 'err', msg: 'WARNING: build reported success but produced no output paks.' });
            }
        } else {
            lines.push({ kind: 'err', msg: 'ERROR: ' + (data.error || 'unknown') });
        }
        setBuildLog(lines);
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
    const frag = document.createDocumentFragment();
    lines.forEach((l, i) => {
        if (i > 0) frag.appendChild(document.createTextNode('\n'));
        const span = document.createElement('span');
        span.className = l.kind;
        span.textContent = '[' + l.kind.toUpperCase() + ']';
        frag.appendChild(span);
        frag.appendChild(document.createTextNode(' ' + l.msg));
    });
    out.replaceChildren(frag);
    // Auto-scroll to bottom so the last (most relevant) status line is visible
    // when the build finishes - mirrors the SSE-style append used in mods.js.
    out.scrollTop = out.scrollHeight;
}

function assetPathToId(assetPath) {
    if (!assetPath) return '';
    const s = assetPath;
    const dot = s.lastIndexOf('.');
    const slash = s.lastIndexOf('/');
    const cut = Math.max(dot, slash);
    return cut >= 0 && cut < s.length - 1 ? s.substring(cut + 1) : s;
}

function itemIdToAssetPath(itemId) {
    if (!itemId) return null;
    return state.itemPathsByItemId.get(itemId) || null;
}

const REQUIREMENT_PATH_PREFIX
    = '/R5BusinessRules/InventoryItems/DefaultItems/Trading/DA_Requirement_';
const REQUIREMENT_FACTIONS = ['Brethren', 'Bucaneers', 'Civilians', 'Smugglers'];
const REQUIREMENT_LEVELS   = [1, 2, 3, 4];

function requirementOptions() {
    const out = [{ value: 'None', label: 'None' }];
    for (const f of REQUIREMENT_FACTIONS) {
        for (const n of REQUIREMENT_LEVELS) {
            const stem = 'DA_Requirement_' + f + '_' + n;
            const path = REQUIREMENT_PATH_PREFIX + f + '_' + n + '.' + stem;
            out.push({ value: path, label: f + ' Rep ' + n });
        }
    }
    return out;
}

function buildRequirementSelectHtml(currentValue, recipeId, disabledAttr) {
    const value = (currentValue == null || currentValue === '') ? 'None' : currentValue;
    const opts = requirementOptions();
    const known = opts.some(o => o.value === value);
    let html = '';
    if (!known) {
        const short = shortenSellerRequirement(value) || value;
        html += '<option value="' + esc(value) + '" selected>'
              + esc(short + ' (custom)') + '</option>';
    }
    for (const o of opts) {
        const sel = (o.value === value && known) ? ' selected' : '';
        html += '<option value="' + esc(o.value) + '"' + sel + '>'
              + esc(o.label) + '</option>';
    }
    return '<select class="buyer-req-select"'
         +    ' data-buyer-field="requirement"'
         +    ' data-recipe-id="' + esc(recipeId) + '"'
         +    disabledAttr + '>'
         + html
         + '</select>';
}

function randomHex(n) {
    let s = '';
    while (s.length < n) s += Math.floor(Math.random() * 0x100000000).toString(16);
    return s.substring(0, n);
}

function shortenSellerRequirement(reqPath) {
    if (!reqPath) return '';
    const s = String(reqPath);
    const m = s.match(/DA_Requirement_([A-Za-z]+)_(\d+)/);
    if (m) return m[1] + ' Rep ' + m[2];
    const last = s.split('/').pop().split('.').pop();
    return last.replace(/^DA_Requirement_/, '').replace(/_/g, ' ');
}

// Escapes a value for use inside a double-quoted [attr="..."] selector -
// the only context cssEsc is used in - so escaping " and \ is necessary
// and sufficient (an identifier-grade CSS.escape would be equivalent here,
// just noisier). String() guards against a non-string id slipping through.
function cssEsc(s) {
    return String(s).replace(/(["\\])/g, '\\$1');
}

async function onQuit() {
    if (state.isDirty && !await confirm('Unsaved changes will be lost. Exit anyway?')) return;
    await fetch('/api/shutdown', { method: 'POST' }).catch(() => {});
    const info = document.createElement('div');
    info.className = 'shutdown-info';
    info.textContent = 'Server stopped. This window can be closed.';
    document.body.replaceChildren(info);
}

async function onPlay() {
    // Launch Windrose.exe via the backend. The button never disables - the
    // game runs as its own process, and if the user wants to relaunch (or
    // the launch failed and they want to retry) they shouldn't have to wait
    // for any state to clear.
    const btn = document.getElementById('btn-play');
    const originalLabel = btn.textContent;
    btn.disabled = true;
    try {
        const r = await fetch('/api/play', { method: 'POST' });
        const data = await r.json().catch(() => ({}));
        if (!r.ok || !data.success) {
            await alert('Could not launch Windrose:\n\n' + (data.error || ('HTTP ' + r.status)));
        }
        await new Promise(x => setTimeout(x, 1000))
    } catch (e) {
        await alert('Could not launch Windrose:\n\n' + (e && e.message ? e.message : String(e)));
    } finally {
        btn.disabled = false;
    }
}

async function onReport() {
    openReportModal();
}

function openReportModal() {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    const card = document.createElement('div');
    card.className = 'modal-card report-modal';

    const h = document.createElement('h2');
    h.textContent = 'Report Issue';
    h.style.margin = '0';
    card.appendChild(h);

    const intro = document.createElement('p');
    intro.className = 'modal-message';
    intro.textContent =
        'Sends a bug report with R5.log, Quartermaster_Inject.log, all '
      + 'profiles and the current ~mods file listing. Nothing is uploaded '
      + 'until you click "Send".';
    card.appendChild(intro);

    const nickLabel = document.createElement('label');
    nickLabel.textContent = 'Nickname';
    nickLabel.style.fontWeight = 'bold';
    card.appendChild(nickLabel);

    const nickInput = document.createElement('input');
    nickInput.type = 'text';
    nickInput.className = 'modal-input';
    nickInput.maxLength = 80;
    nickInput.placeholder = 'How we can reach you? (optional)';
    card.appendChild(nickInput);

    const titleLabel = document.createElement('label');
    titleLabel.textContent = 'Title';
    titleLabel.style.fontWeight = 'bold';
    card.appendChild(titleLabel);

    const titleInput = document.createElement('input');
    titleInput.type = 'text';
    titleInput.className = 'modal-input';
    titleInput.placeholder = 'Short summary (e.g. "Game crashes when building flame torch")';
    card.appendChild(titleInput);

    const descLabel = document.createElement('label');
    descLabel.textContent = 'Description';
    descLabel.style.fontWeight = 'bold';
    card.appendChild(descLabel);

    const descInput = document.createElement('textarea');
    descInput.className = 'modal-input';
    descInput.rows = 8;
    descInput.placeholder =
        'Steps to reproduce, expected vs actual behaviour, profile name, '
      + 'anything else relevant. Logs are attached automatically.';
    card.appendChild(descInput);

    const status = document.createElement('div');
    status.className = 'report-status';
    status.hidden = true;
    card.appendChild(status);

    const actions = document.createElement('div');
    actions.className = 'modal-actions';
    card.appendChild(actions);

    const cancel = document.createElement('button');
    cancel.type = 'button';
    cancel.textContent = 'Cancel';
    actions.appendChild(cancel);

    const send = document.createElement('button');
    send.type = 'button';
    send.className = 'primary';
    send.textContent = 'Send';
    actions.appendChild(send);

    overlay.appendChild(card);
    document.body.appendChild(overlay);

    const close = () => {
        document.removeEventListener('keydown', onKey, true);
        overlay.remove();
    };
    const onKey = (e) => {
        if (e.key === 'Escape') {
            e.preventDefault();
            close();
        }
    };
    document.addEventListener('keydown', onKey, true);

    cancel.addEventListener('click', close);

    send.addEventListener('click', async () => {
        const title = titleInput.value.trim();
        const description = descInput.value.trim();
        const nickname = nickInput.value.trim();
        if (!title) {
            setReportStatus(status, 'error', 'Title is required.');
            titleInput.focus();
            return;
        }
        if (!description) {
            setReportStatus(status, 'error', 'Description is required.');
            descInput.focus();
            return;
        }

        // Disable form during submit
        nickInput.disabled = true;
        titleInput.disabled = true;
        descInput.disabled = true;
        send.disabled = true;
        cancel.disabled = true;
        setReportStatus(status, 'pending',
            'Collecting logs, profiles and the mods listing, then uploading the report...');

        let data = null;
        let networkError = null;
        try {
            const r = await fetch('/api/report', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ title, description, nickname }),
            });
            try { data = await r.json(); } catch { data = null; }
        } catch (ex) {
            networkError = ex && ex.message ? ex.message : String(ex);
        }

        cancel.disabled = false;
        cancel.textContent = 'Close';

        if (networkError != null) {
            send.disabled = false;
            send.textContent = 'Retry';
            setReportStatus(status, 'error',
                'Could not reach the configurator backend: ' + networkError);
            return;
        }

        if (data && data.success) {
            const collected = (data.collected || []).join(', ');
            const sizeKb = data.attachmentSizeBytes
                ? (data.attachmentSizeBytes / 1024).toFixed(1) + ' KB'
                : 'unknown size';
            let msg = 'Report sent successfully ('
                + sizeKb + '). Attached: ' + collected;
            if (data.missing && data.missing.length) {
                msg += '\nSkipped (not found): ' + data.missing.join(', ');
            }
            if (data.serverResponse) {
                msg += '\nServer response: ' + data.serverResponse;
            }
            setReportStatus(status, 'success', msg);
            send.style.display = 'none';
            return;
        }

        // Backend returned a non-success payload (network ok, server-side
        // failure - either the report-collection step or the outbound POST).
        send.disabled = false;
        send.textContent = 'Retry';
        let errMsg = (data && data.error) ? data.error : 'Unknown error.';
        if (data && data.statusCode) {
            errMsg += '\nUpstream status: ' + data.statusCode;
        }
        if (data && data.serverResponse) {
            errMsg += '\nUpstream response: ' + data.serverResponse;
        }
        if (data && data.missing && data.missing.length) {
            errMsg += '\nNot collected: ' + data.missing.join(', ');
        }
        setReportStatus(status, 'error', errMsg);
    });

    titleInput.focus();
}

function setReportStatus(el, kind, message) {
    el.hidden = false;
    el.className = 'report-status report-status-' + kind;
    el.textContent = '';

    if (kind === 'pending') {
        const spinner = document.createElement('span');
        spinner.className = 'report-spinner';
        el.appendChild(spinner);
    }

    const text = document.createElement('span');
    text.className = 'report-status-text';
    text.textContent = message;
    el.appendChild(text);
}

function setFooterCollapsed(collapsed) {
    const footer = document.getElementById('footer');
    const btn    = document.getElementById('footer-toggle');
    footer.classList.toggle('collapsed', collapsed);
    btn.setAttribute('aria-expanded', String(!collapsed));
}

function bindHandlers() {
    document.getElementById('profile-select').addEventListener('change', async e => {
        const nextId = e.target.value;
        if (state.isDirty) {
            e.target.value = state.current.id;
            if (!await confirm('Discard unsaved changes?')) return;
        }
        loadProfile(nextId);
    });
    document.getElementById('btn-new').addEventListener('click',       onNew);
    document.getElementById('btn-no-profile-create').addEventListener('click', onNew);
    document.getElementById('btn-duplicate').addEventListener('click', onDuplicate);
    document.getElementById('btn-rename').addEventListener('click',    onRename);
    document.getElementById('btn-save').addEventListener('click',      onSave);
    document.getElementById('btn-delete').addEventListener('click',    onDelete);
    document.getElementById('btn-build').addEventListener('click',     onBuild);
    document.getElementById('btn-play').addEventListener('click',      onPlay);
    document.getElementById('btn-report').addEventListener('click',    onReport);
    document.getElementById('btn-quit').addEventListener('click',      onQuit);

    document.getElementById('footer-toggle').addEventListener('click', () => {
        const isCollapsed = document.getElementById('footer').classList.contains('collapsed');
        setFooterCollapsed(!isCollapsed);
    });

    for (const b of document.querySelectorAll('.tab')) {
        b.addEventListener('click', () => setActiveTab(b.dataset.tab));
    }

    document.getElementById('picker-dropdown').addEventListener('mousedown', onPickerClick);
    document.addEventListener('click',  onDocClickClosePicker);
    document.addEventListener('keydown', e => {
        if (e.key === 'Escape') {
            closePicker();
        }
    });
    window.addEventListener('resize', () => {
        if (state.picker) positionPicker(state.picker.input);
    });

    bindProfileDropHandlers();

    bindMiscHandlers();
    bindItemsHandlers();
    bindLootHandlers();
    bindModsHandlers();
    bindBuyersHandlers();
    bindSellersHandlers();
    bindCreatorHandlers();
    bindBuildingsHandlers();
    bindCooldownsHandlers();
    bindStationsHandlers();
    bindShipMusicHandlers();
    bindLightingHandlers();
    bindCharactersHandlers();
}

// Window-level drag-and-drop import for profile JSONs. A single JSON file
// dropped anywhere over the app window triggers POST /api/profiles/import
// with the parsed body. Conflict on existing id pops a confirm-dialog.
// We deliberately only react to drags that announce a 'Files' type so
// per-tab drop targets (ship-music audio etc.) can still own their own
// pointer events when active.
let _profileDropCounter = 0;

function bindProfileDropHandlers() {
    const overlay = document.getElementById('profile-drop-overlay');
    if (!overlay) return;

    // dragenter/leave fire for every child crossing the boundary; counter
    // logic keeps the overlay sticky while the user moves the cursor over
    // nested elements.
    window.addEventListener('dragenter', e => {
        if (!hasFilesPayload(e)) return;
        e.preventDefault();
        _profileDropCounter += 1;
        overlay.hidden = false;
    });
    window.addEventListener('dragover', e => {
        if (!hasFilesPayload(e)) return;
        e.preventDefault();
        if (e.dataTransfer) e.dataTransfer.dropEffect = 'copy';
    });
    window.addEventListener('dragleave', e => {
        if (!hasFilesPayload(e)) return;
        _profileDropCounter = Math.max(0, _profileDropCounter - 1);
        if (_profileDropCounter === 0) overlay.hidden = true;
    });
    window.addEventListener('drop', async e => {
        if (!hasFilesPayload(e)) return;
        e.preventDefault();
        _profileDropCounter = 0;
        overlay.hidden = true;
        const files = e.dataTransfer && e.dataTransfer.files
            ? Array.from(e.dataTransfer.files) : [];
        if (files.length === 0) return;
        if (files.length > 1) {
            await alert('Drop one profile file at a time.');
            return;
        }
        await handleProfileImportDrop(files[0]);
    });
}

function hasFilesPayload(e) {
    if (!e.dataTransfer) return false;
    const types = e.dataTransfer.types;
    if (!types) return false;
    // DOMStringList in some browsers, plain array in others.
    for (let i = 0; i < types.length; i++) {
        if (types[i] === 'Files') return true;
    }
    return false;
}

async function handleProfileImportDrop(file) {
    if (!file) return;
    // Two accepted shapes: a bare .json profile, or a .zip bundle that
    // contains <id>.json (and optionally a sibling <id>/ subfolder with
    // Icons / ShipMusic assets). The MIME sniff is permissive because
    // browsers report .zip as application/zip OR application/x-zip-compressed
    // OR empty, depending on platform - the extension is the primary cue.
    const name = file.name || '';
    const lowerType = (file.type || '').toLowerCase();
    const isZipExt = /\.zip$/i.test(name);
    const isZipMime = lowerType === 'application/zip'
                   || lowerType === 'application/x-zip-compressed'
                   || lowerType === 'application/x-compressed';
    const isJsonExt = /\.json$/i.test(name);
    const isJsonMime = lowerType === 'application/json';

    if (!isJsonExt && !isJsonMime && !isZipExt && !isZipMime) {
        await alert('Drop a .json profile file or a .zip profile bundle.');
        return;
    }

    if (state.isDirty) {
        if (!await confirm('You have unsaved changes that will be lost on switch. Continue with import?')) {
            return;
        }
        state.isDirty = false;
    }

    if (isZipExt || isZipMime) {
        await handleProfileImportZip(file);
        return;
    }

    let text;
    try {
        text = await file.text();
    } catch (ex) {
        await alert('Could not read file: ' + (ex && ex.message ? ex.message : String(ex)));
        return;
    }

    let parsed;
    try {
        parsed = JSON.parse(text);
    } catch (ex) {
        await alert('Not valid JSON: ' + ex.message);
        return;
    }

    if (!parsed || typeof parsed !== 'object') {
        await alert('JSON root must be a profile object.');
        return;
    }
    if (!parsed.id || typeof parsed.id !== 'string') {
        await alert('Profile JSON is missing the "id" field.');
        return;
    }
    if (!parsed.name || typeof parsed.name !== 'string') {
        await alert('Profile JSON is missing the "name" field.');
        return;
    }

    await importProfilePayload(parsed, false);
}

// ZIP-bundle import. Server inspects the archive, finds <id>.json + the
// matching <id>/ subfolder, and writes both into Profiles/. On id-conflict
// the server returns 409 with existingName so we can prompt for overwrite,
// mirroring the JSON path.
async function handleProfileImportZip(file) {
    await importProfileZipFile(file, false);
}

async function importProfileZipFile(file, overwrite) {
    const url = '/api/profiles/import-zip' + (overwrite ? '?overwrite=true' : '');
    const form = new FormData();
    form.append('file', file, file.name || 'profile.zip');

    let resp;
    try {
        resp = await fetch(url, { method: 'POST', body: form });
    } catch (ex) {
        await alert('Import failed: ' + (ex && ex.message ? ex.message : String(ex)));
        return;
    }

    if (resp.status === 409 && !overwrite) {
        let data = null;
        try { data = await resp.json(); } catch { /* ignore */ }
        const conflictId = data && data.conflictId ? data.conflictId : '(unknown id)';
        const existingName = data && data.existingName ? data.existingName : '(unknown name)';
        const ok = await confirm(
            'A profile with id "' + conflictId + '" already exists ("' + existingName + '"). '
            + 'Overwrite the JSON and replace its subfolder with the ZIP contents?');
        if (!ok) return;
        await importProfileZipFile(file, true);
        return;
    }

    if (!resp.ok) {
        let errMsg = 'HTTP ' + resp.status;
        try {
            const data = await resp.json();
            if (data && data.error) errMsg = data.error;
        } catch { /* ignore */ }
        await alert('Import failed: ' + errMsg);
        return;
    }

    let saved;
    try { saved = await resp.json(); } catch { saved = null; }
    // Server returns { profile, extractedFiles, subfolderFound }.
    const importedProfile = saved && saved.profile ? saved.profile : null;
    const importedId = importedProfile && importedProfile.id ? importedProfile.id : null;
    if (!importedId) {
        await alert('Import succeeded but server response did not include the profile id.');
        return;
    }

    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    await loadProfile(importedId);
}

async function importProfilePayload(payload, overwrite) {
    const url = '/api/profiles/import' + (overwrite ? '?overwrite=true' : '');
    let resp;
    try {
        resp = await fetch(url, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(payload),
        });
    } catch (ex) {
        await alert('Import failed: ' + (ex && ex.message ? ex.message : String(ex)));
        return;
    }

    if (resp.status === 409 && !overwrite) {
        let data = null;
        try { data = await resp.json(); } catch { /* ignore */ }
        const existingName = data && data.existingName ? data.existingName : '(unknown name)';
        const ok = await confirm(
            'A profile with id "' + payload.id + '" already exists ("' + existingName + '"). '
            + 'Overwrite with the imported profile?');
        if (!ok) return;
        await importProfilePayload(payload, true);
        return;
    }

    if (!resp.ok) {
        let errMsg = 'HTTP ' + resp.status;
        try {
            const data = await resp.json();
            if (data && data.error) errMsg = data.error;
        } catch { /* ignore */ }
        await alert('Import failed: ' + errMsg);
        return;
    }

    let saved;
    try {
        saved = await resp.json();
    } catch {
        saved = null;
    }
    const importedId = (saved && saved.id) || payload.id;

    // Refresh the profile list and switch to the imported profile so the
    // user sees the result immediately.
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    await loadProfile(importedId);
}

function onPickerClick(e) {
    const li = e.target.closest && e.target.closest('.picker-option');
    if (!li || !li.dataset.pickId) return;
    if (!state.picker) return;
    e.preventDefault();
    const picker = state.picker;
    closePicker();
    if (picker.source === 'buyer') {
        setBuyerEntryField(picker.buyerId, picker.recipeId, picker.buyerField, li.dataset.pickId);
        refreshBuyerCard(picker.buyerId);
        return;
    }
    if (picker.source === 'seller') {
        setSellerEntryField(picker.sellerId, picker.recipeId, picker.sellerField, li.dataset.pickId);
        refreshSellerCard(picker.sellerId);
        return;
    }
    if (picker.source === 'vanillaMi') {
        setVanillaMiParentForSlot(picker.buildingId, picker.slotIndex, li.dataset.pickId);
        return;
    }
    if (picker.source === 'recipeResource') {
        setRecipeResourceForRow(picker.buildingId, picker.rowIdx, li.dataset.pickId);
        return;
    }
    if (picker.source === 'vanillaBuilding') {
        setVanillaBuildingTemplateForCard(picker.buildingIndex, li.dataset.pickId);
        return;
    }
    confirmAddedEntry(picker.ltId, picker.addedIndex, picker.type, li.dataset.pickId);
}

function onDocClickClosePicker(e) {
    if (!state.picker) return;
    const dd = document.getElementById('picker-dropdown');
    if (dd && dd.contains(e.target)) return;
    if (e.target === state.picker.input) return;
    closePicker();
}

// kind:
//   'alert'   - single OK, no return value
//   'confirm' - OK/Cancel, returns true/false
//   'prompt'  - text input + OK/Cancel, returns string/null
//   'choice'  - arbitrary buttons; opts.buttons = [{label, value, primary?}]
//               returns the selected button's `value`. Escape resolves to
//               `cancelValue` (defaults to null) so callers can distinguish
//               "user dismissed" from any explicit choice.
function showModal({ kind, message, defaultValue, buttons, cancelValue }) {
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

        const effCancelValue = kind === 'choice'
            ? (cancelValue !== undefined ? cancelValue : null)
            : (kind === 'prompt' ? null : (kind === 'alert' ? undefined : false));
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
                close(effCancelValue);
            } else if (e.key === 'Enter') {
                if (kind === 'prompt' && e.target !== input) return;
                if (kind === 'choice') return; // Enter is ambiguous for multi-choice
                e.preventDefault();
                close(confirmValue());
            }
        };

        let firstFocusable = null;
        if (kind === 'choice') {
            const list = Array.isArray(buttons) ? buttons : [];
            for (const b of list) {
                const btn = document.createElement('button');
                btn.type = 'button';
                btn.textContent = String(b.label ?? '');
                if (b.primary) btn.className = 'primary';
                btn.addEventListener('click', () => close(b.value));
                actions.appendChild(btn);
                if (!firstFocusable || b.primary) firstFocusable = btn;
            }
        } else {
            if (kind !== 'alert') {
                const cancel = document.createElement('button');
                cancel.type = 'button';
                cancel.textContent = 'Cancel';
                cancel.addEventListener('click', () => close(effCancelValue));
                actions.appendChild(cancel);
            }
            const ok = document.createElement('button');
            ok.type = 'button';
            ok.className = 'primary';
            ok.textContent = 'OK';
            ok.addEventListener('click', () => close(confirmValue()));
            actions.appendChild(ok);
            firstFocusable = ok;
        }

        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey, true);

        if (input) {
            input.focus();
            input.select();
        } else if (firstFocusable) {
            firstFocusable.focus();
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

// Guard for actions that mutate the profile server-side AND then full-reload
// it via loadProfile() (which would silently discard any in-memory pending
// changes). If state.isDirty is set, presents a 3-button dialog so the user
// can save first, knowingly discard, or cancel out.
//
// Returns true if the caller may proceed (clean state, save succeeded, or
// user explicitly chose discard). Returns false if the user cancelled or
// the inline-save failed.
async function confirmDiscardUnsavedChanges(actionLabel) {
    if (!state.isDirty) return true;
    const label = actionLabel ? ' before ' + actionLabel : '';
    const choice = await showModal({
        kind: 'choice',
        message: 'You have unsaved profile changes. Save them' + label + ', or discard?',
        buttons: [
            { label: 'Save and continue', value: 'save', primary: true },
            { label: 'Discard changes',   value: 'discard' },
            { label: 'Cancel',            value: 'cancel' },
        ],
        cancelValue: 'cancel',
    });
    if (choice === 'save') {
        try {
            await onSave();
        } catch (err) {
            await alert('Save failed: ' + (err && err.message ? err.message : err));
            return false;
        }
        return true;
    }
    if (choice === 'discard') return true;
    return false; // cancel or dismissed
}
window.confirmDiscardUnsavedChanges = confirmDiscardUnsavedChanges;

// Custom modal for the manual Game-install override. Built on the same
// modal-overlay class as showModal() but with a richer body:
//   - text input pre-populated with the current override or Steam suggestion
//   - "Detect via Steam" button that fetches /api/game-install and writes
//     the auto-detected gameRoot into the input
//   - validation feedback line that updates after each save attempt
//   - Save / Clear override / Cancel buttons
//
// Resolves when the user closes the modal (any path). Save attempts
// re-prompt on failure so the user can correct the path without
// re-opening the modal.
function showGameInstallModal({ initialValue, status }) {
    return new Promise(resolve => {
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        const card = document.createElement('div');
        card.className = 'modal-card game-install-modal';

        const title = document.createElement('h3');
        title.textContent = 'Configure Windrose game install';
        title.style.margin = '0 0 .5em 0';
        card.appendChild(title);

        const intro = document.createElement('p');
        intro.className = 'modal-message';
        intro.textContent = 'Point Quartermaster at the folder that contains R5\\Binaries\\Win64\\Windrose-Win64-Shipping.exe (or WindroseServer-Win64-Shipping.exe on a dedicated server). Everything else (vanilla paks, ~mods folder, dxgi target) is derived from this.';
        card.appendChild(intro);

        // Steam-detect line (only shown if Steam suggested a path).
        const steamLine = document.createElement('p');
        steamLine.className = 'modal-message';
        steamLine.style.color = 'var(--text-dim)';
        steamLine.style.fontSize = '.9em';
        if (status && status.steamGameRoot) {
            steamLine.textContent = 'Steam auto-detect found: ' + status.steamGameRoot;
        } else if (status && status.steamError) {
            steamLine.textContent = 'Steam auto-detect: ' + status.steamError;
        } else {
            steamLine.textContent = 'Steam auto-detect found nothing.';
        }
        card.appendChild(steamLine);

        const input = document.createElement('input');
        input.type = 'text';
        input.className = 'modal-input';
        input.style.fontFamily = 'var(--monospace)';
        input.style.fontSize = 'var(--monospace-size)';
        input.value = initialValue || '';
        input.placeholder = 'e.g. C:\\Games\\Windrose';
        card.appendChild(input);

        const feedback = document.createElement('div');
        feedback.style.minHeight = '1.4em';
        feedback.style.marginTop = '.4em';
        feedback.style.fontSize = '.9em';
        feedback.style.color = 'var(--text-dim)';
        card.appendChild(feedback);

        const actions = document.createElement('div');
        actions.className = 'modal-actions';
        actions.style.flexWrap = 'wrap';
        card.appendChild(actions);
        overlay.appendChild(card);

        let closed = false;
        const close = (value) => {
            if (closed) return;
            closed = true;
            document.removeEventListener('keydown', onKey, true);
            overlay.remove();
            resolve(value);
        };
        const onKey = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                close(null);
            }
        };

        const setBusy = (busy) => {
            for (const b of actions.querySelectorAll('button')) b.disabled = busy;
            input.disabled = busy;
        };

        // "Detect via Steam" - re-fetches /api/game-install and writes the
        // detected gameRoot into the input. If Steam doesn't surface
        // anything, the feedback line explains why instead of clearing
        // what the user already typed.
        const detectBtn = document.createElement('button');
        detectBtn.type = 'button';
        detectBtn.textContent = 'Detect via Steam';
        detectBtn.addEventListener('click', async () => {
            setBusy(true);
            feedback.style.color = 'var(--text-dim)';
            feedback.textContent = 'Probing Steam...';
            try {
                const r = await fetch('/api/game-install');
                const data = await r.json();
                if (data && data.steamGameRoot) {
                    input.value = data.steamGameRoot;
                    feedback.textContent = 'Filled from Steam auto-detect.';
                } else {
                    feedback.style.color = 'var(--danger)';
                    feedback.textContent = (data && data.steamError) || 'Steam auto-detect found nothing.';
                }
            } catch (err) {
                feedback.style.color = 'var(--danger)';
                feedback.textContent = 'Steam probe failed: ' + (err && err.message ? err.message : err);
            } finally {
                setBusy(false);
            }
        });
        actions.appendChild(detectBtn);

        // "Clear override" - DELETEs the persisted override (back to Steam
        // auto-detect). Closes the modal with value 'cleared' so callers
        // can react.
        const clearBtn = document.createElement('button');
        clearBtn.type = 'button';
        clearBtn.textContent = 'Clear override';
        clearBtn.addEventListener('click', async () => {
            setBusy(true);
            feedback.style.color = 'var(--text-dim)';
            feedback.textContent = 'Clearing...';
            try {
                const r = await fetch('/api/game-install', { method: 'DELETE' });
                if (!r.ok) {
                    const errPayload = await r.json().catch(() => null);
                    feedback.style.color = 'var(--danger)';
                    feedback.textContent = (errPayload && errPayload.error) || ('HTTP ' + r.status);
                    setBusy(false);
                    return;
                }
                close('cleared');
            } catch (err) {
                feedback.style.color = 'var(--danger)';
                feedback.textContent = 'Clear failed: ' + (err && err.message ? err.message : err);
                setBusy(false);
            }
        });
        actions.appendChild(clearBtn);

        const cancelBtn = document.createElement('button');
        cancelBtn.type = 'button';
        cancelBtn.textContent = 'Cancel';
        cancelBtn.addEventListener('click', () => close(null));
        actions.appendChild(cancelBtn);

        const saveBtn = document.createElement('button');
        saveBtn.type = 'button';
        saveBtn.className = 'primary';
        saveBtn.textContent = 'Save';
        saveBtn.addEventListener('click', async () => {
            const path = (input.value || '').trim();
            if (!path) {
                feedback.style.color = 'var(--danger)';
                feedback.textContent = 'Path is empty. Enter a folder or click "Clear override" to revert to Steam auto-detect.';
                return;
            }
            setBusy(true);
            feedback.style.color = 'var(--text-dim)';
            feedback.textContent = 'Validating...';
            try {
                const r = await fetch('/api/game-install', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ gameRoot: path }),
                });
                if (!r.ok) {
                    const errPayload = await r.json().catch(() => null);
                    feedback.style.color = 'var(--danger)';
                    feedback.textContent = (errPayload && errPayload.error) || ('HTTP ' + r.status);
                    setBusy(false);
                    return;
                }
                close('saved');
            } catch (err) {
                feedback.style.color = 'var(--danger)';
                feedback.textContent = 'Save failed: ' + (err && err.message ? err.message : err);
                setBusy(false);
            }
        });
        actions.appendChild(saveBtn);

        document.body.appendChild(overlay);
        document.addEventListener('keydown', onKey, true);
        input.focus();
        input.select();
    });
}
window.showGameInstallModal = showGameInstallModal;
