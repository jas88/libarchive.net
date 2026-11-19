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
# To link another static library, add it to the archive list below.
# Usage: link_libarchive <library>
# Uses CC, PREFIX and TOOLCHAIN_SYSROOT.
link_libarchive() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: link_libarchive needs the library to create as its argument"
        return 1
    fi
    local var
    for var in CC PREFIX TOOLCHAIN_SYSROOT; do
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
        # The libraries libarchive.a uses
        "$PREFIX/lib/libbz2.a"
        "$PREFIX/lib/libz.a"
        "$PREFIX/lib/libxml2.a"
        "$PREFIX/lib/liblzma.a"
        "$PREFIX/lib/liblzo2.a"
        "$PREFIX/lib/libzstd.a"
        "$PREFIX/lib/liblz4.a"
        "$PREFIX/lib/libmbedcrypto.a"
        # Compiler support routines and the C library, used by all of the above
        "$libgcc"
        "$TOOLCHAIN_SYSROOT/lib/libc.a"
    )
    local -a link=(
        "${cc[@]}" -shared -nostdlib -o "$lib" "-Wl,-z,defs"
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

# Write the C source of the load test program run by test_load_musl and test_load_host.
# Besides loading the library and reading an empty archive, it fails unless names are
# UTF-8 in the "C" locale, which it never leaves, as a .NET process never does: the
# libarchive patch is in the library (see "The libarchive patch" in BUILD.md).
# Usage: write_load_test_source <file>
write_load_test_source() {
    if [ -z "${1:-}" ]; then
        echo "ERROR: write_load_test_source needs the file to write as its argument"
        return 1
    fi
    cat > "$1" <<'EOF'
/*
 * Load test for a freshly linked libarchive.so, from build-linux-common.sh.
 * Usage: loadtest <path to libarchive.so>
 * Exits non-zero unless the library loads with every symbol bound at once
 * (RTLD_NOW), a little of its API works, and names are UTF-8 in the "C" locale.
 */
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/types.h>
#include <wchar.h>

struct archive;
struct archive_entry;

#define ARCHIVE_OK 0
#define ARCHIVE_EOF 1
#define AE_IFREG 0100000

static void *library;

/* Store the address of the named function in *function, or say why it is missing */
static int find(const char *name, void *function, size_t size)
{
    void *address;

    dlerror();
    address = dlsym(library, name);
    if (address == NULL) {
        const char *error = dlerror();
        fprintf(stderr, "FAIL: %s: %s\n", name, error != NULL ? error : "null address");
        return 0;
    }
    /* POSIX guarantees that a data pointer can hold the address of a function */
    memcpy(function, &address, size);
    return 1;
}

#define FIND(name, function) find(name, &(function), sizeof (function))

/* "Größe.txt", composed (NFC), as UTF-8 and as Unicode code points */
static const char name_utf8[] = "Gr\xc3\xb6\xc3\x9f" "e.txt";
static const wchar_t name_wcs[] = { 'G', 'r', 0xF6, 0xDF, 'e', '.', 't', 'x', 't', 0 };

/*
 * Fail unless narrow strings are UTF-8: narrow to wide and back, and a zip entry
 * that keeps the name's UTF-8 bytes, with the UTF-8 flag (general purpose bit 11),
 * and reads back unchanged.
 */
static int check_utf8_names(void)
{
    struct archive_entry *(*entry_new)(void);
    void (*entry_free)(struct archive_entry *);
    void (*set_pathname)(struct archive_entry *, const char *);
    const char *(*pathname)(struct archive_entry *);
    const wchar_t *(*pathname_w)(struct archive_entry *);
    void (*copy_pathname_w)(struct archive_entry *, const wchar_t *);
    void (*set_filetype)(struct archive_entry *, unsigned int);
    void (*set_perm)(struct archive_entry *, mode_t);
    void (*set_size)(struct archive_entry *, int64_t);
    struct archive *(*write_new)(void);
    int (*set_format_zip)(struct archive *);
    int (*write_open_memory)(struct archive *, void *, size_t, size_t *);
    int (*write_header)(struct archive *, struct archive_entry *);
    int (*write_close)(struct archive *);
    int (*write_free)(struct archive *);
    struct archive *(*read_new)(void);
    int (*support_format_zip)(struct archive *);
    int (*open_memory)(struct archive *, const void *, size_t);
    int (*next_header)(struct archive *, struct archive_entry **);
    const char *(*error_string)(struct archive *);
    int (*read_free)(struct archive *);
    static unsigned char buf[65536];
    size_t used = 0, len = sizeof name_utf8 - 1;
    struct archive *a;
    struct archive_entry *entry;
    const wchar_t *w;
    const char *p;
    int ok = 1;

    if (!FIND("archive_entry_new", entry_new) || !FIND("archive_entry_free", entry_free)
        || !FIND("archive_entry_set_pathname", set_pathname)
        || !FIND("archive_entry_pathname", pathname)
        || !FIND("archive_entry_pathname_w", pathname_w)
        || !FIND("archive_entry_copy_pathname_w", copy_pathname_w)
        || !FIND("archive_entry_set_filetype", set_filetype)
        || !FIND("archive_entry_set_perm", set_perm) || !FIND("archive_entry_set_size", set_size)
        || !FIND("archive_write_new", write_new)
        || !FIND("archive_write_set_format_zip", set_format_zip)
        || !FIND("archive_write_open_memory", write_open_memory)
        || !FIND("archive_write_header", write_header) || !FIND("archive_write_close", write_close)
        || !FIND("archive_write_free", write_free) || !FIND("archive_read_new", read_new)
        || !FIND("archive_read_support_format_zip", support_format_zip)
        || !FIND("archive_read_open_memory", open_memory)
        || !FIND("archive_read_next_header", next_header)
        || !FIND("archive_error_string", error_string) || !FIND("archive_read_free", read_free))
        return 0;

    entry = entry_new();
    if (entry == NULL) {
        fprintf(stderr, "FAIL: archive_entry_new returned NULL\n");
        return 0;
    }
    set_pathname(entry, name_utf8);
    w = pathname_w(entry);
    if (w == NULL || wcscmp(w, name_wcs) != 0) {
        fprintf(stderr, "FAIL: archive_entry_pathname_w does not decode the UTF-8 name\n");
        ok = 0;
    }
    copy_pathname_w(entry, name_wcs);
    p = pathname(entry);
    if (p == NULL || strcmp(p, name_utf8) != 0) {
        fprintf(stderr, "FAIL: archive_entry_pathname does not encode the wide name as UTF-8\n");
        ok = 0;
    }

    set_pathname(entry, name_utf8);
    set_filetype(entry, AE_IFREG);
    set_perm(entry, 0644);
    set_size(entry, 0);
    a = write_new();
    if (a == NULL || set_format_zip(a) != ARCHIVE_OK
        || write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
        || write_header(a, entry) != ARCHIVE_OK || write_close(a) != ARCHIVE_OK) {
        p = a != NULL ? error_string(a) : NULL;
        fprintf(stderr, "FAIL: writing a zip entry: %s\n", p != NULL ? p : "");
        ok = 0;
    }
    if (a != NULL)
        write_free(a);
    entry_free(entry);
    if (!ok)
        return 0;
    /* Local file header: flags at 6 (bit 11 is UTF-8), name length at 26, name at 30 */
    if (used < 30 + len || memcmp(buf, "PK\3\4", 4) != 0 || (buf[7] & 0x08) == 0
        || (size_t)buf[26] != len || buf[27] != 0 || memcmp(buf + 30, name_utf8, len) != 0) {
        fprintf(stderr, "FAIL: the zip local file header lacks the UTF-8 flag or the UTF-8 name\n");
        return 0;
    }
    a = read_new();
    if (a == NULL || support_format_zip(a) != ARCHIVE_OK || open_memory(a, buf, used) != ARCHIVE_OK
        || next_header(a, &entry) != ARCHIVE_OK) {
        p = a != NULL ? error_string(a) : NULL;
        fprintf(stderr, "FAIL: reading the zip entry: %s\n", p != NULL ? p : "");
        ok = 0;
    } else if ((p = pathname(entry)) == NULL || strcmp(p, name_utf8) != 0) {
        fprintf(stderr, "FAIL: the zip entry's name does not read back as the same UTF-8\n");
        ok = 0;
    }
    if (a != NULL)
        read_free(a);
    if (ok)
        printf("OK: names are UTF-8 in the \"C\" locale\n");
    return ok;
}

int main(int argc, char **argv)
{
    const char *(*version_string)(void);
    const char *(*version_details)(void);
    struct archive *(*read_new)(void);
    int (*support_filter_all)(struct archive *);
    int (*support_format_all)(struct archive *);
    int (*open_memory)(struct archive *, const void *, size_t);
    int (*next_header)(struct archive *, struct archive_entry **);
    const char *(*error_string)(struct archive *);
    int (*read_free)(struct archive *);
    struct archive *reader;
    struct archive_entry *entry;
    const char *text;
    int result, failed = 0;

    if (argc != 2) {
        fprintf(stderr, "usage: %s <path to libarchive.so>\n", argv[0]);
        return 2;
    }
    library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (library == NULL) {
        fprintf(stderr, "FAIL: dlopen %s: %s\n", argv[1], dlerror());
        return 1;
    }
    if (!find("archive_version_string", &version_string, sizeof version_string)
        || !find("archive_version_details", &version_details, sizeof version_details)
        || !find("archive_read_new", &read_new, sizeof read_new)
        || !find("archive_read_support_filter_all", &support_filter_all, sizeof support_filter_all)
        || !find("archive_read_support_format_all", &support_format_all, sizeof support_format_all)
        || !find("archive_read_open_memory", &open_memory, sizeof open_memory)
        || !find("archive_read_next_header", &next_header, sizeof next_header)
        || !find("archive_error_string", &error_string, sizeof error_string)
        || !find("archive_read_free", &read_free, sizeof read_free))
        return 1;

    text = version_string();
    if (text == NULL || strncmp(text, "libarchive ", 11) != 0) {
        fprintf(stderr, "FAIL: archive_version_string returned %s\n", text != NULL ? text : "NULL");
        return 1;
    }
    printf("archive_version_string: %s\n", text);
    text = version_details();
    if (text == NULL) {
        fprintf(stderr, "FAIL: archive_version_details returned NULL\n");
        return 1;
    }
    printf("archive_version_details: %s\n", text);

    /* Read an empty input with every filter and format enabled: each of them bids on
       it, and the "empty" format must win and report the end of the archive. */
    reader = read_new();
    if (reader == NULL) {
        fprintf(stderr, "FAIL: archive_read_new returned NULL\n");
        return 1;
    }
    if ((result = support_filter_all(reader)) != ARCHIVE_OK) {
        fprintf(stderr, "FAIL: archive_read_support_filter_all returned %d\n", result);
        failed = 1;
    }
    if ((result = support_format_all(reader)) != ARCHIVE_OK) {
        fprintf(stderr, "FAIL: archive_read_support_format_all returned %d\n", result);
        failed = 1;
    }
    if (!failed && (result = open_memory(reader, "", 0)) != ARCHIVE_OK) {
        text = error_string(reader);
        fprintf(stderr, "FAIL: archive_read_open_memory returned %d: %s\n", result, text != NULL ? text : "");
        failed = 1;
    }
    if (!failed && (result = next_header(reader, &entry)) != ARCHIVE_EOF) {
        text = error_string(reader);
        fprintf(stderr, "FAIL: archive_read_next_header on an empty input returned %d, not ARCHIVE_EOF: %s\n",
            result, text != NULL ? text : "");
        failed = 1;
    }
    if ((result = read_free(reader)) != ARCHIVE_OK) {
        fprintf(stderr, "FAIL: archive_read_free returned %d\n", result);
        failed = 1;
    }
    if (!check_utf8_names())
        failed = 1;
    if (failed)
        return 1;
    printf("OK: %s loaded with RTLD_NOW and read an empty archive\n", argv[1]);
    return 0;
}
EOF
}

# Load a freshly linked library in a process that contains nothing but musl: a small
# program built with the target toolchain as a dynamically linked musl executable, run
# by the musl dynamic loader from the toolchain's sysroot. No glibc and no libgcc_s is
# present, so nothing else can supply a symbol the library lacks, and it then fails to
# load. Runs natively (x64), or under the named QEMU user-mode emulator for another
# architecture. If that emulator is not installed, the test fails the build under
# GitHub Actions and is skipped with a warning elsewhere.
# Usage: test_load_musl <library> [qemu-binary]
# Uses CC, and AR to find the toolchain's readelf.
test_load_musl() {
    local lib="${1:-}" qemu="${2:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: test_load_musl needs the library to load as its argument"
        return 1
    fi
    local var
    for var in CC AR; do
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

    write_load_test_source loadtest.c || return
    # -static-libgcc: the program must not depend on libgcc_s, which could supply a
    # helper routine that the library lacks
    "${cc[@]}" -O2 -Wall -Wextra -static-libgcc -o loadtest-musl loadtest.c || return
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

# Load a freshly linked library in a program built with the host's own gcc and C library
# (glibc on the CI runners), as a .NET process on a typical Linux distribution would.
# Only possible for a library built for the host's architecture (x64).
# Usage: test_load_host <library>
test_load_host() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: test_load_host needs the library to load as its argument"
        return 1
    fi

    echo "=== Load test: ${lib} in a program built with the host's gcc ==="
    write_load_test_source loadtest.c || return
    # -ldl for glibc before 2.34, where dlopen is not in libc itself
    gcc -O2 -Wall -Wextra -o loadtest-host loadtest.c -ldl || return
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
