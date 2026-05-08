'use strict';

// Windrose Stack Size Configurator -- vanilla JS, no framework.
// State lives in a single object; the few mutators that change it call the
// affected render functions explicitly.

const state = {
    items: [],       // [{id, vanillaStack, itemClass, rarity, category, icon, meta}]
    profiles: [],    // summaries from /api/profiles
    current: null,   // full profile, with .isBuiltin flag from server
    isDirty: false,
};

// ---------- API helpers --------------------------------------------------

async function api(method, path, body) {
    const opts = { method, headers: {} };
    if (body !== undefined) {
        opts.headers['Content-Type'] = 'application/json';
        opts.body = JSON.stringify(body);
    }
    const r = await fetch(path, opts);
    if (r.status === 204) return null;
    if (!r.ok) {
        let err = { error: r.statusText };
        try { err = await r.json(); } catch (e) { /* keep statusText */ }
        throw new Error(method + ' ' + path + ': ' + (err.error || r.status));
    }
    return await r.json();
}

const esc = s => String(s == null ? '' : s).replace(/[&<>"']/g, c => ({
    '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
}[c]));

// ---------- Boot ---------------------------------------------------------

async function init() {
    const [profiles, items] = await Promise.all([
        api('GET', '/api/profiles'),
        api('GET', '/api/items'),
    ]);
    state.profiles = profiles;
    state.items = items
        .filter(i => typeof i.maxCountInSlot === 'number')
        .map(i => Object.assign({}, i, { vanillaStack: i.maxCountInSlot }));

    populateProfileSelect();
    populateValueFilter('filter-class',  'itemClass', 'All classes');
    populateValueFilter('filter-rarity', 'rarity',    'All rarities');
    bindHandlers();

    if (state.profiles.length > 0) {
        await loadProfile(state.profiles[0].id);
    } else {
        updateButtons();
    }
}

document.addEventListener('DOMContentLoaded', () => {
    init().catch(err => {
        document.body.innerHTML =
            '<pre style="color:#e16464;padding:2em;white-space:pre-wrap;">' +
            esc('Init failed: ' + err.message + '\n\n' + (err.stack || '')) +
            '</pre>';
    });
});

// ---------- Profile loading ---------------------------------------------

async function loadProfile(id) {
    state.current = await api('GET', '/api/profiles/' + encodeURIComponent(id));
    state.current.globals = state.current.globals || {};
    state.current.overrides = state.current.overrides || {};
    state.isDirty = false;
    document.getElementById('profile-select').value = id;
    applyProfileToUI();
    renderItems();
    renderStatus();
    updateButtons();
    setBuildLog([{ kind: 'info', msg: 'Profile loaded: ' + state.current.name }]);
}

function applyProfileToUI() {
    const p = state.current;
    const ss = (p.globals && p.globals.stackSize) || {};
    const mode = ss.absolute != null ? 'absolute'
              : ss.multiplier != null ? 'multiplier'
              : 'none';
    document.querySelector('input[name="ssmode"][value="' + mode + '"]').checked = true;
    document.getElementById('ss-mult').value = ss.multiplier == null ? 4 : ss.multiplier;
    document.getElementById('ss-cap').value  = ss.cap        == null ? 0 : ss.cap;
    document.getElementById('ss-abs').value  = ss.absolute   == null ? 999 : ss.absolute;
    syncStackSizeInputsState();
    renderProfileMeta();
}

function renderProfileMeta() {
    const p = state.current;
    const out = document.getElementById('profile-meta');
    if (!p) { out.innerHTML = ''; return; }
    let html = '';
    if (p.isBuiltin) html += '<span class="builtin-badge">BUILTIN</span>';
    if (state.isDirty) html += '<span class="dirty-badge">UNSAVED</span>';
    if (p.description) html += esc(p.description);
    out.innerHTML = html;
}

function syncStackSizeInputsState() {
    const mode = document.querySelector('input[name="ssmode"]:checked').value;
    const isReadonly = !!(state.current && state.current.isBuiltin);
    document.getElementById('ss-mult').disabled = mode !== 'multiplier' || isReadonly;
    document.getElementById('ss-cap').disabled  = mode !== 'multiplier' || isReadonly;
    document.getElementById('ss-abs').disabled  = mode !== 'absolute'   || isReadonly;
    for (const r of document.querySelectorAll('input[name="ssmode"]')) {
        r.disabled = isReadonly;
    }
}

