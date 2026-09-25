#!/usr/bin/env bash
# Build the leaderboard stub into raw AArch64 bytes for injection at 0x3451000.
# The stub is linked at its real VA so PC-relative bl/b/adrp resolve under ASLR.
# Requires LLVM's clang + lld + objcopy (brew install llvm).
# NOTE: server/patch_android_leaderboard.py rebuilds the stub from source itself;
# this script is only for inspecting the blob by hand.
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
  --defsym=il2cpp_class_is_valuetype=0x02317CB8
  --defsym=il2cpp_class_value_size=0x02317CD4
  --defsym=il2cpp_gc_wbarrier_set_field=0x02318414
  --defsym=il2cpp_class_get_method_from_name=0x02317C9C
  --defsym=il2cpp_runtime_invoke=0x0231855C
  --defsym=il2cpp_object_get_class=0x02318508
  --defsym=il2cpp_class_get_type=0x02317D1C
  --defsym=il2cpp_type_get_object=0x023185E8
  --defsym=retail_apply_rank=0x025F771C
  --defsym=retail_create_row_resume=0x025F72B8
  --defsym=retail_button_press=0x04F76098
  --defsym=retail_get_window=0x0278C698
  --defsym=retail_bring_front=0x0278BBF0
  --defsym=retail_instantiate_9=0x0379B140
  --defsym=retail_instantiate_method_slot=0x055893A8
  --defsym=retail_inspect_player=0x025F33E4
  --defsym=retail_forget=0x0486610C
  --defsym=retail_component_get_transform=0x04E40208
)
CC=clang; LD=ld.lld; OBJCOPY=llvm-objcopy
for p in /opt/homebrew/opt/llvm/bin /usr/local/opt/llvm/bin; do
  [ -x "$p/ld.lld" ] && ! command -v "$LD" >/dev/null && LD="$p/ld.lld"
  [ -x "$p/llvm-objcopy" ] && ! command -v "$OBJCOPY" >/dev/null && OBJCOPY="$p/llvm-objcopy"
done
$CC -target aarch64-linux-gnu -O2 -ffreestanding -fno-stack-protector \
    -fno-pic -mno-outline-atomics -fno-jump-tables -c "$HERE/leaderboard_stub.c" -o "$HERE/lbstub.o"
"$LD" -T "$HERE/lbstub.ld" "${DEFSYMS[@]}" -o "$HERE/lbstub.elf" "$HERE/lbstub.o"
"$OBJCOPY" -O binary --only-section=.text --only-section=.rodata "$HERE/lbstub.elf" "$OUT"
echo "wrote $OUT ($(wc -c <"$OUT") bytes)"
