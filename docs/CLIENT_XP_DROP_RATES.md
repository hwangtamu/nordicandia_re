# Client-side XP rate & item-drop quantity — where they live

Research on the Android arm64 IL2CPP client (`tmp/apk-libil2cpp.so`, sha256
`529bd257…`, IL2CPP metadata in `tmp/android-metadata.json`).
VAs are the file's virtual addresses; **file offset = VA − 0x4000** for `.text`,
**file offset = VA** for the first (R) LOAD where the constants live.

---

## 1. Base experience multiplier

### 1.1 The formula

`Monster.GetExperience(double level, double expMult)` — **VA `0x2A7A6E0` / file `0x2A766E0`**

Disassembly (`0x2A7A6E0`–`0x2A7A77C`):

```
d8 = expMult                                  ; arg 2
d9 = level                                    ; arg 1
d0 = 1.33                                     ; [0x1387BA0]
d1 = expMult * 1.33
d0 = level
bl  Math.Pow(double,double)                   ; 0x40B0094  -> Pow(level, expMult*1.33)
d0 = d0 * 0.1                                 ; [0x1387838]
d0 = d0 + 22.0                                ; immediate
d0 = d0 * 0.88                                ; [0x13876A0]
d0 = d0 * 0.6                                 ; [0x13874D0]   <-- global base-XP rate
ret
```

So:

```
XP = ( pow(level, expMult * 1.33) * 0.1 + 22 ) * 0.88 * 0.6
```

The trailing `0.6` is the single global "base experience" knob. The three
constants before it shape the curve; `expMult` is a per-monster multiplier
passed in by the caller.

| constant | file offset | value |
|---|---|---|
| `pow` exponent factor | `0x1387BA0` | `1.33` |
| linear factor | `0x1387838` | `0.1` |
| curve factor | `0x13876A0` | `0.88` |
| **global XP rate** | **`0x13874D0`** | **`0.6`** |

### 1.2 Who calls it

| caller | VA | role |
|---|---|---|
| `Monster.CalculateAttributes(double level, bool forceElemental)` | `0x2A6D004` | computes the monster's XP value (stored into its attribute map) |
| `GameCalculator.CalculateIdleLevelsGained(...)` | `0x2BA9DE8` | offline/idle XP |
| `WindowLevels.<UpdateLevels>b__7` | `0x25F90E4` | UI display only |

In `Monster.CalculateAttributes` the `expMult` argument is loaded from a global
singleton: `[[0x558A890] + 0xB8] + 0x8`.

### 1.3 Grant path (server is authoritative for the *final* value)

The client accumulates base XP and ships it over the realtime socket:

```
NetSocket.UpdateCharacterRealtimeDataAsync_d_133.MoveNext   VA 0x2E15530
   -> InternalWebSocket.UpdateCharacterMetadataAsync(
          Double baseExperience, Double offense, Double defense, Double recovery,
          Int32 numMonsterKills, Int32 numItemsLooted, Int32 lastSeenOpals,
          DateTime timeSentUtc, Int32 lastSeenSilver)        VA 0x2D9BBB8
        -> server UpdateCharacterMetadataResponse {
               Double FinalExperienceGained, Double NewExperience, ... }
```

`baseExperience` is the `d8` register at the call site (`0x2E16B30`).
So the **client decides the base XP**, the **server decides the final XP**
(bonuses, rate). Changing the client only matters if the server trusts
`BaseExperienceGained`.

---

## 2. Base item-drop quantity

### 2.1 The function

`ItemGenerator.QueueRandomLoot(...)` — **VA `0x2C8FE9C` / file `0x2C8BE9C`**

```
void QueueRandomLoot(
    ItemGenerator* this,            // x0
    LootContext   lootContext,      // w1
    GameWorld*    world,            // x2
    int32_t       numItems,         // w3   <-- BASE ITEM QUANTITY
    double        magicFind,        // d0
    double        itemLevel,        // d1
    double        ownerLevel,       // d2
    Vector3D      position,         // s3,s4,s5 (+ s6)
    float         dropRadius,
    bool          clampToNavMesh,
    bool          guaranteeItem,
    double        magicFindFactorMultiplier,
    double        craftingReagentsMultiplier,
    Nullable<Guid> dropTableId,
    double        requiredLevelMultiplier,
    bool          guaranteeSetItem,
    bool          ignoreLootFilter)
```

`numItems` is argument 4 (`w3`). Confirmed at the call site `0x2C221CC`
(`w3 = w24`) and `0x2C223B4` (`w3 = 1`).

