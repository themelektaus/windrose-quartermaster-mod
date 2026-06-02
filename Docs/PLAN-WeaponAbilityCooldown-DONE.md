# Plan: Weapon Special Ability Cooldowns (Soul Eater / Soul Harvest)

Status: **Umgesetzt** (Scope: nur Soul Eater). Aufgegriffen aus einer
User-Anfrage (Nexus): *"is it possible to edit Soul Eater's Special Ability
cooldown? 3 minutes is really long."*

Implementiert via neuem `WeaponAbilityCooldownPatcher` (CurveTable-Row-Patch),
verdrahtet als Cooldown-Job-Shape `WeaponAbilityCurve`, Profil-Feld
`Cooldowns.SoulEaterAbilityMultiplier`, plus Slider "Soul Eater - Soul Harvest"
im Cooldowns-Tab. Die anderen "Cooldown"-Rows (Halberd/Rapier/Saber) wurden
am 2026-06-02 vermessen und als interne Tick-Intervalle (0,35/0,35/2 s) - keine
echten Ability-Cooldowns - identifiziert und bewusst nicht verdrahtet (Details
im Generalisierungs-Abschnitt). Soul Eater bleibt der einzige.

## Kurzfassung

Ja, moddbar - mit derselben Pak-Build-Technik wie der bestehende Cooldowns-Tab,
aber es braucht **einen neuen Patcher-Shape**: der Wert liegt nicht als Inline-
Float im GameplayEffect, sondern in einer geteilten **CurveTable-Row**. Der
bestehende `CooldownsPatcher` (editiert `ScalableFloatMagnitude.Value` inline)
greift hier deshalb **nicht**.

## Identifikation: was "Soul Eater" technisch ist

- **Waffe**: Souldrinker-Greatsword
  - `R5/Plugins/R5BusinessRules/Content/InventoryItems/Equipments/Weapon/DA_EID_MeleeWeapon_Greatsword_Souldrinker_Base.json` (+ `_Advanced`)
  - ItemTag `EquipData.MeleeWeapon.Greatsword.Souldrinker.Base`
  - EN-Name (InventoryItems.csv, Spalte 3 = Source) = "Souldrinker";
    RU-Name = "Пожиратель душ" = woertlich **"Soul Eater"** -> daher der
    Name beim User (vermutlich RU-Client oder lose Uebersetzung).
- **Special Ability** = "Soul Harvest" (`[F]`):
  - InventoryItems.csv, Key `EID_MeleeWeapon_Greatsword_Souldrinker_Base_Effect1`:
    *"This weapon has a special attack [F] that drains Health from nearby
    enemies. Cooldown: {0} min."*
  - Ability-BP: `GA_Wpn_TwoHand_Souldrinker_Base_SoulHarvest` (+ `_Advanced`)
  - Ability-Params: `DA_Wpn_TwoHand_Souldrinker_Base_SoulHarvest_AbilityParams`

## Wo der Cooldown-Wert sitzt

Der `{0}`-Platzhalter ("3 min") wird per `DisplayType: SecondsAsMinutes` aus
einer **CurveTable** formatiert (siehe `ItemDescriptionData` in der Waffen-JSON):

- **CurveTable**: `R5/Content/Gameplay/ItemsLogic/Weapon/Shared/CT_Weapon_GE_Values.uasset`
- **Row**: `Greatsword_Souldrinker_AbilityCooldown` = **`180.0`** (= 3 min, im
  uexp per `od -t fF` als Float verifiziert: `180` ist in der Table praesent)
- **Konsument**: `R5/Content/Gameplay/ItemsLogic/Weapon/Wpn_TwoHand/Greatsword_Souldrinker_Base/SoulHarvestAbility/GE_Wpn_TwoHand_Souldrinker_Base_SoulHarvest_Cooldown.uasset`
  - dessen `.uasset`-Name-Map enthaelt `CurveTable`, `CT_Weapon_GE_Values` und
    `Greatsword_Souldrinker_AbilityCooldown` -> die `DurationMagnitude` des GE
    ist ein **ScalableFloat mit CurveTable-Ref** (kein Inline-`Value`-Float).

Heisst: **wir editieren die CurveTable-Row**, nicht den GE. Der GE bleibt
unangetastet und liest den neuen Wert ueber die Curve.

## Warum der bestehende CooldownsPatcher nicht reicht

