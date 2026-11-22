/*
 * Declarations shared by the source files of the Linux load test program, which
 * build_load_test in build-linux-common.sh builds into one program:
 * linux-loadtest.c (main, and the checks that need no files),
 * linux-loadtest-files.c (archives in files), linux-loadtest-lfs32.c (glibc's file
 * functions with a 32-bit off_t) and linux-loadtest-guard.c (the stack protector of a
 * 32-bit ARM library: its guard, and its __stack_chk_fail).
 *
 * Each file defines _GNU_SOURCE and _FILE_OFFSET_BITS=64 before it includes anything, so
 * that the program's own file functions are 64-bit ones in a 32-bit glibc process too.
 */
#ifndef LINUX_LOADTEST_H
#define LINUX_LOADTEST_H

#include <stddef.h>

struct archive;
struct archive_entry;

#define ARCHIVE_OK 0
#define ARCHIVE_EOF 1
#define AE_IFREG 0100000
#define AE_IFDIR 0040000

/* The library under test, as dlopen returned it */
extern void *library;

/* Store the address of the named function in *function, or say why it is missing */
int find(const char *name, void *function, size_t size);

#define FIND(name, function) find(name, &(function), sizeof (function))

/* Each of these says what failed and returns 0, or returns 1 */
int check_file_io(void);    /* linux-loadtest-files.c */
int check_lfs32(void);      /* linux-loadtest-lfs32.c */
int check_stack_guard(const char *path);    /* linux-loadtest-guard.c; path: the library */
int check_stack_chk_fail(const char *path); /* linux-loadtest-guard.c; path: the library */

#endif
