# Windrose Stack Size Mod -- Details

Configurator + build pipeline for JSON-based Windrose mods (item stack
sizes etc.) from a vanilla snapshot extracted out of the game's main pak.
The end product is `_P.pak` files that you copy into the `~mods` folder
of your server or client.

---

## 1. Prerequisites

| What | Why | Required? |
|---|---|---|
| **.NET 10 SDK** (preview) | Builds the GUI + StackSizeCore library | yes |
| **PowerShell 5.1+** | Runs the two one-time data dump scripts | yes |
| **Internet access** (one-time) | Auto-downloads `repak.exe` v0.2.3 from [trumank/repak](https://github.com/trumank/repak/releases) on first use | yes (first run only) |
| **Windrose game/server install** | Source for the vanilla pak | yes |
| **CUE4Parse submodule** | Read UE5 IoStore containers when extracting icons | yes (icons only) |
| **UE4SS** in the game | Generate the `.usmap` file CUE4Parse needs | yes (icons only) |

The Steam install of Windrose is auto-detected via the Windows registry
(`HKCU\Software\Valve\Steam\SteamPath` /
`HKLM\SOFTWARE\WOW6432Node\Valve\Steam\InstallPath`) and Steam's
`libraryfolders.vdf`. Dedicated-server installs (where the pak lives
outside Steam) are supported via `-VanillaPak <path>`.

The pak is AES-encrypted; the public game key is hardcoded in
`Library\Dump.ps1` and is identical to the one used by every other
Windrose modding tool.

---

## 2. Setup after cloning

```powershell
git clone <repo-url> 'E:\Windrose\Mods\Stack Size'
cd 'E:\Windrose\Mods\Stack Size'

# CUE4Parse submodule (needed by the icon extractor only)
git submodule update --init Tools/CUE4Parse

# Extract vanilla JSONs
.\Dump-WindroseVanilla.ps1

# (Optional) Extract icons + localized metadata so the GUI shows real
# item names and pictures. Needs a *.usmap next to the script -- press
# Ctrl+Num6 in the running game with UE4SS' Keybinds mod active and drop
# the resulting file in the mod root.
.\Extract-Icons.ps1
```

Both scripts auto-resolve their tooling:

- **Vanilla pak**: walked from the Steam install path. Override with
  `-VanillaPak <path>` for non-Steam locations.
- **`repak.exe`**: pinned v0.2.3, downloaded + SHA256-verified to
  `repak.exe` in the mod root on first use.
- **`IconExtractor.exe`** (Phase 2): `dotnet publish`'d on first use into
  `Tools\IconExtractor\publish\IconExtractor.exe` from the CUE4Parse
  submodule. Requires `.NET 8 SDK` for IconExtractor specifically.
- **`*.usmap`**: located via newest-mtime in the mod root. UE4SS dumps
  one when you press Ctrl+Num6 in-game.

Layout:

```
.\Sources\Vanilla        ~1097 vanilla item JSONs   (gitignored)
.\Icons                  per-item PNG + JSON sidecars (gitignored)
.\Profiles\_builtin      11 read-only profile templates (tracked)
.\Profiles\<id>.json     user profiles                (gitignored)
.\Builds                 finished .pak files          (gitignored)
.\.build-tmp             scratch dir for in-flight builds (gitignored)
```

---

## 3. Run the GUI

```powershell
cd .\GUI
dotnet run -c Release
# -> http://localhost:17777
```

Top-level layout:

| Pane | What |
|---|---|
| Header | Profile dropdown + New / Duplicate / Rename / Save / Delete / Build |
| Left | Stack-size globals (None / Multiplier / Absolute, with optional Cap) and a live status block (total / overrides / will-be-modified / promoted) |
| Right | Filterable item list. Each row: icon, localized name, computed target, inline override input |
| Footer | Build log + a link back to `/items-test.html` for the raw debug view |

Resolver rule (per item, for `MaxCountInSlot`):

```
overrides[itemId].stackSize
?? globals.stackSize.absolute
?? vanilla * globals.stackSize.multiplier (clamped at cap)
?? null      // skip -> stays vanilla in-game
```

For items with `vanillaStack <= 1`, globals only apply when the item is
"promotable" -- `ItemClass == Consumable`, or `ItemClass == Default && Category == Resource`.
Equipment / NPCs / Ship cannons / Quest tokens at stack=1 stay at 1
unless the user sets an explicit per-item override.

---

## 4. Headless / CLI mode

The GUI binary doubles as a CLI for headless / CI use:

```powershell
# Build a builtin (or any user profile, by id or display name)
dotnet run --project GUI -- --test-patcher --profile x4
dotnet run --project GUI -- --test-patcher --profile "My Stacks"

# Direct one-shot without a profile (legacy multiplier semantics)
dotnet run --project GUI -- --test-patcher --multiplier 4 --build-pak
dotnet run --project GUI -- --test-patcher --absolute 9999 --build-pak

# Patch only (no pack), keep temp dir for inspection
dotnet run --project GUI -- --test-patcher --multiplier 4 --out .\.build-tmp\inspect
```

---

## 5. HTTP API

The GUI is just a thin shell over a small REST API -- handy if you
want to drive builds from another tool:

| Method | Path | Purpose |
|---|---|---|
| GET | `/api/items` | list of all `R5BLInventoryItem` JSONs + icon URL + localized meta |
| GET | `/api/profiles` | list of profile summaries |
| GET | `/api/profiles/{id}` | full profile |
| POST | `/api/profiles` | create user profile (server picks GUID) |
| PUT | `/api/profiles/{id}` | overwrite user profile |
| DELETE | `/api/profiles/{id}` | delete user profile |
| POST | `/api/profiles/{id}/duplicate` | clone (incl. builtin -> user) |
| POST | `/api/build` body: `{profileId}` | run the full pipeline synchronously |

Builtins are read-only via the API: `PUT` / `DELETE` on a builtin returns
`403`. The supported way to "edit" a builtin is `POST /duplicate` and
edit the clone.

---

## 6. Profile schema (Profiles\_builtin\x4.json)

```json
{
  "id": "11000000-0000-4000-8000-000000000004",
  "name": "x4",
  "description": "All vanilla stacks quadrupled.",
  "createdAt": "2026-05-08T00:00:00+00:00",
  "modifiedAt": "2026-05-08T00:00:00+00:00",
  "globals": {
    "stackSize": { "multiplier": 4 }
  },
  "overrides": {}
}
```

Item-centric overrides + per-property globals. The structure is set up to
grow:

- new property (e.g. `weight`): add a typed global container
  (`globals.weight = { ... }`) and a field on `ItemOverride`
  (`overrides[id].weight`); old profiles stay backward-compatible
  because missing fields are simply `null`.
- new globals modes: extend `StackSizeGlobal` -- the resolver picks
  `Absolute` over `Multiplier` already; add new branches as needed.

User profiles live at `Profiles\<id>.json` (gitignored). Builtins live at
`Profiles\_builtin\<slug>.json` (tracked, read-only).

---

## 7. Repo layout

```
Stack Size\
+-- Dump-WindroseVanilla.ps1       Setup: extract vanilla item JSONs
+-- Extract-Icons.ps1              Setup: extract per-item icons + meta
+-- Library\
|   +-- Common.ps1                 Shared PS helpers (logging, paths, tooling resolvers)
|   +-- Dump.ps1                   Invoke-WindroseVanillaDump
|   +-- Icons.ps1                  Invoke-WindroseIconExtract
+-- Tools\
|   +-- StackSizeCore\             C# class library: Profile, ProfileStore,
|   |                              StackPatcher, PakBuilder, RepakResolver,
|   |                              BuildPipeline
|   +-- IconExtractor\             C# CLI: walks vanilla JSONs, pulls icons +
|   |                              localized strings out of the IoStore container
|   +-- CUE4Parse\                 git submodule (UE pak / IoStore reader)
+-- GUI\
|   +-- GUI.csproj                 ASP.NET minimal-API shell (.NET 10)
|   +-- Program.cs                 Wires endpoints + static files
|   +-- Endpoints\                 ItemsEndpoint, ProfilesEndpoint, BuildEndpoint
|   +-- ItemDto.cs, PatcherCli.cs  ...
|   +-- wwwroot\                   index.html (configurator), items-test.html (debug),
|                                  app.css, app.js, style.css, script.js
+-- Profiles\
|   +-- _builtin\                  x2..x10, 999, 9999 (tracked, read-only)
|   +-- <id>.json                  user profiles (gitignored)
+-- Sources\Vanilla\               extracted vanilla JSONs (gitignored)
+-- Icons\                         per-item PNG + sidecar JSON (gitignored)
+-- Builds\                        finished .pak files (gitignored)
+-- .build-tmp\                    scratch dir for in-flight builds (gitignored)
+-- repak.exe                      auto-downloaded on first use (gitignored)
+-- *.usmap                        generated by UE4SS Ctrl+Num6 (gitignored)
```

---

## 8. Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `SHA256 mismatch for repak_cli-*.zip` | Download corrupted, or trumank rotated the pinned release | Delete `repak.exe` and retry; if it persists, bump `PinnedVersion` in `Tools\StackSizeCore\RepakResolver.cs` and `WindroseRepakVersion` in `Library\Common.ps1` |
| `Could not find a Windrose vanilla pak under any Steam library` | Windrose isn't installed via Steam, or it's installed in a non-standard location Steam doesn't track | `.\Dump-WindroseVanilla.ps1 -VanillaPak <path>` |
| `CUE4Parse submodule is not initialized` | First clone forgot the submodule | `git submodule update --init Tools/CUE4Parse` |
| `No *.usmap file found` | Icon extractor needs a UE5 mappings file | Press Ctrl+Num6 in-game with UE4SS Keybinds active, drop the produced `.usmap` in the mod root |
| `Profile produces no changes -- nothing to pack` | Profile has neither globals nor overrides | Pick a Multiplier / Absolute mode, or add at least one override |
| `Builtin profiles cannot be modified` | Tried to edit a built-in profile in the GUI | Click `Duplicate` first; user copies are editable |
| Encoding issues (`???` in JSONs) | Old Windows PowerShell ANSI fallback | Already handled: scripts read/write via UTF-8 explicitly |

---

## 9. What this repo does NOT do

- **No auto-deploy.** Server / client paths are not configurable. You
  copy `_P.pak` files into the `~mods` folder yourself.
- **No `.uasset` mods.** This pipeline targets JSON-based R5BusinessRules
  mods. Mesh / material / animation mods need different tools (FModel,
  UAssetGUI, repak unpack/repack).
- **No multi-mod orchestration.** Only one `StackSize_*.pak` should live
  in `~mods` at a time; remove the previous build before installing a new one.
