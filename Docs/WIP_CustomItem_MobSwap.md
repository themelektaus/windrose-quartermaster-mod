# Work In Progress: Custom Item + Mob Swap

Stand: 2026-05-11

## Big Picture

Ziel war ein eigenes (= neu hinzugef�gtes, nicht ersetztes) Item nach Vorlage der "Eber-Pfeife", spaeter erweitert zu einem Pet-Spawn-Item das einen Wolf statt eines Boars beschwoert.

Was funktioniert:
- Custom-Item-Workflow ist komplett bewiesen (Item-DA + LootTable + CSV via reines JSON-Modding).
- Asset-Override eines vanilla BPGC zu einer anderen Mob-Klasse ist machbar.
- Boar -> Dodo Swap ist live (Dodo spawnt, ist friendly, geht auf Gegner los).

Was nicht funktioniert:
- Dodo-Spawn macht **keinen sichtbaren Schaden** trotz Animation und Combat-Stat-Chain.
- Boar -> Wolf/AlphaWolf Swap **crasht beim Weltladen**, sobald nicht nur pure NameMap-Append, sondern Identity-Strings getauscht werden.

---

## Aktueller Deploy-Stand in `~mods`

| Pak | Inhalt | Status |
|---|---|---|
| `Quartermaster_QmTestPipeL1Clone_P.pak` | Custom-Item `[QM] Test Pipe` (L1-Klon der Boar Whistle) + 30 Foliage-LootTables (jede Faserpflanze droppt die Pipe) + CSV mit Lokalisierung (korrekter Pfad `R5/Content/Localization/Data/InventoryItems.csv`) | aktiv, funktioniert |
| Mob-Swap-Pak | aktuell **keiner** drin (Iter3-Wolf hatte gecrasht und wurde rausgeworfen) | - |

Optional zur�ckdeploybar:
- `DodoFriend_P.{pak,ucas,utoc}` - liefert Boar -> Dodo Swap (spawnt friendly Dodo, macht aber keinen Schaden)
- `WolfIter2_P.pak` - liefert pure NameMap-Append (Welt l�dt, Spawn ist aber weiterhin Boar)

---

## Custom-Item: was wir gelernt haben

### Funktioniert

| Bestandteil | Pfad / Mechanismus |
|---|---|
| Item-DataAsset | `R5/Plugins/R5BusinessRules/Content/InventoryItems/Consumables/Misc/DA_CID_Misc_QmTestPipe_L1_T01.json` |
| LootTable-Drops (Foliage) | 30x `R5/Plugins/R5BusinessRules/Content/LootTables/Foliage/*.json` mit angehaengtem Pipe-Eintrag |
| CSV-Lokalisierung | `R5/Content/Localization/Data/InventoryItems.csv` (NICHT der Plugin-Pfad - das war ein Bug in der ersten Variante) |
| Item-Tag-Validitaet | Item-Tag muss bereits in der GameplayTag-Registry des Spiels existieren. Eigene Tags scheitern stumm bei Activation. Wir nutzen Vanilla-Tag `ConsData.Misc.SpawnerBoar.L1.T01`. |
| Spawn-Logik | Geerbt aus `DA_ConsumableAbilityData_SpawnerBoar` + vanilla L1 Path. L1 hat `LootTableData: "None"`, L2 spawnt nicht (WIP-Stub). |

### Sackgassen / Fallen

| Problem | Loesung |
|---|---|
| Eigener `ItemTag` -> Cooldown laeuft, kein Effekt | Tag muss vanilla sein, oder es muesste eine Tag-Registry-Datei mitgeliefert werden. |
| CSV-Patch im falschen Pfad (`R5/Plugins/...`) | `<MISSING STRING TABLE ENTRY>` in Tooltip. Korrigiert nach `R5/Content/Localization/Data/InventoryItems.csv`. |
| Klon der L2 (Mighty Boar Whistle) | L2 spawnt nichts -> WIP-Stub. Nur L1-Klon funktioniert. |

---

## Mob-Swap: was wir gelernt haben

### Reference-Mod "FriendlyAlphaWolf" - Analyse

Die Ref-Mod hat **zwei Saeulen**:

