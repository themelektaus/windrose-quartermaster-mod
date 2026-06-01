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
        list.replaceChildren();
        return;
    }
    const customs = state.current.customItems || [];
    if (customs.length === 0) {
        list.replaceChildren();
        return;
    }
    const frag = document.createDocumentFragment();
    for (let i = 0; i < customs.length; i++) {
        const node = buildCustomItemCardNode(customs[i], i);
        if (node) frag.appendChild(node);
    }
    list.replaceChildren(frag);
}

function setCreatorTitleName(el, name) {
    if (name) {
        el.textContent = name;
    } else {
        const em = document.createElement('em');
        em.textContent = '(unnamed)';
        el.replaceChildren(em);
    }
}

function buildCustomItemCardNode(custom, index) {
    if (!custom) return null;
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

    const card = cloneTemplate('tpl-creator-card');
    card.dataset.customIndex = index;

    const header = card.querySelector('.creator-card-header');
    if (iconUrl) {
        const img = document.createElement('img');
        img.className = 'creator-icon';
        img.src = iconUrl;
        img.alt = '';
        header.insertBefore(img, header.firstChild);
    } else {
        const ph = document.createElement('div');
        ph.className = 'creator-icon-placeholder';
        header.insertBefore(ph, header.firstChild);
    }

    setCreatorTitleName(card.querySelector('.creator-title-name'), custom.name || '');
    card.querySelector('.creator-title-id').textContent = custom.id;

    const tplSelect = card.querySelector('select[data-creator-field="templateId"]');
    const tplCatalog = state.itemTemplates.list || [];
    if (custom.templateId && !state.itemTemplates.byId.has(custom.templateId)) {
        tplSelect.appendChild(new Option(custom.templateId + ' (unknown)', custom.templateId));
    }
    for (const t of tplCatalog) tplSelect.appendChild(new Option(t.label, t.id));
    if (custom.templateId) tplSelect.value = custom.templateId;

    if (!hasCustomIcon) card.querySelector('button[data-creator-action="icon-reset"]').disabled = true;
    card.querySelector('.creator-icon-status').textContent =
        hasCustomIcon ? 'Custom PNG uploaded' : 'Template icon';

    card.querySelector('input[data-creator-field="name"]').value = custom.name || '';
    card.querySelector('textarea[data-creator-field="description"]').value = custom.description || '';
    card.querySelector('input[data-creator-field="vanityText"]').value = custom.vanityText || '';
    card.querySelector('input[data-creator-field="maxCountInSlot"]').value = maxStack;

    const raritySelect = card.querySelector('select[data-creator-field="rarity"]');
    for (const r of ['Common', 'Uncommon', 'Rare', 'Epic', 'Legendary']) {
        raritySelect.appendChild(new Option(r, r));
    }
    raritySelect.value = rarity;

    card.querySelector('input[data-creator-field="keepInInventoryOnDeath"]').checked = keepOnDeath;
    card.querySelector('.creator-fields > label:last-child input').value = iconPath;
    return card;
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
        if (titleEl) setCreatorTitleName(titleEl, custom.name || '');
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
