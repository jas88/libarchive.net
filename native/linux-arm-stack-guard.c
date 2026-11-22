/*
 * The stack protector of the 32-bit ARM Linux libraries, linux-arm and linux-musl-arm: their
 * guard, and linux-arm's handler of a failed check. link_libarchive (build-linux-common.sh)
 * links this file into those two.
 *
 * On 32-bit ARM, code compiled with -fstack-protector-strong checks each protected frame
 * against the global __stack_chk_guard, and calls __stack_chk_fail if the frame no longer
 * holds it. glibc and musl each define both, and seed the guard at start-up from the random
 * bytes that the kernel gives every process (AT_RANDOM). zig's compiler-rt defines both too,
 * weakly, and the guard as a constant, in its one object, beside the __aeabi_* helpers
 * (integer division and the like) that the library needs, and so links in. In a link, a
 * definition in an object, even a weak one, takes the place of a shared library's, so every
 * frame of the library would be checked against a constant that anyone can know.
 *
 * This file defines __stack_chk_guard strongly, so that compiler-rt's gives way, and
 * hidden, so that it is the library's alone. A constructor seeds it from the process's
 * AT_RANDOM bytes, the ones that glibc and musl seed theirs from, with its lowest byte
 * cleared as glibc clears it, so that a string written past a buffer ends before it can
 * reproduce the guard. With the first priority that programs may use, it runs before any
 * other code of the library, and it has no stack protector itself, as its own frame would
 * otherwise be checked against the guard that it changes.
 *
 * compiler-rt's __stack_chk_fail stops the process at an undefined instruction (SIGILL),
 * with no message, as musl's own does, so linux-musl-arm keeps it. glibc's writes
 * "*** stack smashing detected ***: terminated" to stderr and aborts (SIGABRT), and in
 * linux-arm so does this file's, which it defines strongly and hidden, as it does the guard.
 * That uses nothing that the overflow can have corrupted, neither the heap nor the frames
 * above its own: the message is a constant, and syscall and abort are glibc's, which the
 * dynamic loader bound when it loaded the library (-z now). From glibc 2.27 on, abort
 * flushes no stdio stream. It makes the write system call itself, as glibc's handler does:
 * write is a cancellation point, where a thread with a cancellation request pending would
 * unwind the frames that failed their check, and not end the process. It has no stack
 * protector itself, which would call it again. verify_stack_guard and verify_stack_chk_fail
 * (build-linux-common.sh) check the linked library, and the load test compares its
 * __stack_chk_fail with the C library's (linux-loadtest-guard.c).
 */
#include <errno.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>
#include <sys/auxv.h>
#include <sys/syscall.h>
#include <unistd.h>

__attribute__((visibility("hidden"))) uintptr_t __stack_chk_guard;

__attribute__((constructor(101), no_stack_protector))
static void libarchive_net_seed_stack_guard(void)
{
    const void *random = (const void *)getauxval(AT_RANDOM);
    uintptr_t guard;

    /* Every Linux kernel since 2.6.29 gives AT_RANDOM; without it the guard stays 0 */
    if (random == NULL)
        return;
    memcpy(&guard, random, sizeof guard);
    __stack_chk_guard = guard & ~(uintptr_t)0xff;
}

#ifdef __GLIBC__
/* glibc's message, as its __stack_chk_fail writes it, through __fortify_fail */
static const char stack_smashing_message[] = "*** stack smashing detected ***: terminated\n";

__attribute__((visibility("hidden"), noreturn, no_stack_protector))
void __stack_chk_fail(void)
{
    /* One write, made again only if a signal interrupts it, as glibc's is */
    while (syscall(SYS_write, STDERR_FILENO, stack_smashing_message,
            sizeof stack_smashing_message - 1) < 0 && errno == EINTR)
        continue;
    abort();
}
#endif
