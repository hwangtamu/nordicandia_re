#!/usr/bin/env python3
"""Disassemble Android libil2cpp.so power methods and recover the numeric constants
that the client assigns to skill fields (weapon-damage multiplier, cooldown, radius,
duration, mana, jump count, ...).

Inputs (not committed; regenerate from the APK / existing dumps):
  tmp/apk-libil2cpp.so                 Android arm64 native module
  tmp/android-metadata.json            Il2CppInspector address map for that module
  steam_analysis/dump.cs               type dump with field offsets (names)

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/disasm_powers.py Slam ChainLightning Might ...
  .tools/web-assets-venv/bin/python tools/web-content/disasm_powers.py --all
"""
from __future__ import annotations

import json
import pathlib
import re
import struct
import sys

from capstone import Cs, CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN

ROOT = pathlib.Path(__file__).resolve().parents[2]
SO = ROOT / "tmp/apk-libil2cpp.so"
META = ROOT / "tmp/android-metadata.json"
DUMP = ROOT / "steam_analysis/dump.cs"


def load_segments(data: bytes):
    e_phoff = struct.unpack_from("<Q", data, 0x20)[0]
    e_phentsize = struct.unpack_from("<H", data, 0x36)[0]
    e_phnum = struct.unpack_from("<H", data, 0x38)[0]
    segs = []
    for i in range(e_phnum):
        off = e_phoff + i * e_phentsize
        p_type, p_flags, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_align = struct.unpack_from("<IIQQQQQQ", data, off)
        if p_type == 1:
            segs.append((p_vaddr, p_offset, p_filesz))
    return segs


def va_to_off(segs, va):
    for vaddr, off, size in segs:
        if vaddr <= va < vaddr + size:
            return off + (va - vaddr)
    return None


def field_offsets_by_class():
    """class name -> {field_offset: field_name} from dump.cs."""
    text = DUMP.read_text(errors="ignore")
    lines = text.splitlines()
    result: dict[str, dict[int, str]] = {}
    current = None
    for line in lines:
        m = re.match(r"\t(?:public|internal|private|protected)?\s*(?:sealed\s+|abstract\s+|static\s+)?class\s+([\w<>]+)", line)
        if m:
            current = m.group(1)
            result.setdefault(current, {})
        if current:
            fm = re.search(r"(?:private|public|protected|internal)\s+[\w<>\[\],\. \?]+\s+(_?\w+);\s*//\s*(0x[0-9A-Fa-f]+)", line)
            if fm:
                result[current][int(fm.group(2), 16)] = fm.group(1)
    return result


def load_game_attributes() -> dict[int, str]:
    """Offset -> attribute name from the GameAttributes class in dump.cs."""
    lines = DUMP.read_text(errors="ignore").splitlines()
    start = None
    for i, line in enumerate(lines):
        if re.search(r"\bclass GameAttributes\b", line):
            start = i
            break
    if start is None:
        return {}
    result: dict[int, str] = {}
    i = start + 1
    while i < len(lines):
        if re.match(r"\t}", lines[i]):
            break
        mm = re.search(r"GameAttribute[ID]?\s+_?([\w]+)_k__BackingField;\s*//\s*(0x[0-9A-Fa-f]+)", lines[i])
        if mm:
            result[int(mm.group(2), 16)] = mm.group(1)
        i += 1
    return result


def method_map():
    meta = json.loads(META.read_text())["addressMap"]
    by_name = {}
    for m in meta["methodDefinitions"]:
        by_name.setdefault(m["name"], m["virtualAddress"])
    return meta, by_name


def find_method_address(meta, class_name: str, method: str = "InternalInitializePowerParameters") -> int | None:
    # Names look like _ZN4Game5Slam33InternalInitializePowerParametersEi or with a namespace.
    needle = method
    for m in meta["methodDefinitions"]:
        name = m.get("name", "")
        if needle in name and re.search(r"\d" + re.escape(class_name) + r"\d+", name):
            return int(m["virtualAddress"], 16)
    for m in meta["methodDefinitions"]:
        name = m.get("name", "")
        if needle in name and class_name in name:
            return int(m["virtualAddress"], 16)
    return None


def read_const(data, segs, addr, size):
    off = va_to_off(segs, addr)
    if off is None or off + size > len(data):
        return None
    raw = data[off:off + size]
    if size == 8:
        return struct.unpack("<d", raw)[0]
    if size == 4:
        return struct.unpack("<f", raw)[0]
    return None


def disassemble(data, segs, start, length):
    off = va_to_off(segs, start)
    code = data[off:off + length]
    md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
    return list(md.disasm(code, start))


