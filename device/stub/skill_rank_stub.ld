/* Link the skill-rank stub at its real VA.
 * .text goes in the BestHTTP Examples code cave (shared with the online/email/
 * leaderboard/realtime stubs); .bss goes into the RW tail that
 * patch_android_realtime.py already grows (0x5DE4000..0x5DE5E40). */
ENTRY(rankup_trampoline)
SECTIONS {
  . = 0x3457000;
  .text : { *(.text*) }
  . = ALIGN(16);
  .rodata : { *(.rodata*) }
  . = 0x5DE8000;
  .bss : { *(.bss*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) *(.eh_frame*) }
}