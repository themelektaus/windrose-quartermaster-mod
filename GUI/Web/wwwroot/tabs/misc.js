'use strict';

const STACK_SIZE_SETS = [
    { name: 'ssmode',      mult: 'ss-mult',   cap: 'ss-cap',   abs: 'ss-abs'   },
    { name: 'ssmode-misc', mult: 'm-ss-mult', cap: 'm-ss-cap', abs: 'm-ss-abs' },
];

// "Deposit visuals" catalog (deposits + selectable albedo textures), fetched once.
let DEPOSIT_VISUAL_CATALOG = null;

async function loadDepositVisualCatalog() {
    if (DEPOSIT_VISUAL_CATALOG) return DEPOSIT_VISUAL_CATALOG;
    try {
        const resp = await fetch('/api/deposit-visual/catalog');
        if (resp.ok) DEPOSIT_VISUAL_CATALOG = await resp.json();
    } catch (_) { /* leave null; selects stay empty until reachable */ }
    return DEPOSIT_VISUAL_CATALOG;
}

function populateDepositVisualSelects() {
    const cat = DEPOSIT_VISUAL_CATALOG;
    if (!cat || !cat.textures) return;
    for (const id of ['deposit-iron-texture', 'deposit-sulfur-texture']) {
        const el = document.getElementById(id);
        if (!el || el.options.length) continue; // populate once
        for (const t of cat.textures) {
            const o = document.createElement('option');
            o.value = t.key;
            o.textContent = t.label;
            el.appendChild(o);
        }
    }
}

function depositDefaultTexture(depositKey) {
    const cat = DEPOSIT_VISUAL_CATALOG;
    if (!cat || !cat.deposits) return null;
    const d = cat.deposits.find(x => x.key === depositKey);
    return d ? d.defaultTexture : null;
}

function setSelectValueIfPresent(id, val) {
    const el = document.getElementById(id);
    if (!el || val == null) return;
    for (const o of el.options) {
        if (o.value === val) { el.value = val; return; }
    }
}

function syncStackSizeUIFromState() {
    const ss = (state.current && state.current.globals && state.current.globals.stackSize) || {};
    const mode = ss.absolute != null ? 'absolute'
              : ss.multiplier != null ? 'multiplier'
              : 'none';
    for (const set of STACK_SIZE_SETS) {
        const radio = document.querySelector('input[name="' + set.name + '"][value="' + mode + '"]');
        if (radio) radio.checked = true;
        document.getElementById(set.mult).value = ss.multiplier == null ? 4   : ss.multiplier;
        document.getElementById(set.cap).value  = ss.cap        == null ? 0   : ss.cap;
        document.getElementById(set.abs).value  = ss.absolute   == null ? 999 : ss.absolute;
    }
}

function syncStackSizeInputsState() {
    const checked = document.querySelector('input[name="ssmode"]:checked');
    const mode = checked ? checked.value : 'none';
    for (const set of STACK_SIZE_SETS) {
        document.getElementById(set.mult).disabled = mode !== 'multiplier';
        document.getElementById(set.cap).disabled  = mode !== 'multiplier';
        document.getElementById(set.abs).disabled  = mode !== 'absolute';
        for (const r of document.querySelectorAll('input[name="' + set.name + '"]')) {
            r.disabled = false;
        }
    }
}

