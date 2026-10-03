#!/usr/bin/env python3
"""Export the E01 full item-affix catalog from gamedata_decrypted/ItemAffixes.json.

ItemAffixes.json (626) is the client's item-affix set: Implicit 494, Prefix 60,
Suffix 37, Set 12, Unique 9, unknown 14. Item generation draws random affixes from the
Prefix/Suffix subset, filtered by Domain (0=Item, 2=Area, 4=Monster) and by the item's
tags (TagData: tag guid -> {Weight, ValueMultiplier}).

This writes the server-embedded GameData/affix_catalog.json with every random (Prefix/
Suffix) item affix, its attributes (id/name/per-rarity ranges) and tag weights, so loot
generation can use the real catalog instead of the curated web subset. Reference
resolution (attribute id, group, tag) is checked and reported.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/export_item_affix_catalog.py
"""
from __future__ import annotations

import json
import pathlib
import sys

ROOT = pathlib.Path(__file__).resolve().parents[2]
GAMEDATA = ROOT / "gamedata_decrypted"
GENERATED = ROOT / "tools" / "web-content" / "generated"
OUT = ROOT / "server" / "Nordicandia.Server" / "GameData" / "affix_catalog.json"
TYPE_TAGS_OUT = ROOT / "server" / "Nordicandia.Server" / "GameData" / "item_type_tags.json"
REPORT = GENERATED / "affix_catalog_report.json"

RANDOM_TYPES = (0, 1)  # Prefix, Suffix


def ranges(spec: dict) -> list[dict]:
    out = []
    for vr in spec.get("ValueRangeByRarityList") or []:
        value_range = vr.get("ValueRange") or {}
        out.append({
            "rarity": vr.get("Rarity"),
            "min": value_range.get("MinValue", 0.0),
            "max": value_range.get("MaxValue", 0.0),
            "valueType": value_range.get("ValueType"),
        })
    return out


def inner(entry: dict) -> dict:
    sd = entry.get("SerializedData")
    if isinstance(sd, str):
        return json.loads(sd)
    return sd or {k: v for k, v in entry.items() if k != "SerializedData"}


def type_eligibility(tags: dict) -> dict[str, tuple[set, set]]:
    """Item-type name -> (inherited tag names, inherited explicit affix guids)."""
    item_types = json.loads((GAMEDATA / "ItemTypes.json").read_text())
    by_id = {t["Id"]: t for t in item_types}
    cache: dict[str, tuple[set, set]] = {}

    def resolve(gid: str) -> tuple[set, set]:
        names: set = set()
        affixes: set = set()
        seen: set = set()
        cur = gid
        while cur and cur not in seen:
            seen.add(cur)
            entry = by_id.get(cur)
            if not entry:
                break
            data = inner(entry)
            names |= {tags.get(t) for t in data.get("TagIds", [])}
            affixes |= set(data.get("AffixIds", []))
            cur = data.get("ParentTypeId")
        return {n for n in names if n}, affixes

    for entry in item_types:
        name = inner(entry).get("Name")
        if name:
            cache[name] = resolve(entry["Id"])
    return cache


