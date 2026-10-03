#!/usr/bin/env python3
"""Extract each playable class's active/passive power pools for the web loadout.

`CharacterClasses.json` lists the per-class power Guids; `powers_full.json` (from
`export_powers.py`) carries the name/description/icon/tags/implementation for every power.
This joins them so the server can offer the full selectable pool instead of only the
3-active + 1-passive starter kit.

Outputs:
  tools/web-content/generated/power_pools.json
  server/Nordicandia.Server/GameData/power_pools.json

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/export_power_pools.py
"""
from __future__ import annotations

import json
import pathlib

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
FULL = ROOT / "tools/web-content/generated/powers_full.json"
OUTS = [
    ROOT / "tools/web-content/generated/power_pools.json",
    ROOT / "server/Nordicandia.Server/GameData/power_pools.json",
]

PLAYABLE = {"Warrior", "Hunter", "Mage", "Necromancer"}


def entry(power: dict) -> dict:
    return {
        "id": power["id"],
        "integerId": power["integerId"],
        "name": power["name"],
        "description": power.get("description", ""),
        "icon": power.get("icon", ""),
        "tags": power.get("tags", []),
        "type": power.get("type", ""),
        "implementedBy": power.get("implementedBy"),
        "parameterFields": power.get("parameterFields", []),
    }


def main() -> int:
    powers = json.loads(FULL.read_text())["powers"]
    by_id = {p["id"]: p for p in powers.values()}
    pools = json.loads((GAMEDATA / "CharacterClasses.json").read_text())
    result: dict[str, dict] = {}
    for klass in pools:
        name = klass.get("Name")
        if name not in PLAYABLE:
            continue
        result[name] = {
            "active": [entry(by_id[g]) for g in klass.get("ActiveSkills", []) if g in by_id],
            "passive": [entry(by_id[g]) for g in klass.get("PassiveSkills", []) if g in by_id],
        }
    text = json.dumps(result, indent=2, ensure_ascii=False) + "\n"
    for out in OUTS:
        out.write_text(text)
        print(f"wrote {out}")
    for name, pool in result.items():
        print(f"  {name}: {len(pool['active'])} active, {len(pool['passive'])} passive")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
