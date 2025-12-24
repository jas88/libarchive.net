/* Linker script fragment to preserve initialization sections with --gc-sections */
/* These sections are not directly referenced but are needed for library startup */
SECTIONS {
    .preinit_array : { KEEP (*(.preinit_array*)) }
    .init_array : { KEEP (*(.init_array*)) }
    .fini_array : { KEEP (*(.fini_array*)) }
    .init : { KEEP (*(.init)) }
    .fini : { KEEP (*(.fini)) }
    .ctors : { KEEP (*(.ctors*)) }
    .dtors : { KEEP (*(.dtors*)) }
}
INSERT AFTER .text;
