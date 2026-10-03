#!/usr/bin/env python3
"""Export Brains.json with condition hashes resolved.

`Game.AI.MonsterBrainCondition` builds its `BrainCondition` set in code; brains reference
them by `StringHashHelper.HashNameSafe(name)` (Common.Helpers.StringHashHelper @ 0x02D760A0).
That hash is reimplemented here so `Brains.json`'s integer `BrainConditions` become names.

Output: tools/web-content/generated/brains_catalog.json
  { "<brain name>": { "id": int, "actions": [ { "power": str, "weight": int, "conditions": [str] } ] } }
"""
from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
BRAINS = ROOT / "gamedata_decrypted" / "Brains.json"
POWERS = ROOT / "gamedata_decrypted" / "Powers.json"
OUT = ROOT / "tools" / "web-content" / "generated" / "brains_catalog.json"

# Game.AI.MonsterBrainCondition backing fields (steam_analysis/dump.cs).
CONDITION_NAMES = [
    "TargetNear", "NoRepeat", "TargetIsEnemy", "TargetFar", "TargetNotTooFar", "TargetTooFar",
    "TargetNotTooFarFromMaster", "TargetTooFarFromMaster", "MasterIsNotBeingAttacked",
    "MasterIsBeingAttacked", "ImExpired", "IHaveNoMinions", "IHaveMinions", "StateIdle",
    "StateWander", "StateCombat", "StateRunOutOfCombat", "FrostRunGameModeCompleted",
    "WorldLootExists", "PetHasFreeInventorySpace", "NotIntimidated", "NotOnFullLife",
    "MoreThan50PercentLife", "LessThan50PercentLife", "ImAboveOrEqualToTier4",
    "ImNotNormalMonster", "AliveIdleNonBossMonstersExist", "UniqueHuginnHasPulledMonsters",
    "HasReturnedToMaster", "OdrTotemIsAlive", "OdrTotemSpawned", "CanSummonOdrTotem",
    "OdrTotemDead", "CanEnrage", "OdrTotemHasNoCharges", "HasTotemChargeFire",
    "HasTotemChargeCold", "HasTotemChargeLightning", "HasUnholyFocusBuff",
    "TargetCanBeInstaKilledByDeath",
]


def hash_name_safe(name: str) -> int:
    s = name.lower()
    n = len(s)
    if n < 1:
        return 0
    w21 = w22 = 0x15051505
    i = 0
    while True:
        w22 = ((w22 * 33) ^ (ord(s[i]) & 0xFFFF)) & 0xFFFFFFFF
        if i == n - 1:
            break
        w21 = ((w21 * 33) ^ (ord(s[i + 1]) & 0xFFFF)) & 0xFFFFFFFF
        i += 2
        if i >= n:
            break
    return (w21 * 0x5D588B65 + w22) & 0xFFFFFFFF


def signed(v: int) -> int:
    return v - 0x100000000 if v >= 0x80000000 else v


def items(path: pathlib.Path):
    data = json.load(open(path))
    return data if isinstance(data, list) else list(data.values())[0]


def main() -> None:
    condition = {signed(hash_name_safe(n)): n for n in CONDITION_NAMES}
    power_name = {}
    for p in items(POWERS):
        power_name[p.get("Id")] = p.get("SerializedData", {}).get("Name")

    result = {}
    for brain in items(BRAINS):
        name = brain.get("Name")
        actions = []
        for action in brain.get("BrainActions", []):
            conditions = [condition.get(c, f"0x{c & 0xFFFFFFFF:08x}") for c in action.get("BrainConditions", [])]
            actions.append({
                "power": power_name.get(action.get("PowerId"), ""),
                "weight": action.get("Weight", 0),
                "conditions": conditions,
            })
        result[name] = {"id": brain.get("IntegerId", 0), "actions": actions}

    OUT.parent.mkdir(parents=True, exist_ok=True)
    json.dump(result, open(OUT, "w"), indent=1, sort_keys=True)
    print(f"brains: {len(result)} -> {OUT}")


if __name__ == "__main__":
    main()
