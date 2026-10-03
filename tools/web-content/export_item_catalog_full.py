#!/usr/bin/env python3
"""Export the B02 full item denominator (all 608 Items.json entries).

Per docs/web/B02_DEPRECATION_RULES.md, each entry is annotated with:
  * identity: guid, integerId, name, typeName
  * source signals: hasDropPool, typeName
  * status: 正式 / 废弃 / 待核实  (no _UNUSED names exist in Items.json; all start as 正式)
  * availability: drop | non-drop (currency/material/essence — exact source per item is 待核实)
  * in_current_catalog: whether the name is in server GameData/item_catalog.json (the curated 458)

Sources:
  * gamedata_decrypted/Items.json
  * gamedata_decrypted/ItemTypes.json
  * server/Nordicandia.Server/GameData/item_catalog.json

Output: tools/web-content/generated/item_catalog_full.json

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/export_item_catalog_full.py
"""
from __future__ import annotations

import datetime
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
CATALOG = ROOT / "server" / "Nordicandia.Server" / "GameData" / "item_catalog.json"
OUT = ROOT / "tools" / "web-content" / "generated" / "item_catalog_full.json"

BASELINE = "android-1.9.3 (versionCode 507033)"


def load(name: str):
    return json.loads((GAMEDATA / f"{name}.json").read_text())


def inner(entry: dict) -> dict:
    sd = entry.get("SerializedData")
    if isinstance(sd, dict):
        return sd
    if sd:
        return json.loads(sd)
    return {k: v for k, v in entry.items() if k != "SerializedData"}


def main() -> None:
    types = {t["Id"]: inner(t).get("Name") for t in load("ItemTypes")}
    curated = set(json.loads(CATALOG.read_text()).keys())

    items = []
    for entry in load("Items"):
        data = inner(entry)
        name = data.get("Name")
        type_guid = data.get("TypeId")
        drop_pool = data.get("DropPool")
        items.append({
            "guid": entry["Id"],
            "integerId": entry.get("IntegerId"),
            "name": name,
            "typeGuid": type_guid,
            "typeName": types.get(type_guid),
            "hasDropPool": bool(drop_pool),
            "status": "正式",
            "availability": "drop" if drop_pool else "non-drop（货币/材料/精华类，具体来源待核实）",
            "inCurrentCatalog": name in curated,
        })

    items.sort(key=lambda x: (x["typeName"] or "", x["name"] or ""))
    out = {
        "baseline": BASELINE,
        "generatedAt": datetime.date.today().isoformat(),
        "counts": {
            "total": len(items),
            "withDropPool": sum(1 for i in items if i["hasDropPool"]),
            "withoutDropPool": sum(1 for i in items if not i["hasDropPool"]),
            "inCurrentCatalog": sum(1 for i in items if i["inCurrentCatalog"]),
            "notInCurrentCatalog": sum(1 for i in items if not i["inCurrentCatalog"]),
        },
        "items": items,
    }
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT} ({len(items)} items)")


if __name__ == "__main__":
    main()
