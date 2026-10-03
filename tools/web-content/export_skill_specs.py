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


def handled_attr(proto: str, attr: str) -> bool:
    if attr in ALWAYS_HANDLED:
        return True
    hs = HANDLED.get(proto, set())
    if attr in hs:
        return True
    # wildcard groups
    if proto == "leech" and "Life_Leech" in attr:
        return True
    if proto == "summon" and ("Minion" in attr or "Summon" in attr):
        return True
    if proto in ("might", "warding", "haste", "fortune"):
        return True  # passives: bonuses flow through the attribute engine
    return False


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
            gaps = sorted(a for a in vals if not handled_attr(proto, a))
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
lines.append(f"有 **{len(gapped)}** 个技能带有当前原型**未实现**的真实属性（behaviorGaps）——"
             "这些曾被静默折叠进原型，现逐条显式列出：")
lines.append("")
lines.append("| 技能 | 职业 | 原型 | 未实现的属性 |")
lines.append("|---|---|---|---|")
for s in sorted(gapped, key=lambda x: (x["prototype"], x["name"])):
    lines.append(f"| {s['name']} | {s['class']} | `{s['prototype']}` | {', '.join(s['behaviorGaps'])} |")
lines.append("")
lines.append("## 说明")
lines.append("")
lines.append("- `prototypeConfidence` 来自生成目录的实际映射（`PowerProfiles.generated.cs`），"
             "不是分类器推测。")
lines.append("- 等级曲线（per-rank scaling）在 IL2CPP 方法体内，93 个技能统一标记为未恢复，"
             "不虚构数值。")
lines.append("- 被动技能（might/warding/haste/fortune）的加成走属性引擎，不计入 gaps。")
lines.append("- 本清单是 C05（首批 4 技能贯通）与 C06（生产技能池去原型化）的输入。")
(ROOT / "docs/web/C04_SKILL_SPECS.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
print(f"specs: {len(specs)}, gapped: {len(gapped)}")
