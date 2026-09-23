#!/usr/bin/env python3
"""
Nordicandia (com.IterativeStudios.Nordicandia) offline data / save decryptor.

Recovered from libil2cpp.so:  CryptorEngine.EncryptToBytes / DecryptToBytes
  - TripleDES (System.Security.Cryptography.TripleDESCryptoServiceProvider)
  - Mode     = ECB
  - Padding  = PKCS7
  - Key      = MD5(UTF8("iloveburgers"))   (16 bytes -> 2-key 3DES)
  - useHashing = true (hard-coded by Game.Utils / CryptorEngineSaveCipher)

Game definition TextAssets are Base64(ciphertext).
Saves use the same cipher directly on bytes (CryptorEngineSaveCipher / LegacyCipher).

Usage:
    python3 decrypt_nordicandia.py <encrypted-file-or-base64-text> [-o out]
    python3 decrypt_nordicandia.py --encrypt <plaintext-file> -o out
"""
import sys, base64, hashlib, argparse
from Crypto.Cipher import DES3
from Crypto.Util.Padding import pad, unpad

KEY = hashlib.md5(b"iloveburgers").digest()
CIPHER_NAME = "TripleDES-ECB/PKCS7 key=MD5('iloveburgers')"


def decrypt_bytes(data: bytes) -> bytes:
    return unpad(DES3.new(KEY, DES3.MODE_ECB).decrypt(data), 8)


def encrypt_bytes(data: bytes) -> bytes:
    return DES3.new(KEY, DES3.MODE_ECB).encrypt(pad(data, 8))


def maybe_b64(data: bytes) -> bytes:
    stripped = bytes(data).strip()
    if stripped and all(chr(c) in "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/=\r\n" for c in stripped):
        try:
            return base64.b64decode(stripped)
        except Exception:
            pass
    return data


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("-o", "--output")
    ap.add_argument("--encrypt", action="store_true")
    args = ap.parse_args()

    raw = open(args.input, "rb").read()
    if args.encrypt:
        out = encrypt_bytes(raw)
    else:
        out = decrypt_bytes(maybe_b64(raw))

    if args.output:
        open(args.output, "wb").write(out)
        print(f"wrote {len(out)} bytes to {args.output}  [{CIPHER_NAME}]")
    else:
        sys.stdout.buffer.write(out)


if __name__ == "__main__":
    main()
