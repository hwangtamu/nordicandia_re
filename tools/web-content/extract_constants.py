#!/usr/bin/env python3
"""Extract the client's `Game.Constants` values used by the attribute formulas.

`Game.Constants` getters (mangled `_ZN4Game14GameAttributes9Constants...`) are tiny
functions that return a literal, either from the ARM64 literal pool (`adrp` + `ldr d0`)
or via `fmov`/`mov`. This reads those literals so `Constants.X` in the attribute
formulas resolves (e.g. `Armor_Per_Constitution_Factor = 0.03`).

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/extract_constants.py
Output:
  tools/web-content/generated/constants.json
"""
from __future__ import annotations

import json
import pathlib
import re
import struct
import sys

from capstone import CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN, Cs

ROOT = pathlib.Path(__file__).resolve().parents[2]
SO = ROOT / "tmp/apk-libil2cpp.so"
META = ROOT / "tmp/android-metadata.json"
OUT = ROOT / "tools/web-content/generated/constants.json"


def main() -> int:
    if not SO.exists() or not META.exists():
        print(f"missing {SO} or {META}", file=sys.stderr)
        return 1
    data = SO.read_bytes()

    e_phoff = struct.unpack_from("<Q", data, 0x20)[0]
    e_phnum = struct.unpack_from("<H", data, 0x38)[0]
    e_phentsize = struct.unpack_from("<H", data, 0x36)[0]
    segs = []
    for i in range(e_phnum):
        o = e_phoff + i * e_phentsize
        t, _, po, pv, _, fs, _, _ = struct.unpack_from("<IIQQQQQQ", data, o)
        if t == 1:
            segs.append((pv, po, fs))

    def off(v: int) -> int:
        for va, o, s in segs:
            if va <= v < va + s:
                return o + (v - va)
        raise ValueError(f"unmapped address {v:#x}")

    def read_double(va: int) -> float:
        return struct.unpack("<d", data[off(va):off(va) + 8])[0]

    md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
    out: dict[str, float] = {}
    for m in json.loads(META.read_text())["addressMap"]["methodDefinitions"]:
        name = m["name"]
        if "GameAttributes9Constants" not in name or "get_" not in name:
            continue
        rest = name.split("get_", 1)[1]
        prop = rest[:-2] if rest.endswith("Ev") else rest
        addr = int(m["virtualAddress"], 16)
        base = None
        value = None
        for insn in md.disasm(data[off(addr):off(addr) + 0x30], addr):
            if insn.mnemonic == "adrp":
                mm = re.search(r", #(0x[0-9a-f]+)", insn.op_str)
                if mm:
                    base = int(mm.group(1), 16)
            elif insn.mnemonic == "ldr" and base is not None:
                mm = re.search(r"\[x8, #(0x[0-9a-f]+)\]", insn.op_str)
                if mm:
                    value = read_double(base + int(mm.group(1), 16))
            elif insn.mnemonic == "fmov" and insn.op_str.startswith("d0, #"):
                value = float(insn.op_str.split("#")[1])
            elif insn.mnemonic == "mov" and insn.op_str.startswith("x8, #"):
                mm = re.search(r"#(0x[0-9a-f]+)", insn.op_str)
                if mm:
                    value = struct.unpack("<d", struct.pack("<Q", int(mm.group(1), 16)))[0]
        if value is not None:
            out[prop] = value

    OUT.write_text(json.dumps(out, indent=2))
    print(f"wrote {OUT}: {len(out)} constants")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
