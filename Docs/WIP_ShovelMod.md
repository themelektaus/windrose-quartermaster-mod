# Work In Progress: Shovel Terraform Unblock

Stand: 2026-05-12

## Big Picture

Ziel: Die Schaufel-Terraform-Tools (Increase / Decrease / Flatten) sollen auch dort
funktionieren, wo die vanilla-Engine "blockiert" sagt - z.B. neben Gebaeuden, Buschwerk,
Loot-Containern, Quest-Chests. Aktuell teilweise gelungen, Flatten neben Strukturen
funktioniert noch nicht.

Was funktioniert:
- `GAS.Ability.Terraform.Blocked` Tag aus den 3 Tool-DAs raus -> der Standard-Block-Hebel ist deaktiviert.
- `R5TerraformProcessor_GenericActor` aus der Global-Processor-Liste raus -> der Catch-All-Block-Hebel ist auch weg.
- Increase / Decrease arbeiten "teilweise" durch (richtungsabhaengig - weg von der Struktur klappt).

Was nicht funktioniert:
- **Flatten neben Gebaeuden / Strukturen funktioniert garnicht** - vermutlich greift hier `R5TerraformProcessor_Building` (C++-side direkt vom Tool registriert, nicht ueber Global-Liste).
- Flatten muss einen groesseren Bereich (Radius) gleichzeitig pruefen - schon ein einziges blockiertes Tile reicht zum Stillstand.

---

## Aktueller Deploy-Stand in `~mods`

| Pak | Inhalt | Status |
|---|---|---|
| `ShovelUnblock_P.{pak,ucas,utoc}` | DataAsset-Patch: `GAS.Ability.Terraform.Blocked` Tag aus 3x `DA_TerraformToolParams_*` raus | aktiv |
| `ShovelNoGenericBlock_P.pak` | Config-Override: `R5TerraformProcessor_GenericActor` aus `+GlobalTerraformProcessors` auskommentiert (Legacy-Pak-Format ohne Zen) | aktiv |
| `Quartermaster_QmTestPipeL1Clone_P.pak` | (unabhaengig, separates Thema) | aktiv |

---

## Was die Engine fuer Block-Hebel hat

In `R5/Config/DefaultGame.ini` unter `[/Script/R5.R5TerraformSettings]` registriert,
plus weitere die direkt vom Tool genutzt werden:

| Processor | Quelle | Was er blockiert / erlaubt |
|---|---|---|
| `BP_TerraformProcessor_Volumes` (BPGC) | DefaultGame.ini | erlaubt POI-Volumes (Whitelist) |
| `BP_TerraformProcessor_DialogueChests` (BPGC) | DefaultGame.ini | erlaubt 8 spezifische Quest-Chests |
| `BP_TerraformProcessor_WaterPlane` (BPGC) | DefaultGame.ini | erlaubt R5WaterPlane/Actor |
| `R5TerraformProcessor_PickupResource` (C++) | DefaultGame.ini | regelt Pickups |
| `R5TerraformProcessor_Mercuna` (C++) | DefaultGame.ini | NPC-Navigation |
| `R5TerraformProcessor_Loot` (C++) | DefaultGame.ini | Loot-Container |
| `R5TerraformProcessor_DamageableFoliage` (C++) | DefaultGame.ini | Baeume / Foliage |
| `R5TerraformProcessor_GenericActor` (C++) | DefaultGame.ini | **Catch-All - blockiert alles was nicht whitelisted ist** (von uns disabled) |
| `R5TerraformProcessor_Building` (C++) | direkt vom Tool registriert, nicht in `GlobalTerraformProcessors` | **vermutlich der noch aktive Block-Hebel bei Strukturen** |

Plus der DataAsset-seitige `ProgressBlockTags` Container in jedem `DA_TerraformToolParams_*`:

| Tag | Bedeutung | Status nach unserem Patch |
|---|---|---|
| `GAS.Character.Movement.Falling` | Spieler in der Luft | bleibt drin (Safety) |
| `GAS.Ability.Terraform.Blocked` | "Objekt im Weg" - wird von den Processors gesetzt | **entfernt** |
| `GAS.CharacterFsm.State.Death` | Spieler tot | bleibt drin (Safety) |

Der `GE_Terraform_BlockMovement` GameplayEffect setzt den `Blocked`-Tag wenn ein
Processor was findet. Ohne den Tag im `ProgressBlockTags`-Container wirkt das nicht
mehr - aber das hilft natuerlich nur fuer Processors die ueber Tags arbeiten.

---

## Source-Stand fuer Re-Build

### DataAsset-Patch (`ShovelUnblock_P`)

| Datei | Pfad |
|---|---|
| Source (Vanilla) | `Test/ShovelMod/Sources/R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Shovel_T01_Regular/Modes/{Increase,Decrease,Flatten}/DA_TerraformToolParams_*.uasset` + `.uexp` |
| Gepatched | `Test/ShovelMod/Patched/R5/Content/Gameplay/.../Modes/{Increase,Decrease,Flatten}/DA_TerraformToolParams_*.uasset` + `.uexp` |
| Build-Output | `Test/ShovelMod/build/ShovelUnblock_P.{pak,ucas,utoc}` |

Patch: nur die 3 DataAssets, jeweils ein Tag aus `ProgressBlockTags` raus. Zen-Format.

