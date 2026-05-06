# Windrose Stack Size Mod

PowerShell pipeline for building stack-size paks for [Windrose](https://www.nexusmods.com/windrose).
Multiplies (or replaces) `MaxCountInSlot` for every stackable vanilla item
and packs the result into `.pak` files that go into the `~mods` folder.

The structural source is the reference mod
[Max Stack Sizes by Synthlight](https://www.nexusmods.com/windrose/mods/26).
Vanilla values come from a snapshot of the game's defaults
(see `Sources\Vanilla\` in the repo).

For more details (workflow for custom mods, vanilla re-dump, troubleshooting)
see [`DETAILS.md`](./DETAILS.md).

---

## One-time setup

```powershell
# Create config
Copy-Item config.example.psd1 config.psd1
```

Set two paths in `config.psd1`:

- `Tools.RepakExe` -- path to your `repak.exe`
- `References.StackModX4` -- path to `Stack_Size_Changes_x04_P.pak` (used as
  the structural source). Source:
  <https://www.nexusmods.com/windrose/mods/26>

## Build all variations at once

```powershell
.\Build-AllStackVariations.ps1
```

Output: 13 paks in `Builds\`:

```
StackSize_x2_P.pak    StackSize_x3_P.pak    StackSize_x4_P.pak
StackSize_x5_P.pak    StackSize_x6_P.pak    StackSize_x7_P.pak
StackSize_x8_P.pak    StackSize_x9_P.pak    StackSize_x10_P.pak
StackSize_x100_P.pak
StackSize_999_P.pak   StackSize_9999_P.pak
StackSize_99999_P.pak StackSize_999999_P.pak
```

## Build individual variations

```powershell
# Only x10 and 999
.\Build-AllStackVariations.ps1 -Variants x10,999

# Overwrite existing paks
.\Build-AllStackVariations.ps1 -Force

# Clean up build sources after building
.\Build-AllStackVariations.ps1 -CleanSources
```

## Install pak

Copy manually into the `~mods` folder:

```powershell
# Server
Copy-Item .\Builds\StackSize_x4_P.pak `
  'E:\Windrose\Server\<YourServer>\R5\Content\Paks\~mods\' -Force

# Client
Copy-Item .\Builds\StackSize_x4_P.pak `
  'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

Only **one** `StackSize_*.pak` per `~mods` folder -- remove any older one first.