function syncPickupInputState() {
    const enabled = document.getElementById('pickup-enabled');
    const slider  = document.getElementById('pickup-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncPickupReadout() {
    const slider = document.getElementById('pickup-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('pickup-multiplier-value').textContent =
        mul.toFixed(1) + 'x';
    document.getElementById('pickup-range').textContent =
        (4.0 * mul).toFixed(1) + ' m';
}

function syncShipPickupInputState() {
    const enabled = document.getElementById('ship-pickup-enabled');
    const slider  = document.getElementById('ship-pickup-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncShipPickupReadout() {
    const slider = document.getElementById('ship-pickup-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('ship-pickup-multiplier-value').textContent =
        mul.toFixed(1) + 'x';
}

function syncCropOverlapInputState() {
    const enabled = document.getElementById('crop-overlap-enabled');
    const slider  = document.getElementById('crop-overlap-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncCropOverlapReadout() {
    const slider = document.getElementById('crop-overlap-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('crop-overlap-multiplier-value').textContent = mul.toFixed(2) + 'x';
    const pct = Math.round((mul - 1.0) * 100.0);
    document.getElementById('crop-overlap-readout').textContent =
        pct === 0 ? 'vanilla' : pct + '% footprint';
}

// Vanilla CT_CharactersAttributes player bases (Hero_MaxHealth / Hero_MaxStamina).
const VANILLA_HERO_HEALTH = 320;
const VANILLA_HERO_STAMINA = 150;

function syncPlayerStatsInputState() {
    const healthOn  = document.getElementById('player-stats-health-enabled').checked;
    const staminaOn = document.getElementById('player-stats-stamina-enabled').checked;
    document.getElementById('player-stats-health').disabled  = !healthOn;
    document.getElementById('player-stats-stamina').disabled = !staminaOn;
}

function syncPlayerStatsReadout() {
    const health  = parseFloat(document.getElementById('player-stats-health').value) || 1.0;
    const stamina = parseFloat(document.getElementById('player-stats-stamina').value) || 1.0;
    document.getElementById('player-stats-health-value').textContent = health.toFixed(1) + 'x';
    document.getElementById('player-stats-stamina-value').textContent = stamina.toFixed(1) + 'x';
    document.getElementById('player-stats-health-readout').textContent =
        Math.round(VANILLA_HERO_HEALTH * health) + ' HP';
    document.getElementById('player-stats-stamina-readout').textContent =
        Math.round(VANILLA_HERO_STAMINA * stamina) + ' stamina';
}

function syncBellInputState() {
    document.getElementById('bell-cap').disabled = false;
    document.getElementById('signal-fire-cap').disabled = false;
}

function syncBuildingStabilityInputState() {
    document.getElementById('building-stability-enabled').disabled = false;
}

function syncNoSmokeInputState() {
    document.getElementById('nosmoke-campfire').disabled = false;
    document.getElementById('nosmoke-furnace').disabled  = false;
    document.getElementById('nosmoke-kiln').disabled     = false;
}

function syncMinimapInputState() {
    const enabled = document.getElementById('minimap-enabled');
    const slider  = document.getElementById('minimap-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncMinimapReadout() {
    const slider = document.getElementById('minimap-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('minimap-multiplier-value').textContent = mul.toFixed(1) + 'x';
    const footDist  = 25 * mul / 10;
    const shipDist  = 75 * mul / 10;
    document.getElementById('minimap-foot-readout').textContent = footDist.toFixed(1) + ' m';
    document.getElementById('minimap-ship-readout').textContent = shipDist.toFixed(1) + ' m';
}

function syncBonfireInputState() {
    const enabled = document.getElementById('bonfire-enabled');
    const slider  = document.getElementById('bonfire-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncBonfireReadout() {
    const slider = document.getElementById('bonfire-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('bonfire-multiplier-value').textContent = mul.toFixed(1) + 'x';
    document.getElementById('bonfire-radius-readout').textContent = (mul * 50).toFixed(0) + ' m'
    document.getElementById('bonfire-height-readout').textContent = (mul * 30).toFixed(0) + ' m'
}

function syncPickaxeInputState() {
    const enabled = document.getElementById('pickaxe-enabled');
    const slider  = document.getElementById('pickaxe-multiplier');
    enabled.disabled = false;
    slider.disabled  = !enabled.checked;
}

function syncPickaxeReadout() {
    const slider = document.getElementById('pickaxe-multiplier');
    const mul = parseFloat(slider.value) || 1.0;
    document.getElementById('pickaxe-multiplier-value').textContent = mul.toFixed(1) + 'x';
    const pct = (mul - 1.0) * 100.0;
    const sign = pct >= 0 ? '+' : '';
    document.getElementById('pickaxe-readout').textContent = sign + pct.toFixed(0) + '%';
}

function setStackSizeFromUI(srcEvt) {
    if (!state.current) return;
    let src = STACK_SIZE_SETS[0];
    if (srcEvt && srcEvt.target) {
        const t = srcEvt.target;
        const found = STACK_SIZE_SETS.find(s =>
            t.name === s.name || t.id === s.mult || t.id === s.cap || t.id === s.abs);
        if (found) src = found;
    }
    const checked = document.querySelector('input[name="' + src.name + '"]:checked');
    const mode = checked ? checked.value : 'none';
    const mult = parseInt(document.getElementById(src.mult).value, 10);
    const cap  = parseInt(document.getElementById(src.cap).value,  10);
    const abs  = parseInt(document.getElementById(src.abs).value,  10);

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
    syncStackSizeUIFromState();
    syncStackSizeInputsState();
    markDirty();
    renderStatus();
    renderItems();
}

function setPickupRadiusFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('pickup-enabled').checked;
    const slider  = document.getElementById('pickup-multiplier');
    const mul     = parseFloat(slider.value) || 1.0;
    state.current.globals = state.current.globals || {};
    if (enabled && Math.abs(mul - 1.0) > 1e-9) {
        state.current.globals.pickupRadius = { multiplier: mul };
    } else {
        delete state.current.globals.pickupRadius;
    }
    syncPickupReadout();
    syncPickupInputState();
    markDirty();
}

function setShipPickupFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('ship-pickup-enabled').checked;
    const slider  = document.getElementById('ship-pickup-multiplier');
    const mul     = parseFloat(slider.value) || 1.0;
    state.current.globals = state.current.globals || {};
    if (enabled && Math.abs(mul - 1.0) > 1e-9) {
        state.current.globals.shipPickup = { multiplier: mul };
    } else {
        delete state.current.globals.shipPickup;
    }
    syncShipPickupReadout();
    syncShipPickupInputState();
    markDirty();
}

function setCropOverlapFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('crop-overlap-enabled').checked;
    const slider  = document.getElementById('crop-overlap-multiplier');
    const mul     = parseFloat(slider.value) || 1.0;
    state.current.globals = state.current.globals || {};
    if (enabled && Math.abs(mul - 1.0) > 1e-9) {
        state.current.globals.cropOverlap = { multiplier: mul };
    } else {
        delete state.current.globals.cropOverlap;
    }
    syncCropOverlapReadout();
    syncCropOverlapInputState();
    markDirty();
}

function setPlayerStatsFromUI() {
    if (!state.current) return;
    const healthOn  = document.getElementById('player-stats-health-enabled').checked;
    const staminaOn = document.getElementById('player-stats-stamina-enabled').checked;
    const health  = parseFloat(document.getElementById('player-stats-health').value) || 1.0;
    const stamina = parseFloat(document.getElementById('player-stats-stamina').value) || 1.0;
    state.current.globals = state.current.globals || {};
    const hMul = (healthOn  && Math.abs(health  - 1.0) > 1e-9) ? health  : 1.0;
    const sMul = (staminaOn && Math.abs(stamina - 1.0) > 1e-9) ? stamina : 1.0;
    if (Math.abs(hMul - 1.0) > 1e-9 || Math.abs(sMul - 1.0) > 1e-9) {
        state.current.globals.playerStats = { healthMultiplier: hMul, staminaMultiplier: sMul };
    } else {
        delete state.current.globals.playerStats;
    }
    syncPlayerStatsReadout();
    syncPlayerStatsInputState();
    markDirty();
}

function setBellLimitsFromUI() {
    if (!state.current) return;
    const bellRaw   = document.getElementById('bell-cap').value;
    const signalRaw = document.getElementById('signal-fire-cap').value;
    const bell   = parseInt(bellRaw,   10);
    const signal = parseInt(signalRaw, 10);
    if (!isFinite(bell) || !isFinite(signal)) return;

    state.current.globals = state.current.globals || {};
    const isVanillaBell   = bell === 10;
    const isVanillaSignal = signal === 3;
    if (isVanillaBell && isVanillaSignal) {
        delete state.current.globals.fastTravelBells;
    } else {
        state.current.globals.fastTravelBells = {
            bellCap: bell,
            signalFireCap: signal,
        };
    }
    markDirty();
}

function syncEquipmentSlotsReadout() {
    const ring = parseInt(document.getElementById('ring-slots').value, 10);
    const neck = parseInt(document.getElementById('necklace-slots').value, 10);
    const back = parseInt(document.getElementById('backpack-slots').value, 10);
    document.getElementById('ring-slots-value').textContent = isFinite(ring) ? ring : 1;
    document.getElementById('necklace-slots-value').textContent = isFinite(neck) ? neck : 1;
    document.getElementById('backpack-slots-value').textContent = isFinite(back) ? back : 1;
}

function setEquipmentSlotsFromUI() {
    if (!state.current) return;
    const ring = parseInt(document.getElementById('ring-slots').value, 10);
    const neck = parseInt(document.getElementById('necklace-slots').value, 10);
    const back = parseInt(document.getElementById('backpack-slots').value, 10);
    if (!isFinite(ring) || !isFinite(neck) || !isFinite(back)) return;
    syncEquipmentSlotsReadout();
    state.current.globals = state.current.globals || {};
    if (ring === 1 && neck === 1 && back === 1) {
        delete state.current.globals.equipmentSlots;
    } else {
        state.current.globals.equipmentSlots = {
            ringSlots: ring,
            necklaceSlots: neck,
            backpackSlots: back,
        };
    }
    markDirty();
}

function syncStorageSlotsReadout() {
    const inv = parseFloat(document.getElementById('player-inv-mult').value);
    const chest = parseFloat(document.getElementById('chest-slots-mult').value);
    document.getElementById('player-inv-mult-value').textContent =
        'x' + (isFinite(inv) ? inv : 1);
    document.getElementById('chest-slots-mult-value').textContent =
        'x' + (isFinite(chest) ? chest : 1);
}

function setStorageSlotsFromUI() {
    if (!state.current) return;
    const inv = parseFloat(document.getElementById('player-inv-mult').value);
    const chest = parseFloat(document.getElementById('chest-slots-mult').value);
    if (!isFinite(inv) || !isFinite(chest)) return;
    syncStorageSlotsReadout();
    state.current.globals = state.current.globals || {};
    const invOn = Math.abs(inv - 1.0) > 1e-9;
    const chestOn = Math.abs(chest - 1.0) > 1e-9;
    if (!invOn && !chestOn) {
        delete state.current.globals.storageSlots;
    } else {
        state.current.globals.storageSlots = {
            playerInventoryMultiplier: inv,
            chestSlotsMultiplier: chest,
        };
    }
    markDirty();
}

function syncShipSlotsReadout() {
    const mult = parseFloat(document.getElementById('ship-cargo-mult').value);
    const combat = parseInt(document.getElementById('ship-combat-slots').value, 10);
    document.getElementById('ship-cargo-mult-value').textContent =
        'x' + (isFinite(mult) ? mult : 1);
    document.getElementById('ship-combat-slots-value').textContent =
        isFinite(combat) ? combat : 1;
}

function setShipSlotsFromUI() {
    if (!state.current) return;
    const mult = parseFloat(document.getElementById('ship-cargo-mult').value);
    const combat = parseInt(document.getElementById('ship-combat-slots').value, 10);
    if (!isFinite(mult) || !isFinite(combat)) return;
    syncShipSlotsReadout();
    state.current.globals = state.current.globals || {};
    const cargoOn = Math.abs(mult - 1.0) > 1e-9;
    const combatOn = combat !== 1;
    if (!cargoOn && !combatOn) {
        delete state.current.globals.shipSlots;
    } else {
        state.current.globals.shipSlots = {
            cargoMultiplier: mult,
            combatOrderSlots: combat,
        };
    }
    markDirty();
}

// ---------------------------------------------------------------------------
// UI Scale. Unlike the other Misc sliders this is NOT profile-bound: it edits
// the per-user UE config directly (%LOCALAPPDATA%\R5\Saved\Config\Windows\
// Engine.ini -> [/Script/Engine.UserInterfaceSettings] ApplicationScale). So
// it never touches state.current / markDirty(); the slider mirrors the live
// file (GET on load) and an explicit Apply button writes it back (POST).
// ---------------------------------------------------------------------------
function syncUiScaleReadout() {
    const slider = document.getElementById('ui-scale');
    if (!slider) return;
    const v = parseFloat(slider.value);
    const pct = isFinite(v) ? Math.round(v * 100) : 100;
    document.getElementById('ui-scale-value').textContent = pct + '%';
}

function uiScaleSetStatus(msg) {
    const el = document.getElementById('ui-scale-status');
    if (el) el.textContent = msg || '';
}

async function loadUiScale() {
    const slider = document.getElementById('ui-scale');
    if (!slider) return;
    try {
        const res = await api('GET', '/api/uiscale');
        if (!res || !res.supported) {
            uiScaleSetStatus('No Windrose Engine.ini found yet - launch the game once.');
            syncUiScaleReadout();
            state.uiScaleModified = false;
            updateMiscTabIndicator();
            return;
        }
        let v = parseFloat(res.scale);
        if (!isFinite(v)) v = 1.0;
        if (v < 0.5) v = 0.5;
        if (v > 1.1) v = 1.1;
        slider.value = String(v);
        syncUiScaleReadout();
        uiScaleSetStatus(res.isSet
            ? 'Current: ' + Math.round(v * 100) + '%'
                + (res.readOnly ? ' (Engine.ini locked read-only).' : '.')
            : 'Not set yet (vanilla 100%).');
        // UI scale lives in Engine.ini (machine-wide, not profile globals), so
        // flag it for the Misc tab indicator: modified = set to a non-vanilla
        // (!= 100%) value.
        state.uiScaleModified = !!res.isSet && Math.abs(v - 1.0) > 1e-6;
        updateMiscTabIndicator();
    } catch (e) {
        uiScaleSetStatus('Could not read current UI scale: ' + e.message);
        syncUiScaleReadout();
        state.uiScaleModified = false;
        updateMiscTabIndicator();
    }
}

async function uiScaleApply() {
    const slider = document.getElementById('ui-scale');
    if (!slider) return;
    const v = parseFloat(slider.value) || 1.0;
    uiScaleSetStatus('Applying ' + Math.round(v * 100) + '%...');
    try {
        const res = await api('POST', '/api/uiscale', { scale: v });
        const applied = res.scale != null ? res.scale : v;
        const pct = Math.round(applied * 100);
        state.uiScaleModified = Math.abs(applied - 1.0) > 1e-6;
        updateMiscTabIndicator();
        uiScaleSetStatus('UI scale set to ' + pct + '%'
            + (res.readOnlySet
                ? ' and Engine.ini locked (read-only) so the game keeps it.'
                : '. WARNING: could not set Engine.ini read-only - the game may reset it on launch.')
            + ' Close Windrose first if it is running, then launch to see it.');
    } catch (e) {
        uiScaleSetStatus('Apply failed: ' + e.message);
    }
}

function setBuildingStabilityFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('building-stability-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.buildingStability = { enabled: true };
    } else {
        delete state.current.globals.buildingStability;
    }
    markDirty();
}

function setNoFogFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('nofog-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.noFog = { enabled: true };
    } else {
        delete state.current.globals.noFog;
    }
    markDirty();
}

function setPersistentLootFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('persistent-loot-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.persistentLoot = { enabled: true };
    } else {
        delete state.current.globals.persistentLoot;
    }
    markDirty();
}

function setKeepStatusFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('keep-status-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.keepStatus = { enabled: true };
    } else {
        delete state.current.globals.keepStatus;
    }
    markDirty();
}

function setShantyFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('shanty-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.shanty = { enabled: true };
    } else {
        delete state.current.globals.shanty;
    }
    markDirty();
}

function setItemSpawnerFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('itemspawner-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.itemSpawner = { enabled: true };
    } else {
        delete state.current.globals.itemSpawner;
    }
    markDirty();
}

function syncDepositVisualInputState() {
    document.getElementById('deposit-iron-texture').disabled =
        !document.getElementById('deposit-iron-enabled').checked;
    document.getElementById('deposit-sulfur-texture').disabled =
        !document.getElementById('deposit-sulfur-enabled').checked;
}

function applyDepositVisualToUI(p) {
    populateDepositVisualSelects();
    const dv = (p && p.globals && p.globals.depositVisual) || null;
    document.getElementById('deposit-iron-enabled').checked = !!(dv && dv.iron === true);
    setSelectValueIfPresent('deposit-iron-texture',
        (dv && dv.ironTexture) || depositDefaultTexture('iron'));
    document.getElementById('deposit-sulfur-enabled').checked = !!(dv && dv.sulfur === true);
    setSelectValueIfPresent('deposit-sulfur-texture',
        (dv && dv.sulfurTexture) || depositDefaultTexture('sulfur'));
    syncDepositVisualInputState();
}

function setDepositVisualFromUI() {
    if (!state.current) return;
    const ironOn    = document.getElementById('deposit-iron-enabled').checked;
    const ironTex   = document.getElementById('deposit-iron-texture').value || null;
    const sulfurOn  = document.getElementById('deposit-sulfur-enabled').checked;
    const sulfurTex = document.getElementById('deposit-sulfur-texture').value || null;
    state.current.globals = state.current.globals || {};
    if (ironOn || sulfurOn) {
        const dv = {};
        if (ironOn)   { dv.iron = true;   if (ironTex)   dv.ironTexture = ironTex; }
        if (sulfurOn) { dv.sulfur = true; if (sulfurTex) dv.sulfurTexture = sulfurTex; }
        state.current.globals.depositVisual = dv;
    } else {
        delete state.current.globals.depositVisual;
    }
    syncDepositVisualInputState();
    markDirty();
}

function setLandFastTravelFromUI() {
    if (!state.current) return;
    const enabled = document.getElementById('land-fast-travel-enabled').checked;
    state.current.globals = state.current.globals || {};
    if (enabled) {
        state.current.globals.landFastTravel = { enabled: true };
    } else {
        delete state.current.globals.landFastTravel;
    }
    markDirty();
}

function setMinimapRangeFromUI() {
    if (!state.current) return;
    syncMinimapReadout();
    syncMinimapInputState();
    const enabled = document.getElementById('minimap-enabled').checked;
    const mul = parseFloat(document.getElementById('minimap-multiplier').value);
    state.current.globals = state.current.globals || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete state.current.globals.minimapRange;
    } else {
        state.current.globals.minimapRange = { multiplier: mul };
    }
    markDirty();
}

