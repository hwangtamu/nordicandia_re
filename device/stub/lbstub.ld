/* Leaderboard stub: .text in the RX cave, writable globals in the RW slack at the
 * tail of the last PT_LOAD. Kept off 0x5DDE000 so it can coexist with the email stub. */
ENTRY(lb_overall)
SECTIONS {
  . = 0x3451000;
  .text : { *(.text*) }
  . = ALIGN(16);
  .rodata : { *(.rodata*) }
  . = 0x5DDE100;
  .bss : { *(.bss*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) }
}