function populateProfileSelect() {
    const sel = document.getElementById('profile-select');
    state.profiles.sort((a, b) => {
        if (a.isBuiltin !== b.isBuiltin) return a.isBuiltin ? -1 : 1;
        return a.name.localeCompare(b.name);
    });
    sel.innerHTML = '';
    for (const p of state.profiles) {
        const o = document.createElement('option');
        o.value = p.id;
        // Star marks builtins so they're scannable in the dropdown.
        o.textContent = (p.isBuiltin ? '★ ' : '') + p.name;
        sel.appendChild(o);
    }
}

function populateValueFilter(elId, key, allLabel) {
    const sel = document.getElementById(elId);
    const values = Array.from(new Set(state.items.map(i => i[key]).filter(x => x))).sort();
    // Keep the placeholder option, append discovered values.
    sel.innerHTML = '<option value="">' + esc(allLabel) + '</option>';
    for (const v of values) {
        const o = document.createElement('option');
        o.value = v; o.textContent = v;
        sel.appendChild(o);
    }
}

// ---------- Computed target (mirrors StackPatcher.cs) -------------------

function computeTarget(item) {
    const overrides = (state.current && state.current.overrides) || {};
    const ss = (state.current && state.current.globals && state.current.globals.stackSize) || {};
    const v = item.vanillaStack;

    const ov = overrides[item.id];
    if (ov && typeof ov.stackSize === 'number') {
        if (ov.stackSize === v) {
            return { html: '<span class="skip">no change</span>', changed: false, overridden: true, target: v };
        }
        return {
            html: '<b>' + ov.stackSize + '</b> <small>(override)</small>',
            changed: true, overridden: true, target: ov.stackSize,
        };
    }

    // For vanillaStack <= 1, globals only apply if the item is "promotable":
    //   * ItemClass == Consumable, OR
    //   * ItemType.TagName == Inventory.ItemType.Resource (catches Misc-tagged
    //     treasure pieces the game still classifies as resources), OR
    //   * Default+Resource (legacy folder rule).
    // Must stay in sync with StackPatcher.IsPromotable on the server side.
    const isPromotable = item.itemClass === 'Consumable'
        || item.itemType === 'Inventory.ItemType.Resource'
        || (item.itemClass === 'Default' && item.category === 'Resource');
    if (v <= 1 && !isPromotable) {
        return {
            html: '<span class="skip">vanilla (locked at 1)</span>',
            changed: false, overridden: false, target: v,
        };
    }

    if (typeof ss.absolute === 'number') {
        if (ss.absolute === v) {
            return { html: '<span class="skip">no change</span>', changed: false, overridden: false, target: v };
        }
        return { html: '<b>' + ss.absolute + '</b>', changed: true, overridden: false, target: ss.absolute };
    }
    if (typeof ss.multiplier === 'number') {
        let target = v * ss.multiplier;
        if (typeof ss.cap === 'number' && ss.cap > 0 && target > ss.cap) target = ss.cap;
        if (target === v) {
            return { html: '<span class="skip">no change</span>', changed: false, overridden: false, target: v };
        }
        return { html: '<b>' + target + '</b>', changed: true, overridden: false, target };
    }

    return { html: '<span class="skip">vanilla</span>', changed: false, overridden: false, target: v };
}

// ---------- Item rendering ----------------------------------------------

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

    // Use a DocumentFragment so we only touch the DOM once for ~1000 rows.
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

    const ov = state.current && state.current.overrides && state.current.overrides[item.id];
    const ovValue = ov && ov.stackSize != null ? ov.stackSize : '';
    const isReadonly = !!(state.current && state.current.isBuiltin);

    const iconHtml = item.icon
        ? '<img src="' + esc(item.icon) + '" loading="lazy" alt="">'
        : '<div class="placeholder-icon">?</div>';

    li.innerHTML =
        iconHtml +
        '<div class="name">' +
            '<b>' + esc(displayName) + '</b>' +
            '<small>' + esc(subtitle) + '</small>' +
        '</div>' +
        '<div class="compute">v' + item.vanillaStack + ' → ' + target.html + '</div>' +
        '<input type="number" class="override-input" data-item-id="' + esc(item.id) + '" ' +
               'value="' + esc(ovValue) + '" placeholder="—" min="0" step="1"' +
               (isReadonly ? ' disabled' : '') + '>';
    return li;
}

