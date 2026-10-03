#!/usr/bin/env python3
"""Export the E03 item-set catalog (ItemSets.json -> set_catalog.json).

Each set lists breakpoints by equipped-piece count (NumItems); each breakpoint references
set affixes (ItemAffixes.json GenerationType=3) whose DefaultValueRange is the fixed bonus
value. Attribute names resolve through the full attribute id table.

Outputs:
  tools/web-content/generated/set_catalog.json        (reference)
  server/Nordicandia.Server/GameData/set_catalog.json (embedded)
"""
from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
GENERATED = ROOT / "tools" / "web-content" / "generated"
OUT = ROOT / "server" / "Nordicandia.Server" / "GameData" / "set_catalog.json"


def inner(entry: dict) -> dict:
    sd = entry.get("SerializedData")
    if isinstance(sd, str):
        return json.loads(sd)
    return sd or {k: v for k, v in entry.items() if k != "SerializedData"}


def main() -> int:
    sets = json.loads((GAMEDATA / "ItemSets.json").read_text())
    # Set affixes live in the full Affixes.json (not the item-affix subset).
    affixes = {a["Id"]: a for a in json.loads((GAMEDATA / "Affixes.json").read_text())}
    affixes.update({a["Id"]: a for a in json.loads((GAMEDATA / "ItemAffixes.json").read_text())})
    attr_ids = json.loads((GENERATED / "attribute_ids.json").read_text())

    catalog: dict[str, dict] = {}
    unresolved_affix: set = set()
    unresolved_attr: set = set()
    for entry in sets:
        data = inner(entry)
        # ItemSets entries double-nest SerializedData (list of breakpoints + metadata).
        breakpoints_raw = data.get("SerializedData") or data.get("Breakpoints") or []
        breakpoints = []
        for bp in breakpoints_raw:
            attributes = []
            for affix_id in bp.get("AffixIds") or []:
                affix = affixes.get(affix_id)
                if affix is None:
                    unresolved_affix.add(affix_id)
                    continue
                for spec in affix.get("AttributeSpecifierDefinitionList") or []:
                    aid = spec.get("AttributeId")
                    default = spec.get("DefaultValueRange") or {}
                    value = default.get("MinValue")
                    if value is None:
                        ranges = [r.get("ValueRange") or {} for r in spec.get("ValueRangeByRarityList") or []]
                        value = max((r.get("MaxValue", 0.0) for r in ranges), default=0.0)
                    name = attr_ids.get(str(aid))
                    if name is None:
                        unresolved_attr.add(aid)
                    attributes.append({"attributeId": aid, "attributeName": name, "value": value})
            breakpoints.append({"numItems": bp.get("NumItems"), "attributes": attributes})
        set_id = str(entry.get("IntegerId"))
        catalog[set_id] = {"name": data.get("Name"), "breakpoints": breakpoints}

    for path in (GENERATED / "set_catalog.json", OUT):
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(catalog, ensure_ascii=False, indent=1, sort_keys=True) + "\n")

    total_bonuses = sum(len(bp["attributes"]) for s in catalog.values() for bp in s["breakpoints"])
    print(f"wrote {len(catalog)} sets, {total_bonuses} breakpoint bonuses")
    print(f"  unresolved set affixes: {sorted(unresolved_affix)}")
    print(f"  unresolved attribute ids: {sorted(unresolved_attr)}")
    return 1 if (unresolved_affix or unresolved_attr) else 0


if __name__ == "__main__":
    raise SystemExit(main())
