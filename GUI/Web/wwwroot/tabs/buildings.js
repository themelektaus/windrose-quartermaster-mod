// Building Creator tab (Etappe G mesh-driven).
//
// Each card represents one CustomBuilding entry in the profile. The
// card collects the user-cooked asset metadata (folder + mesh + icon
// stems + asset prefix + display strings) plus, for each material
// slot the mesh exposes, a Vanilla MI parent pick + param overrides.
//
// Slot list is mesh-driven: as soon as cookedFolderPath + meshStem
// are filled, we hit /api/buildings/inspect-cooked to learn how many
// material slots the mesh has and what their names + per-slot
// user-cooked MI refs are. The user then picks a Vanilla MI parent
// per slot via /api/vanilla-materials autocomplete; picking the
// parent triggers /api/vanilla-materials/inspect which returns the
// param schema we render param controls for. If the cooked folder
// has a user-MI with the same parent-master as the picked Vanilla
// MI, we pre-fill its values as defaults.

// In-memory caches that survive across re-renders. The CookedFolder
// inspections are keyed by building.id; vanilla material inspections
// by packagePath (path is canonical / stable).
const _buildingScanTimers   = new Map();
const _buildingScanCache    = new Map();   // index -> last scanned path
const _cookedInspectionCache = new Map();  // building.id -> inspection
const _cookedInspectionTimers = new Map(); // building.id -> setTimeout
const _vanillaInspectCache  = new Map();   // packagePath -> MaterialInstanceDto

// Recipe (Etappe H2): per-template default cost.
const _recipeDefaultCache    = new Map();  // templateId -> BuildingRecipeInspectionDto
const _recipeFetchInflight   = new Map();  // templateId -> Promise

// Lookup cache for human labels when re-rendering rows whose itemPath
// the user picked in a previous session (state.vanillaResources is the
// authoritative source; this is a hot-path map for prettifyResourcePath).
const _resourceDisplayCache  = new Map();  // packagePath -> displayName

// Lazy loaders for the centralized picker dropdowns. Same UX as
// loot-tables: full catalog loaded once, filtered client-side. Loaded
// on the first focusin of the relevant input.
const _vanillaMaterialsLoad = { promise: null };
const _vanillaResourcesLoad = { promise: null };
const _vanillaBuildingsLoad = { promise: null };

// Cache for per-template inspections (Mesh/Icon/Recipe stems + FText
// keys). Keyed by templateId, populated on demand when the user picks a
// template OR when an existing building's templateId is rendered. Falls
// back to inflight-promise dedup so concurrent fetches share a single
// network round-trip.
const _vanillaBuildingInspectInflight = new Map();

function ensureVanillaMaterialsLoaded() {
    if (state.vanillaMaterials) return Promise.resolve(state.vanillaMaterials);
    if (_vanillaMaterialsLoad.promise) return _vanillaMaterialsLoad.promise;
    _vanillaMaterialsLoad.promise = api('GET', '/api/vanilla-materials?search=&limit=2000')
        .then(list => {
            state.vanillaMaterials = list || [];
            _vanillaMaterialsLoad.promise = null;
            return state.vanillaMaterials;
        })
        .catch(ex => {
            _vanillaMaterialsLoad.promise = null;
            state.vanillaMaterials = [];
            console.error('Failed to load vanilla materials catalog:', ex);
            return state.vanillaMaterials;
        });
    return _vanillaMaterialsLoad.promise;
}

function ensureVanillaResourcesLoaded() {
    if (state.vanillaResources) return Promise.resolve(state.vanillaResources);
    if (_vanillaResourcesLoad.promise) return _vanillaResourcesLoad.promise;
    _vanillaResourcesLoad.promise = api('GET', '/api/vanilla-resources?search=&limit=500')
        .then(list => {
            state.vanillaResources = list || [];
            // Seed the display cache for re-renders.
            for (const r of state.vanillaResources) {
                if (r && r.packagePath) {
                    _resourceDisplayCache.set(r.packagePath, r.displayName || r.stem || '');
                }
            }
            _vanillaResourcesLoad.promise = null;
            return state.vanillaResources;
        })
        .catch(ex => {
            _vanillaResourcesLoad.promise = null;
            state.vanillaResources = [];
            console.error('Failed to load vanilla resources catalog:', ex);
            return state.vanillaResources;
        });
    return _vanillaResourcesLoad.promise;
}

function ensureVanillaBuildingTemplatesLoaded() {
    if (state.vanillaBuildingTemplates) return Promise.resolve(state.vanillaBuildingTemplates);
    if (_vanillaBuildingsLoad.promise) return _vanillaBuildingsLoad.promise;
    _vanillaBuildingsLoad.promise = api('GET', '/api/building-templates/vanilla?search=&limit=1000')
        .then(list => {
            state.vanillaBuildingTemplates = list || [];
            _vanillaBuildingsLoad.promise = null;
            return state.vanillaBuildingTemplates;
        })
        .catch(ex => {
            _vanillaBuildingsLoad.promise = null;
            state.vanillaBuildingTemplates = [];
            console.error('Failed to load vanilla building templates catalog:', ex);
            return state.vanillaBuildingTemplates;
        });
    return _vanillaBuildingsLoad.promise;
}

// Inspect-on-demand for the per-template metadata (Mesh/Icon/Recipe
// stems + FText keys). Cached in state.vanillaBuildingInspections so
// repeat renders are free. The cache is keyed by the templateId the
// profile stores - the legacy "Painting"/"Bucket" sentinels don't
// resolve through this endpoint, callers handle them separately.
function ensureVanillaBuildingInspection(templateId) {
    if (!templateId) return Promise.resolve(null);
    if (state.vanillaBuildingInspections.has(templateId)) {
        return Promise.resolve(state.vanillaBuildingInspections.get(templateId));
    }
    const inflight = _vanillaBuildingInspectInflight.get(templateId);
    if (inflight) return inflight;
    const p = api('GET', '/api/building-templates/vanilla/inspect?id=' + encodeURIComponent(templateId))
        .then(dto => {
            state.vanillaBuildingInspections.set(templateId, dto);
            _vanillaBuildingInspectInflight.delete(templateId);
            return dto;
        })
        .catch(ex => {
            _vanillaBuildingInspectInflight.delete(templateId);
            console.warn('Vanilla template inspect failed for', templateId, ex);
            return null;
        });
    _vanillaBuildingInspectInflight.set(templateId, p);
    return p;
}

// Open the central picker over the building card's template input.
// Loads the catalog on first open (~849 entries) and uses the optional
// category facet to narrow the list.
//
// Note: we deliberately do NOT call openPicker() - that helper hard-codes
// `source: 'loot'` and would clobber our `vanillaBuilding` source,
// dropping populatePicker into the default items branch (which is what
// caused DA_DID_Item_Recipe_* entries to show up instead of DA_BI_*
// building templates). Mirror openVanillaMiPicker / openResourcePicker
// which manage the dropdown directly.
async function openVanillaBuildingPicker(inputEl, buildingIndex) {
    if (!inputEl) return;
    closePicker();
    const card = inputEl.closest && inputEl.closest('.building-card');
    const cat  = card ? card.querySelector('[data-building-template-category]') : null;
    state.picker = {
        source: 'vanillaBuilding',
        input: inputEl,
        buildingIndex: buildingIndex,
        category: cat ? cat.value || '' : '',
    };
    const dd = document.getElementById('picker-dropdown');
    if (dd) {
        if (state.vanillaBuildingTemplates) {
            populatePicker(inputEl.value);
        } else {
            dd.innerHTML = '<li class="picker-empty">Loading vanilla building templates...</li>';
        }
        dd.hidden = false;
    }
    positionPicker(inputEl);
    if (inputEl.value) {
        try { inputEl.select(); } catch (_) { /* ignore */ }
    }
    await ensureVanillaBuildingTemplatesLoaded();
    if (state.picker && state.picker.input === inputEl) {
        populatePicker(inputEl.value);
        positionPicker(inputEl);
    }
}

// Commit the picked template-id to the building card. Inspects in the
// background so the recipe-default cache + slot/template hint refresh
// without an extra round-trip when the user opens the recipe editor.
async function setVanillaBuildingTemplateForCard(buildingIndex, templateId) {
    if (!state.current) return;
    const list = state.current.customBuildings || [];
    const custom = list[buildingIndex];
    if (!custom) return;
    if (custom.templateId === templateId) return;
    custom.templateId = templateId;
    state.isDirty = true;
    // Clear the per-template recipe-default cache for the new id so
    // triggerRecipeRender re-fetches the new vanilla pre-fill.
    _recipeDefaultCache.delete(templateId);
    // Re-render the card so the template-name input + summary line
    // refresh. The render loop itself triggers ensureVanillaBuildingInspection
    // for the hint line.
    renderBuildingCreator();
    refreshSaveButton();
    // Warm the inspection cache so triggerRecipeRender hits a populated
    // cache on next call.
    ensureVanillaBuildingInspection(templateId);
}

