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


# ---------------------------------------------------------------- class graph

def parse_class_graph(path: pathlib.Path):
    """class -> (base chain, method names) from dump.cs."""
    bases: dict[str, str] = {}
    methods: dict[str, list[str]] = {}
    if not path.exists():
        return bases, methods
    current = None
    for line in path.read_text(errors="ignore").splitlines():
        m = re.match(r"\t(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+)?class\s+([\w<>]+)\s*:\s*([\w<>,\. ]+?)\s*(?://|$)", line)
        if m:
            current = m.group(1)
            bases[current] = m.group(2).split(",")[0].strip()
            methods.setdefault(current, [])
            continue
        if re.match(r"\t(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+)?class\s+([\w<>]+)\b", line):
            current = re.match(r"\t(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+)?class\s+([\w<>]+)\b", line).group(1)
            methods.setdefault(current, [])
            continue
        if current and re.search(r"\b(public|private|protected|internal)\b.*\b(\w+)\s*\(", line):
            mm = re.search(r"\b(public|private|protected|internal)\b[\w<>\[\],\. ]*?\s+(\w+)\s*\(", line)
            if mm and not line.strip().startswith("//"):
                methods[current].append(mm.group(2))
    return bases, methods


def class_chain(bases: dict, name: str) -> list:
    out = []
    seen = set()
    while name and name not in seen:
        seen.add(name)
        out.append(name)
        name = bases.get(name)
    return out


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


def classify_effect(name: str, description: str, tags: list[str], values: dict, chain: list | None = None) -> str:
    """Effect classification. The class inheritance chain (recovered from the IL2CPP
    type dump) is authoritative; attributes and wording only disambiguate."""
    chain = chain or []
    joined = " ".join(chain)
    keys = list(values.keys())
    text = f"{name} {description}".lower()
    if "Nova" in chain:
        return "nova"
    if "Projectile" in joined or "Projectile" in chain:
        return "projectile"
    if "Aura" in joined:
        return "aura"
    if any(k.startswith("Minion_Inheritance") or "Minion_Duration" in k for k in keys):
        return "summon"
    if any(part in ("PowerScript", "Minion") for part in chain) and "ActionTimedSkill" not in chain:
        return "summon"
    if any("Num_Chains" in k for k in keys):
        return "chain"
    if any("Mana_Shield" in k for k in keys):
        return "shield"
    if any("Life_Leech" in k for k in keys):
        return "leech"
    if "Base_Power_Radius" in values and values["Base_Power_Radius"] > 0:
        return "nova"
    if any("Movement_Speed" in k for k in keys) and "Base_Power_Weapon_Damage_Multiplier" not in values:
        return "mobility"
    if any("Pierce" in k or "Num_Charges" in k for k in keys):
        return "projectile"
    if any(w in text for w in ("rain", "nova", "whirlwind", "all enemies", "nearby")):
        return "nova"
    if "Base_Power_Weapon_Damage_Multiplier" in values or "Power_Weapon_Damage_Multiplier_2" in values:
        return "strike"
    return "rally"


