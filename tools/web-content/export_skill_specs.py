#!/usr/bin/env python3
"""C04: per-skill behavior specs.

Reads the recovered power data and emits, for each of the 93 player skills:
  - cast conditions / target / resources / cooldown / level curve / effect timeline /
    buffs / projectile / summon / mastery rewrites
  - behaviorGaps: real recovered attributes the current prototype does NOT implement.

The gaps list is the anti-silent-mapping inventory: anything here was previously
folded into strike/nova/rally/... without being implemented. Nothing is dropped.

Outputs:
  tools/web-content/generated/skill_specs.json
  docs/web/C04_SKILL_SPECS.md
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
GEN = ROOT / "tools/web-content/generated"

powers_full = json.load(open(GEN / "powers_full.json"))
power_values = json.load(open(GEN / "power_values.json"))
power_pools = json.load(open(GEN / "power_pools.json"))
passive_recovery_path = GEN / "passive_skill_recovery.json"
passive_recovery = json.load(open(passive_recovery_path)) if passive_recovery_path.exists() else {"skills": []}
passive_recovery_by_name = {s["name"]: s for s in passive_recovery.get("skills", [])}

# Current prototype mapping, parsed from the generated server catalog (the actual
# mapping in force, not the classifier).
effect_by_name: dict[str, str] = {}
conf_by_name: dict[str, str] = {}
gen = (ROOT / "server/Nordicandia.Server/WebApi/PowerProfiles.generated.cs").read_text()
for m in re.finditer(r'new\((?:\d+, )?\"([^\"]+)\", \"[^\"]*\", \"[^\"]*\", \"([^\"]+)\"(.*)', gen):
    name, effect, rest = m.group(1), m.group(2), m.group(3)
    cm = re.search(r'"(client-verified|provisional-behaviour|provisional)"', rest)
    if effect in ("strike", "nova", "chain", "projectile", "rally", "mobility",
                  "shield", "leech", "summon", "aura", "might", "warding",
                  "haste", "fortune"):
        effect_by_name.setdefault(name, effect)
        if cm:
            conf_by_name.setdefault(name, cm.group(1))

# Attributes each prototype actually implements (from CombatInstance.UseSkill +
# CombatInstance.Effects). Base_Cooldown / Base_Mana_Cost are handled by the cast
# gate for every skill and never count as gaps.
ALWAYS_HANDLED = {"Base_Cooldown", "Base_Mana_Cost"}
HANDLED: dict[str, set[str]] = {
    "strike": {"Base_Power_Weapon_Damage_Multiplier"},
    "nova": {"Base_Power_Weapon_Damage_Multiplier", "Base_Power_Radius",
             "Power_War_Stomp_Stun_Duration", "Power_Freeze_Duration"},
    "chain": {"Base_Power_Weapon_Damage_Multiplier", "Base_Power_Radius",
              "ChainLightning_Max_Num_Chains"},
    "projectile": {"Base_Power_Weapon_Damage_Multiplier",
                   "Power_Power_Shot_Pierce_Chance_Percent", "Power_Projectile_Pierce_Chance",
                   "Power_Projectile_Fork_Chance", "Power_Projectile_Chain_Chance"},
    "rally": {"Buff_Duration", "Strength_Bonus_Percent", "Attack_Speed_Bonus_Percent",
              "Buff_Stack_Duration", "Power_Incremental_Fortress_Armor_Bonus_Percent_Per_Stack",
              "Potion_Effect_Increase_Percent"},
    "mobility": {"Buff_Duration", "Movement_Speed_Bonus_Percent"},
    "shield": {"Buff_Duration", "Mana_Shield_Life_Factor"},
    "leech": {"Buff_Duration", "Life_Leech_Maximum_Recovery_Bonus_Percent"},
    "summon": set(),
    "aura": set(),
    "might": set(),
    "warding": set(),
    "haste": set(),
    "fortune": set(),
}

# Skill-name handlers are explicit because the production runtime has accumulated
# dedicated implementations (C05/C06) that are more specific than their prototype.
# Only list attributes actually consumed by those handlers; approximation parameters,
# state stored without runtime effect, and unknown fields remain visible as gaps.
HANDLED_BY_NAME: dict[str, set[str]] = {
    "Retaliation": {"Buff_Duration"},
    "Intimidate": {"Buff_Duration", "Power_Intimidate_Opponent_All_Damage_Reduced_Percent"},
    "Whirlwind": {"Power_Duration", "Power_Whirlwind_Damage_Frequency"},
    "FrozenArrow": {"Base_Power_Radius"},
    "ManaArrows": {"Power_Mana_Arrows_Mana_Per_Hit", "Power_Mana_Arrows_Num_Charges"},
    "Fade": {"Evasion_Bonus_Percent"},
    "ImpalingTrap": {"Power_Duration"},
    "Backflip": {"Power_Backflip_Mirror_Image_Duration", "Power_Backflip_Mirror_Image_Health_Percent"},
    "MarkOfTheChosen": {"Power_Duration", "Power_Mark_Of_The_Chosen_Amplify_Damage_Taken_Percent"},
    "Thorns": {"Base_Physical_Damage_Reduction_Bonus", "Reflect_Melee_Damage_Percent"},
    "Blizzard": {"Power_Duration"},
    "ElementalSeal": {"Power_Duration"},
    "ManaShield": {"Mana_Shield_Damage_Absorbtion_Factor"},
    "IceBlast": {"Base_Power_Radius", "Power_Weapon_Damage_Multiplier_2"},
    "InfernalBlast": {"Base_Power_Radius", "Power_Duration", "Power_Weapon_Damage_Multiplier_2"},
    "Thunderstrike": {"Power_Duration"},
    "Tornado": {"Base_Power_Radius"},
    "PoisonCloud": {"Power_Duration", "Power_Slow_Effect_Percent"},
    "AstralWalk": {"AstralWalk_Movement_Speed_Bonus_Percent"},
    "Shadowbolt": {"Base_Power_Radius", "Power_Weapon_Damage_Multiplier_2"},
    "DrainLife": {"Power_Duration", "Base_Power_Weapon_Damage_Multiplier",
                  "Power_Drain_Life_Percent_Of_Life_Leech_Leeched_As_Life",
                  "Power_Drain_Life_Life_Leech_Bonus_Per_Active_Minion"},
    "BoneLink": {"Amplify_Damage_Taken_Percent", "Buff_Duration"},
    "Decay": {"Power_Duration", "Power_Decay_Target_Reduced_Attack_Speed_Percent"},
    "DemonicPresence": {"Power_Duration"},
    "Sacrifice": {"Mana_Granted_Percent", "Next_Offensive_Spell_Cast_Damage_Increased_Percent",
                  "Power_Duration"},
}


def display_gap(name: str, kind: str, attr: str) -> str:
    if kind != "passive":
        return attr
    match = re.fullmatch(r"field_0x([0-9a-fA-F]+)", attr)
    recovered = passive_recovery_by_name.get(name)
    if not match or recovered is None:
        return attr
    offset = int(match.group(1), 16)
    field = next((f["name"] for f in recovered.get("nativeFieldAssignments", [])
                  if f.get("nativeOffset") is not None and int(f["nativeOffset"], 16) == offset
                  and f.get("mappingConfidence") != "unpaired"), None)
    return f"{field} [field_0x{offset:x}]" if field else attr


def handled_attr(name: str, proto: str, attr: str) -> bool:
    if attr in ALWAYS_HANDLED:
        return True
    if attr in HANDLED_BY_NAME.get(name, set()):
        return True
    hs = HANDLED.get(proto, set())
    if attr in hs:
        return True
    # wildcard groups
    if proto == "leech" and "Life_Leech" in attr:
        return True
    # Do not infer coverage from an attribute-name substring or from a passive
    # prototype. Explicit per-name/attribute mappings above are the only exceptions;
    # unverified passives and summon fields must stay visible until runtime coverage
    # is demonstrated.


def target_of(tags: list[str], proto: str) -> str:
    if proto in ("rally", "mobility", "shield", "leech", "aura"):
        return "self"
    if proto == "summon":
        return "self (minions)"
    if "Melee" in tags:
        return "melee enemy"
    if proto in ("projectile", "strike", "chain"):
        return "enemy"
    if proto == "nova":
        return "enemies in radius"
    return "unknown"


specs = []
for cls, kit in power_pools.items():
    for kind in ("active", "passive"):
        for s in kit[kind]:
            name = s["name"]
            vals = power_values.get(name, {})
            full = powers_full["powers"].get(s.get("id", ""), {})
            proto = effect_by_name.get(name, s.get("effect", "unknown"))
            gaps = sorted(display_gap(name, kind, a) for a in vals if not handled_attr(name, proto, a))
            buff_attrs = sorted(a for a in vals if "Buff" in a or "Leech" in a)
            proj_attrs = sorted(a for a in vals if "Projectile" in a or "Pierce" in a or "Fork" in a or "Chain" in a)
            summon_attrs = sorted(a for a in vals if "Minion" in a or "Summon" in a)
            masteries = [m["name"] for m in powers_full["masteriesByPower"].get(s.get("id", ""), [])]
            specs.append({
                "name": name,
                "class": cls,
                "kind": kind,
                "implementedBy": s.get("implementedBy", ""),
                "tags": s.get("tags", full.get("tags", [])),
                "prototype": proto,
                "prototypeConfidence": conf_by_name.get(name, "unknown"),
                "castConditions": {
                    "requiredWeapons": len(full.get("requiredWeapons", [])),
                    "targetedCast": full.get("isTargetedCast"),
                },
                "target": target_of(s.get("tags", []), proto),
                "resources": {"mana": vals.get("Base_Mana_Cost")},
                "cooldown": vals.get("Base_Cooldown"),
                "levelCurve": {"status": "not recovered",
                               "note": "per-rank scaling lives in IL2CPP method bodies"},
                "effectTimeline": {
                    "duration": vals.get("Power_Duration") or vals.get("Buff_Duration"),
                    "radius": vals.get("Base_Power_Radius"),
                    "damageMultiplier": vals.get("Base_Power_Weapon_Damage_Multiplier"),
                },
                "buffs": buff_attrs,
                "projectile": proj_attrs or None,
                "summon": summon_attrs or None,
                "masteryCount": len(masteries),
                "masteries": masteries,
                "behaviorGaps": gaps,
                "nativePassiveRecovery": ({
                    "sourceAvailable": passive_recovery_by_name[name]["sourceAvailable"],
                    "recoveryStatus": passive_recovery_by_name[name]["recoveryStatus"],
                    "fields": passive_recovery_by_name[name]["dumpFields"],
                    "nativeFieldAssignments": passive_recovery_by_name[name]["nativeFieldAssignments"],
                    "initializerFormulas": passive_recovery_by_name[name]["initializerFormulas"],
                    "suspiciousInitializerValues": passive_recovery_by_name[name]["suspiciousInitializerValues"],
                    "applyAttributes": passive_recovery_by_name[name]["applyAttributes"],
                    "applyAttributeWrites": passive_recovery_by_name[name]["applyAttributeWrites"],
                    "methods": passive_recovery_by_name[name]["methods"],
                    "nestedTypes": [n["type"] for n in passive_recovery_by_name[name]["nestedTypes"]],
                    "triggerHooks": passive_recovery_by_name[name]["triggerHooks"],
                } if kind == "passive" and name in passive_recovery_by_name else None),
            })

json.dump({"count": len(specs), "specs": specs},
          open(GEN / "skill_specs.json", "w"), ensure_ascii=False, indent=1)

# ---- Markdown ----
gapped = [s for s in specs if s["behaviorGaps"]]
lines = []
lines.append("# C04：逐技能行为规格（2026-10-03）")
lines.append("")
lines.append(f"共 {len(specs)} 个技能（主动 {sum(1 for s in specs if s['kind']=='active')} / "
             f"被动 {sum(1 for s in specs if s['kind']=='passive')}）。")
lines.append("每个技能的施法条件/目标/资源/冷却/等级曲线/效果时间线/Buff/弹体/召唤/精通改写见 "
             "`tools/web-content/generated/skill_specs.json`。")
lines.append("")
lines.append("## 反静默映射清单")
lines.append("")
lines.append(f"有 **{len(gapped)}** 个技能带有当前原型**未处理**的真实属性或专属参数（behaviorGaps）——"
             "这些曾被静默折叠进原型，现逐条显式列出：")
lines.append("")
lines.append("| 技能 | 职业 | 原型 | 未处理属性/参数 |")
lines.append("|---|---|---|---|")
for s in sorted(gapped, key=lambda x: (x["prototype"], x["name"])):
    lines.append(f"| {s['name']} | {s['class']} | `{s['prototype']}` | {', '.join(s['behaviorGaps'])} |")
lines.append("")
lines.append("## 说明")
lines.append("")
lines.append("- `prototypeConfidence` 来自生成目录的实际映射（`PowerProfiles.generated.cs`），"
             "不是分类器推测。")
lines.append("- `behaviorGaps` 同时计入通用原型和 C05/C06 按技能分支；只读取但未产生实际效果的值仍保留为 gap。被动技能附带 `nativePassiveRecovery`，链接原生 Init/Apply/Remove 地址、字段及属性写入；原始 field offset 只在原生 this-store 与 Cpp2IL 字段赋值可配对时命名，不再用桌面 dump.cs 的同偏移字段直接推断。")
lines.append("- 整体技能等级曲线仍未完整恢复；passive sidecar 暴露已解出的初始化字段公式，"
             "但不把局部字段公式冒充完整技能成长或已接入运行时。")
lines.append("- 被动与召唤属性不再按 prototype 或字段名通配豁免；未逐项证实的属性（含 field_0x120/field_0x128）保留在 gaps。")
lines.append("- 本清单是 C05（首批 4 技能贯通）与 C06（生产技能池去原型化）的输入。")
(ROOT / "docs/web/C04_SKILL_SPECS.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"specs: {len(specs)}, gapped: {len(gapped)}")
