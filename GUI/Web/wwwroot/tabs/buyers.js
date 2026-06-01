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
    sel.replaceChildren(new Option('All factions', ''));
    for (const f of factions) sel.appendChild(new Option(f, f));
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

function cloneTemplate(id) {
    const tpl = document.getElementById(id);
    if (!tpl) throw new Error('template #' + id + ' missing');
    return tpl.content.firstElementChild.cloneNode(true);
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
        const li = document.createElement('li');
        li.className = 'buyers-empty';
        li.textContent = msg;
        list.replaceChildren(li);
    } else {
        const frag = document.createDocumentFragment();
        for (const b of filtered) frag.appendChild(buildBuyerCardNode(b));
        list.replaceChildren(frag);
    }

    document.getElementById('buyers-count').textContent =
        filtered.length + ' / ' + state.buyers.list.length + ' lists';
}

function buildBuyerCardNode(b) {
    const listOvr = (state.current && state.current.buyerLists
                     && state.current.buyerLists[b.id]) || null;
    const removedSet = listOvr && listOvr.removedRecipeIds
        ? new Set(listOvr.removedRecipeIds)
        : new Set();
    const addedIds = (listOvr && listOvr.addedRecipeIds) || [];
    const recipeOrder = listOvr && listOvr.recipeOrder;

    const entries = b.entries || [];
    const rows = [];

    if (recipeOrder) {
        const vanillaMap = new Map(entries.map(e => [e.recipeId, e]));
        for (const id of recipeOrder) {
            const ve = vanillaMap.get(id);
            if (ve) rows.push(buildBuyerEntryRowNode(b.id, ve, false));
            else if (id.startsWith('QM_Custom_')) rows.push(buildBuyerAddedRowNode(b.id, id));
        }
        for (const e of entries) {
            if (removedSet.has(e.recipeId)) rows.push(buildBuyerEntryRowNode(b.id, e, true));
        }
    } else {
        for (const e of entries) rows.push(buildBuyerEntryRowNode(b.id, e, removedSet.has(e.recipeId)));
        for (const id of addedIds) rows.push(buildBuyerAddedRowNode(b.id, id));
    }

    const card = cloneTemplate('tpl-buyer-card');
    card.dataset.buyerId = b.id;
    card.querySelector('.buyer-faction').textContent = b.faction || '(other)';
    card.querySelector('.buyer-label').textContent = b.label || b.id;
    card.querySelector('.buyer-sub').textContent =
        b.id + (b.entries ? '  -  ' + b.entries.length + ' entries' : '');

    const editedCount  = countEditedInBuyer(b);
    const removedCount = removedSet.size;
    const addedCount   = addedIds.length;
    const badge = card.querySelector('.buyer-change-badge');
    if (editedCount + removedCount + addedCount > 0) {
        if (editedCount)  badge.appendChild(buyerBadge('edited',  editedCount  + ' edited'));
        if (removedCount) badge.appendChild(buyerBadge('removed', removedCount + ' removed'));
        if (addedCount)   badge.appendChild(buyerBadge('added',   addedCount   + ' added'));
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

    card.querySelector('.buyer-add-btn').dataset.buyerAdd = b.id;
    return card;
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

function buyerBadge(kind, text) {
    const span = document.createElement('span');
    span.className = 'badge ' + kind;
    span.textContent = text;
    return span;
}

function buyerActionBtn(extraClass, dataKey, dataVal, title, glyph) {
    const btn = document.createElement('button');
    btn.className = 'btn-link ' + extraClass;
    btn.dataset[dataKey] = dataVal;
    btn.title = title;
    btn.textContent = glyph;
    return btn;
}

function appendBuyerMoveBtns(parent, buyerId, recipeId) {
    parent.appendChild(buyerActionBtn(
        'buyer-move', 'buyerMove', buyerId + '|' + recipeId + '|-1', 'Move up', '▲'));
    parent.appendChild(buyerActionBtn(
        'buyer-move', 'buyerMove', buyerId + '|' + recipeId + '|1', 'Move down', '▼'));
}

function configureBuyerItemCell(td, recipeId, itemId, disabled) {
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

function setBuyerNumInput(input, recipeId, value, disabled) {
    input.value = String(value);
    input.dataset.recipeId = recipeId;
    if (disabled) input.disabled = true;
}

function buildBuyerRowNode(recipeId, rowClass, itemId, itemCount, payItemId, payCount, requirement, disabled) {
    const tr = cloneTemplate('tpl-buyer-row');
    tr.className = rowClass;
    tr.dataset.recipeId = recipeId;

    configureBuyerItemCell(tr.querySelector('[data-cell="item"]'),    recipeId, itemId,    disabled);
    configureBuyerItemCell(tr.querySelector('[data-cell="payItem"]'), recipeId, payItemId, disabled);

    const nums = tr.querySelectorAll('.buyer-num-input');
    setBuyerNumInput(nums[0], recipeId, itemCount, disabled);
    setBuyerNumInput(nums[1], recipeId, payCount,  disabled);

    // Sourced from the shared builder so the option list has one source of truth.
    tr.querySelector('.buyer-req').innerHTML =
        buildRequirementSelectHtml(requirement, recipeId, disabled ? ' disabled' : '');
    return tr;
}

function buildBuyerUnresolvedRow(rowClass, recipeId, recipeText, hintText) {
    const tr = cloneTemplate('tpl-buyer-unresolved-row');
    tr.className = rowClass;
    if (recipeId != null) tr.dataset.recipeId = recipeId;
    tr.querySelector('.buyer-recipe').textContent = recipeText;
    tr.querySelector('.hint').textContent = hintText;
    return tr;
}

function buildBuyerEntryRowNode(buyerId, e, removed) {
    if (!e.resolved) {
        return buildBuyerUnresolvedRow('buyer-row unresolved', null,
            e.recipeId || '(unknown)', '(recipe not found in vanilla extract)');
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

    const tr = buildBuyerRowNode(e.recipeId, rowClass, itemId, itemCount, payItemId, payCount, requirement, removed);
    const actions = tr.querySelector('.buyer-row-actions');
    if (removed) {
        actions.appendChild(buyerActionBtn(
            'buyer-restore', 'buyerRestore', buyerId + '|' + e.recipeId, 'Restore', '↺'));
    } else {
        appendBuyerMoveBtns(actions, buyerId, e.recipeId);
        if (ovr) {
            actions.appendChild(buyerActionBtn(
                'buyer-reset', 'buyerReset', e.recipeId, 'Reset to vanilla', '↶'));
        }
        actions.appendChild(buyerActionBtn(
            'buyer-delete', 'buyerDelete', buyerId + '|' + e.recipeId, 'Remove from list', '✕'));
    }
    return tr;
}

function buildBuyerAddedRowNode(buyerId, recipeId) {
    const ovr = (state.current && state.current.buyerRecipes
                 && state.current.buyerRecipes[recipeId]) || null;
    if (!ovr) {
        return buildBuyerUnresolvedRow('buyer-row added orphan', recipeId, recipeId,
            '(added recipe has no edit-spec - profile corrupted)');
    }
    const itemId    = ovr.itemPath    ? assetPathToId(ovr.itemPath)    : '';
    const payItemId = ovr.payItemPath ? assetPathToId(ovr.payItemPath) : '';
    const itemCount = ovr.itemCount != null ? ovr.itemCount : 0;
    const payCount  = ovr.payCount  != null ? ovr.payCount  : 0;
    const requirement = ovr.craftRequirement != null ? ovr.craftRequirement : 'None';

    const tr = buildBuyerRowNode(recipeId, 'buyer-row added', itemId, itemCount, payItemId, payCount, requirement, false);
    const actions = tr.querySelector('.buyer-row-actions');
    appendBuyerMoveBtns(actions, buyerId, recipeId);
    actions.appendChild(buyerActionBtn(
        'buyer-delete', 'buyerDeleteAdded', buyerId + '|' + recipeId, 'Delete added entry', '✕'));
    return tr;
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
    old.replaceWith(buildBuyerCardNode(buyer));
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
    const emptyAdd   = !lo.addedRecipeIds   || lo.addedRecipeIds.length   === 0;
    const emptyRem   = !lo.removedRecipeIds || lo.removedRecipeIds.length === 0;
    const hasOrder   = Array.isArray(lo.recipeOrder);
    if (emptyAdd && emptyRem && !hasOrder) {
        delete state.current.buyerLists[buyerId];
    }
    if (state.current.buyerLists && Object.keys(state.current.buyerLists).length === 0) {
        delete state.current.buyerLists;
    }
}

function materializeBuyerRecipeOrder(buyerId) {
    const buyer = state.buyers.list.find(b => b.id === buyerId);
    if (!buyer) return null;
    const lo = getOrCreateBuyerListOverride(buyerId);
    if (!lo) return null;
    if (!lo.recipeOrder) {
        const removedSet = new Set(lo.removedRecipeIds || []);
        const vanillaIds = (buyer.entries || [])
            .filter(e => e.resolved && !removedSet.has(e.recipeId))
            .map(e => e.recipeId);
        lo.recipeOrder = vanillaIds.concat(lo.addedRecipeIds || []);
    }
    return lo;
}

function moveBuyerEntry(buyerId, recipeId, delta) {
    const lo = materializeBuyerRecipeOrder(buyerId);
    if (!lo || !lo.recipeOrder) return;
    const arr = lo.recipeOrder;
    const idx = arr.indexOf(recipeId);
    if (idx < 0) return;
    const newIdx = idx + delta;
    if (newIdx < 0 || newIdx >= arr.length) return;
    arr.splice(idx, 1);
    arr.splice(newIdx, 0, recipeId);
    markDirty();
    refreshBuyerCard(buyerId);
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
    if (lo && lo.recipeOrder) {
        const orderIdx = lo.recipeOrder.indexOf(recipeId);
        if (orderIdx >= 0) lo.recipeOrder.splice(orderIdx, 1);
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
    if (lo.recipeOrder) lo.recipeOrder.push(id);
    markDirty();
    refreshBuyerCard(buyerId);
}

function onBuyersListClick(e) {
    const t = e.target.closest && e.target.closest(
        '[data-buyer-add],[data-buyer-delete],[data-buyer-delete-added],'
        + '[data-buyer-restore],[data-buyer-reset],[data-buyer-move]');
    if (!t) return;
    if (t.dataset.buyerAdd) {
        addBuyerEntry(t.dataset.buyerAdd);
        return;
    }
    if (t.dataset.buyerMove) {
        const [buyerId, recipeId, deltaStr] = t.dataset.buyerMove.split('|');
        moveBuyerEntry(buyerId, recipeId, parseInt(deltaStr, 10));
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
