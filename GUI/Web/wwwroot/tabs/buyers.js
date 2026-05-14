'use strict';

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

function populateBuyerFactionFilter() {
    const sel = document.getElementById('buyers-filter-faction');
    if (!sel) return;
    const seen = new Set();
    for (const b of state.buyers.list) {
        if (b.faction) seen.add(b.faction);
    }
    const factions = Array.from(seen).sort();
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
        ? '<tr><td colspan="6" class="buyer-empty-row">(no entries)</td></tr>'
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
         +       '<th>Requirement</th>'
         +       '<th class="buyer-row-actions">&nbsp;</th>'
         +     '</tr></thead>'
         +     '<tbody>' + rows + '</tbody>'
         +   '</table>'
         +   '<div class="buyer-card-footer">'
         +     '<button class="buyer-add-btn primary" data-buyer-add="' + esc(b.id) + '">Add Entry</button>'
         +   '</div>'
         + '</li>';
}

function countEditedInBuyer(b) {
    if (!state.current || !state.current.buyerRecipes) return 0;
    const recipes = state.current.buyerRecipes;
    let n = 0;
    for (const e of (b.entries || [])) {
        if (e.recipeId && recipes[e.recipeId]) n++;
    }
    return n;
}

function buildBuyerEntryRowHtml(buyerId, e, removed) {
    if (!e.resolved) {
        return '<tr class="buyer-row unresolved">'
             +   '<td colspan="6" class="buyer-unresolved">'
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
    const requirement = (ovr && ovr.craftRequirement != null)
        ? ovr.craftRequirement
        : (e.craftRequirement || 'None');

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
         +   '<td class="buyer-req">' + buildRequirementSelectHtml(requirement, e.recipeId, disabledAttr) + '</td>'
         +   '<td class="buyer-row-actions">' + actionBtn + '</td>'
         + '</tr>';
}

function buildBuyerAddedRowHtml(buyerId, recipeId) {
    const ovr = (state.current && state.current.buyerRecipes
                 && state.current.buyerRecipes[recipeId]) || null;
    if (!ovr) {
        return '<tr class="buyer-row added orphan" data-recipe-id="' + esc(recipeId) + '">'
             +   '<td colspan="6" class="buyer-unresolved">'
             +     '<span class="buyer-recipe">' + esc(recipeId) + '</span>'
             +     ' <span class="hint">(added recipe has no edit-spec - profile corrupted)</span>'
             +   '</td>'
             + '</tr>';
    }
    const itemId    = ovr.itemPath    ? assetPathToId(ovr.itemPath)    : '';
    const payItemId = ovr.payItemPath ? assetPathToId(ovr.payItemPath) : '';
    const itemCount = ovr.itemCount != null ? ovr.itemCount : 0;
    const payCount  = ovr.payCount  != null ? ovr.payCount  : 0;
    const requirement = ovr.craftRequirement != null ? ovr.craftRequirement : 'None';
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
         +   '<td class="buyer-req">' + buildRequirementSelectHtml(requirement, recipeId, '') + '</td>'
         +   '<td class="buyer-row-actions">'
         +     '<button class="btn-link buyer-delete" data-buyer-delete-added="'
         +       esc(buyerId) + '|' + esc(recipeId) + '" title="Delete added entry">&#x2715;</button>'
         +   '</td>'
         + '</tr>';
}

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
            craftRequirement: vanillaEntry && vanillaEntry.craftRequirement
                ? vanillaEntry.craftRequirement
                : 'None',
            isCustom:    false,
        };
        state.current.buyerRecipes[recipeId] = ovr;
    }
    return ovr;
}

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

function setBuyerEntryField(buyerId, recipeId, field, rawValue) {
    if (!state.current) return;
    const buyer = state.buyers.list.find(b => b.id === buyerId);
    const vanilla = buyer && buyer.entries
        ? buyer.entries.find(e => e.recipeId === recipeId)
        : null;
    const ovr = getOrCreateBuyerRecipeOverride(recipeId, vanilla);
    if (!ovr) return;

    if (field === 'itemCount' || field === 'payCount') {
        const n = parseInt(rawValue, 10);
        if (!isFinite(n) || n < 0) return;
        ovr[field] = n;
    } else if (field === 'item' || field === 'payItem') {
        const id = (rawValue || '').trim();
        const targetField = field === 'item' ? 'itemPath' : 'payItemPath';
        if (!id) {
            ovr[targetField] = null;
        } else {
            const path = itemIdToAssetPath(id);
            if (!path) return;
            ovr[targetField] = path;
        }
    } else if (field === 'requirement') {
        ovr.craftRequirement = (rawValue || 'None');
    }

    markDirty();
}

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

function resetBuyerRecipeOverride(buyerId, recipeId) {
    if (!state.current || !state.current.buyerRecipes) return;
    delete state.current.buyerRecipes[recipeId];
    if (Object.keys(state.current.buyerRecipes).length === 0) {
        delete state.current.buyerRecipes;
    }
    markDirty();
    refreshBuyerCard(buyerId);
}

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
        craftRequirement: 'None',
        isCustom: true,
    };
    lo.addedRecipeIds.push(id);
    markDirty();
    refreshBuyerCard(buyerId);
}

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
        toggleRemoveBuyerEntry(buyerId, recipeId);
        return;
    }
    if (t.dataset.buyerReset) {
        const card = t.closest('.buyer-card');
        const buyerId = card && card.dataset.buyerId;
        if (buyerId) resetBuyerRecipeOverride(buyerId, t.dataset.buyerReset);
        return;
    }
}

function onBuyersListChange(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.buyerField) return;
    if (t.dataset.buyerPickerTarget) return;
    const recipeId = t.dataset.recipeId;
    const card = t.closest('.buyer-card');
    const buyerId = card && card.dataset.buyerId;
    if (!buyerId || !recipeId) return;
    setBuyerEntryField(buyerId, recipeId, t.dataset.buyerField, t.value);
    refreshBuyerCard(buyerId);
}

function onBuyersListFocusIn(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.buyerPickerTarget) return;
    if (t.disabled) return;
    const recipeId = t.dataset.recipeId;
    const field    = t.dataset.buyerField;
    const card     = t.closest('.buyer-card');
    const buyerId  = card && card.dataset.buyerId;
    if (!buyerId || !recipeId || !field) return;
    openBuyerPicker(t, buyerId, recipeId, field);
}

function onBuyersListInput(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.buyerPickerTarget) return;
    if (!state.picker || state.picker.input !== t) return;
    populatePicker(t.value);
    positionPicker(t);
}

function bindBuyersHandlers() {
    document.getElementById('buyers-filter').addEventListener('input',           renderBuyers);
    document.getElementById('buyers-filter-faction').addEventListener('change',  renderBuyers);

    const buyersList = document.getElementById('buyers-list');
    if (buyersList) {
        buyersList.addEventListener('click',  onBuyersListClick);
        buyersList.addEventListener('change', onBuyersListChange);
        buyersList.addEventListener('focusin', onBuyersListFocusIn);
        buyersList.addEventListener('input',  onBuyersListInput);
        buyersList.addEventListener('scroll', () => {
            if (state.picker) positionPicker(state.picker.input);
        }, { passive: true });
    }
}
