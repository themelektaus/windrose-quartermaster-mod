'use strict';

function computeTarget(item) {
    const overrides = (state.current && state.current.overrides) || {};
    const ss = (state.current && state.current.globals && state.current.globals.stackSize) || {};
    const v = item.vanillaStack;

    const ov = overrides[item.id];
    if (ov && typeof ov.stackSize === 'number') {
        if (ov.stackSize === v) {
            return { html: '<span class="skip">' + ov.stackSize + '</span>', changed: false, overridden: true, noChange: true, target: v };
        }
        return {
            html: '<b>' + ov.stackSize + '</b>',
            changed: true, overridden: true, target: ov.stackSize,
        };
    }

    const isPromotable = item.itemClass === 'Consumable'
        || item.itemType === 'Inventory.ItemType.Resource'
        || (item.itemClass === 'Default' && item.category === 'Resource');
    if (v <= 1 && !isPromotable) {
        return {
            html: '<span class="skip">1</span>',
            changed: false, overridden: false, target: v,
        };
    }

    if (typeof ss.absolute === 'number') {
        if (ss.absolute === v) {
            return { html: '<span class="skip">0</span>', changed: false, overridden: false, noChange: true, target: v };
        }
        return { html: '<b>' + ss.absolute + '</b>', changed: true, overridden: false, target: ss.absolute };
    }
    if (typeof ss.multiplier === 'number') {
        let target = v * ss.multiplier;
        if (typeof ss.cap === 'number' && ss.cap > 0 && target > ss.cap) target = ss.cap;
        if (target === v) {
            return { html: '<span class="skip">0</span>', changed: false, overridden: false, noChange: true, target: v };
        }
        return { html: '<b>' + target + '</b>', changed: true, overridden: false, target };
    }

    return { html: '<span class="skip">' + v + '</span>', changed: false, overridden: false, target: v };
}

function filterItems() {
    const q   = document.getElementById('item-filter').value.toLowerCase().trim();
    const fc  = document.getElementById('filter-class').value;
    const fr  = document.getElementById('filter-rarity').value;
    const chg = document.getElementById('filter-changed').value;

    return state.items.filter(item => {
        if (q) {
            const name = (item.meta && item.meta.name) || '';
            if (!item.id.toLowerCase().includes(q) && !name.toLowerCase().includes(q)) return false;
        }
        if (fc && item.itemClass !== fc) return false;
        if (fr && item.rarity    !== fr) return false;
        if (chg !== 'all') {
            const t = computeTarget(item);
            if (chg === 'changed'    && !t.changed)    return false;
            if (chg === 'unchanged'  &&  t.changed)    return false;
            if (chg === 'overridden' && !t.overridden) return false;
        }
        return true;
    });
}

function renderItems() {
    const ul = document.getElementById('item-list');
    const filtered = filterItems();
    document.getElementById('item-count').textContent =
        filtered.length + ' / ' + state.items.length + ' items';

    const frag = document.createDocumentFragment();
    for (const item of filtered) frag.appendChild(buildItemRow(item));
    ul.innerHTML = '';
    ul.appendChild(frag);
}

function buildItemRow(item) {
    const li = document.createElement('li');
    li.className = 'item';
    li.dataset.itemId = item.id;

    const target = computeTarget(item);
    if (target.changed)    li.classList.add('changed');
    if (target.overridden) li.classList.add('overridden');

    const displayName = (item.meta && item.meta.name) || item.id;
    const subtitle = (item.itemClass || '')
        + (item.category ? ' · ' + item.category : '')
        + (item.rarity   ? ' · ' + item.rarity   : '');

    const description = item.meta?.description ?? ``
    const ov = state.current && state.current.overrides && state.current.overrides[item.id];
    const ovValue = ov && ov.stackSize != null ? ov.stackSize : '';

    const iconHtml = item.icon
        ? '<img src="' + esc(item.icon) + '" loading="lazy" alt="">'
        : '<div class="placeholder-icon">?</div>';

    li.innerHTML =
        iconHtml +
        '<div class="name">' +
            '<b>' + esc(displayName) + '</b>' +
            '<small>' + esc(subtitle) + '</small>' +
            '<div>' + esc(description) + '</div>' +
        '</div>' +
        '<div class="compute">' + item.vanillaStack + ' → ' + target.html + '</div>' +
        '<input type="number" class="override-input" data-item-id="' + esc(item.id) + '" ' +
               'value="' + esc(ovValue) + '" placeholder="' + target.target + '" min="0" step="1">';
    return li;
}

function refreshRowInPlace(itemId) {
    const item = state.items.find(i => i.id === itemId);
    if (!item) return;
    const row = document.querySelector('.item[data-item-id="' + cssEsc(itemId) + '"]');
    if (!row) return;
    const t = computeTarget(item);
    row.classList.toggle('changed',    t.changed);
    row.classList.toggle('overridden', t.overridden);
    row.classList.toggle('noChange',   t.noChange);
    const compute = row.querySelector('.compute');
    if (compute) compute.innerHTML = item.vanillaStack + ' → ' + t.html;
}

function renderStatus() {
    const overrides = (state.current && state.current.overrides) || {};
    let overrideCount = 0;
    for (const k in overrides) if (overrides[k] && overrides[k].stackSize != null) overrideCount++;

    let modified = 0, promoted = 0;
    for (const item of state.items) {
        const t = computeTarget(item);
        if (t.changed) {
            modified++;
            if (item.vanillaStack <= 1) promoted++;
        }
    }
    document.getElementById('stat-total').textContent     = state.items.length;
    document.getElementById('stat-overrides').textContent = overrideCount;
    document.getElementById('stat-modified').textContent  = modified;
    document.getElementById('stat-promoted').textContent  = promoted;
}

function setOverrideFromInput(itemId, rawValue) {
    if (!state.current) return;
    state.current.overrides = state.current.overrides || {};
    const trimmed = (rawValue || '').trim();
    if (trimmed === '') {
        delete state.current.overrides[itemId];
    } else {
        const n = parseInt(trimmed, 10);
        if (!isFinite(n) || n < 0) return;
        state.current.overrides[itemId] = { stackSize: n };
    }
    markDirty();
    renderStatus();
    refreshRowInPlace(itemId);
}

function populateValueFilter(elId, key, allLabel) {
    const sel = document.getElementById(elId);
    const values = Array.from(new Set(state.items.map(i => i[key]).filter(x => x))).sort();
    sel.innerHTML = '<option value="">' + esc(allLabel) + '</option>';
    for (const v of values) {
        const o = document.createElement('option');
        o.value = v; o.textContent = v;
        sel.appendChild(o);
    }
}

function bindItemsHandlers() {
    document.getElementById('item-filter').addEventListener('input',     renderItems);
    document.getElementById('filter-class').addEventListener('change',   renderItems);
    document.getElementById('filter-rarity').addEventListener('change',  renderItems);
    document.getElementById('filter-changed').addEventListener('change', renderItems);

    document.getElementById('item-list').addEventListener('input', e => {
        if (e.target.classList && e.target.classList.contains('override-input')) {
            setOverrideFromInput(e.target.dataset.itemId, e.target.value);
        }
    });
}
