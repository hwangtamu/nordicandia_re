/* Leaderboard stub: .text in the RX cave (BestHTTP Examples demo code), writable
 * globals in a private page ABOVE the retail image.
 *
 * The retail .bss is 0x5BA8C50..0x5DDE840, so the old 0x5DDE100 placement shared
 * memory with live il2cpp globals: the cached row class was overwritten and
 * il2cpp_array_new crashed with a bad klass.  0x5DE6000 is past the end of the
 * image; patch_android_leaderboard.py grows the last RW PT_LOAD p_memsz so the
 * loader maps it as fresh zero pages.  Layout of the private state pages:
 *   0x5DE4000..0x5DE5E40  realtime stub
 *   0x5DE6000..0x5DE7000  leaderboard stub (this file; feed + click mappings)
 *   0x5DE7000..0x5DE8000  email-login stub
 */
ENTRY(lb_overall)
SECTIONS {
  . = 0x3451000;
  .text : { *(.text*) }
  . = ALIGN(16);
  .rodata : { *(.rodata*) }
  . = 0x5DE6000;
  .bss : { *(.bss*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) *(.eh_frame*) }
}
