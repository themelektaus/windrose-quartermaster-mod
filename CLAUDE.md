# Quartermaster - Windrose Mod-Workspace

Mehrere parallele Mod-Workstreams fuer das Spiel
[Windrose](https://www.nexusmods.com/windrose) (UE5.6, R5-Build).

## Workstreams

| Bereich | Pfad | Tech |
|---|---|---|
| **Configurator + Pak-Build-Pipeline** | `GUI/`, `Sources/`, `Tools/QuartermasterCore/` | C# .NET 10 + WPF + WebView2, baut `Quartermaster_*.pak` |
| **DLL-Mod (Build-Mode-Slot-Inject)** | `Tools/DllProxy/dxgi/` | C++ DXGI-Proxy, UE5-Reflection-Hook auf `GetBuildingGroupsByCategoryTag` |
| **Asset-Mods** (Pak-only) | diverse `Docs/WIP_*.md` | Pak-Triplets (`.pak`/`.ucas`/`.utoc`) gebaut via `retoc` |
| **Tooling** | `Tools/ar_writer/`, `Tools/Dumper7Setup/` | Python AR-Patcher, Dumper-7 SDK-Generator |

## Wichtige Pfade

| Was | Pfad |
|---|---|
| Workspace-Root | `E:\Windrose\Mods\Quartermaster\` |
| Game-Install | `E:\Games\steamapps\common\Windrose\` |
| Deploy-Ziel Paks | `E:\Games\steamapps\common\Windrose\R5\Content\Paks\~mods\` |
| Deploy-Ziel DLL | `E:\Games\steamapps\common\Windrose\R5\Binaries\Win64\dxgi.dll` |
| DLL-Runtime-Log | `%LOCALAPPDATA%\R5\Saved\Logs\Quartermaster_Inject.log` |
| Referenz-Mods (read-only) | `E:\Windrose\Mods\Quartermaster\References\` |
| usmap (UE-Reflection) | `R5-5.6.1-0+UE5-20260518.usmap` (root, Zstd-komprimiert, von Dumper-7) |

## DLL-Mod Build/Deploy

Bash-Befehle mit Forward-Slashes und Quotes. Die `.bat`-Scripts
machen `pushd "%~dp0"` als erstes, daher egal von wo aufgerufen.

| Zweck | Befehl |
|---|---|
| Dev-Build (Diag-Logs) | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat"` |
| Release-Build (schlank) | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat" release` |
| Deploy DLL | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/deploy.bat"` |
| Uninstall DLL | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/uninstall.bat"` |
| **Build + Deploy (chained)** | `"E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/build.bat" && "E:/Windrose/Mods/Quartermaster/Tools/DllProxy/dxgi/deploy.bat"` |

**Workflow-Regel:** Nach jedem erfolgreichen DLL-Build IMMER direkt
deployen - nicht erst auf Bestaetigung warten. Chained Pattern mit
`&&` macht das automatisch (Deploy nur bei erfolgreichem Build).

Build-Output: Dev ~190 KB (`log-level=5 diag=1`), Release ~181 KB
(`log-level=3 diag=0`). Build-Type steht beim DLL-Start im Log in
der `Build:`-Zeile.

## Configurator (C# GUI)

```powershell
# Dev (Desktop-Window, hot reload)
dotnet run --project GUI\App -c Release

# Single-File-EXE bauen (94 MB, portabel)
dotnet publish GUI\App -p:PublishProfile=win-x64
```

Mehr Details in `README.md`.

## Pak-Mod Build (Asset-Mods)

Generelle Tools direkt im Workspace-Root:

- `retoc.exe` - IoStore-Container bauen (`.ucas`+`.utoc`)
- `repak.exe` - Legacy-Pak-Wrapper (`.pak`)
- `ffmpeg.exe` - Audio-Konvertierung fuer Ship-Music

Build-Skripte und Sources liegen pro Mod im jeweiligen `Sources/`-Subfolder.

## Steam-Update Recovery (DLL-Offsets)

Wenn nach einem Game-Update die hardcoded Offsets in
`Tools/DllProxy/dxgi/qm_ue.hpp` nicht mehr stimmen (Symptom:
`init NEVER reached 100000 objects` im Log):

1. Windrose normal via Steam starten + in Welt laden
2. `Tools\Dumper7Setup\run_dump.bat` doppelklicken (F8 = Dump, F6 = Unload)
3. Neue Offsets aus `Tools/Dumper7Setup/output/<UE-Version>/CppSDK/SDK/Basic.hpp`
   uebernehmen (`OFFSET_GObjects`, `OFFSET_AppendString`, `OFFSET_GWorld`,
   `OFFSET_ProcessEvent`)
4. Build + Deploy

Note: `qm_scan.cpp` macht zur Laufzeit auch einen Validation-Scan -
falls hardcoded Offsets stale und der Scan greift, sieht man im Log
`[Scan] rescan: GObjects relocated`. Hardcoded Offsets bleiben dann
trotzdem Fallback und sollten nachgezogen werden.

## Submodule

| Modul | Pfad | Upstream |
|---|---|---|
| MinHook | `Tools/DllProxy/dxgi/minhook` | TsudaKageyu/minhook |
| Dumper-7 | `Tools/Dumper7` | Encryqed/Dumper-7 |
| CUE4Parse | `Tools/CUE4Parse` | (Pak-Inspection im Configurator) |
