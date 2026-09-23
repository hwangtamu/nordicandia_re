/* Link the email-login stub at its real VA. .text goes in the RX code cave;
 * writable state goes into the mapped-but-unused slack at the end of the last
 * RW PT_LOAD (libil2cpp is loaded RW to 0x5DE5E40). */
ENTRY(email_login_entry)
SECTIONS {
  . = 0x344EC24;
  .text : { *(.text*) }
  . = ALIGN(16);
  rodata_start = .;
  .rodata : { *(.rodata*) }
  . = 0x5DE5000;
  .bss : { *(.bss*) *(COMMON) }
  /DISCARD/ : { *(.comment) *(.note*) *(.llvm_addrsig) }
}
