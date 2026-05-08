# Windrose Stack Size Mod

Configurator + build pipeline for stack-size paks for
[Windrose](https://www.nexusmods.com/windrose). A small web GUI lets you
edit profiles -- a profile is a global stack-size policy (multiplier or
absolute value) plus a list of per-item overrides -- and produces a
finished `_P.pak` you drop into the `~mods` folder.

Vanilla values are extracted directly from the game's main pak file
(`pakchunk0-WindowsServer.pak` or `pakchunk0-Windows.pak`) -- no external
reference mod needed.

For more details (icon extraction, internals) see [`DETAILS.md`](./DETAILS.md).

---

## One-time setup

You need:

- **.NET 10 SDK** (or newer preview) -- the GUI and pipeline are C#.
- **PowerShell 5.1+** -- only for the two setup scripts below.
- **Internet access (first run only)** -- `repak.exe` is auto-downloaded
  (pinned v0.2.3, SHA256-verified).
- **CUE4Parse submodule** initialized -- needed by the icon extractor:
  ```powershell
  git submodule update --init Tools/CUE4Parse
  ```

Two one-time data dumps (re-run after a Windrose patch that changes items):

```powershell
# 1. Extract the vanilla item JSONs from the local Windrose install.
.\Dump-WindroseVanilla.ps1

# 2. Extract per-item icons + localized metadata (optional but the GUI uses them).
.\Extract-Icons.ps1
```

The Steam install of Windrose is auto-detected via the registry +
`libraryfolders.vdf`. Pass `-VanillaPak <path>` if your pak lives outside
any Steam library (e.g. a dedicated server install).

The icon extractor needs a UE5 `.usmap` next to it. Generate one once via
UE4SS (`Ctrl+Num6` in-game with the Keybinds mod active) and drop the
resulting `*.usmap` file into the mod root.

## Run the configurator

```powershell
cd .\GUI
dotnet run -c Release
```

Then open <http://localhost:17777>.

The dropdown shows 11 built-in profiles (`x2`...`x10`, `999`, `9999`)
that reproduce the legacy variant grid. Built-ins are read-only -- click
**Duplicate** to create an editable copy.

For each profile you can:

- Pick a **global stack-size mode**: None, `vanilla * Multiplier` (with
  optional Cap), or a flat `Absolute` value.
- Set **per-item overrides** that win over the global policy, even for
  items that are normally locked at stack=1 (Equipment, NPCs, Ship cannons,
  Quest tokens).
- Press **Build .pak** to run the patch + pack pipeline. The finished
  `_P.pak` lands in `Builds\` after roughly a second.

User profiles persist as `Profiles\<id>.json` (gitignored). Builtins live
under `Profiles\_builtin\` (tracked).

## Headless build

Same pipeline without the browser:

```powershell
# Build a builtin
dotnet run --project GUI -- --test-patcher --profile x4

# Build a user profile (by id or name)
dotnet run --project GUI -- --test-patcher --profile "My Stacks"

# Direct multiplier without a profile
dotnet run --project GUI -- --test-patcher --multiplier 4 --build-pak
```

## Install a pak

Copy the produced `.pak` into the `~mods` folder of your server or client:

```powershell
Copy-Item .\Builds\StackSize_x4_P.pak `
  'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

Only **one** `StackSize_*.pak` per `~mods` folder -- remove any older one first.