`CooldownsPatcher.PatchScalableFloatDuration` sucht
`DurationMagnitude > ScalableFloatMagnitude > Value` (FloatProperty) und
multipliziert den. Bei Soul Eater ist der `Value` aber nicht inline gesetzt -
die Magnitude zeigt auf die Curve. Der Patcher wuerde mit
*"No Value FloatProperty inside ScalableFloatMagnitude"* werfen.

`PatchTopLevelMagnitude` (fuer BP_Calc-Faelle) passt ebenfalls nicht.

-> Neuer Shape noetig: **CurveTable-Row-Patcher**.

## Generalisierungs-Potenzial (dieselbe CurveTable) - VERIFIZIERT, kein Bedarf

`CT_Weapon_GE_Values` haelt GE-Werte vieler Waffen. Am 2026-06-02 wurde die
ganze Table frisch gedumpt (UAssetAPI-Probe, FName-verankert) und **jede**
"Cooldown"-Row mit ihrem Vanilla-Wert vermessen:

| Row | Vanilla-Wert | Was es WIRKLICH ist |
|---|---:|---|
| `Greatsword_Souldrinker_AbilityCooldown` | **180 s** (3 min) | echter Soul-Harvest-`[F]`-Ability-CD - die **einzige** echte "AbilityCooldown" (umgesetzt) |
| `Halberd_Corrupted_DmgRegistrationCooldown` | **0,35 s** | Damage-Tick-Intervall (Schadens-Registrierung), KEIN Ability-CD |
| `Rapier_Eviscerate_HealCooldown` | **2 s** | Heal-Tick-Intervall, KEIN Ability-CD |
| `Saber_Corrupted_DmgRegistrationCooldown` | **0,35 s** | Damage-Tick-Intervall, KEIN Ability-CD |

Wichtig: Die Cooldown-GEs `GE_..._CorruptionBurst_Cooldown` (Halberd/Saber) und
`GE_..._ScourgeMarkHeal_Cooldown` (Rapier) **lesen** zwar genau diese Rows -
aber die gemessenen Werte (0,35 s / 0,35 s / 2 s) zeigen eindeutig: das sind
interne Mechanik-Intervalle (wie oft Schaden/Heal tickt), **nicht** der
player-facing "druecke F, warte X Minuten"-Recharge wie bei Soul Eater (180 s).

**Entscheidung (2026-06-02, mit User):** Diese drei Rows werden **nicht**
verdrahtet. Ein Slider darauf waere irrefuehrend (User erwartet einen Cooldown
a la Soul Eater) und wuerde bei 0,01x die Tick-Mechanik der Waffen verbiegen,
ohne nuetzlichen Effekt. Soul Eater bleibt der einzige Weapon-Ability-Cooldown
im Tool. Der `WeaponAbilityCooldownPatcher` ist generisch ueber den Row-Namen,
falls je eine echte lange Ability-CD-Row dazukommt (Game-Update).

## Reproduzierbare Recon (fuer Implementierung)

Alle Tools liegen im Repo-Root; AES-Key = `WindroseGameSecrets.AesKey`.

```bash
KEY=0x5F43...CFAE   # WindroseGameSecrets.AesKey
PAKS="E:/Games/steamapps/common/Windrose/R5/Content/Paks"

# 1) AssetRegistry extrahieren + parsen (lesbare Package-Pfade)
./repak.exe -a $KEY get "$PAKS/pakchunk0-Windows.pak" "R5/AssetRegistry.bin" > AssetRegistry.bin
./retoc.exe asset-registry AssetRegistry.bin > ar.txt
grep -iE "souldrinker|SoulHarvest|CT_Weapon_GE_Values" ar.txt

# 2) Cooldown-GE + CurveTable nach Legacy konvertieren (INPUT = Paks-DIR wegen ScriptObjects/global.utoc)
./retoc.exe -a $KEY to-legacy "$PAKS/" out --version UE5_6 \
    --filter "Souldrinker" --filter "CT_Weapon_GE_Values" --no-shaders

# 3) Lokalisierung (Anzeigename + Effect-Text) - CSV-Loc, kein .locres
./repak.exe -a $KEY get "$PAKS/pakchunk0-Windows.pak" \
    "R5/Content/Localization/Data/InventoryItems.csv" > InventoryItems.csv
grep -i "Souldrinker_Base_(ItemName|Effect1)" InventoryItems.csv
```

Hinweis: `retoc list` / `repak list` geben fuer IoStore nur Chunk-IDs bzw. den
Pak-Index; die **lesbaren** Asset-Pfade kommen aus `asset-registry`. Die
`.json`-Assets (R5BusinessRules-Daten) liegen dagegen direkt im legacy `.pak`.

