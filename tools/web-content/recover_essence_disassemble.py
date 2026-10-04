#!/usr/bin/env python3
"""E04: recover the disassemble/essence evidence from the reviewed Android 1.9.3 binary.

Targets (all in tmp/apk-libil2cpp.so; the script fails on a different binary):
  Game.Items.CraftingUtils.GetDisassemblableAffixes           0x02C82770
    + predicate   <GetDisassemblableAffixes>b__14_0           0x02C86C40  (rarity >= 2)
    + IsOpenAffix <GetDisassemblableAffixes>b__14_3           0x02C86D7C
    + Linked read <GetDisassemblableAffixes>b__14_1           0x02C86DE0
  EssenceGrid.CreateEssences                                  0x02506C7C
  EssenceLootboxSmall.<InternalOnRequestUse>d__7.MoveNext     0x02CD44C4

Disassembly is a deterministic filter (no success chance): the output is the prefix/suffix
affixes at rarity >= 2 whose attributes include an open affix. `EssenceAffixId` is the
item-definition field mapping an essence to its affix. The lootbox is the only random piece.

Also exports the EssenceAffixId catalog (Items.json essence items -> affix names).

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/recover_essence_disassemble.py
"""
from __future__ import annotations

import hashlib
import json
import pathlib

import disasm_powers as native

BASELINE = "Android 1.9.3 (507033)"
BINARY_SHA256 = "529bd257af0cd6e953fb50f51a69857f42f04eb70acab0586f8413d1f97afd1d"

SPANS = {
    "get_disassemblable_affixes": (0x02C82770, 0x02C82940),
    "predicate_prefix_suffix_rarity2": (0x02C86C40, 0x02C86D7C),
    "attribute_is_open_affix": (0x02C86D7C, 0x02C86DE0),
    "linked_attribute_read": (0x02C86DE0, 0x02C86E40),
    "create_essences": (0x02506C7C, 0x02506EA0),
    "essence_lootbox_use": (0x02CD44C4, 0x02CD4A00),
}

# Constant-pool doubles referenced by the disassemble/essence code.
CONSTANT_ADDRESSES = [0x13872D0, 0x1387B90]


def main() -> None:
    data = native.SO.read_bytes()
    digest = hashlib.sha256(data).hexdigest()
    if digest != BINARY_SHA256:
        raise ValueError("Unreviewed native binary; review addresses and formula before exporting")
    segments = native.load_segments(data)

    evidence = {}
    for name, (start, end) in SPANS.items():
        off = native.va_to_off(segments, start)
        raw = data[off:off + end - start]
        evidence[name] = {
            "start": hex(start),
            "endExclusive": hex(end),
            "sha256": hashlib.sha256(raw).hexdigest(),
            "instructions": [f"{i.address:#x} {i.mnemonic} {i.op_str}".rstrip()
                             for i in native.disassemble(data, segments, start, end - start)],
        }

    # Assert the reviewed instructions are present (guards against a swapped build).
    asserts = {
        # The predicate's rarity gate: `x.Rarity >= 2` (SharedNet Rarity).
        "predicate_prefix_suffix_rarity2": ["cmp w8, #2"],
    }
    for span, needles in asserts.items():
        blob = "\n".join(evidence[span]["instructions"])
        for needle in needles:
            assert needle in blob, f"missing {needle!r} in {span}"

    meta, _ = native.method_map()
    names = {int(m["virtualAddress"], 16): m["name"] for m in meta["methodDefinitions"]}
    for span, (start, _) in SPANS.items():
        assert start in names, f"no method at {hex(start)} for {span}"

    constants = {hex(a): native.read_const(data, segments, a, 8) for a in CONSTANT_ADDRESSES}

    # EssenceAffixId catalog: essence items -> their affix definition name.
    gamedata = native.ROOT / "gamedata_decrypted"
    affix_names = {a["Id"]: a.get("Name") for a in json.loads((gamedata / "Affixes.json").read_text())}
    essences = []
    unresolved = set()
    for entry in json.loads((gamedata / "Items.json").read_text()):
        sd = entry.get("SerializedData")
        if isinstance(sd, str):
            sd = json.loads(sd)
        sd = sd or {}
        guid = sd.get("EssenceAffixId")
        if not guid:
            continue
        name = affix_names.get(guid)
        if name is None:
            unresolved.add(guid)
        essences.append({
            "integerId": entry.get("IntegerId"),
            "name": sd.get("Name"),
            "essenceAffixId": guid,
            "affixName": name,
        })
    essences.sort(key=lambda e: e["integerId"] or 0)

    out = native.ROOT / "tools/web-content/generated/essence_recovery.json"
    out.write_text(json.dumps({
        "baseline": BASELINE,
        "binarySha256": digest,
        "method": "Game.Items.CraftingUtils.GetDisassemblableAffixes",
        "address": "0x02C82770",
        "predicate": "affix != null && affix.IsPrefixOrSuffix && affix.Rarity >= 2 && "
                     "affix.AffixDefinition != null && attributes include an open affix (IsOpenAffix)",
        "disassembleChance": "deterministic (no success chance)",
        "linkedAttribute": "GameAttributeMap.Linked (offset 0x2C8) carries the affix's granted item",
        "lootbox": "EssenceLootboxSmall.InternalOnRequestUse is the only random step "
                   "(Max_Num_Essence_Added_Affixes_On_Item 198)",
        "constants": constants,
        "evidence": evidence,
        "essenceAffixCatalog": essences,
        "unresolvedAffixGuids": sorted(unresolved),
    }, ensure_ascii=False, indent=1) + "\n")

    # Server-embedded catalog (essence integer id -> affix name).
    catalog = {str(e["integerId"]): {"name": e["name"], "affix": e["affixName"],
                                     "essenceAffixId": e["essenceAffixId"]} for e in essences}
    embedded = native.ROOT / "server/Nordicandia.Server/GameData/essence_affix_catalog.json"
    embedded.write_text(json.dumps(catalog, ensure_ascii=False, indent=1, sort_keys=True) + "\n")

    print(f"wrote {out.relative_to(native.ROOT)}: {len(essences)} essences, "
          f"{len(unresolved)} unresolved affix guids")
    print(f"wrote {embedded.relative_to(native.ROOT)}")


if __name__ == "__main__":
    main()
