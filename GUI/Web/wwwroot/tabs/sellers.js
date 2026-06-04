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
    sel.replaceChildren(new Option('All factions', ''));
    for (const f of factions) sel.appendChild(new Option(f, f));
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
        const li = document.createElement('li');
        li.className = 'buyers-empty';
        li.textContent = msg;
        list.replaceChildren(li);
    } else {
        const frag = document.createDocumentFragment();
        for (const s of filtered) frag.appendChild(buildSellerCardNode(s));
        list.replaceChildren(frag);
    }

    document.getElementById('sellers-count').textContent =
        filtered.length + ' / ' + state.sellers.list.length + ' lists';
}

function buildSellerCardNode(s) {
    const listOvr = (state.current && state.current.sellerLists
                     && state.current.sellerLists[s.id]) || null;
    const removedSet = listOvr && listOvr.removedRecipeIds
        ? new Set(listOvr.removedRecipeIds)
        : new Set();
    const addedIds = (listOvr && listOvr.addedRecipeIds) || [];
    const recipeOrder = listOvr && listOvr.recipeOrder;

    const entries = s.entries || [];
    const rows = [];

    if (recipeOrder) {
        const vanillaMap = new Map(entries.map(e => [e.recipeId, e]));
        for (const id of recipeOrder) {
            const ve = vanillaMap.get(id);
            if (ve) rows.push(buildSellerEntryRowNode(s.id, ve, false));
            else if (id.startsWith('QM_SCustom_')) rows.push(buildSellerAddedRowNode(s.id, id));
        }
        for (const e of entries) {
            if (removedSet.has(e.recipeId)) rows.push(buildSellerEntryRowNode(s.id, e, true));
        }
    } else {
        for (const e of entries) rows.push(buildSellerEntryRowNode(s.id, e, removedSet.has(e.recipeId)));
        for (const id of addedIds) rows.push(buildSellerAddedRowNode(s.id, id));
    }

    const card = cloneTemplate('tpl-seller-card');
    card.dataset.sellerId = s.id;
    card.querySelector('.buyer-faction').textContent = s.faction || '(other)';
    card.querySelector('.buyer-label').textContent = s.label || s.id;
    card.querySelector('.buyer-sub').textContent =
        s.id + (s.entries ? '  -  ' + s.entries.length + ' entries' : '');

    const editedCount  = countEditedInSeller(s);
    const removedCount = removedSet.size;
    const addedCount   = addedIds.length;
    const badge = card.querySelector('.buyer-change-badge');
    if (editedCount + removedCount + addedCount > 0) {
        if (editedCount)  badge.appendChild(sellerBadge('edited',  editedCount  + ' edited'));
        if (removedCount) badge.appendChild(sellerBadge('removed', removedCount + ' removed'));
        if (addedCount)   badge.appendChild(sellerBadge('added',   addedCount   + ' added'));
        badge.hidden = false;
    }

    const tbody = card.querySelector('tbody');
    if (rows.length === 0) {
        const tr = document.createElement('tr');
        const td = document.createElement('td');
        td.colSpan = 6;
        td.className = 'buyer-empty-row';
        td.textContent = '(no entries)';
        tr.appendChild(td);
        tbody.appendChild(tr);
    } else {
        const frag = document.createDocumentFragment();
        for (const r of rows) frag.appendChild(r);
        tbody.appendChild(frag);
    }

    card.querySelector('.buyer-add-btn').dataset.sellerAdd = s.id;
    return card;
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

function sellerBadge(kind, text) {
    const span = document.createElement('span');
    span.className = 'badge ' + kind;
    span.textContent = text;
    return span;
}

function sellerActionBtn(extraClass, dataKey, dataVal, title, glyph) {
    const btn = document.createElement('button');
    btn.className = 'btn-link ' + extraClass;
    btn.dataset[dataKey] = dataVal;
    btn.title = title;
    btn.textContent = glyph;
    return btn;
}

function appendSellerMoveBtns(parent, sellerId, recipeId) {
    parent.appendChild(sellerActionBtn(
        'buyer-move', 'sellerMove', sellerId + '|' + recipeId + '|-1', 'Move up', '▲'));
    parent.appendChild(sellerActionBtn(
        'buyer-move', 'sellerMove', sellerId + '|' + recipeId + '|1', 'Move down', '▼'));
}

function configureSellerItemCell(td, recipeId, itemId, disabled) {
    const item = itemId ? state.itemsById.get(itemId) : null;
    const name = (item && item.meta && item.meta.name) || itemId || '(no item)';
    if (item && item.icon) {
        const img = document.createElement('img');
        img.className = 'buyer-icon';
        img.src = item.icon;
        img.alt = '';
        img.loading = 'lazy';
        td.querySelector('.buyer-icon').replaceWith(img);
    }
    td.querySelector('.buyer-item-name').textContent = name;
    const input = td.querySelector('.buyer-item-input');
    input.value = itemId || '';
    input.dataset.recipeId = recipeId;
    if (disabled) input.disabled = true;
}

function setSellerNumInput(input, recipeId, value, disabled) {
    input.value = String(value);
    input.dataset.recipeId = recipeId;
    if (disabled) input.disabled = true;
}

function buildSellerRowNode(recipeId, rowClass, itemId, itemCount, payItemId, payCount, requirement, disabled) {
    const tr = cloneTemplate('tpl-seller-row');
    tr.className = rowClass;
    tr.dataset.recipeId = recipeId;

    configureSellerItemCell(tr.querySelector('[data-cell="item"]'),    recipeId, itemId,    disabled);
    configureSellerItemCell(tr.querySelector('[data-cell="payItem"]'), recipeId, payItemId, disabled);

    const nums = tr.querySelectorAll('.buyer-num-input');
    setSellerNumInput(nums[0], recipeId, itemCount, disabled);
    setSellerNumInput(nums[1], recipeId, payCount,  disabled);

    // Select markup comes from the requirement builder (single option-list source).
    tr.querySelector('.buyer-req').innerHTML =
        buildSellerRequirementSelectHtml(requirement, recipeId, disabled ? ' disabled' : '');
    return tr;
}

function buildSellerUnresolvedRow(rowClass, recipeId, recipeText, hintText) {
    const tr = cloneTemplate('tpl-seller-unresolved-row');
    tr.className = rowClass;
    if (recipeId != null) tr.dataset.recipeId = recipeId;
    tr.querySelector('.buyer-recipe').textContent = recipeText;
    tr.querySelector('.hint').textContent = hintText;
    return tr;
}

function buildSellerEntryRowNode(sellerId, e, removed) {
    if (!e.resolved) {
        return buildSellerUnresolvedRow('buyer-row unresolved', null,
            e.recipeId || '(unknown)', '(recipe not found in vanilla extract)');
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

    const tr = buildSellerRowNode(e.recipeId, rowClass, itemId, itemCount, payItemId, payCount, requirement, removed);
    const actions = tr.querySelector('.buyer-row-actions');
    if (removed) {
        actions.appendChild(sellerActionBtn(
            'buyer-restore', 'sellerRestore', sellerId + '|' + e.recipeId, 'Restore', '↺'));
    } else {
        appendSellerMoveBtns(actions, sellerId, e.recipeId);
        if (ovr) {
            actions.appendChild(sellerActionBtn(
                'buyer-reset', 'sellerReset', e.recipeId, 'Reset to vanilla', '↶'));
        }
        actions.appendChild(sellerActionBtn(
            'buyer-delete', 'sellerDelete', sellerId + '|' + e.recipeId, 'Remove from list', '✕'));
    }
    return tr;
}

function buildSellerAddedRowNode(sellerId, recipeId) {
    const ovr = (state.current && state.current.sellerRecipes
                 && state.current.sellerRecipes[recipeId]) || null;
    if (!ovr) {
        return buildSellerUnresolvedRow('buyer-row added orphan', recipeId, recipeId,
            '(added recipe has no edit-spec - profile corrupted)');
    }
    const itemId    = ovr.itemPath    ? assetPathToId(ovr.itemPath)    : '';
    const payItemId = ovr.payItemPath ? assetPathToId(ovr.payItemPath) : '';
    const itemCount = ovr.itemCount != null ? ovr.itemCount : 0;
    const payCount  = ovr.payCount  != null ? ovr.payCount  : 0;
    const requirement = ovr.craftRequirement != null ? ovr.craftRequirement : 'None';

    const tr = buildSellerRowNode(recipeId, 'buyer-row added', itemId, itemCount, payItemId, payCount, requirement, false);
    const actions = tr.querySelector('.buyer-row-actions');
    appendSellerMoveBtns(actions, sellerId, recipeId);
    actions.appendChild(sellerActionBtn(
        'buyer-delete', 'sellerDeleteAdded', sellerId + '|' + recipeId, 'Delete added entry', '✕'));
    return tr;
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
    old.replaceWith(buildSellerCardNode(seller));
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
    // A recipeOrder that matches the natural (vanilla minus removed, plus added)
    // order is a no-op -> drop it so reordering back to vanilla clears the override.
    if (Array.isArray(lo.recipeOrder)) {
        const seller = state.sellers.list.find(s => s.id === sellerId);
        if (seller) {
            const removedSet = new Set(lo.removedRecipeIds || []);
            const defaultOrder = (seller.entries || [])
                .filter(e => e.resolved && !removedSet.has(e.recipeId))
                .map(e => e.recipeId)
                .concat(lo.addedRecipeIds || []);
            if (lo.recipeOrder.length === defaultOrder.length
                && lo.recipeOrder.every((id, i) => id === defaultOrder[i])) {
                delete lo.recipeOrder;
            }
        }
    }
    const emptyAdd   = !lo.addedRecipeIds   || lo.addedRecipeIds.length   === 0;
    const emptyRem   = !lo.removedRecipeIds || lo.removedRecipeIds.length === 0;
    const hasOrder   = Array.isArray(lo.recipeOrder);
    if (emptyAdd && emptyRem && !hasOrder) {
        delete state.current.sellerLists[sellerId];
    }
    if (state.current.sellerLists && Object.keys(state.current.sellerLists).length === 0) {
        delete state.current.sellerLists;
    }
}

function materializeSellerRecipeOrder(sellerId) {
    const seller = state.sellers.list.find(s => s.id === sellerId);
    if (!seller) return null;
    const lo = getOrCreateSellerListOverride(sellerId);
    if (!lo) return null;
    if (!lo.recipeOrder) {
        const removedSet = new Set(lo.removedRecipeIds || []);
        const vanillaIds = (seller.entries || [])
            .filter(e => e.resolved && !removedSet.has(e.recipeId))
            .map(e => e.recipeId);
        lo.recipeOrder = vanillaIds.concat(lo.addedRecipeIds || []);
    }
    return lo;
}

function moveSellerEntry(sellerId, recipeId, delta) {
    const lo = materializeSellerRecipeOrder(sellerId);
    if (!lo || !lo.recipeOrder) return;
    const arr = lo.recipeOrder;
    const idx = arr.indexOf(recipeId);
    if (idx < 0) return;
    const newIdx = idx + delta;
    if (newIdx < 0 || newIdx >= arr.length) return;
    arr.splice(idx, 1);
    arr.splice(newIdx, 0, recipeId);
    pruneSellerListOverride(sellerId);
    markDirty();
    refreshSellerCard(sellerId);
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
        if (lo.recipeOrder && !lo.recipeOrder.includes(recipeId)) {
            lo.recipeOrder.push(recipeId);
        }
    } else {
        lo.removedRecipeIds.push(recipeId);
        if (lo.recipeOrder) {
            const orderIdx = lo.recipeOrder.indexOf(recipeId);
            if (orderIdx >= 0) lo.recipeOrder.splice(orderIdx, 1);
        }
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
    if (lo && lo.recipeOrder) {
        const orderIdx = lo.recipeOrder.indexOf(recipeId);
        if (orderIdx >= 0) lo.recipeOrder.splice(orderIdx, 1);
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
    if (lo.recipeOrder) lo.recipeOrder.push(id);
    markDirty();
    refreshSellerCard(sellerId);
}

function onSellersListClick(e) {
    const t = e.target.closest && e.target.closest(
        '[data-seller-add],[data-seller-delete],[data-seller-delete-added],'
        + '[data-seller-restore],[data-seller-reset],[data-seller-move]');
    if (!t) return;
    if (t.dataset.sellerAdd) {
        addSellerEntry(t.dataset.sellerAdd);
        return;
    }
    if (t.dataset.sellerMove) {
        const [sellerId, recipeId, deltaStr] = t.dataset.sellerMove.split('|');
        moveSellerEntry(sellerId, recipeId, parseInt(deltaStr, 10));
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
