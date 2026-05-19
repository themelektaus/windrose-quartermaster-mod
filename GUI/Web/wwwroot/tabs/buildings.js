// Building Creator tab. Mirrors the Item Creator (creator.js) for
// the CRUD shape, with extras for:
//   - the cooked-folder picker (path text field + Scan button)
//   - per-slot inputs (image stem/path) when the template declares
//     userAlbedoRequired on a slot
//   - a non-blocking scan summary that warns about missing items
//     (no mesh / no icon / no albedo for a required slot) and flags
//     user-cooked materials that the patcher will SKIP at build time
//
// Templates live behind /api/building-templates (currently only
// Painting). The actual buildings live inside Profile.customBuildings
// and are persisted via the regular PUT /api/profiles/{id}, exactly
// like the customItems list. No separate building store.

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

    // Kick off background scans for every card with a path - the
    // result populates the per-card scan area when it arrives.
    for (let i = 0; i < customs.length; i++) {
        const c = customs[i];
        if (c && c.cookedFolderPath) {
            scanCookedFolderForCard(i, c.cookedFolderPath);
        }
    }
}

function buildCustomBuildingCardHtml(custom, index) {
    if (!custom) return '';
    const tpl = state.buildingTemplates.byId.get(custom.templateId) || null;

    const tplCatalog = state.buildingTemplates.list || [];
    let tplOpts = tplCatalog.map(t =>
        '<option value="' + escapeHtml(t.id) + '"'
            + (t.id === custom.templateId ? ' selected' : '')
            + '>' + escapeHtml(t.label) + '</option>'
    ).join('');
    if (custom.templateId && !state.buildingTemplates.byId.has(custom.templateId)) {
        tplOpts = '<option value="' + escapeHtml(custom.templateId) + '" selected>'
                + escapeHtml(custom.templateId) + ' (unknown)</option>' + tplOpts;
    }

    const safeName   = escapeHtml(custom.name || '');
    const safeDesc   = escapeHtml(custom.description || '');
    const safePath   = escapeHtml(custom.cookedFolderPath || '');
    const safePrefix = escapeHtml(custom.assetPrefix || '');
    const safeMesh   = escapeHtml(custom.meshStem || '');
    const safeIcon   = escapeHtml(custom.iconStem || '');

    const slotsHtml = renderBuildingSlots(custom, tpl);

    const tplHint = tpl
        ? escapeHtml(tpl.description || '') + (tpl.categoryTag ? ' (tab: ' + escapeHtml(tpl.categoryTag) + ')' : '')
        : '';

    return ''
        + '<li class="building-card" data-building-index="' + index + '">'
        +   '<header class="building-card-header">'
        +     '<div class="building-titles">'
        +       '<div class="building-title-name">' + (safeName || '<em>(unnamed)</em>') + '</div>'
        +       '<div class="building-title-id">' + escapeHtml(custom.id) + '</div>'
        +       '<label class="building-title-template">'
        +         '<span>Template:</span>'
        +         '<select data-building-field="templateId">' + tplOpts + '</select>'
        +       '</label>'
        +       (tplHint ? '<small class="building-title-template"><span>' + tplHint + '</span></small>' : '')
        +     '</div>'
        +     '<div class="building-card-actions">'
        +       '<button type="button" class="btn-link danger" data-building-action="delete" title="Delete building">Delete</button>'
        +     '</div>'
        +   '</header>'
        +   '<div class="building-fields">'
        +     '<label class="building-field">'
        +       '<span>Name (in-game)</span>'
        +       '<input type="text" data-building-field="name" value="' + safeName + '" placeholder="Building display name">'
        +     '</label>'
        +     '<label class="building-field">'
        +       '<span>Asset prefix</span>'
        +       '<input type="text" data-building-field="assetPrefix" value="' + safePrefix + '" placeholder="QmPainting">'
        +     '</label>'
        +     '<label class="building-field building-field-wide">'
        +       '<span>Description (tooltip)</span>'
        +       '<textarea data-building-field="description" rows="2" placeholder="Tooltip text...">' + safeDesc + '</textarea>'
        +     '</label>'
        +     '<label class="building-field building-field-wide">'
        +       '<span>Cooked folder path</span>'
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
        +       '<span>Mesh stem (SM_...)</span>'
        +       '<input type="text" data-building-field="meshStem" value="' + safeMesh + '" placeholder="SM_QmPainting_01">'
        +     '</label>'
        +     '<label class="building-field">'
        +       '<span>Icon stem (T_..._Icon)</span>'
        +       '<input type="text" data-building-field="iconStem" value="' + safeIcon + '" placeholder="T_QmPainting_Icon">'
        +     '</label>'
        +     slotsHtml
        +   '</div>'
        + '</li>';
}

