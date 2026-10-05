#!/usr/bin/env python3
"""Repackage the published v16 XAPK with only the Season toggle library fix."""

import argparse
import os
from pathlib import Path
import shutil
import tempfile
import zipfile

os.environ.setdefault("ANDROID_BUILD_TOOLS", str(Path.home() / "Library/Android/sdk/build-tools/36.0.0"))
import patch_xapk as px


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("release_xapk", type=Path)
    parser.add_argument("patched_library", type=Path)
    parser.add_argument("output_xapk", type=Path)
    args = parser.parse_args()
    if args.release_xapk.resolve() == args.output_xapk.resolve():
        parser.error("Use a separate output path to preserve the release XAPK")
    keystore = Path(__file__).resolve().parent.parent / "patched_arm64/nordicandia.keystore"
    with tempfile.TemporaryDirectory() as td:
        tmp = Path(td)
        with zipfile.ZipFile(args.release_xapk) as source:
            config = tmp / "config.arm64_v8a.apk"
            config.write_bytes(source.read(config.name))
        unsigned = tmp / "config-unsigned.apk"
        signed = tmp / config.name
        px.rewrite_apk(str(config), str(unsigned), {
            "lib/arm64-v8a/libil2cpp.so": args.patched_library.read_bytes(),
        })
        px.sign(str(unsigned), str(signed), str(keystore), "nordpass", "nord")
        with zipfile.ZipFile(args.release_xapk) as source, zipfile.ZipFile(args.output_xapk, "w") as output:
            for info in source.infolist():
                if info.filename == config.name:
                    output.writestr(info, signed.read_bytes())
                else:
                    with source.open(info) as reader, output.open(info, "w") as writer:
                        shutil.copyfileobj(reader, writer)
    print(args.output_xapk, args.output_xapk.stat().st_size)


if __name__ == "__main__":
    main()
