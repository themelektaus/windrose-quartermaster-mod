'use strict';

function getLootGlobalForCategory(cat) {
    const g = state.current && state.current.globals;
    if (!g || !g.loot || !g.loot.byCategory) return null;
    const v = g.loot.byCategory[cat];
    return typeof v === 'number' ? v : null;
}

function renderLootGlobals() {
    const out = document.getElementById('loot-globals');
    if (!out) return;
    const frag = document.createDocumentFragment();
    for (const c of state.lootCategories) {
        const v = getLootGlobalForCategory(c.name);

        const cat = document.createElement('span');
        cat.className = 'cat';
        cat.textContent = c.name;
        const count = document.createElement('span');
        count.className = 'cat-count';
        count.textContent = '(' + c.count + ')';
        cat.appendChild(count);

        const input = document.createElement('input');
        input.type = 'number';
        input.min = '0';
        input.step = '0.5';
        input.placeholder = '1.0';
        input.dataset.lootCat = c.name;
        if (v != null) input.value = v;

        const reset = document.createElement('button');
        reset.className = 'reset';
        reset.type = 'button';
        reset.dataset.resetCat = c.name;
        reset.textContent = 'x';

        frag.append(cat, input, reset);
    }
    out.replaceChildren(frag);
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

function isLootGlobalEmpty() {
    const g = state.current && state.current.globals && state.current.globals.loot;
    if (!g) return true;
    if (g.byCategory && Object.keys(g.byCategory).length > 0) return false;
    if (g.treeMultiplier != null) return false;
    if (g.digVolumeMultiplier != null) return false;
    return true;
}

function pruneLootGlobal() {
    if (state.current && state.current.globals && isLootGlobalEmpty()) {
        delete state.current.globals.loot;
    }
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
    pruneLootGlobal();
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
        pruneLootGlobal();
    }
    markDirty();
    renderLootGlobals();
    renderLootStatus();
    renderLootTables();
}

function renderResourceMults() {
    const g = state.current && state.current.globals && state.current.globals.loot;
    const treeInput    = document.getElementById('loot-tree-mult');
    const digInput     = document.getElementById('loot-digvol-mult');
    if (treeInput)    treeInput.value    = (g && g.treeMultiplier != null) ? g.treeMultiplier : '';
    if (digInput)     digInput.value     = (g && g.digVolumeMultiplier != null) ? g.digVolumeMultiplier : '';
}

function setResourceMult(field, rawValue) {
    if (!state.current) return;
    state.current.globals = state.current.globals || {};
    state.current.globals.loot = state.current.globals.loot || {};

    const trimmed = (rawValue || '').trim();
    if (trimmed === '') {
        delete state.current.globals.loot[field];
    } else {
        const n = parseFloat(trimmed);
        if (!isFinite(n) || n < 0) return;
        state.current.globals.loot[field] = n;
    }
    pruneLootGlobal();
    markDirty();
}

function resetResourceMult(field) {
    if (!state.current) return;
    const g = state.current.globals && state.current.globals.loot;
    if (!g || g[field] == null) return;
    delete g[field];
    pruneLootGlobal();
    markDirty();
    renderResourceMults();
}

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

