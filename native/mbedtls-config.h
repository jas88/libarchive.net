/*
 * Minimal Mbed TLS configuration for the Linux builds of libarchive.
 *
 * The build-linux*.sh scripts copy this file over include/mbedtls/mbedtls_config.h
 * before building libmbedcrypto.a, and install that same copy for libarchive to
 * compile against, so the library and its only consumer always agree on the
 * configuration.
 *
 * It is deliberately minimal: only the modules libarchive calls are enabled.
 *   - AES block cipher, PBKDF2-HMAC-SHA1 and HMAC-SHA1 (through the MD layer) for
 *     ZIP AES encryption: archive_cryptor.c, archive_hmac.c
 *   - MD5, RIPEMD-160, SHA-1, SHA-256, SHA-384 and SHA-512: archive_digest.c
 *   - the version string: archive_version_details.c
 *
 * Everything else stays off on purpose. libarchive.so is linked with -nostdlib
 * against a static musl libc and has no NEEDED entries, so one unresolved symbol
 * makes it unloadable. Nothing that would need an entropy source, the PSA crypto
 * core, threads, time, file or network I/O is enabled, nor any CPU-specific
 * acceleration (AES-NI, ARMv8 crypto extensions): the same portable C builds for
 * x64, arm and arm64, and no unused TLS, X.509 or public-key code is linked in.
 *
 * Do not define MBEDTLS_DEPRECATED_REMOVED: libarchive calls
 * mbedtls_pkcs5_pbkdf2_hmac(), which Mbed TLS 3.x keeps only as a deprecated function.
 */

#define MBEDTLS_AES_C
#define MBEDTLS_MD_C
#define MBEDTLS_MD5_C
#define MBEDTLS_RIPEMD160_C
#define MBEDTLS_SHA1_C
#define MBEDTLS_SHA256_C
#define MBEDTLS_SHA384_C
#define MBEDTLS_SHA512_C
#define MBEDTLS_PKCS5_C
#define MBEDTLS_VERSION_C
