ENTRY(ws_connect_trampoline)
SECTIONS {
  . = 0x3456000;
  .text : { *(.text*) }
  . = ALIGN(16);
  .rodata : { *(.rodata*) }
  . = 0x5DE4000;
  .bss : { *(.bss*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) *(.eh_frame*) }
}
