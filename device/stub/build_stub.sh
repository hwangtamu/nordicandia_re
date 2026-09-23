#!/usr/bin/env bash
# Build the email-login native stub into raw AArch64 bytes for injection at
# 0x344EC24 in libil2cpp.so.
#
# The stub must be linked at its real virtual address so that every PC-relative
# `bl`/`b`/`adrp`+`add` resolves to the right target regardless of the ASLR base.
#
# Requires LLVM's lld + objcopy (brew install llvm). Without them, use the
# Python relocator in relocate_stub.py (same result).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/stub.bin}"

# Target VAs of the client APIs the stub calls (libil2cpp 1.9.3 arm64-v8a).
DEFSYMS=(
  --defsym=il2cpp_string_new=0x02318588
  --defsym=NetClient_get_Current=0x02DA1E28
  --defsym=UIWindowManager_ShowSingleInputDialogOkCancel=0x0279075C
  --defsym=NetClient_SignInWithEmail=0x02DA2584
  --defsym=NetClient_RegisterGameAccount=0x02DA46E0
)

CC=clang
LD=ld.lld
OBJCOPY=llvm-objcopy
if ! command -v "$LD" >/dev/null; then
  # Xcode/brew LLVM may install them under a versioned prefix.
  for p in /opt/homebrew/opt/llvm/bin /usr/local/opt/llvm/bin; do
    [ -x "$p/ld.lld" ] && LD="$p/ld.lld" && OBJCOPY="$p/llvm-objcopy"
  done
fi

$CC -target aarch64-linux-gnu -O2 -ffreestanding -fno-stack-protector \
    -fno-pic -mno-outline-atomics -c "$HERE/email_login_stub.c" -o "$HERE/stub.o"

"$LD" -Ttext=0x344EC24 "${DEFSYMS[@]}" -o "$HERE/stub.elf" "$HERE/stub.o"
"$OBJCOPY" -O binary --only-section=.text "$HERE/stub.elf" "$OUT"
echo "wrote $OUT ($(wc -c <"$OUT") bytes)"