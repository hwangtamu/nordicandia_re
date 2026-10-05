#!/usr/bin/env python3
"""Recover companion and combat-pet unlock rows from Android 1.9.3 game data.

PetRow.Present reads OpalCost (attribute 421) / SilverCost (968) from the pet
MonsterDefinition's affixes and evaluates their ItemAttributeSpecifierDefinition
at level 1, rarity 1, multiplier 1. No price formula is inferred from pet names.
"""
from __future__ import annotations

import json
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
PET_TYPE = "9bfa45a4-9be6-46d4-b502-53b5c4545421"
COMBAT_PET_TYPE = "29aade2b-4788-426a-a618-687489e24b79"
CURRENCY_ATTRIBUTES = {421: "OP", 968: "SL"}


def fixed_value(specifier: dict) -> float | None:
    for row in specifier.get("ValueRangeByRarityList", []):
        if row.get("Rarity") == 1:
            value_range = row.get("ValueRange") or {}
            if "MinValue" in value_range and value_range["MinValue"] == value_range.get("MaxValue"):
                return float(value_range["MinValue"])
    default = specifier.get("DefaultValueRange") or {}
    if "MinValue" in default and default["MinValue"] == default.get("MaxValue"):
        return float(default["MinValue"])
    return None


def main() -> None:
    monsters = json.loads((ROOT / "gamedata_decrypted/Monsters.json").read_text())
    affixes = json.loads((ROOT / "gamedata_decrypted/Affixes.json").read_text())
    affix_by_id = {row["Id"]: row for row in affixes}
    entries = []
    for monster in monsters:
        definition = monster.get("SerializedData") or {}
        type_id = definition.get("TypeId")
        kind = "Pet" if type_id == PET_TYPE else "CombatPet" if type_id == COMBAT_PET_TYPE else None
        if kind is None:
            continue
        unlock_currency = None
        unlock_cost = None
        effects = []
        for affix_id in definition.get("AffixIds", []):
            affix = affix_by_id.get(affix_id)
            if affix is None:
                continue
            for specifier in affix.get("AttributeSpecifierDefinitionList", []):
                attribute_id = specifier.get("AttributeId")
                value = fixed_value(specifier)
                if value is None:
                    continue
                affix_name = affix.get("Name", "")
                effects.append({"affix": affix_name, "attributeId": attribute_id, "value": value})
                currency = CURRENCY_ATTRIBUTES.get(attribute_id)
                if currency is not None:
                    if unlock_currency is not None and unlock_currency != currency:
                        raise ValueError(f"conflicting pet purchase currencies: {definition.get('Name')}")
                    unlock_currency = currency
                    unlock_cost = int(value)
        entries.append({
            "integerId": monster["IntegerId"],
            "name": definition["Name"],
            "kind": kind,
            "image": definition.get("Image", ""),
            "unlockCurrency": unlock_currency,
            "unlockCost": unlock_cost,
            "effects": effects,
        })
    entries.sort(key=lambda item: (item["kind"], item["integerId"]))
    output = {"source": "gamedata_decrypted/Monsters.json + Affixes.json; PetRow.Present attr ids 421/968",
              "priceLevel": 1, "priceRarity": 1, "items": entries}
    for path in (ROOT / "tools/web-content/generated/pet_catalog.json",
                 ROOT / "server/Nordicandia.Server/GameData/pet_catalog.json"):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(output, indent=2, ensure_ascii=False) + "\n")
    print(f"wrote pet_catalog.json: {sum(x['kind'] == 'Pet' for x in entries)} pets, "
          f"{sum(x['kind'] == 'CombatPet' for x in entries)} combat pets")


if __name__ == "__main__":
    main()
