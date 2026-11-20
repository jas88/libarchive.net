#!/bin/bash
# Shared helpers for the Linux native library builds (build-linux*.sh)
# Source this file in those scripts, after build-config.sh
#
# These live here rather than in build-config.sh because that file is also sourced
# by build-windows.sh under plain sh, and the functions below need bash (here-strings,
# indirect expansion, arrays).

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

# Build and install a static, position-independent libxml2 for libarchive's XAR reader and
# writer, which use its xmlReader and xmlWriter interfaces and nothing else of it.
# download_all_libraries must have unpacked it in the current directory, which must be the
# build directory, and zlib must already be installed in $PREFIX. Installs libxml2.a, its
# headers and libxml-2.0.pc into $PREFIX, where libarchive's configure finds it with
# pkg-config (see export_cross_build_tools in build-config.sh).
# Usage: build_libxml2 [configure option...]
# The options are the target's own: its --cache-file, and --build and --host for a cross build.
# Uses CFLAGS, PREFIX, NCPU and LIBXML2_VERSION. Each step ends in "|| return" (see build_mbedtls).
build_libxml2() {
    local var
    for var in PREFIX NCPU LIBXML2_VERSION; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: build_libxml2 needs ${var} to be set"
            return 1
        fi
    done

    echo "Building libxml2 ${LIBXML2_VERSION}..."
    cd "libxml2-${LIBXML2_VERSION}" || return
    # The release's own configure, as on Windows, so the Linux builds need no autotools
    # (macOS runs autogen.sh). --with-zlib names where zlib was installed, as on macOS and
    # Windows; libxml2 has had no lzma support (and no --with-lzma option) since 2.15.0.
    # The --without options leave out what libarchive does not use: the Python bindings, and
    # the C14N, catalog, debug, HTML, module, pattern, regexp, RELAX NG, schema, XInclude,
    # XPath and XPointer support. The xmlReader and xmlWriter interfaces need the push parser
    # and the output code, which stay in.
    # ac_cv_have_decl_getentropy=yes: libxml2 seeds the random numbers that protect its hash
    # tables from crafted collisions with getentropy if configure finds it declared, and with
    # the time and some addresses if not. Its check includes only <sys/random.h>, and musl
    # declares getentropy in <unistd.h> (as POSIX 2024 does), which is the other header that
    # dict.c includes for it.
    ./configure "$@" --prefix="$PREFIX" --enable-silent-rules --disable-shared --enable-static --with-pic \
        --without-python --with-zlib="$PREFIX" \
        --without-c14n --without-catalog --without-debug --without-html --without-modules \
        --without-pattern --without-regexps --without-relaxng --without-schemas \
        --without-xinclude --without-xpath --without-xptr \
        ac_cv_have_decl_getentropy=yes || return
    # A check that libxml2 renames would ignore that answer without saying so
    if ! awk '$1 == "#define" && $2 == "HAVE_DECL_GETENTROPY" && $3 == "1" {found = 1} END {exit !found}' config.h; then
        echo "ERROR: libxml2's config.h does not define HAVE_DECL_GETENTROPY to 1, so libxml2 would"
        echo "seed its hash tables from the time instead of getentropy (see build_libxml2)"
        return 1
    fi
    # Only the library, its headers and its pkg-config file: "make install" would also build
    # and install xmllint, xml2-config and a CMake package file, which nothing here uses.
    # -fvisibility=hidden, as for mbedTLS: libarchive.so does not export libxml2's functions,
    # so another libxml2 in the process can neither clash with them nor take their place. The
    # libarchive code linked with them still calls them.
    make -sj"$NCPU" install-libLTLIBRARIES install-pkgconfigDATA CFLAGS="$CFLAGS -fvisibility=hidden" || return
    make -sC include install || return
    cd .. || return
}

