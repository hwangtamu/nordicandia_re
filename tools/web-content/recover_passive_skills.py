#!/usr/bin/env python3
"""Build a version-bound evidence inventory for every passive skill in the class pools.

This is a first-pass extraction index, not an automatic claim that each passive's
behavior is understood. It joins the shipped Power pools, known parameter recovery,
IL2CPP native method metadata, Cpp2IL method listings, and dump.cs field/attribute
names. Raw method operations are retained so each behavioral interpretation can be
reviewed against the disassembly.

Run with:
  .tools/web-assets-venv/bin/python tools/web-content/recover_passive_skills.py
"""
from __future__ import annotations

import bisect
import hashlib
import json
import re
from pathlib import Path

import disasm_powers as native

ROOT = native.ROOT
GEN = ROOT / "tools/web-content/generated"
ANNOTATED = ROOT / "tmp/cpp2il-annotated-all/Game/Skills"
OUTPUT = GEN / "passive_skill_recovery.json"
REPORT = ROOT / "docs/web/PASSIVE_SKILL_RECOVERY.md"


def method_sections(text: str) -> list[tuple[str, str]]:
    matches = list(re.finditer(r"^Method: (.+)$", text, re.M))
    return [(m.group(1).strip(), text[m.end():matches[i + 1].start() if i + 1 < len(matches) else len(text)])
            for i, m in enumerate(matches)]


def section_for(sections: list[tuple[str, str]], signature: str) -> str | None:
    for name, body in sections:
        if signature in name:
            return body
    return None


def native_method(meta: dict, name: str, method: str) -> int | None:
    # IL2CPP symbols encode class and method-name lengths, e.g.
    # _ZN4Game6Skills8Overkill5ApplyEv.
    suffix = f"{len(name)}{name}{len(method)}{method}"
    terminator = "Ei" if method == "InternalInitializePowerParameters" else "Ev"
    candidates = [m for m in meta["methodDefinitions"]
                  if m.get("name", "").endswith(suffix + terminator)]
    return int(candidates[0]["virtualAddress"], 16) if len(candidates) == 1 else None


def native_method_digest(data: bytes, segments: list, addresses: list[int], address: int | None) -> str | None:
    if address is None:
        return None
    pos = bisect.bisect_right(addresses, address)
    if pos >= len(addresses):
        return None
    offset = native.va_to_off(segments, address)
    if offset is None:
        return None
    end = min(addresses[pos] - address, len(data) - offset)
    return hashlib.sha256(data[offset:offset + end]).hexdigest()


def clean_attribute_name(field_name: str) -> str:
    return field_name.removeprefix("_").removesuffix("_k__BackingField")


def source_method_evidence(name: str, signature: str, body: str, meta: dict,
                           data: bytes, segments: list, addresses: list[int]) -> dict:
    method_name = signature.split("(", 1)[0].rsplit(" ", 1)[-1]
    address = native_method(meta, name, method_name)
    disassembly = re.findall(r"^\s*(0x[0-9a-fA-F]+)\s+(.+)$", body, re.M)
    return {
        "signature": signature,
        "nativeAddress": hex(address) if address is not None else None,
        "nativeMethodSha256": native_method_digest(data, segments, addresses, address),
        "annotatedStartAddress": disassembly[0][0] if disassembly else None,
        "annotationMatchesNativeAddress": bool(disassembly and address is not None
                                                and int(disassembly[0][0], 16) == address),
    }


def extract_assignments(body: str) -> list[str]:
    """Keep field stores and calculations that can be reviewed without losing raw ISIL."""
    isil = body.split("ISIL:", 1)[1] if "ISIL:" in body else ""
    patterns = ("this._", "this.field_", "Move this.", "Call Math.",
                "GameAttributeMap.set_Item", "PowerContext.get_Attributes")
    return [line.strip() for line in isil.splitlines()
            if any(p in line for p in patterns)]


