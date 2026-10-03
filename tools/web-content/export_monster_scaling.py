#!/usr/bin/env python3
"""Export D04: monster base-stat curves + the monster roster.

The client (Game.Monster.Get*, Android 1.9.3) derives every combat stat from a
level curve; there are no per-monster base numbers. The formulas and their constant
pool values were read straight from tmp/apk-libil2cpp.so (see
docs/web/D04_MONSTER_SCALING.md). This script encodes them so the numbers can be
regenerated and inspected without the server.

Output: tools/web-content/generated/monster_scaling.json
"""
from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
OUT = ROOT / "tools" / "web-content" / "generated" / "monster_scaling.json"
BASELINE = "android-1.9.3 (versionCode 507033)"

# --- recovered constants (module constant pool) ---------------------------------
FINAL_STATS_MULT = 0.8        # Monster.get_FinalStatsMult 0x02A770E0
RESISTANCE = 0.05             # Monster.GetResistance 0x02A7AAD4
MAX_PHYSICAL_DR = 0.9         # Monster.GetMaxPhysicalDamageReduction 0x02A7AAE0
VAR_LO, VAR_HI = 0.99, 1.01   # pool 0x13875D8 / 0x1387390

# exponent (e), coefficient (b), linear (a), add (c), final factor (k)
FORMULAS = {
    "life":          dict(addr="0x02A7A780", e=1.315, b=0.12, a=0.41, c=8,  k=2.0,  variance=True,  final=True),
    "armor":         dict(addr="0x02A7A884", e=1.40,  b=0.01, a=150,  c=80, k=1.0,  variance=False, final=True),
    "evasion":       dict(addr="0x02A7A930", e=1.28,  b=0.006, a=8,   c=4,  k=0.25, variance=False, final=True),
    "minAttackRating": dict(addr="0x02A7A584", e=1.20, b=0.005, a=1.2, c=100, k=0.5, variance=False, final=True),
    "maxAttackRating": dict(addr="0x02A7A630", e=1.20, b=0.005, a=1.2, c=100, k=0.8, variance=False, final=True),
    "weaponDamage":  dict(addr="0x02A7A424/0x02A7A4D4", e=1.24, b=0.01, a=0.35, c=0, k=2.0, variance=False, final=True),
    "experience":    dict(addr="0x02A7A6E0", e=1.33, b=0.1,  a=0,    c=16, k=0.88 * 0.6, variance=False, final=False),
    "forceField":    dict(addr="0x02A7A9DC", e=1.30, b=0.2,  a=0.4,  c=50, k=2.0,  variance=True,  final=True),
}


def value(name: str, level: float, exp_mult: float = 1.0, final_mult: float = FINAL_STATS_MULT) -> float:
    f = FORMULAS[name]
    p = level ** (f["e"] * exp_mult)
    base = (f["a"] * level + f["b"] * p + f["c"]) * f["k"]
    if f["final"]:
        base *= final_mult
    return base


def inner(entry: dict) -> dict:
    sd = entry.get("SerializedData")
    if isinstance(sd, str):
        return json.loads(sd)
    if isinstance(sd, dict):
        return sd
    return {k: v for k, v in entry.items() if k != "SerializedData"}


RARITY_LABELS = {0: "Normal", 1: "Magic", 2: "Rare", 4: "Champion", 6: "Boss"}


def main() -> None:
    monsters = []
    for entry in json.loads((GAMEDATA / "Monsters.json").read_text()):
        data = inner(entry)
        monsters.append({
            "integerId": entry.get("IntegerId"),
            "name": data.get("Name"),
            "availableRarities": data.get("AvailableRarities"),
            "rarityLabels": [RARITY_LABELS.get(r, str(r)) for r in data.get("AvailableRarities") or []],
            "size": data.get("Size"),
            "damageType": data.get("DamageType"),
        })

    table = {}
    for level in range(1, 61):
        table[str(level)] = {name: round(value(name, level), 6) for name in FORMULAS}

    OUT.write_text(json.dumps({
        "baseline": BASELINE,
        "source": "Game.Monster.Get* (tmp/cpp2il-isil2/.../Monster.txt); constants from tmp/apk-libil2cpp.so",
        "constants": {
            "FinalStatsMult": FINAL_STATS_MULT,
            "Resistance": RESISTANCE,
            "MaxPhysicalDamageReduction": MAX_PHYSICAL_DR,
            "VarianceRange": [VAR_LO, VAR_HI],
        },
        "formulaShape": "((a*level + b*level^(e*expMult) + c) * k) * finalMult",
        "formulas": {n: {k: v for k, v in f.items()} for n, f in FORMULAS.items()},
        "expMult": {"default": 1.0, "status": "Provisional",
                    "note": "world/global difficulty multiplier; config field not statically resolved"},
        "rarityFinalMult": {
            "status": "ClientVerified",
            "note": "CalculateAttributes 0x02A6D0FC-0x02A6D128 selects finalMult by rarity",
            "normalMagicRare": 0.8,   # table[0] @0x1385BA0 = get_FinalStatsMult()
            "champion": 0.88,         # table[1] @0x1385BA8
            "boss": 1.2,              # @0x13880D8
        },
        "attackIntervalCoefficients": {
            "status": "Interim (decoded, not wired)",
            "note": "CalculateAttributes sets v531/v532 min/max multipliers per Rarity feeding "
                    "Item_Attack_Speed_MainHand (interval seconds); rarity->value read from the pool",
            "byRarity": {
                "0": {"v531": 1.4},
                "2": {"v531": 1.6, "v532": 3.4},
                "4": {"v531": 1.8},
                "6": {"v531": 1.2},
            },
            "tierScaling": "min=max(v531*(1+(tier-1)*0.005),1); max=min(v532*(1+(tier-1)*0.04),16)",
        },
        "monsters": monsters,
        "table": table,
    }, ensure_ascii=False, indent=1) + "\n")

    print(f"wrote {OUT.relative_to(ROOT)} ({len(monsters)} monsters, {len(table)} levels)")
    for level in (1, 10, 30, 60):
        row = table[str(level)]
        print(f"  L{level:>2}: " + " ".join(f"{k}={v:.3f}" for k, v in row.items()))


if __name__ == "__main__":
    main()
