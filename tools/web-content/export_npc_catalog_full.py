#!/usr/bin/env python3
"""Export the B02 NPC/quest denominator.

NPCs: distinct PrimaryNpcId from Conversations.json, resolved to monster names.
Conversations: 66 (4 _UNUSED per deprecation rules). Quests: 47.

Output: tools/web-content/generated/npc_catalog_full.json
"""
from __future__ import annotations

import datetime
import json
import pathlib
from collections import Counter

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
OUT = ROOT / "tools" / "web-content" / "generated" / "npc_catalog_full.json"

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

    convs = load("Conversations")
    quests = load("Quests")

    npc_ids: dict = {}
    conv_items = []
    for x in convs:
        deprecated = "UNUSED" in (x.get("Name") or "")
        npc_ids.setdefault(x.get("PrimaryNpcId"), []).append(x.get("Name"))
        conv_items.append({
            "guid": x["Id"],
            "name": x.get("Name"),
            "npcName": monsters.get(x.get("PrimaryNpcId")),
            "status": "废弃" if deprecated else "正式",
        })

    npcs = [{"npcName": monsters.get(npc_id, npc_id[:8]),
             "conversations": names,
             "conversationCount": len(names)}
            for npc_id, names in sorted(npc_ids.items(),
                                        key=lambda kv: monsters.get(kv[0], ""))]

    quest_items = [{"guid": x["Id"], "name": x.get("Name"),
                    "category": x.get("QuestCategory"),
                    "presentationType": x.get("PresentationType"),
                    "status": "正式"}
                   for x in quests]

    out = {
        "baseline": BASELINE,
        "generatedAt": datetime.date.today().isoformat(),
        "counts": {
            "npcs": len(npcs),
            "conversations": len(conv_items),
            "conversationsDeprecated": sum(1 for c in conv_items if c["status"] == "废弃"),
            "quests": len(quest_items),
        },
        "npcs": npcs,
        "conversations": sorted(conv_items, key=lambda c: c["name"] or ""),
        "quests": sorted(quest_items, key=lambda q: q["name"] or ""),
    }
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1) + "\n")
    print(f"wrote {OUT} ({len(npcs)} npcs, {len(conv_items)} conversations, {len(quest_items)} quests)")


if __name__ == "__main__":
    main()
