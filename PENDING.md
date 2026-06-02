# PENDING - offene Punkte (Triage)

Kurz-Notizen zum späteren Weitermachen. Stand: 2026-06-02.

> **Punkte 1 + 2 sind GEFIXT (2026-06-02).** Details unten am jeweiligen
> Eintrag. Zum Wirksamwerden Dev-Server neu starten (zieht Core-Build +
> frischen wwwroot-Embed).

## 1. Characters Save-Patcher: Ship-Slots lassen sich nicht zurück-patchen (Downgrade-Bug) - ✅ GEFIXT

**Repro:** Ship-Slots in der Save gepatcht (cargo hoch, combat hoch),
dann Profil-Slider zurück auf Vanilla gestellt (cargo 1x = 28, combat 1).
Der Patcher will jetzt **nicht** mehr zurück (downgrade).

**Symptom (Screenshot, Karte "Speedrunner - Ketch_Stock"):**
- Anzeige: `Cargo 84 (vanilla 28) -> 28 | Combat orders 5 -> 1 [Ketch_Stock]`
- Button: **"Up to date"**
- Text: *"Already cargo 84 / combat 5 - nothing to do."*

Heißt: Ziel ist eindeutig **28 / 1**, die Save steht auf **84 / 5** - trotzdem
hält der "alreadyMatches"/"Up to date"-Check den Zustand für aktuell und
verweigert den Downgrade. Der Vergleich prüft offenbar gegen den **falschen
Zielwert** (vermutlich current-vs-current, oder die schon gepatchten 84/5
werden als neue "Vanilla-Basis" gelesen und mit dem Multiplier wieder zu
84/5 hochgerechnet, statt gegen den echten Vanilla-Baseline 28 zu prüfen).

**Verdächtige Stellen:**
- `Tools/QuartermasterCore/ShipSaveSlotsPatcher.cs` (alreadyMatches-Logik /
  Ziel-Berechnung, Vanilla-Baseline-Quelle)
- `GUI/Web/Endpoints/SavegameEndpoint.cs` (Ship-Discovery: liefert `vanilla`,
  `bp` (blueprint) und current - prüfen, ob das Ziel korrekt aus Vanilla*Mult
  statt aus dem aktuell gepatchten Wert kommt)
- `GUI/Web/wwwroot/tabs/characters.js` ("Up to date"-Entscheidung im UI)

**Erwartet:** Wenn Save 84/5 und Ziel 28/1, muss der Patcher 84->28 und 5->1
schreiben (Downgrade), nicht "nothing to do".

**FIX:** Ursache war das `cargoActive`/`combatActive`-Gate ("Slider != Vanilla")
in `ShipSaveSlotsPatcher.PatchShip` UND `characters.js shipNeedsPatch`. Bei
Slider == Vanilla wurde das Gate `false` -> "Up to date", obwohl das Ziel
korrekt 28/1 war. Das Ziel kommt ohnehin idempotent aus `vanillaBase * mult`
(Cargo) bzw. absolut (Combat), das Gate war reines Dead-Weight, das Downgrades
blockiert hat (Equipment-Slots/Rings hatten dieses Gate nie -> die gingen). Das
Gate ist jetzt raus: "needs patch" = aktueller Save-Wert (live ODER blueprint)
!= Ziel. Der Blocking-Item-Check schützt den Shrink (Downgrade) weiterhin.

## 2. Build-Gate: "Profile produces no changes" obwohl Ship-Slots gepatcht wurden - ✅ GEFIXT

**Log:**
```
[OK] Patching vanilla items -> ...\R5\Plugins\R5BusinessRules\Content\InventoryItems
[OK] Patched items: 0 (0 promoted, 0 overridden, 0 capped)
[OK] Patching ship inventory slots (cargo / combat orders)
[OK]   cargo x3, combat orders 5 - 12 ship file(s) patched. NOTE: only affects NEW ships; existing ships need the save patcher.
[ERR] ERROR: Profile produces no changes - nothing to pack.
```

