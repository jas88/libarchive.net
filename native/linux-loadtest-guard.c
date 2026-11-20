/*
 * The load test's check of a 32-bit ARM library's stack protector guard (see
 * linux-loadtest.h): the library's own __stack_chk_guard, from linux-arm-stack-guard.c,
 * must hold the first bytes of this process's AT_RANDOM, the random bytes that the kernel
 * gives every process, with the lowest byte cleared, as that file's constructor seeds it.
 * zig's compiler-rt, whose constant guard it replaces, cannot pass, and nor can a guard
 * seeded any other way. The guard is hidden, so the check looks it up in the symbol table
 * of the library's file, and reads it where the library is loaded. Elsewhere the library
 * uses the C library's guard, and check_stack_guard has nothing to check.
 */
#define _GNU_SOURCE
#define _FILE_OFFSET_BITS 64
#include <stddef.h>

#include "linux-loadtest.h"

#if defined(__arm__)
#include <dlfcn.h>
#include <elf.h>
#include <errno.h>
#include <fcntl.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/auxv.h>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>

/* The value of the symbol of that name that the ELF file of size bytes defines, or 0 */
static Elf32_Addr defined_symbol(const unsigned char *file, size_t size, const char *name)
{
    const Elf32_Ehdr *header = (const Elf32_Ehdr *)file;
    const Elf32_Shdr *sections;
    size_t i, j;

    if (size < sizeof *header || memcmp(header->e_ident, ELFMAG, SELFMAG) != 0
        || header->e_ident[EI_CLASS] != ELFCLASS32 || header->e_shentsize != sizeof (Elf32_Shdr)
        || header->e_shoff > size || header->e_shnum > (size - header->e_shoff) / sizeof (Elf32_Shdr))
        return 0;
    sections = (const Elf32_Shdr *)(file + header->e_shoff);
    for (i = 0; i < header->e_shnum; i++) {
        const Elf32_Shdr *table = &sections[i], *strings;
        const Elf32_Sym *symbols;

        if (table->sh_type != SHT_SYMTAB || table->sh_link >= header->e_shnum
            || table->sh_offset > size || table->sh_size > size - table->sh_offset)
            continue;
        strings = &sections[table->sh_link];
        if (strings->sh_offset > size || strings->sh_size > size - strings->sh_offset)
            continue;
        symbols = (const Elf32_Sym *)(file + table->sh_offset);
        for (j = 0; j < table->sh_size / sizeof (Elf32_Sym); j++)
            if (symbols[j].st_shndx != SHN_UNDEF && symbols[j].st_name < strings->sh_size
                && strncmp((const char *)file + strings->sh_offset + symbols[j].st_name, name,
                    strings->sh_size - symbols[j].st_name) == 0)
                return symbols[j].st_value;
    }
    return 0;
}

int check_stack_guard(const char *path)
{
    const unsigned char *random = (const unsigned char *)getauxval(AT_RANDOM);
    void *function = dlsym(library, "archive_version_string");
    uintptr_t guard, expected;
    Elf32_Addr address = 0;
    struct stat st;
    Dl_info info;
    void *file;
    int fd;

    if (random == NULL) {
        fprintf(stderr, "FAIL: the kernel gave this process no random bytes (AT_RANDOM)\n");
        return 0;
    }
    if (function == NULL || dladdr(function, &info) == 0 || info.dli_fbase == NULL) {
        fprintf(stderr, "FAIL: dladdr does not say where %s is loaded\n", path);
        return 0;
    }
    if ((fd = open(path, O_RDONLY)) < 0 || fstat(fd, &st) != 0) {
        fprintf(stderr, "FAIL: %s: %s\n", path, strerror(errno));
        if (fd >= 0)
            close(fd);
        return 0;
    }
    file = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (file == MAP_FAILED) {
        fprintf(stderr, "FAIL: mmap %s: %s\n", path, strerror(errno));
        return 0;
    }
    address = defined_symbol(file, (size_t)st.st_size, "__stack_chk_guard");
    munmap(file, (size_t)st.st_size);
    if (address == 0) {
        fprintf(stderr, "FAIL: %s defines no __stack_chk_guard of its own (linux-arm-stack-guard.c)\n", path);
        return 0;
    }

    memcpy(&guard, (const unsigned char *)info.dli_fbase + address, sizeof guard);
    memcpy(&expected, random, sizeof expected);
    expected &= ~(uintptr_t)0xff;
    if (guard != expected) {
        fprintf(stderr, "FAIL: the library's stack protector guard is not seeded from this process's"
            " random bytes (AT_RANDOM), as linux-arm-stack-guard.c seeds it\n");
        return 0;
    }
    printf("OK: the library's stack protector guard holds this process's random bytes (AT_RANDOM)\n");
    return 1;
}
#else
/* The library uses its C library's stack protector guard */
int check_stack_guard(const char *path)
{
    (void)path;
    return 1;
}
#endif