## Offene Fragen (durch Spike geklaert)

1. **Curve-Struktur** -> **Single-Key-Konstante**. Jede Row in dieser Table ist
   eine Ein-Key-Kurve (Time=1.0, Value=<seconds>); kein Level-Skalierungs-Array.
   Soul-Eater-Row `Greatsword_Souldrinker_AbilityCooldown` = **180.0** (= 3 min).
   -> nur dieser eine Value-Float wird skaliert.
2. **Base vs Advanced** -> **eine geteilte Row** (kein `_Base`/`_Advanced`-Suffix
   in der Row). Ein Edit deckt beide Waffen-Tiers ab.
3. **UAssetAPI-Zugriff** -> UAssetAPI 1.1.0 hat **keinen** UCurveTable-Parser:
   die ganze RowMap liegt als roher `Extras`-Blob am CurveTable-Export. Verifizierte
   Serialisierung (UE5.6, R5) pro Row:
   `FName RowName (int32 idx, int32 0)` (8B) + Curve-Header `00 0B 01 01 00 00 00`
   (7B) + `float Time` @+15 + `float Value` @+19. Row-FName ist eindeutig.
   Patcher ankert am FName, prueft Struktur (Time finit/klein), skaliert Value@+19
   in-place; `asset.Write()` ist byte-stabil (verifiziert), `retoc to-zen` packt
   die gepatchte Table sauber zum Mod-Triplet.

## Implementierungs-Skizze

### 1) Patcher: `WeaponAbilityCooldownPatcher.cs` (neu, Core)

- Input: legacy `CT_Weapon_GE_Values.uasset` (via `retoc to-legacy --filter CT_Weapon_GE_Values`).
- Finde die Row-Export per Name (`Greatsword_Souldrinker_AbilityCooldown`).
- Setze den/die Key-`Value`(s) = vanilla * multiplier (Clamp 0.1..3.0, analog
  `CooldownsPatcher`).
- Schreibe Asset zurueck, zurueck nach Zen (`retoc to-zen`) ins Mod-Pak.
- Registry: `Dictionary<rowName, (curveTablePath)>` - generalisierbar.

### 2) Pipeline-Wiring (`BuildPipeline.Resolvers.cs` / `BuildPipeline.cs`)

- Neuer `CooldownJobShape.WeaponAbilityCurve` ODER eigener Job-Typ.
- `to-legacy --filter CT_Weapon_GE_Values` (eine Datei, ein Job, ggf. mehrere
  Rows in einem Durchgang patchen statt pro Row neu zu extrahieren).

### 3) Profile-Schema (`Profile.cs`)

```csharp
// in Cooldowns (oder neuer Block WeaponAbilities)
public double? SoulEaterAbilityMultiplier;   // null/1.0 = vanilla
```

### 4) GUI (`cooldowns.html` + `cooldowns.js`)

- Neue Card "Weapon Abilities" mit Slider "Soul Eater - Soul Harvest"
  (0.1x..3.0x, Readout in Minuten, vanilla 180s -> "3.0 min").
- Konsistent mit dem bestehenden `mult-pill`/`mult-readout`-Muster.

## Aufwand-Schaetzung

| Teil | LoC | Risiko |
|---|---:|---|
| CurveTable-Row-Patcher | ~120 | mittel (Curve-Key-Struktur in UAssetAPI) |
| Pipeline-Wiring | ~60 | gering (analog Cooldown-Jobs) |
| Profile + Resolver | ~30 | gering |
| GUI (Card + Slider) | ~60 | gering (Muster existiert) |
| **gesamt** | **~270** | **mittel** |

Hauptrisiko = der Curve-Key-Zugriff (offene Frage 1+3). Ein 30-min-Spike
(Row-Export im legacy uexp mit UAssetAPI lesen + dumpen) klaert das vorab.

## Out of Scope (entschieden)

- Halberd/Rapier/Saber "Cooldowns" - **verworfen** (siehe Generalisierungs-
  Abschnitt): sind interne Tick-Intervalle (0,35/0,35/2 s), keine echten
  Ability-Cooldowns. Verifiziert am 2026-06-02, bewusst nicht verdrahtet.
- Absolute Sekunden-Eingabe statt Multiplikator - der Tab nutzt durchgaengig
  Multiplikatoren, dabei bleiben.
