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
// Default-texture stems shipped with the app
// (Tools/Templates/DefaultTextures/T_*). The per-slot texture
// dropdowns surface these as an "always available" optgroup on top
// of whatever the user-cooked folder contributes. Loaded once on
// first building-card render and re-used across cards.
const _defaultTextureStemsLoad = { promise: null };
let _defaultTextureStems = null;  // string[] once loaded

// Component-FX presets. Loaded once via /api/buildings/component-presets
// and surfaced as a per-building dropdown so the user can attach a
// donor-BP bundle (Niagara + Light + Audio for "torch"; AudioComponent
// for "audio") to any building.
const _componentPresetsLoad = { promise: null };
let _componentPresets = null;  // {id, displayName, description, kind}[] once loaded

// Cache for per-template inspections (Mesh/Icon/Recipe stems + FText
// keys). Keyed by templateId, populated on demand when the user picks a
// template OR when an existing building's templateId is rendered. Falls
// back to inflight-promise dedup so concurrent fetches share a single
// network round-trip.
const _vanillaBuildingInspectInflight = new Map();

// Loading placeholder for the centralized #picker-dropdown while a
// catalog lazy-loads. app.js's populatePicker rebuilds the list once
// the load settles.
function setPickerLoading(dd, text) {
    const li = document.createElement('li');
    li.className = 'picker-empty';
    li.textContent = text;
    dd.replaceChildren(li);
}

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

function ensureComponentPresetsLoaded() {
    if (_componentPresets) return Promise.resolve(_componentPresets);
    if (_componentPresetsLoad.promise) return _componentPresetsLoad.promise;
    _componentPresetsLoad.promise = api('GET', '/api/buildings/component-presets')
        .then(dto => {
            _componentPresets = (dto && Array.isArray(dto.presets)) ? dto.presets.slice() : [];
            _componentPresetsLoad.promise = null;
            return _componentPresets;
        })
        .catch(ex => {
            _componentPresetsLoad.promise = null;
            _componentPresets = [];
            console.error('Failed to load component presets:', ex);
            return _componentPresets;
        });
    return _componentPresetsLoad.promise;
}

function ensureDefaultTextureStemsLoaded() {
    if (_defaultTextureStems) return Promise.resolve(_defaultTextureStems);
    if (_defaultTextureStemsLoad.promise) return _defaultTextureStemsLoad.promise;
    _defaultTextureStemsLoad.promise = api('GET', '/api/buildings/default-textures')
        .then(dto => {
            _defaultTextureStems = (dto && Array.isArray(dto.stems)) ? dto.stems.slice() : [];
            _defaultTextureStemsLoad.promise = null;
            return _defaultTextureStems;
        })
        .catch(ex => {
            _defaultTextureStemsLoad.promise = null;
            _defaultTextureStems = [];
            console.error('Failed to load default-texture stems:', ex);
            return _defaultTextureStems;
        });
    return _defaultTextureStemsLoad.promise;
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
async function openVanillaBuildingPicker(inputEl, buildingIndex, selectAll) {
    if (!inputEl) return;
    if (selectAll === undefined) selectAll = true;
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
            setPickerLoading(dd, 'Loading vanilla building templates...');
        }
        dd.hidden = false;
    }
    positionPicker(inputEl);
    if (selectAll && inputEl.value) {
        try { inputEl.select() } catch (_) { /* ignore */ }
    }
    await ensureVanillaBuildingTemplatesLoaded();
    if (state.picker && state.picker.input === inputEl) {
        populatePicker(inputEl.value);
        positionPicker(inputEl);
    }
}

