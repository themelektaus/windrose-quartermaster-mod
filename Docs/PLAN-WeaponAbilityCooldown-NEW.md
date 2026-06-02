# Plan: Weapon Special Ability Cooldowns (Soul Eater / Soul Harvest)

Status: **Planungsnotiz** - noch nicht umgesetzt. Aufgegriffen aus einer
User-Anfrage (Nexus): *"is it possible to edit Soul Eater's Special Ability
cooldown? 3 minutes is really long."*

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

## Generalisierungs-Potenzial (dieselbe CurveTable)

`CT_Weapon_GE_Values` haelt GE-Werte vieler Waffen. Cooldown-aehnliche Rows:

| Row | Bedeutung |
|---|---|
| `Greatsword_Souldrinker_AbilityCooldown` | **Soul Eater Soul-Harvest-CD (180s)** - die einzige echte "AbilityCooldown" |
| `Halberd_Corrupted_DmgRegistrationCooldown` | Sub-Mechanik (Damage-Tick), kein klassischer Ability-CD |
| `Rapier_Eviscerate_HealCooldown` | Sub-Mechanik (Heal-Tick) |
| `Saber_Corrupted_DmgRegistrationCooldown` | Sub-Mechanik (Damage-Tick) |

Andere Waffen-Abilities haben **eigene** Cooldown-GEs mit eventuell anderem
Shape (vor Generalisierung pruefen):
- `GE_Wpn_MainHand_Rapier_Eviscerate_Advanced_ScourgeMarkHeal_Cooldown`
- `GE_Wpn_MainHand_Saber_Corrupted_Base_CorruptionBurst_Cooldown`
- `GE_Wpn_TwoHand_Halberd_Corrupted_Base_CorruptionBurst_Cooldown`

Empfehlung: **zuerst nur Soul Eater** (eindeutig, der konkrete User-Request).
Den Patcher aber so bauen, dass weitere `(CurveTable, Row)`-Eintraege spaeter
trivial dazukommen.

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

## Offene Fragen (vor Implementierung verifizieren)

1. **Curve-Struktur**: Ist die Row `Greatsword_Souldrinker_AbilityCooldown` eine
   `RichCurve`/`SimpleCurve` mit einem einzelnen Key (konstant 180), oder
   level-skaliert (mehrere Keys ueber Item-Level 1..15)? Bestimmt, ob wir einen
   Key oder alle Keys der Row skalieren. (CurveLevel im JSON ist `0` -> spricht
   fuer einen flachen/konstanten Wert, aber im uexp gegenpruefen.)
2. **Base vs Advanced**: Teilen sich beide Waffen-Tiers die *eine* Row
   `Greatsword_Souldrinker_AbilityCooldown` (kein `_Base`/`_Advanced`-Suffix in
   der Row gesehen -> wahrscheinlich ja, ein Edit deckt beide).
3. **UAssetAPI-Zugriff auf CurveTable**: Row als NormalExport finden, Key-Value(s)
   setzen. Pruefen, welcher PropertyType die Keys traegt (FRichCurveKey-Struct
   mit `Value`-Float).

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

## Out of Scope (fuer jetzt)

- Die anderen Weapon-Ability-Cooldowns (Halberd/Rapier/Saber) - erst wenn
  jemand danach fragt; Patcher ist darauf vorbereitet.
- Absolute Sekunden-Eingabe statt Multiplikator - der Tab nutzt durchgaengig
  Multiplikatoren, dabei bleiben.