### 2.2 Who calls it

| caller | VA | role |
|---|---|---|
| `DeathPayload.<Apply>d__11.MoveNext` | `0x2C20A90` | **monster-kill drops (main path)** |
| `LootChest.OpenChest` | `0x298F034` | chest / container drops |
| `WindowFrostRunLevelComplete.<GenerateItems>d__31.MoveNext` | `0x2598B50` | frost-run rewards |
| `WindowGuildSiegeBattleCompleted*.<GenerateItems>…` | `0x25AB344`, `0x25B12A8` | guild-siege rewards |
| `WindowWelcomeBack.<GenerateItems>d__27.MoveNext` | `0x2676410` | welcome-back rewards |

### 2.3 How `numItems` is computed in `DeathPayload.Apply`

At `0x2C21700`–`0x2C2173C`:

```
d0 = d8 + d12
d0 = d0 + 1.0
d1 = d10 + 1.0
d0 = d0 * d9
d0 = d0 * d1
d8 = d11 + d0
d0 = floor(d8)
w24 = (int)d8                     ; overflow-checked
numItems = w24
```

i.e.

```
numItems = (int) floor( d11 + (d8 + d12 + 1) * d9 * (d10 + 1) )
```

`d9` and `d10` come from `GameAttributeMap` doubles via the static attribute
getters at `0x2C30D3C` and `0x2C30D8C`; `d8`/`d11`/`d12` are further quantity
bonuses accumulated earlier in the coroutine.

### 2.4 Related `GameAttributes` (stat-based bonuses, not the base)

| attribute | VA |
|---|---|
| `Item_Quantity_Bonus_Percent` | `0x2ACE6E0` |
| `Item_Quantity_Final_Multiplier_Hidden` | `0x2ACE898` |
| `Item_Quantity_Bonus_Percent_Total` | `0x2ACEA50` |
| `Area_Item_Drop_Quantity_Bonus_Percent` | `0x2ADE9B8` |
| `Experience` | `0x2ACE370` |
| `Base_Experience_Percent` | `0x2ACE3C8` |
| `Experience_Bonus_Percent` | `0x2ACE420` |

---

## 3. How to change them

### Option A — constant patch (XP only, 1 instruction's worth of data)

Patch the 8-byte little-endian double at **file offset `0x13874D0`**:

| target rate | value |
|---|---|
| 2× | `0.6 * 2 = 1.2` |
| 5× | `3.0` |
| 10× | `6.0` |

No stub needed. Affects every `Monster.GetExperience` caller (kills, idle XP,
and the level-window display, so the UI stays consistent).

### Option B — native hook stubs (both, runtime-tunable)

Fits the existing `device/stub/*.c` + `server/patch_android_*.py` infra.

* **XP**: hook `Monster.GetExperience` @ `0x2A7A6E0`. Trampoline calls the
  original, then multiplies `d0` by `XP_FACTOR` before `ret`.
* **Drops**: hook `ItemGenerator.QueueRandomLoot` @ `0x2C8FE9C`. Trampoline
  rewrites `w3 = (int)(w3 * DROP_FACTOR)` (keep `>= 1`), then tail-calls the
  original with the displaced prologue replayed.

This is the cleaner route because it can be driven by a config value and can be
limited (e.g. only `LootContext` for monster deaths, leaving chest/quest drops
untouched).

### Suggested driver flag

`server/build_combined.py` could grow `--xp-rate N` and `--drop-rate N` which
(a) patch `0x13874D0`, and/or (b) inject the two hook stubs, then re-run
`build_xapk_release.py`.

---

## 4. Caveats

* **Server authority.** Final XP is computed server-side from the
  client-supplied `BaseExperienceGained`; a client XP boost only lands if the
  server accepts it. Item drops are rolled client-side (`ItemGenerator`) and
  then persisted, so the drop hook is the more reliable of the two.
* **Anti-cheat.** The APK ships `ACTk.Runtime.dll` (CodeStage Anti-Cheat
  Toolkit — `Genuine/CodeHash`, `ObscuredFile`, memory detection). The existing
  `online`/`realtime`/`skill_rank` patches already coexist with it, but a
  dedicated XP/drop patch should be checked against the same integrity paths.
* **Offline/idle XP** also flows through `Monster.GetExperience`
  (`GameCalculator.CalculateIdleLevelsGained`), so an XP change is global, not
  per-monster.
