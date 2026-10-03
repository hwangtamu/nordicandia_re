#!/usr/bin/env python3
"""Export the E02 loot-table item-type spawn weights (Droprates.json).

LootTables[<name>].ItemTypeWeights maps an item-type guid -> spawn weight; the weight is
multiplied by ItemTypeCharacterClassWeightMultipliers[typeGuid][classGuid] for the player's
class. This resolves the guids to type names and the class IntegerIds so the server can pick
the item type by weight instead of uniformly among the slot's candidates.

Output: server/Nordicandia.Server/GameData/loot_table_item_types.json
"""
from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
OUT = ROOT / "server" / "Nordicandia.Server" / "GameData" / "loot_table_item_types.json"


def inner(entry: dict) -> dict:
    sd = entry.get("SerializedData")
    if isinstance(sd, str):
        return json.loads(sd)
    return sd or {}


def main() -> int:
    item_types = {t["Id"]: inner(t).get("Name")
                  for t in json.loads((GAMEDATA / "ItemTypes.json").read_text())}
    classes = {c["Id"]: c.get("IntegerId") for c in json.loads((GAMEDATA / "CharacterClasses.json").read_text())}
    droprates = json.loads((GAMEDATA / "Droprates.json").read_text())

    tables: dict[str, dict[str, int]] = {}
    for table in droprates["LootTables"]:
        data = inner(table)
        weights = data.get("ItemTypeWeights") or {}
        resolved = {}
        for guid, weight in weights.items():
            name = item_types.get(guid)
            if name is None:
                raise SystemExit(f"unresolved item type {guid} in {data.get('Name')}")
            resolved[name] = weight
        if resolved:
            tables[data.get("Name")] = resolved

    class_multipliers: dict[str, dict[str, float]] = {}
    for type_guid, per_class in droprates["ItemTypeCharacterClassWeightMultipliers"].items():
        name = item_types.get(type_guid)
        if name is None:
            raise SystemExit(f"unresolved class-multiplier type {type_guid}")
        class_multipliers[name] = {}
        for class_guid, multiplier in per_class.items():
            class_id = classes.get(class_guid)
            if class_id is None:
                raise SystemExit(f"unresolved class {class_guid}")
            class_multipliers[name][str(class_id)] = multiplier

    # The loot table often lists a parent weapon type (Axe2H -> TwoHandMeleeWeapon), so export the
    # ItemTypes parent chain for the server to walk when resolving a definition's type weight.
    parents = {}
    for t in json.loads((GAMEDATA / "ItemTypes.json").read_text()):
        data = inner(t)
        name = data.get("Name")
        parent = item_types.get(data.get("ParentTypeId"))
        if name and parent:
            parents[name] = parent

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps({"tables": tables, "classMultipliers": class_multipliers,
                               "parents": parents},
                              ensure_ascii=False, indent=1, sort_keys=True) + "\n")
    print(f"wrote {OUT.relative_to(ROOT)}: {len(tables)} tables, "
          f"{sum(len(t) for t in tables.values())} type weights, "
          f"{len(class_multipliers)} class-multiplier types")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