def main() -> int:
    affixes = json.loads((GAMEDATA / "ItemAffixes.json").read_text())
    attr_ids = json.loads((GENERATED / "attribute_ids.json").read_text())
    tags = {t["Id"]: t["Tag"] for t in json.loads((GAMEDATA / "Tags.json").read_text()) if "Id" in t}
    groups = {g["Id"]: g["Name"] for g in json.loads((GAMEDATA / "AffixGroups.json").read_text()) if "Id" in g}
    type_tags = type_eligibility(tags)

    unresolved_attr: set = set()
    unresolved_tag: set = set()
    catalog: dict[str, dict] = {}

    for x in affixes:
        gen = x.get("GenerationType")
        if gen not in RANDOM_TYPES:
            continue
        entry_attrs = []
        for spec in x.get("AttributeSpecifierDefinitionList") or []:
            aid = spec.get("AttributeId")
            name = attr_ids.get(str(aid))
            if name is None:
                unresolved_attr.add(aid)
            entry_attrs.append({
                "attributeId": aid,
                "attributeName": name,
                "ranges": ranges(spec),
            })
        entry_tags = []
        for guid, meta in (x.get("TagData") or {}).items():
            if guid == "00000000-0000-0000-0000-000000000000":
                continue
            name = tags.get(guid)
            if name is None:
                unresolved_tag.add(guid)
            entry_tags.append({
                "tag": name,
                "guid": guid,
                "weight": (meta or {}).get("Weight"),
                "valueMultiplier": (meta or {}).get("ValueMultiplier", 1.0),
            })
        affix_tags = {t["tag"] for t in entry_tags if t["tag"]}
        affix_guid = x.get("Id")
        eligible = sorted(
            type_name for type_name, (names, guids) in type_tags.items()
            if affix_guid in guids or (affix_tags & names)
        )
        catalog[x["Name"]] = {
            "guid": affix_guid,
            "integerId": x.get("IntegerId"),
            "generationType": gen,
            "domain": x.get("Domain"),
            "group": groups.get(x.get("GroupId")),
            "groupId": x.get("GroupId"),
            "attributes": entry_attrs,
            "tags": entry_tags,
            "eligibleTypes": eligible,
            "dependencies": x.get("ItemAffixDependencies") or [],
        }

    # An item-domain affix (0) is what equipment can roll; 2/4 are area/monster modifiers.
    by_domain: dict[int, int] = {}
    by_type: dict[int, int] = {}
    for e in catalog.values():
        by_domain[e["domain"]] = by_domain.get(e["domain"], 0) + 1
        by_type[e["generationType"]] = by_type.get(e["generationType"], 0) + 1

    # Droprates.json side tables (affix rarity pool + class item-type multipliers).
    droprates = json.loads((GAMEDATA / "Droprates.json").read_text())
    (GENERATED / "affix_rarity.json").write_text(json.dumps({
        "baseline": "android-1.9.3 (versionCode 507033)",
        "source": "gamedata_decrypted/Droprates.json",
        "affixRarityRatio": droprates.get("AffixRarityRatio"),
        "itemTypeCharacterClassWeightMultipliers": droprates.get("ItemTypeCharacterClassWeightMultipliers"),
        "lootTableItemTypeWeights": [
            {"name": inner(t).get("Name"), "itemTypeWeights": inner(t).get("ItemTypeWeights")}
            for t in (droprates.get("LootTables") or [])
        ],
    }, ensure_ascii=False, indent=1, sort_keys=True) + "\n")

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(catalog, ensure_ascii=False, indent=1, sort_keys=True) + "\n")
    # Item-type -> inherited tag names, so loot can weight affixes by the item type's tags.
    TYPE_TAGS_OUT.write_text(json.dumps(
        {name: sorted(names) for name, (names, _guids) in type_tags.items()},
        ensure_ascii=False, indent=1, sort_keys=True) + "\n")
    REPORT.write_text(json.dumps({
        "baseline": "android-1.9.3 (versionCode 507033)",
        "source": "gamedata_decrypted/ItemAffixes.json",
        "randomAffixes": len(catalog),
        "byGeneration": by_type,
        "byDomain": by_domain,
        "unresolvedAttributeIds": sorted(unresolved_attr),
        "unresolvedTagGuids": sorted(unresolved_tag),
    }, ensure_ascii=False, indent=1) + "\n")

    print(f"wrote {OUT.relative_to(ROOT)}: {len(catalog)} random item affixes")
    print(f"  generation: {by_type}")
    print(f"  domain: {by_domain}")
    print(f"  unresolved attributes: {sorted(unresolved_attr)}")
    print(f"  unresolved tags: {sorted(unresolved_tag)}")
    return 1 if unresolved_attr else 0


if __name__ == "__main__":
    raise SystemExit(main())