function computeLtChanged(lt) {
    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    if (ovr) {
        if (ovr.added && ovr.added.length > 0) return true;
        if (ovr.removed && ovr.removed.length > 0) return true;
        if (ovr.entries && Object.keys(ovr.entries).length > 0) return true;
    }
    const mult = getLootGlobalForCategory(lt.category);
    if (mult != null && mult !== 1) {
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
    ul.replaceChildren(frag);
}

function ltBadge(kind, text) {
    const span = document.createElement('span');
    span.className = 'lt-badge ' + kind;
    span.textContent = text;
    return span;
}

function buildLtRow(lt) {
    const li = cloneTemplate('tpl-lt-row');
    if (!state.expandedLts.has(lt.id)) li.classList.add('collapsed');
    li.dataset.ltId = lt.id;
    if (computeLtChanged(lt))    li.classList.add('changed');
    if (computeLtOverridden(lt)) li.classList.add('overridden');

    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    const editCount = ovr && ovr.entries ? Object.keys(ovr.entries).length : 0;
    const remCount  = ovr && ovr.removed ? ovr.removed.length : 0;
    const addCount  = ovr && ovr.added   ? ovr.added.length   : 0;
    const mult      = getLootGlobalForCategory(lt.category);

    const header = li.querySelector('.lt-header');
    header.dataset.toggle = lt.id;
    header.querySelector('.lt-id').textContent = lt.id;
    header.querySelector('.lt-meta-info').textContent =
        (lt.type || '') + ' · ' + (lt.entries ? lt.entries.length : 0) + ' entries';

    const badgesEl = header.querySelector('.lt-meta-badges');
    const parts = [];
    if (mult != null && mult !== 1) parts.push(document.createTextNode('×' + mult));
    if (editCount > 0) parts.push(ltBadge('edited',  editCount + ' edited'));
    if (remCount  > 0) parts.push(ltBadge('removed', remCount  + ' removed'));
    if (addCount  > 0) parts.push(ltBadge('added',   addCount  + ' added'));
    parts.forEach((node, i) => {
        if (i > 0) badgesEl.appendChild(document.createTextNode(' '));
        badgesEl.appendChild(node);
    });

    if (state.expandedLts.has(lt.id)) {
        renderLtBody(li, lt);
    }
    return li;
}

function renderLtBody(li, lt) {
    const body = li.querySelector('.lt-body');
    if (!body) return;

    const frag = document.createDocumentFragment();

    for (const e of lt.entries) {
        frag.appendChild(buildLtEntryRowNode(lt, e, false));
    }

    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    if (ovr && ovr.added) {
        for (let i = 0; i < ovr.added.length; i++) {
            frag.appendChild(buildLtAddedRowNode(lt, ovr.added[i], i, false));
        }
    }

    const addRow = document.createElement('div');
    addRow.className = 'lt-add-row';
    const addBtn = document.createElement('button');
    addBtn.type = 'button';
    addBtn.className = 'add-btn';
    addBtn.dataset.addEntry = lt.id;
    addBtn.textContent = '+ Add entry';
    addRow.appendChild(addBtn);
    frag.appendChild(addRow);

    body.replaceChildren(frag);
}

// Fills the cloned target cell; opts.icon -> <img>, else a glyph placeholder.
function configureLtTarget(row, opts) {
    const iconEl = row.querySelector('.placeholder-icon');
    if (opts.icon) {
        const img = document.createElement('img');
        img.src = opts.icon;
        img.alt = '';
        img.loading = 'lazy';
        iconEl.replaceWith(img);
    } else if (opts.glyphInSpan) {
        const span = document.createElement('span');
        span.textContent = opts.glyph;
        iconEl.appendChild(span);
    } else {
        iconEl.textContent = opts.glyph;
    }
    const targetEl = row.querySelector('.target');
    if (opts.subtable) targetEl.classList.add('subtable');
    targetEl.querySelector('b').textContent = opts.bText;
    targetEl.querySelector('small').textContent = opts.smallText;
}

function setLtNumInput(input, ltId, index, placeholder, value, disabled) {
    input.placeholder = placeholder;
    input.value = value;
    input.dataset.ltId = ltId;
    input.dataset.index = index;
    if (disabled) input.disabled = true;
}

function buildLtEntryRowNode(lt, e, isReadonly) {
    const r = resolveLootEntry(lt, e);
    const row = cloneTemplate('tpl-lt-entry-row');
    if (r.removed) row.classList.add('removed');
    if (r.edited)  row.classList.add('edited');
    row.dataset.ltId = lt.id;
    row.dataset.vanillaIndex = e.index;

    const isItem  = !!e.lootItemId;
    const isTable = !!e.lootTableId;
    const item    = isItem ? state.itemsById.get(e.lootItemId) : null;
    if (isItem) {
        const name = (item && item.meta && item.meta.name) || e.lootItemId;
        configureLtTarget(row, {
            icon: item && item.icon ? item.icon : null, glyph: '?',
            bText: name, smallText: e.lootItemId,
        });
    } else if (isTable) {
        configureLtTarget(row, {
            glyph: '▦', glyphInSpan: true, subtable: true,
            bText: e.lootTableId, smallText: '(sub-table)',
        });
    } else {
        configureLtTarget(row, { glyph: '·', bText: '(no drop)', smallText: '' });
    }

    const ovr  = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    const edit = (ovr && ovr.entries && ovr.entries[String(e.index)]) || null;
    const minVal    = edit && edit.min    != null ? edit.min    : '';
    const maxVal    = edit && edit.max    != null ? edit.max    : '';
    const weightVal = edit && edit.weight != null ? edit.weight : '';
    const minPh = r.changedByMult ? r.min : e.min;
    const maxPh = r.changedByMult ? r.max : e.max;

    const valDisabled = isReadonly || r.removed;
    const inputs = row.querySelectorAll('[data-edit-field]');
    setLtNumInput(inputs[0], lt.id, e.index, minPh,    minVal,    valDisabled);
    setLtNumInput(inputs[1], lt.id, e.index, maxPh,    maxVal,    valDisabled);
    setLtNumInput(inputs[2], lt.id, e.index, e.weight, weightVal, valDisabled);

    const removeBtn = row.querySelector('[data-toggle-remove]');
    removeBtn.dataset.toggleRemove = lt.id;
    removeBtn.dataset.index = e.index;
    if (isReadonly) removeBtn.disabled = true;
    return row;
}

function setLtAddedNumInput(input, ltId, addedIndex, value, disabled) {
    input.value = value;
    input.dataset.ltId = ltId;
    input.dataset.addedIndex = addedIndex;
    if (disabled) input.disabled = true;
}

function buildLtAddedRowNode(lt, addedEntry, addedIndex, isReadonly) {
    const a = addedEntry || {};
    const isItem    = !!(a.lootItem  && a.lootItem  !== 'None');
    const isTable   = !!(a.lootTable && a.lootTable !== 'None');
    const isNoDrop  = a.lootItem === 'None' && a.lootTable === 'None';
    const inferredItemId  = isItem  ? lastSegment(a.lootItem)        : null;
    const inferredTableId = isTable ? lootTablePathToId(a.lootTable) : null;
    const item = inferredItemId ? state.itemsById.get(inferredItemId) : null;

    if (!isItem && !isTable && !isNoDrop) {
        return buildLtAddedFormNode(lt, a, addedIndex, isReadonly);
    }

    const row = cloneTemplate('tpl-lt-added-row');
    row.dataset.ltId = lt.id;
    row.dataset.addedIndex = addedIndex;

    if (isItem) {
        const name = (item && item.meta && item.meta.name) || inferredItemId || '(item)';
        configureLtTarget(row, {
            icon: item && item.icon ? item.icon : null, glyph: '+',
            bText: name, smallText: a.lootItem,
        });
    } else if (isTable) {
        configureLtTarget(row, {
            glyph: '▦', subtable: true,
            bText: inferredTableId || a.lootTable, smallText: '(added sub-table)',
        });
    } else {
        configureLtTarget(row, { glyph: '·', bText: '(no drop)', smallText: 'added empty slot' });
    }

    const inputs = row.querySelectorAll('[data-added-field]');
    setLtAddedNumInput(inputs[0], lt.id, addedIndex, a.min || 1, isReadonly);
    setLtAddedNumInput(inputs[1], lt.id, addedIndex, a.max || 1, isReadonly);
    setLtAddedNumInput(inputs[2], lt.id, addedIndex, a.weight || 0, isReadonly);

    const delBtn = row.querySelector('[data-delete-added]');
    delBtn.dataset.deleteAdded = lt.id;
    delBtn.dataset.addedIndex = addedIndex;
    if (isReadonly) delBtn.disabled = true;
    return row;
}

function buildLtAddedFormNode(lt, a, addedIndex, isReadonly) {
    const row = cloneTemplate('tpl-lt-add-form');
    row.dataset.ltId = lt.id;
    row.dataset.addedIndex = addedIndex;

    const select = row.querySelector('[data-add-form-type]');
    select.dataset.addFormType = lt.id;
    select.dataset.addedIndex = addedIndex;
    if (isReadonly) select.disabled = true;

    const target = row.querySelector('[data-add-form-target]');
    target.dataset.addFormTarget = lt.id;
    target.dataset.addedIndex = addedIndex;
    if (isReadonly) target.disabled = true;

    const inputs = row.querySelectorAll('[data-added-field]');
    setLtAddedNumInput(inputs[0], lt.id, addedIndex, a.min || 1, isReadonly);
    setLtAddedNumInput(inputs[1], lt.id, addedIndex, a.max || 1, isReadonly);
    setLtAddedNumInput(inputs[2], lt.id, addedIndex, a.weight || 0, isReadonly);

    const delBtn = row.querySelector('[data-delete-added]');
    delBtn.dataset.deleteAdded = lt.id;
    delBtn.dataset.addedIndex = addedIndex;
    if (isReadonly) delBtn.disabled = true;
    return row;
}

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
    renderLootStatus();
}

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