async function loadBuildingTemplates() {
    const errBox = document.getElementById('buildings-error');
    if (errBox) {
        errBox.hidden = true;
        errBox.textContent = '';
    }
    try {
        const list = await api('GET', '/api/building-templates');
        const byId = new Map();
        for (const t of list || []) byId.set(t.id, t);
        state.buildingTemplates.list = list || [];
        state.buildingTemplates.byId = byId;
        state.buildingTemplates.loaded = true;
        state.buildingTemplates.error = null;
    } catch (ex) {
        state.buildingTemplates.error = (ex && ex.message) ? ex.message : String(ex);
        if (errBox) {
            errBox.hidden = false;
            errBox.textContent = 'Failed to load building templates: ' + state.buildingTemplates.error;
        }
    }
}

function newCustomBuildingId() {
    const bytes = new Uint8Array(4);
    if (window.crypto && window.crypto.getRandomValues) {
        window.crypto.getRandomValues(bytes);
    } else {
        for (let i = 0; i < 4; i++) bytes[i] = Math.floor(Math.random() * 256);
    }
    let hex = '';
    for (let i = 0; i < 4; i++) hex += bytes[i].toString(16).padStart(2, '0');
    return 'QmBldg_' + hex;
}

// -----------------------------------------------------------------------
// Top-level render.
// -----------------------------------------------------------------------
function renderBuildingCreator() {
    const list = document.getElementById('buildings-list');
    if (!list) return;
    if (!state.current) {
        list.innerHTML = '';
        return;
    }
    const customs = state.current.customBuildings || [];
    if (customs.length === 0) {
        list.innerHTML = '';
        return;
    }
    const parts = [];
    for (let i = 0; i < customs.length; i++) {
        parts.push(buildCustomBuildingCardHtml(customs[i], i));
    }
    list.innerHTML = parts.join('');

    // For each card with a cooked folder set, kick off background work:
    //  - the lightweight scan (file classification)
    //  - the deep inspect (mesh slots + user-MI defaults) if both
    //    cookedFolderPath + meshStem are present
    //  - the recipe default fetch (per templateId, cached) so the
    //    cost editor shows the vanilla pre-fill on initial render
    for (let i = 0; i < customs.length; i++) {
        const c = customs[i];
        if (!c) continue;
        if (c.cookedFolderPath) {
            scanCookedFolderForCard(i, c.cookedFolderPath);
        }
        if (c.cookedFolderPath && c.meshStem) {
            triggerCookedInspect(i, c.id, c.cookedFolderPath, c.meshStem);
        }
        if (c.templateId) {
            triggerRecipeRender(c);
            // Etappe I: kick off template inspection for the title hint line.
            // Only for Vanilla DA paths - the legacy "Painting"/"Bucket"
            // sentinels are not in the catalog and would 500. Heuristic:
            // Vanilla paths start with "/Game/Gameplay/Building/".
            if (c.templateId.indexOf('/Game/Gameplay/Building/') === 0
                && !state.vanillaBuildingInspections.has(c.templateId)) {
                ensureVanillaBuildingInspection(c.templateId).then(() => {
                    // Re-render to surface the resolved hint line.
                    renderBuildingCreator();
                });
            }
        }
    }
    // First-render of any card: fetch the category facet list so the
    // picker dropdown can offer it. Cheap (~8 entries) but only worth
    // doing when at least one card exists.
    if (!state.vanillaBuildingCategories && customs.length > 0) {
        api('GET', '/api/building-templates/vanilla/categories')
            .then(cats => {
                state.vanillaBuildingCategories = cats || [];
                renderBuildingCreator();
            })
            .catch(ex => {
                console.warn('Failed to load building categories:', ex);
                state.vanillaBuildingCategories = [];
            });
    }
}

function buildCustomBuildingCardHtml(custom, index) {
    if (!custom) return '';
    // Legacy compatibility: Painting/Bucket sentinels resolve through
    // the static catalog the backend still ships. Anything else is
    // resolved against the on-demand vanilla-inspection cache.
    const tpl = state.buildingTemplates.byId.get(custom.templateId) || null;
    const ins = (custom.templateId && state.vanillaBuildingInspections.get)
        ? state.vanillaBuildingInspections.get(custom.templateId) || null
        : null;

    const safeName   = escapeHtml(custom.name || '');
    const safeDesc   = escapeHtml(custom.description || '');
    const safePath   = escapeHtml(custom.cookedFolderPath || '');
    const safePrefix = escapeHtml(custom.assetPrefix || '');
    const safeMesh   = escapeHtml(custom.meshStem || '');
    const safeIcon   = escapeHtml(custom.iconStem || '');

    const missingHtml = renderMissingRequiredBanner(custom, tpl);

    // Template label: the picker shows the file stem (Vanilla DA) or
    // the legacy label ("Painting"/"Bucket"). The inspection cache
    // wins for Vanilla DA paths because the user picked them.
    let tplLabel = '';
    if (ins) tplLabel = ins.displayName || ins.id || '';
    else if (tpl) tplLabel = tpl.label || tpl.id || '';
    else if (custom.templateId) tplLabel = custom.templateId;

    // Per-template hint line: for legacy templates use the description;
    // for Vanilla DAs surface mesh/icon/category for context.
    let tplHint = '';
    if (tpl) {
        tplHint = escapeHtml(tpl.description || '')
            + (tpl.categoryTag ? ' (tab: ' + escapeHtml(tpl.categoryTag) + ')' : '');
    } else if (ins) {
        const parts = [];
        if (ins.category) parts.push(escapeHtml(ins.category));
        if (ins.meshStem) parts.push('mesh: ' + escapeHtml(ins.meshStem));
        if (ins.recipeStem) parts.push('cost: ' + escapeHtml(ins.recipeStem));
        tplHint = parts.join(' · ');
        if (ins.warnings && ins.warnings.length > 0) {
            tplHint += ' · ' + escapeHtml(ins.warnings[0]);
        }
    } else if (custom.templateId) {
        tplHint = '<em>resolving template...</em>';
    }

    // Category facet options for the picker. ~8 entries in 5.6.
    const categoryOpts = (state.vanillaBuildingCategories || []).map(c =>
        '<option value="' + escapeHtml(c) + '">' + escapeHtml(c) + '</option>'
    ).join('');

    return ''
        + '<li class="building-card" data-building-index="' + index + '" data-building-id="' + escapeHtml(custom.id) + '">'
        +   '<header class="building-card-header">'
        +     '<div class="building-titles">'
        +       '<div class="building-title-name">' + (safeName || '<em>(unnamed)</em>') + '</div>'
        +       '<div class="building-title-id">' + escapeHtml(custom.id) + '</div>'
        +       '<div class="building-title-template">'
        +         '<span>Template:</span>'
        +         '<input type="text" data-building-template-input value="' + escapeHtml(tplLabel)
        +           '" placeholder="Click to browse vanilla templates..." autocomplete="off">'
        +         (categoryOpts
                    ? '<select data-building-template-category title="Filter picker by category">'
                        + '<option value="">All categories</option>' + categoryOpts + '</select>'
                    : '')
        +       '</div>'
        +       (tplHint ? '<small class="building-title-template"><span>' + tplHint + '</span></small>' : '')
        +     '</div>'
        +     '<div class="building-card-actions">'
        +       '<button type="button" class="btn-link danger" data-building-action="delete" title="Delete building">Delete</button>'
        +     '</div>'
        +   '</header>'
        +   '<div class="building-fields">'
        +     missingHtml
        +     '<label class="building-field">'
        +       '<span>Name (in-game)</span>'
        +       '<input type="text" data-building-field="name" value="' + safeName + '" placeholder="Building display name">'
        +     '</label>'
        +     '<label class="building-field">'
        +       '<span>Asset prefix<span class="required-marker" title="required">*</span></span>'
        +       '<input type="text" data-building-field="assetPrefix" value="' + safePrefix + '" placeholder="QmPainting">'
        +     '</label>'
        +     '<label class="building-field building-field-wide">'
        +       '<span>Description (tooltip)</span>'
        +       '<textarea data-building-field="description" rows="2" placeholder="Tooltip text...">' + safeDesc + '</textarea>'
        +     '</label>'
        +     '<label class="building-field building-field-wide">'
        +       '<span>Cooked folder path<span class="required-marker" title="required">*</span></span>'
        +       '<div class="building-folder-row">'
        +         '<input type="text" data-building-field="cookedFolderPath" value="' + safePath
        +           '" placeholder="E:\\UEProj\\Saved\\Cooked\\Windows\\<Project>\\Content\\Quartermaster\\Items">'
        +         '<button type="button" class="btn-link" data-building-action="scan" title="Re-scan folder">Scan</button>'
        +       '</div>'
        +     '</label>'
        +     '<div class="building-field building-field-wide" data-building-scan-host>'
        +       (custom.cookedFolderPath
                    ? '<div class="building-scan"><em>Scanning...</em></div>'
                    : '<div class="building-scan"><em>Pick a folder above and click Scan, or just type a path - we&apos;ll scan automatically when you stop editing.</em></div>')
        +     '</div>'
        +     '<label class="building-field">'
        +       '<span>Mesh stem (SM_...)<span class="required-marker" title="required">*</span></span>'
        +       '<input type="text" data-building-field="meshStem" value="' + safeMesh + '" placeholder="SM_QmPainting_01">'
        +     '</label>'
        +     '<label class="building-field">'
        +       '<span>Icon stem (T_..._Icon)</span>'
        +       '<input type="text" data-building-field="iconStem" value="' + safeIcon + '" placeholder="T_QmPainting_Icon">'
        +     '</label>'
        +     '<div class="building-field building-field-wide" data-building-slots-host>'
        +       (custom.cookedFolderPath && custom.meshStem
                    ? '<div class="building-slots-status"><em>Reading mesh slots...</em></div>'
                    : '<div class="building-slots-status"><em>Set the cooked folder and mesh stem above to see material slots.</em></div>')
        +     '</div>'
        +     '<div class="building-field building-field-wide" data-building-recipe-host>'
        +       '<div class="building-recipe-status"><em>Loading build cost...</em></div>'
        +     '</div>'
        +   '</div>'
        + '</li>';
}

