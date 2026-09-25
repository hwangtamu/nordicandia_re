#!/usr/bin/env python3
"""Restore the omitted Android 1.9.3 ARM64 online socket connector and progress pump.

Apply after the online/email/leaderboard patches. Requires clang, lld and llvm-objcopy.
The input remains untouched; output must be a separate file. No device deployment.
"""
import argparse
import json
import os
from pathlib import Path
import shutil
import struct
import subprocess
import tempfile
from patch_android_email_login import _read_symbols, _segments, _off_for, _b

STUB_VA, STUB_END = 0x3456000, 0x345C9AC
STATE_VA, STATE_END = 0x5DE4000, 0x5DE5E40
ROOT = Path(__file__).resolve().parent.parent
TARGETS = {
    'Internal_ctor': 0x2D9AC0C, 'Internal_connect': 0x2D9B2F4,
    'Internal_is_connected': 0x2D99DF4, 'Internal_close': 0x2D9B978,
    'Task_is_completed': 0x412729C, 'WaitUntil': 0x485A0AC,
    'NetSocket_update_fixed': 0x2E09E80, 'NetSocket_update': 0x2E09F14,
    'Forget': 0x486610C, 'CONNECT_RESUME': 0x2E09054,
    'WAIT_RESUME': 0x2E0CEE0, 'RESULT_RESUME': 0x2E0D088,
    'TUPLE_MI': 0x55B9548,
    # NetClient.UpdateAsync is the omitted item-operation flush / keep-alive pump.
    'NetClient_get_Current': 0x2DA1E28, 'NetClient_UpdateAsync': 0x2DAC8EC,
}

def tool(name):
    path = shutil.which(name)
    if path:
        return path
    for directory in ('/opt/homebrew/opt/llvm/bin', '/usr/local/opt/llvm/bin'):
        path = Path(directory) / name
        if path.exists():
            return str(path)
    raise RuntimeError(f'{name} is required to build the realtime stub')

def build_stub(source, directory, host):
    syms = _read_symbols(source)
    targets = dict(TARGETS)
    targets.update({k:v for k,v in syms.items() if k.startswith('il2cpp_')})
    stem = directory / 'realtime_stub'
    subprocess.run([tool('clang'), '-target', 'aarch64-linux-gnu', '-O2', '-ffreestanding',
                    '-fno-stack-protector', '-fno-pic', '-mno-outline-atomics',
                    '-DWS_HOST=' + json.dumps(host), '-c', str(ROOT/'device/stub/realtime_stub.c'),
                    '-o', str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('ld.lld'), '-T', str(ROOT/'device/stub/realtime_stub.ld'),
                    *[f'--defsym={k}={v:#x}' for k,v in targets.items()],
                    '-o', str(stem.with_suffix('.elf')), str(stem.with_suffix('.o'))], check=True)
    subprocess.run([tool('llvm-objcopy'), '-O', 'binary', '--only-section=.text',
                    '--only-section=.rodata', str(stem.with_suffix('.elf')), str(stem.with_suffix('.bin'))], check=True)
    return stem.with_suffix('.bin').read_bytes(), _read_symbols(stem.with_suffix('.elf'))

def _extend_rw_memsz(result, state_va, state_end):
    """The stub keeps its state at STATE_VA.  On the retail libil2cpp the last
    RW PT_LOAD stops at 0x5dde840, i.e. *below* STATE_VA, so the loader never
    maps that page and the first st.global faults with SEGV_ACCERR.  Grow the
    p_memsz of the last RW PT_LOAD up to STATE_END: the loader then maps fresh
    anonymous zero pages there (memsz > filesz), giving the stub private state
    that cannot collide with any il2cpp global."""
    phoff = struct.unpack_from('<Q', result, 32)[0]
    entsize, count = struct.unpack_from('<HH', result, 54)
    target = None
    for i in range(count):
        base = phoff + i * entsize
        typ, flags, off, va, _, filesz, memsz, _ = struct.unpack_from('<IIQQQQQQ', result, base)
        if typ == 1 and (flags & 2) and va <= state_va:
            if target is None or va > target[1]:
                target = (base, va, memsz)
    if target is None:
        raise ValueError('no writable PT_LOAD covers the realtime state')
    base, va, memsz = target
    if va + memsz >= state_end:
        return
    struct.pack_into('<Q', result, base + 40, state_end - va)
    print(f'grew RW PT_LOAD p_memsz to {state_end - va:#x} (state at {state_va:#x})')


