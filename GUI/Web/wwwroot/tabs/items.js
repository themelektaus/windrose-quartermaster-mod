'use strict';

function computeTarget(item) {
    const overrides = (state.current && state.current.overrides) || {};
    const ss = (state.current && state.current.globals && state.current.globals.stackSize) || {};
    const v = item.vanillaStack;

    const ov = overrides[item.id];
    if (ov && typeof ov.stackSize === 'number') {
        if (ov.stackSize === v) {
            return { display: ov.stackSize, strong: false, changed: false, overridden: true, noChange: true, target: v };
        }
        return { display: ov.stackSize, strong: true, changed: true, overridden: true, target: ov.stackSize };
    }

    const isPromotable = item.itemClass === 'Consumable'
        || item.itemType === 'Inventory.ItemType.Resource'
        || (item.itemClass === 'Default' && item.category === 'Resource');
    if (v <= 1 && !isPromotable) {
        return { display: 1, strong: false, changed: false, overridden: false, target: v };
    }

    if (typeof ss.absolute === 'number') {
        if (ss.absolute === v) {
            return { display: 0, strong: false, changed: false, overridden: false, noChange: true, target: v };
        }
        return { display: ss.absolute, strong: true, changed: true, overridden: false, target: ss.absolute };
    }
    if (typeof ss.multiplier === 'number') {
        let target = v * ss.multiplier;
        if (typeof ss.cap === 'number' && ss.cap > 0 && target > ss.cap) target = ss.cap;
        if (target === v) {
            return { display: 0, strong: false, changed: false, overridden: false, noChange: true, target: v };
        }
        return { display: target, strong: true, changed: true, overridden: false, target };
    }

    return { display: v, strong: false, changed: false, overridden: false, target: v };
}

function setComputeCell(el, vanillaStack, t) {
    el.textContent = vanillaStack + ' → ';
    const tag = t.strong ? 'b' : 'span';
    const span = document.createElement(tag);
    if (!t.strong) span.className = 'skip';
    span.textContent = t.display;
    el.appendChild(span);
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
    ul.replaceChildren(frag);
}

function buildItemRow(item) {
    const li = cloneTemplate('tpl-item-row');
    li.dataset.itemId = item.id;

    const target = computeTarget(item);
    if (target.changed)    li.classList.add('changed');
    if (target.overridden) li.classList.add('overridden');

    if (item.icon) {
        const img = document.createElement('img');
        img.src = item.icon;
        img.loading = 'lazy';
        img.alt = '';
        li.querySelector('.placeholder-icon').replaceWith(img);
    }

    li.querySelector('.item-name').textContent = (item.meta && item.meta.name) || item.id;
    li.querySelector('.item-sub').textContent = (item.itemClass || '')
        + (item.category ? ' · ' + item.category : '')
        + (item.rarity   ? ' · ' + item.rarity   : '');
    li.querySelector('.item-desc').textContent = (item.meta && item.meta.description) || '';

    setComputeCell(li.querySelector('.compute'), item.vanillaStack, target);

    const input = li.querySelector('.override-input');
    input.dataset.itemId = item.id;
    const ov = state.current && state.current.overrides && state.current.overrides[item.id];
    input.value = ov && ov.stackSize != null ? ov.stackSize : '';
    input.placeholder = target.target;
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
    if (compute) setComputeCell(compute, item.vanillaStack, t);
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
    sel.replaceChildren(new Option(allLabel, ''));
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