function setBonfireRadiusFromUI() {
    if (!state.current) return;
    syncBonfireReadout();
    syncBonfireInputState();
    const enabled = document.getElementById('bonfire-enabled').checked;
    const mul = parseFloat(document.getElementById('bonfire-multiplier').value);
    state.current.globals = state.current.globals || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete state.current.globals.bonfireRadius;
    } else {
        state.current.globals.bonfireRadius = { multiplier: mul };
    }
    markDirty();
}

function setPickaxeRangeFromUI() {
    if (!state.current) return;
    syncPickaxeReadout();
    syncPickaxeInputState();
    const enabled = document.getElementById('pickaxe-enabled').checked;
    const mul = parseFloat(document.getElementById('pickaxe-multiplier').value);
    state.current.globals = state.current.globals || {};
    if (!enabled || !isFinite(mul) || Math.abs(mul - 1.0) < 1e-9) {
        delete state.current.globals.pickaxeRange;
    } else {
        state.current.globals.pickaxeRange = { multiplier: mul };
    }
    markDirty();
}

// Level Rewards (talent/stat points per level) lives entirely in tabs/leveling.js
// - including the two compact mirror sliders on this Basic card. See
// bindLevelingHandlers / applyLevelingToUI / syncLevelingMiscCard there.

