#!/usr/bin/env python3
"""Extract Nordicandia's real per-class powers (active + passive) from the decrypted
definitions so the web client and server use the shipped skill names, descriptions,
icons and tags instead of placeholders.

Sources (gamedata_decrypted/):
  Powers.json           159 powers: Name, Description, Image, TypeId, TagIds
  PowerTypes.json       26 types: which powers are Active/Passive and their class
  PowerTags.json        19 damage/school tags
  CharacterClasses.json per-class ActiveSkills / PassiveSkills GUID lists

Outputs:
  web/public/assets/powers.json                          client display content
  server/Nordicandia.Server/WebApi/Powers.generated.cs   server behaviour table

The client-visible metadata (name/description/icon/tags/type) is ClientVerified. The
per-skill *behaviour parameters* (multiplier, cooldown, radius, buff/heal amounts) are
Provisional: the client's power execution code has not been recovered, so each skill is
mapped to the closest effect archetype with placeholder numbers.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/export_powers.py
"""
from __future__ import annotations

import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
WEB_OUT = ROOT / "web/public/assets/powers.json"
CS_OUT = ROOT / "server/Nordicandia.Server/WebApi/Powers.generated.cs"
CONTENT_VERSION = "m0-1"

ACTIVE_PER_CLASS = 3


def load(name: str):
    return json.loads((GAMEDATA / f"{name}.json").read_text())


def basename(path: str | None) -> str | None:
    if not path:
        return None
    return re.sub(r"\.[Pp][Nn][Gg]$", "", path.replace("\\", "/").rsplit("/", 1)[-1])


def derive_effect(name: str, description: str, tags: list[str]) -> dict:
    """Map a real skill to a provisional effect archetype + parameters."""
    text = f"{name} {description}".lower()
    tag_set = {t.lower() for t in tags}
    aoe_words = ("all enemies", "nearby", "area", "around you", "rain", "nova", "whirlwind", "shards")
    buff_words = ("increases your", "seconds", "speed", "strength", "armor", "evasion", "resistance")
    heal_words = ("heal", "life leech", "leech", "recover", "shield", "absorb", "drain")
    projectile_words = ("arrow", "bolt", "shard", "lightning", "projectile", "shoot", "fire")

    if any(w in text for w in heal_words):
        return {"effect": "rally", "multiplier": 0.0, "cooldown": 18.0, "radius": 0.0,
                "healPercent": 0.28, "buffBonus": 0.15, "buffSeconds": 6.0}
    if any(w in text for w in aoe_words):
        return {"effect": "nova", "multiplier": 1.4, "cooldown": 8.0, "radius": 5.5,
                "healPercent": 0.0, "buffBonus": 0.0, "buffSeconds": 0.0}
    if any(w in text for w in buff_words):
        return {"effect": "rally", "multiplier": 0.0, "cooldown": 16.0, "radius": 0.0,
                "healPercent": 0.15, "buffBonus": 0.25, "buffSeconds": 6.0}
    if any(w in text for w in projectile_words) or "lightning" in tag_set:
        return {"effect": "strike", "multiplier": 1.9, "cooldown": 5.0, "radius": 0.0,
                "healPercent": 0.0, "buffBonus": 0.0, "buffSeconds": 0.0}
    return {"effect": "strike", "multiplier": 2.2, "cooldown": 4.0, "radius": 0.0,
            "healPercent": 0.0, "buffBonus": 0.0, "buffSeconds": 0.0}


def derive_passive(name: str, description: str) -> dict:
    text = f"{name} {description}".lower()
    if any(w in text for w in ("magic find", "item quantity", "treasure")):
        return {"effect": "fortune", "offenseBonus": 0.05, "healthBonus": 0.0}
    if any(w in text for w in ("armor", "resistance", "evasion", "defense", "fortress")):
        return {"effect": "warding", "offenseBonus": 0.0, "healthBonus": 0.15}
    if any(w in text for w in ("cooldown", "casting", "attack speed")):
        return {"effect": "haste", "offenseBonus": 0.10, "healthBonus": 0.0}
    if any(w in text for w in ("intelligence", "strength", "damage", "deadly", "pierce", "overkill")):
        return {"effect": "might", "offenseBonus": 0.12, "healthBonus": 0.0}
    return {"effect": "might", "offenseBonus": 0.10, "healthBonus": 0.05}