def extract_native_this_stores(body: str) -> list[dict]:
    """Return instance-field stores only after proving the base register aliases this."""
    disassembly = body.split("ISIL:", 1)[0]
    this_registers = set(re.findall(r"\bMOV\s+X(19|20), X0\b", disassembly, re.I))
    stores = []
    pattern = re.compile(r"\bSTR\s+([DWXVSQ])\d+, \[X(19|20) \+ (0x[0-9a-fA-F]+)\]", re.I)
    for line in disassembly.splitlines():
        match = pattern.search(line)
        if not match or match.group(2) not in this_registers:
            continue
        kind = match.group(1).upper()
        width = 16 if kind in ("V", "Q") else 8 if kind in ("D", "X") else 4
        start = int(match.group(3), 16)
        components = [hex(start + 8 * i) for i in range(width // 8)] or [hex(start)]
        stores.append({"nativeOffset": hex(start), "width": width,
                       "components": components, "raw": line.strip()})
    return stores


def extract_native_field_assignments(body: str, parameter_recovery: dict) -> list[dict]:
    stores = extract_native_this_stores(body)
    offsets = [component for store in stores for component in store["components"]]
    isil = body.split("ISIL:", 1)[-1]
    assignments = re.findall(r"Move this\.(\w+) \(System\.[^)]+\),", isil)
    recovered_fields = list(parameter_recovery.get("fields", {}).keys())
    # Audited vector formulas have precise lane names in the existing recovery table;
    # otherwise align only the source-ordered intersection and leave excess values unknown.
    names = recovered_fields if recovered_fields and len(recovered_fields) == len(offsets) else assignments
    paired = [{"name": name, "nativeOffset": offset,
               "mappingConfidence": "source-order Cpp2IL assignment paired with native store to this"}
              for name, offset in zip(names, offsets)]
    paired.extend({"name": name, "nativeOffset": None, "mappingConfidence": "unpaired"}
                  for name in names[len(offsets):])
    paired.extend({"name": None, "nativeOffset": offset, "mappingConfidence": "unpaired-native-store"}
                  for offset in offsets[len(names):])
    return paired


def extract_rank_formulas(body: str, data: bytes, segments: list) -> dict[str, dict]:
    """Symbolically recover straightforward affine rank fields from Cpp2IL ISIL.

    This deliberately supports only auditable affine arithmetic and Min/Max caps;
    unsupported expressions remain absent instead of being guessed.
    """
    isil = body.split("ISIL:", 1)[1] if "ISIL:" in body else ""
    registers: dict[str, dict | None] = {}
    fields: dict[str, dict] = {}

    def scalar(token: str):
        token = token.strip()
        address = re.fullmatch(r"\[([0-9a-fA-F]{6,})\]", token)
        if address:
            return native.read_const(data, segments, int(address.group(1), 16), 8)
        number = re.fullmatch(r"(-?(?:\d+\.\d*|\.\d+|\d+)(?:[eE][+-]?\d+)?)(?:d)?", token)
        if number:
            return float(number.group(1))
        return None

    def operand(token: str):
        token = token.strip()
        symbol = re.match(r"(rank|v\d+)\b", token)
        if symbol:
            name = symbol.group(1)
            if name == "rank":
                return {"rank1": 1.0, "perRank": 1.0, "expression": "rank"}
            if name in registers:
                return registers[name]
            # Cpp2IL sometimes elides SCVTF D0,W(rank-1) but retains its SIMD lane label.
            if "V0.D0" in token and any(v and v["rank1"] == 0 and v["perRank"] == 1
                                           for v in registers.values()):
                return next(v for v in registers.values()
                            if v and v["rank1"] == 0 and v["perRank"] == 1)
        value = scalar(token)
        if value is not None:
            return {"rank1": value, "perRank": 0.0, "expression": repr(value)}
        return None

    def combine(op: str, left: dict | None, right: dict | None):
        if left is None or right is None:
            return None
        if op == "Add":
            return {"rank1": left["rank1"] + right["rank1"],
                    "perRank": left["perRank"] + right["perRank"],
                    "expression": f"({left['expression']}+{right['expression']})"}
        if op == "Subtract":
            return {"rank1": left["rank1"] - right["rank1"],
                    "perRank": left["perRank"] - right["perRank"],
                    "expression": f"({left['expression']}-{right['expression']})"}
        if op == "Multiply":
            if left["perRank"] == 0:
                factor, linear = left["rank1"], right
            elif right["perRank"] == 0:
                factor, linear = right["rank1"], left
            else:
                return None
            return {"rank1": factor * linear["rank1"], "perRank": factor * linear["perRank"],
                    "expression": f"({left['expression']}*{right['expression']})"}
        if op == "Divide":
            if right["perRank"] != 0 or right["rank1"] == 0:
                return None
            return {"rank1": left["rank1"] / right["rank1"],
                    "perRank": left["perRank"] / right["rank1"],
                    "expression": f"({left['expression']}/{right['expression']})"}
        return None

    for line in isil.splitlines():
        text = line.strip()
        match = re.match(r"\d+ (Add|Subtract|Multiply|Divide) (v\d+) @ [^,]+, (.*)$", text)
        if match:
            op, destination, remainder = match.groups()
            args = [part.strip() for part in remainder.split(",")]
            if len(args) >= 2:
                registers[destination] = combine(op, operand(args[0]), operand(args[1]))
                continue
        match = re.match(r"\d+ Call Math\.(Min|Max), (v\d+) @ [^,]+, (.*)$", text)
        if match:
            operation, destination, remainder = match.groups()
            args = [operand(part) for part in remainder.split(",")]
            if len(args) >= 2 and all(arg is not None for arg in args[:2]):
                expression = combine("Min" if operation == "Min" else "Max", args[0], args[1])
                bound = args[0] if args[0]["perRank"] == 0 else args[1]
                value = args[1] if bound is args[0] else args[0]
                if expression is None and value["perRank"] != 0 and bound["perRank"] == 0:
                    expression = dict(value)
                    expression["capMin" if operation == "Max" else "capMax"] = bound["rank1"]
                    expression["expression"] = f"{operation.lower()}({value['expression']},{bound['rank1']})"
                registers[destination] = expression
            continue
        match = re.match(r"\d+ Move this\.(\w+) \(System\.[^)]+\), (.*)$", text)
        if match:
            field, source = match.groups()
            value = operand(source)
            if value is not None:
                fields[field] = value
            continue
        # Preserve the rank-minus-one scalar conversion across Cpp2IL's omitted SIMD cast.
        match = re.match(r"\d+ Subtract (v\d+) @ [^,]+, rank @ [^,]+, 1$", text)
        if match:
            registers[match.group(1)] = {"rank1": 0.0, "perRank": 1.0, "expression": "rank-1"}
    return fields


def extract_attribute_writes(body: str, attributes_by_offset: dict[int, str], ids_by_name: dict[str, int]) -> list[dict]:
    writes = []
    isil = body.split("ISIL:", 1)[1] if "ISIL:" in body else ""
    register_fields: dict[str, str] = {}
    for line in isil.splitlines():
        text = line.strip()
        source = re.search(r"this\.(\w+)", text)
        target = re.search(r"(?:Move|op_Implicit)[^,]*,?\s*(v\d+) @", text)
        if source and target:
            register_fields[target.group(1)] = source.group(1)
        if "Buff.SetAttribute" not in text and "GameAttributeMap.set_Item" not in text:
            continue
        offsets = re.findall(r"\+([0-9A-Fa-f]{2,})\]", text)
        if not offsets:
            writes.append({"attribute": None, "attributeId": None, "raw": text})
            continue
        offset = int(offsets[-1], 16)
        field_name = attributes_by_offset.get(offset)
        attribute = clean_attribute_name(field_name) if field_name else None
        source_field_match = re.search(r"this\.(\w+)", text)
        value_register = re.search(r",\s*(v\d+) @ [^,]+$", text)
        source_field = source_field_match.group(1) if source_field_match else (
            register_fields.get(value_register.group(1)) if value_register else None)
        writes.append({
            "attributeStaticOffset": hex(offset),
            "attributeField": field_name,
            "attribute": attribute,
            "attributeId": ids_by_name.get(attribute) if attribute else None,
            "sourceField": source_field,
            "raw": text,
        })
    return writes


def extract_pool() -> list[dict]:
    pools = json.loads((GEN / "power_pools.json").read_text())
    result = []
    for class_name, pool in pools.items():
        for passive in pool.get("passive", []):
            result.append({"class": class_name, **passive})
    return result


def recover() -> dict:
    if not native.SO.exists() or not native.META.exists():
        raise FileNotFoundError("passive recovery needs tmp/apk-libil2cpp.so and tmp/android-metadata.json")
    data = native.SO.read_bytes()
    segments = native.load_segments(data)
    meta, _ = native.method_map()
    addresses = sorted(set(int(m["virtualAddress"], 16) for m in meta["methodDefinitions"]))
    dump_fields = native.field_offsets_by_class()
    attributes_by_offset = dump_fields.get("GameAttributes", {})
    ids_json = json.loads((GEN / "attribute_ids.json").read_text())
    ids_by_name = {name: int(attribute_id) for attribute_id, name in ids_json.items()}
    values = json.loads((GEN / "power_values.json").read_text())
    parameter_recovery = json.loads((GEN / "power_parameter_recovery.json").read_text()).get("powers", {})
    rows = []

    for passive in extract_pool():
        name = passive["name"]
        source_path = ANNOTATED / f"{name}.txt"
        source_exists = source_path.exists()
        text = source_path.read_text(errors="replace") if source_exists else ""
        sections = method_sections(text)
        fields = dump_fields.get(name, {})

        raw_values = values.get(name, {})
        parameter_data = parameter_recovery.get(name, {})

        methods = {}
        for label, signature in (("initialize", "InternalInitializePowerParameters("),
                                 ("apply", "System.Void Apply()"),
                                 ("remove", "System.Void Remove()")):
            body = section_for(sections, signature)
            methods[label] = source_method_evidence(name, signature, body, meta, data, segments, addresses) if body else None

        initializer_body = section_for(sections, "InternalInitializePowerParameters(")
        apply_body = section_for(sections, "System.Void Apply()")
        native_field_assignments = extract_native_field_assignments(initializer_body or "", parameter_data)
        initializer_formulas = extract_rank_formulas(initializer_body or "", data, segments)
        suspicious_initializer_values = {
            field: value for field, value in initializer_formulas.items()
            if value["perRank"] == 0 and 0 < abs(value["rank1"]) < 1e-250
        }
        for field in suspicious_initializer_values:
            initializer_formulas.pop(field)
        native_fields_by_offset = {int(f["nativeOffset"], 16): f["name"]
                                   for f in native_field_assignments
                                   if f["nativeOffset"] is not None and f["name"] is not None
                                   and f["mappingConfidence"] != "unpaired"}
        resolved_values = {}
        unresolved_fields = []
        for key, value in raw_values.items():
            match = re.fullmatch(r"field_0x([0-9a-fA-F]+)", key)
            if match and int(match.group(1), 16) in native_fields_by_offset:
                resolved_values[native_fields_by_offset[int(match.group(1), 16)]] = value
            elif match:
                unresolved_fields.append(key)
                resolved_values[key] = value
            else:
                resolved_values[key] = value
        nested_files = sorted(ANNOTATED.glob(f"{name}_NestedType_*.txt"))
        nested_types = []
        trigger_hooks = []
        hook_terms = ("OnPayload", "OnMeleeSwingHit", "BuffManager_OnBuffAdded",
                      "BuffManager_OnBuffRemoved", "Brain_OnSkillStarted",
                      "Target_OnEnterWorld", "Target_OnLeaveWorld", "OnKill",
                      "OnMonsterDeath", "OnDamage", "Update(", "Stack(")
        for nested_path in nested_files:
            nested_text = nested_path.read_text(errors="replace")
            nested_sections = method_sections(nested_text)
            type_match = re.search(r"^Type: (.+)$", nested_text, re.M)
            type_name = type_match.group(1) if type_match else nested_path.stem
            method_rows = []
            for signature, body in nested_sections:
                start = re.search(r"^\s*(0x[0-9a-fA-F]+)\s+", body, re.M)
                address = int(start.group(1), 16) if start else None
                row = {"signature": signature,
                       "annotatedStartAddress": start.group(1) if start else None,
                       "nativeMethodSha256": native_method_digest(data, segments, addresses, address)}
                method_rows.append(row)
                if any(term in signature for term in hook_terms):
                    trigger_hooks.append({"owner": type_name, **row})
            nested_types.append({"type": type_name,
                                 "sourceFile": str(nested_path.relative_to(ROOT)),
                                 "methods": method_rows})

        attrs = sorted({w["attribute"] for w in extract_attribute_writes(
            apply_body or "", attributes_by_offset, ids_by_name) if w["attribute"]})
        implementation_name = passive.get("implementedBy") or name
        rows.append({
            "name": name,
            "id": passive.get("id"),
            "integerId": passive.get("integerId"),
            "class": passive["class"],
            "description": passive.get("description", ""),
            "icon": passive.get("icon", ""),
            "implementedBy": implementation_name,
            "source": str(source_path.relative_to(ROOT)) if source_exists else None,
            "sourceAvailable": source_exists,
            "dumpFields": [{"name": field_name, "dumpOffset": hex(offset)}
                            for offset, field_name in sorted(fields.items()) if offset >= 0x120],
            "nativeFieldAssignments": native_field_assignments,
            "rawPowerValueEntries": raw_values,
            "resolvedRawFieldValues": resolved_values,
            "unresolvedRawFieldNames": unresolved_fields,
            "parameterRecovery": parameter_data or None,
            "methods": methods,
            "initializerReviewLines": extract_assignments(initializer_body or ""),
            "initializerFormulas": initializer_formulas,
            "suspiciousInitializerValues": suspicious_initializer_values,
            "applyAttributeWrites": extract_attribute_writes(
                apply_body or "", attributes_by_offset, ids_by_name),
            "applyAttributes": attrs,
            "nestedTypes": nested_types,
            "triggerHooks": trigger_hooks,
            "recoveryStatus": "native_source_indexed_manual_behavior_review_pending" if source_exists
                              else "native_implementation_source_not_found",
        })

    indexed = sum(row["sourceAvailable"] for row in rows)
    result = {
        "schemaVersion": 1,
        "source": {
            "binary": str(native.SO.relative_to(ROOT)),
            "binarySha256": hashlib.sha256(data).hexdigest(),
            "metadata": str(native.META.relative_to(ROOT)),
            "fieldNames": "steam_analysis/dump.cs",
            "annotatedMethods": "tmp/cpp2il-annotated-all/Game/Skills",
        },
        "count": len(rows),
        "indexedImplementationCount": indexed,
        "fullyBehaviorRecoveredCount": 0,
        "note": "Method indexing and field/attribute name resolution are evidence extraction only. They do not claim that every trigger, condition, timing rule, or runtime effect has been interpreted or implemented.",
        "skills": rows,
    }
    return result


def write_report(result: dict) -> None:
    rows = result["skills"]
    lines = [
        "# 被动技能反汇编恢复台账（自动索引）",
        "",
        "该文件将 4 个职业池中的每个被动技能，与 Android 基线的 `InternalInitializePowerParameters`、`Apply`、`Remove` 及 Cpp2IL 子类/嵌套类方法证据关联。它是逐项人工语义恢复的输入，**方法被索引不等于效果已完全还原**。",
        "",
        f"- 被动职业池行数：{result['count']}；存在对应 IL2CPP 类源文件：{result['indexedImplementationCount']}；全行为已验收：{result['fullyBehaviorRecoveredCount']}。",
        f"- 自动恢复线性等级公式字段：{sum(len(s['initializerFormulas']) for s in rows)}；事件/时序 hook 入口：{sum(len(s['triggerHooks']) for s in rows)}（尚未接入运行时）。",
        "- 原生二进制 SHA-256：`{}`。".format(result["source"]["binarySha256"]),
        "- 逐技能的 native 方法地址/hash、字段名/偏移、参数、Buff attribute 写入、嵌套类与事件触发钩子见 `tools/web-content/generated/passive_skill_recovery.json`。",
        "",
        "| 职业 | 被动 | Native 类源 | dump.cs 字段索引（非 Android 偏移证明） | Apply 写入属性 | 参数恢复状态 |",
        "|---|---|---|---|---|---|",
    ]
    for row in rows:
        fields = ", ".join(f"{f['name']}@{f['dumpOffset']}" for f in row["dumpFields"]) or "—"
        attrs = ", ".join(row["applyAttributes"]) or "—"
        pr = row.get("parameterRecovery", {})
        status = (pr.get("status") if pr else None) or (
            f"{len(row['initializerFormulas'])} affine field formulas" if row["initializerFormulas"]
            else "initializers not classified")
        source = "✓" if row["sourceAvailable"] else "missing"
        lines.append(f"| {row['class']} | {row['name']} | {source} | {fields} | {attrs} | {status} |")
    lines += [
        "",
        "## 解释边界",
        "",
        "- `resolvedRawFieldValues` 仅将旧抽取器的 raw offset 与已证明的 Android `this` 写入/Cpp2IL 字段赋值配对；值仍标作 raw store，不假称 rank-1 最终参数。真正的 rank 曲线见 `initializerFormulas` 或版本门控 `parameterRecovery`。",
        "- 极小次正规数（当前 EvasiveManeuver 的 cooldown 字段）留在 `suspiciousInitializerValues`，未当作已恢复参数。桌面 `dump.cs` 的字段偏移不单独作为 Android 字段映射证据。",
        "- `applyAttributes` 由 `Buff.SetAttribute`/`GameAttributeMap.set_Item` 的静态 GameAttribute 偏移映射得到；继承效果、Buff 事件处理、动态目标、概率时序及 Remove 对称性仍需人工审阅原生指令与完整 ISIL。",
        "- `triggerHooks` 提示需要继续追踪的 OnPayload、OnMeleeSwingHit、BuffManager、Brain 和 world 事件；仅索引钩子入口，不代表其条件/时间线已经复刻。",
        "- `parameterRecovery` 仅引用已有的版本门控恢复结果；未恢复的参数保留 raw 方法证据，不填猜测值。",
        "- DaggerSpecialization 若没有类方法源，会明确记录为缺失，需检查版本内容、父类继承或未命名 IL2CPP 类型，不能把缺少的方法当作无效果。",
        "",
        "## 复现",
        "",
        "```bash",
        ".tools/web-assets-venv/bin/python tools/web-content/recover_passive_skills.py",
        "```",
        "",
    ]
    REPORT.write_text("\n".join(lines), encoding="utf-8")


def main() -> None:
    result = recover()
    OUTPUT.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    write_report(result)
    print(f"passives={result['count']} indexed={result['indexedImplementationCount']} -> {OUTPUT}")


if __name__ == "__main__":
    main()
