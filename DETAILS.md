# Windrose-Modding -- Details

Pipeline zum Bauen von JSON-basierten Windrose-Mods (Item-Stack-Sizes etc.) aus
einem Vanilla-Snapshot. Endprodukt sind `_P.pak`-Dateien, die du in den
`~mods`-Ordner deines Servers oder Clients kopierst.

---

## 1. Voraussetzungen

| Was | Wozu | Pflicht? |
|---|---|---|
| **Windows PowerShell 5.1** (oder PS7) | Skripte ausfuehren | ja |
| **`repak.exe`** ([trumank/repak](https://github.com/trumank/repak/releases)) | Pak packen/entpacken | ja |
| **Windrose-Spiel oder -Server** | als Deploy-Ziel | ja, wenn du testen willst |
| **UE4SS** im Server | nur fuer Vanilla-Re-Dump nach Game-Update | optional |
| Eine bestehende Mod als Template (z.B. `Stack_Size_Changes_x04_P.pak`, [Nexus #26](https://www.nexusmods.com/windrose/mods/26)) | als Strukturquelle fuer `-FromPak` / `Build-AllStackVariations` | optional |

> Hinweis: Das Repo enthaelt bereits einen vollstaendigen Vanilla-Snapshot
> (`Sources/Vanilla/`, ~1268 JSONs). Den brauchst du **nicht** selbst zu
> erzeugen, ausser nach einem Spiel-Patch der Item-Werte geaendert hat.

---

## 2. Setup nach dem Clone

```powershell
# 1. Repo klonen (Beispiel)
git clone <repo-url> E:\Windrose\Modding
cd E:\Windrose\Modding

# 2. Eigene Config aus Template anlegen
Copy-Item .\config.example.psd1 .\config.psd1
```

Dann **`config.psd1`** im Editor oeffnen und mindestens diesen Pfad anpassen:

```powershell
Tools = @{
    RepakExe = 'C:\Pfad\zu\deiner\repak.exe'
}
```

Optional, wenn du `Build-AllStackVariations.ps1` nutzen willst:

```powershell
References = @{
    StackModX4 = 'E:\Windrose\Mods\Max Stack Sizes\R5\Content\Paks\~mods\Stack_Size_Changes_x04_P.pak'
}
```

> Die Stack-Mod-Referenz stammt von Nexus:
> <https://www.nexusmods.com/windrose/mods/26>
> ("Max Stack Sizes -- 999/9999/999999 or Multipliers x2-10/x100"). Die x4-Variante
> wird als Strukturquelle genutzt, weil sie korrekte `ItemMesh`-Pfade und ein
> vom Spiel akzeptiertes JSON-Schema mitbringt -- unser eigener Vanilla-Dump kann
> das aufgrund von UE4SS-Limits (`TSoftObjectPtr`) nicht.

`Paths` darfst du leer lassen -- dann werden alle Pfade relativ zum
Modding-Root aufgeloest:

```
.\Sources                 (Mod-Quellen, inkl. Sources\Vanilla\)
.\Builds                  (fertige .pak-Dateien)
.\ue4ss-mods\VanillaItemDumper\Dumps   (Lua-Mod-Output)
```

---

## 3. Smoke-Test (alles laeuft?)

DryRuns ohne Side-Effects, sollten alle ohne Fehler durchlaufen:

```powershell
# Init-DryRun: liest aus Sources\Vanilla\, listet 3 Cannonball-Files
.\Build-WindroseMod.ps1 -Action Init -Source .\Sources\__Smoke -Filter '*Cannonball*' -DryRun

# Multiplier-DryRun gegen Vanilla: zeigt Statistik (~520 modifiziert)
.\Apply-StackMultiplier.ps1 -Source .\Sources\Vanilla -Multiplier 4 -DryRun

# Master-DryRun: zeigt geplante Variantenbauten
.\Build-AllStackVariations.ps1 -Variants x10 -DryRun
```

Wenn das durchlaeuft, ist die Pipeline einsatzbereit.

---

## 4. Workflow A -- Eine Mod bauen

```powershell
# 1. Neue Source aus Vanilla initialisieren (z.B. nur Cannonball-Items)
.\Build-WindroseMod.ps1 -Action Init `
    -Source .\Sources\MyAmmoMod -Filter '*Cannonball*'

# 2. Werte in den JSONs unter .\Sources\MyAmmoMod\R5\... editieren
#    (z.B. MaxCountInSlot anpassen)

# 3. Pak bauen
.\Build-WindroseMod.ps1 -Source .\Sources\MyAmmoMod -Force
# -> .\Builds\MyAmmoMod_P.pak

# 4. Selbst kopieren ins Spiel
Copy-Item .\Builds\MyAmmoMod_P.pak `
    'E:\Windrose\Server\Nockalmeer\R5\Content\Paks\~mods\' -Force
# bzw. fuer den Steam-Client:
Copy-Item .\Builds\MyAmmoMod_P.pak `
    'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

**Alternative Init-Modi:**

```powershell
# Aus existierender Mod als Template (volles Schema mit Mesh-Pfaden)
.\Build-WindroseMod.ps1 -Action Init `
    -Source .\Sources\MyMod -FromPak 'E:\Windrose\Mods\...\Stack_Size_Changes_x04_P.pak'

# Nur bestimmte Kategorien aus Vanilla
.\Build-WindroseMod.ps1 -Action Init `
    -Source .\Sources\MyConsumableMod -Categories Consumables
```

---

## 5. Workflow B -- Alle Stack-Variationen bauen

Das Master-Skript baut Multiplier x2..x10, x100 und Absolutwerte 999..999999
in einem Rutsch:

```powershell
# Alle 14 Variationen
.\Build-AllStackVariations.ps1 -Force

# Nur ausgewaehlte
.\Build-AllStackVariations.ps1 -Variants x10,x100,999999 -Force

# Nach Build die Source-Ordner aufraeumen
.\Build-AllStackVariations.ps1 -CleanSources -Force
```

Output: `.\Builds\StackSize_<name>_P.pak`. Per Variante:
- `Init` aus `$cfg.References.StackModX4` (Strukturquelle mit korrekten Mesh-Pfaden)
- `Apply-StackMultiplier` mit `-VanillaSource .\Sources\Vanilla` (multipliziert
  Vanilla-Wert, nicht den Stack-Mod-Wert)
- `Build` ins `Builds\`-Verzeichnis

Die Paks musst du wieder **manuell** in die jeweiligen `~mods`-Ordner kopieren.

---

## 6. Workflow C -- Vanilla-Snapshot neu erzeugen (selten)

Nur noetig nach Spiel-Patches, die Item-Werte aendern.

1. Lua-Mod auf den Server kopieren:
   ```
   Quelle: .\ue4ss-mods\VanillaItemDumper\
   Ziel:   <Server>\R5\Binaries\Win64\ue4ss\Mods\VanillaItemDumper\
   ```
2. Server starten -- Lua-Mod schreibt JSONs in
   `<Server>\R5\Binaries\Win64\ue4ss\Mods\VanillaItemDumper\Dumps\`.
   Erfolg im UE4SS-Log: `[VanillaItemDumper] done: 1268 dumped, 0 failed`
3. Dumps ins Modding-Verzeichnis spiegeln:
   ```powershell
   robocopy '<Server>\R5\Binaries\Win64\ue4ss\Mods\VanillaItemDumper\Dumps' `
            '.\ue4ss-mods\VanillaItemDumper\Dumps' /MIR
   ```
4. Tree rekonstruieren (flach -> Verzeichnisbaum):
   ```powershell
   .\Dump-WindroseVanilla.ps1 -Clean -Force
   ```
   -> ueberschreibt `Sources\Vanilla\`.

> Bekannte Limitierung: UE4SS auf diesem Build exposed `TSoftObjectPtr` aus
> Lua heraus nicht. `ItemMesh`-Felder im Vanilla-Dump sind deshalb `"None"`.
> Fuer Stack-Size-Mods irrelevant, weil wir die Strukturquelle aus `-FromPak`
> einer existierenden Mod beziehen. Falls du von Grund auf neue Items willst,
> brauchst du eine andere Mesh-Pfad-Quelle.

---

## 7. Repo-Struktur

```
Modding\
+-- Build-WindroseMod.ps1          Pipeline: Init + Build + Pack
+-- Apply-StackMultiplier.ps1      MaxCountInSlot multiplizieren / setzen
+-- Build-AllStackVariations.ps1   Master: alle Stack-Variationen
+-- Dump-WindroseVanilla.ps1       Vanilla-Dump-Reorganisierer (flach -> Tree)
+-- _config.ps1                    Config-Loader (von allen Skripten dot-sourced)
+-- config.example.psd1            Config-Template (in Git)
+-- config.psd1                    Eigene Config (NICHT in Git)
+-- Sources\
|   +-- Vanilla\                   1268 Vanilla-JSONs (in Git, Snapshot)
|   +-- StackSize_*\               Build-Artefakte (NICHT in Git)
+-- Builds\                        fertige .pak-Dateien (NICHT in Git)
+-- ue4ss-mods\
    +-- VanillaItemDumper\         Lua-Mod-Quelle fuer UE4SS
        +-- Scripts\               main.lua + json.lua
        +-- Dumps\                 Output zur Laufzeit (NICHT in Git)
```

---

## 8. Troubleshooting

| Symptom | Ursache | Fix |
|---|---|---|
| `repak.exe` nicht gefunden | `Tools.RepakExe` in `config.psd1` falsch / leer | Pfad korrigieren |
| `config.psd1` fehlt -> Warning, Fallback auf example | normal beim ersten Start | `Copy-Item config.example.psd1 config.psd1` |
| `R5LogJsonConverter: Error` im Spiel-Log | JSON-Schema nicht ladbar (z.B. `[{}]`-Arrays, Number-Enums) | Mod aus `-FromPak` einer funktionierenden Mod initialisieren statt aus rohem Vanilla-Dump |
| `missing static mesh` -> Server-Crash beim Loot-Spawn | `ItemMesh: "None"` im Vanilla-Dump | Source aus `-FromPak` initialisieren -- die Stack-Mod-Referenz hat korrekte Mesh-Pfade |
| `_INIT.txt` landet im Pak | sollte nicht passieren (wird per Temp-Stash entfernt) | Build-Skript neu ziehen |
| Encoding-Probleme (`???`-Zeichen) in JSONs | `Get-Content` ohne UTF-8 (PS 5.1 Default) | gefixt: Skripte lesen via `[System.IO.File]::ReadAllText(..., UTF8)` |

---

## 9. Was die Pipeline NICHT macht

- **Kein Auto-Deploy.** Server- und Client-Pfade sind nicht in der Config.
  Du kopierst `_P.pak`-Dateien selbst in den `~mods`-Ordner.
- **Keine `.uasset`-Mods.** Die Pipeline ist auf JSON-basierte
  R5BusinessRules-Mods zugeschnitten. Mesh-/Material-/Animation-Mods brauchen
  andere Tools (FModel, UAssetGUI, repak unpack/repack -- nicht Teil dieses
  Repos).
- **Kein Mappings-Dump.** `DumpUSMAP()` ist im Repo nicht enthalten -- fuer
  R5BusinessRules-JSONs nicht noetig.
