async function loadSellers() {
    try {
        const data = await api('GET', '/api/sellers');
        state.sellers.loaded = true;
        state.sellers.list = data || [];
        state.sellers.error = null;
        populateSellerFactionFilter();
    } catch (e) {
        state.sellers.loaded = true;
        state.sellers.list = [];
        state.sellers.error = 'Failed to load sellers: ' + e.message;
    }
    renderSellers();
    renderSellersStatus();
}

function populateSellerFactionFilter() {
    const sel = document.getElementById('sellers-filter-faction');
    if (!sel) return;
    const seen = new Set();
    for (const s of state.sellers.list) {
        if (s.faction) seen.add(s.faction);
    }
    const factions = Array.from(seen).sort();
    const prev = sel.value;
    sel.innerHTML = '<option value="">All factions</option>'
        + factions.map(f => '<option value="' + esc(f) + '">' + esc(f) + '</option>').join('');
    if (prev && factions.includes(prev)) sel.value = prev;
}

function filterSellers() {
    const q = (document.getElementById('sellers-filter').value || '').trim().toLowerCase();
    const faction = document.getElementById('sellers-filter-faction').value;
    const out = [];
    for (const s of state.sellers.list) {
        if (faction && s.faction !== faction) continue;
        if (q) {
            let hay = (s.label + ' ' + s.id + ' ' + s.faction).toLowerCase();
            for (const e of s.entries) {
                hay += ' ' + (e.itemId || '') + ' ' + (e.recipeId || '');
            }
            if (!hay.includes(q)) continue;
        }
        out.push(s);
    }
    return out;
}

function renderSellers() {
    const errEl = document.getElementById('sellers-error');
    if (state.sellers.error) {
        errEl.textContent = state.sellers.error;
        errEl.hidden = false;
    } else {
        errEl.hidden = true;
    }

    const list = document.getElementById('sellers-list');
    const filtered = filterSellers();

    if (filtered.length === 0) {
        const msg = state.sellers.list.length === 0
            ? 'No PlayerBuys RecipeLists in vanilla yet. Re-run setup to extract them.'
            : 'No sellers match the current filter.';
        list.innerHTML = '<li class="buyers-empty">' + esc(msg) + '</li>';
    } else {
        list.innerHTML = filtered.map(buildSellerCardHtml).join('');
    }

    document.getElementById('sellers-count').textContent =
        filtered.length + ' / ' + state.sellers.list.length + ' lists';
}

function buildSellerCardHtml(s) {
    const listOvr = (state.current && state.current.sellerLists
                     && state.current.sellerLists[s.id]) || null;
    const removedSet = listOvr && listOvr.removedRecipeIds
        ? new Set(listOvr.removedRecipeIds)
        : new Set();
    const addedIds = (listOvr && listOvr.addedRecipeIds) || [];

    const entries = s.entries || [];
    const vanillaRows = entries.map(e => buildSellerEntryRowHtml(s.id, e, removedSet.has(e.recipeId)));
    const addedRows = addedIds.map(id => buildSellerAddedRowHtml(s.id, id));
    const allRows = vanillaRows.concat(addedRows);

    const rows = allRows.length === 0
        ? '<tr><td colspan="6" class="buyer-empty-row">(no entries)</td></tr>'
        : allRows.join('');

    const editedCount = countEditedInSeller(s);
    const removedCount = removedSet.size;
    const addedCount = addedIds.length;
    const changeBadge = (editedCount + removedCount + addedCount) === 0
        ? ''
        : ' <span class="buyer-change-badge">'
        +   (editedCount  ? '<span class="badge edited">'  + editedCount  + ' edited</span>'  : '')
        +   (removedCount ? '<span class="badge removed">' + removedCount + ' removed</span>' : '')
        +   (addedCount   ? '<span class="badge added">'   + addedCount   + ' added</span>'   : '')
        + '</span>';

    const sub = s.id + (s.entries ? '  -  ' + s.entries.length + ' entries' : '');
    return '<li class="buyer-card" data-seller-id="' + esc(s.id) + '">'
         +   '<header class="buyer-header">'
         +     '<div class="buyer-title">'
         +       '<span class="buyer-faction">' + esc(s.faction || '(other)') + '</span>'
         +       '<span class="buyer-label">' + esc(s.label || s.id) + '</span>'
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
         +     '<button class="buyer-add-btn primary" data-seller-add="' + esc(s.id) + '">Add Entry</button>'
         +   '</div>'
         + '</li>';
}

