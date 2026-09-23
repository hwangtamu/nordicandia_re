#!/usr/bin/env python3
"""
Option B: repackage + re-sign a Nordicandia XAPK so the client talks to a private server.

What it does
  1. patches the server hostname string literals inside
     base.apk!/assets/bin/Data/Managed/Metadata/global-metadata.dat  (same byte length)
  2. re-signs every split APK with one key (split sets must share a signer)
  3. zipaligns, verifies, and repackages everything into a new .xapk

ABI-generic: it auto-detects config.<abi>.apk in --indir, so you can point it at
either the armeabi-v7a or the arm64-v8a download; only the config split differs.

Examples
  # v7a build (default host points at the Android emulator's host alias 10.0.2.2)
  python3 patch_xapk.py --prod-host ab.10-0-2-2.sslip.io --staging-host abcde.10-0-2-2.sslip.io

  # reachable over adb reverse (client connects to device-localhost:443)
  python3 patch_xapk.py --prod-host a.127-0-0-1.sslip.io --staging-host abcd.127-0-0-1.sslip.io

  # arm64 build
  python3 patch_xapk.py --indir /path/to/arm64_xapk_dir --config-apk config.arm64_v8a.apk
"""
import argparse, glob, json, os, re, shutil, subprocess, zipfile

BASE_NAME = "com.IterativeStudios.Nordicandia.apk"
ASSETS_NAME = "UnityDataAssetPack.apk"
METADATA = "assets/bin/Data/Managed/Metadata/global-metadata.dat"

_WIN = os.name == "nt"
BUILD_TOOLS = os.environ.get("ANDROID_BUILD_TOOLS", "/home/han.wang1/Android/Sdk/build-tools/36.0.0")
ZIPALIGN = os.path.join(BUILD_TOOLS, "zipalign.exe" if _WIN else "zipalign")
APKSIGNER = os.path.join(BUILD_TOOLS, "apksigner.bat" if _WIN else "apksigner")
KEYTOOL = os.environ.get("KEYTOOL", "keytool.exe" if _WIN else "keytool")


def patch_metadata(data: bytes, prod_host: str, staging_host: str) -> bytes:
    old_prod, old_staging = b"prod.nordicandia.net", b"staging.nordicandia.net"
    if len(prod_host) != len(old_prod):
        raise SystemExit(f"--prod-host must be exactly {len(old_prod)} chars (got {len(prod_host)})")
    if len(staging_host) != len(old_staging):
        raise SystemExit(f"--staging-host must be exactly {len(old_staging)} chars (got {len(staging_host)})")
    if data.count(old_prod) != 1 or data.count(old_staging) != 1:
        raise SystemExit("unexpected host-literal occurrences; is this really the right metadata?")
    return data.replace(old_prod, prod_host.encode()).replace(old_staging, staging_host.encode())


def rewrite_apk(src, dst, replacements, compress_override=None):
    with zipfile.ZipFile(src) as zin, zipfile.ZipFile(dst, "w") as zout:
        for info in zin.infolist():
            data = zin.read(info.filename)
            if info.filename in replacements:
                data = replacements[info.filename]
                if compress_override is not None:
                    info.compress_type = compress_override
            zout.writestr(info, data)


def run(*cmd):
    print("  $", " ".join(cmd))
    subprocess.run(cmd, check=True, stdout=subprocess.DEVNULL, stderr=subprocess.STDOUT)


def sign(apk_in, apk_out, keystore, passwd, alias):
    aligned = apk_in + ".aligned"
    run(ZIPALIGN, "-f", "-p", "4", apk_in, aligned)
    run(APKSIGNER, "sign", "--ks", keystore, "--ks-pass", f"pass:{passwd}",
        "--key-pass", f"pass:{passwd}", "--ks-key-alias", alias,
        "--v1-signing-enabled", "true", "--v2-signing-enabled", "true", "--v3-signing-enabled", "true",
        "--out", apk_out, aligned)
    os.remove(aligned)
    run(APKSIGNER, "verify", "--print-certs", apk_out)


