# Windrose Quartermaster

Configurator + build pipeline for JSON-based mods for
[Windrose](https://www.nexusmods.com/windrose). A small web GUI lets you
edit profiles - a profile bundles item-stack-size tweaks and loot-table
edits (with more domains to come) - and produces a single `_P.pak` you
drop into the `~mods` folder.

Vanilla values are extracted directly from the game's main pak file
(`pakchunk0-WindowsServer.pak` or `pakchunk0-Windows.pak`) - no external
reference mod needed.

For more details (architecture, internals) see [`DETAILS.md`](./DETAILS.md).

---

## Prerequisites

- **.NET 10 SDK** (or newer preview) - everything is C# now.
- **Windrose installed via Steam** - auto-detected via the registry +
  `libraryfolders.vdf`. Dedicated-server / non-Steam installs work too,
  you just need to point at a pak path.
- **Git** on the PATH - the configurator transparently runs
  `git submodule update --init Tools/CUE4Parse` on first use to pull the
  CUE4Parse reader the icon extractor needs. (No need to do it yourself,
  but the binary has to be reachable.)
- **A UE5 `*.usmap` file in the mod root** - generated once by UE4SS via
  the built-in dumper. With UE4SS' Keybinds mod active, press
  `Ctrl+Num6` in-game; the dumper writes a file like
  `R5-5.6.1-0+UE5-<hash>.usmap` next to `UE4SS.exe`. Copy that file into
  the mod root.

`repak.exe` is auto-downloaded (pinned v0.2.3, SHA256-verified) on first
use. There are no PowerShell scripts left - everything runs through the
GUI or the headless CLI shim.

---

## Run the configurator

Two equivalent ways: a desktop window (recommended) or a browser tab.

### Desktop launcher (WPF + WebView2)

```powershell
cd .\App
dotnet run -c Release
```

Opens a single Quartermaster window backed by Microsoft Edge WebView2.
Kestrel is hosted in-process on a free port (no fixed `:17777` collision,
multiple instances can run side-by-side). Closing the window stops the
server cleanly.

Requires the **Microsoft Edge WebView2 Runtime** -- preinstalled on
Windows 11 and recent Windows 10 builds. If missing, the launcher links
to the
[evergreen installer](https://developer.microsoft.com/microsoft-edge/webview2/).

### Browser

```powershell
cd .\GUI
dotnet run -c Release
```

Then open <http://localhost:17777>.

**On first start the GUI is empty** until the vanilla item JSONs +
icons are extracted. The setup overlay does that for you: when it
detects a missing piece (no `Sources\Vanilla`, no `Icons\*.png`) it
auto-runs the dump + icon-extraction pipeline and streams the live
log into the page. ~30-90 seconds total. Subsequent launches skip
straight into the configurator.

The dropdown shows 11 built-in profiles (`x2`...`x10`, `999`, `9999`)
that reproduce the legacy variant grid. Built-ins are read-only - click
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

## Headless CLI

Same pipeline without the browser:

```powershell
# One-time setup (dump vanilla JSONs + extract icons). Skips steps that
# are already done; pass --force to re-run everything.
dotnet run --project GUI -- --setup

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
Copy-Item .\Builds\Quartermaster_x4_P.pak `
  'E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\' -Force
```

Only **one** `Quartermaster_*.pak` per `~mods` folder -- remove any older one first.
