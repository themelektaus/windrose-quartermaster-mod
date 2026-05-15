'use strict';

// One row per cooldown family. Each row drives the slider HTML id naming
// scheme (`cd-<key>-*`), the camelCase profile globals key the multiplier
// is stored under, and the readout renderer (so each family can display
// its own units: seconds, percent, or "~Ns" averaged).
const COOLDOWN_FAMILIES = [
    {
        key: 'elixir',
        profileKey: 'elixirMultiplier',
        defaultEnabled: 0.5,
        vanillaSeconds: 3,
        renderReadout: (mul) => (3 * mul).toFixed(1) + ' s',
    },
    {
        key: 'medicine',
        profileKey: 'medicineMultiplier',
        defaultEnabled: 0.5,
        vanillaSeconds: 15,
        renderReadout: (mul) => (15 * mul).toFixed(1) + ' s',
    },
    {
        key: 'recall',
        profileKey: 'recallMultiplier',
        defaultEnabled: 0.25,
        vanillaSeconds: 600,
        renderReadout: (mul) => Math.round(600 * mul) + ' s',
    },
    {
        key: 'shiprepair',
        profileKey: 'shipRepairKitMultiplier',
        defaultEnabled: 0.25,
        vanillaSeconds: 40,
        renderReadout: (mul) => Math.round(40 * mul) + ' s',
    },
    {
        key: 'boar',
        profileKey: 'boarWhistleMultiplier',
        defaultEnabled: 0.5,
        vanillaSeconds: null,
        renderReadout: (mul) => {
            const pct = (mul - 1.0) * 100.0;
            const sign = pct >= 0 ? '+' : '';
            return sign + pct.toFixed(0) + '%';
        },
    },
    {
        key: 'shipsummon',
        profileKey: 'shipSummonMultiplier',
        defaultEnabled: 0.4,
        vanillaSeconds: 5,
        renderReadout: (mul) => (5 * mul).toFixed(1) + ' s',
    },
    {
        key: 'reload',
        profileKey: 'rangedReloadMultiplier',
        defaultEnabled: 0.5,
        // Avg ~13s across all firearm variants (Pistol 12, Musket 15,
        // Blunderbuss 14ish). The "~" prefix makes clear it's averaged.
        vanillaSeconds: 13,
        renderReadout: (mul) => '~' + (13 * mul).toFixed(1) + ' s',
    },
    {
        key: 'cannon',
        profileKey: 'shipCannonMultiplier',
        defaultEnabled: 0.5,
        vanillaSeconds: 10,
        renderReadout: (mul) => (10 * mul).toFixed(1) + ' s',
    },
];

function getCooldownEls(family) {
    return {
        enabled: document.getElementById('cd-' + family.key + '-enabled'),
        slider:  document.getElementById('cd-' + family.key + '-multiplier'),
        value:   document.getElementById('cd-' + family.key + '-multiplier-value'),
        readout: document.getElementById('cd-' + family.key + '-readout'),
    };
}

function syncCooldownInputState(family) {
    const els = getCooldownEls(family);
    if (!els.enabled || !els.slider) return;
    els.enabled.disabled = false;
    els.slider.disabled  = !els.enabled.checked;
}

function syncCooldownReadout(family) {
    const els = getCooldownEls(family);
    if (!els.slider || !els.value || !els.readout) return;
    const mul = parseFloat(els.slider.value);
    const safeMul = isFinite(mul) ? mul : 1.0;
    els.value.innerHTML = safeMul.toFixed(2).replace(/\.?0+$/, '') + 'x<!--&times;-->';
    els.readout.textContent = family.renderReadout(safeMul);
}

function setCooldownFromUI(family) {
    if (!state.current) return;
    const els = getCooldownEls(family);
    if (!els.enabled || !els.slider) return;
    syncCooldownReadout(family);
    syncCooldownInputState(family);
    const enabled = els.enabled.checked;
    const mul = parseFloat(els.slider.value);
    state.current.globals = state.current.globals || {};
    const cd = state.current.globals.cooldowns || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete cd[family.profileKey];
    } else {
        cd[family.profileKey] = mul;
    }
    // Collapse empty cooldowns object so saved JSON stays clean.
    const anyActive = COOLDOWN_FAMILIES.some(f => cd[f.profileKey] != null);
    if (anyActive) {
        state.current.globals.cooldowns = cd;
    } else {
        delete state.current.globals.cooldowns;
    }
    markDirty();
}

function applyCooldownsToUI() {
    const cd = (state.current && state.current.globals && state.current.globals.cooldowns) || {};
    for (const family of COOLDOWN_FAMILIES) {
        const els = getCooldownEls(family);
        if (!els.enabled || !els.slider) continue;
        const stored = cd[family.profileKey];
        const active = stored != null && Math.abs(stored - 1.0) > 1e-9;
        els.enabled.checked = active;
        els.slider.value = active ? stored : family.defaultEnabled;
        syncCooldownReadout(family);
        syncCooldownInputState(family);
    }
}

function bindCooldownsHandlers() {
    for (const family of COOLDOWN_FAMILIES) {
        const els = getCooldownEls(family);
        if (!els.enabled || !els.slider) continue;
        // Capture family in the listener so each handler stays bound to
        // its own row (avoids the classic for-loop-var closure pitfall).
        const local = family;
        els.enabled.addEventListener('change', () => setCooldownFromUI(local));
        els.slider.addEventListener('input',  () => setCooldownFromUI(local));
    }
}
