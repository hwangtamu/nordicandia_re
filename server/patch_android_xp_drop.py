#!/usr/bin/env python3
"""10x experience + 5x item-drop quantity for the shipped Android 1.9.3 client.

Two entry-instruction hooks injected into the shared code cave:

  Monster.GetExperience(double level, double expMult)       0x2A7A6E0
      -> multiplies the returned base XP (d0) by 10.
  ItemGenerator.QueueRandomLoot(..., int32 numItems, ...)   0x2C8FE9C
      -> multiplies numItems (arg 4, w3) by 5.

Both replay the displaced prologue and branch back, so the retail bodies run
unmodified.  The XP hook is used instead of patching the .rodata double at
0x13874D0 because that constant is shared by 16 call sites in the binary.

The input is left untouched; output must be a separate file. No device deployment.
"""
import argparse
import shutil
import subprocess
import tempfile
from pathlib import Path

from patch_android_email_login import _read_symbols, _segments, _off_for, _b

STUB_VA, STUB_END = 0x3458000, 0x345C9AC
XP_VA, XP_RESUME = 0x2A7A6E0, 0x2A7A6EC
DROP_VA, DROP_RESUME = 0x2C8FE9C, 0x2C8FEA0
XP_PROLOGUE = bytes.fromhex('e923bd6dfe0b00f9f44f02a9')  # 3 instrs
DROP_PROLOGUE = bytes.fromhex('ffc303d1')                # sub sp, sp, #0xf0
ROOT = Path(__file__).resolve().parent.parent

TARGETS = {
    'XP_RESUME': XP_RESUME,
    'DROP_RESUME': DROP_RESUME,
}


def tool(name):
    path = shutil.which(name)
    if path:
        return path
    for directory in ('/opt/homebrew/opt/llvm/bin', '/usr/local/opt/llvm/bin'):
        path = Path(directory) / name
        if path.exists():
            return str(path)
    raise RuntimeError(f'{name} is required to build the xp/drop stub')


def build_stub(directory):
    stem = directory / 'xp_drop_stub'
    subprocess.run([tool('clang'), '-target', 'aarch64-linux-gnu', '-O2', '-ffreestanding',
                    '-fno-stack-protector', '-fno-pic', '-mno-outline-atomics',
                    '-c', str(ROOT / 'device/stub/xp_drop_stub.c'),
                    '-o', str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('ld.lld'), '-T', str(ROOT / 'device/stub/xp_drop_stub.ld'),
                    *[f'--defsym={k}={v:#x}' for k, v in TARGETS.items()],
                    '-o', str(stem.with_suffix('.elf')), str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('llvm-objcopy'), '-O', 'binary', '--only-section=.text',
                    '--only-section=.rodata', str(stem.with_suffix('.elf')),
                    str(stem.with_suffix('.bin'))], check=True)
    return stem.with_suffix('.bin').read_bytes(), _read_symbols(stem.with_suffix('.elf'))


def apply(data, blob, syms):
    result = bytearray(data)
    segs = _segments(data)
    if len(blob) > STUB_END - STUB_VA:
        raise ValueError('xp/drop stub exceeds its reserved code cave')
    for name in ('xp_hook', 'xp_body_impl', 'drop_hook'):
        if not STUB_VA <= syms[name] < STUB_VA + len(blob):
            raise ValueError(f'{name} lies outside the compiled stub')

    off = _off_for(segs, XP_VA)
    if data[off:off + len(XP_PROLOGUE)] != XP_PROLOGUE:
        raise ValueError(f'unexpected Monster.GetExperience prologue at {XP_VA:#x}')
    result[off:off + 4] = _b(XP_VA, syms['xp_hook'])

    off = _off_for(segs, DROP_VA)
    if data[off:off + len(DROP_PROLOGUE)] != DROP_PROLOGUE:
        raise ValueError(f'unexpected QueueRandomLoot prologue at {DROP_VA:#x}')
    result[off:off + 4] = _b(DROP_VA, syms['drop_hook'])

    off = _off_for(segs, STUB_VA)
    result[off:off + len(blob)] = blob
    return bytes(result)


def build(source, output, artifacts=None):
    source, output = Path(source), Path(output)
    if source.resolve() == output.resolve():
        raise ValueError('Use a separate output path')
    with tempfile.TemporaryDirectory(prefix='nord-xpdrop-') as work:
        directory = Path(work)
        blob, syms = build_stub(directory)
        patched = apply(source.read_bytes(), blob, syms)
        output.write_bytes(patched)
        if artifacts:
            artifacts = Path(artifacts); artifacts.mkdir(parents=True, exist_ok=True)
            for item in directory.iterdir():
                shutil.copy2(item, artifacts / item.name)
        print(f'Injected xp/drop stub ({len(blob)} bytes) at {STUB_VA:#x}; '
              f'GetExperience {XP_VA:#x} -> b {syms["xp_hook"]:#x} (x10), '
              f'QueueRandomLoot {DROP_VA:#x} -> b {syms["drop_hook"]:#x} (x5)')


if __name__ == '__main__':
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('source', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--artifacts', type=Path)
    args = p.parse_args()
    build(args.source, args.output, args.artifacts)