// Suppression flag set right after a template pick. The picker-option
// mousedown closes the dropdown and re-renders the building card
// synchronously, so the new template-input ends up right under the
// mouse cursor. The follow-up mouseup/click then focuses that new
// input and would re-open the picker via the focusin/click handlers,
// AND a possible `change` event on the old detached input bubbles to
// onBuildingListChange which also opens the picker.
//
// We use a "next-user-gesture" semantic instead of a time window: the
// flag stays armed until the user performs a fresh mousedown/keydown
// anywhere in the document (which can only happen AFTER the current
// gesture's mouseup+click+focusin chain has completed). A safety
// timeout disarms after 5s in case neither event ever fires (e.g.
// mouse leaves the window before release).
let _suppressBuildingTemplatePickerReopen = false;
function _armBuildingTemplatePickerReopenSuppression() {
    _suppressBuildingTemplatePickerReopen = true;
    let disarmed = false;
    const disarm = () => {
        if (disarmed) return;
        disarmed = true;
        _suppressBuildingTemplatePickerReopen = false;
        document.removeEventListener('mousedown', disarm, true);
        document.removeEventListener('keydown', disarm, true);
        clearTimeout(safety);
    };
    // Defer wiring the listeners by one tick so the current gesture's
    // tail events (mouseup -> click -> focusin) all see the flag still
    // set. Those fire synchronously after the current mousedown task;
    // setTimeout(0) callbacks run after them.
    setTimeout(() => {
        if (disarmed) return;
        document.addEventListener('mousedown', disarm, true);
        document.addEventListener('keydown', disarm, true);
    }, 0);
    const safety = setTimeout(disarm, 5000);
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
    // Clear the per-template recipe-default cache for the new id so
    // triggerRecipeRender re-fetches the new vanilla pre-fill.
    _recipeDefaultCache.delete(templateId);
    // Arm the suppression *before* the re-render so the focusin/click
    // (and any change event on the old detached input) that the mouse
    // release triggers is ignored. The flag stays armed until the
    // user's *next* fresh mousedown/keydown disarms it.
    _armBuildingTemplatePickerReopenSuppression();
    // Re-render the card so the template-name input + summary line
    // refresh. The render loop itself triggers ensureVanillaBuildingInspection
    // for the hint line.
    renderBuildingCreator();
    // markDirty() flips state.isDirty AND wakes up the Save button
    // (updateButtons) + the UNSAVED badge (renderProfileMeta). Setting
    // state.isDirty by hand without these would leave the Save button
    // greyed out - the very bug this call fixes.
    if (typeof markDirty === 'function') markDirty();
    // Warm the inspection cache so triggerRecipeRender hits a populated
    // cache on next call.
    ensureVanillaBuildingInspection(templateId);
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
//
// Active-card model: only one building card is rendered at a time. The
// dropdown next to "New Building" picks which one. This keeps the panel
// readable when many buildings exist AND avoids parallel inspect/scan
// requests stepping on each other (retoc spawns, UAssetAPI's Usmap
// construction etc. are not thread-safe under concurrent load).
// -----------------------------------------------------------------------
function renderBuildingCreator() {
    const list   = document.getElementById('buildings-list');
    const picker = document.getElementById('buildings-active-picker');
    const pickerWrap = picker ? picker.closest('.building-active-pick') : null;
    if (!list) return;

    if (!state.current) {
        list.replaceChildren();
        if (picker) picker.replaceChildren();
        if (pickerWrap) pickerWrap.hidden = true;
        return;
    }

    const customs = state.current.customBuildings || [];

    if (customs.length === 0) {
        list.replaceChildren();
        if (picker) picker.replaceChildren();
        if (pickerWrap) pickerWrap.hidden = true;
        state.buildingCreatorActiveId = null;
        return;
    }

    // Resolve the active id: prefer the previously-selected one if it
    // still exists, otherwise fall back to the first entry.
    let activeId = state.buildingCreatorActiveId;
    let activeIndex = customs.findIndex(c => c && c.id === activeId);
    if (activeIndex < 0) {
        activeIndex = 0;
        activeId = customs[0].id;
        state.buildingCreatorActiveId = activeId;
    }

    // Populate / refresh the dropdown. Hidden when only one building
    // exists (no choice to make).
    if (picker) {
        picker.replaceChildren();
        for (let i = 0; i < customs.length; i++) {
            const c = customs[i];
            if (!c) continue;
            const label = (c.name && c.name.trim()) ? c.name : '(unnamed)';
            picker.appendChild(new Option(label, c.id));
        }
        picker.value = activeId;
    }
    if (pickerWrap) pickerWrap.hidden = (customs.length <= 1);

    // Render ONLY the active card.
    const active = customs[activeIndex];
    const activeCard = buildCustomBuildingCardNode(active, activeIndex);
    if (activeCard) list.replaceChildren(activeCard);
    else list.replaceChildren();

    // Kick off background work for the active card only:
    //  - lightweight scan (file classification)
    //  - deep inspect (mesh slots + user-MI defaults) if both
    //    cookedFolderPath + meshStem are present
    //  - recipe default fetch (per templateId, cached) so the
    //    cost editor shows the vanilla pre-fill on initial render
    if (active) {
        if (active.cookedFolderPath) {
            scanCookedFolderForCard(activeIndex, active.cookedFolderPath);
        }
        if (active.cookedFolderPath && active.meshStem) {
            triggerCookedInspect(activeIndex, active.id, active.cookedFolderPath, active.meshStem);
        }
        if (active.templateId) {
            triggerRecipeRender(active);
            // Etappe I: kick off template inspection for the title hint line.
            // Only for Vanilla DA paths - the legacy "Painting"/"Bucket"
            // sentinels are not in the catalog and would 500. Heuristic:
            // Vanilla paths start with "/Game/Gameplay/Building/".
            if (active.templateId.indexOf('/Game/Gameplay/Building/') === 0
                && !state.vanillaBuildingInspections.has(active.templateId)) {
                ensureVanillaBuildingInspection(active.templateId).then(() => {
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
    // First-render: also kick off the default-texture-stems load so
    // the per-slot texture dropdowns can list the shipped stems
    // (served by /api/buildings/default-textures, see
    // DefaultTextureProvider.Stems) without the user having to cook
    // them into their own folder. Re-render once the
    // catalog arrives so the dropdown surfaces the new optgroup
    // (slot UI re-renders via triggerCookedInspect on tab interaction
    // anyway, but the re-render ensures the group appears immediately
    // even on a freshly-opened card).
    if (!_defaultTextureStems && customs.length > 0) {
        ensureDefaultTextureStemsLoaded().then(() => {
            renderBuildingCreator();
        });
    }
    // Same lazy-load pattern for the component-preset catalog so the
    // per-building Component Preset dropdown can offer the shipped
    // presets. The first render shows a "(loading...)" placeholder;
    // the re-render after the catalog arrives swaps in the real list.
    if (!_componentPresets && customs.length > 0) {
        ensureComponentPresetsLoaded().then(() => {
            renderBuildingCreator();
        });
    }
}

// Dropdown next to "New Building" - switches the active card.
function onBuildingsActivePickerChange(e) {
    const picker = e.target;
    if (!picker || picker.id !== 'buildings-active-picker') return;
    const newId = picker.value || null;
    if (!newId || newId === state.buildingCreatorActiveId) return;
    state.buildingCreatorActiveId = newId;
    renderBuildingCreator();
}

// Builds a one-element status node: <div class="cls"><em>text</em></div>.
function buildStatusNode(cls, text) {
    const div = document.createElement('div');
    div.className = cls;
    const em = document.createElement('em');
    em.textContent = text;
    div.appendChild(em);
    return div;
}

// Like buildStatusNode but the text is plain (no <em>), used for the
// error-state status rows that render with a building-scan-error class.
function buildPlainStatusNode(cls, text) {
    const div = document.createElement('div');
    div.className = cls;
    div.textContent = text;
    return div;
}

function buildCustomBuildingCardNode(custom, index) {
    if (!custom) return null;
    // The templateId is a Vanilla DA virtual path (e.g.
    // "/Game/Gameplay/Building/.../DA_BI_FloorTorch_01"). The picker
    // populates the on-demand vanilla-inspection cache on selection
    // so subsequent renders avoid re-hitting the backend.
    const ins = (custom.templateId && state.vanillaBuildingInspections.get)
        ? state.vanillaBuildingInspections.get(custom.templateId) || null
        : null;

    // Template label: file stem from the inspection when available,
    // otherwise fall back to the raw templateId so the user can see
    // which DA was picked even if the inspection cache is cold.
    let tplLabel = '';
    if (ins) tplLabel = ins.displayName || ins.id || '';
    else if (custom.templateId) tplLabel = custom.templateId;

    const card = cloneTemplate('tpl-building-card');
    card.dataset.buildingIndex = index;
    card.dataset.buildingId = custom.id;

    setBuildingTitleName(card.querySelector('.building-title-name'), custom.name || '');
    card.querySelector('.building-title-id').textContent = custom.id;

    const tplInput = card.querySelector('[data-building-template-input]');
    tplInput.value = tplLabel;

    // Category facet options for the picker. ~8 entries in 5.6.
    const categories = state.vanillaBuildingCategories || [];
    if (categories.length > 0) {
        const catSel = document.createElement('select');
        catSel.dataset.buildingTemplateCategory = '';
        catSel.title = 'Filter picker by category';
        catSel.appendChild(new Option('All categories', ''));
        for (const c of categories) catSel.appendChild(new Option(c, c));
        tplInput.insertAdjacentElement('afterend', catSel);
    }

    // Per-template hint line: surface mesh/icon/category from the
    // inspection for context. Shows a resolving placeholder while the
    // inspection is still loading from the backend.
    const titles = card.querySelector('.building-titles');
    if (ins) {
        const parts = [];
        if (ins.category) parts.push(ins.category);
        if (ins.meshStem) parts.push('mesh: ' + ins.meshStem);
        if (ins.recipeStem) parts.push('cost: ' + ins.recipeStem);
        let tplHint = parts.join(' · ');
        if (ins.warnings && ins.warnings.length > 0) {
            tplHint += ' · ' + ins.warnings[0];
        }
        if (tplHint) {
            const small = document.createElement('small');
            small.className = 'building-title-template';
            const span = document.createElement('span');
            span.textContent = tplHint;
            small.appendChild(span);
            titles.appendChild(small);
        }
    } else if (custom.templateId) {
        const small = document.createElement('small');
        small.className = 'building-title-template';
        const span = document.createElement('span');
        const em = document.createElement('em');
        em.textContent = 'resolving template...';
        span.appendChild(em);
        small.appendChild(span);
        titles.appendChild(small);
    }

    const fields = card.querySelector('.building-fields');

    const banner = buildMissingRequiredBannerNode(custom, null);
    if (banner) fields.insertBefore(banner, fields.firstChild);

    card.querySelector('input[data-building-field="name"]').value = custom.name || '';
    card.querySelector('textarea[data-building-field="description"]').value = custom.description || '';
    card.querySelector('input[data-building-field="cookedFolderPath"]').value = custom.cookedFolderPath || '';

    const scanHost = card.querySelector('[data-building-scan-host]');
    scanHost.appendChild(custom.cookedFolderPath
        ? buildStatusNode('building-scan', 'Scanning...')
        : buildStatusNode('building-scan', "Pick a folder above and click Scan, or just type a path - we'll scan automatically when you stop editing."));

    // Mesh + Icon selects sit between the scan-host and slots-host.
    scanHost.after(renderMeshIconSelectsNode(custom));

    const slotsHost = card.querySelector('[data-building-slots-host]');
    slotsHost.appendChild((custom.cookedFolderPath && custom.meshStem)
        ? buildStatusNode('building-slots-status', 'Reading mesh slots...')
        : buildStatusNode('building-slots-status', 'Set the cooked folder and mesh stem above to see material slots.'));

    // Component-preset block (+ optional audio sub-block) closes the fields.
    fields.appendChild(renderComponentPresetSelectNode(custom));
    return card;
}

function setBuildingTitleName(el, name) {
    if (name) {
        el.textContent = name;
    } else {
        const em = document.createElement('em');
        em.textContent = '(unnamed)';
        el.replaceChildren(em);
    }
}

// Renders the Component-FX preset dropdown for one building card.
// Loaded list comes from /api/buildings/component-presets (cached in
// _componentPresets). When the list isn't loaded yet, render a disabled
// placeholder; the re-render after ensureComponentPresetsLoaded
// resolves swaps it for the real select.
//
// Wire-format compat: profiles saved before the rename serialized the
// field as flamePresetId. The backend's Profile.cs CustomBuilding has
// a backward-compat setter that migrates flamePresetId -> componentPresetId
// on load, so the GUI only deals with the new key.
function renderComponentPresetSelectNode(custom) {
    const currentId = (custom && custom.componentPresetId) ? custom.componentPresetId : '';
    const presets = Array.isArray(_componentPresets) ? _componentPresets : null;
    const helpText = 'Optional. Wraps the building with components cloned from a vanilla donor BP. '
                   + 'No effect when set to None (default).';

    const label = cloneTemplate('tpl-building-component-preset');
    const select = label.querySelector('select[data-building-field="componentPresetId"]');
    const hintEm = label.querySelector('small em');

    if (!presets) {
        // Not loaded yet - placeholder. Will re-render once the catalog arrives.
        select.disabled = true;
        select.appendChild(new Option('(loading...)', ''));
        hintEm.textContent = helpText;
        return label;
    }

    select.appendChild(new Option('None (no preset)', ''));
    for (const p of presets) {
        if (!p || !p.id) continue;
        select.appendChild(new Option(p.displayName || p.id, p.id));
    }
    // If the profile carries an id that isn't in the catalog (e.g.
    // someone hand-edited the JSON), preserve it as a non-matching
    // option so the user sees "unknown" instead of a silent revert.
    if (currentId && !presets.some(p => p && p.id === currentId)) {
        select.appendChild(new Option(currentId + ' (unknown - check catalog)', currentId));
    }
    select.value = currentId;

    // Surface the picked preset's description as the hint line.
    let hint = helpText;
    if (currentId) {
        const cur = presets.find(p => p && p.id === currentId);
        if (cur && cur.description) hint = cur.description;
    }
    hintEm.textContent = hint;

    const frag = document.createDocumentFragment();
    frag.appendChild(label);
    const audio = renderAudioSourceBlockNode(custom);
    if (audio) frag.appendChild(audio);
    return frag;
}

// Audio-Source upload + range slider block. Only rendered when the
// active component preset's kind is "audio". The block contains:
//   - Upload control (file picker, supported formats: wav/mp3/ogg/flac/m4a/aac/opus)
//   - Status row showing current audio (filename + duration) or "vanilla loop"
//   - Clear button
//   - Range slider (1-300 meters)
// All controls only become live AFTER the building has an Id (= it's
// been saved at least once), since the upload endpoint takes /buildings/{bid}.
function renderAudioSourceBlockNode(custom) {
    if (!custom) return null;
    const presetId = (custom && custom.componentPresetId) || '';
    const presets = Array.isArray(_componentPresets) ? _componentPresets : null;
    let isAudioPreset = false;
    if (presets) {
        const cur = presets.find(p => p && p.id === presetId);
        isAudioPreset = !!(cur && cur.kind === 'audio');
    } else if (presetId === 'audio') {
        // Fallback when catalog hasn't loaded yet - assume preset id
        // "audio" is the audio preset (matches the shipped catalog).
        isAudioPreset = true;
    }
    if (!isAudioPreset) return null;

    const src = custom.audioSource || null;
    const rangeMeters = (typeof custom.audioRangeMeters === 'number' && custom.audioRangeMeters > 0)
        ? custom.audioRangeMeters : 15;
    const volume = (typeof custom.audioVolume === 'number' && custom.audioVolume > 0)
        ? custom.audioVolume : 0.45;

    const block = cloneTemplate('tpl-building-audio-block');

    const statusEl = block.querySelector('.building-audio-status');
    if (src && src.originalFilename) {
        const dur = typeof src.durationSec === 'number'
            ? src.durationSec.toFixed(1) + 's' : '';
        const sz = typeof src.sizeBytes === 'number'
            ? (src.sizeBytes / 1024 / 1024).toFixed(2) + ' MB' : '';
        const parts = [src.originalFilename, dur, sz].filter(Boolean);
        const ready = document.createElement('span');
        ready.className = 'building-audio-status-ready';
        ready.textContent = parts.join(' - ');
        const clearBtn = document.createElement('button');
        clearBtn.type = 'button';
        clearBtn.className = 'btn-link danger';
        clearBtn.dataset.buildingAction = 'audio-clear';
        clearBtn.textContent = 'Clear';
        statusEl.append(ready, ' ', clearBtn);
    } else {
        const vanilla = document.createElement('span');
        vanilla.className = 'building-audio-status-vanilla';
        vanilla.textContent = 'No custom audio - vanilla clock tick-tack loop will play.';
        statusEl.appendChild(vanilla);
    }

    const rangeInput = block.querySelector('input[data-building-field="audioRangeMeters"]');
    rangeInput.value = String(rangeMeters);
    block.querySelector('[data-audio-range-display]').textContent = String(rangeMeters) + ' m';

    const volInput = block.querySelector('input[data-building-field="audioVolume"]');
    volInput.value = String(Math.round(volume * 100));
    block.querySelector('[data-audio-volume-display]').textContent = String(Math.round(volume * 100)) + ' %';
    return block;
}

// Swap an existing .building-audio-block for a freshly rendered one.
// Mirrors the old `blk.outerHTML = render...` semantics: a null result
// (preset no longer audio) removes the block entirely.
function replaceAudioBlock(blk, custom) {
    const node = renderAudioSourceBlockNode(custom);
    if (node) blk.replaceWith(node);
    else blk.remove();
}

// -----------------------------------------------------------------------
// Missing-fields banner. Required:
//   - cookedFolderPath
//   - meshStem        (asset prefix is derived from this server-side)
//   - iconStem        (so the build menu thumbnail isn't blank)
//   - per-slot VanillaMaterialParentPath (only when slot list is known)
// -----------------------------------------------------------------------
function buildMissingRequiredBannerNode(custom, _tpl) {
    if (!custom) return null;
    const missing = [];
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
    if (missing.length === 0) return null;
    const div = document.createElement('div');
    div.className = 'building-missing-fields';
    div.append('Required field' + (missing.length === 1 ? '' : 's') + ' empty: ');
    const strong = document.createElement('strong');
    strong.textContent = missing.join(', ');
    div.appendChild(strong);
    div.append('. This building will be skipped at Build time.');
    return div;
}

function refreshMissingFieldsBanner(card, custom) {
    if (!card) return;
    const fields = card.querySelector('.building-fields');
    if (!fields) return;
    const node = buildMissingRequiredBannerNode(custom, null);
    const existing = fields.querySelector(':scope > .building-missing-fields');
    if (!node) {
        if (existing) existing.remove();
        return;
    }
    if (existing) {
        existing.replaceWith(node);
    } else {
        fields.insertBefore(node, fields.firstChild);
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
    if (host) host.replaceChildren(buildStatusNode('building-slots-status', 'Reading mesh slots...'));

    let inspection;
    try {
        // profileId lets the backend resolve profile-relative folder
        // names like "MyPainting" -> <Profiles>/<profileId>/MyPainting.
        // Absolute paths and unknown names fall through to the raw
        // value (= unchanged "Folder does not exist" behaviour).
        const profileId = (state.current && state.current.id) ? state.current.id : '';
        const url = '/api/buildings/inspect-cooked?path='
            + encodeURIComponent(path)
            + '&meshStem=' + encodeURIComponent(stem)
            + '&profileId=' + encodeURIComponent(profileId);
        inspection = await api('GET', url);
    } catch (ex) {
        if (host) host.replaceChildren(buildPlainStatusNode('building-slots-status building-scan-error',
            'Failed to read mesh: ' + ((ex && ex.message) ? ex.message : String(ex))));
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
        host.replaceChildren(buildStatusNode('building-slots-status', 'Awaiting mesh inspection...'));
        return;
    }
    if (!inspection.ok) {
        host.replaceChildren(buildPlainStatusNode('building-slots-status building-scan-error',
            inspection.error || 'Mesh inspect failed'));
        return;
    }
    if (!Array.isArray(inspection.meshSlots) || inspection.meshSlots.length === 0) {
        // The backend swallows mesh-read exceptions into `warnings` but
        // still returns ok=true with empty meshSlots. Surface those warnings
        // here so the user sees the actual cause (e.g. UAssetAPI throwing
        // under parallel load) instead of a silent "no slots" message.
        const warnings = Array.isArray(inspection.warnings) ? inspection.warnings : [];
        const status = buildStatusNode('building-slots-status', 'Mesh has no material slots.');
        if (warnings.length > 0) {
            const wrap = document.createElement('div');
            wrap.className = 'building-slots-warnings';
            for (const w of warnings) {
                const wEl = document.createElement('div');
                wEl.className = 'building-slots-warning';
                wEl.textContent = String(w);
                wrap.appendChild(wEl);
            }
            status.appendChild(wrap);
        }
        host.replaceChildren(status);
        return;
    }

    const listWrap = document.createElement('div');
    listWrap.className = 'building-slots-list';
    const title = document.createElement('div');
    title.className = 'building-slots-list-title';
    title.textContent = 'Material slots (from ' + (inspection.meshStem || '') + ')';
    listWrap.appendChild(title);
    for (const ms of inspection.meshSlots) {
        listWrap.appendChild(renderSlotCardNode(building, ms, inspection));
    }
    host.replaceChildren(listWrap);

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

function renderSlotCardNode(building, meshSlot, inspection) {
    const key = String(meshSlot.index);
    const sl = (building.slots && building.slots[key]) || {};
    const slotName = meshSlot.slotName || ('slot' + meshSlot.index);
    const parentPath = sl.vanillaMaterialParentPath || '';
    const parentStem = parentPath ? extractStem(parentPath) : '';

    const slot = cloneTemplate('tpl-building-slot');
    slot.dataset.slotIndex = meshSlot.index;

    slot.querySelector('.building-slot-name').textContent =
        'Slot ' + meshSlot.index + ' — ' + slotName;

    const header = slot.querySelector('.building-slot-header');
    const userMi = document.createElement('small');
    if (meshSlot.userMaterialStem) {
        userMi.append('User-cooked MI: ');
        const code = document.createElement('code');
        code.textContent = meshSlot.userMaterialStem;
        userMi.appendChild(code);
    } else {
        const em = document.createElement('em');
        em.textContent = '(no user MI bound to this slot)';
        userMi.appendChild(em);
    }
    header.appendChild(userMi);

    slot.querySelector('[data-slot-parent-search]').value = parentStem;

    setSlotParentCurrent(slot.querySelector('.building-slot-parent-current'), parentPath);

    const host = slot.querySelector('[data-slot-params-host]');
    host.appendChild(parentPath
        ? buildStatusNode('building-slot-params-status', 'Loading parameters...')
        : buildStatusNode('building-slot-params-status', 'Parameters will appear after picking a parent.'));
    return slot;
}

// Fills .building-slot-parent-current: "Current: <code>path</code>" when
// a parent is set, otherwise the "Pick a parent above." placeholder.
function setSlotParentCurrent(el, parentPath) {
    if (!el) return;
    if (parentPath) {
        el.replaceChildren('Current: ');
        const code = document.createElement('code');
        code.textContent = parentPath;
        el.appendChild(code);
    } else {
        const em = document.createElement('em');
        em.textContent = 'Pick a parent above.';
        el.replaceChildren(em);
    }
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
async function openVanillaMiPicker(input, buildingId, slotIndex, selectAll) {
    if (selectAll === undefined) selectAll = true;
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
            setPickerLoading(dd, 'Loading vanilla materials catalog...');
        }
        dd.hidden = false;
    }
    positionPicker(input);
    if (selectAll && input.value) {
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
    if (currentEl) setSlotParentCurrent(currentEl, packagePath);

    await renderSlotParams(buildingId, slotIndex, packagePath);
    markDirty();
    updateButtons();
    refreshMissingFieldsBanner(card, building);
}

// -----------------------------------------------------------------------
// Slot param render. Triggered after a Vanilla parent is picked.
// Inspects the picked MI to learn its param schema, then renders one
// control per param. Pre-fills from the user-cooked MI's values when
// the parents match.
// -----------------------------------------------------------------------
// <div class="building-slot-params-group"><div class="...-group-title">title</div></div>
function buildSlotParamsGroup(title) {
    const group = document.createElement('div');
    group.className = 'building-slot-params-group';
    const titleEl = document.createElement('div');
    titleEl.className = 'building-slot-params-group-title';
    titleEl.textContent = title;
    group.appendChild(titleEl);
    return group;
}

async function renderSlotParams(buildingId, slotIndex, packagePath) {
    const card = document.querySelector('li.building-card[data-building-id="' + buildingId + '"]');
    if (!card) return;
    const slotEl = card.querySelector('.building-slot[data-slot-index="' + slotIndex + '"]');
    if (!slotEl) return;
    const host = slotEl.querySelector('[data-slot-params-host]');
    if (!host) return;
    host.replaceChildren(buildStatusNode('building-slot-params-status', 'Loading parameters...'));

    let mi;
    if (_vanillaInspectCache.has(packagePath)) {
        mi = _vanillaInspectCache.get(packagePath);
    } else {
        try {
            mi = await api('GET', '/api/vanilla-materials/inspect?path=' + encodeURIComponent(packagePath));
            _vanillaInspectCache.set(packagePath, mi);
        } catch (ex) {
            host.replaceChildren(buildPlainStatusNode('building-slot-params-status building-scan-error',
                'Failed to inspect MI: ' + ((ex && ex.message) ? ex.message : String(ex))));
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
    // (redirects the texture path under the mod's output namespace to a
    // vanilla stem that doesn't exist there).
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
    const frag = document.createDocumentFragment();

    const headerEl = document.createElement('div');
    headerEl.className = 'building-slot-params-header';
    const headerSpan = document.createElement('span');
    headerSpan.append('Params from ');
    const headerCode = document.createElement('code');
    headerCode.textContent = mi.stem;
    headerSpan.appendChild(headerCode);
    headerEl.appendChild(headerSpan);
    if (userMi) {
        const small = document.createElement('small');
        small.append('(pre-filled from user-cooked ');
        const code = document.createElement('code');
        code.textContent = meshSlot.userMaterialStem;
        small.append(code, ')');
        headerEl.append(' ', small);
    }
    frag.appendChild(headerEl);

    let groupCount = 0;

    if (mi.scalars && mi.scalars.length > 0) {
        const group = buildSlotParamsGroup('Scalars');
        for (const s of mi.scalars) {
            const current = sl.scalarParams[s.name];
            const v = (typeof current === 'number') ? current : s.value;
            const row = cloneTemplate('tpl-building-slot-param-scalar');
            row.querySelector('span').textContent = s.name;
            const input = row.querySelector('input[data-param-scalar]');
            input.dataset.paramScalar = s.name;
            input.value = v;
            const reset = row.querySelector('button[data-param-reset-scalar]');
            reset.dataset.paramResetScalar = s.name;
            reset.dataset.default = s.value;
            reset.title = 'Reset to Vanilla default (' + s.value + ')';
            group.appendChild(row);
        }
        frag.appendChild(group);
        groupCount++;
    }

    if (mi.vectors && mi.vectors.length > 0) {
        const group = buildSlotParamsGroup('Colors');
        for (const vp of mi.vectors) {
            const current = sl.vectorParams[vp.name];
            const r = current ? current[0] : vp.r;
            const g = current ? current[1] : vp.g;
            const b = current ? current[2] : vp.b;
            const a = current ? current[3] : vp.a;
            const row = cloneTemplate('tpl-building-slot-param-vector');
            row.querySelector('span').textContent = vp.name;
            const colorIn = row.querySelector('input[data-param-vector]');
            colorIn.dataset.paramVector = vp.name;
            colorIn.value = rgbToHex(r, g, b);
            const alphaIn = row.querySelector('input[data-param-vector-alpha]');
            alphaIn.dataset.paramVectorAlpha = vp.name;
            alphaIn.value = a;
            const reset = row.querySelector('button[data-param-reset-vector]');
            reset.dataset.paramResetVector = vp.name;
            reset.dataset.defaultR = vp.r;
            reset.dataset.defaultG = vp.g;
            reset.dataset.defaultB = vp.b;
            reset.dataset.defaultA = vp.a;
            group.appendChild(row);
        }
        frag.appendChild(group);
        groupCount++;
    }

    if (mi.textures && mi.textures.length > 0) {
        const cookedTextures = collectCookedTextureStems(building, inspection);
        const group = buildSlotParamsGroup('Textures');
        for (const t of mi.textures) {
            const current = sl.textureParams[t.name];
            const stem = (typeof current === 'string') ? current : '';
            const row = cloneTemplate('tpl-building-slot-param-texture');
            row.querySelector('span').textContent = t.name;
            const select = row.querySelector('select[data-param-texture]');
            select.dataset.paramTexture = t.name;
            renderTextureOptionsInto(select, cookedTextures, stem, t.textureStem);
            const reset = row.querySelector('button[data-param-reset-texture]');
            reset.dataset.paramResetTexture = t.name;
            group.appendChild(row);
        }
        frag.appendChild(group);
        groupCount++;
    }

    if (groupCount === 0) {
        frag.appendChild(buildStatusNode('building-slot-params-status', 'This MI has no editable parameters.'));
    }

    host.replaceChildren(frag);
    // Dirty + button refresh is the caller's responsibility - this
    // function gets called both from user-initiated picks (which DO
    // mutate the profile) and from background re-renders on tab open
    // (which must NOT enable Save by themselves).
    refreshMissingFieldsBanner(card, building);
}

// Collect the texture-stem candidates the per-slot dropdown should
// offer. Returns a { defaults, folder } pair so the renderer can keep
// the two groups visually distinct (shipped defaults vs. files the
// user actually cooked into their folder).
//
//   - defaults: stems shipped with the app
//     (Tools/Templates/DefaultTextures/T_*). Always available; the
//     build pipeline stages the matching .uasset/.uexp/.ubulk
//     triplets into every build, so referencing them never breaks.
//     Empty array until the lazy loader resolves - the dropdown
//     re-renders once the catalog arrives.
//   - folder: T_* stems from the user's cooked folder scan, with
//     any stem that also appears in `defaults` filtered out so the
//     same name doesn't show up twice. Empty until the scan runs.
function collectCookedTextureStems(building, _inspection) {
    const defaults = Array.isArray(_defaultTextureStems) ? _defaultTextureStems.slice() : [];
    const defaultsSet = new Set(defaults);

    const folder = [];
    const scanList = _buildingTextureStemCache.get(building.id);
    if (Array.isArray(scanList)) {
        for (const s of scanList) {
            if (!s || !s.startsWith('T_')) continue;
            if (defaultsSet.has(s)) continue;  // shipped default wins
            folder.push(s);
        }
    }
    folder.sort((a, b) => a.localeCompare(b));
    return { defaults, folder };
}

// Render the per-slot texture-param <select> body. Emits:
//   - "(use Vanilla: <vanillaStem>)" placeholder that selects to "" so
//     the param falls through to the cloned MI's parent default
//   - "Default textures" optgroup with the shipped stems (canonical
//     list lives in DefaultTextureProvider.Stems, fetched at runtime
//     via /api/buildings/default-textures) - always present even if
//     the user hasn't picked a cooked folder yet
//   - "From cooked folder" optgroup with T_* stems the scan found
//     (only when at least one is available); the shipped defaults
//     are already filtered out of this list in collectCookedTextureStems
//   - Survival entry for a saved value that doesn't appear in either
//     list (e.g. user typed a stem the scan no longer finds) so the
//     dropdown doesn't visually drop the user's pick
//
// `currentStem` is the value currently saved on the slot's
// textureParams; `vanillaStem` is the parent-MI's existing texture
// reference for this param (used in the placeholder text only).
function renderTextureOptionsInto(select, stems, currentStem, vanillaStem) {
    select.appendChild(new Option('(use Vanilla: ' + (vanillaStem || '?') + ')', ''));

    let foundCurrent = false;
    const addOptgroup = (label, list) => {
        const og = document.createElement('optgroup');
        og.label = label;
        for (const s of list) {
            if (s === currentStem) foundCurrent = true;
            og.appendChild(new Option(s, s));
        }
        select.appendChild(og);
    };

    if (stems.defaults && stems.defaults.length > 0) {
        addOptgroup('Default textures (always available)', stems.defaults);
    }
    if (stems.folder && stems.folder.length > 0) {
        addOptgroup('From cooked folder', stems.folder);
    }
    // Saved value that didn't match either group - keep it as a free-
    // standing option so the picker reflects current state instead of
    // silently dropping the user's pick.
    if (currentStem && !foundCurrent) {
        select.appendChild(new Option(currentStem + ' (not found)', currentStem));
    }
    select.value = currentStem || '';
}

const _buildingTextureStemCache = new Map(); // building.id -> [stem...]
const _buildingMeshStemCache    = new Map(); // building.id -> [stem...] (SM_*)
const _buildingIconStemCache    = new Map(); // building.id -> [stem...] (T_*_Icon)

// -----------------------------------------------------------------------
// Mesh + Icon stem dropdowns, populated from the cooked-folder scan.
//
// Asset prefix used to be a manual user input; it's now derived
// automatically server-side from the picked MeshStem (e.g.
// "SM_QmWieselburger_01" -> "QmWieselburger"). The Mesh/Icon selects
// list every matching stem the scan found in the cooked folder so the
// user picks instead of typing. If no scan has run yet (or the cache is
// empty), we still show the currently-saved value as a single option so
// the field stays addressable until the scan completes.
// -----------------------------------------------------------------------
function renderMeshIconSelectsNode(custom) {
    const meshStems = _buildingMeshStemCache.get(custom.id) || [];
    const iconStems = _buildingIconStemCache.get(custom.id) || [];
    const frag = document.getElementById('tpl-building-mesh-icon').content.cloneNode(true);
    fillStemSelect(frag.querySelector('select[data-building-field="meshStem"]'), custom.meshStem || '', meshStems);
    fillStemSelect(frag.querySelector('select[data-building-field="iconStem"]'), custom.iconStem || '', iconStems);
    return frag;
}

function fillStemSelect(select, currentValue, options) {
    const hasScan = options.length > 0;
    select.appendChild(new Option('', ''));

    let foundCurrent = false;
    for (const stem of options) {
        if (stem === currentValue) foundCurrent = true;
        select.appendChild(new Option(stem, stem));
    }
    // Survive cache-misses: if the profile already has a value the scan
    // didn't surface (e.g. card just rendered, scan still running, or
    // folder no longer contains the stem), keep the saved value as a
    // selected entry so the picker reflects current state instead of
    // looking like the user lost their pick.
    if (currentValue && !foundCurrent) {
        const label = hasScan
            ? currentValue + ' (not found in folder)'
            : currentValue;
        select.appendChild(new Option(label, currentValue));
    }
    select.value = currentValue || '';
}

// Re-render Mesh + Icon selects in-place after a scan refreshes the
// per-kind stem caches. We replace the two label containers as a unit
// instead of patching individual options to keep the markup in sync
// with renderMeshIconSelectsNode without a full card re-render (which
// would lose focus/scroll state if the user is mid-edit).
function refreshMeshIconSelects(card, custom) {
    if (!card || !custom) return;
    // The two .building-field labels for mesh/icon sit between the
    // scan-host and the slots-host - easiest to find them by their
    // data-building-field selects.
    const meshSelect = card.querySelector('select[data-building-field="meshStem"]');
    const iconSelect = card.querySelector('select[data-building-field="iconStem"]');
    const meshLabel = meshSelect ? meshSelect.closest('label.building-field') : null;
    const iconLabel = iconSelect ? iconSelect.closest('label.building-field') : null;
    if (!meshLabel || !iconLabel) return;
    const frag = renderMeshIconSelectsNode(custom);
    const newMesh = frag.querySelector('select[data-building-field="meshStem"]').closest('label.building-field');
    const newIcon = frag.querySelector('select[data-building-field="iconStem"]').closest('label.building-field');
    if (newMesh) meshLabel.replaceWith(newMesh);
    if (newIcon) iconLabel.replaceWith(newIcon);
}

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
    const cEl = document.getElementById('buildings-stat-count');
    if (cEl) cEl.textContent = customs.length;
    const cnt = document.getElementById('buildings-count');
    if (cnt) cnt.textContent = customs.length === 0
        ? '' : (customs.length + ' building' + (customs.length === 1 ? '' : 's'));
}

async function onBuildingsNew() {
    if (!state.current) return;
    const name = await prompt('Name for the new building:', 'My Building');
    if (name == null) return;
    const trimmed = String(name).trim();
    if (!trimmed) return;

    state.current.customBuildings = state.current.customBuildings || [];
    const newEntry = {
        id: newCustomBuildingId(),
        // Empty templateId: the user picks the donor Vanilla DA via
        // the per-card template picker after creation.
        templateId: '',
        name: trimmed,
        description: '',
        cookedFolderPath: '',
        // assetPrefix is derived server-side from meshStem at save time;
        // no user input here.
        meshStem: '',
        iconStem: '',
        slots: {},  // populated dynamically after mesh inspection
    };
    state.current.customBuildings.push(newEntry);
    // Make the new building immediately active in the dropdown.
    state.buildingCreatorActiveId = newEntry.id;
    markDirty();
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
    // File upload (audio source) needs its own path - the dataset key
    // is buildingAction (not buildingField), and the handler does an
    // async upload + re-render rather than mutating profile state.
    if (t.dataset.buildingAction === 'audio-upload') {
        onBuildingsAudioChange(e);
        return;
    }
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
            if (titleEl) setBuildingTitleName(titleEl, custom.name || '');
            // Keep the active-card dropdown in sync with the typed name.
            const picker = document.getElementById('buildings-active-picker');
            if (picker) {
                const opt = picker.querySelector('option[value="' + cssEscape(custom.id) + '"]');
                if (opt) opt.textContent = (custom.name && custom.name.trim()) ? custom.name : '(unnamed)';
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
        } else if (field === 'meshStem') {
            custom.meshStem = t.value || '';
            if (custom.cookedFolderPath) {
                triggerCookedInspect(index, custom.id, custom.cookedFolderPath, custom.meshStem);
            }
            refreshMissingFieldsBanner(card, custom);
        } else if (field === 'iconStem') {
            custom.iconStem = t.value || '';
            refreshMissingFieldsBanner(card, custom);
        } else if (field === 'componentPresetId') {
            // Empty string = no preset (default). Stored as null in the
            // profile so older builds round-trip cleanly.
            const v = (t.value || '').trim();
            custom.componentPresetId = v ? v : null;
            // Re-render this card's component-preset block so the hint
            // line (description) updates to match the new selection, AND
            // the Audio Source sub-block appears/disappears with the
            // "audio" kind.
            const host = card.querySelector('label.building-field-wide select[data-building-field="componentPresetId"]');
            if (host) {
                const lbl = host.closest('label.building-field');
                if (lbl) {
                    // Remove the old audio block (if rendered) so we don't
                    // end up with two when switching preset back-and-forth.
                    const oldAudio = card.querySelector('.building-audio-block');
                    if (oldAudio) oldAudio.remove();
                    lbl.replaceWith(renderComponentPresetSelectNode(custom));
                }
            }
            // Trigger a meta-refresh against the server when we now show
            // the audio block - keeps the status line up-to-date even if
            // someone hand-edited the WAV outside the GUI.
            if (custom.id && custom.componentPresetId) {
                const presets = Array.isArray(_componentPresets) ? _componentPresets : null;
                const cur = presets ? presets.find(p => p && p.id === custom.componentPresetId) : null;
                if (cur && cur.kind === 'audio') refreshAudioStatus(custom, card);
            }
        } else if (field === 'audioRangeMeters') {
            const n = Number(t.value);
            custom.audioRangeMeters = (n > 0 ? n : 15);
            const disp = card.querySelector('[data-audio-range-display]');
            if (disp) disp.textContent = String(custom.audioRangeMeters) + ' m';
        } else if (field === 'audioVolume') {
            // Slider is in percent (0..100), profile stores an absolute
            // VolumeMultiplier (0..1.0). Floor at 0 means muted; we
            // store 0.001 so the staging clamp (which treats 0 as
            // "unset -> default 0.45") doesn't reset a user-chosen
            // mute back to vanilla.
            const pct = Number(t.value);
            const mult = (pct > 0 ? pct / 100 : 0.001);
            custom.audioVolume = mult;
            const disp = card.querySelector('[data-audio-volume-display]');
            if (disp) disp.textContent = String(pct) + ' %';
        // (Etappe I removed the legacy `<select data-building-field=
        //  "templateId">` element; template picks now go through the
        //  central picker via data-building-template-input, handled
        //  below in setVanillaBuildingTemplateForCard.)
        } else {
            return;
        }
        markDirty();
    } else if (t.dataset.slotParentSearch !== undefined) {
        // Re-filter the central picker while the user types. If the picker
        // is not currently open for this input (e.g. focus stayed in the
        // input after a previous pick, where focusin won't fire again),
        // open it without select-all so the user's keystroke isn't
        // clobbered. The opener also calls populatePicker with the current
        // value, so the filter applies right away.
        if (state.picker && state.picker.input === t) {
            populatePicker(t.value);
            positionPicker(t);
        } else {
            openBuildingPickerForInput(t, false);
        }
        return;  // not dirty until they actually pick a result
    } else if (t.dataset.buildingTemplateInput !== undefined) {
        // Etappe I: re-filter the vanilla-template picker as the user
        // types. Same input-as-search-box pattern as slotParentSearch /
        // recipeSearch above. Commit happens via onPickerClick.
        //
        // Suppression: after a pick, renderBuildingCreator() replaces
        // the input which can trigger a `change` event on the now-
        // detached old input (if its value differed since focus). That
        // bubbles here and would re-open the picker. Bail out while
        // the post-pick flag is armed.
        if (_suppressBuildingTemplatePickerReopen) return;
        // Defensive: detached old input from a re-render still has
        // the dataset attribute but isn't visible; never open a picker
        // anchored to a detached element (getBoundingClientRect would
        // return zeros and the dropdown would appear at 0,0).
        if (!document.body.contains(t)) return;
        if (state.picker && state.picker.input === t) {
            populatePicker(t.value);
            positionPicker(t);
        } else {
            openBuildingPickerForInput(t, false);
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
        markDirty();
    } else if (t.dataset.paramVector !== undefined || t.dataset.paramVectorAlpha !== undefined) {
        applyVectorParam(custom, t);
        markDirty();
    } else if (t.dataset.paramTexture !== undefined) {
        applyTextureParam(custom, t);
        markDirty();
    } else if (t.dataset.recipeSearch !== undefined) {
        // Mirror of slotParentSearch: re-filter the central picker as
        // the user types. Pick is committed via the central onPickerClick.
        // If the picker isn't open for this input, open it (selectAll=false)
        // so typing-while-focused also pops it up.
        if (state.picker && state.picker.input === t) {
            populatePicker(t.value);
            positionPicker(t);
        } else {
            openBuildingPickerForInput(t, false);
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
        markDirty();
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
        markDirty();
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
        markDirty();
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
        markDirty();
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
            markDirty();
        } else if (action === 'remove') {
            const idx = parseInt(recipeBtn.dataset.recipeRowIdx, 10);
            if (!isFinite(idx)) return;
            const rows = ensureUserRecipeRows(building);
            if (idx >= 0 && idx < rows.length) rows.splice(idx, 1);
            renderRecipeForCard(building.id, defaults);
            markDirty();
        } else if (action === 'reset') {
            // Revert to "use vanilla defaults" by dropping the user list.
            building.recipeCost = null;
            renderRecipeForCard(building.id, defaults);
            markDirty();
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
        const deletedId = c.id;
        customs.splice(index, 1);
        // If we just deleted the active card, advance the active id to
        // the next available entry (or clear it if the list is now empty).
        if (state.buildingCreatorActiveId === deletedId) {
            if (customs.length === 0) {
                state.buildingCreatorActiveId = null;
            } else {
                const nextIdx = Math.min(index, customs.length - 1);
                state.buildingCreatorActiveId = customs[nextIdx] ? customs[nextIdx].id : null;
            }
        }
        markDirty();
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
    } else if (action === 'audio-clear') {
        const c = customs[index];
        if (!c.id) return;
        if (!await confirm('Clear the uploaded audio for "' + (c.name || c.id) + '"?')) return;
        const profileId = state.current && state.current.id;
        try {
            await api('DELETE', '/api/profiles/' + encodeURIComponent(profileId)
                + '/buildings/' + encodeURIComponent(c.id) + '/audio');
            c.audioSource = null;
            // Rerender just the audio block (preserves the file input
            // outside the block from being unnecessarily reset).
            const blk = card.querySelector('.building-audio-block');
            if (blk) replaceAudioBlock(blk, c);
        } catch (err) {
            await alert('Audio clear failed: ' + (err && err.message ? err.message : err));
        }
    }
}

// -----------------------------------------------------------------------
// Audio-source file upload (per-building "audio" preset).
// -----------------------------------------------------------------------
function onBuildingsAudioChange(e) {
    const t = e.target;
    if (!t || t.tagName !== 'INPUT' || t.type !== 'file') return;
    if (t.dataset.buildingAction !== 'audio-upload') return;
    const card = t.closest('li.building-card');
    if (!card) return;
    const index = parseInt(card.dataset.buildingIndex, 10);
    if (!isFinite(index)) return;
    const customs = state.current && state.current.customBuildings;
    if (!customs || !customs[index]) return;
    const c = customs[index];
    if (!t.files || t.files.length === 0) return;
    const file = t.files[0];
    const profileId = state.current && state.current.id;
    if (!profileId || !c.id) {
        alert('Save the profile first so the building gets an Id, then upload.');
        return;
    }
    uploadBuildingAudio(profileId, c, file, card).finally(() => {
        // Reset file input so the same file can be re-selected if needed.
        try { t.value = ''; } catch (_) {}
    });
}

async function uploadBuildingAudio(profileId, custom, file, card) {
    const status = card.querySelector('.building-audio-status');
    if (status) {
        const em = document.createElement('em');
        em.textContent = 'Uploading + transcoding ' + file.name + '...';
        status.replaceChildren(em);
    }
    try {
        const form = new FormData();
        form.append('audio', file, file.name);
        const url = '/api/profiles/' + encodeURIComponent(profileId)
                  + '/buildings/' + encodeURIComponent(custom.id) + '/audio';
        const resp = await fetch(url, { method: 'POST', body: form });
        if (!resp.ok) {
            let err = 'HTTP ' + resp.status;
            try { const j = await resp.json(); if (j && j.error) err = j.error; } catch (_) {}
            throw new Error(err);
        }
        const dto = await resp.json();
        if (dto && dto.source) {
            custom.audioSource = {
                originalFilename: dto.source.originalFilename,
                durationSec: dto.source.durationSec,
                sampleRate: dto.source.sampleRate,
                channels: dto.source.channels,
                sizeBytes: dto.source.sizeBytes,
            };
        }
        const blk = card.querySelector('.building-audio-block');
        if (blk) replaceAudioBlock(blk, custom);
    } catch (err) {
        await alert('Audio upload failed: ' + (err && err.message ? err.message : err));
        const blk = card.querySelector('.building-audio-block');
        if (blk) replaceAudioBlock(blk, custom);
    }
}

// Refreshes the audio meta from the server (called when user switches
// the preset to audio - we want the status line to reflect the actual
// on-disk state, in case the file was added/removed outside the GUI).
async function refreshAudioStatus(custom, card) {
    if (!custom || !custom.id) return;
    const profileId = state.current && state.current.id;
    if (!profileId) return;
    try {
        const dto = await api('GET', '/api/profiles/' + encodeURIComponent(profileId)
            + '/buildings/' + encodeURIComponent(custom.id) + '/audio');
        if (dto && dto.source) {
            custom.audioSource = {
                originalFilename: dto.source.originalFilename,
                durationSec: dto.source.durationSec,
                sampleRate: dto.source.sampleRate,
                channels: dto.source.channels,
                sizeBytes: dto.source.sizeBytes,
            };
        } else {
            custom.audioSource = null;
        }
        if (typeof dto.rangeMeters === 'number') custom.audioRangeMeters = dto.rangeMeters;
        if (typeof dto.volume === 'number') custom.audioVolume = dto.volume;
        const blk = card.querySelector('.building-audio-block');
        if (blk) replaceAudioBlock(blk, custom);
    } catch (_) {
        // best-effort - status was already showing a usable state
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
        host.replaceChildren(buildStatusNode('building-scan', 'Enter a cooked folder path to scan.'));
        return;
    }
    const cached = _buildingScanCache.get(index);
    if (cached === path && host.querySelector('.building-scan-files')) {
        return;
    }

    host.replaceChildren(buildStatusNode('building-scan', 'Scanning ' + path + '...'));
    try {
        // profileId lets the backend resolve profile-relative folder
        // names like "MyPainting" -> <Profiles>/<profileId>/MyPainting.
        const profileId = (state.current && state.current.id) ? state.current.id : '';
        const scan = await api('GET', '/api/buildings/scan-cooked?path=' + encodeURIComponent(path)
            + '&profileId=' + encodeURIComponent(profileId));
        _buildingScanCache.set(index, path);
        host.replaceChildren(renderScanResultNode(scan, customs[index]));
        // Cache stems per kind so the Mesh + Icon + Texture dropdowns
        // can list them without re-fetching. The Mesh + Icon selects
        // are then re-rendered in-place so the user sees the new picks
        // appear without needing to switch buildings.
        if (scan && Array.isArray(scan.entries)) {
            const textureStems = [];
            const meshStems = [];
            const iconStems = [];
            for (const e of scan.entries) {
                if (e.kind === 'texture' || e.kind === 'icon') textureStems.push(e.stem);
                if (e.kind === 'mesh') meshStems.push(e.stem);
                if (e.kind === 'icon') iconStems.push(e.stem);
            }
            _buildingTextureStemCache.set(customs[index].id, textureStems);
            _buildingMeshStemCache.set(customs[index].id, meshStems);
            _buildingIconStemCache.set(customs[index].id, iconStems);
            refreshMeshIconSelects(card, customs[index]);
            // If the saved Mesh/Icon stem is missing in the scan but
            // there's exactly one candidate, auto-pick it. Saves a click
            // for the common 1-mesh-per-folder case.
            const c = customs[index];
            let autoPicked = false;
            if (!c.meshStem && meshStems.length === 1) { c.meshStem = meshStems[0]; autoPicked = true; }
            if (!c.iconStem && iconStems.length === 1) { c.iconStem = iconStems[0]; autoPicked = true; }
            if (autoPicked) {
                markDirty();
                refreshMeshIconSelects(card, c);
                refreshMissingFieldsBanner(card, c);
                if (c.cookedFolderPath && c.meshStem) {
                    triggerCookedInspect(index, c.id, c.cookedFolderPath, c.meshStem);
                }
            }
        }
    } catch (ex) {
        host.replaceChildren(buildPlainStatusNode('building-scan building-scan-error',
            'Scan failed: ' + ((ex && ex.message) ? ex.message : String(ex))));
    }
}

// Builds a scan-warning span with the given .bad/.skip class and text.
function scanWarnSpan(cls, text) {
    const span = document.createElement('span');
    span.className = cls;
    span.textContent = text;
    return span;
}

function renderScanResultNode(scan, building) {
    if (!scan || !scan.exists) {
        return buildPlainStatusNode('building-scan building-scan-error',
            scan && scan.error ? scan.error : 'Folder not found.');
    }
    const entries = scan.entries || [];
    const counts = {};
    for (const e of entries) counts[e.kind] = (counts[e.kind] || 0) + 1;
    const totalAssets = (counts.mesh || 0) + (counts.icon || 0) + (counts.texture || 0)
                      + (counts.material || 0) + (counts.matinst || 0)
                      + (counts.blueprint || 0) + (counts.data || 0);

    const warnings = [];
    if ((counts.mesh || 0) === 0) warnings.push(scanWarnSpan('bad', 'No mesh (SM_*) found.'));
    if ((counts.icon || 0) === 0) warnings.push(scanWarnSpan('bad', 'No icon (T_*_Icon) found.'));
    if ((counts.material || 0) + (counts.matinst || 0) > 0) {
        warnings.push(scanWarnSpan('skip',
            ((counts.material || 0) + (counts.matinst || 0))
            + ' user-cooked material(s) - will be skipped at build (replaced by Vanilla-MI clone).'));
    }
    const stems = new Set(entries.map(e => e.stem));
    if (building && building.meshStem && !stems.has(building.meshStem)) {
        warnings.push(scanWarnSpan('bad', 'Mesh stem "' + building.meshStem + '" not found in folder.'));
    }
    if (building && building.iconStem && !stems.has(building.iconStem)) {
        warnings.push(scanWarnSpan('bad', 'Icon stem "' + building.iconStem + '" not found in folder.'));
    }

    const root = cloneTemplate('tpl-building-scan-result');

    const statusRow = document.createElement('div');
    statusRow.className = 'building-scan-status';
    const totalSpan = document.createElement('span');
    totalSpan.textContent = entries.length + ' file(s), ' + totalAssets + ' asset(s)';
    statusRow.appendChild(totalSpan);
    const addCountSpan = (text) => {
        const s = document.createElement('span');
        s.textContent = text;
        statusRow.appendChild(s);
    };
    if (counts.mesh)    addCountSpan('mesh: ' + counts.mesh);
    if (counts.icon)    addCountSpan('icon: ' + counts.icon);
    if (counts.texture) addCountSpan('texture: ' + counts.texture);
    if (counts.material || counts.matinst) {
        const s = document.createElement('span');
        s.className = 'skip';
        s.textContent = 'material: ' + ((counts.material || 0) + (counts.matinst || 0)) + ' (skipped)';
        statusRow.appendChild(s);
    }
    root.appendChild(statusRow);

    if (warnings.length > 0) {
        const wrap = document.createElement('div');
        warnings.forEach((node, i) => {
            if (i > 0) wrap.appendChild(document.createTextNode(' '));
            wrap.appendChild(node);
        });
        root.appendChild(wrap);
    }

    const files = entries.filter(e => e.kind !== 'sidecar' && e.kind !== 'other');
    if (files.length > 0) {
        const ul = document.createElement('ul');
        ul.className = 'building-scan-files';
        for (const e of files) {
            const li = cloneTemplate('tpl-building-scan-file');
            const kind = li.querySelector('.kind');
            const extra = scanKindClass(e.kind).trim();
            if (extra) kind.classList.add(extra);
            kind.textContent = scanKindLabel(e.kind);
            li.querySelector('.name').textContent = e.name;
            ul.appendChild(li);
        }
        root.appendChild(ul);
    }
    return root;
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

    host.replaceChildren(buildRecipeSectionNode(rows, usingVanilla, vanillaTag, defaultsErr));
}

function buildRecipeSectionNode(rows, usingVanilla, vanillaTag, errMsg) {
    const section = cloneTemplate('tpl-building-recipe');
    const header = section.querySelector('.building-recipe-header');

    const sourceBadge = document.createElement('span');
    sourceBadge.className = usingVanilla
        ? 'building-recipe-source vanilla'
        : 'building-recipe-source user';
    sourceBadge.textContent = usingVanilla ? 'Vanilla defaults' : 'Custom (overrides vanilla)';
    // Order is <strong>, badge, .building-recipe-actions.
    header.insertBefore(sourceBadge, header.querySelector('.building-recipe-actions'));

    if (!usingVanilla) {
        const reset = document.createElement('button');
        reset.type = 'button';
        reset.className = 'btn-link danger';
        reset.dataset.recipeAction = 'reset';
        reset.title = 'Discard overrides; use template default';
        reset.textContent = 'Reset';
        header.querySelector('.building-recipe-actions').appendChild(reset);
    }

    const ol = section.querySelector('.building-recipe-rows');

    if (errMsg) {
        const err = document.createElement('div');
        err.className = 'building-recipe-error';
        err.textContent = errMsg;
        section.insertBefore(err, ol);
    }
    if (vanillaTag) {
        const meta = document.createElement('div');
        meta.className = 'building-recipe-meta';
        meta.append('Vanilla tag: ');
        const code = document.createElement('code');
        code.textContent = vanillaTag;
        meta.appendChild(code);
        section.insertBefore(meta, ol);
    }

    for (let idx = 0; idx < rows.length; idx++) {
        ol.appendChild(buildRecipeRowNode(rows[idx], idx));
    }

    if (rows.length === 0) {
        section.appendChild(buildStatusNode('building-recipe-empty', 'No build cost - building is free.'));
    }
    return section;
}

function buildRecipeRowNode(row, idx) {
    const itemPath = (row && row.itemPath) || '';
    const count    = (row && Number.isFinite(row.count)) ? row.count : 0;
    const display  = itemPath
        ? (_resourceDisplayCache.get(itemPath) || prettifyResourcePath(itemPath))
        : '';

    const li = cloneTemplate('tpl-building-recipe-row');
    li.dataset.recipeRow = idx;

    const search = li.querySelector('[data-recipe-search]');
    search.dataset.recipeSearch = idx;
    search.value = display;

    const current = li.querySelector('.building-recipe-current');
    if (itemPath) {
        const code = document.createElement('code');
        code.textContent = itemPath;
        current.appendChild(code);
    } else {
        const em = document.createElement('em');
        em.textContent = 'Pick a resource';
        current.appendChild(em);
    }

    const countInput = li.querySelector('[data-recipe-count]');
    countInput.dataset.recipeCount = idx;
    countInput.value = count;

    const remove = li.querySelector('button[data-recipe-action="remove"]');
    remove.dataset.recipeRowIdx = idx;
    return li;
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
async function openResourcePicker(input, buildingId, rowIdx, selectAll) {
    if (selectAll === undefined) selectAll = true;
    closePicker();
    state.picker = { input, source: 'recipeResource', buildingId, rowIdx };
    const dd = document.getElementById('picker-dropdown');
    if (dd) {
        if (state.vanillaResources) {
            populatePicker(input.value);
        } else {
            setPickerLoading(dd, 'Loading resources catalog...');
        }
        dd.hidden = false;
    }
    positionPicker(input);
    if (selectAll && input.value) {
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
    markDirty();
    updateButtons();
}

// selectAll defaults to true (focusin/click paths want to select existing
// text so the user can immediately type to replace it). The input-event
// path passes false because the user is mid-keystroke - select() would
// clobber the cursor position and the next character would replace the
// just-typed text.
function openBuildingPickerForInput(t, selectAll) {
    if (!t || !t.dataset) return false;
    // Defensive: detached inputs (e.g. leftover refs from a re-render)
    // would anchor the dropdown at 0,0 via getBoundingClientRect. Bail.
    if (!document.body.contains(t)) return false;
    if (selectAll === undefined) selectAll = true;

    // Vanilla MI parent search box -> open the centralized picker.
    if (t.dataset.slotParentSearch !== undefined) {
        const card = t.closest('li.building-card');
        const slotEl = t.closest('.building-slot');
        if (!card || !slotEl) return false;
        const buildingId = card.dataset.buildingId;
        const slotIndex = parseInt(slotEl.dataset.slotIndex, 10);
        if (!isFinite(slotIndex)) return false;
        openVanillaMiPicker(t, buildingId, slotIndex, selectAll);
        return true;
    }

    // Recipe-row resource search box -> open the centralized picker.
    if (t.dataset.recipeSearch !== undefined) {
        const card = t.closest('li.building-card');
        const rowEl = t.closest('li.building-recipe-row');
        if (!card || !rowEl) return false;
        const buildingId = card.dataset.buildingId;
        const rowIdx = parseInt(rowEl.dataset.recipeRow, 10);
        if (!isFinite(rowIdx)) return false;
        openResourcePicker(t, buildingId, rowIdx, selectAll);
        return true;
    }

    // Template picker input (Etappe I) -> open the Vanilla building DA
    // picker. Input is read-only so the user can only commit via the
    // central dropdown.
    if (t.dataset.buildingTemplateInput !== undefined) {
        const card = t.closest('li.building-card');
        if (!card) return false;
        const index = parseInt(card.dataset.buildingIndex, 10);
        if (!isFinite(index)) return false;
        openVanillaBuildingPicker(t, index, selectAll);
        return true;
    }

    return false;
}

function onBuildingListFocusIn(e) {
    const t = e.target;
    // Suppression: ignore the focusin that fires when the mouse-release
    // after a picker-option click lands on the freshly rendered template
    // input. See setVanillaBuildingTemplateForCard.
    if (t && t.dataset && t.dataset.buildingTemplateInput !== undefined
        && _suppressBuildingTemplatePickerReopen) {
        return;
    }
    openBuildingPickerForInput(t);
}

// Re-open the central picker when the user clicks an already-focused
// picker input. focusin only fires on focus changes; if the input still
// has focus from a previous open+pick cycle (e.g. setVanillaMiParentForSlot
// keeps the same input element, or mouseup re-focused the rendered input),
// a second mousedown will not fire focusin and the picker would stay
// closed. Mirroring the click here keeps "click input -> picker opens"
// consistent regardless of focus state.
function onBuildingListClickReopenPicker(e) {
    const t = e.target;
    if (!t || !t.dataset) return;
    // Only intercept the three picker-bound inputs.
    if (t.dataset.slotParentSearch === undefined
        && t.dataset.recipeSearch === undefined
        && t.dataset.buildingTemplateInput === undefined) {
        return;
    }
    // If the picker is already open for THIS input, the focusin path
    // already handled it (or it was kept open by typing); do nothing.
    if (state.picker && state.picker.input === t) return;
    // Suppression: same as in onBuildingListFocusIn - ignore the click
    // that comes from the mouse-release after a picker-option pick.
    if (t.dataset.buildingTemplateInput !== undefined
        && _suppressBuildingTemplatePickerReopen) {
        return;
    }
    openBuildingPickerForInput(t);
}

function bindBuildingsHandlers() {
    const btn = document.getElementById('btn-buildings-new');
    if (btn) btn.addEventListener('click', onBuildingsNew);

    const activePicker = document.getElementById('buildings-active-picker');
    if (activePicker) activePicker.addEventListener('change', onBuildingsActivePickerChange);

    const list = document.getElementById('buildings-list');
    if (list) {
        list.addEventListener('input',   onBuildingListChange);
        list.addEventListener('change',  onBuildingListChange);
        list.addEventListener('click',   onBuildingListClick);
        list.addEventListener('click',   onBuildingListClickReopenPicker);
        list.addEventListener('focusin', onBuildingListFocusIn);
        // Re-position the floating picker when the list scrolls so the
        // dropdown stays glued to the input. Mirrors the loot tab.
        list.addEventListener('scroll', () => {
            if (state.picker) positionPicker(state.picker.input);
        }, { passive: true });
    }
}
