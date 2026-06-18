'use strict';

// Characters tab - unified existing-character save patcher.
//
// The paks (Equipment Slots, Ship Slots, Level Rewards) only affect NEWLY
// created characters / ships. This tab retro-fits the same values onto an
// EXISTING character's RocksDB save: ring / necklace slots, every owned ship's
// cargo / combat-order slots, and the Level Rewards talent / stat points.
//
// Each character is ONE card. Every patchable area is compared against the
// active profile's target (the Basic / Level Rewards tab sliders). When ANY area
// differs the single "Backup + patch" button activates; pressing it sends only
// the differing areas to the one /api/savegame/patch endpoint, which writes just
// those. Areas already at target are left untouched.

// ---------------------------------------------------------------------------
// Targets - all derived from the ACTIVE PROFILE, never from the save, so they
// track even unsaved slider edits (and downgrade a previously-patched save back
// toward vanilla when a slider is lowered).
// ---------------------------------------------------------------------------

function charEquipTarget() {
    const eqs = state.current && state.current.globals && state.current.globals.equipmentSlots;
    const sto = state.current && state.current.globals && state.current.globals.storageSlots;
    const invMult = sto && sto.playerInventoryMultiplier != null ? sto.playerInventoryMultiplier : 1;
    const bpSlotsMult = sto && sto.backpackSlotsMultiplier != null ? sto.backpackSlotsMultiplier : 1;
    return {
        ring: eqs && eqs.ringSlots != null ? eqs.ringSlots : 1,
        neck: eqs && eqs.necklaceSlots != null ? eqs.necklaceSlots : 1,
        back: eqs && eqs.backpackSlots != null ? eqs.backpackSlots : 1,
        defSlots: Math.max(16, Math.round(16 * invMult)),
        bpSlotsMult: bpSlotsMult,
    };
}

function charShipTarget() {
    const ss = state.current && state.current.globals && state.current.globals.shipSlots;
    return {
        mult: ss && ss.cargoMultiplier != null ? ss.cargoMultiplier : 1,
        combat: ss && ss.combatOrderSlots != null ? ss.combatOrderSlots : 1,
    };
}

// Mirrors ShipSlotsPatcher.CargoTarget (round away from zero, never below
// vanilla, capped). Math.round rounds .5 up, matching AwayFromZero for the
// non-negative values here.
function shipCargoTarget(base, mult) {
    if (!base || base <= 0) return base || 0;
    let t = Math.round(base * mult);
    if (t < base) t = base;
    if (t > 200) t = 200;
    return t;
}

function shipTypeLabel(sourceDa) {
    if (!sourceDa) return 'unknown';
    return sourceDa.replace(/^DA_ShipInventory_/, '');
}

function shipLabel(s) {
    return 'Ship ' + (s.shipName ? '"' + s.shipName + '"' : shipTypeLabel(s.sourceDa));
}

// Cumulative mod reward across the level-ups a character has already had: levels
// 2..charLevel (the level-1 / starting row grants nothing). Mirrors leveling.js
// / LevelingPatcher. Returns null until the vanilla catalog loads.
function cumulativeModReward(charLevel, dim) {
    if (!levelingCatalog) return null;
    const isStat = dim === 'stat';
    const mul = getLevOverall(isStat);
    let sum = 0;
    for (const e of levelingCatalog) {
        if (e.isStarting) continue;       // level 1 starting row - never granted
        if (e.level > charLevel) break;   // catalog is level-ascending
        const vanilla = isStat ? e.vanillaStat : e.vanillaTalent;
        const ov = getLevelOverrideField(e.level, dim);
        sum += (ov != null) ? ov : applyHybridJs(vanilla, mul);
    }
    return sum;
}

// Absolute target FREE pools: free = max(0, modCumulative - spent). The patch is
// bidirectional - lowering the multiplier (e.g. back to vanilla 1x) reduces the
// free pool again; invested nodes are never touched, so spent points cannot be
// clawed back below 0. null until the catalog loads.
function progressionTargetFor(prog) {
    if (!levelingCatalog || !prog) return null;
    const modTalent = cumulativeModReward(prog.characterLevel, 'talent');
    const modStat = cumulativeModReward(prog.characterLevel, 'stat');
    if (modTalent == null || modStat == null) return null;
    return {
        talent: Math.max(0, modTalent - prog.spentTalent),
        stat: Math.max(0, modStat - prog.spentStat),
    };
}

// ---------------------------------------------------------------------------
// Per-area "needs patch" (each mirrors its backend patcher's match rule: BOTH
// the live count AND the blueprint must equal the target).
// ---------------------------------------------------------------------------

