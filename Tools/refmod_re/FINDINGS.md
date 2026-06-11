# R5ModSettings (Referenz-Mod) - Reverse-Engineering-Befunde

Ziel: verstehen, **warum die Referenz-Mod beim Reopen des Settings-Menüs zuverlässig
rendert** und unsere #18e-#18o-Versuche nicht. Komplett offline aus `main.dll`
(UE4SS-C++-Mod, 532 KB) via pefile + capstone rekonstruiert.

## Kernursache (bewiesen, nicht mehr vermutet)

Die Referenz baut + mountet ihr Panel **im POST-Callback eines per-UFunction-Hooks
auf `BP_Settings_SC_C:CookTabs`**. `CookTabs` ist die BP-Funktion, die bei **jedem**
Öffnen den Tab-Content frisch kocht. Der Mount passiert damit **synchron im Cook-Frame**,
genau wenn das Spiel den Content-Tree neu aufbaut und Slate realisiert.

Warum wir das nie erreichten:
- `CookTabs` dispatcht über den **Script-VM-Pfad** (`exec == ProcessInternal`), **nicht**
  über `UObject::ProcessEvent`. Unser globaler PE-Hook ist dafür blind.
- Unser ProcessInternal-Rider feuert für `CookTabs` zur Laufzeit faktisch nicht
  (Log: nur `OnEnter`/`OnExit`, nie `CookTabs`).
- Alle unsere Mounts (OnEnter, Klick, PE-post-OnInitialized) lagen im **falschen
  Cook-Fenster** -> Slot wurde nur als UObject angehängt, nie Slate-realisiert.

## Hook-Mechanismus (das fehlende Werkzeug)

`sub_180015AF0` = `install_or_defer_hook(state, funcPath, preCb, postCb)`:
1. `FindObject(funcPath)` -> UFunction (z.B. `..BP_Settings_SC_C:CookTabs`)
2. `UObjectGlobals::RegisterHook(fn, preCb, postCb, custom)` (UE4SS-Import)
3. nicht gefunden -> in Pending-Liste, später Retry.

`RegisterHook` patcht die **UFunction selbst** (ersetzt `Func`/ExecFunction durch einen
Thunk), daher feuert der Callback bei **jeder** Invokation - auch BP-intern über den VM.

Gehookte UFunctions (Pfade als Strings in `sub_180016180`):
| UFunction | Zweck (Post/Pre-Callback) |
|---|---|
| `BP_Settings_SC_C:CookTabs` | **Per-Open Build+Mount** (`sub_18001CE10`) |
| `WBP_MetaUI_TabsGroup_C:SetData` | Mods-Tab in die native Tab-Data-Array injizieren |
| `WBP_Settings_Screen_C:OnTabsStateChanged` | Tab-Wechsel -> Panel show/hide (`sub_18001D090`) |
| `BP_Settings_SC_C:OnExit` | Runtime-State reset |
| div. Entry-Delegates (Switcher/Scalar/Discrete/KeyBinding/ArtButton) | Wert-Persistenz |
| `RegisterLoadMapPre/PostCallback` | Reset über Level-Loads |

## Per-Open Build+Mount Pipeline

`CookTabs post (sub_18001CE10)` -> ready-check `sub_18003E840` (resolve
`SettingsScreenWidget`/`TabsWidget`/`hbox_Tabs`) -> `sub_18001C350` (resolve sc, screen,
tabs, hbox, tabCount, **content-parent**) -> `sub_18001D960` -> `sub_180029150` ->
**`sub_180025E00`** (Build+Mount):

1. `screen->WidgetTree` lesen (`sub_180040CB0` = GetObjectProperty by name).
2. Cache-Check: wenn `gespeicherter_parent == content-parent && gespeicherter_screen ==
   screen` -> reuse. Beim Reopen ist der **Screen ein frisches UObject** -> Cache miss ->
   **immer Fresh-Rebuild** (altes Panel via RemoveFromParent verworfen).
3. `StaticConstructObject(WidgetTree, UScrollBox,  "R5ModSettings_ModsPanel")`  -> panel
4. `StaticConstructObject(WidgetTree, UVerticalBox,"R5ModSettings_ModsContent")` -> content
5. `AddChild(panel, content)` (ScrollBox enthält die VerticalBox)
6. Entries bauen (`WBP_Settings_EntryHeader/Switcher/Scalar/Discrete/KeyBinding_C` +
   `WBP_ArtButton_TiledText_C`) und je `AddChild(content, entry)`.
7. **`slot = AddChild(content-parent, panel)`** (Mount in den Screen). slot==null ->
   "Could not mount Mods panel".
8. Erfolg: `slot->SetFill(1.0f,true)`, **`SetVisibility(panel, Collapsed=1)`** (versteckt
   gebaut). Beim Mods-Tab-Klick -> `SetVisibility(panel, Visible=0)`.

Reflection-Primitive (alle via `GetFunctionByNameInChain` + `ProcessEvent`):
- `sub_18003FC60` = `AddChild(parent, child) -> UPanelSlot*`
- `sub_1800411C0` = `SetVisibility(widget, ESlateVisibility uint8)`
- `sub_180040080` = `StaticConstructObject(WidgetTree, class, FName name)` (CreateWidget-Äquiv.)
- `sub_180027B30` = UClass-Lookup by short name ("ScrollBox"/"VerticalBox")

## Konsequenz für Quartermaster

Unsere Mod macht inhaltlich fast dasselbe (Tab injizieren, eigenes ScrollBox-Panel,
Visibility-Gate, Button). **Einziger struktureller Fehler: der Mount läuft nicht im
CookTabs-Post.** Fix = denselben Hook-Layer nachbauen:

- Unsere DLL liest `UFunction::ExecFunction` bereits (`qm_modtab.cpp` enum). Wir können
  `CookTabs->ExecFunction` auf einen eigenen Thunk setzen, im Thunk das Original
  (`ProcessInternal`) aufrufen und **danach** (Post) unseren Fresh-Build+Mount in den
  aufgelösten Content-Parent ausführen - exakt der bewiesen-korrekte Moment.
- Panel versteckt (Collapsed) bauen, beim QM-Tab-Klick via bestehendem #18d-Gate zeigen.

Artefakte in diesem Ordner: `re_overview.py` (PE), `re_strings.py` (+`strings_all.txt`),
`re_disasm.py` (Capstone + .pdata-Funktionsgrenzen + Xref), `re_callgraph.py`.
