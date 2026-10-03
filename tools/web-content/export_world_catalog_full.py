#!/usr/bin/env python3
"""Export the B02 world denominator (36 Worlds.json entries).

Each world: tier, theme, boss (resolved to monster name), monster-type spawn
weights (resolved to type names).

Output: tools/web-content/generated/world_catalog_full.json
"""
from __future__ import annotations

import datetime
import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
OUT = ROOT / "tools" / "web-content" / "generated" / "world_catalog_full.json"

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
    monsters = {}
    for x in load("Monsters"):
        monsters[x["Id"]] = inner(x).get("Name")
    types = {}
    for t in load("MonsterTypes"):
        types[t["Id"]] = inner(t).get("Name")

    items = []
    for x in load("Worlds"):
        tier = x.get("Tier")
        boss_id = x.get("BossMonsterId")
        weights = x.get("MonsterTypeSpawnWeights") or {}
        items.append({
            "guid": x["Id"],
            "integerId": x.get("IntegerId"),
            "name": x.get("Name"),
            "tier": tier,
            "themeId": x.get("ThemeId"),
            "bossName": monsters.get(boss_id) if boss_id else None,
            "bossDangling": bool(boss_id and boss_id not in monsters),
            "spawnWeights": {types.get(g, g[:8]): w for g, w in weights.items()},
            "status": "正式",
            "availability": "城镇" if tier is None else f"世界阶层 {tier}",
        })

    items.sort(key=lambda i: (i["tier"] is None, i["tier"] or 0))
    out = {
        "baseline": BASELINE,
        "generatedAt": datetime.date.today().isoformat(),
        "counts": {
            "total": len(items),
            "tiered": sum(1 for i in items if i["tier"] is not None),
            "town": sum(1 for i in items if i["tier"] is None),
            "bossDangling": sum(1 for i in items if i["bossDangling"]),
        },
        "items": items,
    }
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT} ({len(items)} worlds)")


if __name__ == "__main__":
    main()
