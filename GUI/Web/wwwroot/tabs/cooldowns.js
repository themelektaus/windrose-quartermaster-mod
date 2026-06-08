'use strict';

const COOLDOWN_FAMILIES = [
    {
        key: 'elixir',
        profileKey: 'elixirMultiplier',
        vanillaSeconds: 3,
        renderReadout: (mul) => (3 * mul).toFixed(1) + ' s',
    },
    {
        key: 'medicine',
        profileKey: 'medicineMultiplier',
        vanillaSeconds: 15,
        renderReadout: (mul) => (15 * mul).toFixed(1) + ' s',
    },
    {
        key: 'recall',
        profileKey: 'recallMultiplier',
        vanillaSeconds: 600,
        renderReadout: (mul) => Math.round(600 * mul) + ' s',
    },
    {
        key: 'shiprepair',
        profileKey: 'shipRepairKitMultiplier',
        vanillaSeconds: 40,
        renderReadout: (mul) => Math.round(40 * mul) + ' s',
    },
    {
        key: 'boar',
        profileKey: 'boarWhistleMultiplier',
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
        vanillaSeconds: 5,
        renderReadout: (mul) => (5 * mul).toFixed(1) + ' s',
    },
    {
        key: 'reload',
        profileKey: 'rangedReloadMultiplier',
        vanillaSeconds: 13,
        renderReadout: (mul) => '~' + (13 * mul).toFixed(1) + ' s',
    },
    {
        key: 'cannon',
        profileKey: 'shipCannonMultiplier',
        vanillaSeconds: 12,
        renderReadout: (mul) => '~' + (12 * mul).toFixed(1) + ' s',
    },
    {
        key: 'cannonrange',
        profileKey: 'shipCannonRangeMultiplier',
        // Player cannon firing range (ShotRangeInterval.Max): ~180-200 m vanilla.
        // 180 m (18000 units) is the representative caliber for the readout.
        renderReadout: (mul) => '~' + Math.round(180 * mul) + ' m',
    },
    {
        key: 'cannondamage',
        profileKey: 'shipCannonDamageMultiplier',
        // Player cannonball damage (DamageGEData[].DamageInterval). Internal
        // damage numbers are abstract, so show the boost as a percentage.
        renderReadout: (mul) => {
            const pct = (mul - 1.0) * 100.0;
            const sign = pct >= 0 ? '+' : '';
            return sign + pct.toFixed(0) + '%';
        },
    },
    {
        key: 'souleater',
        profileKey: 'soulEaterAbilityMultiplier',
        vanillaSeconds: 180,
        renderReadout: (mul) => (180 * mul).toFixed(1) + ' s',
    },
    {
        key: 'soulharvestdamage',
        profileKey: 'soulHarvestDamageMultiplier',
        // Soul Harvest life-drain magnitude (CT row, vanilla 900). Internal damage
        // number is abstract, so show the change as a percentage like cannon damage.
        renderReadout: (mul) => {
            const pct = (mul - 1.0) * 100.0;
            const sign = pct >= 0 ? '+' : '';
            return sign + pct.toFixed(0) + '%';
        },
    },
    {
        key: 'soulharvestradius',
        profileKey: 'soulHarvestRadiusMultiplier',
        // TargetParams MaxDistance, vanilla 500 units = 5 m (100 units = 1 m).
        renderReadout: (mul) => (5 * mul).toFixed(1) + ' m',
    },
    {
        key: 'boardingrange',
        profileKey: 'shipBoardingRangeMultiplier',
        // MaxBoardingDistance is per-ship (3000-5500 units = 30-55 m); Brig's
        // 4000 units = 40 m is the representative readout (100 units = 1 m).
        renderReadout: (mul) => '~' + Math.round(40 * mul) + ' m',
    },
    {
        key: 'boardingaim',
        profileKey: 'shipBoardingAimMultiplier',
        // BoardingSweepRadius, vanilla 100 units = 1 m of aim forgiveness.
        renderReadout: (mul) => '~' + (1 * mul).toFixed(1) + ' m',
    },
    {
        key: 'boardingangle',
        profileKey: 'shipBoardingAngleMultiplier',
        // MainAxisDifferenceAngle, vanilla ~34 deg (Brig/Frigate); the runtime
        // cosine is recomputed from the scaled angle. Capped below 90 deg.
        renderReadout: (mul) => '~' + Math.min(89, Math.round(34 * mul)) + String.fromCharCode(176),
    },
    {
        key: 'boardingspeed',
        profileKey: 'shipBoardingSpeedMultiplier',
        // MaxClosingSpeedKnots, vanilla 15 kn (absent on Cutter -> left vanilla).
        renderReadout: (mul) => '~' + Math.round(15 * mul) + ' kn',
    },
    {
        key: 'foodduration',
        profileKey: 'foodBuffDurationMultiplier',
        vanillaSeconds: 900,
        renderReadout: (mul) => formatSeconds(900 * mul),
    },
];

