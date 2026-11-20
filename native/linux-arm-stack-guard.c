/*
 * The stack protector's guard of the 32-bit ARM Linux libraries, linux-arm and
 * linux-musl-arm: link_libarchive (build-linux-common.sh) links this file into those two.
 *
 * On 32-bit ARM, code compiled with -fstack-protector-strong checks each protected frame
 * against the global __stack_chk_guard. glibc and musl each define one, and seed it at
 * start-up from the random bytes that the kernel gives every process (AT_RANDOM). zig's
 * compiler-rt defines one too, weakly and as a constant, in its one object, beside the
 * __aeabi_* helpers (integer division and the like) that the library needs, and so links
 * in. In a link, a definition in an object, even a weak one, takes the place of a shared
 * library's, so every frame of the library would be checked against a constant that
 * anyone can know.
 *
 * This file defines __stack_chk_guard strongly, so that compiler-rt's gives way, and
 * hidden, so that it is the library's alone. A constructor seeds it from the process's
 * AT_RANDOM bytes, the ones that glibc and musl seed theirs from, with its lowest byte
 * cleared as glibc clears it, so that a string written past a buffer ends before it can
 * reproduce the guard. With the first priority that programs may use, it runs before any
 * other code of the library, and it has no stack protector itself, as its own frame would
 * otherwise be checked against the guard that it changes. Everything else of the stack
 * protector stays compiler-rt's: its __stack_chk_fail stops the process at once.
 * verify_stack_guard (build-linux-common.sh) checks the linked library.
 */
#include <stdint.h>
#include <string.h>
#include <sys/auxv.h>

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