// -----------------------------------------------------------------------
// Missing-fields banner. Required:
//   - assetPrefix
//   - cookedFolderPath
//   - meshStem
//   - iconStem  (Etappe G: required so the build menu thumbnail isn't blank)
//   - per-slot VanillaMaterialParentPath (only when slot list is known)
// -----------------------------------------------------------------------
function renderMissingRequiredBanner(custom, _tpl) {
    if (!custom) return '';
    const missing = [];
    if (!custom.assetPrefix      || !custom.assetPrefix.trim())      missing.push('Asset prefix');
    if (!custom.cookedFolderPath || !custom.cookedFolderPath.trim()) missing.push('Cooked folder path');
    if (!custom.meshStem         || !custom.meshStem.trim())         missing.push('Mesh stem');
    if (!custom.iconStem         || !custom.iconStem.trim())         missing.push('Icon stem');

    // If we have an inspection result with slots, also check that
    // every slot has a Vanilla parent picked.
    const inspection = _cookedInspectionCache.get(custom.id);
    if (inspection && inspection.ok && Array.isArray(inspection.meshSlots)) {
        const slotsDict = custom.slots || {};
        for (const ms of inspection.meshSlots) {
            const key = String(ms.index);
            const sl = slotsDict[key];
            if (!sl || !sl.vanillaMaterialParentPath) {
                missing.push('Slot ' + ms.index + ' Vanilla parent');
            }
        }
    }
    if (missing.length === 0) return '';
    return '<div class="building-missing-fields">'
        + 'Required field' + (missing.length === 1 ? '' : 's') + ' empty: <strong>'
        + missing.map(escapeHtml).join(', ')
        + '</strong>. This building will be skipped at Build time.'
        + '</div>';
}

function refreshMissingFieldsBanner(card, custom) {
    if (!card) return;
    const fields = card.querySelector('.building-fields');
    if (!fields) return;
    const html = renderMissingRequiredBanner(custom, null);
    const existing = fields.querySelector(':scope > .building-missing-fields');
    if (!html) {
        if (existing) existing.remove();
        return;
    }
    if (existing) {
        existing.outerHTML = html;
    } else {
        fields.insertAdjacentHTML('afterbegin', html);
    }
}

// -----------------------------------------------------------------------
// Cooked-folder deep inspect (mesh slots + user MIs). Debounced.
// -----------------------------------------------------------------------
function triggerCookedInspect(index, buildingId, path, meshStem) {
    const key = buildingId;
    const existing = _cookedInspectionTimers.get(key);
    if (existing) clearTimeout(existing);
    const handle = setTimeout(() => {
        _cookedInspectionTimers.delete(key);
        runCookedInspect(index, buildingId, path, meshStem);
    }, 500);
    _cookedInspectionTimers.set(key, handle);
}

async function runCookedInspect(index, buildingId, rawPath, meshStem) {
    const path = (rawPath || '').trim();
    const stem = (meshStem || '').trim();
    if (!path || !stem) return;
    const card = document.querySelector('li.building-card[data-building-id="' + buildingId + '"]');
    const host = card ? card.querySelector('[data-building-slots-host]') : null;
    if (host) host.innerHTML = '<div class="building-slots-status"><em>Reading mesh slots...</em></div>';

    let inspection;
    try {
        const url = '/api/buildings/inspect-cooked?path='
            + encodeURIComponent(path)
            + '&meshStem=' + encodeURIComponent(stem);
        inspection = await api('GET', url);
    } catch (ex) {
        if (host) host.innerHTML = '<div class="building-slots-status building-scan-error">Failed to read mesh: '
            + escapeHtml((ex && ex.message) ? ex.message : String(ex)) + '</div>';
        return;
    }
    _cookedInspectionCache.set(buildingId, inspection);
    renderSlotPickersForCard(buildingId);
    if (card) refreshMissingFieldsBanner(card, currentBuildingById(buildingId));
}

function currentBuildingById(buildingId) {
    const arr = (state.current && state.current.customBuildings) || [];
    for (const b of arr) if (b && b.id === buildingId) return b;
    return null;
}

// -----------------------------------------------------------------------
// Per-slot UI render. One section per mesh material slot, each with:
//   - slot header (index + slotName + user-MI hint)
//   - Vanilla parent search dropdown
//   - param controls (rendered only once a parent is picked)
// -----------------------------------------------------------------------
function renderSlotPickersForCard(buildingId) {
    const card = document.querySelector('li.building-card[data-building-id="' + buildingId + '"]');
    if (!card) return;
    const host = card.querySelector('[data-building-slots-host]');
    if (!host) return;

    const building = currentBuildingById(buildingId);
    if (!building) return;
    const inspection = _cookedInspectionCache.get(buildingId);
    if (!inspection) {
        host.innerHTML = '<div class="building-slots-status"><em>Awaiting mesh inspection...</em></div>';
        return;
    }
    if (!inspection.ok) {
        host.innerHTML = '<div class="building-slots-status building-scan-error">'
            + escapeHtml(inspection.error || 'Mesh inspect failed') + '</div>';
        return;
    }
    if (!Array.isArray(inspection.meshSlots) || inspection.meshSlots.length === 0) {
        host.innerHTML = '<div class="building-slots-status"><em>Mesh has no material slots.</em></div>';
        return;
    }

    const parts = ['<div class="building-slots-list">'];
    parts.push('<div class="building-slots-list-title">Material slots (from ' + escapeHtml(inspection.meshStem) + ')</div>');
    for (const ms of inspection.meshSlots) {
        parts.push(renderSlotCardHtml(building, ms, inspection));
    }
    parts.push('</div>');
    host.innerHTML = parts.join('');

    // For slots that already have a Vanilla parent picked, fire the
    // inspect to (re)build the param controls.
    const slotsDict = building.slots || {};
    for (const ms of inspection.meshSlots) {
        const key = String(ms.index);
        const sl = slotsDict[key];
        if (sl && sl.vanillaMaterialParentPath) {
            renderSlotParams(buildingId, ms.index, sl.vanillaMaterialParentPath);
        }
    }
}

function renderSlotCardHtml(building, meshSlot, inspection) {
    const key = String(meshSlot.index);
    const sl = (building.slots && building.slots[key]) || {};
    const safeSlotName = escapeHtml(meshSlot.slotName || ('slot' + meshSlot.index));
    const safeUserMi = meshSlot.userMaterialStem
        ? '<small>User-cooked MI: <code>' + escapeHtml(meshSlot.userMaterialStem) + '</code></small>'
        : '<small><em>(no user MI bound to this slot)</em></small>';
    const safeParent = escapeHtml(sl.vanillaMaterialParentPath || '');
    const parentStem = safeParent ? extractStem(sl.vanillaMaterialParentPath) : '';
    return ''
        + '<div class="building-slot" data-slot-index="' + meshSlot.index + '">'
        +   '<div class="building-slot-header">'
        +     '<span class="building-slot-name">Slot ' + meshSlot.index + ' &mdash; ' + safeSlotName + '</span>'
        +     safeUserMi
        +   '</div>'
        +   '<div class="building-slot-parent">'
        +     '<label><span>Vanilla MI parent<span class="required-marker">*</span></span>'
        +       '<input type="text" data-slot-parent-search value="' + escapeHtml(parentStem) + '"'
        +         ' placeholder="Click to browse all Vanilla MIs, type to filter..." autocomplete="off" spellcheck="false">'
        +     '</label>'
        +     (safeParent
                ? '<div class="building-slot-parent-current">Current: <code>' + safeParent + '</code></div>'
                : '<div class="building-slot-parent-current"><em>Pick a parent above.</em></div>')
        +   '</div>'
        +   '<div class="building-slot-params" data-slot-params-host>'
        +     (safeParent
                ? '<div class="building-slot-params-status"><em>Loading parameters...</em></div>'
                : '<div class="building-slot-params-status"><em>Parameters will appear after picking a parent.</em></div>')
        +   '</div>'
        + '</div>';
}