**Problem:** Profil hat **nur** Ship-Slot-Multiplier gesetzt (cargo x3,
combat 5). Der Ship-Slots-**Pak**-Patch läuft erfolgreich (12 Dateien), aber
das "produces no changes"-Gate zählt diesen Job-Typ nicht mit -> Build bricht
vor dem Packen ab. Ein Profil, das *ausschließlich* Ship-Slots ändert, kann
so nicht gebaut werden.

**Verdächtige Stellen:**
- `Tools/QuartermasterCore/BuildPipeline.cs` (die "no changes -> nothing to
  pack"-Abbruchbedingung; Ship-Slots-Pak-Jobs müssen als Änderung zählen)
- `Tools/QuartermasterCore/ShipSlotsPatcher.cs` (Pak-Patcher) + dessen
  Verkabelung/Ergebnis-Aggregation

**Erwartet:** Ship-Slot-Pak-Patches (cargo/combat) zählen als echte Änderung;
Build packt das Triplet auch wenn sonst nichts gesetzt ist.

**FIX:** `shipSlotsResult` wurde zwar in `tmpDir` geschrieben, aber nicht in die
`totalWritten`-Summe in `BuildPipeline.cs` aufgenommen -> bei einem reinen
Ship-Slots-Profil war `totalWritten == 0` und das Gate warf "produces no
changes". Jetzt zählt `shipSlotsResult.FilesWritten` mit (analog zu den
Equipment-Slots), damit landen die 12 Ship-JSONs im Legacy-Pak.

---

## 3. User-Meldung: "Reload Multiplier for Cannons wirkt nicht" - RECON, NICHT GELÖST

**User-Report (Original):**
```
Really an awesome mod, thank you. Many things make a lot more fun now!

I have only one issue: The Reload Multiplier for Cannons doesn't seem to work.
I tried different settings and also different cannons (since I thought, maybe
it's not working for the basic cannons at the start) but so far no luck.
Reload Time is still the same as in vanilla. What am I doing wrong?
```

### Betroffener Regler / Kette
- UI-Karte **"Ship Cannons"** (Cooldowns-Tab), Slider-ID `cd-cannon-multiplier`
- Profil-Feld `ShipCannonMultiplier` (in `Profile.cs`)
- Pak-Patcher `ShipCannonPatcher.cs`
- Verkabelt über die generische "Cooldowns"-Logik (identisch zu den 8 anderen
  Cooldown-Familien, die nachweislich funktionieren).

### Was VERIFIZIERT wurde (alles korrekt auf Tool-Seite)

1. **Datenmodell stimmt.** Echte Vanilla-Assets per UAssetAPI-Dumper (usmap,
   `EngineVersion.VER_UE5_6`) gedumpt. `AimingData.ReloadTime` ist das
   **einzige** Reload-Feld in den `DA_BatteryManagerParams_*`. Daneben gibt es:
   - `DA_BatteryShotParams_12/24/36` (pro Kaliber) -> nur `ShotDelay`
     (Intervall zwischen Einzelschüssen), **kein** Reload.
   - Aiming-DAs (`DA_CutterAiming`, `DA_FrigateAiming`) -> nur Rotation.
   Reload sitzt also batterie-/hull-level, **genau** wo der Patcher zielt.
   Vanilla-Werte: **Cutter 10s, Ketch/Brig 16s, Frigate 15s**.

2. **GUI->Profil->Pipeline-Wiring stimmt** (`cooldowns.js`: `cannon` ->
   `shipCannonMultiplier`, Slider-ID matcht; `Profile.cs`-Property-Name passt
   zum JS-Key -> keine Null-Deserialisierung). Generisch wie die anderen 8.

3. **Patcher-Mathe stimmt** (16 -> 8 bei 0.5x, alle Batterien getroffen).

