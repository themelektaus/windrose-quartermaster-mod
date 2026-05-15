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

function renderLtBody(li, lt) {
    const body = li.querySelector('.lt-body');
    if (!body) return;

    const rows = [];

    for (const e of lt.entries) {
        rows.push(buildLtEntryRowHtml(lt, e, false));
    }

    const ovr = (state.current && state.current.lootOverrides && state.current.lootOverrides[lt.id]) || null;
    if (ovr && ovr.added) {
        for (let i = 0; i < ovr.added.length; i++) {
            rows.push(buildLtAddedRowHtml(lt, ovr.added[i], i, false));
        }
    }

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
        (isReadonly ? ' disabled' : '') + '>⨉</button>';

    return '<div class="' + classes.join(' ') + '" data-lt-id="' + esc(lt.id) + '" data-vanilla-index="' + e.index + '">' +
        targetHtml +
        '<div class="value">' +
        '  <label>Minimum</label>' +
        '  <input type="number" class="num" placeholder="' + minPh + '" value="' + esc(minVal) +
            '" data-edit-field="min" data-lt-id="' + esc(lt.id) + '" data-index="' + e.index + '"' +
            (isReadonly || r.removed ? ' disabled' : '') + '>' +
        '</div>' +
        '<div class="value">' +
        '  <label>Maximum</label>' +
        '  <input type="number" class="num" placeholder="' + maxPh + '" value="' + esc(maxVal) +
            '" data-edit-field="max" data-lt-id="' + esc(lt.id) + '" data-index="' + e.index + '"' +
            (isReadonly || r.removed ? ' disabled' : '') + '>' +
        '</div>' +
        '<div class="value">' +
        '  <label>Weight</label>' +
        '  <input type="number" class="num" placeholder="' + e.weight + '" value="' + esc(weightVal) +
            '" data-edit-field="weight" data-lt-id="' + esc(lt.id) + '" data-index="' + e.index + '"' +
            (isReadonly || r.removed ? ' disabled' : '') + '>' +
        '</div>' +
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
        return '<div class="placeholder-icon"><span>▦</span></div>' +
            '<div class="target subtable">' +
                '<b>' + esc(e.lootTableId) + '</b>' +
                '<small>(sub-table)</small>' +
            '</div>';
    }
    return '<div class="placeholder-icon">·</div>' +
        '<div class="target"><b>(no drop)</b><small></small></div>';
}

function buildLtAddedRowHtml(lt, addedEntry, addedIndex, isReadonly) {
    const a = addedEntry || {};
    const isItem    = !!(a.lootItem  && a.lootItem  !== 'None');
    const isTable   = !!(a.lootTable && a.lootTable !== 'None');
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
        return buildLtAddedFormHtml(lt, a, addedIndex, isReadonly);
    }

    return '<div class="lt-entry added" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '">' +
        targetHtml +
        '<div class="value">' +
          '<label>Minimum</label>' +
          '<input type="number" class="num" value="' + esc(a.min || 1) +
            '" data-added-field="min" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
            (isReadonly ? ' disabled' : '') + '>' +
        '</div>' +
        '<div class="value">' +
          '<label>Maximum</label>' +
          '<input type="number" class="num" value="' + esc(a.max || 1) +
            '" data-added-field="max" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
            (isReadonly ? ' disabled' : '') + '>' +
        '</div>' +
        '<div class="value">' +
          '<label>Weight</label>' +
          '<input type="number" class="num" value="' + esc(a.weight || 0) +
            '" data-added-field="weight" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
            (isReadonly ? ' disabled' : '') + '>' +
        '</div>' +
        '<div class="row-actions">' +
            '<button type="button" class="danger" data-delete-added="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>⨉</button>' +
        '</div>' +
    '</div>';
}

function buildLtAddedFormHtml(lt, a, addedIndex, isReadonly) {
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
            '<div class="value">' +
              '<label>Minimum</label>' +
              '<input type="number" class="num" value="' + esc(a.min || 1) +
                '" data-added-field="min" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>' +
            '</div>' +
            '<div class="value">' +
              '<label>Maximum</label>' +
              '<input type="number" class="num" value="' + esc(a.max || 1) +
                '" data-added-field="max" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>' +
            '</div>' +
            '<div class="value">' +
              '<label>Weight</label>' +
              '<input type="number" class="num" value="' + esc(a.weight || 0) +
                '" data-added-field="weight" data-lt-id="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                (isReadonly ? ' disabled' : '') + '>' +
            '</div>' +
            '<div class="row-actions">' +
                '<button type="button" class="danger" data-delete-added="' + esc(lt.id) + '" data-added-index="' + addedIndex + '"' +
                    (isReadonly ? ' disabled' : '') + '>⨉</button>' +
            '</div>' +
        '</div>' +
    '</div>';
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
