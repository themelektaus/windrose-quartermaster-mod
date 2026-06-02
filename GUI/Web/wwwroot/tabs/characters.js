'use strict';

// Characters tab - existing-character equipment-slot save patcher.
//
// The Equipment Slots sliders (Basic tab) write the new ring/necklace counts
// into the pak, which only affects NEWLY created characters. This tab patches
// the slot count straight into an EXISTING character's RocksDB save so it picks
// up the same counts. The patch target is the active profile's slider values;
// each character is compared against that target and only patched when it
// differs (the backend additionally backs up + no-ops when it already matches).

// Ring / necklace target taken from the active profile's Equipment Slots
// sliders (default vanilla 1/1 when the profile carries no override).
function charTarget() {
    const eqs = state.current && state.current.globals && state.current.globals.equipmentSlots;
    const ring = eqs && eqs.ringSlots != null ? eqs.ringSlots : 1;
    const neck = eqs && eqs.necklaceSlots != null ? eqs.necklaceSlots : 1;
    return { ring, neck };
}

function charGlobalStatus(msg) {
    const el = document.getElementById('char-global-status');
    if (el) el.textContent = msg || '';
}

async function loadCharacters() {
    state.characters.loaded = true;
    charGlobalStatus('Scanning save profiles...');
    const cs = state.characters;
    const [chars, ships] = await Promise.allSettled([
        api('GET', '/api/savegame/characters'),
        api('GET', '/api/savegame/ships'),
    ]);
    if (chars.status === 'fulfilled') {
        cs.supported = !!chars.value.supported;
        cs.list = chars.value.characters || [];
        cs.error = null;
    } else {
        cs.supported = true; cs.list = []; cs.error = chars.reason.message;
    }
    if (ships.status === 'fulfilled') {
        cs.shipsSupported = !!ships.value.supported;
        cs.ships = ships.value.ships || [];
        cs.shipsError = null;
    } else {
        cs.shipsSupported = true; cs.ships = []; cs.shipsError = ships.reason.message;
    }
    renderCharacters();
}

// Mirrors the backend's "already matches" rule: a character only counts as up
// to date when BOTH the live slot count AND the blueprint count equal the
// target, for ring and necklace alike.
function charNeedsPatch(c, target) {
    return c.ringSlots !== target.ring
        || c.necklaceSlots !== target.neck
        || c.blueprintRing !== target.ring
        || c.blueprintNeck !== target.neck;
}

