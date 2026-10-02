#!/usr/bin/env python3
"""Full extraction of Nordicandia's powers/masteries for the web remake.

Extracts everything that exists in the decrypted data plus the IL2CPP type dump:

  gamedata_decrypted/Powers.json           159 powers (name/desc/icon/type/tags/weapons)
  gamedata_decrypted/PowerTypes.json       26 types + hierarchy + eligible slots
  gamedata_decrypted/PowerTags.json        19 damage/school tags
  gamedata_decrypted/PowerMasteries.json   297 masteries (skill-tree upgrades)
  gamedata_decrypted/CharacterClasses.json per-class pools (abilities/active/passive/tactics)
  steam_analysis/dump.cs                   IL2CPP type dump: [HandledPower]/[HandledPowerMastery]
                                           -> implementation class + field names

Outputs:
  web/public/assets/powers.json            display content used by the client
  tools/web-content/generated/powers_full.json  full reference extraction (powers+masteries)
  server/Nordicandia.Server/WebApi/Powers.generated.cs  server behaviour table (3 active + passive)

Confidence:
  * names/descriptions/icons/types/tags/weapons/pools/mastery trees = ClientVerified
  * description placeholder *roles* are inferred from wording = Inferred
  * numeric effect values are NOT in the data; they live in IL2CPP method bodies, so the
    server table keeps provisional numbers. The dump only yields parameter field names.

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
DUMP = ROOT / "steam_analysis/dump.cs"
WEB_OUT = ROOT / "web/public/assets/powers.json"
FULL_OUT = ROOT / "tools/web-content/generated/powers_full.json"
CS_OUT = ROOT / "server/Nordicandia.Server/WebApi/Powers.generated.cs"
CONTENT_VERSION = "m0-1"
ACTIVE_PER_CLASS = 3

SLOT_TYPES = {
    "-1": "Void", "0": "Ability", "1": "ActiveSkill", "2": "PassiveSkill",
    "3": "Tactics", "4": "SkillTraining", "5": "SpecialSkill",
}


def load(name: str):
    return json.loads((GAMEDATA / f"{name}.json").read_text())


def basename(path: str | None) -> str | None:
    if not path:
        return None
    return re.sub(r"\.[Pp][Nn][Gg]$", "", path.replace("\\", "/").rsplit("/", 1)[-1])


# ---------------------------------------------------------------- dump parsing

def parse_dump(path: pathlib.Path) -> tuple[dict, dict]:
    """Map power/mastery name -> {class, fields} from the IL2CPP dump (optional)."""
    handled_power: dict[str, dict] = {}
    handled_mastery: dict[str, dict] = {}
    if not path.exists():
        print(f"  (dump {path} missing — skipping implementation mapping)")
        return handled_power, handled_mastery

    text = path.read_text(errors="ignore")
    lines = text.splitlines()
    i = 0
    while i < len(lines):
        line = lines[i]
        m = re.search(r'\[HandledPower\(new string\[\d+\] \{"([^"]+)"', line)
        table = handled_power
        if not m:
            m = re.search(r'\[HandledPowerMastery\(new string\[\d+\] \{"([^"]+)"', line)
            table = handled_mastery
        if not m:
            i += 1
            continue
        target = m.group(1)
        j = i + 1
        while j < len(lines) and "class " not in lines[j]:
            j += 1
        cls = re.search(r'class\s+([A-Za-z0-9_]+)', lines[j]) if j < len(lines) else None
        fields: list[str] = []
        k = j + 1
        while k < len(lines) and not lines[k].startswith("\t}"):
            fm = re.search(r'private\s+[\w<>\[\],\. ]+\s+(_?[A-Za-z0-9_]+);', lines[k])
            if fm:
                fields.append(fm.group(1))
            k += 1
        table[target] = {"class": cls.group(1) if cls else None, "fields": fields}
        i = k
    return handled_power, handled_mastery


# ---------------------------------------------------------------- placeholders

ROLE_PATTERNS = [
    (r"\{i\}%? ?weapon damage", "damage"),
    (r"deal[s]? \{i\}", "damage"),
    (r"\{i\}% ?(?:of )?(?:the )?(?:target|enemy)", "percent"),
    (r"for \{i\} ?second", "duration"),
    (r"\{i\} ?second", "duration"),
    (r"within \{i\} ?unit", "radius"),
    (r"of \{i\} ?unit", "radius"),
    (r"\{i\} ?jump", "count"),
    (r"\{i\} ?arrow", "count"),
    (r"\{i\} ?charge", "count"),
    (r"costs \{i\} ?mana", "mana"),
    (r"\{i\} ?mana", "mana"),
    (r"\{i\} ?%", "percent"),
]


def placeholder_roles(description: str) -> list[dict]:
    roles = []
    for match in re.finditer(r"\{(\d+)\}", description):
        index = int(match.group(1))
        if any(r["index"] == index for r in roles):
            continue
        role = "value"
        for pattern, name in ROLE_PATTERNS:
            if re.search(pattern.replace(r"\{i\}", r"\{%d\}" % index), description, re.IGNORECASE):
                role = name
                break
        roles.append({"index": index, "role": role})
    return roles


# ---------------------------------------------------------------- behaviour mapping

def derive_effect(name: str, description: str, tags: list[str]) -> dict:
    text = f"{name} {description}".lower()
    tag_set = {t.lower() for t in tags}
    aoe_words = ("all enemies", "nearby", "area", "around you", "rain", "nova", "whirlwind", "shards")
    buff_words = ("increases your", "seconds", "speed", "strength", "armor", "evasion", "resistance")
    heal_words = ("heal", "life leech", "leech", "recover", "shield", "absorb", "drain")
    projectile_words = ("arrow", "bolt", "shard", "lightning", "projectile", "shoot", "fire")
    summons = ("summon", "minion", "companion", "wolf", "skeleton", "demon")

    if any(w in text for w in summons):
        return {"effect": "rally", "multiplier": 0.0, "cooldown": 18.0, "radius": 0.0,
                "healPercent": 0.20, "buffBonus": 0.15, "buffSeconds": 8.0}
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
    if any(w in text for w in ("armor", "resistance", "evasion", "defense", "fortress", "shield")):
        return {"effect": "warding", "offenseBonus": 0.0, "healthBonus": 0.15}
    if any(w in text for w in ("cooldown", "casting", "attack speed", "movement speed")):
        return {"effect": "haste", "offenseBonus": 0.10, "healthBonus": 0.0}
    if any(w in text for w in ("intelligence", "strength", "damage", "deadly", "pierce", "overkill")):
        return {"effect": "might", "offenseBonus": 0.12, "healthBonus": 0.0}
    return {"effect": "might", "offenseBonus": 0.10, "healthBonus": 0.05}


# ---------------------------------------------------------------- main

def main() -> int:
    powers = {p["Id"]: p for p in load("Powers")}
    ptypes = {p["Id"]: p.get("SerializedData", {}) for p in load("PowerTypes")}
    tags = {t["Id"]: t["Tag"] for t in load("PowerTags")}
    masteries = load("PowerMasteries")
    classes = load("CharacterClasses")
    handled_power, handled_mastery = parse_dump(DUMP)

    def tag_names(power) -> list[str]:
        return [tags[t] for t in (power.get("SerializedData", {}).get("TagIds") or []) if t in tags]

    def type_name(power) -> str:
        return ptypes.get(power.get("SerializedData", {}).get("TypeId"), {}).get("Name", "?")

    def power_record(power) -> dict:
        sd = power["SerializedData"]
        name = sd.get("Name")
        desc = sd.get("Description") or ""
        impl = handled_power.get(name, {})
        return {
            "id": power["Id"],
            "integerId": power["IntegerId"],
            "name": name,
            "nameKey": sd.get("NameTranslationKey"),
            "description": desc,
            "descriptionKey": sd.get("DescriptionTranslationKey"),
            "icon": basename(sd.get("Image")),
            "type": type_name(power),
            "typeId": sd.get("TypeId"),
            "tags": tag_names(power),
            "requiredWeapons": sd.get("RequiredWeaponItemTypeIds") or [],
            "isTargetedCast": bool(sd.get("IsTargetedCast", False)),
            "isHidden": bool(sd.get("IsHidden", False)),
            "placeholders": placeholder_roles(desc),
            "implementedBy": impl.get("class"),
            "parameterFields": impl.get("fields", []),
        }

    def mastery_record(mastery) -> dict:
        sd = mastery.get("SerializedData", {})
        impl = handled_mastery.get(sd.get("Name"), {})
        return {
            "id": mastery["Id"],
            "integerId": mastery["IntegerId"],
            "parentPowerId": sd.get("ParentPowerId"),
            "name": sd.get("Name"),
            "nameKey": sd.get("NameTranslationKey"),
            "descriptionKey": sd.get("DescriptionTranslationKey"),
            "description": sd.get("Description") or "",
            "icon": basename(sd.get("Image")),
            "category": sd.get("MasteryCategory"),
            "treeRow": sd.get("MasteryTreeRow"),
            "maxPoints": sd.get("MaxAllocatablePoints"),
            "isProxyBuff": bool(sd.get("IsProxyBuff", False)),
            "isHidden": bool(sd.get("IsHidden", False)),
            "dependencies": sd.get("MasteryDependencyIds") or [],
            "attributeSpecifiers": sd.get("AttributeSpecifierDefinitionList") or [],
            "implementedBy": impl.get("class"),
            "parameterFields": impl.get("fields", []),
        }

    mastery_records = [mastery_record(m) for m in masteries]
    by_parent: dict[str, list] = {}
    for record in mastery_records:
        by_parent.setdefault(record["parentPowerId"], []).append(record)

    out_classes: dict[str, dict] = {}
    for cls in classes:
        pool = {
            "abilities": [power_record(powers[p]) for p in cls.get("Abilities", []) if p in powers],
            "active": [power_record(powers[p]) for p in cls.get("ActiveSkills", []) if p in powers],
            "passive": [power_record(powers[p]) for p in cls.get("PassiveSkills", []) if p in powers],
            "tactics": [power_record(powers[p]) for p in cls.get("Tactics", []) if p in powers],
        }
        if not pool["active"] and not pool["passive"]:
            continue
        out_classes[str(cls["IntegerId"])] = {
            "name": cls["Name"],
            "hidden": bool(cls.get("Hidden", False)),
            "image": basename(cls.get("Image")),
            **{k: v for k, v in pool.items() if v},
            "poolSize": {k: len(v) for k, v in pool.items() if v},
        }

    # Two active + passive chosen for the web kit: first N actives, first passive.
    kits: dict[str, dict] = {}
    for key, value in out_classes.items():
        active = value.get("active", [])[:ACTIVE_PER_CLASS]
        passive_pool = value.get("passive", [])
        if not active or not passive_pool:
            continue
        passive = passive_pool[0]
        kits[key] = {
            "name": value["name"],
            "active": [dict(a, **derive_effect(a["name"], a["description"], a["tags"]), slot=i, confidence="provisional-behaviour")
                       for i, a in enumerate(active)],
            "passive": dict(passive, **derive_passive(passive["name"], passive["description"]), confidence="provisional-behaviour"),
            "activePoolSize": len(value.get("active", [])),
            "passivePoolSize": len(passive_pool),
            "tacticsPoolSize": len(value.get("tactics", [])),
        }

    web_document = {
        "contentVersion": CONTENT_VERSION,
        "slotTypes": SLOT_TYPES,
        "tags": tags,
        "types": {pid: {"name": sd.get("Name"), "parentId": sd.get("ParentTypeId"),
                            "eligibleSlots": sd.get("EligibleSlots")} for pid, sd in ptypes.items()},
        "classes": out_classes,
        "kits": kits,
    }
    WEB_OUT.write_text(json.dumps(web_document, indent=2))

    full_document = {
        "contentVersion": CONTENT_VERSION,
        "counts": {
            "powers": len(powers),
            "powerTypes": len(ptypes),
            "powerTags": len(tags),
            "masteries": len(masteries),
            "classes": len(out_classes),
            "handledPowers": len(handled_power),
            "handledMasteries": len(handled_mastery),
        },
        "powers": {p["SerializedData"].get("Name"): power_record(p) for p in powers.values()},
        "masteriesByPower": by_parent,
    }
    FULL_OUT.parent.mkdir(parents=True, exist_ok=True)
    FULL_OUT.write_text(json.dumps(full_document, indent=2))

    write_csharp(kits)
    print(f"wrote {WEB_OUT}")
    print(f"wrote {FULL_OUT}")
    print(f"wrote {CS_OUT}")
    print(f"  powers={len(powers)} types={len(ptypes)} tags={len(tags)} masteries={len(masteries)} "
          f"classes={len(out_classes)} handledPower={len(handled_power)} handledMastery={len(handled_mastery)}")
    for key, value in kits.items():
        print(f"  class {key} {value['name']}: active={[a['name'] for a in value['active']]} passive={value['passive']['name']}")
    return 0


def cs_str(value: str | None) -> str:
    return '"' + (value or "").replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\r", "") + '"'


def write_csharp(kits: dict) -> None:
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
    for key, value in kits.items():
        lines.append(f"        [{key}] = new ClassPowers({cs_str(value['name'])}, new SkillProfile[]")
        lines.append("        {")
        for a in value["active"]:
            lines.append(
                "            new(" + ", ".join([
                    str(a["slot"]), cs_str(a["name"]), cs_str(a["description"]), cs_str(a["icon"]),
                    cs_str(a["effect"]), f"{a['multiplier']}D", f"{a['cooldown']}D", f"{a['radius']}D",
                    f"{a['healPercent']}D", f"{a['buffBonus']}D", f"{a['buffSeconds']}D",
                ]) + "),")
        p = value["passive"]
        lines.append("        }, new PassiveProfile(" + ", ".join([
            cs_str(p["name"]), cs_str(p["description"]), cs_str(p["icon"]), cs_str(p["effect"]),
            f"{p['offenseBonus']}D", f"{p['healthBonus']}D",
        ]) + ")),")
    lines += ["    };", "}", ""]
    CS_OUT.write_text("\n".join(lines))


if __name__ == "__main__":
    raise SystemExit(main())
