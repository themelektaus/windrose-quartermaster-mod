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
- **A UE5 `*.usmap` file** -- only needed when running from the source
  tree. The single-file EXE ships an embedded copy and seeds it into
  `QuartermasterData\` automatically, so end users don't need this. For
  game updates (UE-version bump), regenerate one with UE4SS Keybinds
  (Ctrl+Num6 in-game) and drop it into the data root -- newest mtime
  wins, so it transparently supersedes the embedded copy.

`repak.exe` is auto-downloaded (pinned v0.2.3, SHA256-verified) on first
use. There are no PowerShell scripts left - everything runs through the
GUI or the headless CLI shim.

---

## Run the configurator

Two equivalent ways: a desktop window (recommended) or a browser tab.

### Desktop launcher (WPF + WebView2)

```powershell
dotnet run --project GUI\App -c Release
```

Opens a single Quartermaster window backed by Microsoft Edge WebView2.
Kestrel is hosted in-process on a free port (no fixed `:17777` collision,
multiple instances can run side-by-side). Closing the window stops the
server cleanly.

Requires the **Microsoft Edge WebView2 Runtime** -- preinstalled on
Windows 11 and recent Windows 10 builds. If missing, the launcher links
to the
[evergreen installer](https://developer.microsoft.com/microsoft-edge/webview2/).

### Single-file build (one .exe to share)

```powershell
dotnet publish GUI\App -p:PublishProfile=win-x64
```

Produces a single self-contained `Quartermaster.exe` (~94 MB, all .NET +
WebView2 native libs + frontend + builtin profiles + a default UE5
`.usmap` + the CUE4Parse-backed icon extractor bundled, compressed) at
`GUI\App\bin\Publish\Quartermaster.exe`. You can drop it **anywhere** --
desktop, USB stick, `C:\Tools\`, doesn't matter. On first run a sibling
`QuartermasterData\` folder is created **next to the EXE** so the data
travels with it (USB-stick portable):

```
<wherever>\Quartermaster.exe
<wherever>\QuartermasterData\
  .webview2\                     <- WebView2 cache/cookies
  Profiles\_builtin\             <- seeded from embedded resources every start
  Profiles\<id>.json             <- user profiles you create
  Sources\Vanilla\               <- extracted by setup (1097 item JSONs)
  Icons\                         <- extracted by setup (1097 PNGs)
  Tools\IconExtractor\publish\   <- seeded from embedded zip on first run
  Tools\repak.exe                <- auto-downloaded from GitHub on first setup
  *.usmap                        <- seeded from embedded resource on first run;
                                    drop a newer one here after game updates
```

When you launch the EXE from inside the source repo (or any ancestor
folder containing `Profiles\_builtin\`), it stays in "dev mode" and
reads/writes against the repo paths instead. That way the standard
`dotnet run` workflow keeps using the tracked `Profiles\_builtin\`
files as the source of truth.

> **End-user prerequisites for the portable EXE**: the .NET **8 desktop
> runtime** must be installed (the bundled icon extractor is a
> framework-dependent net8.0 process; the WPF host itself ships its own
> .NET 10 runtime). Most modern Windows machines already have it via
> Windows Update or other apps; the runtime installer is a free
> ~70 MB download from <https://dotnet.microsoft.com/download/dotnet/8.0>.
> No SDK, Git, or CUE4Parse source needed any more.

### Browser

```powershell
dotnet run --project GUI\Web -c Release
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
  `_P.pak` lands directly in the game's `~mods` folder, ready to play.

User profiles persist as `Profiles\<id>.json` (gitignored). Builtins live
under `Profiles\_builtin\` (tracked).

## Headless CLI

Same pipeline without the browser:

```powershell
# One-time setup (dump vanilla JSONs + extract icons). Skips steps that
# are already done; pass --force to re-run everything.
dotnet run --project GUI\Web -- --setup

# Build a builtin
dotnet run --project GUI\Web -- --test-patcher --profile x4

# Build a user profile (by id or name)
dotnet run --project GUI\Web -- --test-patcher --profile "My Stacks"

# Direct multiplier without a profile
dotnet run --project GUI\Web -- --test-patcher --multiplier 4 --build-pak
```

## Install a pak

Builds from the GUI land directly in `<Windrose>\R5\Content\Paks\~mods\`,
nothing to copy. CLI builds (`--build-pak`) still write to the `Builds\`
folder so smoke tests don't touch the live game; copy from there manually
if you want a CLI-built pak in-game.

Only **one** `Quartermaster_*.pak` per `~mods` folder -- remove any older
one first (the **Mods** tab handles this with a single click).