function setNoSmokeFromUI() {
    if (!state.current) return;
    const c = document.getElementById('nosmoke-campfire').checked;
    const f = document.getElementById('nosmoke-furnace').checked;
    const k = document.getElementById('nosmoke-kiln').checked;
    state.current.globals = state.current.globals || {};
    if (!c && !f && !k) {
        delete state.current.globals.noSmoke;
    } else {
        const ns = {};
        if (c) ns.campfire = true;
        if (f) ns.furnace = true;
        if (k) ns.kiln = true;
        state.current.globals.noSmoke = ns;
    }
    markDirty();
}

function setBuildingRotationFromUI() {
    if (!state.current) return;
    const a1  = document.getElementById('building-rot-1').checked;
    const a5  = document.getElementById('building-rot-5').checked;
    const a10 = document.getElementById('building-rot-10').checked;
    state.current.globals = state.current.globals || {};
    if (!a1 && !a5 && !a10) {
        delete state.current.globals.buildingRotation;
    } else {
        const br = {};
        if (a1)  br.add1 = true;
        if (a5)  br.add5 = true;
        if (a10) br.add10 = true;
        state.current.globals.buildingRotation = br;
    }
    markDirty();
}

// ---------------------------------------------------------------------------
// Bonfire / building-center hearth-music ("The Hearth") replacement.
//
// Single-slot SWAV swap. The card on the Misc tab carries:
//   - A file-input (multi-format accept) that POSTs to
//     /api/profiles/{id}/bonfire-music with the audio bytes.
//   - A status line that reflects the on-disk + profile state:
//       * "vanilla"   - no upload yet, vanilla "The Hearth" plays
//       * "ready"     - WAV on disk + filename in profile; build picks it up
//       * "broken"    - filename in profile but WAV missing on disk
//   - A "Clear" link that DELETEs the upload.
//
// Unlike the volume / multiplier cards on this tab, this one auto-saves
// the audio bytes server-side (the upload POST persists the WAV +
// updates BonfireMusicGlobal.OriginalFilename atomically). The status
// refresh is best-effort and never marks the profile dirty: the
// underlying bytes ARE the persistence, the in-memory state.current is
// just a mirror.
// ---------------------------------------------------------------------------
function bonfireMusicClearButton() {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn-link danger';
    btn.id = 'bonfire-music-clear';
    btn.textContent = 'Clear';
    return btn;
}

