#!/usr/bin/env python3
"""Compose every client patch into one libil2cpp.so.

The individual patch scripts each apply patch_android_online internally, so they
cannot be chained as-is. This driver applies online once, neutralises the
per-script online step, then applies email -> leaderboard -> realtime -> skill_rank
in the order their code caves / RW state expect.
"""
import hashlib
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import patch_android_online
import patch_android_email_login as email
import patch_android_leaderboard as lb
import patch_android_realtime as rt
import patch_android_skill_rank as sr

STUB_DIR = Path(__file__).resolve().parent.parent / 'device/stub'


def build(src: Path, out: Path, host: str = 'prod.038c3288.nip.io') -> Path:
    src, out = Path(src), Path(out)
    if src.resolve() == out.resolve():
        raise ValueError('Use a separate output path')

    data = patch_android_online.patch(src.read_bytes())
    print('online      ok')

    steps = []
    t = out.with_name(out.name + '.email')
    t.write_bytes(data); email.patch_online = lambda b: b
    email.build(t, out, STUB_DIR / 'stub.bin'); data = out.read_bytes(); print('email       ok')

    t.write_bytes(data); lb.patch_online = lambda b: b
    lb.build(t, out); data = out.read_bytes(); print('leaderboard ok')

    t.write_bytes(data)
    rt.build(t, out, host=host); data = out.read_bytes(); print('realtime    ok')

    t.write_bytes(data)
    sr.build(t, out); data = out.read_bytes(); print('skill_rank  ok')

    t.unlink(missing_ok=True)
    print('wrote', out, 'sha256=' + hashlib.sha256(data).hexdigest())
    return out


if __name__ == '__main__':
    import argparse
    p = argparse.ArgumentParser()
    p.add_argument('source', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--host', default='prod.038c3288.nip.io')
    a = p.parse_args()
    build(a.source, a.output, a.host)