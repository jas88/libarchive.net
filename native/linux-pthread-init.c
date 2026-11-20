/*
 * Default initialisation of mutexes and condition variables, for the Linux libraries
 * (link_libarchive in build-linux-common.sh links this file into libarchive.so).
 *
 * libarchive.so contains musl, and the code in it is compiled against musl's headers, so
 * every pthread_mutex_t and pthread_cond_t in it is musl's size. In a glibc process, though,
 * the library's calls of the C library's functions go to glibc's (see linux-libc-imports.txt),
 * and glibc's pthread_mutex_init clears glibc's size of mutex: 48 bytes on arm64, where
 * musl's is 40. It would write 8 bytes past every mutex that libxml2 (and liblzma's
 * multithreaded encoder) initialises.
 *
 * The library is linked with -Wl,--wrap=pthread_mutex_init and -Wl,--wrap=pthread_cond_init,
 * so that every call of either in it comes here instead. For default attributes (a null
 * attribute pointer, which is all that libxml2 passes), the object is cleared at musl's size.
 * In musl and in glibc alike, a mutex or condition variable that is all zero bytes is one with
 * default attributes: it is what PTHREAD_MUTEX_INITIALIZER and PTHREAD_COND_INITIALIZER
 * expand to, and what each library's own initialisation leaves.
 *
 * An object initialised with attributes is left to the C library's function. That writes
 * nothing past it either: nothing in the library can make mutex attributes, as no
 * pthread_mutexattr_ function is in linux-libc-imports.txt, and glibc's condition variable is
 * musl's size, 48 bytes, on x64, arm64 and arm alike. (liblzma initialises its condition
 * variables with attributes, to use CLOCK_MONOTONIC.)
 */
#include <pthread.h>
#include <string.h>

/* The C library's functions, which -Wl,--wrap gives these names */
int __real_pthread_mutex_init(pthread_mutex_t *mutex, const pthread_mutexattr_t *attr);
int __real_pthread_cond_init(pthread_cond_t *cond, const pthread_condattr_t *attr);

/* Hidden, so that the library's own calls bind to these and nothing exports them */
__attribute__((visibility("hidden")))
int __wrap_pthread_mutex_init(pthread_mutex_t *mutex, const pthread_mutexattr_t *attr);
__attribute__((visibility("hidden")))
int __wrap_pthread_cond_init(pthread_cond_t *cond, const pthread_condattr_t *attr);

int __wrap_pthread_mutex_init(pthread_mutex_t *mutex, const pthread_mutexattr_t *attr)
{
    if (attr != NULL)
        return __real_pthread_mutex_init(mutex, attr);
    memset(mutex, 0, sizeof *mutex);
    return 0;
}

int __wrap_pthread_cond_init(pthread_cond_t *cond, const pthread_condattr_t *attr)
{
    if (attr != NULL)
        return __real_pthread_cond_init(cond, attr);
    memset(cond, 0, sizeof *cond);
    return 0;
}
