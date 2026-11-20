/*
 * The load test's check that, in a 32-bit glibc process, the library calls none of glibc's
 * file functions with a 32-bit off_t (see linux-loadtest.h). Elsewhere check_lfs32 has
 * nothing to check.
 */
#define _GNU_SOURCE
#define _FILE_OFFSET_BITS 64
#include <dlfcn.h>
#include <fcntl.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/types.h>

#include "linux-loadtest.h"

#if defined(__GLIBC__) && !defined(__LP64__)
/*
 * A 32-bit glibc has two of each function that takes or returns a file offset, or the sizes
 * of a file system or a directory entry: one with a 32-bit off_t and 32-bit counts, under
 * the standard name, and one with 64-bit ones (lseek64 and so on). Its open, openat, fopen
 * and mkstemp open files without O_LARGEFILE, so that they cannot pass 2 GiB, and open64
 * and the others with it. The library's off_t is 64-bit, so in a glibc process it must call
 * only the 64-bit ones. For each 32-bit one, this program has a function of the same name
 * (exported when the program is linked with -rdynamic, without which check_lfs32 fails),
 * so that the library's calls of it come here, before glibc: each counts the call, and
 * passes it on, with its arguments as they are, to glibc's. The program itself calls the
 * 64-bit ones only, as it is compiled with _FILE_OFFSET_BITS=64.
 */
enum {
    LFS32_OPEN, LFS32_OPENAT, LFS32_FOPEN, LFS32_MKSTEMP, LFS32_LSEEK, LFS32_FTRUNCATE,
    LFS32_FSEEKO, LFS32_FTELLO, LFS32_READDIR, LFS32_FSTATFS, LFS32_FSTATVFS, LFS32_COUNT
};
static const char *const lfs32_names[LFS32_COUNT] = {
    "open", "openat", "fopen", "mkstemp", "lseek", "ftruncate", "fseeko", "ftello", "readdir",
    "fstatfs", "fstatvfs",
};
static const char *const lfs32_differences[LFS32_COUNT] = {
    "opens files without O_LARGEFILE", "opens files without O_LARGEFILE",
    "opens files without O_LARGEFILE", "opens files without O_LARGEFILE",
    "has a 32-bit off_t", "has a 32-bit off_t", "has a 32-bit off_t", "has a 32-bit off_t",
    "returns a struct dirent with a 32-bit d_ino and d_off",
    "fills a struct statfs with 32-bit counts", "fills a struct statvfs with 32-bit counts",
};
static unsigned lfs32_calls[LFS32_COUNT];

/*
 * Count a call of one of glibc's 32-bit functions, saying so at the first, and store the address
 * of glibc's own in *next. A call with a 64-bit offset gets the wrong arguments altogether, so the
 * first one is reported at once, in case what follows from it stops the program.
 */
static void lfs32_next(int index, void *next, size_t size)
{
    void *address;

    if (lfs32_calls[index]++ == 0)
        fprintf(stderr, "FAIL: the library called glibc's %s, which %s: it must call %s64\n",
            lfs32_names[index], lfs32_differences[index], lfs32_names[index]);
    dlerror();
    if ((address = dlsym(RTLD_NEXT, lfs32_names[index])) == NULL) {
        const char *error = dlerror();
        fprintf(stderr, "FAIL: dlsym(RTLD_NEXT, \"%s\"): %s\n", lfs32_names[index],
            error != NULL ? error : "null address");
        abort();
    }
    memcpy(next, &address, size);
}

#define LFS32_NEXT(index, next) lfs32_next(index, &(next), sizeof (next))

/* glibc's declarations of its 32-bit functions, where off_t is a long */
int lfs32_open(const char *path, int flags, ...) __asm__("open");
int lfs32_openat(int dirfd, const char *path, int flags, ...) __asm__("openat");
FILE *lfs32_fopen(const char *path, const char *mode) __asm__("fopen");
int lfs32_mkstemp(char *template_name) __asm__("mkstemp");
long lfs32_lseek(int fd, long offset, int whence) __asm__("lseek");
int lfs32_ftruncate(int fd, long length) __asm__("ftruncate");
int lfs32_fseeko(FILE *stream, long offset, int whence) __asm__("fseeko");
long lfs32_ftello(FILE *stream) __asm__("ftello");
void *lfs32_readdir(void *dir) __asm__("readdir");
int lfs32_fstatfs(int fd, void *buf) __asm__("fstatfs");
int lfs32_fstatvfs(int fd, void *buf) __asm__("fstatvfs");

