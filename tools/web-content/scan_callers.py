#!/usr/bin/env python3
"""Find call sites of a method in the Android libil2cpp.so.

Uses the IL2CPP address map for method names and a vectorised scan of ARM64 `bl`
instructions. Handy for locating the (often inlined-around) native functions behind
a mechanic, e.g. which callers compute the armour->damage-reduction fraction.

Usage:
  .tools/web-assets-venv/bin/python tools/web-content/scan_callers.py ApplyDamageReduction
  .tools/web-assets-venv/bin/python tools/web-content/scan_callers.py --addr 0x02BAF34C
  .tools/web-assets-venv/bin/python tools/web-content/scan_callers.py --file 0x02A73020
"""
from __future__ import annotations

import argparse
import bisect
import json
import pathlib
import struct

import numpy as np

ROOT = pathlib.Path(__file__).resolve().parents[2]
SO = ROOT / "tmp/apk-libil2cpp.so"
META = ROOT / "tmp/android-metadata.json"


def load_segments(data: bytes):
    e_phoff = struct.unpack_from("<Q", data, 0x20)[0]
    e_phnum = struct.unpack_from("<H", data, 0x38)[0]
    e_phentsize = struct.unpack_from("<H", data, 0x36)[0]
    segs = []
    for i in range(e_phnum):
        off = e_phoff + i * e_phentsize
        p_type, p_flags, p_offset, p_vaddr, p_paddr, p_filesz, p_memsz, p_align = struct.unpack_from("<IIQQQQQQ", data, off)
        if p_type == 1:
            segs.append((p_offset, p_vaddr, p_filesz))
    return segs


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("name", nargs="?", help="substring of the target method name")
    parser.add_argument("--addr", help="explicit target address, e.g. 0x02BAF34C")
    parser.add_argument("--file", help="pretty-print a function body instead of scanning")
    args = parser.parse_args()

    data = SO.read_bytes()
    segs = load_segments(data)
    meta = json.loads(META.read_text())["addressMap"]
    methods = [(int(m["virtualAddress"], 16), m["name"]) for m in meta["methodDefinitions"]]
    starts = sorted(methods)
    keys = [s[0] for s in starts]

    def containing(va: int) -> str:
        i = bisect.bisect_right(keys, va) - 1
        return starts[i][1] if i >= 0 else "?"

    if args.file:
        from capstone import Cs, CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN

        target = int(args.file, 16)
        i = bisect.bisect_right(keys, target)
        limit = starts[i][0] if i < len(starts) else target + 0x400
        off = next((o + (target - v) for o, v, s in segs if v <= target < v + s), None)
        if off is None:
            print("address not in any segment")
            return 1
        md = Cs(CS_ARCH_ARM64, CS_MODE_LITTLE_ENDIAN)
        for insn in md.disasm(data[off:off + max(0x40, limit - target)], target):
            print(f"{insn.address:#x} {insn.mnemonic} {insn.op_str}")
        return 0

    if args.addr:
        want = int(args.addr, 16)
    elif args.name:
        match = next(((a, n) for a, n in methods if args.name in n), None)
        if match is None:
            print(f"no method matching {args.name!r}")
            return 1
        want = match[0]
        print(f"target: {match[1]} @ {want:#x}")
    else:
        parser.error("provide a name or --addr")
        return 2

    words = np.frombuffer(data, dtype="<u4")
    idx = np.nonzero((words >> 26) == 0x25)[0]
    imm = (words[idx] & 0x03FFFFFF).astype(np.int64)
    imm = np.where(imm >= 2**25, imm - 2**26, imm)
    # Map each instruction's file offset to its virtual address (vectorised over PT_LOADs).
    offs = idx.astype(np.int64) * 4
    va = np.full(offs.shape, -1, dtype=np.int64)
    for po, pv, fs in segs:
        in_seg = (offs >= po) & (offs < po + fs)
        va[in_seg] = offs[in_seg] - po + pv
    target = va + imm * 4
    hits = idx[target == want]
    print(f"{len(hits)} call site(s)")
    seen = set()
    for h in hits:
        off = int(h) * 4
        site = next((off - po + pv for po, pv, fs in segs if po <= off < po + fs), -1)
        name = containing(site)
        if name in seen:
            continue
        seen.add(name)
        print(f"  {site:#x} in {name}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
