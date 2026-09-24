#!/usr/bin/env python3
"""Wire the Android client's leaderboard window to the private server.

The shipped 1.9.3 Android build renders a locally synthesised board
(Nordicandia.Client.Offline.OfflineFakeLeaderboard) and never calls the
MagicOnion leaderboard API, so characters that only exist server side (e.g.
Steam characters) can never show up. This patch replaces the three
OfflineFakeLeaderboard.Build* entry points with a stub that fetches the real
cross-platform standings from the server's plain-HTTP JSON feed and returns
them as System.FakeLeaderboardRow[].

Applies the standard online patches first, then:

  * writes device/stub/leaderboard_stub.bin at 0x3451000 (inside the unused
    BestHTTP Examples demo code region, 0x344EBFC..0x345C9AC),
  * patches 0x02E3FABC / 0x02E40264 / 0x02E404F0 with a plain B to the stub
    entry points (lb_overall / lb_class / lb_helheim).
"""
import argparse
import hashlib
import struct
from pathlib import Path

from patch_android_online import patch as patch_online

STUB_VA = 0x3451000
LB_OVERALL = STUB_VA            # build_rows("overall", 0)
LB_CLASS = 0x3451D78            # build_rows("class", classId)
LB_HELHEIM = 0x3451DA0          # build_rows("helheim", 0)

BUILD_OVERALL = 0x02E3FABC
BUILD_CLASS = 0x02E40264
BUILD_HELHEIM = 0x02E404F0


def _segments(data):
    phoff = struct.unpack_from("<Q", data, 32)[0]
    entsize, count = struct.unpack_from("<HH", data, 54)
    segs = []
    for i in range(count):
        typ, flags, off, va, _, size, _, _ = struct.unpack_from("<IIQQQQQQ", data, phoff + i * entsize)
        if typ == 1:
            segs.append((va, off, size))
    return segs


def _off_for(segs, va):
    for start, off, size in segs:
        if start <= va < start + size:
            return off + va - start
    raise ValueError(f"{va:#x} not in any segment")


def _b(frm, to):
    """Plain branch (B): must not touch x30, the patched function keeps its caller's LR."""
    return struct.pack("<I", 0x14000000 | (((to - frm) // 4) & 0x3FFFFFF))


def build(src: Path, out: Path, stub_bin: Path):
    data = patch_online(src.read_bytes())
    result = bytearray(data)
    segs = _segments(bytes(data))

    blob = stub_bin.read_bytes()
    assert len(blob) <= 0x345C9AC - STUB_VA, "stub does not fit the BestHTTP Examples cave"
    o = _off_for(segs, STUB_VA)
    result[o:o + len(blob)] = blob
    print(f"injected {len(blob)} bytes at {STUB_VA:#x}")

    for name, site, dest in (
        ("BuildOverallBoard", BUILD_OVERALL, LB_OVERALL),
        ("BuildClassBoard", BUILD_CLASS, LB_CLASS),
        ("BuildHelheimBoard", BUILD_HELHEIM, LB_HELHEIM),
    ):
        o = _off_for(segs, site)
        result[o:o + 4] = _b(site, dest)
        print(f"{name:20s} {site:#010x} -> b {dest:#x}")

    out.write_bytes(bytes(result))
    print(f"wrote {out} sha256={hashlib.sha256(result).hexdigest()}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src", type=Path)
    ap.add_argument("out", type=Path)
    ap.add_argument("--stub", type=Path,
                    default=Path(__file__).resolve().parent.parent / "device/stub/leaderboard_stub.bin")
    args = ap.parse_args()
    build(args.src, args.out, args.stub)


if __name__ == "__main__":
    main()
