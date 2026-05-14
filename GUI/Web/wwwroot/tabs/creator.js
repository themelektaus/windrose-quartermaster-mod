async function loadItemTemplates() {
    const errBox = document.getElementById('creator-error');
    errBox.hidden = true;
    errBox.textContent = '';
    try {
        const list = await api('GET', '/api/item-templates');
        const byId = new Map();
        for (const t of list || []) byId.set(t.id, t);
        state.itemTemplates.list = list || [];
        state.itemTemplates.byId = byId;
        state.itemTemplates.loaded = true;
        state.itemTemplates.error = null;
    } catch (ex) {
        state.itemTemplates.error = (ex && ex.message) ? ex.message : String(ex);
        errBox.hidden = false;
        errBox.textContent = 'Failed to load templates: ' + state.itemTemplates.error;
    }
}

function newCustomItemId() {
    const bytes = new Uint8Array(4);
    if (window.crypto && window.crypto.getRandomValues) {
        window.crypto.getRandomValues(bytes);
    } else {
        for (let i = 0; i < 4; i++) bytes[i] = Math.floor(Math.random() * 256);
    }
    let hex = '';
    for (let i = 0; i < 4; i++) hex += bytes[i].toString(16).padStart(2, '0');
    return 'QmItem_' + hex;
}

function renderItemCreator() {
    const list = document.getElementById('creator-list');
    if (!state.current) {
        list.innerHTML = '';
        return;
    }
    const customs = state.current.customItems || [];
    if (customs.length === 0) {
        list.innerHTML = '';
        return;
    }
    const parts = [];
    for (let i = 0; i < customs.length; i++) {
        parts.push(buildCustomItemCardHtml(customs[i], i));
    }
    list.innerHTML = parts.join('');
}

function buildCustomItemCardHtml(custom, index) {
    if (!custom) return '';
    const tpl = state.itemTemplates.byId.get(custom.templateId) || null;
    const profileId = state.current ? state.current.id : '';
    const hasCustomIcon = !!(custom.iconPath && profileId);
    const iconUrl = hasCustomIcon
        ? '/api/profiles/' + encodeURIComponent(profileId)
            + '/icons/' + encodeURIComponent(custom.id)
            + '?t=' + (custom._iconCacheBust || 0)
        : (tpl && custom.templateId
            ? '/Icons/' + encodeURIComponent(custom.templateId) + '.png'
            : '');

    const rarity = custom.rarity || (tpl ? tpl.defaultRarity : 'Common');
    const maxStack = (custom.maxCountInSlot != null)
        ? custom.maxCountInSlot
        : (tpl ? tpl.defaultMaxCountInSlot : 1);
    const keepOnDeath = (custom.keepInInventoryOnDeath != null)
        ? !!custom.keepInInventoryOnDeath
        : !!(tpl && tpl.defaultKeepInInventoryOnDeath);
    const synthesizedTextureRef = '/Game/UI/Icons/Items/Custom/T_QmCustomIcon_'
        + custom.id + '.T_QmCustomIcon_' + custom.id;
    const iconPath = hasCustomIcon
        ? synthesizedTextureRef
        : (custom.itemTexture || (tpl ? tpl.defaultItemTexture : ''));

    const rarityOpts = ['Common', 'Uncommon', 'Rare', 'Epic', 'Legendary']
        .map(r => `<option value="${r}"${r === rarity ? ' selected' : ''}>${r}</option>`)
        .join('');

    const tplCatalog = state.itemTemplates.list || [];
    let tplOpts = tplCatalog.map(t =>
        `<option value="${escapeHtml(t.id)}"${t.id === custom.templateId ? ' selected' : ''}>${escapeHtml(t.label)}</option>`
    ).join('');
    if (custom.templateId && !state.itemTemplates.byId.has(custom.templateId)) {
        tplOpts = `<option value="${escapeHtml(custom.templateId)}" selected>${escapeHtml(custom.templateId)} (unknown)</option>` + tplOpts;
    }

    const safeName = escapeHtml(custom.name || '');
    const safeDesc = escapeHtml(custom.description || '');
    const safeVanity = escapeHtml(custom.vanityText || '');

    return ''
        + '<li class="creator-card" data-custom-index="' + index + '">'
        +   '<header class="creator-card-header">'
        +     (iconUrl ? '<img class="creator-icon" src="' + iconUrl + '" alt="">' : '<div class="creator-icon-placeholder"></div>')
        +     '<div class="creator-titles">'
        +       '<div class="creator-title-name">' + (safeName || '<em>(unnamed)</em>') + '</div>'
        +       '<div class="creator-title-id">' + escapeHtml(custom.id) + '</div>'
        +       '<label class="creator-title-template">'
        +         '<span>Based on:</span>'
        +         '<select data-creator-field="templateId">' + tplOpts + '</select>'
        +       '</label>'
        +     '</div>'
        +     '<div class="creator-card-actions">'
        +       '<button type="button" class="btn-link danger" data-creator-action="delete" title="Delete custom item">Delete</button>'
        +     '</div>'
        +   '</header>'
        +   '<div class="creator-fields">'
        +     '<div class="creator-field creator-field-wide creator-icon-actions">'
        +       '<input type="file" accept="image/png" data-creator-action="icon-pick" hidden>'
        +       '<button type="button" class="btn-link" data-creator-action="icon-upload" title="Upload PNG (auto-resized to 256x256)">Upload Icon...</button>'
        +       '<button type="button" class="btn-link" data-creator-action="icon-reset"'
        +           (hasCustomIcon ? '' : ' disabled')
        +           ' title="Revert to template icon">Reset</button>'
        +       '<span class="creator-icon-status">'
        +           (hasCustomIcon ? 'Custom PNG uploaded' : 'Template icon')
        +       '</span>'
        +     '</div>'
        +     '<label class="creator-field">'
        +       '<span>Name</span>'
        +       '<input type="text" data-creator-field="name" value="' + safeName + '" placeholder="Item display name">'
        +     '</label>'
        +     '<label class="creator-field creator-field-wide">'
        +       '<span>Description</span>'
        +       '<textarea data-creator-field="description" rows="2" placeholder="Tooltip text...">' + safeDesc + '</textarea>'
        +     '</label>'
        +     '<label class="creator-field creator-field-wide">'
        +       '<span>Vanity Text</span>'
        +       '<input type="text" data-creator-field="vanityText" value="' + safeVanity + '" placeholder="Optional flavor text...">'
        +     '</label>'
        +     '<label class="creator-field">'
        +       '<span>Max stack</span>'
        +       '<input type="number" min="1" step="1" data-creator-field="maxCountInSlot" value="' + maxStack + '">'
        +     '</label>'
        +     '<label class="creator-field">'
        +       '<span>Rarity</span>'
        +       '<select data-creator-field="rarity">' + rarityOpts + '</select>'
        +     '</label>'
        +     '<label class="creator-field creator-checkbox">'
        +       '<input type="checkbox" data-creator-field="keepInInventoryOnDeath"' + (keepOnDeath ? ' checked' : '') + '>'
        +       '<span>Keep in inventory on death</span>'
        +     '</label>'
        +     '<label class="creator-field creator-field-wide" style="display: none; ">'
        +       '<span>Icon path</span>'
        +       '<input type="text" value="' + escapeHtml(iconPath) + '" readonly disabled>'
        +     '</label>'
        +   '</div>'
        + '</li>';
}

