/*
 * The load test's checks of a 32-bit ARM library's stack protector (see linux-loadtest.h),
 * which linux-arm-stack-guard.c gives it. Its symbols are hidden, so the checks look them
 * up in the symbol table of the library's file, and find them where the library is loaded.
 * Elsewhere the library uses the C library's guard and handler, and check_stack_guard and
 * check_stack_chk_fail have nothing to check.
 *
 * check_stack_guard: the library's own __stack_chk_guard must hold the first bytes of this
 * process's AT_RANDOM, the random bytes that the kernel gives every process, with the
 * lowest byte cleared, as that file's constructor seeds it. zig's compiler-rt, whose
 * constant guard it replaces, cannot pass, and nor can a guard seeded any other way.
 *
 * check_stack_chk_fail: the library's __stack_chk_fail, which its code calls when a frame
 * fails its check, must end the process as the one that the C library exports does: by the
 * same signal, with the same output on stderr. Each is called in a child process of its
 * own, whose stderr is a pipe that the check reads. With glibc both abort (SIGABRT) after
 * glibc's message, which linux-arm-stack-guard.c's handler writes too. With musl both stop
 * at an undefined instruction (SIGILL) with no output: musl's handler does, and so does
 * compiler-rt's, which the musl library keeps. glibc writes its message to stderr, the
 * pipe, even in a process with a controlling terminal: glibc 2.31, 2.35, 2.36 and 2.41 were
 * seen to, and none of them reads LIBC_FATAL_STDERR_, which older glibc read, writing the
 * message to /dev/tty unless it was set.
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
#include <sys/resource.h>
#include <sys/stat.h>
#include <sys/wait.h>
#include <unistd.h>

/* The value of the symbol of that name and type (STT_*) that the ELF file of size bytes defines, or 0 */
static Elf32_Addr defined_symbol(const unsigned char *file, size_t size, const char *name, unsigned type)
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
            if (symbols[j].st_shndx != SHN_UNDEF && ELF32_ST_TYPE(symbols[j].st_info) == type
                && symbols[j].st_name < strings->sh_size
                && strncmp((const char *)file + strings->sh_offset + symbols[j].st_name, name,
                    strings->sh_size - symbols[j].st_name) == 0)
                return symbols[j].st_value;
    }
    return 0;
}

/*
 * The address in this process of the symbol of that name and type that the library's file,
 * path, defines in its symbol table; or say why there is none, and return NULL.
 */
static void *library_symbol(const char *path, const char *name, unsigned type)
{
    void *function = dlsym(library, "archive_version_string");
    Elf32_Addr value;
    struct stat st;
    Dl_info info;
    void *file;
    int fd;

    if (function == NULL || dladdr(function, &info) == 0 || info.dli_fbase == NULL) {
        fprintf(stderr, "FAIL: dladdr does not say where %s is loaded\n", path);
        return NULL;
    }
    if ((fd = open(path, O_RDONLY)) < 0 || fstat(fd, &st) != 0) {
        fprintf(stderr, "FAIL: %s: %s\n", path, strerror(errno));
        if (fd >= 0)
            close(fd);
        return NULL;
    }
    file = mmap(NULL, (size_t)st.st_size, PROT_READ, MAP_PRIVATE, fd, 0);
    close(fd);
    if (file == MAP_FAILED) {
        fprintf(stderr, "FAIL: mmap %s: %s\n", path, strerror(errno));
        return NULL;
    }
    value = defined_symbol(file, (size_t)st.st_size, name, type);
    munmap(file, (size_t)st.st_size);
    if (value == 0) {
        fprintf(stderr, "FAIL: %s defines no %s of its own in its symbol table\n", path, name);
        return NULL;
    }
    return (unsigned char *)info.dli_fbase + value;
}