4. **>> KNACKPUNKT: IoStore-Pack-Roundtrip ERHÄLT den Wert.** Probe:
   Cannon patchen (16->8) -> `to-zen` packen -> aus dem Container ZURÜCK
   extrahieren (`to-legacy`, mit `global.*` ScriptObjects daneben) ->
   `ReloadTime = 8.0`. **Die Build-Pipeline produziert ein korrektes Pak.**
   Damit ist "Build verliert die Änderung beim Packen" AUSGESCHLOSSEN.

5. **Kein Silent-Failure-Pfad:** `AfterExtract` in `IoStoreCompositeBuilder.cs`
   prüft Asset-Existenz und WIRFT bei fehlenden Cannon-Assets -> da der User
   erfolgreich baut, wird `AimingData.ReloadTime` tatsächlich skaliert+gepackt.
   Fehler werden NICHT verschluckt (propagieren -> Build bricht ab).

6. **Mein eigener deployter Pak (Tausi):** enthält **keinen** Cannon-Multiplier
   (15 Assets, keine `BatteryManagerParams`) -> Feature ist opt-in, von mir nie
   aktiviert. Erklärt, warum ich selbst nichts dazu wusste; hilft dem Reporter
   aber nicht.

### Fazit der Triage
**Kein konzeptioneller / grundlegender Fehler.** Datenmodell, Property-Wahl,
Wiring, Patch-Mathe und Pack-Roundtrip sind alle bewiesen korrekt. Der Cannon-
Reload nutzt exakt dieselbe Pak-Override-Technik wie die funktionierenden 8
Cooldown-Familien.

### OFFEN - nur 2 Möglichkeiten, ohne Spiel NICHT trennbar:
- **(A) Runtime-Override greift nicht** für diese `DamageModelContent`-Assets
  (echter Bug -> Fix nötig). Eher unwahrscheinlich, aber nur in-game beweisbar.
- **(B) Reporter-seitig:** Slider verstellt aber nicht neu gebaut/deployt,
  falsches Pak getestet, oder ~1.0x belassen. Angesichts dass alles andere
  stimmt, das WAHRSCHEINLICHSTE.

### Entscheidender nächster Schritt (bereit)
**Test-Pak `zzz_CannonReloadTest_P`** gebaut: alle 8 Hulls bei **0.2x**
(Cutter 10->2s, Ketch/Brig 16->3.2s, Frigate 15->3s) - unverkennbar, falls der
Override greift. Mountet zuletzt (`zzz_`-Prefix), kollidiert nicht mit dem
laufenden Ship-Slots-Test. **Noch NICHT in `~mods` deployt** (um den laufenden
Test nicht zu stören). 1-Minuten-Test in-game = trennt (A) von (B) eindeutig.

### Nebenbefund - echter, unabhängiger Mini-Bug (fixbar)
Slider `min=0.01` im HTML, aber `ShipCannonPatcher.MinMultiplier = 0.1` (auch
`RangedReloadPatcher`). Wer den Regler auf 0.01-0.09 zieht, lässt den **ganzen
Build mit Exception abstürzen** (Kommentar im Code: "the GUI should have clamped
this" - tut es aber nicht). Angleichen: Slider `min=0.1` ODER Patcher-Clamp
lockern. Separater 2-Min-Fix.

### Verdächtige / relevante Stellen
- `Tools/QuartermasterCore/ShipCannonPatcher.cs` (Patcher + `MinMultiplier`)
- `Tools/QuartermasterCore/RangedReloadPatcher.cs` (gleicher Clamp)
- `GUI/Web/wwwroot/tabs/cooldowns.js` (Slider-Wiring, `min=0.01`)
- `Tools/QuartermasterCore/Profile.cs` (`ShipCannonMultiplier`)
- `Tools/QuartermasterCore/IoStoreCompositeBuilder.cs` (Extract/Pack-Pfad)

### Recon-Artefakte
- Wegwerf-Probes lagen unter `.build-tmp/cannon-probe/` (gitignored) - aufgeräumt.
- **Behalten:** Property-Dumper (UAssetAPI) + Test-Pak `zzz_CannonReloadTest_P`
  für späteren in-game-Test.