/* The mode argument of open or openat, which is there only to create a file */
#define LFS32_MODE(flags, last, mode) do { \
        if (((flags) & O_CREAT) != 0 || ((flags) & O_TMPFILE) == O_TMPFILE) { \
            va_list ap; \
            va_start(ap, last); \
            (mode) = va_arg(ap, mode_t); \
            va_end(ap); \
        } \
    } while (0)

int lfs32_open(const char *path, int flags, ...)
{
    int (*next)(const char *, int, ...);
    mode_t mode = 0;

    LFS32_MODE(flags, flags, mode);
    LFS32_NEXT(LFS32_OPEN, next);
    return next(path, flags, mode);
}

int lfs32_openat(int dirfd, const char *path, int flags, ...)
{
    int (*next)(int, const char *, int, ...);
    mode_t mode = 0;

    LFS32_MODE(flags, flags, mode);
    LFS32_NEXT(LFS32_OPENAT, next);
    return next(dirfd, path, flags, mode);
}

FILE *lfs32_fopen(const char *path, const char *mode)
{
    FILE *(*next)(const char *, const char *);

    LFS32_NEXT(LFS32_FOPEN, next);
    return next(path, mode);
}

int lfs32_mkstemp(char *template_name)
{
    int (*next)(char *);

    LFS32_NEXT(LFS32_MKSTEMP, next);
    return next(template_name);
}

long lfs32_lseek(int fd, long offset, int whence)
{
    long (*next)(int, long, int);

    LFS32_NEXT(LFS32_LSEEK, next);
    return next(fd, offset, whence);
}

int lfs32_ftruncate(int fd, long length)
{
    int (*next)(int, long);

    LFS32_NEXT(LFS32_FTRUNCATE, next);
    return next(fd, length);
}

int lfs32_fseeko(FILE *stream, long offset, int whence)
{
    int (*next)(FILE *, long, int);

    LFS32_NEXT(LFS32_FSEEKO, next);
    return next(stream, offset, whence);
}

long lfs32_ftello(FILE *stream)
{
    long (*next)(FILE *);

    LFS32_NEXT(LFS32_FTELLO, next);
    return next(stream);
}

void *lfs32_readdir(void *dir)
{
    void *(*next)(void *);

    LFS32_NEXT(LFS32_READDIR, next);
    return next(dir);
}

int lfs32_fstatfs(int fd, void *buf)
{
    int (*next)(int, void *);

    LFS32_NEXT(LFS32_FSTATFS, next);
    return next(fd, buf);
}

int lfs32_fstatvfs(int fd, void *buf)
{
    int (*next)(int, void *);

    LFS32_NEXT(LFS32_FSTATVFS, next);
    return next(fd, buf);
}

/* Fail if the library called any of glibc's 32-bit functions, or if its calls could not be seen */
int check_lfs32(void)
{
    long (*own_lseek)(int, long, int) = lfs32_lseek;
    void *own;
    size_t i;
    int ok = 1;

    memcpy(&own, &own_lseek, sizeof own);
    if (dlsym(RTLD_DEFAULT, "lseek") != own) {
        fprintf(stderr, "FAIL: lseek is not this program's own, so the library's calls of glibc's 32-bit"
            " functions cannot be seen: link the program with -rdynamic\n");
        return 0;
    }
    for (i = 0; i < LFS32_COUNT; i++)
        if (lfs32_calls[i] != 0) {
            fprintf(stderr, "FAIL: the library called glibc's %s %u times in all\n", lfs32_names[i], lfs32_calls[i]);
            ok = 0;
        }
    if (ok)
        printf("OK: the library called none of glibc's file functions with a 32-bit off_t, only their 64-bit versions\n");
    return ok;
}
#else
/* off_t is 64-bit in every file function of this C library */
int check_lfs32(void)
{
    return 1;
}
#endif
