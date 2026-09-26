#!/usr/bin/env python3
"""Package a signed, installable XAPK from the source split APKs + a patched libil2cpp.

Steps: patch global-metadata.dat host -> replace lib/arm64-v8a/libil2cpp.so ->
zipalign+apksigner every split -> assemble Nordicandia_1.9.3_private.xapk.
"""
import os
import sys
import zipfile
from pathlib import Path

os.environ.setdefault('ANDROID_BUILD_TOOLS', str(Path.home() / 'Library/Android/sdk/build-tools/36.0.0'))
sys.path.insert(0, str(Path(__file__).resolve().parent))
import patch_xapk as px  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent
SRC = ROOT / 'dist/android-arm64-src'
OUT = ROOT / 'dist/android-arm64-release'
KEYSTORE = ROOT / 'patched_arm64/nordicandia.keystore'
PROD = 'prod.038c3288.nip.io'
STAGING = 'staging.038c3288.nip.io'


def main():
    lib = Path(sys.argv[1]) if len(sys.argv) > 1 else Path('/tmp/full.so')
    OUT.mkdir(parents=True, exist_ok=True)

    base = SRC / px.BASE_NAME
    with zipfile.ZipFile(base) as z:
        meta = z.read(px.METADATA)
        ctype = z.getinfo(px.METADATA).compress_type
    meta2 = px.patch_metadata(meta, PROD, STAGING)
    base_tmp = OUT / (px.BASE_NAME + '.patched-unsigned')
    px.rewrite_apk(str(base), str(base_tmp), {px.METADATA: meta2}, compress_override=ctype)
    print(f'  metadata: prod->{PROD} staging->{STAGING}')

    cfg = SRC / 'config.arm64_v8a.apk'
    cfg_tmp = OUT / 'config.arm64_v8a.apk.patched-unsigned'
    px.rewrite_apk(str(cfg), str(cfg_tmp), {'lib/arm64-v8a/libil2cpp.so': lib.read_bytes()})
    print(f'  libil2cpp: {lib} ({lib.stat().st_size} bytes)')

    for src, dst in ((base_tmp, OUT / px.BASE_NAME),
                     (cfg_tmp, OUT / 'config.arm64_v8a.apk'),
                     (SRC / px.ASSETS_NAME, OUT / px.ASSETS_NAME)):
        print('  signing', dst.name)
        px.sign(str(src), str(dst), str(KEYSTORE), 'nordpass', 'nord')
    base_tmp.unlink(missing_ok=True)
    cfg_tmp.unlink(missing_ok=True)

    members = [(px.BASE_NAME, str(OUT / px.BASE_NAME)),
               ('icon.png', str(SRC / 'icon.png')),
               ('config.arm64_v8a.apk', str(OUT / 'config.arm64_v8a.apk')),
               (px.ASSETS_NAME, str(OUT / px.ASSETS_NAME)),
               ('manifest.json', str(SRC / 'manifest.json'))]
    xapk = px.build_xapk(str(OUT), members, str(SRC / 'manifest.json'), 'config.arm64_v8a')
    print('DONE', xapk, os.path.getsize(xapk), 'bytes')


if __name__ == '__main__':
    main()