def apply(data, blob, syms):
    result=bytearray(data); segs=_segments(data)
    def replace(va, expected, replacement):
        off=_off_for(segs,va)
        if data[off:off+len(expected)] != expected:
            raise ValueError(f'Unexpected original instructions at {va:#x}; refusing input')
        result[off:off+len(replacement)]=replacement
    if len(blob)>STUB_END-STUB_VA:
        raise ValueError('Realtime stub exceeds its reserved code cave')
    for name in ('ws_connect_trampoline','ws_wait_trampoline','ws_result_trampoline','ws_fixed_update'):
        if not STUB_VA <= syms[name] < STUB_VA+len(blob):
            raise ValueError(f'{name} lies outside the compiled stub')
    if not STATE_VA <= syms['g_wait'] < STATE_END-16:
        raise ValueError('Realtime state is outside the reserved writable space')
    # The initial offline-diversion experiments must not survive into this build.
    for va, original in [(0x257D29C,'68420035'),(0x26FB754,'00010037')]:
        off=_off_for(segs,va)
        if data[off:off+4] not in (bytes.fromhex(original),bytes.fromhex('1f2003d5')):
            raise ValueError(f'Unknown world-entry patch at {va:#x}')
        result[off:off+4]=bytes.fromhex(original)
    replace(0x2E09050, bytes.fromhex('ff8301d1'), _b(0x2E09050,syms['ws_connect_trampoline']))
    replace(0x2E0CED4, bytes.fromhex('085c40f9'), _b(0x2E0CED4,syms['ws_wait_trampoline']))
    replace(0x2E0D064, bytes.fromhex('ff8b0039'), _b(0x2E0D064,syms['ws_result_trampoline']))
    replace(0x26FFEB0, bytes.fromhex('ffc301d1'), _b(0x26FFEB0,syms['ws_fixed_update']))
    # BestHTTP has a separate TLS verifier from Mono's gRPC transport.
    replace(0x2FF634C, bytes.fromhex('ff0304d1'), bytes.fromhex('c0035fd6'))
    _extend_rw_memsz(result, STATE_VA, STATE_END)
    off=_off_for(segs,STUB_VA)
    result[off:off+len(blob)]=blob
    return bytes(result)

def build(source, output, host='prod.038c3288.nip.io', artifacts=None):
    source,output=Path(source),Path(output)
    if source.resolve()==output.resolve():
        raise ValueError('Use a separate output path')
    if not host or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789.-' for c in host):
        raise ValueError('host must be a DNS hostname or IPv4 address')
    with tempfile.TemporaryDirectory(prefix='nord-realtime-') as work:
        directory=Path(work)
        blob,syms=build_stub(source,directory,host)
        patched=apply(source.read_bytes(),blob,syms)
        output.write_bytes(patched)
        if artifacts:
            artifacts=Path(artifacts);artifacts.mkdir(parents=True,exist_ok=True)
            for item in directory.iterdir(): shutil.copy2(item,artifacts/item.name)
        print(f'Restored realtime connector and progress pump ({len(blob)} bytes), host={host}')

if __name__=='__main__':
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('source',type=Path);p.add_argument('output',type=Path)
    p.add_argument('--host',default='prod.038c3288.nip.io');p.add_argument('--artifacts',type=Path)
    args=p.parse_args();build(args.source,args.output,args.host,args.artifacts)