1. **Pak**: Ueberschreibt `BP_Mob_Boar_Friend.uasset` + `BP_Mob_Boar_FriendLvl2.uasset` als komplette IoStore-Asset-Overrides. Patch-Strategie ist NICHT NameMap-Swap, sondern:
   - 24 neue Strings im NameMap **angehaengt** (Boar-Strings bleiben unangetastet)
   - 49 neue Imports angelegt
   - CDO (Export[4]) von 182 B (RawExport) auf 1231 B (NormalExport mit ~42 expliziten ObjectProperty-Bindings) aufgeblasen
   - 4 spezifische Properties auf neue Wolf-Indizes umgebogen (Parent, AIController, AbilitySystemParams, AgentParams)
2. **UE4SS-Lua-Hook**: `Scripts/main.lua` findet alle `BP_Mob_Boar_FriendLvl2_C` Instanzen zur Runtime und setzt:
   - `Creature.FactionComponent.FactionsParams = CrewFaction` (sonst feindlich)
   - `Creature.R5Marker.MarkerModelInstance = newModel` (HP-Anzeige fixen)

**Wichtig**: UE4SS ist in unserer Spielinstallation **nicht** installiert. Ohne UE4SS waere auch die Ref-Mod nur "Wolf-Aussehen, Boar-Verhalten".

### Boar -> Dodo (funktioniert teilweise)

Patcher: `BP_Mob_Boar_Friend.uasset` NameMap-Swap (10 Strings Boar -> Dodo).
- Spielstart: OK
- Spawn beim Pfeife-Use: Dodo spawnt, ist friendly, geht auf Gegner los
- Combat: Animation laeuft, aber **kein Schaden** sichtbar

Versuch DodoFriend mit eigenem Asset-Chain (10 Klone via UAssetAPI):
- `DA_Mob_DodoFriend_DamageParams_S1/L1/L2/H1`
- `DA_Mob_DodoFriend_SectionSpec_S1/L1/L2/H1`
- `DA_Mob_DodoFriend_Melee_InstanceParams`
- `GA_Mob_DodoFriend_Melee` (BPGC-Klon - kritisch)
- `DA_Mob_DodoFriend_AbilitySystemParams`
- Update von `BP_Mob_Boar_Friend` ASP-Pointer auf neue ASP

Resultat: Spiel laeuft, Spawn klappt, aber **Animation lief vorher schon nicht wirklich** (war optisch verwechselt) und das Damage-Problem bleibt unveraendert. Vermutlich ist Dodo's `CustomCanApplyGameplayEffectComponent` der Stopper, oder es war nie eine echte Attack-Animation.

### Boar -> Wolf (crasht)

| Iteration | Strategie | Resultat |
|---|---|---|
| Wolf-Swap | NameMap-Swap 10 Boar-Strings -> Wolf | Crash beim Weltladen, 208 ms nach Scenario-Init |
| AlphaWolf-Swap | wie oben, AlphaWolf statt Wolf | Crash identisch, 242 ms nach Scenario-Init |
| Iter2 (Append-only) | 3 neue Wolf-Imports angelegt, Super/Template umgebogen, keine Boar-Strings angefasst | **Welt laedt!** Aber Spawn ist weiterhin Boar (CDO erbt von Wolf, aber Component-Defaults kommen via outer=-10 aus Boar-CDO) |
| Iter3 (Append + Identity-Swap) | Iter2 + NameMap-Indizes [0, 56, 57] auf Wolf umgepoolt | **Crash beim Weltladen** wieder. Vermutlich weil Component-Imports (74-119) mit outer=-10 jetzt in Wolf-CDO suchen, wo die Boar-Component-Namen nicht existieren |

### Architektur-Erkenntnis

- BPGC-Asset-Surgery ist mit reinem NameMap-Swap nicht zu schaffen, weil:
  - Component-Imports tragen Boar-CDO als `outer`
  - Engine resolved Component-Defaults ueber diese Outer-Kette
  - Swap fuehrt zur Sackgasse (Components in Wolf-CDO nicht auffindbar)
- Ref-Mod-Weg (Append + explizit serialisiertes CDO mit Component-Property-Overrides) ist die saubere Loesung, aber:
  - Setzt voraus dass UAssetAPI das CDO als NormalExport schreiben kann
  - UAssetAPI braucht ein usmap-Schema fuer BPGC-Klassen wie `BP_Mob_Boar_C` / `BP_Mob_Boar_Friend_C`
  - R5.usmap enthaelt **nur C++ Klassen, keine BPGCs** -> `FormatException: Failed to find a valid schema for parent name BP_Mob_Boar_C`