# Link the final libarchive.so: all of libarchive.a, then the other static libraries,
# the toolchain's own libgcc.a and musl's static libc.a, and nothing else (-nostdlib).
# The result needs no other library at run time (it has no DT_NEEDED entry), so the
# same file loads in glibc and musl processes.
#
# The archives after libarchive.a are linked as one group (--start-group ...
# --end-group), which makes ld scan them repeatedly until no more symbols resolve.
# A single left-to-right pass cannot be relied on: musl's libc.a calls libgcc's helper
# routines (integer division on arm, 128-bit floating point on arm64), and libgcc.a
# calls back into libc.a (on arm, its division-by-zero handler calls raise).
#
# -z defs makes the link fail on any strong (non-weak) unresolved symbol. Without it ld
# leaves the symbol to be found when the library is loaded, and since the toolchain
# links with -z now (BIND_NOW) and the library names no other library, the library then
# fails to load in any process that does not happen to define that symbol itself. Weak
# references, such as zstd's optional ZSTD_trace_* hooks, are still allowed: they
# resolve to null when nothing defines them.
#
# linux-pthread-init.c is linked in too, with -Wl,--wrap=pthread_mutex_init and
# -Wl,--wrap=pthread_cond_init, so that every call of either in the library goes to it
# (see that file). The link also writes a map with a cross reference table,
# <library>.map, from which verify_libc_imports finds the C library symbols that the
# library's code uses.
#
# To link another static library, add it to the archive list below.
# Usage: link_libarchive <library>
# Uses CC, PREFIX, SCRIPT_DIR and TOOLCHAIN_SYSROOT.
link_libarchive() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: link_libarchive needs the library to create as its argument"
        return 1
    fi
    local var
    for var in CC PREFIX SCRIPT_DIR TOOLCHAIN_SYSROOT; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: link_libarchive needs ${var} to be set"
            return 1
        fi
    done

    # CC can start with a compiler launcher ("sccache .../x86_64-linux-gcc")
    local -a cc
    read -r -a cc <<< "$CC"
    local libgcc
    libgcc=$("${cc[@]}" -print-libgcc-file-name) || return
    # gcc prints just "libgcc.a" when it cannot find the file
    if [ ! -f "$libgcc" ]; then
        echo "ERROR: ${CC} -print-libgcc-file-name gave \"${libgcc}\", which is not a file"
        return 1
    fi

    local -a archives=(
        # The libraries libarchive.a uses. Its XAR reader and writer use libxml2.a, which
        # uses libz.a and the C library in turn.
        "$PREFIX/lib/libxml2.a"
        "$PREFIX/lib/libbz2.a"
        "$PREFIX/lib/libz.a"
        "$PREFIX/lib/liblzma.a"
        "$PREFIX/lib/liblzo2.a"
        "$PREFIX/lib/libzstd.a"
        "$PREFIX/lib/liblz4.a"
        "$PREFIX/lib/libmbedcrypto.a"
        # Compiler support routines and the C library, used by all of the above
        "$libgcc"
        "$TOOLCHAIN_SYSROOT/lib/libc.a"
    )
    local pthread_init=linux-pthread-init.o
    "${cc[@]}" -c -O2 -fPIC -Wall -Wextra -o "$pthread_init" "${SCRIPT_DIR}/linux-pthread-init.c" || return
    local -a link=(
        "${cc[@]}" -shared -nostdlib -o "$lib" "-Wl,-z,defs"
        "-Wl,--wrap=pthread_mutex_init" "-Wl,--wrap=pthread_cond_init"
        "-Wl,-Map=${lib}.map" "-Wl,--cref"
        "$pthread_init"
        "-Wl,--whole-archive" "$PREFIX/lib/libarchive.a" "-Wl,--no-whole-archive"
        "-Wl,--start-group" "${archives[@]}" "-Wl,--end-group"
    )
    echo "Linking ${lib}:"
    echo "${link[*]}"
    "${link[@]}"
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
    # to the stub cryptor), and a library with unresolved symbols can never be loaded
    # (link_libarchive's -z defs now refuses to link one; this check names the cause).
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