function extractStem(packagePath) {
    if (!packagePath) return '';
    const idx = packagePath.lastIndexOf('/');
    return idx >= 0 ? packagePath.substring(idx + 1) : packagePath;
}

// -----------------------------------------------------------------------
// Vanilla MI picker - reuses the centralized #picker-dropdown (loot tab
// pattern). The picker:
//   - opens on focus (shows all 1134 entries until the user types)
//   - filters client-side as the user types
//   - commits on mousedown of an option so blur/change cannot race the
//     pick (this was the cause of the "dropdown re-opens after pick" bug)
//   - dismisses on outside-click via app.js's global onDocClickClosePicker
// -----------------------------------------------------------------------
async function openVanillaMiPicker(input, buildingId, slotIndex) {
    closePicker();
    state.picker = { input, source: 'vanillaMi', buildingId, slotIndex };
    // Show all immediately - if the catalog isn't cached yet a brief
    // "Loading..." placeholder appears until ensureVanillaMaterialsLoaded
    // settles. populatePicker is called again after the load resolves.
    const dd = document.getElementById('picker-dropdown');
    if (dd) {
        if (state.vanillaMaterials) {
            populatePicker(input.value);
        } else {
            dd.innerHTML = '<li class="picker-empty">Loading vanilla materials catalog...</li>';
        }
        dd.hidden = false;
    }
    positionPicker(input);
    if (input.value) {
        try { input.select(); } catch (_) { /* ignore */ }
    }
    await ensureVanillaMaterialsLoaded();
    // If the user already moved focus away, the picker may have closed.
    if (state.picker && state.picker.input === input) {
        populatePicker(input.value);
        positionPicker(input);
    }
}

// Called by app.js onPickerClick when the user picks a vanilla MI option.
// Mirrors the inline-button click path that used to live in
// onBuildingListClick, but without the racing input-change handler.
async function setVanillaMiParentForSlot(buildingId, slotIndex, packagePath) {
    const card = document.querySelector('li.building-card[data-building-id="' + buildingId + '"]');
    if (!card) return;
    const slotEl = card.querySelector('.building-slot[data-slot-index="' + slotIndex + '"]');
    if (!slotEl) return;
    const building = currentBuildingById(buildingId);
    if (!building) return;
    building.slots = building.slots || {};
    const slotKey = String(slotIndex);
    building.slots[slotKey] = building.slots[slotKey] || {};

    // If the parent actually changed, clear any param overrides - they
    // belonged to the previous MI's param schema and would either be
    // dropped silently by the patcher (param name not found on the new
    // parent) or, worse, leak old values onto same-named params of the
    // new parent. A fresh parent gets a fresh override dict; renderSlotParams
    // below repopulates pre-fills from the user-cooked MI if applicable.
    const oldPath = building.slots[slotKey].vanillaMaterialParentPath || '';
    if (oldPath && oldPath !== packagePath) {
        building.slots[slotKey].scalarParams  = {};
        building.slots[slotKey].vectorParams  = {};
        building.slots[slotKey].textureParams = {};
    }
    building.slots[slotKey].vanillaMaterialParentPath = packagePath;

    const searchBox = slotEl.querySelector('[data-slot-parent-search]');
    if (searchBox) searchBox.value = extractStem(packagePath);
    const currentEl = slotEl.querySelector('.building-slot-parent-current');
    if (currentEl) currentEl.innerHTML = 'Current: <code>' + escapeHtml(packagePath) + '</code>';

    await renderSlotParams(buildingId, slotIndex, packagePath);
    state.isDirty = true;
    updateButtons();
    refreshMissingFieldsBanner(card, building);
}

// -----------------------------------------------------------------------
// Slot param render. Triggered after a Vanilla parent is picked.
// Inspects the picked MI to learn its param schema, then renders one
// control per param. Pre-fills from the user-cooked MI's values when
// the parents match.
// -----------------------------------------------------------------------
async function renderSlotParams(buildingId, slotIndex, packagePath) {
    const card = document.querySelector('li.building-card[data-building-id="' + buildingId + '"]');
    if (!card) return;
    const slotEl = card.querySelector('.building-slot[data-slot-index="' + slotIndex + '"]');
    if (!slotEl) return;
    const host = slotEl.querySelector('[data-slot-params-host]');
    if (!host) return;
    host.innerHTML = '<div class="building-slot-params-status"><em>Loading parameters...</em></div>';

    let mi;
    if (_vanillaInspectCache.has(packagePath)) {
        mi = _vanillaInspectCache.get(packagePath);
    } else {
        try {
            mi = await api('GET', '/api/vanilla-materials/inspect?path=' + encodeURIComponent(packagePath));
            _vanillaInspectCache.set(packagePath, mi);
        } catch (ex) {
            host.innerHTML = '<div class="building-slot-params-status building-scan-error">Failed to inspect MI: '
                + escapeHtml((ex && ex.message) ? ex.message : String(ex)) + '</div>';
            return;
        }
    }

    const building = currentBuildingById(buildingId);
    if (!building) return;
    const inspection = _cookedInspectionCache.get(buildingId);
    const meshSlot = inspection && inspection.meshSlots
        ? inspection.meshSlots.find(s => s.index === slotIndex) : null;

    // Pre-fill source: if the user has a cooked MI with the same parent
    // master material, use its values as defaults.
    let userMi = null;
    if (meshSlot && meshSlot.userMaterialStem && inspection.userMaterialInstances) {
        const candidate = inspection.userMaterialInstances[meshSlot.userMaterialStem];
        if (candidate && candidate.parentPath === mi.parentPath) userMi = candidate;
    }

    // Initialize the slot dict + apply pre-fill for any params the
    // user hasn't overridden yet. Pre-fill is non-destructive: existing
    // user overrides win. Pre-fill is also vanilla-aware: values from
    // the user-cooked MI that exactly match the Vanilla parent's default
    // are NOT promoted to overrides. Writing vanilla-matching values
    // adds clutter (scalar/vector) or is actively harmful for textures
    // (redirects the texture path to /Game/Quartermaster/Items/<vanilla>
    // which doesn't exist).
    building.slots = building.slots || {};
    const slotKey = String(slotIndex);
    building.slots[slotKey] = building.slots[slotKey] || {};
    const sl = building.slots[slotKey];
    sl.vanillaMaterialParentPath = packagePath;
    sl.scalarParams  = sl.scalarParams  || {};
    sl.vectorParams  = sl.vectorParams  || {};
    sl.textureParams = sl.textureParams || {};
    if (userMi) {
        const EPS = 1e-4;
        const miScalars  = new Map((mi.scalars  || []).map(s => [s.name, s.value]));
        const miVectors  = new Map((mi.vectors  || []).map(v => [v.name, [v.r, v.g, v.b, v.a]]));
        const miTextures = new Map((mi.textures || []).map(t => [t.name, t.textureStem || '']));

        for (const s of userMi.scalars || []) {
            if (s.name in sl.scalarParams) continue;
            const def = miScalars.get(s.name);
            if (def !== undefined && Math.abs(def - s.value) < EPS) continue;
            sl.scalarParams[s.name] = s.value;
        }
        for (const v of userMi.vectors || []) {
            if (v.name in sl.vectorParams) continue;
            const def = miVectors.get(v.name);
            if (def
                && Math.abs(def[0] - v.r) < EPS
                && Math.abs(def[1] - v.g) < EPS
                && Math.abs(def[2] - v.b) < EPS
                && Math.abs(def[3] - v.a) < EPS) continue;
            sl.vectorParams[v.name] = [v.r, v.g, v.b, v.a];
        }
        for (const t of userMi.textures || []) {
            if (t.name in sl.textureParams) continue;
            const userStem = t.textureStem || '';
            if (!userStem) continue;
            const def = miTextures.get(t.name);
            if (def !== undefined && def === userStem) continue;
            sl.textureParams[t.name] = userStem;
        }
    }

    // Now render the param controls. One section per param type with
    // current value pre-populated.
    const parts = [];
    parts.push('<div class="building-slot-params-header">'
        + '<span>Params from <code>' + escapeHtml(mi.stem) + '</code></span>'
        + (userMi ? ' <small>(pre-filled from user-cooked <code>' + escapeHtml(meshSlot.userMaterialStem) + '</code>)</small>' : '')
        + '</div>');

    if (mi.scalars && mi.scalars.length > 0) {
        parts.push('<div class="building-slot-params-group"><div class="building-slot-params-group-title">Scalars</div>');
        for (const s of mi.scalars) {
            const current = sl.scalarParams[s.name];
            const v = (typeof current === 'number') ? current : s.value;
            parts.push('<div class="building-slot-param">'
                + '<label><span>' + escapeHtml(s.name) + '</span>'
                + '<input type="number" step="0.01" data-param-scalar="' + escapeHtml(s.name) + '" value="' + v + '">'
                + '</label>'
                + '<button type="button" class="btn-link" data-param-reset-scalar="' + escapeHtml(s.name) + '" data-default="' + s.value + '" title="Reset to Vanilla default (' + s.value + ')">reset</button>'
                + '</div>');
        }
        parts.push('</div>');
    }

    if (mi.vectors && mi.vectors.length > 0) {
        parts.push('<div class="building-slot-params-group"><div class="building-slot-params-group-title">Colors</div>');
        for (const vp of mi.vectors) {
            const current = sl.vectorParams[vp.name];
            const r = current ? current[0] : vp.r;
            const g = current ? current[1] : vp.g;
            const b = current ? current[2] : vp.b;
            const a = current ? current[3] : vp.a;
            const hex = rgbToHex(r, g, b);
            parts.push('<div class="building-slot-param">'
                + '<label><span>' + escapeHtml(vp.name) + '</span>'
                + '<input type="color" data-param-vector="' + escapeHtml(vp.name) + '" value="' + hex + '">'
                + '<input type="number" step="0.01" min="0" max="1" data-param-vector-alpha="' + escapeHtml(vp.name) + '" value="' + a + '" title="Alpha">'
                + '</label>'
                + '<button type="button" class="btn-link" data-param-reset-vector="' + escapeHtml(vp.name)
                    + '" data-default-r="' + vp.r + '" data-default-g="' + vp.g + '" data-default-b="' + vp.b + '" data-default-a="' + vp.a
                    + '" title="Reset to Vanilla default">reset</button>'
                + '</div>');
        }
        parts.push('</div>');
    }

    if (mi.textures && mi.textures.length > 0) {
        const cookedTextures = collectCookedTextureStems(building, inspection);
        parts.push('<div class="building-slot-params-group"><div class="building-slot-params-group-title">Textures</div>');
        for (const t of mi.textures) {
            const current = sl.textureParams[t.name];
            const stem = (typeof current === 'string') ? current : '';
            parts.push('<div class="building-slot-param">'
                + '<label><span>' + escapeHtml(t.name) + '</span>'
                + '<select data-param-texture="' + escapeHtml(t.name) + '">'
                +   '<option value="">(use Vanilla: ' + escapeHtml(t.textureStem || '?') + ')</option>'
                +   cookedTextures.map(s =>
                        '<option value="' + escapeHtml(s) + '"' + (s === stem ? ' selected' : '') + '>' + escapeHtml(s) + '</option>'
                    ).join('')
                + '</select>'
                + '</label>'
                + '<button type="button" class="btn-link" data-param-reset-texture="' + escapeHtml(t.name) + '" title="Reset to Vanilla">reset</button>'
                + '</div>');
        }
        parts.push('</div>');
    }

    if (parts.length === 1) {
        parts.push('<div class="building-slot-params-status"><em>This MI has no editable parameters.</em></div>');
    }

    host.innerHTML = parts.join('');
    // Dirty + button refresh is the caller's responsibility - this
    // function gets called both from user-initiated picks (which DO
    // mutate the profile) and from background re-renders on tab open
    // (which must NOT enable Save by themselves).
    refreshMissingFieldsBanner(card, building);
}