### Config-Override (`ShovelNoGenericBlock_P`)

| Datei | Pfad |
|---|---|
| Staging | `.build-tmp/shovel-genericactor-off/staging/R5/Config/DefaultGame.ini` (Zeile 290 auskommentiert: `;+GlobalTerraformProcessors=...R5TerraformProcessor_GenericActor`) |
| Build-Output | `.build-tmp/shovel-genericactor-off/ShovelNoGenericBlock_P.pak` |
| Pak-Format | Legacy repak (nur `.pak`, kein `.ucas` / `.utoc`) - Config-Override braucht kein Zen |

UE5 layert Config-Files aus Mod-Paks NICHT - daher komplette `DefaultGame.ini`
mitliefern, identisch zum Vanilla bis auf die eine auskommentierte Zeile.

---

## Naechste Optionen wenn wir das spaeter wieder aufnehmen

| Option | Aufwand | Risiko | Was es bringen wuerde |
|---|---|---|---|
| **A** `R5TerraformProcessor_Building` aus dem Tool-Setup entfernen | hoch (C++-Reflection, evtl. BPGC-Tool-Asset patchen wo der Processor registriert ist) | hoch (nicht klar ob ueberhaupt ueber Asset zugaenglich) | Flatten neben Strukturen geht durch |
| **B** Whitelist im `BP_TerraformProcessor_Volumes` erweitern (alle Building-Actor-Klassen reinaufnehmen) | mittel (BPGC-Properties editieren, SkipActorClasses-Array erweitern) | niedrig (zusaetzliche Whitelist, kein Removal) | Strukturen werden als "erlaubt" markiert, Flatten kommt durch |
| **C** Bewusst beim Building-Skip-Check ansetzen: `R5BuildingActor`-Klasse in eine SkipList eintragen falls so ein Mechanismus existiert | mittel | mittel | evtl. saubere Loesung |
| **D** Mod-Idee parken, nur Increase/Decrease als "good enough" Release | trivial | - | Mod ist nicht vollstaendig, aber 80% der Use-Cases gedeckt |
| **E** Tilde-Konsole testen ob es einen `EnableCheats`/`TerraformDebug` Command gibt der den Building-Block aus dem Tool ausnimmt | trivial | niedrig | reine Recon |

Mein Vorschlag fuer Resume: erst **E** (kostet nichts), dann **B** wenn E nichts bringt.

---

## Wichtige Befunde aus der Recon (damit wir's nicht nochmal machen)

1. Die `BP_TerraformProcessor_*` BPGCs (Volumes, DialogueChests, WaterPlane) sind Whitelist-Mechaniken, nicht Block-Mechaniken. Sie sagen "wenn Actor X im Weg ist, OK ist erlaubt". Was nicht whitelisted ist, faellt durch zu den anderen Processors.
2. Der `R5TerraformProcessor_GenericActor` war der eigentliche Catch-All-Blocker - daher hat `ShovelNoGenericBlock_P` schon einen grossen Effekt gehabt.
3. Bei Flatten reicht ein einziges blockiertes Tile im Tool-Radius zum Stop - daher ist Flatten anfaelliger als die Punkt-Operationen Increase/Decrease.
4. `R5TerraformProcessor_Building` ist nicht in `GlobalTerraformProcessors` aufgelistet - er muss vom Tool selbst registriert werden, vermutlich als Default-Wert in einem `BP_Shovel_*`-Asset oder per C++-Constructor.

---

## Side-Quest: Pickup-Crash mit `[QM] Test Pipe`

Bei einem Test wurde ein Stein eingesammelt, was zum Game-Crash gefuehrt hat. Root-Cause war
nicht die Schaufel-Mod, sondern ein dangling Inventory-Item:

```
R5BLBusinessRule: Error: TR5BLBusinessRule<>::Can
Rule Can() exception: Condition 'ItemParamsView' failed.
Message 'Item /R5BusinessRules/InventoryItems/Consumables/Misc/DA_CID_Misc_QmTestPipe_L1_T01... is not valid'.
```

Das Save hatte `[QM] Test Pipe` Items im Inventar / auf der Map, aber das DA-Asset war
nicht im Pak-Set. Beim Inventory-Pickup-Check (Stein einsammeln pruft Stack-Merge ueber
alle Slots) schlaegt `ItemParamsView` fehl, BL-Worker stirbt, Game geht in Inconsistent-State.

**Workaround**: `Quartermaster_QmTestPipeL1Clone_P.pak` wieder mit deployed, damit die
DA-Referenz aufloesbar bleibt. Die Test-Pipes muessen vor Mod-Removal aus dem Inventar
weggeworfen oder verbraucht werden.

**Offenes Folge-Thema**: Test-Pipes die auf der Map herumliegen (ausserhalb Inventar) -
gibt's einen Cheat-Console-Befehl um die zu cleanen? Recon hat ergeben dass R5 sogar
eingebaute Cheat-Klassen hat (`R5BLActor_PickupResource_DestroyRule`,
`R5BLInventoryCheat_ClearInventoryRule`), Tilde-Mapping ist in `DefaultInput.ini` Zeile 122,
aber vermutlich durch `DA_Administrator`/`DA_AdminManager` Access-Group gated. Tilde-Test
im Spiel wurde noch nicht durchgefuehrt.