function renderBuildingSlots(custom, tpl) {
    if (!tpl || !Array.isArray(tpl.slots) || tpl.slots.length === 0) {
        return '';
    }
    const slotsObj = custom.slots || {};
    const parts = ['<div class="building-slots">'];
    parts.push('<small class="building-field"><span>Material slots</span></small>');
    for (const slotDef of tpl.slots) {
        const sn = slotDef.slotName || '';
        const s = slotsObj[sn] || {};
        const safeSn = escapeHtml(sn);
        const albedoStem = escapeHtml(s.customAlbedoStem || '');
        const albedoPath = escapeHtml(s.customAlbedoPath || '');
        const required = !!slotDef.userAlbedoRequired;
        parts.push(''
            + '<div class="building-slot" data-building-slot-name="' + safeSn + '">'
            +   '<div class="building-slot-header">'
            +     '<span class="building-slot-name">' + safeSn + '</span>'
            +     (required
                ? '<span class="building-slot-required">Image required</span>'
                : '<span class="building-slot-required" style="color:var(--text-dim);">Optional</span>')
            +   '</div>'
            +   '<div class="building-slot-row">'
            +     '<label>Image stem</label>'
            +     '<input type="text" data-building-slot-field="customAlbedoStem"'
            +       ' value="' + albedoStem + '"'
            +       ' placeholder="' + (required ? 'T_QmPainting_Image (required)' : 'leave empty to use default white VT') + '">'
            +   '</div>'
            +   '<div class="building-slot-row">'
            +     '<label>Image path</label>'
            +     '<input type="text" data-building-slot-field="customAlbedoPath"'
            +       ' value="' + albedoPath + '"'
            +       ' placeholder="' + (required
                        ? '/Game/Quartermaster/Items/T_QmPainting_Image'
                        : 'leave empty to use default white VT') + '">'
            +   '</div>'
            + '</div>');
    }
    parts.push('</div>');
    return parts.join('');
}

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

    // Build an empty Slots dict pre-keyed with the template's slot
    // names so the patcher pipeline sees the right shape without the
    // user needing to type the slot name manually.
    const slots = {};
    if (Array.isArray(template.slots)) {
        for (const s of template.slots) {
            if (s && s.slotName) {
                slots[s.slotName] = {
                    customAlbedoStem: null,
                    customAlbedoPath: null,
                    customNormalStem: null,
                    customNormalPath: null,
                    customMtrmStem:   null,
                    customMtrmPath:   null,
                };
            }
        }
    }

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
        slots,
    });
    state.isDirty = true;
    renderBuildingCreator();
    renderBuildingCreatorStatus();
    updateButtons();
}

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
            // Debounce scan so we don't hammer the endpoint on every
            // keystroke. The scan is read-only, but typing a long path
            // would otherwise trigger 30+ FS reads.
            debounceScan(card, index, custom.cookedFolderPath);
        } else if (field === 'assetPrefix') {
            custom.assetPrefix = t.value || '';
        } else if (field === 'meshStem') {
            custom.meshStem = t.value || '';
        } else if (field === 'iconStem') {
            custom.iconStem = t.value || '';
        } else if (field === 'templateId') {
            custom.templateId = t.value;
            // Reset slot dict to the new template's shape - any
            // previous slot inputs that no longer apply get dropped.
            const tpl = state.buildingTemplates.byId.get(custom.templateId);
            const newSlots = {};
            if (tpl && Array.isArray(tpl.slots)) {
                for (const s of tpl.slots) {
                    if (s && s.slotName) {
                        const prior = (custom.slots && custom.slots[s.slotName]) || {};
                        newSlots[s.slotName] = {
                            customAlbedoStem: prior.customAlbedoStem || null,
                            customAlbedoPath: prior.customAlbedoPath || null,
                            customNormalStem: prior.customNormalStem || null,
                            customNormalPath: prior.customNormalPath || null,
                            customMtrmStem:   prior.customMtrmStem   || null,
                            customMtrmPath:   prior.customMtrmPath   || null,
                        };
                    }
                }
            }
            custom.slots = newSlots;
            renderBuildingCreator();
        } else {
            return;
        }
    } else if (t.dataset.buildingSlotField) {
        const slotCard = t.closest('.building-slot');
        if (!slotCard) return;
        const sn = slotCard.dataset.buildingSlotName;
        if (!sn) return;
        custom.slots = custom.slots || {};
        custom.slots[sn] = custom.slots[sn] || {
            customAlbedoStem: null,
            customAlbedoPath: null,
            customNormalStem: null,
            customNormalPath: null,
            customMtrmStem:   null,
            customMtrmPath:   null,
        };
        custom.slots[sn][t.dataset.buildingSlotField] = t.value || null;
    } else {
        return;
    }
    state.isDirty = true;
    renderBuildingCreatorStatus();
    updateButtons();
}

