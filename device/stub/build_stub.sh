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
  --defsym=il2cpp_string_new=0x02318578
  --defsym=NetClient_get_Current=0x02DA1E28
  --defsym=UIWindowManager_ShowSingleInputDialogOkCancel=0x0279075C
  --defsym=SHOWDLG_RESUME=0x02790764
  --defsym=UnityGame_SignInNew=0x02701390
  --defsym=il2cpp_class_get_method_from_name=0x02317C9C
  --defsym=il2cpp_method_get_param=0x02318490
  --defsym=il2cpp_class_from_type=0x02317D14
  --defsym=il2cpp_object_new=0x02318514
  --defsym=WMGR_RESUME=0x0278D73C
  --defsym=login_resume=0x026F2438
  --defsym=GM_ONENABLE_RESUME=0x0263356C
  --defsym=GM_START_RESUME=0x026337B8
  --defsym=NetClient_get_Current=0x02DA1E28
  --defsym=NetClient_SignInWithEmail=0x02DA2584
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

"$LD" -T "$HERE/stub.ld" "${DEFSYMS[@]}" -o "$HERE/stub.elf" "$HERE/stub.o"
"$OBJCOPY" -O binary --only-section=.text --only-section=.rodata "$HERE/stub.elf" "$OUT"
echo "wrote $OUT ($(wc -c <"$OUT") bytes)"