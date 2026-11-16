#!/bin/bash
# Shared helpers for the Linux native library builds (build-linux*.sh)
# Source this file in those scripts, after build-config.sh
#
# These live here rather than in build-config.sh because that file is also sourced
# by build-windows.sh under plain sh, and the functions below need bash (here-strings,
# indirect expansion).

# Download, build and install the static mbedTLS crypto library.
# Unpacks into the current directory, which must be the build directory, and
# installs libmbedcrypto.a and the mbedtls and psa headers into $PREFIX.
# Uses CC, AR, PREFIX, SCRIPT_DIR and MBEDTLS_VERSION, MBEDTLS_URL, MBEDTLS_SHA256,
# and download_library from build-config.sh.
#
# Each step ends in "|| return", which passes the failing command's own status on:
# callers may run this where set -e does not apply (e.g. after "&&" or in an "if"),
# and a failed step must never fall through to the next one.
build_mbedtls() {
    local var
    for var in CC AR PREFIX SCRIPT_DIR MBEDTLS_VERSION MBEDTLS_URL MBEDTLS_SHA256; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: build_mbedtls needs ${var} to be set"
            return 1
        fi
    done

    # mbedTLS is only used on Linux (Windows has CNG, macOS has CommonCrypto), so it is
    # not part of the shared download_all_libraries
    download_library "$MBEDTLS_URL" "mbedtls" "mbedtls-${MBEDTLS_VERSION}" "$MBEDTLS_SHA256" || return

    echo "Building mbedTLS ${MBEDTLS_VERSION}..."
    cd "mbedtls-${MBEDTLS_VERSION}" || return
    # Replace the default configuration in the source tree itself rather than pointing
    # at ours with -DMBEDTLS_CONFIG_FILE: libarchive compiles against the headers
    # installed below and must see exactly the configuration libmbedcrypto.a was built with.
    cp "${SCRIPT_DIR}/mbedtls-config.h" include/mbedtls/mbedtls_config.h || return
    # Only the crypto library is needed, so build that one target and install it by hand
    make -C library libmbedcrypto.a "CC=$CC" "AR=$AR" CFLAGS="-fPIC -O2 -fvisibility=hidden" || return
    mkdir -p "$PREFIX/lib" "$PREFIX/include" || return
    cp library/libmbedcrypto.a "$PREFIX/lib/" || return
    cp -R include/mbedtls include/psa "$PREFIX/include/" || return
    cd .. || return
}

# Verify that a linked library has the mbedTLS crypto backend, failing the build otherwise.
# Usage: verify_crypto_backend <library>
# Uses AR to find the toolchain's nm.
verify_crypto_backend() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_crypto_backend needs the library to check as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_crypto_backend needs AR to be set"
        return 1
    fi

    echo "=== Verifying crypto backend ==="
    # Fail the build if ZIP AES encryption was silently lost. libarchive's configure does
    # not treat an unusable mbedTLS as an error (a 4.x bump, for example, just falls back
    # to the stub cryptor), and a library with unresolved symbols still links but can
    # never be loaded.
    # ${AR%ar}nm replaces only the trailing "ar" of the tool name. ${AR/ar/nm} would
    # rewrite the first "ar" in the whole path instead - the one in "libarchive-linux-...".
    local nm="${AR%ar}nm"
    local symbols undefined
    if ! symbols=$("$nm" "$lib") || ! undefined=$("$nm" -D --undefined-only "$lib"); then
        echo "ERROR: could not read the symbols of ${lib} with ${nm}"
        return 1
    fi
    if ! grep -q ' [Tt] mbedtls_pkcs5_pbkdf2_hmac$' <<< "$symbols"; then
        echo "ERROR: mbedtls_pkcs5_pbkdf2_hmac is not defined in ${lib}."
        echo "libarchive was built without its mbedTLS crypto backend, so ZIP AES encryption would not work."
        return 1
    fi
    if grep -E ' (mbedtls|psa)_' <<< "$undefined"; then
        echo "ERROR: ${lib} has the unresolved mbedTLS symbols listed above and would fail to load."
        return 1
    fi
    echo "mbedTLS crypto backend present, no unresolved mbedtls_/psa_ symbols"
}
