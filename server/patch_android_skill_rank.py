#!/usr/bin/env python3
"""Sync client-side skill ranks to the server on the shipped Android 1.9.3 build.

Apply *after* patch_android_online.py + patch_android_email_login.py +
patch_android_leaderboard.py + patch_android_realtime.py.

Why a stub: the shipped client metadata has no UpgradeCharacterSkillRankRequest/
Response and no ICharacterServiceApi.UpgradeCharacterSkillRank, so generated client
code cannot be extended. The stub hooks LivingPowers.RankUp, reads the new rank
with GetTrainedRank, and POSTs it to the private server's plain-HTTP endpoint
  POST /api/character/skill-rank
(same auth token as gRPC; the server does ownership + 1..200 + monotonic checks).
Transport mirrors the leaderboard stub: raw arm64 syscalls, no libc.

Report rule: only when after > before && after > 1 (0->1 is created server-side
and NewRank <= 1 is rejected).

The input is left untouched; output must be a separate file. No device deployment.
"""
import argparse
import json
import shutil
import struct
import subprocess
import tempfile
from pathlib import Path
from patch_android_email_login import _read_symbols, _segments, _off_for, _b
from patch_android_realtime import _extend_rw_memsz

STUB_VA, STUB_END = 0x3457000, 0x345C9AC
STATE_VA, STATE_END = 0x5DE8000, 0x5DE8400
RANKUP = 0x2C6C63C
RANKUP_RESUME = 0x2C6C640
ROOT = Path(__file__).resolve().parent.parent

# Fixed libil2cpp 1.9.3 arm64 VAs the stub links against.
TARGETS = {
    'get_trained_rank': 0x2C6EA0C,
    'netclient_get_current': 0x2DA1E28,
    'netclient_current_character_id': 0x2DA1E90,
    'netsession_get_current': 0x2E42DAC,
    'netsession_get_session': 0x2E42E04,
    'sessiondto_get_authtoken': 0x0279C8B8,
    'RANKUP_RESUME': RANKUP_RESUME,
}


def tool(name):
    path = shutil.which(name)
    if path:
        return path
    for directory in ('/opt/homebrew/opt/llvm/bin', '/usr/local/opt/llvm/bin'):
        path = Path(directory) / name
        if path.exists():
            return str(path)
    raise RuntimeError(f'{name} is required to build the skill-rank stub')


def build_stub(source, directory):
    syms = _read_symbols(source)
    targets = dict(TARGETS)
    targets.update({k: v for k, v in syms.items() if k.startswith('il2cpp_')})
    stem = directory / 'skill_rank_stub'
    subprocess.run([tool('clang'), '-target', 'aarch64-linux-gnu', '-O2', '-ffreestanding',
                    '-fno-stack-protector', '-fno-pic', '-mno-outline-atomics',
                    '-c', str(ROOT / 'device/stub/skill_rank_stub.c'),
                    '-o', str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('ld.lld'), '-T', str(ROOT / 'device/stub/skill_rank_stub.ld'),
                    *[f'--defsym={k}={v:#x}' for k, v in targets.items()],
                    '-o', str(stem.with_suffix('.elf')), str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('llvm-objcopy'), '-O', 'binary', '--only-section=.text',
                    '--only-section=.rodata', str(stem.with_suffix('.elf')),
                    str(stem.with_suffix('.bin'))], check=True)
    return stem.with_suffix('.bin').read_bytes(), _read_symbols(stem.with_suffix('.elf'))


def apply(data, blob, syms):
    result = bytearray(data)
    segs = _segments(data)
    if len(blob) > STUB_END - STUB_VA:
        raise ValueError('skill-rank stub exceeds its reserved code cave')
    for name in ('rankup_trampoline', 'rankup_body_impl'):
        if not STUB_VA <= syms[name] < STUB_VA + len(blob):
            raise ValueError(f'{name} lies outside the compiled stub')
    if not STATE_VA <= syms['g_token'] < STATE_END:
        raise ValueError('skill-rank state is outside the reserved writable space')
    off = _off_for(segs, RANKUP)
    if data[off:off + 4] != bytes.fromhex('ff4302d1'):   # sub sp, sp, #0x90
        raise ValueError(f'unexpected LivingPowers.RankUp prologue at {RANKUP:#x}')
    result[off:off + 4] = _b(RANKUP, syms['rankup_trampoline'])
    _extend_rw_memsz(result, STATE_VA, STATE_END)
    off = _off_for(segs, STUB_VA)
    result[off:off + len(blob)] = blob
    return bytes(result)


def build(source, output, artifacts=None):
    source, output = Path(source), Path(output)
    if source.resolve() == output.resolve():
        raise ValueError('Use a separate output path')
    with tempfile.TemporaryDirectory(prefix='nord-skillrank-') as work:
        directory = Path(work)
        blob, syms = build_stub(source, directory)
        patched = apply(source.read_bytes(), blob, syms)
        output.write_bytes(patched)
        if artifacts:
            artifacts = Path(artifacts); artifacts.mkdir(parents=True, exist_ok=True)
            for item in directory.iterdir():
                shutil.copy2(item, artifacts / item.name)
        print(f'Injected skill-rank sync stub ({len(blob)} bytes) at {STUB_VA:#x}; '
              f'RankUp {RANKUP:#x} -> b {syms["rankup_trampoline"]:#x}')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('source', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--artifacts', type=Path)
    args = p.parse_args()
    build(args.source, args.output, args.artifacts)