const BACKPACK_TIERS = [4, 8, 12, 16, 20, 1000];
function snapToVanillaTier(extra) {
    let best = BACKPACK_TIERS[0], bestDiff = Math.abs(extra - best);
    for (const t of BACKPACK_TIERS) {
        const d = Math.abs(extra - t);
        if (d < bestDiff) { best = t; bestDiff = d; }
    }
    return best;
}

function equipNeedsPatch(eq, t) {
    if (!eq) return false;
    if (eq.ringSlots !== t.ring || eq.necklaceSlots !== t.neck
        || eq.backpackSlots !== t.back
        || eq.blueprintRing !== t.ring || eq.blueprintNeck !== t.neck
        || eq.blueprintBack !== t.back
        || (eq.blueprintDefault != null && eq.blueprintDefault !== t.defSlots))
        return true;
    // Backpack extra slots: compare actual vs what the multiplier would produce
    const extra = eq.backpackExtraSlots || 0;
    if (!eq.hasBackpackEquipped) {
        // No backpack equipped - any extra slots are orphaned and need removal.
        if (extra > 0) return true;
    } else if (extra > 0) {
        const mult = t.bpSlotsMult || 1;
        const expected = Math.round(snapToVanillaTier(extra) * mult);
        if (extra !== expected) return true;
    }
    return false;
}

function shipNeedsPatch(s, t) {
    if (!s || !s.supported) return false;
    const tc = shipCargoTarget(s.vanillaCargoBase, t.mult);
    return s.cargoSlots !== tc || s.blueprintCargo !== tc
        || s.combatSlots !== t.combat || s.blueprintCombat !== t.combat;
}

function progressionNeedsPatch(prog, target) {
    if (!prog || !target) return false;
    return target.talent !== prog.freeTalent || target.stat !== prog.freeStat;
}

// Does anything at all on this character differ from the profile target?
function characterNeedsPatch(c) {
    const eqT = charEquipTarget();
    const shipT = charShipTarget();
    const progT = progressionTargetFor(c.progression);
    if (equipNeedsPatch(c.equipment, eqT)) return true;
    if (progressionNeedsPatch(c.progression, progT)) return true;
    return (c.ships || []).some(s => shipNeedsPatch(s, shipT));
}

// ---------------------------------------------------------------------------
// Load + render
// ---------------------------------------------------------------------------

function charGlobalStatus(msg) {
    const el = document.getElementById('char-global-status');
    if (el) el.textContent = msg || '';
}

async function loadCharacters() {
    state.characters.loaded = true;
    charGlobalStatus('Scanning save profiles...');
    const cs = state.characters;
    try {
        const res = await api('GET', '/api/savegame/characters');
        cs.supported = !!res.supported;
        cs.list = res.characters || [];
        cs.error = null;
    } catch (e) {
        cs.supported = true; cs.list = []; cs.error = e.message;
    }
    // The Level Rewards target needs the vanilla per-level table; load it before
    // rendering so the rows show targets without a "loading" flash.
    ensureLevelingCatalog().finally(() => renderCharacters());
}

function renderCharacters() {
    const host = document.getElementById('char-list');
    if (!host) return;
    const cs = state.characters;
    host.replaceChildren();

    if (cs.error) {
        charGlobalStatus('Scan failed: ' + cs.error + ' (make sure Windrose is fully closed).');
        return;
    }
    if (!cs.supported) {
        charGlobalStatus('No Windrose save profiles found on this machine.');
        return;
    }
    if (!cs.list.length) {
        charGlobalStatus('No characters found.');
        return;
    }
    // The Level Rewards target needs the level table; wait for it before drawing
    // rows so progression lines don't flash stale.
    if (!levelingCatalog) {
        charGlobalStatus('Loading level table...');
        ensureLevelingCatalog().then(() => renderCharacters());
        return;
    }

    const pending = cs.list.filter(characterNeedsPatch).length;
    charGlobalStatus('Found ' + cs.list.length + ' character(s). '
        + (pending ? pending + ' need patching to match the profile.' : 'All up to date.'));

    for (const c of cs.list) host.appendChild(buildCharacterCard(c));
}