def build_xapk(outdir, members, manifest_path, config_id):
    xapk = os.path.join(outdir, "Nordicandia_1.9.3_private.xapk")
    with open(manifest_path) as f:
        manifest = json.load(f)
    if "split_configs" in manifest:
        manifest["split_configs"] = [config_id, "UnityDataAssetPack"]
    if "split_apks" in manifest:
        for e in manifest["split_apks"]:
            if e.get("id", "").startswith("config."):
                e["id"] = config_id
                e["file"] = config_id + ".apk"
    with zipfile.ZipFile(xapk, "w", zipfile.ZIP_STORED) as z:
        for name, path in members:
            data = open(path, "rb").read()
            if name == "manifest.json":
                data = json.dumps(manifest).encode()
            zi = zipfile.ZipInfo(name, date_time=(1980, 1, 1, 0, 0, 0))
            zi.compress_type = zipfile.ZIP_STORED
            z.writestr(zi, data)
    return xapk


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--indir", default="/local_disk/nordicandia_re")
    ap.add_argument("--outdir", default="/local_disk/nordicandia_re/patched")
    ap.add_argument("--prod-host", default="ab.10-0-2-2.sslip.io")
    ap.add_argument("--staging-host", default="abcde.10-0-2-2.sslip.io")
    ap.add_argument("--config-apk", default=None, help="config split filename (default: auto-detect config.*.apk)")
    ap.add_argument("--online-arm64", action="store_true", help="Patch original 1.9.3 ARM64 device login and online entry")
    ap.add_argument("--keystore", default=None)
    ap.add_argument("--ks-pass", default="nordpass")
    ap.add_argument("--alias", default="nord")
    args = ap.parse_args()

    os.makedirs(args.outdir, exist_ok=True)
    base_src = os.path.join(args.indir, BASE_NAME)
    assets_src = os.path.join(args.indir, ASSETS_NAME)
    if args.config_apk:
        config_src = os.path.join(args.indir, args.config_apk)
    else:
        found = [p for p in glob.glob(os.path.join(args.indir, "config.*.apk")) if "unaligned" not in p]
        if not found:
            raise SystemExit("no config.*.apk found; pass --config-apk")
        config_src = found[0]
    config_id = os.path.basename(config_src)[:-4]
    print(f"base={os.path.basename(base_src)}  config={config_id}  assets={assets_src}")

    keystore = args.keystore or os.path.join(args.outdir, "nordicandia.keystore")
    if not os.path.exists(keystore):
        print("Generating keystore", keystore)
        run(KEYTOOL, "-genkeypair", "-keystore", keystore, "-alias", args.alias,
            "-keyalg", "RSA", "-keysize", "2048", "-validity", "10000",
            "-storepass", args.ks_pass, "-keypass", args.ks_pass,
            "-dname", "CN=Nordicandia Private Server, O=RE")

    print("Patching", os.path.basename(base_src))
    with zipfile.ZipFile(base_src) as z:
        meta = z.read(METADATA)
        ctype = z.getinfo(METADATA).compress_type
    meta2 = patch_metadata(meta, args.prod_host, args.staging_host)
    base_tmp = os.path.join(args.outdir, BASE_NAME + ".patched-unsigned")
    rewrite_apk(base_src, base_tmp, {METADATA: meta2}, compress_override=ctype)
    print(f"  prod    -> {args.prod_host}")
    print(f"  staging -> {args.staging_host}")

    config_tmp = None
    if args.online_arm64:
        from patch_android_online import patch
        library = "lib/arm64-v8a/libil2cpp.so"
        with zipfile.ZipFile(config_src) as z:
            patched_library = patch(z.read(library))
        config_tmp = os.path.join(args.outdir, os.path.basename(config_src) + ".patched-unsigned")
        rewrite_apk(config_src, config_tmp, {library: patched_library})

    jobs = [(base_tmp, os.path.join(args.outdir, BASE_NAME)),
            (config_tmp or config_src, os.path.join(args.outdir, os.path.basename(config_src))),
            (assets_src, os.path.join(args.outdir, ASSETS_NAME))]
    for src, dst in jobs:
        print("Signing", os.path.basename(dst))
        sign(src, dst, keystore, args.ks_pass, args.alias)
    if os.path.exists(base_tmp):
        os.remove(base_tmp)
    if config_tmp:
        os.remove(config_tmp)

    members = [(BASE_NAME, jobs[0][1]), ("icon.png", os.path.join(args.indir, "icon.png")),
               (os.path.basename(config_src), jobs[1][1]), (ASSETS_NAME, jobs[2][1]),
               ("manifest.json", os.path.join(args.indir, "manifest.json"))]
    xapk = build_xapk(args.outdir, members, os.path.join(args.indir, "manifest.json"), config_id)
    print("DONE")
    print("  XAPK     :", xapk, os.path.getsize(xapk), "bytes")
    print("  keystore :", keystore)


if __name__ == "__main__":
    main()