function countEditedInSeller(s) {
    if (!state.current || !state.current.sellerRecipes) return 0;
    const recipes = state.current.sellerRecipes;
    let n = 0;
    for (const e of (s.entries || [])) {
        if (e.recipeId && recipes[e.recipeId]) n++;
    }
    return n;
}

function buildSellerEntryRowHtml(sellerId, e, removed) {
    if (!e.resolved) {
        return '<tr class="buyer-row unresolved">'
             +   '<td colspan="6" class="buyer-unresolved">'
             +     '<span class="buyer-recipe">' + esc(e.recipeId || '(unknown)') + '</span>'
             +     ' <span class="hint">(recipe not found in vanilla extract)</span>'
             +   '</td>'
             + '</tr>';
    }

    const ovr = (state.current && state.current.sellerRecipes
                 && state.current.sellerRecipes[e.recipeId]) || null;
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
        ? '<button class="btn-link buyer-restore" data-seller-restore="' + esc(sellerId)
            + '|' + esc(e.recipeId) + '" title="Restore">&#x21BA;</button>'
        : (ovr
            ? '<button class="btn-link buyer-reset" data-seller-reset="' + esc(e.recipeId)
                + '" title="Reset to vanilla">&#x21B6;</button>'
                + '<button class="btn-link buyer-delete" data-seller-delete="' + esc(sellerId)
                + '|' + esc(e.recipeId) + '" title="Remove from list">&#x2715;</button>'
            : '<button class="btn-link buyer-delete" data-seller-delete="' + esc(sellerId)
                + '|' + esc(e.recipeId) + '" title="Remove from list">&#x2715;</button>');

    const disabledAttr = removed ? ' disabled' : '';
    return '<tr class="' + rowClass + '" data-recipe-id="' + esc(e.recipeId) + '">'
         +   buildSellerEditableItemCellHtml(e.recipeId, 'item', itemId, disabledAttr)
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(itemCount)) + '"'
         +       ' data-seller-field="itemCount" data-recipe-id="' + esc(e.recipeId) + '"'
         +       disabledAttr + '>'
         +   '</td>'
         +   buildSellerEditableItemCellHtml(e.recipeId, 'payItem', payItemId, disabledAttr)
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(payCount)) + '"'
         +       ' data-seller-field="payCount" data-recipe-id="' + esc(e.recipeId) + '"'
         +       disabledAttr + '>'
         +   '</td>'
         +   '<td class="buyer-req">' + buildSellerRequirementSelectHtml(requirement, e.recipeId, disabledAttr) + '</td>'
         +   '<td class="buyer-row-actions">' + actionBtn + '</td>'
         + '</tr>';
}

function buildSellerAddedRowHtml(sellerId, recipeId) {
    const ovr = (state.current && state.current.sellerRecipes
                 && state.current.sellerRecipes[recipeId]) || null;
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
         +   buildSellerEditableItemCellHtml(recipeId, 'item', itemId, '')
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(itemCount)) + '"'
         +       ' data-seller-field="itemCount" data-recipe-id="' + esc(recipeId) + '">'
         +   '</td>'
         +   buildSellerEditableItemCellHtml(recipeId, 'payItem', payItemId, '')
         +   '<td class="num">'
         +     '<input type="number" class="buyer-num-input" min="0"'
         +       ' value="' + esc(String(payCount)) + '"'
         +       ' data-seller-field="payCount" data-recipe-id="' + esc(recipeId) + '">'
         +   '</td>'
         +   '<td class="buyer-req">' + buildSellerRequirementSelectHtml(requirement, recipeId, '') + '</td>'
         +   '<td class="buyer-row-actions">'
         +     '<button class="btn-link buyer-delete" data-seller-delete-added="'
         +       esc(sellerId) + '|' + esc(recipeId) + '" title="Delete added entry">&#x2715;</button>'
         +   '</td>'
         + '</tr>';
}

function buildSellerEditableItemCellHtml(recipeId, field, itemId, disabledAttr) {
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
         +       ' data-seller-picker-target="1"'
         +       ' data-seller-field="' + esc(field) + '"'
         +       ' data-recipe-id="' + esc(recipeId) + '"'
         +       ' placeholder="Search items by name or id..."'
         +       ' autocomplete="off"'
         +       disabledAttr + '>'
         +   '</div>'
         + '</td>';
}