int check_stack_guard(const char *path)
{
    const unsigned char *random = (const unsigned char *)getauxval(AT_RANDOM);
    uintptr_t guard, expected;
    const void *address;

    if (random == NULL) {
        fprintf(stderr, "FAIL: the kernel gave this process no random bytes (AT_RANDOM)\n");
        return 0;
    }
    /* linux-arm-stack-guard.c's */
    if ((address = library_symbol(path, "__stack_chk_guard", STT_OBJECT)) == NULL)
        return 0;

    memcpy(&guard, address, sizeof guard);
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

/* How a child process that called a __stack_chk_fail ended: its status, and its stderr */
struct ending {
    int status;
    size_t length;          /* of the whole output, of which output keeps what fits */
    char output[4096];
};

/*
 * Call handler, a __stack_chk_fail, in a child process whose stderr is a pipe, and say in
 * *ending how the child ended; or say why it could not, and return 0. The child dumps no
 * core, and nothing that this process has buffered is written again in it.
 */
static int end_child(void (*handler)(void), struct ending *ending)
{
    int fds[2], ok = 1;
    pid_t pid;

    ending->length = 0;
    if (pipe(fds) != 0) {
        fprintf(stderr, "FAIL: pipe: %s\n", strerror(errno));
        return 0;
    }
    fflush(NULL);
    if ((pid = fork()) < 0) {
        fprintf(stderr, "FAIL: fork: %s\n", strerror(errno));
        close(fds[0]);
        close(fds[1]);
        return 0;
    }
    if (pid == 0) {
        struct rlimit no_core = { 0, 0 };

        close(fds[0]);
        if (dup2(fds[1], STDERR_FILENO) < 0)
            _exit(126);
        close(fds[1]);
        setrlimit(RLIMIT_CORE, &no_core);
        handler();
        _exit(127);
    }
    close(fds[1]);
    /* Read to the end, keeping what fits */
    for (;;) {
        char chunk[512];
        ssize_t n = read(fds[0], chunk, sizeof chunk);
        size_t room = ending->length < sizeof ending->output ? sizeof ending->output - ending->length : 0;

        if (n == 0)
            break;
        if (n < 0) {
            if (errno == EINTR)
                continue;
            fprintf(stderr, "FAIL: reading the stderr of a child process: %s\n", strerror(errno));
            ok = 0;
            break;
        }
        if (room > 0)
            memcpy(ending->output + ending->length, chunk, (size_t)n < room ? (size_t)n : room);
        ending->length += (size_t)n;
    }
    close(fds[0]);
    while (waitpid(pid, &ending->status, 0) < 0) {
        if (errno != EINTR) {
            fprintf(stderr, "FAIL: waitpid: %s\n", strerror(errno));
            return 0;
        }
    }
    return ok;
}

/* How a process ended, from its status: "signal 6 (Aborted)", "exit status 1" */
static const char *ended(int status, char *text, size_t size)
{
    if (WIFSIGNALED(status))
        snprintf(text, size, "signal %d (%s)", WTERMSIG(status), strsignal(WTERMSIG(status)));
    else if (WIFEXITED(status))
        snprintf(text, size, "exit status %d", WEXITSTATUS(status));
    else
        snprintf(text, size, "status %#x", (unsigned)status);
    return text;
}

/* Print what a child process wrote to stderr, quoted, with \n and other control bytes escaped */
static void print_output(FILE *stream, const struct ending *ending)
{
    size_t i, kept = ending->length < sizeof ending->output ? ending->length : sizeof ending->output;

    if (ending->length == 0) {
        fputs("nothing", stream);
        return;
    }
    fputc('"', stream);
    for (i = 0; i < kept; i++) {
        unsigned char c = (unsigned char)ending->output[i];

        if (c == '\n')
            fputs("\\n", stream);
        else if (c == '"' || c == '\\')
            fprintf(stream, "\\%c", c);
        else if (c < 0x20 || c >= 0x7f)
            fprintf(stream, "\\x%02x", c);
        else
            fputc(c, stream);
    }
    fputc('"', stream);
    if (kept < ending->length)
        fprintf(stream, " (the first %zu of %zu bytes)", kept, ending->length);
}

int check_stack_chk_fail(const char *path)
{
    void (*libc_handler)(void), (*library_handler)(void);
    struct ending libc_end, library_end;
    char libc_text[64], library_text[64];
    const char *libc_name = "the C library";
    void *address;
    Dl_info info;

    dlerror();
    if ((address = dlsym(RTLD_DEFAULT, "__stack_chk_fail")) == NULL) {
        const char *error = dlerror();

        fprintf(stderr, "FAIL: the C library exports no __stack_chk_fail: %s\n",
            error != NULL ? error : "null address");
        return 0;
    }
    if (dladdr(address, &info) != 0 && info.dli_fname != NULL)
        libc_name = info.dli_fname;
    /* POSIX guarantees that a data pointer can hold the address of a function */
    memcpy(&libc_handler, &address, sizeof libc_handler);
    /* linux-arm-stack-guard.c's for glibc, compiler-rt's for musl */
    if ((address = library_symbol(path, "__stack_chk_fail", STT_FUNC)) == NULL)
        return 0;
    memcpy(&library_handler, &address, sizeof library_handler);

    if (!end_child(libc_handler, &libc_end) || !end_child(library_handler, &library_end))
        return 0;
    ended(libc_end.status, libc_text, sizeof libc_text);
    ended(library_end.status, library_text, sizeof library_text);
    if (!WIFSIGNALED(libc_end.status)) {
        fprintf(stderr, "FAIL: %s's __stack_chk_fail did not end the process with a signal, but with %s\n",
            libc_name, libc_text);
        return 0;
    }
    if (!WIFSIGNALED(library_end.status) || WTERMSIG(library_end.status) != WTERMSIG(libc_end.status)) {
        fprintf(stderr, "FAIL: the library's __stack_chk_fail ends the process with %s, and %s's with %s\n",
            library_text, libc_name, libc_text);
        return 0;
    }
    if (library_end.length != libc_end.length || library_end.length > sizeof library_end.output
        || memcmp(library_end.output, libc_end.output, library_end.length) != 0) {
        fprintf(stderr, "FAIL: the library's __stack_chk_fail writes ");
        print_output(stderr, &library_end);
        fprintf(stderr, " to stderr, and %s's ", libc_name);
        print_output(stderr, &libc_end);
        fputc('\n', stderr);
        return 0;
    }
    printf("OK: the library's __stack_chk_fail ends the process as %s's does, with %s, writing ",
        libc_name, libc_text);
    print_output(stdout, &libc_end);
    printf(" to stderr\n");
    return 1;
}
#else
/* The library uses its C library's stack protector guard */
int check_stack_guard(const char *path)
{
    (void)path;
    return 1;
}

/* The library uses its C library's __stack_chk_fail */
int check_stack_chk_fail(const char *path)
{
    (void)path;
    return 1;
}
#endif