function refreshLtRow(ltId) {
    const ul = document.getElementById('lt-list');
    const old = ul && ul.querySelector('.lt[data-lt-id="' + cssEsc(ltId) + '"]');
    if (!old) return;
    const lt = state.lootById.get(ltId);
    if (!lt) return;
    const fresh = buildLtRow(lt);
    old.replaceWith(fresh);
}

function populateLootCategoryFilter() {
    const cat = document.getElementById('lt-filter-category');
    cat.replaceChildren(new Option('All categories', ''));
    for (const c of state.lootCategories) {
        const o = document.createElement('option');
        o.value = c.name;
        o.textContent = c.name + ' (' + c.count + ')';
        cat.appendChild(o);
    }
    const tp = document.getElementById('lt-filter-type');
    tp.replaceChildren(new Option('All types', ''));
    for (const t of state.lootTypes) {
        const o = document.createElement('option');
        o.value = t; o.textContent = t;
        tp.appendChild(o);
    }
}

function onLtListClick(e) {
    const t = e.target;
    if (!t || !t.dataset) return;

    if (t.closest && t.closest('.lt-header') && !t.matches('input, button, select')) {
        const header = t.closest('.lt-header');
        const ltId = header.dataset.toggle;
        if (state.expandedLts.has(ltId)) state.expandedLts.delete(ltId);
        else                              state.expandedLts.add(ltId);
        refreshLtRow(ltId);
        return;
    }

    if (t.dataset.toggleRemove) {
        toggleLootEntryRemoved(t.dataset.toggleRemove, parseInt(t.dataset.index, 10));
        return;
    }

    if (t.dataset.addEntry) {
        addLootEntry(t.dataset.addEntry);
        return;
    }

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
    if (t.dataset.addFormTarget && state.picker && state.picker.input === t) {
        populatePicker(t.value);
        positionPicker(t);
    }
}

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