# Fail the build unless libarchive's configure enabled its XAR reader and writer. They need
# an XML library that can both read and write (libxml2), zlib, and MD5 and SHA-1 (from
# mbedTLS here, as musl has neither). If any of them is missing, libarchive compiles both
# as stubs that fail with "Xar not supported on this platform" (the #if at the top of
# archive_read_support_format_xar.c and archive_write_set_format_xar.c), and configure does
# not fail: given --with-xml2, it still goes on without libxml2 if pkg-config does not find
# it. Run it after libarchive's configure, before make.
# Usage: verify_xar_configured <libarchive's config.h>
verify_xar_configured() {
    local config="${1:-}"
    if [ -z "$config" ]; then
        echo "ERROR: verify_xar_configured needs libarchive's config.h as its argument"
        return 1
    fi
    if [ ! -f "$config" ]; then
        echo "ERROR: ${config} does not exist: run libarchive's configure first"
        return 1
    fi

    echo "=== Verifying that libarchive's configure enabled XAR ==="
    # libxml2, linkable and with the headers of its reader and writer; zlib's header; and
    # mbedTLS's MD5 and SHA-1, from which archive_digest_private.h defines ARCHIVE_HAS_MD5
    # and ARCHIVE_HAS_SHA1. configure writes "#define <macro> 1" for each one it found and
    # "/* #undef <macro> */" for each one it did not.
    local macro
    local -a missing=()
    for macro in HAVE_LIBXML2 HAVE_LIBXML_XMLREADER_H HAVE_LIBXML_XMLWRITER_H HAVE_ZLIB_H \
        ARCHIVE_CRYPTO_MD5_MBEDTLS ARCHIVE_CRYPTO_SHA1_MBEDTLS; do
        if ! awk -v macro="$macro" '$1 == "#define" && $2 == macro {found = 1} END {exit !found}' "$config"; then
            missing+=("$macro")
        fi
    done
    if [ ${#missing[@]} -gt 0 ]; then
        echo "ERROR: ${config} does not define these, so libarchive would have no XAR reader or writer:"
        printf '  %s\n' "${missing[@]}"
        echo "See libarchive's configure output above and its config.log. It finds libxml2 with"
        echo "pkg-config, in PKG_CONFIG_LIBDIR (${PKG_CONFIG_LIBDIR:-unset}), and MD5 and SHA-1 in mbedTLS."
        return 1
    fi
    echo "libxml2's reader and writer, zlib, and mbedTLS's MD5 and SHA-1 found"
}

# Fail the build unless a linked library has libarchive's XAR reader and writer, and the
# libxml2 code that they call, none of it left undefined.
# Usage: verify_xar_support <library>
# Uses AR to find the toolchain's nm and strings.
verify_xar_support() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_xar_support needs the library to check as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_xar_support needs AR to be set"
        return 1
    fi

    echo "=== Verifying XAR support ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local nm="${AR%ar}nm" strings="${AR%ar}strings"
    local symbols undefined text
    if ! symbols=$("$nm" "$lib") || ! undefined=$("$nm" -D --undefined-only "$lib"); then
        echo "ERROR: could not read the symbols of ${lib} with ${nm}"
        return 1
    fi
    if ! text=$("$strings" -a "$lib"); then
        echo "ERROR: could not read the strings of ${lib} with ${strings}"
        return 1
    fi

    local failed=0 function unresolved
    # The libxml2 functions with which the XAR reader and writer start. They are local (t),
    # as libxml2 is compiled with -fvisibility=hidden (see build_libxml2).
    for function in xmlReaderForIO xmlNewTextWriterMemory; do
        if ! awk -v name="$function" 'NF >= 3 && ($2 == "T" || $2 == "t") && $3 == name {found = 1} END {exit !found}' <<< "$symbols"; then
            echo "ERROR: ${function} is not defined in ${lib}: libxml2 was not linked in."
            failed=1
        fi
    done
    # libxml2's names begin with "xml" ("html" for its HTML parser). Weak references count
    # too: nothing outside the library should supply any part of libxml2.
    unresolved=$(awk 'NF >= 2 && $NF ~ /^(xml|html)/ {print "  " $NF}' <<< "$undefined")
    if [ -n "$unresolved" ]; then
        echo "ERROR: ${lib} leaves these libxml2 symbols undefined:"
        echo "$unresolved"
        failed=1
    fi
    # The stubs' message (see verify_xar_configured) is in no other libarchive code
    if awk 'index($0, "Xar not supported on this platform") {found = 1} END {exit !found}' <<< "$text"; then
        echo "ERROR: ${lib} contains the message of libarchive's XAR stubs, \"Xar not supported on this platform\"."
        failed=1
    fi
    if [ "$failed" -ne 0 ]; then
        return 1
    fi
    echo "XAR reader and writer present, with libxml2 linked in and none of it undefined"
}

# Fail the build unless the code linked into a library uses only the C library symbols that
# linux-libc-imports.txt (in this directory) names, and calls the C library's
# pthread_mutex_init and pthread_cond_init only from linux-pthread-init.c. In a glibc process
# those calls go to glibc's functions, which must agree with musl's headers on what they read
# and write (see linux-libc-imports.txt), so a new one fails the build until it has been
# checked and listed.
# The symbols come from the cross reference table of the map that link_libarchive writes,
# <library>.map: each symbol that musl's libc.a defines and that an object other than the
# members of libc.a and libgcc.a refers to.
# Usage: verify_libc_imports <library>
# Uses AR to find the toolchain's nm, and SCRIPT_DIR.
verify_libc_imports() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_libc_imports needs the library to check as its argument"
        return 1
    fi
    local var
    for var in AR SCRIPT_DIR; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: verify_libc_imports needs ${var} to be set"
            return 1
        fi
    done
    local map="${lib}.map" list="${SCRIPT_DIR}/linux-libc-imports.txt"
    if [ ! -f "$map" ]; then
        echo "ERROR: ${map} does not exist: link ${lib} with link_libarchive, which writes it"
        return 1
    fi
    if [ ! -f "$list" ]; then
        echo "ERROR: ${list} does not exist"
        return 1
    fi

    echo "=== Verifying the C library symbols that ${lib} uses ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local nm="${AR%ar}nm" undefined imports unlisted direct
    if ! undefined=$("$nm" -D --undefined-only "$lib"); then
        echo "ERROR: could not read the dynamic symbols of ${lib} with ${nm}"
        return 1
    fi
    # One line per symbol: the symbol, a tab, then the objects that refer to it. In the
    # table, each entry is the symbol at the start of a line, then the files that define it
    # and those that refer to it, one per line from column 51 (from 2 columns after the
    # symbol on its own line, when the symbol is longer than 49 characters). The library's
    # undefined (weak) symbols are left out: nothing defines them, so their entries list
    # references only.
    imports=$(awk -v undefined=" $(awk 'NF >= 2 {printf "%s ", $NF}' <<< "$undefined")" '
        function in_archive(file, name) { return index(file, name ".a(") == 1 || index(file, "/" name ".a(") > 0 }
        function flush() {
            if (sym != "" && in_archive(def, "libc") && refs != "" && index(undefined, " " sym " ") == 0)
                print sym "\t" refs
            sym = ""; def = ""; refs = ""
        }
        /^Cross Reference Table/ { table = 1; next }
        !table || /^Symbol / || /^$/ { next }
        /^[^ ]/ {
            flush()
            sym = $1
            def = (length(sym) < 50) ? substr($0, 51) : substr($0, length(sym) + 2)
            next
        }
        {
            file = substr($0, 51)
            if (!in_archive(file, "libc") && !in_archive(file, "libgcc")) {
                sub(/^.*\//, "", file)
                refs = refs (refs == "" ? "" : " ") file
            }
        }
        END { flush() }' "$map")
    if [ -z "$imports" ]; then
        echo "ERROR: the cross reference table of ${map} names no C library symbol that ${lib} uses"
        return 1
    fi

    local failed=0
    unlisted=$(awk 'NR == FNR { sub(/#.*/, ""); if (NF) listed[$1] = 1; next }
        !($1 in listed) { print "  " $1 ", used by " substr($0, index($0, "\t") + 1) }' "$list" - <<< "$imports")
    if [ -n "$unlisted" ]; then
        echo "ERROR: ${lib} uses these C library symbols, which ${list##*/} does not list:"
        echo "$unlisted"
        echo "In a glibc process glibc's functions run in their place. Check, for x64, arm64 and arm,"
        echo "that glibc's read and write what musl's headers lay out, then add them to the list."
        failed=1
    fi
    direct=$(awk '$1 == "pthread_mutex_init" || $1 == "pthread_cond_init" {
        n = split(substr($0, index($0, "\t") + 1), refs, " ")
        for (i = 1; i <= n; i++) if (refs[i] != "linux-pthread-init.o") print "  " $1 ", used by " refs[i] }' <<< "$imports")
    if [ -n "$direct" ]; then
        echo "ERROR: ${lib} calls the C library's pthread_mutex_init or pthread_cond_init other than from"
        echo "linux-pthread-init.c, so that link_libarchive's -Wl,--wrap did not take effect:"
        echo "$direct"
        failed=1
    fi
    if [ "$failed" -ne 0 ]; then
        return 1
    fi
    echo "$(wc -l <<< "$imports" | tr -d ' ') C library symbols used, all of them listed; pthread_mutex_init and pthread_cond_init only through linux-pthread-init.c"
}

# Fail the build unless a linked library is self-contained: no DT_NEEDED entry (no other
# library it needs at run time) and no strong undefined dynamic symbol (one that the
# process loading it would have to supply). Weak undefined symbols are allowed, and
# listed: they resolve to null when nothing defines them.
# link_libarchive's -z defs already stops a link that leaves a symbol unresolved; this
# checks the file that is shipped, however it was produced.
# Usage: verify_self_contained <library>
# Uses AR to find the toolchain's nm and readelf.
verify_self_contained() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_self_contained needs the library to check as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_self_contained needs AR to be set"
        return 1
    fi

    echo "=== Verifying that ${lib} is self-contained ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local nm="${AR%ar}nm" readelf="${AR%ar}readelf"
    local dynamic undefined
    if ! dynamic=$("$readelf" -d "$lib"); then
        echo "ERROR: could not read the dynamic section of ${lib} with ${readelf}"
        return 1
    fi
    if ! undefined=$("$nm" -D --undefined-only "$lib"); then
        echo "ERROR: could not read the dynamic symbols of ${lib} with ${nm}"
        return 1
    fi

    # Each list holds one indented entry per line, ready to print
    local needed strong weak failed=0
    needed=$(awk '/\(NEEDED\)/ {print "  " $NF}' <<< "$dynamic")
    # nm prints each undefined symbol as "<type> <name>": w and v are weak references,
    # U (or anything else) a strong one
    strong=$(awk 'NF >= 2 && $(NF-1) != "w" && $(NF-1) != "v" {print "  " $NF}' <<< "$undefined")
    weak=$(awk 'NF >= 2 && ($(NF-1) == "w" || $(NF-1) == "v") {print "  " $NF}' <<< "$undefined")
    if [ -n "$needed" ]; then
        echo "ERROR: ${lib} needs these other libraries at run time (DT_NEEDED):"
        echo "$needed"
        failed=1
    fi
    if [ -n "$strong" ]; then
        echo "ERROR: ${lib} leaves these symbols undefined, so it fails to load in any process that does not define them:"
        echo "$strong"
        failed=1
    fi
    if [ "$failed" -ne 0 ]; then
        return 1
    fi
    echo "No DT_NEEDED entries and no strong undefined dynamic symbols"
    if [ -n "$weak" ]; then
        echo "Weak undefined dynamic symbols (allowed, null when nothing defines them):"
        echo "$weak"
    fi
}

# Load a freshly linked library in a process that contains nothing but musl: the load
# test program (linux-loadtest.c, in this directory) built with the target toolchain as a
# dynamically linked musl executable, run by the musl dynamic loader from the toolchain's
# sysroot. No glibc and no libgcc_s is present, so nothing else can supply a symbol the
# library lacks, and it then fails to load. Runs natively (x64), or under the named QEMU
# user-mode emulator for another architecture. If that emulator is not installed, the
# test fails the build under GitHub Actions and is skipped with a warning elsewhere.
# Usage: test_load_musl <library> [qemu-binary]
# Uses CC, AR to find the toolchain's readelf, and SCRIPT_DIR.
test_load_musl() {
    local lib="${1:-}" qemu="${2:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: test_load_musl needs the library to load as its argument"
        return 1
    fi
    local var
    for var in CC AR SCRIPT_DIR; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: test_load_musl needs ${var} to be set"
            return 1
        fi
    done

    echo "=== Load test: ${lib} in a process that contains only musl ==="
    local -a run=()
    if [ -n "$qemu" ]; then
        local emulator
        if ! emulator=$(command -v "$qemu" || command -v "${qemu}-static"); then
            if [ "${GITHUB_ACTIONS:-}" = true ]; then
                echo "ERROR: ${qemu} (QEMU user-mode emulation) is not installed, so ${lib} cannot be load tested."
                return 1
            fi
            echo "WARNING: ${qemu} (QEMU user-mode emulation) is not installed: SKIPPING the load test of ${lib}."
            echo "WARNING: nothing has shown that ${lib} loads. Install QEMU user-mode emulation (Debian/Ubuntu: qemu-user) to run the test."
            return 0
        fi
        run=("$emulator")
    fi

    # CC can start with a compiler launcher ("sccache .../arm-linux-gcc")
    local -a cc
    read -r -a cc <<< "$CC"
    local readelf="${AR%ar}readelf"
    local sysroot dynamic headers needed interp loader
    sysroot=$("${cc[@]}" -print-sysroot) || return
    if [ -z "$sysroot" ] || [ ! -d "$sysroot" ]; then
        echo "ERROR: ${CC} -print-sysroot gave \"${sysroot}\", which is not a directory"
        return 1
    fi

    # -static-libgcc: the program must not depend on libgcc_s, which could supply a
    # helper routine that the library lacks
    "${cc[@]}" -O2 -Wall -Wextra -static-libgcc -o loadtest-musl "${SCRIPT_DIR}/linux-loadtest.c" || return
    if ! dynamic=$("$readelf" -d loadtest-musl) || ! headers=$("$readelf" -l loadtest-musl); then
        echo "ERROR: could not read the load test program with ${readelf}"
        return 1
    fi
    needed=$(awk '/\(NEEDED\)/ {print $NF}' <<< "$dynamic")
    if [ "$needed" != "[libc.so]" ]; then
        echo "ERROR: the load test program must need musl's libc.so alone, but it needs:"
        echo "${needed:-(nothing)}"
        return 1
    fi
    # musl's dynamic loader, as named in the program (PT_INTERP), inside the sysroot
    interp=$(awk '/Requesting program interpreter:/ {sub(/.*interpreter: /, ""); sub(/\]$/, ""); print}' <<< "$headers")
    loader="${sysroot}${interp}"
    if [ -z "$interp" ] || [ ! -f "$loader" ]; then
        echo "ERROR: musl's dynamic loader was not found: the program names \"${interp}\", and ${loader} does not exist"
        return 1
    fi
    if [ ${#run[@]} -gt 0 ]; then
        run+=(-L "$sysroot")
    fi
    case "$lib" in
        */*) run+=("$loader" ./loadtest-musl "$lib") ;;
        *)   run+=("$loader" ./loadtest-musl "./$lib") ;;
    esac
    echo "${run[*]}"
    if ! "${run[@]}"; then
        echo "ERROR: ${lib} failed to load or work in a process that contains only musl (see above)."
        return 1
    fi
}

# Load a freshly linked library in the load test program (linux-loadtest.c, in this
# directory) built with the host's own gcc and C library (glibc on the CI runners), as a
# .NET process on a typical Linux distribution would. Only possible for a library built
# for the host's architecture (x64).
# Usage: test_load_host <library>
# Uses SCRIPT_DIR.
test_load_host() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: test_load_host needs the library to load as its argument"
        return 1
    fi
    if [ -z "${SCRIPT_DIR:-}" ]; then
        echo "ERROR: test_load_host needs SCRIPT_DIR to be set"
        return 1
    fi

    echo "=== Load test: ${lib} in a program built with the host's gcc ==="
    # -ldl for glibc before 2.34, where dlopen is not in libc itself
    gcc -O2 -Wall -Wextra -o loadtest-host "${SCRIPT_DIR}/linux-loadtest.c" -ldl || return
    local path="$lib"
    case "$lib" in
        */*) ;;
        *) path="./$lib" ;;
    esac
    if ! ./loadtest-host "$path"; then
        echo "ERROR: ${lib} failed to load or work in a program built with the host's gcc (see above)."
        return 1
    fi
}