def main() -> int:
    powers = {p["Id"]: p for p in load("Powers")}
    ptypes = {p["Id"]: p.get("SerializedData", {}) for p in load("PowerTypes")}
    tags = {t["Id"]: t["Tag"] for t in load("PowerTags")}
    classes = load("CharacterClasses")

    def tag_names(power) -> list[str]:
        return [tags[t] for t in (power.get("SerializedData", {}).get("TagIds") or []) if t in tags]

    def type_name(power) -> str:
        return ptypes.get(power.get("SerializedData", {}).get("TypeId"), {}).get("Name", "?")

    def active_entry(power, slot: int) -> dict:
        sd = power["SerializedData"]
        tags_ = tag_names(power)
        effect = derive_effect(sd.get("Name", ""), sd.get("Description") or "", tags_)
        return {
            "slot": slot,
            "id": power["Id"],
            "integerId": power["IntegerId"],
            "name": sd.get("Name"),
            "nameKey": sd.get("NameTranslationKey"),
            "description": sd.get("Description") or "",
            "descriptionKey": sd.get("DescriptionTranslationKey"),
            "icon": basename(sd.get("Image")),
            "type": type_name(power),
            "tags": tags_,
            "confidence": "provisional-behaviour",
            **effect,
        }

    def passive_entry(power) -> dict:
        sd = power["SerializedData"]
        effect = derive_passive(sd.get("Name", ""), sd.get("Description") or "")
        return {
            "id": power["Id"],
            "integerId": power["IntegerId"],
            "name": sd.get("Name"),
            "nameKey": sd.get("NameTranslationKey"),
            "description": sd.get("Description") or "",
            "descriptionKey": sd.get("DescriptionTranslationKey"),
            "icon": basename(sd.get("Image")),
            "type": type_name(power),
            "tags": tag_names(power),
            "confidence": "provisional-behaviour",
            **effect,
        }

    out_classes: dict[str, dict] = {}
    for cls in classes:
        if cls.get("Hidden"):
            continue
        active = [active_entry(powers[pid], slot) for slot, pid in enumerate(cls.get("ActiveSkills", [])[:ACTIVE_PER_CLASS]) if pid in powers]
        passive_ids = [pid for pid in cls.get("PassiveSkills", []) if pid in powers]
        passive = passive_entry(powers[passive_ids[0]]) if passive_ids else None
        if not active or passive is None:
            continue
        out_classes[str(cls["IntegerId"])] = {
            "name": cls["Name"],
            "active": active,
            "passive": passive,
            "activePoolSize": len(cls.get("ActiveSkills", [])),
            "passivePoolSize": len(cls.get("PassiveSkills", [])),
        }

    document = {"contentVersion": CONTENT_VERSION, "classes": out_classes}
    WEB_OUT.write_text(json.dumps(document, indent=2))
    write_csharp(out_classes)
    print(f"wrote {WEB_OUT}")
    print(f"  classes: {', '.join(v['name'] for v in out_classes.values())}")
    for key, value in out_classes.items():
        actives = ", ".join(a["name"] for a in value["active"])
        print(f"  class {key} {value['name']}: active=[{actives}] passive={value['passive']['name']}")
    return 0


def cs_str(value: str | None) -> str:
    return '"' + (value or "").replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\r", "") + '"'


def write_csharp(out_classes: dict) -> None:
    lines = [
        "// <auto-generated> by tools/web-content/export_powers.py — do not edit by hand.",
        "using Nordicandia.Simulation;",
        "",
        "namespace Nordicandia.Server.WebApi;",
        "",
        "public static partial class PowerCatalog",
        "{",
        "    public static readonly IReadOnlyDictionary<int, ClassPowers> ByClass = new Dictionary<int, ClassPowers>",
        "    {",
    ]
    for key, value in out_classes.items():
        lines.append(f"        [{key}] = new ClassPowers({cs_str(value['name'])}, new SkillProfile[]")
        lines.append("        {")
        for a in value["active"]:
            lines.append(
                "            new(" + ", ".join([
                    str(a["slot"]), cs_str(a["name"]), cs_str(a["description"]), cs_str(a["icon"]),
                    cs_str(a["effect"]), f"{a['multiplier']}D", f"{a['cooldown']}D", f"{a['radius']}D",
                    f"{a['healPercent']}D", f"{a['buffBonus']}D", f"{a['buffSeconds']}D",
                ]) + "),")
        lines.append("        }, new PassiveProfile(" + ", ".join([
            cs_str(value["passive"]["name"]), cs_str(value["passive"]["description"]),
            cs_str(value["passive"]["icon"]), cs_str(value["passive"]["effect"]),
            f"{value['passive']['offenseBonus']}D", f"{value['passive']['healthBonus']}D",
        ]) + ")),")
    lines += ["    };", "}", ""]
    CS_OUT.write_text("\n".join(lines))


if __name__ == "__main__":
    raise SystemExit(main())