function renderCharacters() {
    const host = document.getElementById('char-list');
    if (!host) return;
    const cs = state.characters;
    host.replaceChildren();

    if (cs.error) {
        charGlobalStatus('Scan failed: ' + cs.error
            + ' (make sure Windrose is fully closed).');
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

    const target = charTarget();
    charGlobalStatus('Found ' + cs.list.length + ' character(s). Profile target: ring '
        + target.ring + ' / necklace ' + target.neck + '.');

    for (const c of cs.list) {
        host.appendChild(buildCharacterRow(c, target));
    }

    renderShips();
}

function buildCharacterRow(c, target) {
    const needs = charNeedsPatch(c, target);

    const card = document.createElement('section');
    card.className = 'card char-card' + (needs ? ' char-needs-patch' : '');

    const content = document.createElement('div');
    content.className = 'card-content';

    const h = document.createElement('h2');
    h.textContent = c.playerName || c.characterId;
    content.appendChild(h);

    const cur = document.createElement('p');
    cur.className = 'hint char-current';
    cur.textContent = 'Current: ring ' + c.ringSlots + ' / necklace ' + c.necklaceSlots
        + '  →  target: ring ' + target.ring + ' / necklace ' + target.neck;
    content.appendChild(cur);

    const row = document.createElement('div');
    row.className = 'bell-row';
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn-secondary';

    const st = document.createElement('p');
    st.className = 'hint';
    st.style.margin = '.4em 0';

    if (needs) {
        btn.textContent = 'Backup + patch to ' + target.ring + ' / ' + target.neck;
        btn.addEventListener('click', () => charPatch(c, target, btn, st, false));
        if (c.lastStatus) st.textContent = c.lastStatus;
    } else {
        btn.textContent = 'Up to date';
        btn.disabled = true;
        st.textContent = c.lastStatus
            || ('Already ring ' + c.ringSlots + ' / necklace ' + c.necklaceSlots + ' - nothing to do.');
    }

    row.appendChild(btn);
    content.appendChild(row);
    content.appendChild(st);
    card.appendChild(content);
    return card;
}

async function charPatch(c, target, btn, st, force) {
    btn.disabled = true;
    st.textContent = 'Backing up + patching to ring ' + target.ring
        + ' / necklace ' + target.neck + '...';
    try {
        const res = await api('POST', '/api/savegame/patch', {
            dbFolder: c.dbFolder,
            ringSlots: target.ring,
            necklaceSlots: target.neck,
            force: !!force,
        });

        if (res.blocked) {
            const ok = await confirm(
                'Reducing slots would delete equipped items:\n\n'
                + (res.blockingItems || []).join('\n')
                + '\n\nDelete them and patch anyway?');
            if (ok) return charPatch(c, target, btn, st, true);
            st.textContent = 'Cancelled - unequip those items in-game first.';
            btn.disabled = false;
            return;
        }

        if (res.alreadyMatches) {
            c.lastStatus = (res.playerName || c.playerName)
                + ' already has ring ' + target.ring + ' / necklace ' + target.neck + '.';
        } else {
            c.lastStatus =
                'Patched ' + (res.playerName || c.playerName) + ': '
                + res.oldRing + '/' + res.oldNeck + ' → ' + res.newRing + '/' + res.newNeck
                + (res.backupCreated ? ' (backup saved)' : '')
                + (res.checkpointZipRebuilt ? '' : ' [no checkpoint zip - may revert]')
                + '. Turn OFF Steam Cloud Sync for Windrose, then launch to verify.';
        }
        // Reflect the new on-disk counts so the row flips to "Up to date".
        c.ringSlots = res.newRing != null ? res.newRing : target.ring;
        c.necklaceSlots = res.newNeck != null ? res.newNeck : target.neck;
        c.blueprintRing = target.ring;
        c.blueprintNeck = target.neck;
        renderCharacters();
    } catch (e) {
        st.textContent = 'Patch failed: ' + e.message;
        btn.disabled = false;
    }
}

// ---------------------------------------------------------------------------
// Ships (Expanded Naval Tactics: cargo + Combat Orders save patcher).
// ---------------------------------------------------------------------------

// Cargo multiplier + combat-order count from the active profile's Ship Slots
// sliders (default vanilla x1 / 1 when the profile carries no override).
function shipTarget() {
    const ss = state.current && state.current.globals && state.current.globals.shipSlots;
    const mult = ss && ss.cargoMultiplier != null ? ss.cargoMultiplier : 1;
    const combat = ss && ss.combatOrderSlots != null ? ss.combatOrderSlots : 1;
    return { mult, combat };
}

// Mirrors ShipSlotsPatcher.CargoTarget (round away from zero, never below
// vanilla, capped). Math.round in JS rounds .5 up, matching AwayFromZero for
// the non-negative values we deal with here.
function shipCargoTarget(base, mult) {
    if (!base || base <= 0) return base || 0;
    let t = Math.round(base * mult);
    if (t < base) t = base;
    if (t > 200) t = 200;
    return t;
}

function shipNeedsPatch(s, t) {
    if (!s.supported) return false;
    const cargoActive = Math.abs(t.mult - 1) > 1e-9;
    const combatActive = t.combat !== 1;
    const tc = shipCargoTarget(s.vanillaCargoBase, t.mult);
    const cargoDiff = cargoActive && (s.cargoSlots !== tc || s.blueprintCargo !== tc);
    const combatDiff = combatActive && (s.combatSlots !== t.combat || s.blueprintCombat !== t.combat);
    return cargoDiff || combatDiff;
}

function shipGlobalStatus(msg) {
    const el = document.getElementById('ship-global-status');
    if (el) el.textContent = msg || '';
}

function shipTypeLabel(sourceDa) {
    if (!sourceDa) return 'unknown';
    return sourceDa.replace(/^DA_ShipInventory_/, '');
}

function shipDisplayName(s) {
    const owner = s.ownerName || s.characterId || 'Unknown';
    const name = s.shipName ? '"' + s.shipName + '"' : shipTypeLabel(s.sourceDa);
    return owner + ' - ' + name;
}

function renderShips() {
    const host = document.getElementById('ship-list');
    if (!host) return;
    const cs = state.characters;
    host.replaceChildren();

    if (cs.shipsError) {
        shipGlobalStatus('Ship scan failed: ' + cs.shipsError
            + ' (make sure Windrose is fully closed).');
        return;
    }
    if (!cs.shipsSupported) { shipGlobalStatus('No Windrose save profiles found.'); return; }
    if (!cs.ships.length) { shipGlobalStatus('No ships found in any save.'); return; }

    const t = shipTarget();
    const hasTarget = Math.abs(t.mult - 1) > 1e-9 || t.combat !== 1;
    shipGlobalStatus(hasTarget
        ? ('Found ' + cs.ships.length + ' ship(s). Profile target: cargo x' + t.mult
            + ' / combat orders ' + t.combat + '.')
        : ('Found ' + cs.ships.length + ' ship(s). Set the Ship Slots sliders on the Basic tab to a non-vanilla target first.'));

    for (const s of cs.ships) host.appendChild(buildShipRow(s, t));
}

function buildShipRow(s, t) {
    const needs = shipNeedsPatch(s, t);

    const card = document.createElement('section');
    card.className = 'card char-card' + (needs ? ' char-needs-patch' : '');

    const content = document.createElement('div');
    content.className = 'card-content';

    const h = document.createElement('h2');
    h.textContent = shipDisplayName(s);
    content.appendChild(h);

    const tc = shipCargoTarget(s.vanillaCargoBase, t.mult);
    const cur = document.createElement('p');
    cur.className = 'hint char-current';
    cur.textContent = 'Cargo ' + s.cargoSlots + ' (vanilla ' + s.vanillaCargoBase + ')  →  ' + tc
        + '   |   Combat orders ' + s.combatSlots + '  →  ' + t.combat
        + '   [' + shipTypeLabel(s.sourceDa) + ']';
    content.appendChild(cur);

    const row = document.createElement('div');
    row.className = 'bell-row';
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn-secondary';

    const st = document.createElement('p');
    st.className = 'hint';
    st.style.margin = '.4em 0';

    if (!s.supported) {
        btn.textContent = 'Not supported';
        btn.disabled = true;
        st.textContent = 'Expanded Naval Tactics covers Brig / Frigate / Ketch only - this ship is left at vanilla.';
    } else if (needs) {
        btn.textContent = 'Backup + patch to cargo ' + tc + ' / combat ' + t.combat;
        btn.addEventListener('click', () => shipPatch(s, t, btn, st, false));
        if (s.lastStatus) st.textContent = s.lastStatus;
    } else {
        btn.textContent = 'Up to date';
        btn.disabled = true;
        st.textContent = s.lastStatus
            || ('Already cargo ' + s.cargoSlots + ' / combat ' + s.combatSlots + ' - nothing to do.');
    }

    row.appendChild(btn);
    content.appendChild(row);
    content.appendChild(st);
    card.appendChild(content);
    return card;
}

async function shipPatch(s, t, btn, st, force) {
    btn.disabled = true;
    const tc = shipCargoTarget(s.vanillaCargoBase, t.mult);
    st.textContent = 'Backing up + patching to cargo ' + tc + ' / combat ' + t.combat + '...';
    try {
        const res = await api('POST', '/api/savegame/ship-patch', {
            dbFolder: s.dbFolder,
            shipKey: s.shipKey,
            cargoMultiplier: t.mult,
            combatOrderSlots: t.combat,
            force: !!force,
        });

        if (res.blocked) {
            const ok = await confirm(
                'Shrinking would delete loaded items:\n\n'
                + (res.blockingItems || []).join('\n')
                + '\n\nDelete them and patch anyway?');
            if (ok) return shipPatch(s, t, btn, st, true);
            st.textContent = 'Cancelled - empty those slots in-game first.';
            btn.disabled = false;
            return;
        }
        if (res.unsupported) {
            st.textContent = 'Not a supported ship type (' + (res.sourceDa || '') + ').';
            return;
        }

        if (res.alreadyMatches) {
            s.lastStatus = shipDisplayName(s) + ' already matches the target.';
        } else {
            s.lastStatus = 'Patched ' + shipDisplayName(s) + ': cargo '
                + res.oldCargo + ' → ' + res.newCargo + ', combat ' + res.oldCombat + ' → ' + res.newCombat
                + (res.backupCreated ? ' (backup saved)' : '')
                + (res.checkpointZipRebuilt ? '' : ' [no checkpoint zip - may revert]')
                + '. Turn OFF Steam Cloud Sync for Windrose, then launch to verify.';
        }
        // Reflect the new on-disk counts so the row flips to "Up to date".
        s.cargoSlots = res.newCargo != null ? res.newCargo : s.cargoSlots;
        s.blueprintCargo = s.cargoSlots;
        s.combatSlots = res.newCombat != null ? res.newCombat : s.combatSlots;
        s.blueprintCombat = s.combatSlots;
        renderShips();
    } catch (e) {
        st.textContent = 'Patch failed: ' + e.message;
        btn.disabled = false;
    }
}

function bindCharactersHandlers() {
    const reload = document.getElementById('char-reload');
    if (reload) reload.addEventListener('click', loadCharacters);
}