function renderItemCreatorStatus() {
    const customs = (state.current && state.current.customItems) || [];
    const tpls = state.itemTemplates.list.length;
    document.getElementById('creator-stat-count').textContent     = customs.length;
    document.getElementById('creator-stat-templates').textContent = tpls;
    document.getElementById('creator-count').textContent =
        customs.length === 0 ? '' : (customs.length + ' item' + (customs.length === 1 ? '' : 's'));
}

async function onCreatorNew() {
    if (!state.current) return;
    if (!state.itemTemplates.loaded) {
        await loadItemTemplates();
    }
    const tpls = state.itemTemplates.list;
    if (tpls.length === 0) {
        await alert('No templates available - the backend returned an empty catalog.');
        return;
    }
    const template = tpls[0];
    const name = await prompt('Name for the new item:', 'My ' + template.label);
    if (name == null) return;
    const trimmed = String(name).trim();
    if (!trimmed) return;

    state.current.customItems = state.current.customItems || [];
    state.current.customItems.push({
        id: newCustomItemId(),
        templateId: template.id,
        name: trimmed,
        description: '',
        maxCountInSlot: null,
        rarity: null,
        keepInInventoryOnDeath: null,
        itemTexture: null,
        vanityText: '',
    });
    state.isDirty = true;
    syncCustomItemsIntoCatalog();
    renderItemCreator();
    renderItemCreatorStatus();
    updateButtons();
}

function onCreatorListChange(e) {
    const t = e.target;
    const field = t && t.dataset ? t.dataset.creatorField : null;
    if (!field) return;
    const card = t.closest('li.creator-card');
    if (!card) return;
    const index = parseInt(card.dataset.customIndex, 10);
    if (!isFinite(index)) return;
    const custom = (state.current && state.current.customItems || [])[index];
    if (!custom) return;

    if (field === 'name') {
        custom.name = t.value;
        const titleEl = card.querySelector('.creator-title-name');
        if (titleEl) {
            const safe = escapeHtml(custom.name || '');
            titleEl.innerHTML = safe || '<em>(unnamed)</em>';
        }
    } else if (field === 'description') {
        custom.description = t.value;
    } else if (field === 'maxCountInSlot') {
        const n = parseInt(t.value, 10);
        custom.maxCountInSlot = isFinite(n) && n > 0 ? n : null;
    } else if (field === 'rarity') {
        custom.rarity = t.value || null;
    } else if (field === 'keepInInventoryOnDeath') {
        custom.keepInInventoryOnDeath = !!t.checked;
    } else if (field === 'vanityText') {
        custom.vanityText = t.value || '';
    } else if (field === 'templateId') {
        custom.templateId = t.value;
        renderItemCreator();
    } else {
        return;
    }
    state.isDirty = true;
    syncCustomItemsIntoCatalog();
    renderItemCreatorStatus();
    updateButtons();
}