def special_of(values: dict) -> str:
    for key in values:
        if "Freeze" in key:
            return "freeze"
        if "Stun" in key:
            return "stun"
        if "Life_Leech" in key:
            return "leech"
        if key.startswith("Minion_Inheritance") or "Minion_Duration" in key:
            return "summon"
        if "Movement_Speed" in key:
            return "mobility"
        if "Mana_Shield" in key:
            return "shield"
        if "Pierce" in key:
            return "pierce"
    return ""


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
    class_bases, class_methods = parse_class_graph(DUMP)

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
            "baseChain": class_chain(class_bases, impl.get("class") or name),
            "methods": class_methods.get(impl.get("class") or "", []),
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

    # Real numeric values recovered from the Android libil2cpp.so by
    # tools/web-content/disasm_powers.py, keyed by implementation class.
    attr_path = ROOT / "tools/web-content/generated/attribute_ids.json"
    attr_ids = json.loads(attr_path.read_text()) if attr_path.exists() else {}

    def spec_out(spec):
        aid = spec.get("AttributeId")
        return {"AttributeId": aid,
                "AttributeName": attr_ids.get(str(aid), f"attr_{aid}"),
                "Value": spec.get("Value", 0.0),
                "StartValue": spec.get("StartValue", 0.0),
                "Operator": spec.get("AttributeOperator", 0),
                "ModifierType": spec.get("AttributeModifierType", 0),
                "ModifierForSpecificLevel": spec.get("ModifierForSpecificLevel", 0)}

    masteries_by_name = {}
    for power in powers.values():
        pname = power["SerializedData"].get("Name")
        rows = by_parent.get(power["Id"], [])
        if pname and rows:
            masteries_by_name[pname] = [
                {"Name": m["name"], "IntegerId": m["integerId"], "MaxPoints": m["maxPoints"] or 1,
                 "Specs": [spec_out(sp) for sp in m["attributeSpecifiers"]]}
                for m in rows
            ]

    values_path = ROOT / "tools/web-content/generated/power_values.json"
    values = json.loads(values_path.read_text()) if values_path.exists() else {}

    def fmt(value: float) -> str:
        return str(int(value)) if float(value).is_integer() else str(value)

    def fill_description(description: str, placeholders: list, resolved: dict) -> str:
        def repl(match):
            index = int(match.group(1))
            role = next((p["role"] for p in placeholders if p["index"] == index), "value")
            value = resolved.get(role)
            return fmt(value) if value is not None else match.group(0)
        return re.sub(r"\{(\d+)\}", repl, description)

    # Two active + passive chosen for the web kit: first N actives, first passive.
    kits: dict[str, dict] = {}
    for key, value in out_classes.items():
        active = value.get("active", [])[:ACTIVE_PER_CLASS]
        passive_pool = value.get("passive", [])
        if not active or not passive_pool:
            continue
        passive = passive_pool[0]

        def merged_active(a, slot):
            v = values.get(a.get("implementedBy") or "", {})
            effect = derive_effect(a["name"], a["description"], a["tags"])
            multiplier = v.get("Base_Power_Weapon_Damage_Multiplier")
            if multiplier is None:
                multiplier = v.get("Power_Weapon_Damage_Multiplier_2")
            cooldown = v.get("Base_Cooldown")
            mana = v.get("Base_Mana_Cost")
            radius = v.get("Base_Power_Radius")
            duration = v.get("Buff_Duration") or v.get("Power_Duration") or v.get("Power_Freeze_Duration")
            chain = class_chain(class_bases, a.get("implementedBy") or a["name"])
            effect_kind = classify_effect(a["name"], a["description"], a["tags"], v, chain)
            chains = int(v.get("ChainLightning_Max_Num_Chains", 0) or 0)
            verified = multiplier is not None or cooldown is not None or mana is not None
            resolved = {}
            if multiplier is not None:
                resolved["damage"] = multiplier * 100
            if cooldown is not None:
                resolved["duration"] = cooldown
            if mana is not None:
                resolved["mana"] = mana
            if radius is not None:
                resolved["radius"] = radius
            if v.get("ChainLightning_Max_Num_Chains") is not None:
                resolved["count"] = v["ChainLightning_Max_Num_Chains"]
            return dict(a, slot=slot, effect=effect_kind, special=special_of(v), chains=chains, baseChain=chain,
                        multiplier=multiplier if multiplier is not None else effect["multiplier"],
                        cooldown=cooldown if cooldown is not None else effect["cooldown"],
                        radius=radius if radius is not None else effect["radius"],
                        manaCost=mana if mana is not None else 0,
                        healPercent=effect["healPercent"],
                        buffBonus=v.get("Strength_Bonus_Percent", effect["buffBonus"]),
                        buffSeconds=duration if duration is not None else effect["buffSeconds"],
                        values=v, confidence="client-verified" if verified else "provisional-behaviour",
                        descriptionFilled=fill_description(a["description"], a["placeholders"], resolved),
                        masteries=[{"name": m["name"], "treeRow": m["treeRow"], "maxPoints": m["maxPoints"],
                                    "specs": m["attributeSpecifiers"]} for m in by_parent.get(a["id"], [])])

        def merged_passive(p):
            v = values.get(p.get("implementedBy") or "", {})
            effect = derive_passive(p["name"], p["description"])
            base = v.get("field_0x120")
            offense = effect["offenseBonus"]
            health = effect["healthBonus"]
            if base is not None:
                if effect["effect"] in ("might", "haste", "fortune"):
                    offense = base
                elif effect["effect"] == "warding":
                    health = base
            record = dict(p, **effect, values=v, special=special_of(v),
                          confidence="client-verified" if base is not None else "provisional-behaviour",
                          masteries=[{"name": m["name"], "treeRow": m["treeRow"], "maxPoints": m["maxPoints"],
                                      "specs": m["attributeSpecifiers"]} for m in by_parent.get(p["id"], [])])
            record["offenseBonus"] = offense
            record["healthBonus"] = health
            return record

        kits[key] = {
            "name": value["name"],
            "active": [merged_active(a, i) for i, a in enumerate(active)],
            "passive": merged_passive(passive),
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

    write_csharp(kits, masteries_by_name)
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


def cs_values(values) -> str:
    items = ", ".join(f"[{cs_str(k)}] = {v}D" for k, v in (values or {}).items())
    return "new Dictionary<string, double> { " + items + " }"


def write_csharp(kits: dict, masteries_by_name: dict) -> None:
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
                    f"{a['manaCost']}D", f"{a['healPercent']}D", f"{a['buffBonus']}D", f"{a['buffSeconds']}D",
                    cs_str(a["confidence"]), cs_values(a.get("values")),
                ]) + "),")
        p = value["passive"]
        lines.append("        }, new PassiveProfile(" + ", ".join([
            cs_str(p["name"]), cs_str(p["description"]), cs_str(p["icon"]), cs_str(p["effect"]),
            f"{p['offenseBonus']}D", f"{p['healthBonus']}D", cs_str(p["confidence"]),
        ]) + ")),")
    lines += ["    };", "",
              "    public static readonly IReadOnlyDictionary<string, IReadOnlyList<MasteryProfile>> MasteriesByPower = new Dictionary<string, IReadOnlyList<MasteryProfile>>",
              "    {"]
    for pname, rows in masteries_by_name.items():
        lines.append(f"        [{cs_str(pname)}] = new MasteryProfile[]")
        lines.append("        {")
        for m in rows:
            specs = ", ".join(
                f"new MasterySpec({sp['AttributeId']}, {cs_str(sp['AttributeName'])}, {sp['Value']}D, {sp['StartValue']}D, {sp['Operator']}, {sp['ModifierType']}, {sp['ModifierForSpecificLevel']})"
                for sp in m["Specs"])
            lines.append(f"            new({cs_str(m['Name'])}, {m['IntegerId']}, {m['MaxPoints']}, new MasterySpec[] {{ {specs} }}),")
        lines.append("        },")
    lines += ["    };", "}", ""]
    CS_OUT.write_text("\n".join(lines))


if __name__ == "__main__":
    raise SystemExit(main())
