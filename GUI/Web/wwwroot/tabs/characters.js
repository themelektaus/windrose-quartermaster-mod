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
    try {
        const res = await api('GET', '/api/savegame/characters');
        state.characters.supported = !!res.supported;
        state.characters.list = res.characters || [];
        state.characters.error = null;
    } catch (e) {
        state.characters.supported = true;
        state.characters.list = [];
        state.characters.error = e.message;
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

function bindCharactersHandlers() {
    const reload = document.getElementById('char-reload');
    if (reload) reload.addEventListener('click', loadCharacters);
}