const STATION_FAMILIES = [
    {
        key: 'crop',
        profileKey: 'cropGrowthMultiplier',
        vanillaSeconds: 4200,
        renderReadout: (mul) => formatSeconds(4200 * mul),
    },
    {
        key: 'smelting',
        profileKey: 'smeltingMultiplier',
        vanillaSeconds: 30,
        renderReadout: (mul) => formatSeconds(30 * mul),
    },
    {
        key: 'kiln',
        profileKey: 'kilnMultiplier',
        vanillaSeconds: 30,
        renderReadout: (mul) => formatSeconds(30 * mul),
    },
    {
        key: 'tanning',
        profileKey: 'tanningMultiplier',
        vanillaSeconds: 45,
        renderReadout: (mul) => formatSeconds(45 * mul),
    },
    {
        key: 'milling',
        profileKey: 'millingMultiplier',
        vanillaSeconds: 10,
        renderReadout: (mul) => formatSeconds(10 * mul),
    },
    {
        key: 'bits',
        profileKey: 'buildingBitsMultiplier',
        vanillaSeconds: 1,
        renderReadout: (mul) => formatSeconds(1 * mul),
    },
    {
        key: 'deco',
        profileKey: 'decorationMultiplier',
        vanillaSeconds: 1,
        renderReadout: (mul) => formatSeconds(1 * mul),
    },
    {
        key: 'armor',
        profileKey: 'armorWeaponMultiplier',
        vanillaSeconds: 1,
        renderReadout: (mul) => formatSeconds(1 * mul),
    },
    {
        key: 'tradeoutpost',
        profileKey: 'tradeOutpostMultiplier',
        vanillaSeconds: 4200,
        renderReadout: (mul) => formatSeconds(4200 * mul),
    },
    {
        key: 'other',
        profileKey: 'otherMultiplier',
        vanillaSeconds: null,
        renderReadout: (mul) => {
            const pct = (mul - 1.0) * 100.0;
            const sign = pct >= 0 ? '+' : '';
            return sign + pct.toFixed(0) + '%';
        },
    },
];

function getCooldownEls(family) {
    return {
        slider:  document.getElementById('cd-' + family.key + '-multiplier'),
        value:   document.getElementById('cd-' + family.key + '-multiplier-value'),
        readout: document.getElementById('cd-' + family.key + '-readout'),
    };
}

function syncCooldownReadout(family) {
    const els = getCooldownEls(family);
    if (!els.slider || !els.value || !els.readout) return;
    const mul = parseFloat(els.slider.value);
    const safeMul = isFinite(mul) ? mul : 1.0;
    els.value.textContent = safeMul.toFixed(2).replace(/\.?0+$/, '') + 'x';
    els.readout.textContent = family.renderReadout(safeMul);
}

function setCooldownFromUI(family) {
    if (!state.current) return;
    const els = getCooldownEls(family);
    if (!els.slider) return;
    syncCooldownReadout(family);
    const mul = parseFloat(els.slider.value);
    state.current.globals = state.current.globals || {};
    const cd = state.current.globals.cooldowns || {};
    if (!isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete cd[family.profileKey];
    } else {
        cd[family.profileKey] = mul;
    }
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
        if (!els.slider) continue;
        const stored = cd[family.profileKey];
        const active = stored != null && Math.abs(stored - 1.0) > 1e-9;
        els.slider.value = active ? stored : 1.0;
        syncCooldownReadout(family);
    }
}

function bindCooldownsHandlers() {
    for (const family of COOLDOWN_FAMILIES) {
        const els = getCooldownEls(family);
        if (!els.slider) continue;
        const local = family;
        els.slider.addEventListener('input', () => setCooldownFromUI(local));
    }
}

function formatSeconds(s) {
    if (!isFinite(s) || s < 0) return '-';
    if (s < 60)   return s.toFixed(s < 10 ? 1 : 0) + ' s';
    if (s < 3600) return (s / 60).toFixed(s < 600 ? 1 : 0) + ' min';
    return (s / 3600).toFixed(1) + ' h';
}

function getStationEls(family) {
    return {
        slider:  document.getElementById('st-' + family.key + '-multiplier'),
        value:   document.getElementById('st-' + family.key + '-multiplier-value'),
        readout: document.getElementById('st-' + family.key + '-readout'),
    };
}

function syncStationReadout(family) {
    const els = getStationEls(family);
    if (!els.slider || !els.value || !els.readout) return;
    const mul = parseFloat(els.slider.value);
    const safeMul = isFinite(mul) ? mul : 1.0;
    els.value.textContent = safeMul.toFixed(2).replace(/\.?0+$/, '') + 'x';
    els.readout.textContent = family.renderReadout(safeMul);
}

function setStationFromUI(family) {
    if (!state.current) return;
    const els = getStationEls(family);
    if (!els.slider) return;
    syncStationReadout(family);
    const mul = parseFloat(els.slider.value);
    state.current.globals = state.current.globals || {};
    const pt = state.current.globals.productionTimes || {};
    if (!isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete pt[family.profileKey];
    } else {
        pt[family.profileKey] = mul;
    }
    const anyActive = STATION_FAMILIES.some(f => pt[f.profileKey] != null);
    if (anyActive) {
        state.current.globals.productionTimes = pt;
    } else {
        delete state.current.globals.productionTimes;
    }
    markDirty();
}

function applyStationsToUI() {
    const pt = (state.current && state.current.globals && state.current.globals.productionTimes) || {};
    for (const family of STATION_FAMILIES) {
        const els = getStationEls(family);
        if (!els.slider) continue;
        const stored = pt[family.profileKey];
        const active = stored != null && Math.abs(stored - 1.0) > 1e-9;
        els.slider.value = active ? stored : 1.0;
        syncStationReadout(family);
    }
}

function bindStationsHandlers() {
    for (const family of STATION_FAMILIES) {
        const els = getStationEls(family);
        if (!els.slider) continue;
        const local = family;
        els.slider.addEventListener('input', () => setStationFromUI(local));
    }
}
