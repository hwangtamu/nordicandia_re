/* XP / drop-rate stub: .text in the shared BestHTTP Examples code cave
 * (0x344EBFC..0x345C9AC; email 0x344EC24, leaderboard 0x3451000,
 * realtime 0x3456000, skill-rank 0x3457000, this one 0x3458000).
 * No writable state, so there is no .bss. */
ENTRY(xp_hook)
SECTIONS {
  . = 0x3458000;
  .text : { *(.text*) }
  . = ALIGN(16);
  .rodata : { *(.rodata*) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) }
}
