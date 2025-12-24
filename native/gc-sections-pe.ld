/* Linker script fragment to preserve CRT initialization sections with --gc-sections on Windows PE */
/* These sections contain C/C++ initialization code that must not be removed */
SECTIONS {
    /* C runtime initializers (called before main/DllMain) */
    .CRT$XCA : { KEEP (*(.CRT$XCA)) }
    .CRT$XCAA : { KEEP (*(.CRT$XCAA)) }
    .CRT$XCZ : { KEEP (*(.CRT$XCZ)) }

    /* C initializers */
    .CRT$XIA : { KEEP (*(.CRT$XIA)) }
    .CRT$XIAA : { KEEP (*(.CRT$XIAA)) }
    .CRT$XIZ : { KEEP (*(.CRT$XIZ)) }

    /* C++ constructors */
    .CRT$XCA : { KEEP (*(.CRT$XCA*)) }
    .CRT$XCZ : { KEEP (*(.CRT$XCZ*)) }

    /* TLS callbacks */
    .CRT$XLA : { KEEP (*(.CRT$XLA)) }
    .CRT$XLZ : { KEEP (*(.CRT$XLZ)) }

    /* C pre-terminators */
    .CRT$XPA : { KEEP (*(.CRT$XPA)) }
    .CRT$XPZ : { KEEP (*(.CRT$XPZ)) }

    /* C terminators */
    .CRT$XTA : { KEEP (*(.CRT$XTA)) }
    .CRT$XTZ : { KEEP (*(.CRT$XTZ)) }

    /* Thread local storage */
    .tls : { KEEP (*(.tls)) }
    .tls$AAA : { KEEP (*(.tls$AAA)) }
    .tls$ZZZ : { KEEP (*(.tls$ZZZ)) }
}
INSERT AFTER .text;
