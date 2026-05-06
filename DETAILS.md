# Windrose Stack Size Mod -- Details

Pipeline for building JSON-based Windrose mods (item stack sizes etc.) from
a vanilla snapshot. The end product is `_P.pak` files that you copy into the
`~mods` folder of your server or client.

---

## 1. Prerequisites

| What | Why | Required? |
|---|---|---|
| **Windows PowerShell 5.1** (or PS7) | Run scripts | yes |
| **`repak.exe`** ([trumank/repak](https://github.com/trumank/repak/releases)) | Pack/unpack paks | yes |
| **Windrose game or server** | Deploy target | yes, if you want to test |
| **UE4SS** on the server | Only for vanilla re-dump after game updates | optional |
| An existing mod as a template (e.g. `Stack_Size_Changes_x04_P.pak`, [Max Stack Sizes by Synthlight](https://www.nexusmods.com/windrose/mods/26)) | Structural source for `-FromPak` (only for custom mods, not needed for the standard stack-size build) | optional |

> Note: The repo already includes a complete vanilla snapshot
> (`Sources/Vanilla/`, ~1268 JSONs). You do **not** need to regenerate it
> yourself unless a game patch has changed item values.

---

## 2. Setup after cloning

```powershell
# 1. Clone the repo (example)
git clone <repo-url> E:\Windrose\Modding
cd E:\Windrose\Modding

# 2. Create your own config from the template
Copy-Item .\config.example.psd1 .\config.psd1
```

Then open **`config.psd1`** in an editor and set this path:

```powershell
Tools = @{
    RepakExe = 'C:\Path\To\Your\repak.exe'
}
```

That's all that's required. `Build-AllStackVariations.ps1` reuses the
per-variant `Sources\StackSize_*\` folders that are produced from the
vanilla snapshot + `reference-fields.json` -- no external pak needed.

If you want to author a brand-new mod from an existing pak as a template,
pass `-FromPak <path>` explicitly to `Build-WindroseMod.ps1 -Action Init`
(see Workflow A below).

You can leave `Paths` empty -- all paths are then resolved relative to the
modding root:

```
.\Sources                 (mod sources, including Sources\Vanilla\)
.\Builds                  (finished .pak files)
.\ue4ss-mods\VanillaItemDumper\Dumps   (Lua mod output)
```

---

## 3. Smoke test (does everything run?)

DryRuns without side effects -- they should all succeed without errors:

```powershell
# Init dry run: reads from Sources\Vanilla\, lists 3 Cannonball files
.\Build-WindroseMod.ps1 -Action Init -Source .\Sources\__Smoke -Filter '*Cannonball*' -DryRun

# Multiplier dry run against vanilla: shows statistics (~520 modified)
.\Apply-StackMultiplier.ps1 -Source .\Sources\Vanilla -Multiplier 4 -DryRun

# Master dry run: shows planned variant builds
.\Build-AllStackVariations.ps1 -Variants x10 -DryRun
```

If that passes, the pipeline is ready to use.

---

## 4. Workflow A -- Build a single mod

```powershell
# 1. Initialise a new source from vanilla (e.g. only Cannonball items)
.\Build-WindroseMod.ps1 -Action Init `
    -Source .\Sources\MyAmmoMod -Filter '*Cannonball*'

# 2. Edit values in the JSONs under .\Sources\MyAmmoMod\R5\...
#    (e.g. tweak MaxCountInSlot)

# 3. Build the pak
.\Build-WindroseMod.ps1 -Source .\Sources\MyAmmoMod -Force
# -> .\Builds\MyAmmoMod_P.pak

# 4. Copy into the game yourself
Copy-Item .\Builds\MyAmmoMod_P.pak `
    'E:\Windrose\Server\Nockalmeer\R5\Content\Paks\~mods\' -Force
# or for the Steam client:
Copy-Item .\Builds\MyAmmoMod_P.pak `
    'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

**Alternative init modes:**

```powershell
# From an existing mod as template (full schema with mesh paths)
.\Build-WindroseMod.ps1 -Action Init `
    -Source .\Sources\MyMod -FromPak 'E:\Windrose\Mods\...\Stack_Size_Changes_x04_P.pak'

# Only specific categories from vanilla
.\Build-WindroseMod.ps1 -Action Init `
    -Source .\Sources\MyConsumableMod -Categories Consumables
```

---

## 5. Workflow B -- Build all stack variations

The master script builds multipliers x2..x10, x100 and absolute values
999..9999 in one go:

```powershell
# All 11 variations
.\Build-AllStackVariations.ps1 -Force

# Only selected ones
.\Build-AllStackVariations.ps1 -Variants x10,x100,9999 -Force

# Clean up source folders after build
.\Build-AllStackVariations.ps1 -CleanSources -Force
```

Output: `.\Builds\StackSize_<name>_P.pak`. Per variant:
- Reuse the existing `Sources\StackSize_<name>\` folder (or `-FromPak <path>`
  to (re)initialise it from a reference pak)
- `Apply-StackMultiplier` with `-VanillaSource .\Sources\Vanilla` (multiplies
  the vanilla value, not the stack-mod value)
- `Build` into the `Builds\` directory

You still have to copy the paks **manually** into the respective `~mods`
folders.

---

## 6. Workflow C -- Regenerate vanilla snapshot (rare)

Only needed after game patches that change item values.

1. Copy the Lua mod onto the server:
   ```
   Source: .\ue4ss-mods\VanillaItemDumper\
   Target: <Server>\R5\Binaries\Win64\ue4ss\Mods\VanillaItemDumper\
   ```
2. Start the server -- the Lua mod writes JSONs into
   `<Server>\R5\Binaries\Win64\ue4ss\Mods\VanillaItemDumper\Dumps\`.
   Success in the UE4SS log: `[VanillaItemDumper] done: 1268 dumped, 0 failed`
3. Mirror the dumps into the modding directory:
   ```powershell
   robocopy '<Server>\R5\Binaries\Win64\ue4ss\Mods\VanillaItemDumper\Dumps' `
            '.\ue4ss-mods\VanillaItemDumper\Dumps' /MIR
   ```
4. Reconstruct the tree (flat -> directory tree):
   ```powershell
   .\Dump-WindroseVanilla.ps1 -Clean -Force
   ```
   -> overwrites `Sources\Vanilla\`.

> Known limitation: UE4SS on this build does not expose `TSoftObjectPtr`
> from Lua. `ItemMesh` fields in the vanilla dump are therefore `"None"`.
> Irrelevant for stack-size mods because we get the structural source from
> `-FromPak` of an existing mod. If you want brand-new items from scratch,
> you need a different mesh-path source.

---

## 7. Repo layout

```
Modding\
+-- Build-WindroseMod.ps1          Pipeline: Init + Build + Pack
+-- Apply-StackMultiplier.ps1      Multiply / set MaxCountInSlot
+-- Build-AllStackVariations.ps1   Master: all stack variations
+-- Dump-WindroseVanilla.ps1       Vanilla dump reorganiser (flat -> tree)
+-- _config.ps1                    Config loader (dot-sourced by all scripts)
+-- config.example.psd1            Config template (in Git)
+-- config.psd1                    Your own config (NOT in Git)
+-- Sources\
|   +-- Vanilla\                   1268 vanilla JSONs (in Git, snapshot)
|   +-- StackSize_*\               Build artefacts (NOT in Git)
+-- Builds\                        Finished .pak files (NOT in Git)
+-- ue4ss-mods\
    +-- VanillaItemDumper\         Lua mod source for UE4SS
        +-- Scripts\               main.lua + json.lua
        +-- Dumps\                 Runtime output (NOT in Git)
```

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `repak.exe` not found | `Tools.RepakExe` in `config.psd1` wrong / empty | Correct the path |
| `config.psd1` missing -> warning, falls back to example | Normal on first run | `Copy-Item config.example.psd1 config.psd1` |
| `R5LogJsonConverter: Error` in the game log | JSON schema not loadable (e.g. `[{}]` arrays, number enums) | Initialise the mod from `-FromPak` of a working mod instead of a raw vanilla dump |
| `missing static mesh` -> server crash on loot spawn | `ItemMesh: "None"` in vanilla dump | Initialise the source from `-FromPak` -- the Stack-mod reference has correct mesh paths |
| `_INIT.txt` ends up in the pak | Should not happen (removed via temp stash) | Pull the build script again |
| Encoding issues (`???` characters) in JSONs | `Get-Content` without UTF-8 (PS 5.1 default) | Fixed: scripts read via `[System.IO.File]::ReadAllText(..., UTF8)` |

---

## 9. What the pipeline does NOT do

- **No auto-deploy.** Server and client paths are not in the config.
  You copy `_P.pak` files into the `~mods` folder yourself.
- **No `.uasset` mods.** The pipeline targets JSON-based R5BusinessRules
  mods. Mesh / material / animation mods need different tools (FModel,
  UAssetGUI, repak unpack/repack -- not part of this repo).
- **No mappings dump.** `DumpUSMAP()` is not included in the repo -- not
  needed for R5BusinessRules JSONs.
