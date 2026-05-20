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

    mods: {
        loaded: false,
        modsDir: null,
        files: [],
        error: null,
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

    buildingTemplates: {
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
    loadBuildingTemplates();

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
    }
}

const TAB_NAMES = ['misc', 'items', 'creator', 'buildings', 'loot', 'buyers', 'sellers', 'cooldowns', 'shipmusic', 'mods'];

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

    const status = await api('GET', '/api/setup/status');
    if (status.isReady) {
        await loadAppData();
        return;
    }

    showSetupOverlay(status);
}

document.addEventListener('DOMContentLoaded', () => {
    boot().catch(err => {
        document.body.innerHTML =
            '<pre style="color:#e16464;padding:2em;white-space:pre-wrap;">' +
            esc('Init failed: ' + err.message + '\n\n' + (err.stack || '')) +
            '</pre>';
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

function renderSetupChecks(status) {
    const ul = document.getElementById('setup-checks');
    const staticRows = [
        ['hasVanillaPak', 'Windrose install detected via Steam',
                          status.vanillaPakPath || status.vanillaPakError],
        ['hasUsmap',      'UE5 mappings file (.usmap) in mod root',
                          status.usmapPath || 'Drop a Ctrl+Num6 dump into the mod root.'],
    ];
    const sources = Array.isArray(status.sources) ? status.sources : [];
    const sourceRows = sources.map(s => {
        const detail = s.description || s.diskPath || '';
        return '<li class="' + (s.ok ? 'ok' : 'bad') + '">' +
            '<div><b>' + esc(s.label || s.key) + '</b>' +
            (detail ? '<br><small>' + esc(detail) + '</small>' : '') +
            '</div></li>';
    });
    const staticHtml = staticRows.map(([key, label, detail]) => {
        const ok = !!status[key];
        return '<li class="' + (ok ? 'ok' : 'bad') + '">' +
            '<div><b>' + esc(label) + '</b>' +
            (detail ? '<br><small>' + esc(detail) + '</small>' : '') +
            '</div></li>';
    });
    const iconsRow = '<li class="' + (status.hasIcons ? 'ok' : 'bad') + '">' +
        '<div><b>' + esc('Item icons extracted') + '</b>' +
        '<br><small>' + esc((status.iconsDir || '') + ' - produced by the icons step.') + '</small>' +
        '</div></li>';
    // ffmpeg is optional - WAV-only users never need it - so we render
    // the row in a neutral "info" state when absent (not red). Re-running
    // setup downloads it; absence does not block the configurator.
    const ffmpegCls = status.hasFfmpeg ? 'ok' : 'optional';
    const ffmpegDetail = status.hasFfmpeg
        ? (status.ffmpegPath || '')
        : 'Optional - one-time ~190 MB download. Only needed if you upload mp3 / ogg / flac / m4a / aac / opus in the Ship Music tab.';
    const ffmpegRow = '<li class="' + ffmpegCls + '">' +
        '<div><b>' + esc('ffmpeg (audio transcoder)') + '</b>' +
        '<br><small>' + esc(ffmpegDetail) + '</small>' +
        '</div></li>';
    ul.innerHTML = staticHtml.concat(sourceRows).concat([iconsRow, ffmpegRow]).join('');
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
        if (!state.buildingTemplates.loaded) {
            loadBuildingTemplates().then(() => {
                renderBuildingCreator();
                renderBuildingCreatorStatus();
            });
        } else {
            renderBuildingCreator();
            renderBuildingCreatorStatus();
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
    if (state.activeTab === 'buildings' && state.buildingTemplates.loaded) {
        renderBuildingCreator();
        renderBuildingCreatorStatus();
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
    syncStackSizeInputsState();
    syncPickupInputState();
    syncBellInputState();
    syncBuildingStabilityInputState();
    syncNoSmokeInputState();
    syncMinimapInputState();
    syncBonfireInputState();
    syncPickaxeInputState();
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
        dd.innerHTML = '';
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
            rows.push(
                '<li class="picker-option" data-pick-id="' + esc(path) + '">' +
                    '<div class="placeholder-icon">M</div>' +
                    '<div class="info">' +
                        '<b>' + esc(name) + '</b>' +
                        '<small>' + esc(path) + '</small>' +
                    '</div>' +
                '</li>');
        }
    } else if (state.picker.source === 'recipeResource') {
        for (const r of state.vanillaResources || []) {
            const name = r.displayName || r.stem || '';
            const stem = r.stem || '';
            const path = r.packagePath || '';
            if (q
                && !name.toLowerCase().includes(q)
                && !stem.toLowerCase().includes(q)
                && !path.toLowerCase().includes(q)) continue;
            const iconHtml = r.iconUrl
                ? '<img src="' + esc(r.iconUrl) + '" loading="lazy" alt="">'
                : '<div class="placeholder-icon">?</div>';
            rows.push(
                '<li class="picker-option" data-pick-id="' + esc(path) + '">' +
                    iconHtml +
                    '<div class="info">' +
                        '<b>' + esc(name) + '</b>' +
                        '<small>' + esc(stem) + '</small>' +
                    '</div>' +
                '</li>');
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
            rows.push(
                '<li class="picker-option" data-pick-id="' + esc(t.id) + '">' +
                    '<div class="placeholder-icon">B</div>' +
                    '<div class="info">' +
                        '<b>' + esc(name) + '</b>' +
                        '<small>' + esc(cat) + ' · ' + esc(path) + '</small>' +
                    '</div>' +
                '</li>');
        }
    } else if (state.picker.type === 'table') {
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
                && !data.noSmoke && !data.minimapRange && !data.bonfireRadius
                && !data.pickaxeRange && !data.cooldowns
                && !data.shipMusic
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
    out.innerHTML = lines.map(l =>
        '<span class="' + l.kind + '">[' + l.kind.toUpperCase() + ']</span> ' + esc(l.msg)
    ).join('\n');
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

function escapeHtml(s) {
    if (s == null) return '';
    return String(s)
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}

function cssEsc(s) {
    return s.replace(/(["\\])/g, '\\$1');
}

async function onQuit() {
    if (state.isDirty && !await confirm('Unsaved changes will be lost. Exit anyway?')) return;
    await fetch('/api/shutdown', { method: 'POST' }).catch(() => {});
    document.body.innerHTML = '<div class="shutdown-info">Server stopped. This window can be closed.</div>';
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