function buildBonfireMusicStatus(meta) {
    const frag = document.createDocumentFragment();
    const span = document.createElement('span');
    let withClear = true;
    if (!meta || meta.state === 'vanilla') {
        span.className = 'bonfire-music-vanilla';
        span.textContent = 'No custom audio - vanilla "The Hearth" plays.';
        withClear = false;
    } else if (meta.state === 'muted-vanilla') {
        // No upload, slider at 0%: the build synthesizes a silence SWAV.
        span.className = 'bonfire-music-muted';
        span.textContent = 'Vanilla "The Hearth" will be muted on next build (Volume 0%).';
    } else if (meta.state === 'broken') {
        span.className = 'bonfire-music-broken';
        span.textContent = 'Filename "' + (meta.originalFilename || '')
            + '" is set on the profile but its audio.wav is missing - re-upload or clear.';
    } else {
        let sizeNote = '';
        if (typeof meta.wavBytes === 'number' && meta.wavBytes > 0) {
            sizeNote = ' (' + Math.round(meta.wavBytes / 1024) + ' KB)';
        }
        span.className = 'bonfire-music-ready';
        span.textContent = 'Custom: ' + (meta.originalFilename || '') + sizeNote;
    }
    frag.appendChild(span);
    if (withClear) {
        frag.appendChild(document.createTextNode(' '));
        frag.appendChild(bonfireMusicClearButton());
    }
    return frag;
}