function buildSellerRequirementSelectHtml(currentValue, recipeId, disabledAttr) {
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
         +    ' data-seller-field="requirement"'
         +    ' data-recipe-id="' + esc(recipeId) + '"'
         +    disabledAttr + '>'
         + html
         + '</select>';
}

function refreshSellerCard(sellerId) {
    const list = document.getElementById('sellers-list');
    const old = list && list.querySelector('.buyer-card[data-seller-id="' + cssEsc(sellerId) + '"]');
    if (!old) return;
    const seller = state.sellers.list.find(s => s.id === sellerId);
    if (!seller) return;
    const wrap = document.createElement('div');
    wrap.innerHTML = buildSellerCardHtml(seller);
    const fresh = wrap.firstElementChild;
    if (fresh) old.replaceWith(fresh);
}

function getOrCreateSellerRecipeOverride(recipeId, vanillaEntry) {
    if (!state.current) return null;
    state.current.sellerRecipes = state.current.sellerRecipes || {};
    let ovr = state.current.sellerRecipes[recipeId];
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
        state.current.sellerRecipes[recipeId] = ovr;
    }
    return ovr;
}

function getOrCreateSellerListOverride(sellerId) {
    if (!state.current) return null;
    state.current.sellerLists = state.current.sellerLists || {};
    let lo = state.current.sellerLists[sellerId];
    if (!lo) {
        lo = { addedRecipeIds: [], removedRecipeIds: [] };
        state.current.sellerLists[sellerId] = lo;
    }
    if (!lo.addedRecipeIds)   lo.addedRecipeIds = [];
    if (!lo.removedRecipeIds) lo.removedRecipeIds = [];
    return lo;
}

function pruneSellerListOverride(sellerId) {
    if (!state.current || !state.current.sellerLists) return;
    const lo = state.current.sellerLists[sellerId];
    if (!lo) return;
    const emptyAdd = !lo.addedRecipeIds || lo.addedRecipeIds.length === 0;
    const emptyRem = !lo.removedRecipeIds || lo.removedRecipeIds.length === 0;
    if (emptyAdd && emptyRem) {
        delete state.current.sellerLists[sellerId];
    }
    if (Object.keys(state.current.sellerLists).length === 0) {
        delete state.current.sellerLists;
    }
}