async function onBuildingListClick(e) {
    const btn = e.target.closest('button[data-building-action]');
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
    }
}

// Debounced scan trigger. Two semi-independent caches:
//   _buildingScanTimers : per-card timeout id so a fast typer only
//                         fires one scan after they stop
//   _buildingScanCache  : remembers the last path each card scanned
//                         so a re-render of the same path doesn't
//                         re-fire the request
const _buildingScanTimers = new Map();
const _buildingScanCache  = new Map(); // index -> last scanned path

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

    // Skip if we already scanned this exact path for this card, AND
    // the host still shows scan output (a re-render replaces the
    // "Scanning..." placeholder, so we need to refresh in that case).
    const cached = _buildingScanCache.get(index);
    if (cached === path && host.querySelector('.building-scan-files')) {
        return;
    }

    host.innerHTML = '<div class="building-scan"><em>Scanning ' + escapeHtml(path) + '...</em></div>';
    try {
        const scan = await api('GET', '/api/buildings/scan-cooked?path=' + encodeURIComponent(path));
        _buildingScanCache.set(index, path);
        host.innerHTML = renderScanResult(scan, customs[index]);
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

    // Tally counts by kind so the GUI can summarise without forcing
    // the user to read every filename.
    const counts = {};
    for (const e of entries) {
        counts[e.kind] = (counts[e.kind] || 0) + 1;
    }
    const totalAssets = (counts.mesh || 0) + (counts.icon || 0) + (counts.texture || 0)
                      + (counts.material || 0) + (counts.matinst || 0)
                      + (counts.blueprint || 0) + (counts.data || 0);

    // Sanity warnings: things that would later break the build.
    const warnings = [];
    if ((counts.mesh || 0) === 0) {
        warnings.push('<span class="bad">No mesh (SM_*) found.</span>');
    }
    if ((counts.icon || 0) === 0) {
        warnings.push('<span class="bad">No icon (T_*_Icon) found.</span>');
    }
    if ((counts.material || 0) + (counts.matinst || 0) > 0) {
        warnings.push('<span class="skip">'
            + ((counts.material || 0) + (counts.matinst || 0))
            + ' user-cooked material(s) - will be skipped at build (replaced by Vanilla-MI clone).</span>');
    }
    // Cross-check the user's MeshStem / IconStem against what's
    // actually in the folder. Mismatches are common when the user
    // copies a stem from an older project.
    const stems = new Set(entries.map(e => e.stem));
    if (building && building.meshStem && !stems.has(building.meshStem)) {
        warnings.push('<span class="bad">Mesh stem "'
            + escapeHtml(building.meshStem) + '" not found in folder.</span>');
    }
    if (building && building.iconStem && !stems.has(building.iconStem)) {
        warnings.push('<span class="bad">Icon stem "'
            + escapeHtml(building.iconStem) + '" not found in folder.</span>');
    }

    // Per-required-slot albedo presence check.
    const tpl = state.buildingTemplates.byId.get(building.templateId);
    if (tpl && Array.isArray(tpl.slots)) {
        for (const sd of tpl.slots) {
            if (!sd || !sd.userAlbedoRequired) continue;
            const ss = (building.slots && building.slots[sd.slotName]) || {};
            if (!ss.customAlbedoStem) {
                warnings.push('<span class="bad">Slot "'
                    + escapeHtml(sd.slotName)
                    + '" requires an image stem - set it below.</span>');
            } else if (!stems.has(ss.customAlbedoStem)) {
                warnings.push('<span class="bad">Slot "'
                    + escapeHtml(sd.slotName) + '" image "'
                    + escapeHtml(ss.customAlbedoStem) + '" not found in folder.</span>');
            }
        }
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

function bindBuildingsHandlers() {
    const btn = document.getElementById('btn-buildings-new');
    if (btn) btn.addEventListener('click', onBuildingsNew);

    const list = document.getElementById('buildings-list');
    if (list) {
        list.addEventListener('input',  onBuildingListChange);
        list.addEventListener('change', onBuildingListChange);
        list.addEventListener('click',  onBuildingListClick);
    }
}