// Build the texture-stem dropdown options from files actually in the
// user's cooked folder (only T_*.uasset entries). The scan result
// gets cached per-card in _buildingScanCache - if it's not cached
// yet, the GUI fires a scan when the user picks a path, so this
// should be ready by the time the user opens the texture dropdown.
function collectCookedTextureStems(building, inspection) {
    const result = [];
    // The inspection itself only lists MI files. For texture stems,
    // we need to ask the scan-cooked endpoint. But that's already
    // cached on the card host via DOM (renderScanResult writes the
    // file list into the DOM). For simplicity we just pull from a
    // separate cache populated by the scan endpoint.
    const scanList = _buildingTextureStemCache.get(building.id);
    if (Array.isArray(scanList)) {
        for (const s of scanList) {
            if (s && s.startsWith('T_')) result.push(s);
        }
    }
    result.sort((a, b) => a.localeCompare(b));
    return result;
}

const _buildingTextureStemCache = new Map(); // building.id -> [stem...]

function rgbToHex(r, g, b) {
    const c = (x) => {
        const n = Math.max(0, Math.min(1, x || 0));
        return Math.round(n * 255).toString(16).padStart(2, '0');
    };
    return '#' + c(r) + c(g) + c(b);
}

function hexToRgb(hex) {
    if (!hex) return [0, 0, 0];
    const s = hex.replace('#', '');
    if (s.length !== 6) return [0, 0, 0];
    const n = parseInt(s, 16);
    return [
        ((n >> 16) & 0xff) / 255,
        ((n >>  8) & 0xff) / 255,
        ( n        & 0xff) / 255,
    ];
}

// -----------------------------------------------------------------------
// Status panel + card count.
// -----------------------------------------------------------------------
function renderBuildingCreatorStatus() {
    const customs = (state.current && state.current.customBuildings) || [];
    const tpls = state.buildingTemplates.list.length;
    const cEl = document.getElementById('buildings-stat-count');
    const tEl = document.getElementById('buildings-stat-templates');
    if (cEl) cEl.textContent = customs.length;
    if (tEl) tEl.textContent = tpls;
    const cnt = document.getElementById('buildings-count');
    if (cnt) cnt.textContent = customs.length === 0
        ? '' : (customs.length + ' building' + (customs.length === 1 ? '' : 's'));
}

async function onBuildingsNew() {
    if (!state.current) return;
    if (!state.buildingTemplates.loaded) {
        await loadBuildingTemplates();
    }
    const tpls = state.buildingTemplates.list;
    if (tpls.length === 0) {
        await alert('No building templates available - the backend returned an empty catalog.');
        return;
    }
    const template = tpls[0];
    const name = await prompt('Name for the new building:', 'My ' + template.label);
    if (name == null) return;
    const trimmed = String(name).trim();
    if (!trimmed) return;

    state.current.customBuildings = state.current.customBuildings || [];
    state.current.customBuildings.push({
        id: newCustomBuildingId(),
        templateId: template.id,
        name: trimmed,
        description: '',
        cookedFolderPath: '',
        assetPrefix: '',
        meshStem: '',
        iconStem: '',
        slots: {},  // populated dynamically after mesh inspection
    });
    state.isDirty = true;
    renderBuildingCreator();
    renderBuildingCreatorStatus();
    updateButtons();
}