def recover(data, segs, start, limit, set_item_addr):
    """Disassemble [start, limit) and recover the attribute values a skill sets.

    The client calls GameAttributeMap.set_Item(AttributeOrigin, GameAttribute, double):
        x0 = map, w1 = origin, x2 = GameAttribute*, d0 = value
    The GameAttribute pointer is loaded from a static field whose offset matches the
    GameAttributes class table (from dump.cs), so each value can be named.
    Returns list of (origin, field_offset, value) plus the raw instruction count.
    """
    insns = disassemble(data, segs, start, limit - start)
    bases: dict[str, int] = {}
    dvals: dict[str, float] = {}
    wvals: dict[str, int] = {}
    pending_attr: int | None = None
    origin = 0
    records: list[tuple[int, int, float]] = []
    direct: dict[int, float] = {}
    last_loaded: float | None = None
    for insn in insns:
        m, ops = insn.mnemonic, insn.op_str
        if m == "adrp":
            mm = re.match(r"(\w+), #(0x[0-9a-fA-F]+)", ops)
            if mm:
                bases[mm.group(1)] = int(mm.group(2), 16)
        elif m == "add":
            mm = re.match(r"(\w+), (\w+), #(0x[0-9a-fA-F]+)", ops)
            if mm:
                bases[mm.group(1)] = bases.get(mm.group(2), 0) + int(mm.group(3), 16)
        elif m == "fmov":
            mm = re.match(r"([ds]\d+), #(-?[\d.eE+]+)", ops)
            if mm:
                dvals[mm.group(1)] = float(mm.group(2))
                last_loaded = float(mm.group(2))
        elif m.startswith("ldr"):
            mm = re.match(r"([dswx]\d+), \[(\w+)(?:, #(0x[0-9a-fA-F]+))?\]", ops)
            if mm:
                reg, base, disp = mm.group(1), mm.group(2), int(mm.group(3), 16) if mm.group(3) else 0
                if reg == "x2":
                    pending_attr = disp
                elif reg.startswith("d"):
                    v = read_const(data, segs, bases.get(base, 0) + disp, 8)
                    if v is not None:
                        dvals[reg] = v
                        last_loaded = v
        elif m.startswith("mov"):
            mm = re.match(r"(w\d+), #(0x[0-9a-fA-F]+)", ops)
            if mm:
                wvals[mm.group(1)] = int(mm.group(2), 16)
                if mm.group(1) == "w1":
                    origin = int(mm.group(2), 16)
        elif m.startswith("str"):
            mm = re.match(r"(d\d+), \[(x19|x20)(?:, #(0x[0-9a-fA-F]+))?\]", ops)
            if mm:
                disp = int(mm.group(3), 16) if mm.group(3) else 0
                if mm.group(1) in dvals:
                    direct[disp] = dvals[mm.group(1)]
                elif last_loaded is not None:
                    # Rank-1 base of a (rank-1)*perRank + base formula.
                    direct[disp] = last_loaded
        elif m == "bl" and ops.startswith("#"):
            target = int(ops[1:], 16)
            if target == set_item_addr and pending_attr is not None and "d0" in dvals:
                records.append((origin, pending_attr, dvals["d0"]))
                pending_attr = None
    return records, direct, insns


def main() -> int:
    if not SO.exists() or not META.exists():
        print("missing tmp/apk-libil2cpp.so or tmp/android-metadata.json", file=sys.stderr)
        return 1
    data = SO.read_bytes()
    segs = load_segments(data)
    meta, _ = method_map()
    attr_names = load_game_attributes()
    print(f"GameAttributes offsets: {len(attr_names)}")

    powers_full = json.loads((ROOT / "tools/web-content/generated/powers_full.json").read_text())
    # Resolve the set_Item address from the metadata method names.
    set_item_addr = None
    for m in meta["methodDefinitions"]:
        name = m.get("name", "")
        # The double overload mangles as "...GameAttributeDEd".
        if "GameAttributeMap8set_ItemE" in name and name.endswith("d"):
            set_item_addr = int(m["virtualAddress"], 16)
            break
    if set_item_addr is None:
        print("could not resolve GameAttributeMap.set_Item", file=sys.stderr)
        return 1

    targets = sys.argv[1:]
    if not targets or targets == ["--all"]:
        targets = [p["implementedBy"] for p in powers_full["powers"].values() if p.get("implementedBy")]

    all_addrs = sorted(int(m["virtualAddress"], 16) for m in meta["methodDefinitions"])
    import bisect
    result: dict[str, dict] = {}
    for name in targets:
        start = find_method_address(meta, name)
        if start is None:
            continue
        idx = bisect.bisect_right(all_addrs, start)
        limit = all_addrs[idx] if idx < len(all_addrs) else start + 0x400
        if limit - start > 0x1000 or limit <= start:
            limit = start + 0x1000
        records, direct, insns = recover(data, segs, start, limit, set_item_addr)
        entry = {}
        for origin, offset, value in records:
            fname = attr_names.get(offset, f"off_{offset:#x}")
            entry[fname] = value
        for offset, value in direct.items():
            entry[f"field_{offset:#x}"] = value
        result[name] = entry
        print(f"== {name} @ {hex(start)} ({len(insns)} insns)")
        if not entry:
            print("   (no set_Item attributes recovered)")
        for k, v in entry.items():
            print(f"   {k} = {v}")
    out = ROOT / "tools/web-content/generated/power_values.json"
    out.write_text(json.dumps(result, indent=2))
    print(f"wrote {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
