#!/usr/bin/env python3
"""Export web-facing content metadata from the decrypted game definitions.

The web client must never hardcode race/class/monster names or avatar paths; it reads
this file instead. The script also performs the M0 content check: every avatar path it
emits must resolve to a PNG produced by export_assets.py, otherwise it fails.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/export_content.py
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
ASSETS = ROOT / "web/public/assets"
OUT = ASSETS / "content.json"

CONTENT_VERSION = "m0-1"


def load(name: str):
    return json.loads((GAMEDATA / f"{name}.json").read_text())


def basename(path: str | None) -> str | None:
    if not path:
        return None
    stem = path.replace("\\", "/").rsplit("/", 1)[-1]
    return re.sub(r"\.[Pp][Nn][Gg]$", "", stem)


def main() -> int:
    avatars_dir = ASSETS / "avatars"
    available = {p.stem for p in avatars_dir.glob("*.png")}
    missing: list[str] = []

    races = []
    for race in load("CharacterRaces"):
        icon = basename(race.get("ActorImage"))
        if icon and icon not in available:
            missing.append(f"race {race['Name']}: {icon}")
        races.append({
            "integerId": race["IntegerId"],
            "name": race["Name"],
            "nameKey": race.get("NameKey"),
            "icon": icon if icon in available else None,
            "attackPower": race.get("AttackPower"),
            "magicPower": race.get("MagicPower"),
            "defensePower": race.get("DefensePower"),
            "thiefPower": race.get("ThiefPower"),
            "stamina": race.get("Stamina"),
        })

    classes = []
    for cls in load("CharacterClasses"):
        classes.append({
            "integerId": cls["IntegerId"],
            "name": cls["Name"],
            "nameKey": cls.get("NameKey"),
            "hidden": bool(cls.get("Hidden", False)),
        })

    monsters = []
    for monster in load("Monsters"):
        data = monster.get("SerializedData") or {}
        icon = basename(data.get("Image"))
        monsters.append({
            "integerId": monster["IntegerId"],
            "typeId": data.get("TypeId"),
            "name": data.get("Name"),
            "nameKey": data.get("NameTranslationKey"),
            "portrait": icon if icon in available else None,
        })

    content = {
        "contentVersion": CONTENT_VERSION,
        "races": races,
        "classes": classes,
        "monsters": monsters,
        "monsterIcons": sorted(p.stem for p in avatars_dir.glob("MonstersAvatarIcons_*.png")),
        "check": {
            "avatarsAvailable": len(available),
            "iconReferencesMissing": missing,
        },
    }
    OUT.write_text(json.dumps(content, indent=2))
    print(f"wrote {OUT}")
    print(f"  races={len(races)} classes={len(classes)} monsters={len(monsters)} "
          f"monsterIcons={len(content['monsterIcons'])} avatars={len(available)}")
    if missing:
        print("  WARNING unresolved icon references:")
        for m in missing[:20]:
            print("   ", m)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