// -----------------------------------------------------------------------
// Input handlers.
// -----------------------------------------------------------------------
function onBuildingListChange(e) {
    const t = e.target;
    if (!t || !t.dataset) return;
    const card = t.closest('li.building-card');
    if (!card) return;
    const index = parseInt(card.dataset.buildingIndex, 10);
    if (!isFinite(index)) return;
    const custom = (state.current && state.current.customBuildings || [])[index];
    if (!custom) return;

    if (t.dataset.buildingField) {
        const field = t.dataset.buildingField;
        if (field === 'name') {
            custom.name = t.value;
            const titleEl = card.querySelector('.building-title-name');
            if (titleEl) {
                const safe = escapeHtml(custom.name || '');
                titleEl.innerHTML = safe || '<em>(unnamed)</em>';
            }
        } else if (field === 'description') {
            custom.description = t.value;
        } else if (field === 'cookedFolderPath') {
            custom.cookedFolderPath = t.value || '';
            debounceScan(card, index, custom.cookedFolderPath);
            if (custom.meshStem) {
                triggerCookedInspect(index, custom.id, custom.cookedFolderPath, custom.meshStem);
            }
            refreshMissingFieldsBanner(card, custom);
        } else if (field === 'assetPrefix') {
            custom.assetPrefix = t.value || '';
            refreshMissingFieldsBanner(card, custom);
        } else if (field === 'meshStem') {
            custom.meshStem = t.value || '';
            if (custom.cookedFolderPath) {
                triggerCookedInspect(index, custom.id, custom.cookedFolderPath, custom.meshStem);
            }
            refreshMissingFieldsBanner(card, custom);
        } else if (field === 'iconStem') {
            custom.iconStem = t.value || '';
            refreshMissingFieldsBanner(card, custom);
        // (Etappe I removed the legacy `<select data-building-field=
        //  "templateId">` element; template picks now go through the
        //  central picker via data-building-template-input, handled
        //  below in setVanillaBuildingTemplateForCard.)
        } else {
            return;
        }
        state.isDirty = true;
    } else if (t.dataset.slotParentSearch !== undefined) {
        // Re-filter the central picker while the user types. The picker
        // was opened via the focusin handler; the input event just
        // updates the visible matches.
        if (state.picker && state.picker.input === t) {
            populatePicker(t.value);
            positionPicker(t);
        }
        return;  // not dirty until they actually pick a result
    } else if (t.dataset.buildingTemplateInput !== undefined) {
        // Etappe I: re-filter the vanilla-template picker as the user
        // types. Same input-as-search-box pattern as slotParentSearch /
        // recipeSearch above. Commit happens via onPickerClick.
        if (state.picker && state.picker.input === t) {
            populatePicker(t.value);
            positionPicker(t);
        }
        return;
    } else if (t.dataset.buildingTemplateCategory !== undefined) {
        // Category-facet dropdown changed; if the picker is open for this
        // card's input, push the new category into state.picker so the
        // next populatePicker call filters accordingly.
        const card = t.closest('li.building-card');
        if (card && state.picker && state.picker.source === 'vanillaBuilding') {
            const input = card.querySelector('[data-building-template-input]');
            if (input && state.picker.input === input) {
                state.picker.category = t.value || '';
                populatePicker(input.value);
                positionPicker(input);
            }
        }
        return;
    } else if (t.dataset.paramScalar !== undefined) {
        applyScalarParam(custom, t, 'paramScalar');
        state.isDirty = true;
    } else if (t.dataset.paramVector !== undefined || t.dataset.paramVectorAlpha !== undefined) {
        applyVectorParam(custom, t);
        state.isDirty = true;
    } else if (t.dataset.paramTexture !== undefined) {
        applyTextureParam(custom, t);
        state.isDirty = true;
    } else if (t.dataset.recipeSearch !== undefined) {
        // Mirror of slotParentSearch: re-filter the central picker as
        // the user types. Pick is committed via the central onPickerClick.
        if (state.picker && state.picker.input === t) {
            populatePicker(t.value);
            positionPicker(t);
        }
        return;  // not dirty until they actually pick a result
    } else if (t.dataset.recipeCount !== undefined) {
        const idx = parseInt(t.dataset.recipeCount, 10);
        if (!isFinite(idx)) return;
        const rows = ensureUserRecipeRows(custom);
        if (!rows[idx]) return;
        const v = parseInt(t.value, 10);
        rows[idx].count = Number.isFinite(v) && v >= 0 ? v : 0;
        // Source badge needs refreshing when we move from vanilla -> user.
        const defaults = _recipeDefaultCache.get(custom.templateId);
        renderRecipeForCard(custom.id, defaults);
        state.isDirty = true;
    } else {
        return;
    }
    renderBuildingCreatorStatus();
    updateButtons();
}

function applyScalarParam(custom, input, dsKey) {
    const slotEl = input.closest('.building-slot');
    if (!slotEl) return;
    const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
    if (!isFinite(slotIndex)) return;
    const slotKey = String(slotIndex);
    custom.slots = custom.slots || {};
    custom.slots[slotKey] = custom.slots[slotKey] || {};
    custom.slots[slotKey].scalarParams = custom.slots[slotKey].scalarParams || {};
    const name = input.dataset[dsKey];
    const v = parseFloat(input.value);
    if (Number.isFinite(v)) custom.slots[slotKey].scalarParams[name] = v;
    else delete custom.slots[slotKey].scalarParams[name];
}

function applyVectorParam(custom, input) {
    const slotEl = input.closest('.building-slot');
    if (!slotEl) return;
    const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
    if (!isFinite(slotIndex)) return;
    const slotKey = String(slotIndex);
    const name = input.dataset.paramVector || input.dataset.paramVectorAlpha;
    if (!name) return;
    custom.slots = custom.slots || {};
    custom.slots[slotKey] = custom.slots[slotKey] || {};
    custom.slots[slotKey].vectorParams = custom.slots[slotKey].vectorParams || {};
    const cur = custom.slots[slotKey].vectorParams[name] || [0, 0, 0, 1];
    if (input.dataset.paramVector !== undefined) {
        const rgb = hexToRgb(input.value);
        cur[0] = rgb[0]; cur[1] = rgb[1]; cur[2] = rgb[2];
    } else {
        const a = parseFloat(input.value);
        cur[3] = Number.isFinite(a) ? Math.max(0, Math.min(1, a)) : 1;
    }
    custom.slots[slotKey].vectorParams[name] = cur;
}

function applyTextureParam(custom, input) {
    const slotEl = input.closest('.building-slot');
    if (!slotEl) return;
    const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
    if (!isFinite(slotIndex)) return;
    const slotKey = String(slotIndex);
    custom.slots = custom.slots || {};
    custom.slots[slotKey] = custom.slots[slotKey] || {};
    custom.slots[slotKey].textureParams = custom.slots[slotKey].textureParams || {};
    const name = input.dataset.paramTexture;
    if (input.value) custom.slots[slotKey].textureParams[name] = input.value;
    else delete custom.slots[slotKey].textureParams[name];
}

async function onBuildingListClick(e) {
    const t = e.target;
    if (!t) return;

    // (Vanilla parent + recipe-resource picks now use the centralized
    //  #picker-dropdown; commit happens via app.js's onPickerClick which
    //  calls setVanillaMiParentForSlot / setRecipeResourceForRow.)

    // Reset buttons (scalar / vector / texture).
    if (t.dataset && t.dataset.paramResetScalar !== undefined) {
        const slotEl = t.closest('.building-slot');
        const card = t.closest('li.building-card');
        if (!slotEl || !card) return;
        const building = currentBuildingById(card.dataset.buildingId);
        const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
        if (!building || !isFinite(slotIndex)) return;
        const name = t.dataset.paramResetScalar;
        const def = parseFloat(t.dataset.default);
        const slotKey = String(slotIndex);
        if (building.slots && building.slots[slotKey] && building.slots[slotKey].scalarParams) {
            delete building.slots[slotKey].scalarParams[name];
        }
        const input = slotEl.querySelector('input[data-param-scalar="' + cssEscape(name) + '"]');
        if (input && Number.isFinite(def)) input.value = def;
        state.isDirty = true;
        updateButtons();
        return;
    }
    if (t.dataset && t.dataset.paramResetVector !== undefined) {
        const slotEl = t.closest('.building-slot');
        const card = t.closest('li.building-card');
        if (!slotEl || !card) return;
        const building = currentBuildingById(card.dataset.buildingId);
        const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
        if (!building || !isFinite(slotIndex)) return;
        const name = t.dataset.paramResetVector;
        const slotKey = String(slotIndex);
        if (building.slots && building.slots[slotKey] && building.slots[slotKey].vectorParams) {
            delete building.slots[slotKey].vectorParams[name];
        }
        const r = parseFloat(t.dataset.defaultR);
        const g = parseFloat(t.dataset.defaultG);
        const b = parseFloat(t.dataset.defaultB);
        const a = parseFloat(t.dataset.defaultA);
        const colorIn = slotEl.querySelector('input[data-param-vector="' + cssEscape(name) + '"]');
        const alphaIn = slotEl.querySelector('input[data-param-vector-alpha="' + cssEscape(name) + '"]');
        if (colorIn) colorIn.value = rgbToHex(r, g, b);
        if (alphaIn) alphaIn.value = Number.isFinite(a) ? a : 1;
        state.isDirty = true;
        updateButtons();
        return;
    }
    if (t.dataset && t.dataset.paramResetTexture !== undefined) {
        const slotEl = t.closest('.building-slot');
        const card = t.closest('li.building-card');
        if (!slotEl || !card) return;
        const building = currentBuildingById(card.dataset.buildingId);
        const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
        if (!building || !isFinite(slotIndex)) return;
        const name = t.dataset.paramResetTexture;
        const slotKey = String(slotIndex);
        if (building.slots && building.slots[slotKey] && building.slots[slotKey].textureParams) {
            delete building.slots[slotKey].textureParams[name];
        }
        const sel = slotEl.querySelector('select[data-param-texture="' + cssEscape(name) + '"]');
        if (sel) sel.value = '';
        state.isDirty = true;
        updateButtons();
        return;
    }

    // Recipe-section actions (add row, remove row, reset to vanilla).
    const recipeBtn = t.closest('button[data-recipe-action]');
    if (recipeBtn) {
        const card = recipeBtn.closest('li.building-card');
        if (!card) return;
        const building = currentBuildingById(card.dataset.buildingId);
        if (!building) return;
        const action = recipeBtn.dataset.recipeAction;
        const defaults = _recipeDefaultCache.get(building.templateId);
        if (action === 'add') {
            const rows = ensureUserRecipeRows(building);
            rows.push({ itemPath: '', count: 1 });
            renderRecipeForCard(building.id, defaults);
            state.isDirty = true;
        } else if (action === 'remove') {
            const idx = parseInt(recipeBtn.dataset.recipeRowIdx, 10);
            if (!isFinite(idx)) return;
            const rows = ensureUserRecipeRows(building);
            if (idx >= 0 && idx < rows.length) rows.splice(idx, 1);
            renderRecipeForCard(building.id, defaults);
            state.isDirty = true;
        } else if (action === 'reset') {
            // Revert to "use vanilla defaults" by dropping the user list.
            building.recipeCost = null;
            renderRecipeForCard(building.id, defaults);
            state.isDirty = true;
        }
        updateButtons();
        return;
    }

    // Card-level actions (delete, scan).
    const btn = t.closest('button[data-building-action]');
    if (!btn) return;
    const action = btn.dataset.buildingAction;
    const card = btn.closest('li.building-card');
    if (!card) return;
    const index = parseInt(card.dataset.buildingIndex, 10);
    if (!isFinite(index)) return;
    const customs = state.current && state.current.customBuildings;
    if (!customs || !customs[index]) return;

    if (action === 'delete') {
        const c = customs[index];
        const label = c.name || c.id;
        if (!await confirm('Delete building "' + label + '"?')) return;
        customs.splice(index, 1);
        state.isDirty = true;
        renderBuildingCreator();
        renderBuildingCreatorStatus();
        updateButtons();
    } else if (action === 'scan') {
        const c = customs[index];
        if (!c.cookedFolderPath) {
            await alert('Enter a cooked folder path first.');
            return;
        }
        scanCookedFolderForCard(index, c.cookedFolderPath);
        if (c.meshStem) {
            triggerCookedInspect(index, c.id, c.cookedFolderPath, c.meshStem);
        }
    }
}