function setSellerEntryField(sellerId, recipeId, field, rawValue) {
    if (!state.current) return;
    const seller = state.sellers.list.find(s => s.id === sellerId);
    const vanilla = seller && seller.entries
        ? seller.entries.find(e => e.recipeId === recipeId)
        : null;
    const ovr = getOrCreateSellerRecipeOverride(recipeId, vanilla);
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

function toggleRemoveSellerEntry(sellerId, recipeId) {
    const lo = getOrCreateSellerListOverride(sellerId);
    if (!lo) return;
    const idx = lo.removedRecipeIds.indexOf(recipeId);
    if (idx >= 0) {
        lo.removedRecipeIds.splice(idx, 1);
    } else {
        lo.removedRecipeIds.push(recipeId);
    }
    pruneSellerListOverride(sellerId);
    markDirty();
    refreshSellerCard(sellerId);
}

function resetSellerRecipeOverride(sellerId, recipeId) {
    if (!state.current || !state.current.sellerRecipes) return;
    delete state.current.sellerRecipes[recipeId];
    if (Object.keys(state.current.sellerRecipes).length === 0) {
        delete state.current.sellerRecipes;
    }
    markDirty();
    refreshSellerCard(sellerId);
}

function removeAddedSellerEntry(sellerId, recipeId) {
    const lo = state.current && state.current.sellerLists
        && state.current.sellerLists[sellerId];
    if (lo && lo.addedRecipeIds) {
        const idx = lo.addedRecipeIds.indexOf(recipeId);
        if (idx >= 0) lo.addedRecipeIds.splice(idx, 1);
    }
    pruneSellerListOverride(sellerId);
    if (state.current && state.current.sellerRecipes) {
        delete state.current.sellerRecipes[recipeId];
        if (Object.keys(state.current.sellerRecipes).length === 0) {
            delete state.current.sellerRecipes;
        }
    }
    markDirty();
    refreshSellerCard(sellerId);
}

function addSellerEntry(sellerId) {
    if (!state.current) return;
    const lo = getOrCreateSellerListOverride(sellerId);
    if (!lo) return;
    const id = 'QM_SCustom_' + randomHex(8);
    state.current.sellerRecipes = state.current.sellerRecipes || {};
    state.current.sellerRecipes[id] = {
        itemPath: null,
        itemCount: 1,
        payItemPath: null,
        payCount: 1,
        craftRequirement: 'None',
        isCustom: true,
    };
    lo.addedRecipeIds.push(id);
    markDirty();
    refreshSellerCard(sellerId);
}

function onSellersListClick(e) {
    const t = e.target.closest && e.target.closest(
        '[data-seller-add],[data-seller-delete],[data-seller-delete-added],'
        + '[data-seller-restore],[data-seller-reset]');
    if (!t) return;
    if (t.dataset.sellerAdd) {
        addSellerEntry(t.dataset.sellerAdd);
        return;
    }
    if (t.dataset.sellerDelete) {
        const [sellerId, recipeId] = t.dataset.sellerDelete.split('|');
        toggleRemoveSellerEntry(sellerId, recipeId);
        return;
    }
    if (t.dataset.sellerDeleteAdded) {
        const [sellerId, recipeId] = t.dataset.sellerDeleteAdded.split('|');
        removeAddedSellerEntry(sellerId, recipeId);
        return;
    }
    if (t.dataset.sellerRestore) {
        const [sellerId, recipeId] = t.dataset.sellerRestore.split('|');
        toggleRemoveSellerEntry(sellerId, recipeId);
        return;
    }
    if (t.dataset.sellerReset) {
        const card = t.closest('.buyer-card');
        const sellerId = card && card.dataset.sellerId;
        if (sellerId) resetSellerRecipeOverride(sellerId, t.dataset.sellerReset);
        return;
    }
}

function onSellersListChange(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.sellerField) return;
    if (t.dataset.sellerPickerTarget) return;
    const recipeId = t.dataset.recipeId;
    const card = t.closest('.buyer-card');
    const sellerId = card && card.dataset.sellerId;
    if (!sellerId || !recipeId) return;
    setSellerEntryField(sellerId, recipeId, t.dataset.sellerField, t.value);
    refreshSellerCard(sellerId);
}

function onSellersListFocusIn(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.sellerPickerTarget) return;
    if (t.disabled) return;
    const recipeId = t.dataset.recipeId;
    const field    = t.dataset.sellerField;
    const card     = t.closest('.buyer-card');
    const sellerId = card && card.dataset.sellerId;
    if (!sellerId || !recipeId || !field) return;
    openSellerPicker(t, sellerId, recipeId, field);
}

function onSellersListInput(e) {
    const t = e.target;
    if (!t || !t.dataset || !t.dataset.sellerPickerTarget) return;
    if (!state.picker || state.picker.input !== t) return;
    populatePicker(t.value);
    positionPicker(t);
}

function openSellerPicker(input, sellerId, recipeId, sellerField) {
    closePicker();
    state.picker = { input, source: 'seller', type: 'item', sellerId, recipeId, sellerField };
    populatePicker('');
    document.getElementById('picker-dropdown').hidden = false;
    positionPicker(input);
    if (input.value) {
        try { input.select(); } catch (_) { /* ignore */ }
    }
}

function renderSellersStatus() {
    const total = state.sellers.list.length;
    let entries = 0;
    for (const s of state.sellers.list) {
        entries += (s.entries ? s.entries.length : 0);
    }
    document.getElementById('sellers-stat-total').textContent   = total;
    document.getElementById('sellers-stat-entries').textContent = entries;
}

function bindSellersHandlers() {
    document.getElementById('sellers-filter').addEventListener('input',          renderSellers);
    document.getElementById('sellers-filter-faction').addEventListener('change', renderSellers);

    const sellersList = document.getElementById('sellers-list');
    if (sellersList) {
        sellersList.addEventListener('click',   onSellersListClick);
        sellersList.addEventListener('change',  onSellersListChange);
        sellersList.addEventListener('focusin', onSellersListFocusIn);
        sellersList.addEventListener('input',   onSellersListInput);
        sellersList.addEventListener('scroll', () => {
            if (state.picker) positionPicker(state.picker.input);
        }, { passive: true });
    }
}
