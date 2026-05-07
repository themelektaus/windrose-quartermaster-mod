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
| **Internet access (one-time)** | Auto-download `repak.exe` v0.2.3 from [trumank/repak](https://github.com/trumank/repak/releases) on first use. | yes (first run only) |
| **Windrose game or server install** | Source for the vanilla pak | yes |

The Steam install of Windrose is auto-detected via the Windows registry
(`HKCU\Software\Valve\Steam\SteamPath` /
`HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`) and Steam's
`libraryfolders.vdf`, so `pakchunk0-Windows.pak` is found without any
configuration. Dedicated-server installs (where `pakchunk0-WindowsServer.pak`
lives outside Steam) are supported via `-VanillaPak <path>`.

The pak is AES-encrypted; the public game key is hardcoded in
`Dump-WindroseVanilla.ps1` and is identical to the one used by every
other Windrose modding tool (e.g. WindrosePlus).

---

## 2. Setup after cloning

```powershell
# 1. Clone the repo (example)
git clone <repo-url> 'E:\Windrose\Mods\Stack Size'
cd 'E:\Windrose\Mods\Stack Size'

# 2. Extract the vanilla snapshot
.\Dump-WindroseVanilla.ps1
```

That's it -- both moving parts the pipeline depends on are auto-resolved:

- **Vanilla pak**: located by reading the Steam install path from the
  registry, then walking every Steam library listed in
  `libraryfolders.vdf` for a Windrose install. Override with
  `-VanillaPak <path>` if your pak lives elsewhere (typical case: a
  dedicated server install at `<Server>\R5\Content\Paks\pakchunk0-WindowsServer.pak`).
- **`repak.exe`**: downloaded on first use (pinned v0.2.3 from
  trumank/repak, SHA256-verified) and cached as `repak.exe` in the mod
  root. Override with `-RepakExe <path>` for a system-wide install.

All paths are fixed relative to the modding root:

```
.\Sources                 (mod sources, including Sources\Vanilla\)
.\Builds                  (finished .pak files)
```

The dump unpacks ~1097 `R5BLInventoryItem` JSONs into `Sources\Vanilla\`.
Re-run with `-Clean -Force` after a game patch that changed item values.

---

## 3. Smoke test (does everything run?)

DryRuns without side effects -- they should all succeed without errors:

```powershell
# Vanilla extract dry run: prints the planned repak invocation
.\Dump-WindroseVanilla.ps1 -DryRun

# Master dry run: shows planned variant builds
.\Build-AllStackVariations.ps1 -Variants x10 -DryRun
```

If `Sources\Vanilla\` already exists, also try:

```powershell
# Multiplier dry run against vanilla: shows statistics (~520 modified)
.\Apply-StackMultiplier.ps1 -Source .\Sources\Vanilla -Multiplier 4 -DryRun
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

`Sources\Vanilla\` is not in Git -- it's regenerated from the local
game install. Run this after first cloning the repo, and again after
game patches that change item values.

```powershell
.\Dump-WindroseVanilla.ps1 -Clean -Force
```

Behind the scenes this calls:

```
repak --aes-key <public-game-key> unpack `
    -i 'R5/Plugins/R5BusinessRules/Content/InventoryItems' `
    -o .\Sources\Vanilla `
    -f `
    <auto-detected pakchunk0-Windows.pak from the Steam install>
```

Total runtime is well under a second; the include filter restricts the
unpack to ~1097 InventoryItem JSONs out of ~13800 total entries in the pak.

---

## 7. Repo layout

```
Stack Size\
+-- Build-AllStackVariations.ps1   Master: all stack variations
+-- Dump-WindroseVanilla.ps1       Extract vanilla JSONs from the game pak
+-- Apply-StackMultiplier.ps1      Multiply / set MaxCountInSlot (thin wrapper)
+-- Build-WindroseMod.ps1          Pak the source folder into a _P.pak (thin wrapper)
+-- Library\
|   +-- Common.ps1                 Shared helpers (logging, paths, Get-RepakExe, Get-WindroseVanillaPak)
|   +-- Apply.ps1                  Invoke-StackMultiplierApply
|   +-- Pack.ps1                   Invoke-WindroseModPack
|   +-- Dump.ps1                   Invoke-WindroseVanillaDump
+-- repak.exe                      Auto-downloaded on first use (NOT in Git)
+-- Sources\                       Generated by Dump-WindroseVanilla.ps1 (NOT in Git)
|   +-- Vanilla\                   ~1097 vanilla JSONs
|   +-- StackSize_*\               Build artefacts
+-- Builds\                        Finished .pak files (NOT in Git)
```

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `SHA256 mismatch for repak_cli-*.zip` | Download corrupted, or trumank rotated the pinned release | Delete `repak.exe` and retry. If it persists, check `Library\Common.ps1` for the pinned `WindroseRepakVersion` |
| `Invoke-WebRequest: ... could not establish trust relationship` | TLS/proxy/AV blocking GitHub-Releases | Open the release URL in a browser, drop `repak.exe` manually into the mod root |
| `Could not find a Windrose vanilla pak under any Steam library` | Windrose isn't installed on this machine via Steam, or it's installed in a non-standard location Steam doesn't track | Pass `-VanillaPak <path>` to point at a `pakchunk0-WindowsServer.pak` (server) or `pakchunk0-Windows.pak` (client) directly |
| `Could not locate the Steam install` | Steam not installed, or registry keys missing/corrupt | Pass `-VanillaPak <path>` directly |
| `VanillaPak not found: <your path>` | Explicit `-VanillaPak` argument points at a non-existent file | Fix the path |
| `repak unpack` reports "encrypted but no key was provided" | Wrong AES key | The script uses the public Windrose key; if a future patch changes it, update the constant in `Library\Dump.ps1` |
| `R5LogJsonConverter: Error` in the game log | JSON schema not loadable | Re-run `Dump-WindroseVanilla.ps1 -Clean -Force` to refresh the snapshot, then rebuild |
| Encoding issues (`???` characters) in JSONs | `Get-Content` without UTF-8 (PS 5.1 default) | Already handled: scripts read via `[System.IO.File]::ReadAllText(..., UTF8)` |

---

## 9. What the pipeline does NOT do

- **No auto-deploy.** Server and client paths are not configurable.
  You copy `_P.pak` files into the `~mods` folder yourself.
- **No `.uasset` mods.** The pipeline targets JSON-based R5BusinessRules
  mods. Mesh / material / animation mods need different tools (FModel,
  UAssetGUI, repak unpack/repack -- not part of this repo).
- **No mappings dump.** `DumpUSMAP()` is not needed for R5BusinessRules JSONs.