function onLtListFocusIn(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.addFormTarget) return;
    const mode = t.dataset.pickerMode || 'item';
    if (mode === 'nodrop') return;
    const ltId = t.dataset.addFormTarget;
    const idx  = parseInt(t.dataset.addedIndex, 10);
    openPicker(t, ltId, idx, mode);
}

function bindLootHandlers() {
    document.getElementById('loot-globals').addEventListener('input', e => {
        const cat = e.target.dataset && e.target.dataset.lootCat;
        if (cat) setLootGlobalFromInput(cat, e.target.value);
    });
    document.getElementById('loot-globals').addEventListener('click', e => {
        const cat = e.target.dataset && e.target.dataset.resetCat;
        if (cat) resetLootGlobalCategory(cat);
    });

    document.getElementById('loot-tree-mult').addEventListener('input', e => {
        setResourceMult('treeMultiplier', e.target.value);
    });
    document.getElementById('loot-digvol-mult').addEventListener('input', e => {
        setResourceMult('digVolumeMultiplier', e.target.value);
    });
    document.getElementById('loot-tree-mult-reset').addEventListener('click', () => {
        resetResourceMult('treeMultiplier');
    });
    document.getElementById('loot-digvol-mult-reset').addEventListener('click', () => {
        resetResourceMult('digVolumeMultiplier');
    });
    document.getElementById('lt-filter').addEventListener('input',           renderLootTables);
    document.getElementById('lt-filter-category').addEventListener('change', renderLootTables);
    document.getElementById('lt-filter-type').addEventListener('change',     renderLootTables);
    document.getElementById('lt-filter-changed').addEventListener('change',  renderLootTables);

    const ltList = document.getElementById('lt-list');
    ltList.addEventListener('click',   onLtListClick);
    ltList.addEventListener('input',   onLtListInput);
    ltList.addEventListener('change',  onLtListChange);
    ltList.addEventListener('focusin', onLtListFocusIn);

    ltList.addEventListener('scroll', () => {
        if (state.picker) positionPicker(state.picker.input);
    }, { passive: true });
}