// Renders the status line + binds the Clear handler. Called from
// applyProfileToUI() on profile load and from upload/clear handlers
// after they mutate server-side.
function renderBonfireMusicStatus(meta) {
    const el = document.getElementById('bonfire-music-status');
    if (!el) return;
    el.replaceChildren(buildBonfireMusicStatus(meta));
    const clearBtn = document.getElementById('bonfire-music-clear');
    if (clearBtn) {
        clearBtn.addEventListener('click', clearBonfireMusic);
    }
}

// Pulls fresh meta from the server. Best-effort: a failure just leaves
// the existing status line in place (the user can re-upload to fix any
// stale rendering).
async function refreshBonfireMusicStatus() {
    if (!state.current || !state.current.id) {
        renderBonfireMusicStatus(null);
        return;
    }
    try {
        const meta = await api('GET',
            '/api/profiles/' + encodeURIComponent(state.current.id) + '/bonfire-music');
        renderBonfireMusicStatus(meta);
    } catch (_) {
        // Fall back to whatever the profile carries inline so the user
        // sees something rather than a blank card on a transient error.
        const bm = state.current.globals && state.current.globals.bonfireMusic;
        renderBonfireMusicStatus(bm
            ? { state: 'broken', originalFilename: bm.originalFilename }
            : null);
    }
}

async function onBonfireMusicFileChange(e) {
    const t = e.target;
    if (!t || t.tagName !== 'INPUT' || t.type !== 'file') return;
    if (!t.files || t.files.length === 0) return;
    const file = t.files[0];
    if (!state.current || !state.current.id) {
        await alert('Save the profile first so it has an id, then upload.');
        try { t.value = ''; } catch (_) {}
        return;
    }
    const statusEl = document.getElementById('bonfire-music-status');
    if (statusEl) {
        const em = document.createElement('em');
        em.textContent = 'Uploading + transcoding ' + file.name + '...';
        statusEl.replaceChildren(em);
    }
    try {
        const form = new FormData();
        form.append('audio', file, file.name);
        const url = '/api/profiles/' + encodeURIComponent(state.current.id) + '/bonfire-music';
        const resp = await fetch(url, { method: 'POST', body: form });
        if (!resp.ok) {
            let err = 'HTTP ' + resp.status;
            try { const j = await resp.json(); if (j && j.error) err = j.error; } catch (_) {}
            throw new Error(err);
        }
        const dto = await resp.json();
        // Mirror the server-side mutation into state.current so a
        // subsequent loadProfile() round-trip would be a no-op. The
        // upload itself is auto-saved, so we do NOT call markDirty().
        state.current.globals = state.current.globals || {};
        state.current.globals.bonfireMusic = {
            originalFilename: dto.originalFilename,
            volume: (state.current.globals.bonfireMusic
                  && state.current.globals.bonfireMusic.volume) || null,
        };
        renderBonfireMusicStatus({
            state: 'custom',
            originalFilename: dto.originalFilename,
            wavBytes: dto.wavBytes,
        });
        // Auto-saved (no markDirty), so refresh the Misc tab indicator here.
        updateMiscTabIndicator();
    } catch (err) {
        await alert('Bonfire-music upload failed: ' + (err && err.message ? err.message : err));
        // Reload meta to recover the displayed state.
        refreshBonfireMusicStatus();
    } finally {
        try { t.value = ''; } catch (_) {}
    }
}