### Wege noch offen fuer Wolf-Pfad

| Pfad | Aufwand | Erfolgswahrscheinlichkeit |
|---|---|---|
| A. Synthetic Schemas in usmap registrieren | ~2 h | ~50% |
| B. Byte-Level uexp-Patcher (FUnversionedHeader + ObjectPropertyData by hand) | 4-6 h | ~30% |
| C. Ref-Mod-Bytes uebernehmen und durch unsere Pipeline (to-zen + repak) schleusen | 30 min | hoch, aber kollidiert mit Workspace-Regel "References never integrieren" |
| D. UE4SS installieren + Runtime-Faction+AIController-Hook schreiben | mittel | hoch |
| E. Wolf-Pfad parken, vanilla Boar_Friend als Test-Pipe-Spawn akzeptieren | 0 | trivial |

---

## Tooling-Stand

Beim Custom-Item / Mob-Swap entstanden:

| Tool | Pfad | Zweck |
|---|---|---|
| `WolfPatcher` (Iter2/Iter3) | `Tools/bpgc-probe/...` | NameMap-Append + Super/Template-Index-Switch fuer `BP_Mob_Boar_Friend.uasset` |
| `DodoFriendCloner` | gleicher Tool-Tree | Generischer UAssetAPI-Asset-Clone (NameMap-Patch + Save unter neuem Pfad) |
| CDO-Inspector | gleicher Tool-Tree | Diff zwischen Ref-Mod und Vanilla CDO Export[4] |
| Repak-basierter Build | `.build-tmp/whistle-recon/` etc. | Pak-Triplet Build via repak + AES-Key |

`Tools/QuartermasterCore/` (Production-GUI-Pipeline) wurde fuer Custom-Item / Mob-Swap **nicht** veraendert. Alles laeuft via Build-Tmp und manuelles Deploy.

---

## Was vor diesem Mini-Projekt schon stand

| Feature | Pipeline-Pfad | Status |
|---|---|---|
| Stack Sizes / Pickup Radius | Composite (UAssetAPI + retoc to-zen) | ingame verifiziert |
| No-Smoke | Composite | ingame verifiziert |
| Building Stability (787 DA_BIs) | `_Raw_P` Triplet (zen-chunk byte-patch + pack-raw) | ingame verifiziert |
| Minimap Range | `_Raw_P` Triplet (vanilla INI + repak) | ingame verifiziert |
| Bonfire Radius | Composite | ingame verifiziert |

Boarding Distance war revertiert (war Ship-vs-Ship-Feature, nicht relevant).

Commit Stand vor Custom-Item-Experimenten: `3980d30` (Bonfire-Feature integriert). Origin/main steht noch auf `212c713` (Boarding-Commit nicht zurueckgenommen).

---

## Naechste Entscheidung

Vier sinnvolle Pfade ab hier:

| Option | Was wir bekommen | Aufwand |
|---|---|---|
| A | UE4SS in `E:\Games\steamapps\common\Windrose\` installieren + Runtime-Hook fuer Faction/AIController/Marker schreiben. Wolf-Aussehen via Iter3-aehnliches Pak (oder Ref-Mod Bytes), Runtime fixt das Verhalten. | hoch |
| B | Pivot zum Dodo-Damage-Fix. Hier kaempfen wir nicht gegen BPGC-Internals, sondern um GameplayEffects (JSON/DA-Welt, in der wir gut sind). | mittel |
| C | Wolf-Pfad parken. Test-Pipe spawnt vanilla `BP_Mob_Boar_Friend` (heute schon funktional), Item heisst dann passend ("Boar Whistle Clone" o.ae.). | sehr niedrig |
| D | CDO-Re-Serialize via synthetic usmap-Schema fuer BPGC-Klassen. Riskant, aber waere die "richtige" Loesung. | sehr hoch |

Empfehlung: Erst B (Dodo-Damage), dann C als Fallback fuer Wolf-Aussehen. A nur wenn UE4SS-Abhaengigkeit ok ist.
