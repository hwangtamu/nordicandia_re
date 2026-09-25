#!/usr/bin/env bash
# Build the leaderboard stub into raw AArch64 bytes for injection at 0x3451000.
# The stub is linked at its real VA so PC-relative bl/b/adrp resolve under ASLR.
# Requires LLVM's clang + lld + objcopy (brew install llvm).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
OUT="${1:-$HERE/leaderboard_stub.bin}"
DEFSYMS=(
  --defsym=il2cpp_string_new=0x02318578
  --defsym=il2cpp_alloc=0x02317C14
  --defsym=il2cpp_object_new=0x02318514
  --defsym=il2cpp_array_new=0x02317C28
  --defsym=il2cpp_class_from_name=0x02317C74
  --defsym=il2cpp_domain_get=0x02318138
  --defsym=il2cpp_domain_get_assemblies=0x02318144
  --defsym=il2cpp_assembly_get_image=0x02317C3C
  --defsym=il2cpp_runtime_class_init=0x02318560
)
CC=clang; LD=ld.lld; OBJCOPY=llvm-objcopy
if ! command -v "$LD" >/dev/null; then
  for p in /opt/homebrew/opt/llvm/bin /usr/local/opt/llvm/bin; do
    [ -x "$p/ld.lld" ] && LD="$p/ld.lld" && OBJCOPY="$p/llvm-objcopy"
  done
fi
$CC -target aarch64-linux-gnu -O2 -ffreestanding -fno-stack-protector \
    -fno-pic -mno-outline-atomics -c "$HERE/leaderboard_stub.c" -o "$HERE/lbstub.o"
"$LD" -T "$HERE/lbstub.ld" "${DEFSYMS[@]}" -o "$HERE/lbstub.elf" "$HERE/lbstub.o"
"$OBJCOPY" -O binary --only-section=.text --only-section=.rodata "$HERE/lbstub.elf" "$OUT"
echo "wrote $OUT ($(wc -c <"$OUT") bytes)"
