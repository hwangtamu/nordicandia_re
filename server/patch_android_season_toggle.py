#!/usr/bin/env python3
"""Apply the Season-selection fix to the published Android v16 libil2cpp.so."""

import argparse
import hashlib
from pathlib import Path

from patch_android_online import patch_season_toggle


V16_SHA256 = "fa9f7a498548c30352c55a30be2c81d85a353a32030c5ab4d1a89709baaec39f"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    if args.source.resolve() == args.output.resolve():
        parser.error("Use a separate output path to preserve the release library")
    data = args.source.read_bytes()
    if hashlib.sha256(data).hexdigest() != V16_SHA256:
        parser.error("Expected the published Android v16 library")
    fixed = patch_season_toggle(data)
    args.output.write_bytes(fixed)
    print(hashlib.sha256(fixed).hexdigest(), args.output)


if __name__ == "__main__":
    main()
