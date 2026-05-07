# Windrose Stack Size Mod -- Details

Pipeline for building JSON-based Windrose mods (item stack sizes etc.) from
a vanilla snapshot extracted directly out of the game's main pak. The end
product is `_P.pak` files that you copy into the `~mods` folder of your
server or client.

---

## 1. Prerequisites

| What | Why | Required? |
|---|---|---|
| **Windows PowerShell 5.1** (or PS7) | Run scripts | yes |
| **`repak.exe`** ([trumank/repak](https://github.com/trumank/repak/releases)) | Pack/unpack paks | yes |
| **Windrose game or server install** | Source for the vanilla pak | yes |

The vanilla item JSONs are extracted on demand from
`pakchunk0-WindowsServer.pak` (server install) or `pakchunk0-Windows.pak`
(Steam client). The pak is AES-encrypted; the public game key is hardcoded
in `Dump-WindroseVanilla.ps1` and is identical to the one used by every
other Windrose modding tool (e.g. WindrosePlus).

---

## 2. Setup after cloning

```powershell
# 1. Clone the repo (example)
git clone <repo-url> 'E:\Windrose\Mods\Stack Size'
cd 'E:\Windrose\Mods\Stack Size'

# 2. Create your own config from the template
Copy-Item .\config.example.psd1 .\config.psd1
```

Then open **`config.psd1`** in an editor and set these two paths:

```powershell
Tools = @{
    RepakExe = 'C:\Path\To\Your\repak.exe'
}

Game = @{
    VanillaPak = 'E:\Windrose\Server\<YourServer>\R5\Content\Paks\pakchunk0-WindowsServer.pak'
    # Or for the Steam client:
    # VanillaPak = 'C:\Games\steamapps\common\Windrose\R5\Content\Paks\pakchunk0-Windows.pak'
}
```

You can leave `Paths` empty -- all paths are then resolved relative to the
modding root:

```
.\Sources                 (mod sources, including Sources\Vanilla\)
.\Builds                  (finished .pak files)
```

Finally extract the vanilla snapshot once:

```powershell
.\Dump-WindroseVanilla.ps1
```

This unpacks ~1097 `R5BLInventoryItem` JSONs into `Sources\Vanilla\`. Re-run
with `-Clean -Force` after a game patch that changed item values.

---

## 3. Smoke test (does everything run?)

DryRuns without side effects -- they should all succeed without errors:

```powershell
# Multiplier dry run against vanilla: shows statistics (~520 modified)
.\Apply-StackMultiplier.ps1 -Source .\Sources\Vanilla -Multiplier 4 -DryRun

# Master dry run: shows planned variant builds
.\Build-AllStackVariations.ps1 -Variants x10 -DryRun

# Vanilla extract dry run: prints the planned repak invocation
.\Dump-WindroseVanilla.ps1 -DryRun
```

If that passes, the pipeline is ready to use.

---

## 4. Workflow A -- Build a single custom mod

```powershell
# 1. Copy the vanilla snapshot to a working folder
Copy-Item .\Sources\Vanilla .\Sources\MyMod -Recurse

# 2. Trim it down (e.g. only Cannonball items) and/or edit MaxCountInSlot
#    by hand, or run Apply-StackMultiplier with -ExcludePath to drop everything
#    you don't care about.

# 3. Build the pak
.\Build-WindroseMod.ps1 -Source .\Sources\MyMod -Force
# -> .\Builds\MyMod_P.pak

# 4. Copy into the game yourself
Copy-Item .\Builds\MyMod_P.pak `
    'E:\Windrose\Server\<YourServer>\R5\Content\Paks\~mods\' -Force
# or for the Steam client:
Copy-Item .\Builds\MyMod_P.pak `
    'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

Anything you delete from the source folder before packing simply stays
vanilla. Anything you leave in becomes part of the override. There is no
"init" step -- the vanilla source is the template.

---

## 5. Workflow B -- Build all stack variations

The master script builds multipliers x2..x10 and absolute values 999..9999 in one go:

```powershell
# All 11 variations
.\Build-AllStackVariations.ps1 -Force

# Only selected ones
.\Build-AllStackVariations.ps1 -Variants x4,x10,9999 -Force

# Clean up source folders after build
.\Build-AllStackVariations.ps1 -CleanSources -Force
```

Output: `.\Builds\StackSize_<name>_P.pak`. Per variant:
- Copy `Sources\Vanilla\` to `Sources\StackSize_<name>\`
- `Apply-StackMultiplier` (multiplies the vanilla MaxCountInSlot value or
  sets an absolute value, deletes non-stackable items)
- `Build` into the `Builds\` directory

You still have to copy the paks **manually** into the respective `~mods`
folders.

---

## 6. Workflow C -- Regenerate the vanilla snapshot

Only needed after game patches that change item values, or when first
cloning the repo if `Sources\Vanilla\` is missing.

```powershell
.\Dump-WindroseVanilla.ps1 -Clean -Force
```

Behind the scenes this calls:

```
repak --aes-key <public-game-key> unpack `
    -i 'R5/Plugins/R5BusinessRules/Content/InventoryItems' `
    -o .\Sources\Vanilla `
    -f `
    <Game.VanillaPak>
```

Total runtime is well under a second; the include filter restricts the
unpack to ~1097 InventoryItem JSONs out of ~13800 total entries in the pak.

---

## 7. Repo layout

```
Stack Size\
+-- Build-WindroseMod.ps1          Pak the source folder into a _P.pak
+-- Apply-StackMultiplier.ps1      Multiply / set MaxCountInSlot
+-- Build-AllStackVariations.ps1   Master: all stack variations
+-- Dump-WindroseVanilla.ps1       Extract vanilla JSONs from the game pak
+-- _config.ps1                    Config loader (dot-sourced by all scripts)
+-- config.example.psd1            Config template (in Git)
+-- config.psd1                    Your own config (NOT in Git)
+-- Sources\
|   +-- Vanilla\                   ~1097 vanilla JSONs (in Git, snapshot)
|   +-- StackSize_*\               Build artefacts (NOT in Git)
+-- Builds\                        Finished .pak files (NOT in Git)
```

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `repak.exe not found` | `Tools.RepakExe` in `config.psd1` wrong / empty | Correct the path |
| `VanillaPak not found` | `Game.VanillaPak` in `config.psd1` wrong / empty | Set it to your `pakchunk0-*.pak` |
| `config.psd1` missing -> warning, falls back to example | Normal on first run | `Copy-Item config.example.psd1 config.psd1` |
| `repak unpack` reports "encrypted but no key was provided" | Wrong AES key | The script uses the public Windrose key; if a future patch changes it, update the constant in `Dump-WindroseVanilla.ps1` |
| `R5LogJsonConverter: Error` in the game log | JSON schema not loadable | Re-run `Dump-WindroseVanilla.ps1 -Clean -Force` to refresh the snapshot, then rebuild |
| Encoding issues (`???` characters) in JSONs | `Get-Content` without UTF-8 (PS 5.1 default) | Already handled: scripts read via `[System.IO.File]::ReadAllText(..., UTF8)` |

---

## 9. What the pipeline does NOT do

- **No auto-deploy.** Server and client paths are not in the config.
  You copy `_P.pak` files into the `~mods` folder yourself.
- **No `.uasset` mods.** The pipeline targets JSON-based R5BusinessRules
  mods. Mesh / material / animation mods need different tools (FModel,
  UAssetGUI, repak unpack/repack -- not part of this repo).
- **No mappings dump.** `DumpUSMAP()` is not needed for R5BusinessRules JSONs.
