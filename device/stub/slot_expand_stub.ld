/* Link the slot-expand stub at its real VA.
 * .text goes in the code cave at 0x3459000; .bss goes into RW at 0x5DE8400. */
ENTRY(hook_expand_skill_slot)
SECTIONS {
  . = 0x3459000;
  .text : { *(.text*) }
  . = ALIGN(16);
  .rodata : { *(.rodata*) }
  . = 0x5DE8400;
  .bss : { *(.bss*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) *(.eh_frame*) }
}