function cssEscape(s) {
    if (window.CSS && window.CSS.escape) return window.CSS.escape(s);
    return String(s).replace(/(["\\])/g, '\\$1');
}

// -----------------------------------------------------------------------
// Lightweight cooked-folder scan (file classification). Same shape as
// the pre-G implementation; the only addition is that we record T_*
// stems into _buildingTextureStemCache so the texture-param dropdowns
// can show them.
// -----------------------------------------------------------------------
function debounceScan(cardEl, index, rawPath) {
    const existing = _buildingScanTimers.get(index);
    if (existing) clearTimeout(existing);
    const handle = setTimeout(() => {
        _buildingScanTimers.delete(index);
        scanCookedFolderForCard(index, rawPath);
    }, 400);
    _buildingScanTimers.set(index, handle);
}

async function scanCookedFolderForCard(index, rawPath) {
    const customs = state.current && state.current.customBuildings;
    if (!customs || !customs[index]) return;
    const card = document.querySelector('li.building-card[data-building-index="' + index + '"]');
    if (!card) return;
    const host = card.querySelector('[data-building-scan-host]');
    if (!host) return;

    const path = (rawPath || '').trim();
    if (!path) {
        host.innerHTML = '<div class="building-scan"><em>Enter a cooked folder path to scan.</em></div>';
        return;
    }
    const cached = _buildingScanCache.get(index);
    if (cached === path && host.querySelector('.building-scan-files')) {
        return;
    }

    host.innerHTML = '<div class="building-scan"><em>Scanning ' + escapeHtml(path) + '...</em></div>';
    try {
        const scan = await api('GET', '/api/buildings/scan-cooked?path=' + encodeURIComponent(path));
        _buildingScanCache.set(index, path);
        host.innerHTML = renderScanResult(scan, customs[index]);
        // Cache T_* stems so the texture-param dropdowns can list them.
        if (scan && Array.isArray(scan.entries)) {
            const textureStems = [];
            for (const e of scan.entries) {
                if (e.kind === 'texture' || e.kind === 'icon') textureStems.push(e.stem);
            }
            _buildingTextureStemCache.set(customs[index].id, textureStems);
        }
    } catch (ex) {
        host.innerHTML = '<div class="building-scan building-scan-error">Scan failed: '
            + escapeHtml((ex && ex.message) ? ex.message : String(ex)) + '</div>';
    }
}

function renderScanResult(scan, building) {
    if (!scan || !scan.exists) {
        return '<div class="building-scan building-scan-error">'
            + escapeHtml(scan && scan.error ? scan.error : 'Folder not found.')
            + '</div>';
    }
    const entries = scan.entries || [];
    const counts = {};
    for (const e of entries) counts[e.kind] = (counts[e.kind] || 0) + 1;
    const totalAssets = (counts.mesh || 0) + (counts.icon || 0) + (counts.texture || 0)
                      + (counts.material || 0) + (counts.matinst || 0)
                      + (counts.blueprint || 0) + (counts.data || 0);

    const warnings = [];
    if ((counts.mesh || 0) === 0) warnings.push('<span class="bad">No mesh (SM_*) found.</span>');
    if ((counts.icon || 0) === 0) warnings.push('<span class="bad">No icon (T_*_Icon) found.</span>');
    if ((counts.material || 0) + (counts.matinst || 0) > 0) {
        warnings.push('<span class="skip">'
            + ((counts.material || 0) + (counts.matinst || 0))
            + ' user-cooked material(s) - will be skipped at build (replaced by Vanilla-MI clone).</span>');
    }
    const stems = new Set(entries.map(e => e.stem));
    if (building && building.meshStem && !stems.has(building.meshStem)) {
        warnings.push('<span class="bad">Mesh stem "' + escapeHtml(building.meshStem) + '" not found in folder.</span>');
    }
    if (building && building.iconStem && !stems.has(building.iconStem)) {
        warnings.push('<span class="bad">Icon stem "' + escapeHtml(building.iconStem) + '" not found in folder.</span>');
    }

    const fileList = entries
        .filter(e => e.kind !== 'sidecar' && e.kind !== 'other')
        .map(e => '<li>'
            + '<span class="kind' + scanKindClass(e.kind) + '">' + escapeHtml(scanKindLabel(e.kind)) + '</span>'
            + '<span class="name">' + escapeHtml(e.name) + '</span>'
            + '</li>')
        .join('');

    const statusRow = '<div class="building-scan-status">'
        + '<span>' + entries.length + ' file(s), ' + totalAssets + ' asset(s)</span>'
        + (counts.mesh    ? '<span>mesh: ' + counts.mesh    + '</span>' : '')
        + (counts.icon    ? '<span>icon: ' + counts.icon    + '</span>' : '')
        + (counts.texture ? '<span>texture: ' + counts.texture + '</span>' : '')
        + ((counts.material || counts.matinst)
            ? '<span class="skip">material: ' + ((counts.material || 0) + (counts.matinst || 0)) + ' (skipped)</span>'
            : '')
        + '</div>';

    return '<div class="building-scan">'
        + statusRow
        + (warnings.length > 0 ? '<div>' + warnings.join(' ') + '</div>' : '')
        + (fileList ? '<ul class="building-scan-files">' + fileList + '</ul>' : '')
        + '</div>';
}

function scanKindLabel(k) {
    switch (k) {
        case 'mesh':      return 'Mesh';
        case 'icon':      return 'Icon';
        case 'texture':   return 'Texture';
        case 'material':  return 'Material';
        case 'matinst':   return 'MI';
        case 'blueprint': return 'Blueprint';
        case 'data':      return 'DataAsset';
        case 'sidecar':   return 'Sidecar';
        default:          return 'Other';
    }
}

function scanKindClass(k) {
    if (k === 'material' || k === 'matinst') return ' skip';
    return '';
}

// -----------------------------------------------------------------------
// Recipe editor (Etappe H2). Renders the per-building build-cost rows
// using a per-row resource search box + count input.
//
// Profile state contract:
//   custom.recipeCost === undefined / null  -> use template's Vanilla defaults
//   custom.recipeCost === []                 -> explicit "free build"
//   custom.recipeCost === [{itemPath,count}] -> user-edited list
// As soon as the user mutates the editor, we materialize the Vanilla
// defaults into custom.recipeCost so subsequent edits + save round-trip
// cleanly. The "Reset to Vanilla" button clears it back to null.
// -----------------------------------------------------------------------
async function triggerRecipeRender(custom) {
    if (!custom || !custom.id) return;
    // Fetch + cache the template's vanilla defaults once per process.
    const defaults = await fetchRecipeDefaults(custom.templateId);
    renderRecipeForCard(custom.id, defaults);
}

async function fetchRecipeDefaults(templateId) {
    if (!templateId) return null;
    if (_recipeDefaultCache.has(templateId)) {
        return _recipeDefaultCache.get(templateId);
    }
    const inflight = _recipeFetchInflight.get(templateId);
    if (inflight) return inflight;
    const p = api('GET', '/api/buildings/inspect-recipe?templateId=' + encodeURIComponent(templateId))
        .then(dto => {
            _recipeDefaultCache.set(templateId, dto);
            _recipeFetchInflight.delete(templateId);
            // Capture each itemPath -> ?  the displayName cache stays
            // sparse until the user opens a search; rendering pre-fill
            // rows we use a humanized fallback derived from the path.
            return dto;
        })
        .catch(ex => {
            _recipeFetchInflight.delete(templateId);
            return { ok: false, error: (ex && ex.message) ? ex.message : String(ex), defaultRecipeCost: [] };
        });
    _recipeFetchInflight.set(templateId, p);
    return p;
}

function renderRecipeForCard(buildingId, defaults) {
    const card = document.querySelector('li.building-card[data-building-id="' + buildingId + '"]');
    if (!card) return;
    const host = card.querySelector('[data-building-recipe-host]');
    if (!host) return;
    const custom = currentBuildingById(buildingId);
    if (!custom) return;

    const usingVanilla = (custom.recipeCost == null);
    const rows = usingVanilla
        ? ((defaults && Array.isArray(defaults.defaultRecipeCost)) ? defaults.defaultRecipeCost : [])
        : custom.recipeCost;

    const vanillaTag = (defaults && defaults.vanillaRecipeTag) || '';
    const defaultsErr = (defaults && defaults.ok === false) ? defaults.error : '';

    host.innerHTML = buildRecipeSectionHtml(rows, usingVanilla, vanillaTag, defaultsErr);
}

function buildRecipeSectionHtml(rows, usingVanilla, vanillaTag, errMsg) {
    const rowsHtml = rows.map((r, idx) => buildRecipeRowHtml(r, idx)).join('');
    const tagLine = vanillaTag
        ? '<div class="building-recipe-meta">Vanilla tag: <code>' + escapeHtml(vanillaTag) + '</code></div>'
        : '';
    const sourceBadge = usingVanilla
        ? '<span class="building-recipe-source vanilla">Vanilla defaults</span>'
        : '<span class="building-recipe-source user">Custom (overrides vanilla)</span>';
    const errHtml = errMsg
        ? '<div class="building-recipe-error">' + escapeHtml(errMsg) + '</div>'
        : '';
    const emptyHint = (rows.length === 0)
        ? '<div class="building-recipe-empty"><em>No build cost - building is free.</em></div>'
        : '';
    return ''
        + '<div class="building-recipe">'
        +   '<div class="building-recipe-header">'
        +     '<strong>Build cost</strong>'
        +     sourceBadge
        +     '<div class="building-recipe-actions">'
        +       '<button type="button" class="btn-link" data-recipe-action="add">+ Add resource</button>'
        +       (!usingVanilla ? '<button type="button" class="btn-link" data-recipe-action="reset" title="Discard overrides; use template default">Reset to Vanilla</button>' : '')
        +     '</div>'
        +   '</div>'
        +   errHtml
        +   tagLine
        +   '<ol class="building-recipe-rows">' + rowsHtml + '</ol>'
        +   emptyHint
        + '</div>';
}

function buildRecipeRowHtml(row, idx) {
    const itemPath = (row && row.itemPath) || '';
    const count    = (row && Number.isFinite(row.count)) ? row.count : 0;
    const display  = itemPath
        ? (_resourceDisplayCache.get(itemPath) || prettifyResourcePath(itemPath))
        : '';
    const safeDisplay = escapeHtml(display);
    const safePath    = escapeHtml(itemPath);

    return ''
        + '<li class="building-recipe-row" data-recipe-row="' + idx + '">'
        +   '<div class="building-recipe-search">'
        +     '<input type="text" data-recipe-search="' + idx + '" value="' + safeDisplay
        +       '" placeholder="Click to browse resources, type to filter..." autocomplete="off" spellcheck="false">'
        +     (itemPath
                ? '<div class="building-recipe-current"><code>' + safePath + '</code></div>'
                : '<div class="building-recipe-current"><em>Pick a resource</em></div>')
        +   '</div>'
        +   '<label class="building-recipe-count">'
        +     '<span>Count</span>'
        +     '<input type="number" min="1" max="999" step="1" data-recipe-count="' + idx + '" value="' + count + '">'
        +   '</label>'
        +   '<button type="button" class="btn-link danger" data-recipe-action="remove" data-recipe-row-idx="' + idx + '" title="Remove this row">Remove</button>'
        + '</li>';
}

function prettifyResourcePath(path) {
    if (!path) return '';
    const lastSlash = path.lastIndexOf('/');
    const tail = lastSlash >= 0 ? path.substring(lastSlash + 1) : path;
    const dot = tail.indexOf('.');
    const stem = dot >= 0 ? tail.substring(0, dot) : tail;
    let s = stem;
    if (s.startsWith('DA_DID_Resource_')) s = s.substring('DA_DID_Resource_'.length);
    else if (s.startsWith('DA_DID_')) s = s.substring('DA_DID_'.length);
    return s.replace(/_/g, ' ');
}

// Materialize the vanilla defaults onto custom.recipeCost if the user
// is still in "use vanilla" mode and starts editing. Returns the now-
// materialized array (always safe to mutate).
function ensureUserRecipeRows(custom) {
    if (custom.recipeCost == null) {
        const defaults = _recipeDefaultCache.get(custom.templateId);
        const seed = (defaults && Array.isArray(defaults.defaultRecipeCost))
            ? defaults.defaultRecipeCost
            : [];
        // Clone to avoid sharing the cache instance with future buildings.
        custom.recipeCost = seed.map(r => ({ itemPath: r.itemPath || '', count: r.count || 0 }));
    }
    return custom.recipeCost;
}

// Recipe-resource picker - reuses the centralized #picker-dropdown, same
// UX pattern as the loot-table item picker. Includes icon + name +
// subtitle for each resource.
async function openResourcePicker(input, buildingId, rowIdx) {
    closePicker();
    state.picker = { input, source: 'recipeResource', buildingId, rowIdx };
    const dd = document.getElementById('picker-dropdown');
    if (dd) {
        if (state.vanillaResources) {
            populatePicker(input.value);
        } else {
            dd.innerHTML = '<li class="picker-empty">Loading resources catalog...</li>';
        }
        dd.hidden = false;
    }
    positionPicker(input);
    if (input.value) {
        try { input.select(); } catch (_) { /* ignore */ }
    }
    await ensureVanillaResourcesLoaded();
    if (state.picker && state.picker.input === input) {
        populatePicker(input.value);
        positionPicker(input);
    }
}

// Called by app.js onPickerClick when the user picks a resource. Mirrors
// the now-removed recipePickBtn branch.
function setRecipeResourceForRow(buildingId, rowIdx, packagePath) {
    const building = currentBuildingById(buildingId);
    if (!building || !isFinite(rowIdx)) return;
    const rows = ensureUserRecipeRows(building);
    if (!rows[rowIdx]) rows[rowIdx] = { itemPath: '', count: 1 };
    rows[rowIdx].itemPath = packagePath;
    if (!Number.isFinite(rows[rowIdx].count) || rows[rowIdx].count <= 0) rows[rowIdx].count = 1;
    const defaults = _recipeDefaultCache.get(building.templateId);
    renderRecipeForCard(building.id, defaults);
    state.isDirty = true;
    updateButtons();
}

function onBuildingListFocusIn(e) {
    const t = e.target;
    if (!t || !t.dataset) return;

    // Vanilla MI parent search box -> open the centralized picker.
    if (t.dataset.slotParentSearch !== undefined) {
        const card = t.closest('li.building-card');
        const slotEl = t.closest('.building-slot');
        if (!card || !slotEl) return;
        const buildingId = card.dataset.buildingId;
        const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
        if (!isFinite(slotIndex)) return;
        openVanillaMiPicker(t, buildingId, slotIndex);
        return;
    }

    // Recipe-row resource search box -> open the centralized picker.
    if (t.dataset.recipeSearch !== undefined) {
        const card = t.closest('li.building-card');
        const rowEl = t.closest('li.building-recipe-row');
        if (!card || !rowEl) return;
        const buildingId = card.dataset.buildingId;
        const rowIdx = parseInt(rowEl.dataset.recipeRow, 10);
        if (!isFinite(rowIdx)) return;
        openResourcePicker(t, buildingId, rowIdx);
        return;
    }

    // Template picker input (Etappe I) -> open the Vanilla building DA
    // picker. Input is read-only so the user can only commit via the
    // central dropdown.
    if (t.dataset.buildingTemplateInput !== undefined) {
        const card = t.closest('li.building-card');
        if (!card) return;
        const index = parseInt(card.dataset.buildingIndex, 10);
        if (!isFinite(index)) return;
        openVanillaBuildingPicker(t, index);
        return;
    }
}

function bindBuildingsHandlers() {
    const btn = document.getElementById('btn-buildings-new');
    if (btn) btn.addEventListener('click', onBuildingsNew);

    const list = document.getElementById('buildings-list');
    if (list) {
        list.addEventListener('input',   onBuildingListChange);
        list.addEventListener('change',  onBuildingListChange);
        list.addEventListener('click',   onBuildingListClick);
        list.addEventListener('focusin', onBuildingListFocusIn);
        // Re-position the floating picker when the list scrolls so the
        // dropdown stays glued to the input. Mirrors the loot tab.
        list.addEventListener('scroll', () => {
            if (state.picker) positionPicker(state.picker.input);
        }, { passive: true });
    }
}