// In-place row update -- avoids losing focus on the override input.
function refreshRowInPlace(itemId) {
    const item = state.items.find(i => i.id === itemId);
    if (!item) return;
    const row = document.querySelector('.item[data-item-id="' + cssEsc(itemId) + '"]');
    if (!row) return;
    const t = computeTarget(item);
    row.classList.toggle('changed',    t.changed);
    row.classList.toggle('overridden', t.overridden);
    const compute = row.querySelector('.compute');
    if (compute) compute.innerHTML = 'v' + item.vanillaStack + ' → ' + t.html;
}

function cssEsc(s) {
    // CSS.escape for attribute selectors. Items ids are filename-safe so a
    // basic implementation is enough.
    return s.replace(/(["\\])/g, '\\$1');
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

// ---------- Mutations ----------------------------------------------------

function setStackSizeFromUI() {
    if (!state.current) return;
    const mode = document.querySelector('input[name="ssmode"]:checked').value;
    const mult = parseInt(document.getElementById('ss-mult').value, 10);
    const cap  = parseInt(document.getElementById('ss-cap').value,  10);
    const abs  = parseInt(document.getElementById('ss-abs').value,  10);

    state.current.globals = state.current.globals || {};
    if (mode === 'none') {
        state.current.globals.stackSize = null;
    } else if (mode === 'multiplier') {
        state.current.globals.stackSize = {
            multiplier: isFinite(mult) && mult >= 1 ? mult : 1,
            cap:        isFinite(cap)  && cap  > 0 ? cap  : null,
            absolute:   null,
        };
    } else {
        state.current.globals.stackSize = {
            multiplier: null, cap: null,
            absolute:   isFinite(abs) && abs >= 0 ? abs : 0,
        };
    }
    syncStackSizeInputsState();
    markDirty();
    renderStatus();
    renderItems();
}

function setOverrideFromInput(itemId, rawValue) {
    if (!state.current) return;
    state.current.overrides = state.current.overrides || {};
    const trimmed = (rawValue || '').trim();
    if (trimmed === '') {
        delete state.current.overrides[itemId];
    } else {
        const n = parseInt(trimmed, 10);
        if (!isFinite(n) || n < 0) return;  // ignore garbage
        state.current.overrides[itemId] = { stackSize: n };
    }
    markDirty();
    renderStatus();
    refreshRowInPlace(itemId);
}

function markDirty() {
    state.isDirty = true;
    updateButtons();
    renderProfileMeta();
}

function updateButtons() {
    const p = state.current;
    const builtin = !!(p && p.isBuiltin);
    document.getElementById('btn-save').disabled       = !p || builtin || !state.isDirty;
    document.getElementById('btn-rename').disabled     = !p || builtin;
    document.getElementById('btn-delete').disabled     = !p || builtin;
    document.getElementById('btn-build').disabled      = !p;
    document.getElementById('btn-duplicate').disabled  = !p;
}

// ---------- Buttons ------------------------------------------------------

async function onSave() {
    const p = state.current;
    if (!p || p.isBuiltin) return;
    const body = {
        id: p.id, name: p.name, description: p.description,
        createdAt: p.createdAt,
        globals: p.globals, overrides: p.overrides,
    };
    const updated = await api('PUT', '/api/profiles/' + encodeURIComponent(p.id), body);
    state.current = updated;
    state.current.globals   = state.current.globals   || {};
    state.current.overrides = state.current.overrides || {};
    state.isDirty = false;
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    document.getElementById('profile-select').value = p.id;
    renderProfileMeta();
    updateButtons();
}

async function onNew() {
    const name = prompt('New profile name?', 'My Stacks');
    if (!name) return;
    const created = await api('POST', '/api/profiles', {
        name,
        description: '',
        globals: { stackSize: { multiplier: 4 } },
        overrides: {},
    });
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    await loadProfile(created.id);
}

async function onDuplicate() {
    if (!state.current) return;
    const created = await api('POST',
        '/api/profiles/' + encodeURIComponent(state.current.id) + '/duplicate');
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    await loadProfile(created.id);
}

function onRename() {
    if (!state.current || state.current.isBuiltin) return;
    const newName = prompt('New name?', state.current.name);
    if (!newName || newName === state.current.name) return;
    state.current.name = newName;
    markDirty();
    populateProfileSelect();  // dropdown text needs refresh
    document.getElementById('profile-select').value = state.current.id;
}

async function onDelete() {
    if (!state.current || state.current.isBuiltin) return;
    if (!confirm('Delete profile "' + state.current.name + '"?')) return;
    await api('DELETE', '/api/profiles/' + encodeURIComponent(state.current.id));
    state.profiles = await api('GET', '/api/profiles');
    populateProfileSelect();
    if (state.profiles.length > 0) {
        await loadProfile(state.profiles[0].id);
    } else {
        state.current = null;
        renderItems();
        renderStatus();
        updateButtons();
        renderProfileMeta();
    }
}

async function onBuild() {
    if (!state.current) return;
    if (state.isDirty && !state.current.isBuiltin) {
        if (confirm('Save unsaved changes before building?')) {
            await onSave();
        }
    }
    setBuildLog([{ kind: 'info', msg: 'Building (this may take 1-2 seconds)...' }]);
    document.getElementById('btn-build').disabled = true;

    try {
        const r = await fetch('/api/build', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ profileId: state.current.id }),
        });
        const data = await r.json();
        const lines = [];
        for (const m of data.log || []) lines.push({ kind: 'ok', msg: m });
        if (data.success) {
            const sizeKb = (data.sizeBytes / 1024).toFixed(1);
            lines.push({ kind: 'ok', msg:
                'DONE -- ' + data.pakPath + ' (' + sizeKb + ' KB, ' + data.fileCount + ' files)' });
        } else {
            lines.push({ kind: 'err', msg: 'ERROR: ' + (data.error || 'unknown') });
        }
        setBuildLog(lines);
    } catch (e) {
        setBuildLog([{ kind: 'err', msg: 'NETWORK ERROR: ' + e.message }]);
    } finally {
        updateButtons();
    }
}

function setBuildLog(lines) {
    const out = document.getElementById('build-log');
    out.innerHTML = lines.map(l =>
        '<span class="' + l.kind + '">[' + l.kind.toUpperCase() + ']</span> ' + esc(l.msg)
    ).join('\n');
}

// ---------- Bindings -----------------------------------------------------

function bindHandlers() {
    document.getElementById('profile-select').addEventListener('change', e => {
        if (state.isDirty) {
            if (!confirm('Discard unsaved changes?')) {
                e.target.value = state.current.id;
                return;
            }
        }
        loadProfile(e.target.value);
    });
    document.getElementById('btn-new').addEventListener('click',       onNew);
    document.getElementById('btn-duplicate').addEventListener('click', onDuplicate);
    document.getElementById('btn-rename').addEventListener('click',    onRename);
    document.getElementById('btn-save').addEventListener('click',      onSave);
    document.getElementById('btn-delete').addEventListener('click',    onDelete);
    document.getElementById('btn-build').addEventListener('click',     onBuild);

    for (const r of document.querySelectorAll('input[name="ssmode"]')) {
        r.addEventListener('change', setStackSizeFromUI);
    }
    document.getElementById('ss-mult').addEventListener('input', setStackSizeFromUI);
    document.getElementById('ss-cap').addEventListener('input',  setStackSizeFromUI);
    document.getElementById('ss-abs').addEventListener('input',  setStackSizeFromUI);

    document.getElementById('item-filter').addEventListener('input',     renderItems);
    document.getElementById('filter-class').addEventListener('change',   renderItems);
    document.getElementById('filter-rarity').addEventListener('change',  renderItems);
    document.getElementById('filter-changed').addEventListener('change', renderItems);

    // Override-input changes are delegated from the list root.
    document.getElementById('item-list').addEventListener('input', e => {
        if (e.target.classList && e.target.classList.contains('override-input')) {
            setOverrideFromInput(e.target.dataset.itemId, e.target.value);
        }
    });
}