async function onCreatorListClick(e) {
    const btn = e.target.closest('button[data-creator-action]');
    if (!btn) return;
    const action = btn.dataset.creatorAction;
    const card = btn.closest('li.creator-card');
    if (!card) return;
    const index = parseInt(card.dataset.customIndex, 10);
    if (!isFinite(index)) return;
    const customs = state.current && state.current.customItems;
    if (!customs || !customs[index]) return;

    if (action === 'delete') {
        const c = customs[index];
        const label = c.name || c.id;
        if (!await confirm('Delete custom item "' + label + '"?')) return;
        customs.splice(index, 1);
        state.isDirty = true;
        syncCustomItemsIntoCatalog();
        renderItemCreator();
        renderItemCreatorStatus();
        updateButtons();
    } else if (action === 'icon-upload') {
        const c = customs[index];
        const savedIds = state.savedCustomItemIds || new Set();
        if (!savedIds.has(c.id)) {
            await alert('Save the profile first - the new custom item must exist on disk before its icon can be uploaded.');
            return;
        }
        const filePicker = card.querySelector('input[type="file"][data-creator-action="icon-pick"]');
        if (filePicker) filePicker.click();
    } else if (action === 'icon-reset') {
        const c = customs[index];
        if (!c.iconPath) return;
        if (!await confirm('Revert "' + (c.name || c.id) + '" to the template icon?\n\nThe uploaded PNG will be deleted.')) return;
        try {
            const resp = await fetch(
                '/api/profiles/' + encodeURIComponent(state.current.id)
                + '/icons/' + encodeURIComponent(c.id),
                { method: 'DELETE' });
            if (!resp.ok) throw new Error('HTTP ' + resp.status + ' ' + resp.statusText);
            c.iconPath = null;
            c._iconCacheBust = Date.now();
            syncCustomItemsIntoCatalog();
            renderItemCreator();
        } catch (err) {
            await alert('Reset failed: ' + (err && err.message ? err.message : err));
        }
    }
}

async function onCreatorListPickIcon(e) {
    const t = e.target;
    if (!t || !t.dataset || t.dataset.creatorAction !== 'icon-pick') return;
    const file = t.files && t.files[0];
    t.value = '';
    if (!file) return;

    const card = t.closest('li.creator-card');
    if (!card) return;
    const index = parseInt(card.dataset.customIndex, 10);
    if (!isFinite(index)) return;
    const customs = state.current && state.current.customItems;
    if (!customs || !customs[index]) return;
    const custom = customs[index];

    try {
        const head = await file.slice(0, 8).arrayBuffer();
        const b = new Uint8Array(head);
        const isPng = b[0] === 0x89 && b[1] === 0x50 && b[2] === 0x4E && b[3] === 0x47
                   && b[4] === 0x0D && b[5] === 0x0A && b[6] === 0x1A && b[7] === 0x0A;
        if (!isPng) {
            await alert('Not a PNG file. Please pick a .png image.');
            return;
        }
    } catch { /* magic check is best-effort */ }

    try {
        const fd = new FormData();
        fd.append('file', file, file.name);
        const resp = await fetch(
            '/api/profiles/' + encodeURIComponent(state.current.id)
            + '/icons/' + encodeURIComponent(custom.id),
            { method: 'POST', body: fd });
        if (!resp.ok) {
            let msg = 'HTTP ' + resp.status;
            try { const j = await resp.json(); if (j && j.error) msg = j.error; } catch { /* not json */ }
            throw new Error(msg);
        }
        const body = await resp.json();
        custom.iconPath = body.iconPath;
        custom._iconCacheBust = Date.now();
        syncCustomItemsIntoCatalog();
        renderItemCreator();
    } catch (err) {
        await alert('Upload failed: ' + (err && err.message ? err.message : err));
    }
}

function bindCreatorHandlers() {
    document.getElementById('btn-creator-new').addEventListener('click', onCreatorNew);
    const creatorList = document.getElementById('creator-list');
    if (creatorList) {
        creatorList.addEventListener('change', onCreatorListChange);
        creatorList.addEventListener('change', onCreatorListPickIcon);
        creatorList.addEventListener('click',  onCreatorListClick);
    }
}