function buildCharacterCard(c) {
    const eqT = charEquipTarget();
    const shipT = charShipTarget();
    const progT = progressionTargetFor(c.progression);
    const needs = characterNeedsPatch(c);

    const card = document.createElement('section');
    card.className = 'card char-card' + (needs ? ' char-needs-patch' : '');

    const content = document.createElement('div');
    content.className = 'card-content';

    const h = document.createElement('h2');
    h.textContent = (c.playerName || c.characterId)
        + (c.progression ? '  (Lv ' + c.progression.characterLevel + ')' : '');
    content.appendChild(h);

    // One line per patchable area; changed lines get an accent + arrow.
    const areas = document.createElement('div');
    areas.className = 'char-areas';

    if (c.equipment) {
        const bpExtra = c.equipment.backpackExtraSlots || 0;
        const bpExtraSuffix = bpExtra > 0 ? ' / bp slots +' + bpExtra : '';
        const bpExtraTarget = (bpExtra > 0 && c.equipment.hasBackpackEquipped)
            ? Math.round(snapToVanillaTier(bpExtra) * (eqT.bpSlotsMult || 1)) : 0;
        const bpExtraTargetSuffix = bpExtraTarget > 0
            ? ' / bp slots +' + bpExtraTarget : '';
        const eqNow = 'rings ' + c.equipment.ringSlots
            + ' / necklaces ' + c.equipment.necklaceSlots
            + ' / backpacks ' + c.equipment.backpackSlots
            + ' / inventory ' + (c.equipment.blueprintDefault || 16)
            + bpExtraSuffix;
        const eqTarget = 'rings ' + eqT.ring
            + ' / necklaces ' + eqT.neck
            + ' / backpacks ' + eqT.back
            + ' / inventory ' + eqT.defSlots
            + bpExtraTargetSuffix;
        areas.appendChild(areaLine(equipNeedsPatch(c.equipment, eqT), 'Equipment', eqNow, eqTarget));
    }
    if (c.progression) {
        const tt = progT ? progT.talent : c.progression.freeTalent;
        const ts = progT ? progT.stat : c.progression.freeStat;
        areas.appendChild(areaLine(progressionNeedsPatch(c.progression, progT), 'Level rewards',
            c.progression.freeTalent + ' talent / ' + c.progression.freeStat + ' stat free',
            tt + ' talent / ' + ts + ' stat free'));
    }
    for (const s of (c.ships || [])) {
        if (!s.supported) {
            areas.appendChild(areaInfoLine(shipLabel(s),
                'cargo ' + s.cargoSlots + ' / combat ' + s.combatSlots
                + ' - not a supported ship type (left at vanilla)'));
            continue;
        }
        const tc = shipCargoTarget(s.vanillaCargoBase, shipT.mult);
        areas.appendChild(areaLine(shipNeedsPatch(s, shipT), shipLabel(s),
            'cargo ' + s.cargoSlots + ' / combat ' + s.combatSlots,
            'cargo ' + tc + ' / combat ' + shipT.combat));
    }
    content.appendChild(areas);

    const row = document.createElement('div');
    row.className = 'bell-row';
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn-secondary';

    const st = document.createElement('p');
    st.className = 'hint';
    st.style.margin = '.4em 0';

    if (needs) {
        btn.textContent = 'Backup + patch';
        btn.addEventListener('click', () => patchCharacter(c, btn, st));
        if (c.lastStatus) st.textContent = c.lastStatus;
    } else {
        btn.textContent = 'Up to date';
        btn.disabled = true;
        st.textContent = c.lastStatus || 'Everything already matches the profile - nothing to do.';
    }

    row.appendChild(btn);
    content.appendChild(row);
    content.appendChild(st);
    card.appendChild(content);
    return card;
}

// A "current -> target" line; flagged with an accent + arrow when it will change.
function areaLine(changed, label, current, target) {
    const p = document.createElement('p');
    p.className = 'hint char-current char-area' + (changed ? ' char-area-changed' : '');
    p.textContent = changed
        ? label + ': ' + current + '  →  ' + target
        : label + ': ' + current;
    return p;
}

// A non-actionable info line (e.g. an unsupported ship type).
function areaInfoLine(label, text) {
    const p = document.createElement('p');
    p.className = 'hint char-current char-area';
    p.textContent = label + ': ' + text;
    return p;
}

// ---------------------------------------------------------------------------
// Patch - send only the differing areas to the one endpoint.
// ---------------------------------------------------------------------------

function buildPatchRequest(c, force) {
    const eqT = charEquipTarget();
    const shipT = charShipTarget();
    const progT = progressionTargetFor(c.progression);

    const req = { dbFolder: c.dbFolder, force: !!force };
    if (equipNeedsPatch(c.equipment, eqT))
        req.equipment = {
            ringSlots: eqT.ring, necklaceSlots: eqT.neck,
            backpackSlots: eqT.back, playerInventorySlots: eqT.defSlots,
            backpackSlotsMultiplier: eqT.bpSlotsMult || 1.0,
        };
    if (progressionNeedsPatch(c.progression, progT))
        req.progression = { talentPoints: progT.talent, statPoints: progT.stat };
    const ships = (c.ships || []).filter(s => shipNeedsPatch(s, shipT))
        .map(s => ({ shipKey: s.shipKey, cargoMultiplier: shipT.mult, combatOrderSlots: shipT.combat }));
    if (ships.length) req.ships = ships;
    return req;
}

