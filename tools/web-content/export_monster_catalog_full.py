#!/usr/bin/env python3
"""Export the B02 full monster denominator (136 Monsters.json + 30 MonsterTypes).

Per docs/web/B02_DEPRECATION_RULES.md each entry is annotated with:
  identity (guid/integerId/name), type, available rarities, brain (+dangling flag),
  status, and availability notes.

Boss/summon-only monsters (no AvailableRarities, e.g. Golems, minions) are 正式
content with availability=boss/summon, not deprecated.

Output: tools/web-content/generated/monster_catalog_full.json
"""
from __future__ import annotations

import datetime
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
OUT = ROOT / "tools" / "web-content" / "generated" / "monster_catalog_full.json"

BASELINE = "android-1.9.3 (versionCode 507033)"
RARITY_LABELS = {0: "Normal", 1: "Magic", 2: "Rare", 4: "Champion", 6: "Boss"}
# 稀有度语义由数据反推：[0,1,2]=普通野怪三档；[4]=精英变体（SpiderQueen/Goblin2 等）；
# [6]=Boss（Boss_WolfKing）。3/5 在数据中未出现。


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
    types = {t["Id"]: inner(t).get("Name") for t in load("MonsterTypes")}
    brains = {b["Id"]: (inner(b).get("Name") or b["Id"][:8]) for b in load("Brains")}

    items = []
    for entry in load("Monsters"):
        data = inner(entry)
        rarities = data.get("AvailableRarities")
        brain_id = data.get("BrainId")
        brain_name = brains.get(brain_id) if brain_id else None
        type_name = types.get(data.get("TypeId"))
        if rarities:
            availability = "野外/地牢生成（稀有度 %s）" % ",".join(
                RARITY_LABELS.get(r, str(r)) for r in rarities)
        else:
            availability = "非野外生成（Boss/召唤物/触发器，待逐条核实）"
        items.append({
            "guid": entry["Id"],
            "integerId": entry.get("IntegerId"),
            "name": data.get("Name"),
            "typeName": type_name,
            "availableRarities": rarities,
            "brainId": brain_id,
            "brainName": brain_name,
            "brainDangling": brain_id is not None and brain_id not in brains,
            "brainMissing": brain_id is None,
            "status": "正式",
            "availability": availability,
        })

    items.sort(key=lambda x: ((x["typeName"] or ""), x["name"] or ""))
    type_counts: dict = {}
    for i in items:
        type_counts[i["typeName"] or "未知"] = type_counts.get(i["typeName"] or "未知", 0) + 1
    out = {
        "baseline": BASELINE,
        "generatedAt": datetime.date.today().isoformat(),
        "counts": {
            "total": len(items),
            "byType": type_counts,
            "withRarities": sum(1 for i in items if i["availableRarities"]),
            "bossOrSummonOnly": sum(1 for i in items if not i["availableRarities"]),
            "brainDangling": sum(1 for i in items if i["brainDangling"]),
            "brainMissing": sum(1 for i in items if i["brainMissing"]),
        },
        "items": items,
    }
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT} ({len(items)} monsters)")


if __name__ == "__main__":
    main()
