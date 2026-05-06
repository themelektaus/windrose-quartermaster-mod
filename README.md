# Windrose Modding -- StackSize-Mods

PowerShell-Pipeline zum Bauen von Stack-Size-Paks fuer [Windrose](https://www.nexusmods.com/windrose).
Multipliziert (oder ersetzt) `MaxCountInSlot` von allen stackbaren Vanilla-Items
und packt das Ergebnis in `.pak`-Dateien, die in den `~mods`-Ordner gehoeren.

Strukturquelle ist die Referenz-Mod
[Max Stack Sizes (Nexus #26)](https://www.nexusmods.com/windrose/mods/26).
Vanilla-Werte stammen aus einem Snapshot der Spiel-Defaults
(siehe `Sources\Vanilla\` im Repo).

Mehr Details (Workflow fuer eigene Mods, Vanilla-Re-Dump, Troubleshooting):
siehe [`DETAILS.md`](./DETAILS.md).

---

## Einmalig vorbereiten

```powershell
# Config anlegen
Copy-Item config.example.psd1 config.psd1
```

In `config.psd1` zwei Pfade setzen:

- `Tools.RepakExe` -- Pfad zu deiner `repak.exe`
- `References.StackModX4` -- Pfad zu `Stack_Size_Changes_x04_P.pak` (wird als
  Strukturquelle genutzt). Bezugsquelle:
  <https://www.nexusmods.com/windrose/mods/26>

## Alle Variationen auf einmal bauen

```powershell
.\Build-AllStackVariations.ps1
```

Output: 13 Paks in `Builds\`:

```
StackSize_x2_P.pak    StackSize_x3_P.pak    StackSize_x4_P.pak
StackSize_x5_P.pak    StackSize_x6_P.pak    StackSize_x7_P.pak
StackSize_x8_P.pak    StackSize_x9_P.pak    StackSize_x10_P.pak
StackSize_x100_P.pak
StackSize_999_P.pak   StackSize_9999_P.pak
StackSize_99999_P.pak StackSize_999999_P.pak
```

## Einzelne Variationen bauen

```powershell
# Nur x10 und 999
.\Build-AllStackVariations.ps1 -Variants x10,999

# Vorhandene Paks ueberschreiben
.\Build-AllStackVariations.ps1 -Force

# Build-Sources nach dem Bauen aufraeumen
.\Build-AllStackVariations.ps1 -CleanSources
```

## Pak installieren

Manuell in den `~mods`-Ordner kopieren:

```powershell
# Server
Copy-Item .\Builds\StackSize_x4_P.pak `
  'E:\Windrose\Server\<DeinServer>\R5\Content\Paks\~mods\' -Force

# Client
Copy-Item .\Builds\StackSize_x4_P.pak `
  'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

Pro `~mods`-Ordner immer nur **eine** `StackSize_*.pak` -- vorher alte raus.