// Reads the current bonfire-music volume from state.current and parks
// it on the slider. Default 100% (= 1.0 absolute, vanilla baseline) when
// the profile carries no Volume yet. Range exposed to the UI: 0..100,
// where 0 = digital silence (mute, baked into the SWAV samples at build
// time) and 100 = unchanged. Called from applyProfileToUI() so a
// profile switch / load repaints the slider to its persisted value.
function syncBonfireMusicVolumeFromState() {
    const slider = document.getElementById('bonfire-music-volume');
    if (!slider) return;
    const bm = state.current && state.current.globals
        && state.current.globals.bonfireMusic;
    const v = (bm && typeof bm.volume === 'number') ? bm.volume : 1.0;
    let pct = Math.round(v * 100);
    if (!isFinite(pct) || pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    slider.value = String(pct);
    syncBonfireMusicVolumeReadout();
}

function syncBonfireMusicVolumeReadout() {
    const slider = document.getElementById('bonfire-music-volume');
    const out = document.getElementById('bonfire-music-volume-value');
    if (!slider || !out) return;
    out.textContent = (parseFloat(slider.value) || 0).toFixed(0) + '%';
}

// Slider onInput / onChange: mirror the new value into
// state.current.globals.bonfireMusic.volume + markDirty(). The value
// rides in the global PUT body alongside every other profile field and
// gets applied at build time as a pre-encode ffmpeg PCM gain. No POST
// here - we deliberately stay consistent with the ship-music sliders
// (Discard would otherwise have nothing to roll back to).
function setBonfireMusicVolumeFromUI() {
    if (!state.current) return;
    syncBonfireMusicVolumeReadout();
    const slider = document.getElementById('bonfire-music-volume');
    if (!slider) return;
    let pct = parseFloat(slider.value);
    if (!isFinite(pct) || pct < 0) pct = 0;
    if (pct > 100) pct = 100;
    const mul = pct / 100.0;
    state.current.globals = state.current.globals || {};
    // Volume is meaningful even when no audio.wav is uploaded yet (it
    // would silence vanilla "The Hearth" once the user does upload),
    // so we materialize the BonfireMusic node when the slider moves
    // away from the default. At exactly 1.0 we leave the node null /
    // intact to keep "vanilla" profiles minimal in the JSON.
    if (Math.abs(mul - 1.0) < 1e-6) {
        if (state.current.globals.bonfireMusic) {
            state.current.globals.bonfireMusic.volume = null;
        }
    } else {
        if (!state.current.globals.bonfireMusic) {
            state.current.globals.bonfireMusic = {
                originalFilename: null,
                volume: mul,
            };
        } else {
            state.current.globals.bonfireMusic.volume = mul;
        }
    }
    // Re-render the status line client-side so the user sees the
    // muted-vanilla / vanilla transition immediately when dragging the
    // slider to / away from 0% (instead of having to Save first and
    // wait for the GET to return the new state). Mirrors the state
    // machine the server uses in /api/profiles/{id}/bonfire-music GET.
    refreshBonfireMusicStatusLocally();
    markDirty();
}

// Computes a status meta object from the in-memory state.current and
// renders it. Used after slider drags so the muted-vanilla / vanilla /
// custom transitions show up immediately without an HTTP round-trip.
function refreshBonfireMusicStatusLocally() {
    const bm = state.current && state.current.globals
        && state.current.globals.bonfireMusic;
    if (!bm) {
        renderBonfireMusicStatus({ state: 'vanilla' });
        return;
    }
    const hasFilename = !!bm.originalFilename;
    const vol = (typeof bm.volume === 'number') ? bm.volume : 1.0;
    if (hasFilename) {
        // Don't downgrade a "custom"-rendered card with a real upload
        // to "broken" just because we lack the wavBytes here - the next
        // server refresh will reconcile. Render as custom with no size.
        renderBonfireMusicStatus({
            state: 'custom',
            originalFilename: bm.originalFilename,
        });
        return;
    }
    if (vol <= 1e-4) {
        renderBonfireMusicStatus({ state: 'muted-vanilla' });
        return;
    }
    renderBonfireMusicStatus({ state: 'vanilla' });
}

async function clearBonfireMusic() {
    if (!state.current || !state.current.id) return;
    const ok = await confirm('Remove custom bonfire music and revert to vanilla "The Hearth"?');
    if (!ok) return;
    try {
        const url = '/api/profiles/' + encodeURIComponent(state.current.id) + '/bonfire-music';
        const resp = await fetch(url, { method: 'DELETE' });
        if (!resp.ok && resp.status !== 204) {
            let err = 'HTTP ' + resp.status;
            try { const j = await resp.json(); if (j && j.error) err = j.error; } catch (_) {}
            throw new Error(err);
        }
        if (state.current.globals) state.current.globals.bonfireMusic = null;
        renderBonfireMusicStatus(null);
        // Auto-saved (no markDirty), so refresh the Misc tab indicator here.
        updateMiscTabIndicator();
    } catch (err) {
        await alert('Could not clear bonfire music: ' + (err && err.message ? err.message : err));
    }
}

function bindMiscHandlers() {
    for (const set of STACK_SIZE_SETS) {
        for (const r of document.querySelectorAll('input[name="' + set.name + '"]')) {
            r.addEventListener('change', setStackSizeFromUI);
        }
        document.getElementById(set.mult).addEventListener('input', setStackSizeFromUI);
        document.getElementById(set.cap).addEventListener('input',  setStackSizeFromUI);
        document.getElementById(set.abs).addEventListener('input',  setStackSizeFromUI);
    }
    document.getElementById('pickup-enabled').addEventListener('change', setPickupRadiusFromUI);
    document.getElementById('pickup-multiplier').addEventListener('input', setPickupRadiusFromUI);
    document.getElementById('ship-pickup-enabled').addEventListener('change', setShipPickupFromUI);
    document.getElementById('ship-pickup-multiplier').addEventListener('input', setShipPickupFromUI);
    document.getElementById('crop-overlap-enabled').addEventListener('change', setCropOverlapFromUI);
    document.getElementById('crop-overlap-multiplier').addEventListener('input', setCropOverlapFromUI);
    document.getElementById('player-stats-health-enabled').addEventListener('change', setPlayerStatsFromUI);
    document.getElementById('player-stats-stamina-enabled').addEventListener('change', setPlayerStatsFromUI);
    document.getElementById('player-stats-health').addEventListener('input', setPlayerStatsFromUI);
    document.getElementById('player-stats-stamina').addEventListener('input', setPlayerStatsFromUI);
    document.getElementById('bell-cap').addEventListener('input', setBellLimitsFromUI);
    document.getElementById('signal-fire-cap').addEventListener('input', setBellLimitsFromUI);
    document.getElementById('ring-slots').addEventListener('input', setEquipmentSlotsFromUI);
    document.getElementById('necklace-slots').addEventListener('input', setEquipmentSlotsFromUI);
    document.getElementById('backpack-slots').addEventListener('input', setEquipmentSlotsFromUI);
    document.getElementById('player-inv-mult').addEventListener('input', setStorageSlotsFromUI);
    document.getElementById('chest-slots-mult').addEventListener('input', setStorageSlotsFromUI);
    document.getElementById('ship-cargo-mult').addEventListener('input', setShipSlotsFromUI);
    document.getElementById('ship-combat-slots').addEventListener('input', setShipSlotsFromUI);
    document.getElementById('building-stability-enabled').addEventListener('change',
        setBuildingStabilityFromUI);
    document.getElementById('nosmoke-campfire').addEventListener('change', setNoSmokeFromUI);
    document.getElementById('nosmoke-furnace').addEventListener('change',  setNoSmokeFromUI);
    document.getElementById('nosmoke-kiln').addEventListener('change',     setNoSmokeFromUI);
    document.getElementById('building-rot-1').addEventListener('change',  setBuildingRotationFromUI);
    document.getElementById('building-rot-5').addEventListener('change',  setBuildingRotationFromUI);
    document.getElementById('building-rot-10').addEventListener('change', setBuildingRotationFromUI);
    document.getElementById('minimap-enabled').addEventListener('change', setMinimapRangeFromUI);
    document.getElementById('minimap-multiplier').addEventListener('input', setMinimapRangeFromUI);
    document.getElementById('nofog-enabled').addEventListener('change', setNoFogFromUI);
    document.getElementById('persistent-loot-enabled').addEventListener('change', setPersistentLootFromUI);
    document.getElementById('keep-status-enabled').addEventListener('change', setKeepStatusFromUI);
    document.getElementById('shanty-enabled').addEventListener('change', setShantyFromUI);
    document.getElementById('itemspawner-enabled').addEventListener('change', setItemSpawnerFromUI);
    document.getElementById('deposit-iron-enabled').addEventListener('change', setDepositVisualFromUI);
    document.getElementById('deposit-iron-texture').addEventListener('change', setDepositVisualFromUI);
    document.getElementById('deposit-sulfur-enabled').addEventListener('change', setDepositVisualFromUI);
    document.getElementById('deposit-sulfur-texture').addEventListener('change', setDepositVisualFromUI);
    // Catalog is static: load once, fill the dropdowns, then re-apply the current
    // profile so the persisted selection sticks even if it arrived before the fetch.
    loadDepositVisualCatalog().then(() => applyDepositVisualToUI(state.current));
    document.getElementById('land-fast-travel-enabled').addEventListener('change', setLandFastTravelFromUI);
    document.getElementById('bonfire-enabled').addEventListener('change', setBonfireRadiusFromUI);
    document.getElementById('bonfire-multiplier').addEventListener('input', setBonfireRadiusFromUI);
    document.getElementById('pickaxe-enabled').addEventListener('change', setPickaxeRangeFromUI);
    document.getElementById('pickaxe-multiplier').addEventListener('input', setPickaxeRangeFromUI);
    // Level Rewards mirror sliders are bound in bindLevelingHandlers (leveling.js).
    const bmInput = document.getElementById('bonfire-music-upload');
    if (bmInput) bmInput.addEventListener('change', onBonfireMusicFileChange);
    const bmVol = document.getElementById('bonfire-music-volume');
    if (bmVol) bmVol.addEventListener('input', setBonfireMusicVolumeFromUI);
    const uiScale = document.getElementById('ui-scale');
    if (uiScale) uiScale.addEventListener('input', syncUiScaleReadout);
    const uiScaleApplyBtn = document.getElementById('ui-scale-apply');
    if (uiScaleApplyBtn) uiScaleApplyBtn.addEventListener('click', uiScaleApply);
    // UI scale is install-global (not profile-bound): read the live value once.
    loadUiScale();
}
