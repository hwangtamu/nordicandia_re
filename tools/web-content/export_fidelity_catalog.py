#!/usr/bin/env python3
"""Export the B02 content denominator for player skills, passives and masteries.

Sources:
  * tools/web-content/generated/power_pools.json   (per-class selectable powers)
  * tools/web-content/generated/powers_full.json   (definitions + masteries)
  * server/Nordicandia.Server/WebApi/PowerProfiles.generated.cs (web behaviour archetype + confidence)

Output: tools/web-content/generated/fidelity_catalog.json

This is the acceptance denominator for C04/C06: every selectable power and its masteries, with
the current web behaviour archetype (the provisional execution category, not a real port).
"""
from __future__ import annotations

import json
import pathlib
import re

ROOT = pathlib.Path(__file__).resolve().parents[2]
POOLS = ROOT / "tools" / "web-content" / "generated" / "power_pools.json"
FULL = ROOT / "tools" / "web-content" / "generated" / "powers_full.json"
WEB = ROOT / "server" / "Nordicandia.Server" / "WebApi" / "PowerProfiles.generated.cs"
OUT = ROOT / "tools" / "web-content" / "generated" / "fidelity_catalog.json"

# new(0, "Slam", "Slam the ground...", "Slam", "nova", 5.5D, ...
ACTIVE = re.compile(r'new\((\d+),\s*"([^"]+)",\s*"((?:[^"\\]|\\.)*)",\s*"([^"]*)",\s*"([^"]*)",')
# new("Overkill", "Your melee...", "Overkill", "might", 1.0D, ...
PASSIVE = re.compile(r'new\("([^"]+)",\s*"((?:[^"\\]|\\.)*)",\s*"([^"]*)",\s*"([^"]*)",')


def web_archetypes() -> dict[str, str]:
    text = WEB.read_text()
    result: dict[str, str] = {}
    for m in ACTIVE.finditer(text):
        result[m.group(2)] = m.group(5)
    for m in PASSIVE.finditer(text):
        result.setdefault(m.group(1), m.group(4))
    return result


def main() -> None:
    pools = json.load(open(POOLS))
    full = json.load(open(FULL))
    masteries_by_power = full.get("masteriesByPower", {})
    archetypes = web_archetypes()

    classes = {}
    active_total = passive_total = mastery_total = 0
    for class_name, pool in pools.items():
        active = []
        for p in pool.get("active", []):
            masteries = masteries_by_power.get(p["id"], [])
            active.append({
                "integerId": p["integerId"],
                "name": p["name"],
                "source": "Powers.json",
                "tags": p.get("tags", []),
                "type": p.get("type", ""),
                "implementedBy": p.get("implementedBy", ""),
                "parameterFields": p.get("parameterFields", []),
                "webEffect": archetypes.get(p["name"], "missing"),
                "masteryCount": len(masteries),
            })
            active_total += 1
            mastery_total += len(masteries)
        passive = []
        for p in pool.get("passive", []):
            passive.append({
                "integerId": p["integerId"],
                "name": p["name"],
                "source": "Powers.json",
                "tags": p.get("tags", []),
                "type": p.get("type", ""),
                "implementedBy": p.get("implementedBy", ""),
                "parameterFields": p.get("parameterFields", []),
                "webEffect": archetypes.get(p["name"], "missing"),
            })
            passive_total += 1
        classes[class_name] = {"active": active, "passive": passive}

    # Mastery denominator keyed by power name.
    power_name_by_id = {p["id"]: p["name"] for p in full["powers"].values()}
    masteries = {}
    for power_id, rows in masteries_by_power.items():
        name = power_name_by_id.get(power_id)
        if name is None:
            continue
        masteries[name] = [{
            "integerId": r.get("integerId"),
            "name": r.get("name"),
            "maxPoints": r.get("maxPoints"),
            "category": r.get("category"),
        } for r in rows]

    # Powers that reach the web but have no real execution (all behaviour archetypes).
    archetype_set = sorted({a["webEffect"] for cls in classes.values()
                            for a in cls["active"] + cls["passive"]})

    result = {
        "schemaVersion": 1,
        "baseline": {"platform": "android", "version": "1.9.3", "versionCode": 507033},
        "denominator": {
            "classes": len(classes),
            "activeSkills": active_total,
            "passiveSkills": passive_total,
            "playerSkills": active_total + passive_total,
            "masteries": mastery_total,
            "webExecutedArchetypes": archetype_set,
        },
        "classes": classes,
        "masteries": masteries,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    json.dump(result, open(OUT, "w"), indent=1, sort_keys=True)
    d = result["denominator"]
    print(f"classes={d['classes']} active={d['activeSkills']} passive={d['passiveSkills']} "
          f"skills={d['playerSkills']} masteries={d['masteries']}")
    print("web archetypes:", ", ".join(archetype_set))
    print("->", OUT)


if __name__ == "__main__":
    main()