async function patchCharacter(c, btn, st) {
    btn.disabled = true;
    st.textContent = 'Backing up + patching...';
    // Snapshot the request once so a forced retry re-sends exactly the same areas
    // (after a partial apply the recomputed "needs" would otherwise shrink).
    const req = buildPatchRequest(c, false);
    await runCharacterPatch(c, req, btn, st);
}

async function runCharacterPatch(c, req, btn, st) {
    try {
        const res = await api('POST', '/api/savegame/patch', req);

        if (res.blocked) {
            const ok = await confirm(
                'Some slots would shrink and delete equipped / loaded items:\n\n'
                + (res.blockingItems || []).join('\n')
                + '\n\nDelete them and patch anyway?');
            if (ok) {
                req.force = true;
                return runCharacterPatch(c, req, btn, st);
            }
            st.textContent = 'Cancelled - unequip / empty those slots in-game first.';
            btn.disabled = false;
            return;
        }

        applyPatchResultToChar(c, res);
        c.lastStatus = describePatchResult(c, res);
        renderCharacters();
    } catch (e) {
        st.textContent = 'Patch failed: ' + e.message;
        btn.disabled = false;
    }
}

// Reflect the new on-disk values into the in-memory character so the card flips
// to "Up to date" without a full re-scan.
function applyPatchResultToChar(c, res) {
    const eq = res.equipment;
    if (eq && eq.applied && c.equipment) {
        if (eq.newRing != null) { c.equipment.ringSlots = eq.newRing; c.equipment.blueprintRing = eq.newRing; }
        if (eq.newNeck != null) { c.equipment.necklaceSlots = eq.newNeck; c.equipment.blueprintNeck = eq.newNeck; }
        if (eq.newBack != null) { c.equipment.backpackSlots = eq.newBack; c.equipment.blueprintBack = eq.newBack; }
        if (eq.newDefault != null && eq.newDefault > 0) { c.equipment.blueprintDefault = eq.newDefault; }
        if (eq.newBackpackExtraSlots != null) c.equipment.backpackExtraSlots = eq.newBackpackExtraSlots;
    }
    const pr = res.progression;
    if (pr && pr.applied && c.progression) {
        if (pr.newTalent != null) c.progression.freeTalent = pr.newTalent;
        if (pr.newStat != null) c.progression.freeStat = pr.newStat;
    }
    for (const sr of (res.ships || [])) {
        if (!sr.applied) continue;
        const s = (c.ships || []).find(x => x.shipKey === sr.shipKey);
        if (!s) continue;
        if (sr.newCargo != null) { s.cargoSlots = sr.newCargo; s.blueprintCargo = sr.newCargo; }
        if (sr.newCombat != null) { s.combatSlots = sr.newCombat; s.blueprintCombat = sr.newCombat; }
    }
}

function describePatchResult(c, res) {
    const parts = [];
    const eq = res.equipment;
    if (eq && eq.applied)
        parts.push('equipment ' + eq.oldRing + '/' + eq.oldNeck + ' → ' + eq.newRing + '/' + eq.newNeck);
    const pr = res.progression;
    if (pr && pr.applied)
        parts.push('talent ' + pr.oldTalent + ' → ' + pr.newTalent
            + ', stat ' + pr.oldStat + ' → ' + pr.newStat);
    for (const sr of (res.ships || [])) {
        if (!sr.applied) continue;
        const name = sr.shipName ? '"' + sr.shipName + '"' : 'ship';
        parts.push(name + ' cargo ' + sr.oldCargo + ' → ' + sr.newCargo
            + ', combat ' + sr.oldCombat + ' → ' + sr.newCombat);
    }

    if (!parts.length)
        return (res.playerName || c.playerName) + ' already matched - nothing changed.';

    return 'Patched ' + (res.playerName || c.playerName) + ': ' + parts.join('; ')
        + (res.backupCreated ? ' (backup saved)' : '')
        + (res.checkpointZipRebuilt ? '' : ' [no checkpoint zip - may revert]')
        + '. Turn OFF Steam Cloud Sync for Windrose, then launch to verify.';
}

function bindCharactersHandlers() {
    const reload = document.getElementById('char-reload');
    if (reload) reload.addEventListener('click', loadCharacters);
}
