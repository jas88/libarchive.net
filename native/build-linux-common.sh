#!/bin/bash
# Shared helpers for the Linux native library builds (build-linux.sh)
# Source this file in that script, after build-config.sh
#
# These live here rather than in build-config.sh because that file is also sourced
# by build-windows.sh under plain sh, and the functions below need bash (here-strings,
# indirect expansion, arrays).

# Make the tool directory of a Linux build afresh: zig-cc.sh (in this directory) as
# <host>-gcc and <host>-g++, and GNU binutils' ar, ranlib, nm, readelf, objdump and strip
# as <host>-ar and so on, where <host> is the triplet that configure is given as --host.
# For a cross build, configure looks for each tool under its prefixed name, and only if it
# finds none takes the build machine's unprefixed one, warning "using cross tools not
# prefixed with host triplet". The build and its checks run binutils through these links
# alone (as ${AR%ar}nm and so on), so that every tool comes from the same place.
# zig has no nm, readelf or objdump of its own. The tools come from BINUTILS_DIR, under
# their unprefixed names, if that is set, otherwise from PATH on Linux and from Homebrew's
# binutils (in /opt/homebrew/opt/binutils/bin or /usr/local/opt/binutils/bin) on macOS,
# whose own tools are Apple's. readelf reads the ELF files of every target, but the other
# tools read and write only those of the targets that their binutils were built for:
# Homebrew's are built for every target, and a Linux distribution's own binutils for its
# own architecture, such as the x86 ones of CI's Ubuntu runners. A build for another
# architecture there takes that architecture's binutils from BINUTILS_DIR: CI sets it to
# /usr/aarch64-linux-gnu/bin or /usr/arm-linux-gnueabihf/bin, where Ubuntu's packages
# binutils-aarch64-linux-gnu and binutils-arm-linux-gnueabihf put them.
# Usage: setup_linux_tools <tool directory> <host triplet>
# Uses SCRIPT_DIR.
setup_linux_tools() {
    local dir="${1:-}" host="${2:-}"
    if [ -z "$dir" ] || [ -z "$host" ]; then
        echo "ERROR: setup_linux_tools needs the tool directory and the host triplet as its arguments"
        return 1
    fi
    if [ -z "${SCRIPT_DIR:-}" ]; then
        echo "ERROR: setup_linux_tools needs SCRIPT_DIR to be set"
        return 1
    fi

    local bindir="${BINUTILS_DIR:-}" source candidate tool path
    if [ -n "$bindir" ]; then
        source="BINUTILS_DIR (${bindir})"
    elif [ "$(uname -s)" = Linux ]; then
        source="PATH"
    else
        source="Homebrew's binutils (/opt/homebrew/opt/binutils/bin or /usr/local/opt/binutils/bin)"
        for candidate in /opt/homebrew/opt/binutils/bin /usr/local/opt/binutils/bin; do
            if [ -x "${candidate}/readelf" ]; then
                bindir="$candidate"
                break
            fi
        done
    fi

    rm -rf "$dir" || return
    mkdir -p "$dir" || return
    for tool in ar ranlib nm readelf objdump strip; do
        if [ -n "$bindir" ]; then
            path="${bindir}/${tool}"
        else
            path=$(command -v "$tool") || path=""
        fi
        if [ -z "$path" ] || [ ! -x "$path" ]; then
            echo "ERROR: GNU ${tool} was not found in ${source}."
        # awk reads all of the version text, so that the tool never writes to a closed pipe
        elif ! "$path" --version 2>/dev/null | awk 'NR == 1 && /GNU/ {found = 1} END {exit !found}'; then
            echo "ERROR: ${path}, from ${source}, is not the ${tool} of GNU binutils."
            path=""
        fi
        if [ -z "$path" ] || [ ! -x "$path" ]; then
            echo "The Linux builds need GNU binutils' ar, ranlib, nm, readelf, objdump and strip, for the target."
            echo "Install binutils (Debian/Ubuntu: apt-get install binutils; macOS: brew install binutils),"
            echo "or set BINUTILS_DIR to the directory that holds them under their unprefixed names."
            return 1
        fi
        ln -s "$path" "${dir}/${host}-${tool}" || return
    done
    # zig-cc.sh runs zig c++ when it is called by a name that ends in "++"
    ln -s "${SCRIPT_DIR}/zig-cc.sh" "${dir}/${host}-gcc" || return
    ln -s "${SCRIPT_DIR}/zig-cc.sh" "${dir}/${host}-g++" || return
    echo "Tools for ${host} in ${dir}: zig-cc.sh, and GNU binutils from ${bindir:-PATH}"
}

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

# Link the final libarchive.so: all of libarchive.a, then the other static libraries, as a
# shared library of the target's own C library, which zig links it against with the rest
# of what the target needs (its compiler-rt, and the C library's stubs). The C library is
# then the one thing that the library needs at run time: glibc (libc.so.6, its dynamic
# loader for a symbol or two, and before glibc 2.34 the libraries split from libc.so.6,
# such as libpthread.so.0) or musl (libc.so).
#
# The archives after libarchive.a are linked as one group (--start-group ... --end-group),
# so that their order does not matter: libxml2.a, for one, uses libz.a.
#
# -z defs makes the link fail on any strong (non-weak) unresolved symbol, such as a
# function that the target's C library lacks in the oldest version the target names.
# Weak references, such as zstd's optional ZSTD_trace_* hooks, are still allowed: they
# resolve to null when nothing defines them. -z relro and -z now give full RELRO: the
# dynamic loader binds every symbol when it loads the library, and then makes the data it
# relocated read-only.
#
# The version script linux-exports.map (in this directory) exports libarchive's API, the
# archive_* functions, and nothing else (see that file).
#
# Any C sources named after the library are compiled for the target too, and linked in ahead
# of the archives: linux-arm-stack-guard.c, for the 32-bit ARM libraries.
#
# To link another static library, add it to the archive list below.
# Usage: link_libarchive <library> [<C source>...]
# Uses CC, PREFIX and SCRIPT_DIR.
link_libarchive() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: link_libarchive needs the library to create as its argument"
        return 1
    fi
    shift
    local var
    for var in CC PREFIX SCRIPT_DIR; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: link_libarchive needs ${var} to be set"
            return 1
        fi
    done

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
    )
    local -a objects=()
    local source object
    for source in "$@"; do
        object="${source##*/}"
        object="${object%.c}.o"
        "$CC" -c -O2 -fPIC -Wall -Wextra -o "$object" "$source" || return
        objects+=("$object")
    done
    local -a link=(
        "$CC" -shared -o "$lib" "-Wl,-z,defs" "-Wl,-z,relro" "-Wl,-z,now"
        "-Wl,--version-script=${SCRIPT_DIR}/linux-exports.map"
        "${objects[@]}"
        "-Wl,--whole-archive" "$PREFIX/lib/libarchive.a" "-Wl,--no-whole-archive"
        "-Wl,--start-group" "${archives[@]}" "-Wl,--end-group"
    )
    echo "Linking ${lib}:"
    echo "${link[*]}"
    "${link[@]}"
}

# Strip the debugging information from a linked library. zig-cc.sh compiles the code here
# with -g0, but zig builds its compiler-rt, and for glibc the members of libc_nonshared.a,
# with DWARF debugging information, which a library holds wherever it takes code from them:
# about 776 KB in each 32-bit ARM library, which takes compiler-rt whole, naming the
# directory that zig built it in, and about 10 KB in linux-x64 and linux-arm64, which take
# a few members of libc_nonshared.a (linux-musl-x64 and linux-musl-arm64 hold none).
# GNU strip --strip-debug removes the .debug_* sections. It would also remove the symbols
# that name the objects linked in (STT_FILE), as debugging symbols, which
# --keep-file-symbols keeps, for verify_stack_chk_fail. The rest of the symbol table
# (.symtab) stays: the checks after this and the stack protector checks of the 32-bit ARM
# load test read it. zig's own
# -Wl,--strip-debug is not used, as zig makes it a full strip, which removes .symtab too.
# Run it right after link_libarchive, before every check.
# Usage: strip_debug_info <library>
# Uses AR to find the toolchain's strip.
strip_debug_info() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: strip_debug_info needs the library to strip as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: strip_debug_info needs AR to be set"
        return 1
    fi

    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local strip="${AR%ar}strip"
    echo "Stripping the debugging information from ${lib} with ${strip}"
    if ! "$strip" --strip-debug --keep-file-symbols "$lib"; then
        echo "ERROR: could not strip the debugging information from ${lib} with ${strip}"
        return 1
    fi
}

# Fail the build if a library has any debugging section (.debug_*) left, which
# strip_debug_info removes.
# Usage: verify_no_debug_info <library>
# Uses AR to find readelf.
verify_no_debug_info() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_no_debug_info needs the library to check as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_no_debug_info needs AR to be set"
        return 1
    fi

    echo "=== Verifying that ${lib} has no debugging information ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local readelf="${AR%ar}readelf"
    local sections left
    if ! sections=$("$readelf" -W -S "$lib"); then
        echo "ERROR: could not read the sections of ${lib} with ${readelf}"
        return 1
    fi
    # The name of each section that begins ".debug_", which readelf can print as the second
    # or the third field of the line ("[ 9]" or "[19]")
    left=$(awk '/^ *\[/ { for (i = 1; i <= NF; i++) if (index($i, ".debug_") == 1) { print "  " $i; next } }' <<< "$sections")
    if [ -n "$left" ]; then
        echo "ERROR: ${lib} still has these debugging sections:"
        echo "$left"
        return 1
    fi
    echo "No .debug_* section left"
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
# mbedTLS here, as neither glibc nor musl has them). If any of them is missing, libarchive
# compiles both as stubs that fail with "Xar not supported on this platform" (the #if at the
# top of archive_read_support_format_xar.c and archive_write_set_format_xar.c), and
# configure does not fail: given --with-xml2, it still goes on without libxml2 if pkg-config
# does not find it. Run it after libarchive's configure, before make.
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
# Uses AR to find the toolchain's nm.
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
    local nm="${AR%ar}nm"
    local symbols undefined text
    if ! symbols=$("$nm" "$lib") || ! undefined=$("$nm" -D --undefined-only "$lib"); then
        echo "ERROR: could not read the symbols of ${lib} with ${nm}"
        return 1
    fi
    # The runs of printable characters in the whole file, one to a line, as strings -a
    # prints them. tr does it in place of binutils' strings, which the target's binutils
    # may lack under its unprefixed name (Ubuntu's cross binutils have it only as
    # /usr/bin/<triplet>-strings; see setup_linux_tools). The C locale makes tr read the
    # file as bytes.
    if ! text=$(LC_ALL=C tr -cs '[:print:]' '\n' < "$lib"); then
        echo "ERROR: could not read the text of ${lib} with tr"
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

# Fail the build unless a library linked against glibc loads with every glibc from the
# version <baseline> on (2.28 for x64 and arm64, 2.34 for arm). The dynamic loader refuses a
# library that needs a symbol version that the process's glibc lacks, and any import that
# it cannot bind (the library is linked with -z now), so:
# - every symbol version that the library needs must be GLIBC_x.y, with x.y no later than
#   the baseline, compared as version numbers (GLIBC_PRIVATE, glibc's internal interface,
#   fails);
# - every symbol that it imports must have a version, apart from weak references, which
#   resolve to null when nothing defines them: glibc gives each of its symbols a version,
#   so an unversioned one would have to come from something else in the process;
# - it must need no library (DT_NEEDED) but glibc's own: libc.so.6, the libraries split
#   from it before glibc 2.34 (libpthread.so.0, libm.so.6, libdl.so.2 and librt.so.1), and
#   its dynamic loader, ld-linux-*.so.*, which defines a few of glibc's symbols itself, such
#   as the stack protector's __stack_chk_guard on arm64;
# - on 32-bit ARM, it must import none of glibc's functions with a 32-bit time_t or off_t,
#   which glibc-32bit-names.txt (in this directory) lists: a library compiled with
#   -D_TIME_BITS=64 -D_FILE_OFFSET_BITS=64 calls their 64-bit counterparts only.
# readelf reads every target's files alike, so it lists the symbols and versions here.
# Usage: verify_glibc_imports <library> <baseline>
# Uses AR to find readelf, and SCRIPT_DIR.
verify_glibc_imports() {
    local lib="${1:-}" baseline="${2:-}"
    if [ -z "$lib" ] || [ -z "$baseline" ]; then
        echo "ERROR: verify_glibc_imports needs the library to check and the oldest glibc version as its arguments"
        return 1
    fi
    local var
    for var in AR SCRIPT_DIR; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: verify_glibc_imports needs ${var} to be set"
            return 1
        fi
    done

    echo "=== Verifying that ${lib} loads with glibc ${baseline} and later ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local readelf="${AR%ar}readelf"
    local header dynamic versions symbols
    if ! header=$("$readelf" -W -h "$lib") || ! dynamic=$("$readelf" -W -d "$lib") ||
        ! versions=$("$readelf" -W -V "$lib") || ! symbols=$("$readelf" -W --dyn-syms "$lib"); then
        echo "ERROR: could not read ${lib} with ${readelf}"
        return 1
    fi

    # Each list holds one indented entry per line, ready to print
    local needed bad_needed bad_versions imports strong weak newest failed=0
    needed=$(awk '/\(NEEDED\)/ {name = $NF; gsub(/^\[|\]$/, "", name); print name}' <<< "$dynamic")
    bad_needed=$(awk '!/^(libc\.so\.6|libpthread\.so\.0|libm\.so\.6|libdl\.so\.2|librt\.so\.1|ld-linux[-a-z0-9_]*\.so\.[0-9]+)$/ {print "  " $0}' <<< "$needed")
    # The version needs section lists, for each library needed, the versions needed of it:
    # a "File: <library>" line, then a "Name: <version>" line for each version
    bad_versions=$(awk -v baseline="$baseline" '
        # 1 if the dotted version a is no later than b, compared number by number
        function not_later(a, b,    x, y, n, i) {
            n = split(a, x, ".")
            if (split(b, y, ".") > n)
                n = length(y)
            for (i = 1; i <= n; i++) {
                if (x[i] + 0 < y[i] + 0) return 1
                if (x[i] + 0 > y[i] + 0) return 0
            }
            return 1
        }
        /^Version needs section/ { needs = 1; next }
        /^Version (symbols|definition) section/ { needs = 0 }
        needs && match($0, /File: [^ ]+/) { file = substr($0, RSTART + 6, RLENGTH - 6) }
        needs && match($0, /Name: [^ ]+/) {
            version = substr($0, RSTART + 6, RLENGTH - 6)
            if (version !~ /^GLIBC_[0-9]+(\.[0-9]+)*$/ || !not_later(substr(version, 7), baseline))
                print "  " version ", of " file
        }' <<< "$versions")
    # Each undefined dynamic symbol as "<binding> <name>[@<version>]". The name is the last
    # field, or the one before it when readelf ends the line with the version's index, "(2)".
    imports=$(awk '$1 ~ /^[0-9]+:$/ {
            name = $NF; ndx = $(NF - 1)
            if (name ~ /^\([0-9]+\)$/) { name = $(NF - 1); ndx = $(NF - 2) }
            if (ndx == "UND") print $5, name
        }' <<< "$symbols")
    strong=$(awk '$1 != "WEAK" && index($2, "@") == 0 {print "  " $2}' <<< "$imports")
    weak=$(awk '$1 == "WEAK" && index($2, "@") == 0 {print "  " $2}' <<< "$imports")
    if [ -n "$bad_needed" ]; then
        echo "ERROR: ${lib} needs these libraries (DT_NEEDED), which are not glibc's:"
        echo "$bad_needed"
        failed=1
    fi
    if [ -n "$bad_versions" ]; then
        echo "ERROR: ${lib} needs these symbol versions, which glibc ${baseline} does not have:"
        echo "$bad_versions"
        failed=1
    fi
    if [ -n "$strong" ]; then
        echo "ERROR: ${lib} imports these symbols without a version, which no glibc defines:"
        echo "$strong"
        failed=1
    fi
    if awk '$1 == "Machine:" && $2 == "ARM" {found = 1} END {exit !found}' <<< "$header"; then
        local list="${SCRIPT_DIR}/glibc-32bit-names.txt" old
        if [ ! -f "$list" ]; then
            echo "ERROR: ${list} does not exist"
            return 1
        fi
        old=$(awk 'NR == FNR { if ($0 !~ /^#/ && NF == 2) counterpart[$1] = $2; next }
            { name = $2; sub(/@.*/, "", name); if (name in counterpart) print "  " name ", in place of " counterpart[name] }' \
            "$list" - <<< "$imports")
        if [ -n "$old" ]; then
            echo "ERROR: ${lib} imports these functions of glibc's with a 32-bit time_t or off_t:"
            echo "$old"
            echo "It must be compiled with -D_TIME_BITS=64 -D_FILE_OFFSET_BITS=64, as build-linux.sh compiles it."
            failed=1
        fi
    fi
    if [ "$failed" -ne 0 ]; then
        return 1
    fi
    newest=$(awk '/^Version needs section/ { needs = 1; next } /^Version (symbols|definition) section/ { needs = 0 }
        needs && match($0, /Name: GLIBC_[0-9.]+/) { print substr($0, RSTART + 12, RLENGTH - 12) }' <<< "$versions" |
        sort -t . -k 1,1n -k 2,2n -k 3,3n | tail -n 1)
    echo "Needs $(tr '\n' ' ' <<< "$needed")only, and glibc ${newest:-(no version)} at the latest, for its $(awk 'index($2, "@") {n++} END {print n + 0}' <<< "$imports") versioned imports"
    if awk '$1 == "Machine:" && $2 == "ARM" {found = 1} END {exit !found}' <<< "$header"; then
        echo "None of glibc's functions with a 32-bit time_t or off_t (glibc-32bit-names.txt)"
    fi
    if [ -n "$weak" ]; then
        echo "Weak unversioned imports (allowed, null when nothing defines them):"
        echo "$weak"
    fi
}

# Fail the build unless a 32-bit ARM library checks its stack protector against a guard that
# is seeded from the process's random bytes (see linux-arm-stack-guard.c, in this directory):
# its __stack_chk_guard must be the one of that file, which is uninitialised data (.bss)
# until its constructor seeds it, not the initialised one (.data) of zig's compiler-rt, which
# holds a constant, and that constructor must be in .init_array, where the dynamic loader
# finds what to run when it loads the library.
# Usage: verify_stack_guard <library>
# Uses AR to find nm and readelf.
verify_stack_guard() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_stack_guard needs the library to check as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_stack_guard needs AR to be set"
        return 1
    fi

    echo "=== Verifying the stack protector guard of ${lib} ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local nm="${AR%ar}nm" readelf="${AR%ar}readelf"
    local symbols sections guard seed section entries position
    if ! symbols=$("$nm" "$lib") || ! sections=$("$readelf" -W -S "$lib"); then
        echo "ERROR: could not read ${lib} with ${nm} and ${readelf}"
        return 1
    fi
    guard=$(awk '$3 == "__stack_chk_guard" {print $2}' <<< "$symbols")
    if [ "$guard" != b ] && [ "$guard" != B ]; then
        echo "ERROR: ${lib}'s __stack_chk_guard (nm type: ${guard:-none}) is not the uninitialised one of"
        echo "linux-arm-stack-guard.c, so the library's stack protector would check its frames against zig's constant."
        return 1
    fi
    seed=$(awk '$3 == "libarchive_net_seed_stack_guard" {print $1}' <<< "$symbols")
    # .init_array's file offset and size: the third and fourth fields after its name, which
    # readelf can print as the second or the third field of the line ("[ 9]" or "[19]")
    section=$(awk '{ for (i = 1; i < NF; i++) if ($i == ".init_array") { print $(i + 3), $(i + 4); exit } }' <<< "$sections")
    if [ -z "$seed" ] || [ -z "$section" ]; then
        echo "ERROR: ${lib} has no constructor libarchive_net_seed_stack_guard (${seed:-missing}) or no .init_array (${section:-missing})"
        return 1
    fi
    # The section's entries, 32-bit addresses: od prints them in the build machine's byte
    # order, which is little-endian, as 32-bit ARM's is, and with -v prints every line
    # shellcheck disable=SC2086  # the offset and size, split on purpose
    set -- $section
    if ! entries=$(od -v -A n -t x4 -j "$((16#$1))" -N "$((16#$2))" "$lib"); then
        echo "ERROR: could not read the .init_array of ${lib}"
        return 1
    fi
    # Compared as strings: awk would take an address such as 0024e510 for a number
    position=$(awk -v seed="$seed" '{ for (i = 1; i <= NF; i++) { n++; if (($i "") == (seed "")) { print n; exit } } }' <<< "$entries")
    if [ -z "$position" ]; then
        echo "ERROR: the constructor that seeds the guard (at ${seed}) is not in ${lib}'s .init_array: ${entries}"
        return 1
    fi
    echo "__stack_chk_guard is linux-arm-stack-guard.c's (nm: ${guard}), seeded by the constructor at ${seed}, entry ${position} of .init_array"
}

# Fail the build unless a 32-bit ARM library's stack protector ends the process, when a frame
# fails its check, as the library's C library ends it (see linux-arm-stack-guard.c, in this
# directory). Its __stack_chk_fail must be hidden, and defined by linux-arm-stack-guard.c for
# glibc, which then writes glibc's message and aborts, and by zig's compiler-rt for musl,
# which then stops the process at an undefined instruction, as musl's own does. The symbol
# table (.symtab) names the object that defines each local symbol, hidden ones included: the
# linker puts an object's local symbols after the STT_FILE symbol that names it, "compiler_rt"
# for compiler-rt, and strip_debug_info keeps those (--keep-file-symbols).
# Usage: verify_stack_chk_fail <library> <glibc|musl>
# Uses AR to find readelf.
verify_stack_chk_fail() {
    local lib="${1:-}" libc="${2:-}" object
    if [ -z "$lib" ] || [ -z "$libc" ]; then
        echo "ERROR: verify_stack_chk_fail needs the library to check and its C library, glibc or musl, as its arguments"
        return 1
    fi
    case "$libc" in
        glibc) object=linux-arm-stack-guard.c ;;
        musl)  object=compiler_rt ;;
        *)
            echo "ERROR: verify_stack_chk_fail: unknown C library \"${libc}\" (glibc or musl)"
            return 1
            ;;
    esac
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_stack_chk_fail needs AR to be set"
        return 1
    fi

    echo "=== Verifying the stack protector's __stack_chk_fail of ${lib} ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local readelf="${AR%ar}readelf" symbols found
    if ! symbols=$("$readelf" -W -s "$lib"); then
        echo "ERROR: could not read ${lib} with ${readelf}"
        return 1
    fi
    # Each __stack_chk_fail that .symtab defines, indented, as "<type> <binding> <visibility>
    # <object>", where <object> is the name of the STT_FILE symbol before it
    found=$(awk '/^Symbol table / { symtab = index($0, ".symtab") > 0; file = "(none)"; next }
        symtab && $4 == "FILE" { file = $8; next }
        symtab && $8 == "__stack_chk_fail" && $7 != "UND" { print "  " $4, $5, $6, file }' <<< "$symbols")
    if [ "$found" != "  FUNC LOCAL HIDDEN ${object}" ]; then
        echo "ERROR: ${lib}'s __stack_chk_fail is not the hidden one of ${object}, which ends the process"
        echo "as ${libc}'s own __stack_chk_fail does. .symtab defines (type, binding, visibility, object):"
        echo "${found:-  no __stack_chk_fail}"
        return 1
    fi
    echo "__stack_chk_fail is the hidden one of ${object}, which ends the process as ${libc}'s does"
}

# Fail the build unless a library linked against musl needs musl's libc.so and nothing else
# (DT_NEEDED), and no symbol versions, which musl does not have. Anything more would make
# the library fail to load in a musl process, or bind to something other than musl.
# Usage: verify_musl_imports <library>
# Uses AR to find readelf.
verify_musl_imports() {
    local lib="${1:-}"
    if [ -z "$lib" ]; then
        echo "ERROR: verify_musl_imports needs the library to check as its argument"
        return 1
    fi
    if [ -z "${AR:-}" ]; then
        echo "ERROR: verify_musl_imports needs AR to be set"
        return 1
    fi

    echo "=== Verifying that ${lib} needs musl alone ==="
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local readelf="${AR%ar}readelf"
    local dynamic versions needed failed=0
    if ! dynamic=$("$readelf" -W -d "$lib") || ! versions=$("$readelf" -W -V "$lib"); then
        echo "ERROR: could not read ${lib} with ${readelf}"
        return 1
    fi
    needed=$(awk '/\(NEEDED\)/ {print $NF}' <<< "$dynamic")
    if [ "$needed" != "[libc.so]" ]; then
        echo "ERROR: ${lib} must need musl's libc.so alone (DT_NEEDED), but it needs:"
        echo "${needed:-(nothing)}"
        failed=1
    fi
    if awk '/^Version needs section/ {found = 1} END {exit !found}' <<< "$versions"; then
        echo "ERROR: ${lib} needs symbol versions, which musl does not have:"
        echo "$versions"
        failed=1
    fi
    if [ "$failed" -ne 0 ]; then
        return 1
    fi
    echo "Needs libc.so only, with no symbol versions"
}

# Build the load test program (linux-loadtest*.c, in this directory) for a library, with CC,
# as a dynamically linked program of the library's C library. The zig target and its flags
# are ZIG_TARGET and ZIG_TARGET_FLAGS unless others are given.
# - A glibc program is linked with -rdynamic, which exports its stand-ins for glibc's
#   32-bit file functions (see linux-loadtest-lfs32.c), and -ldl, as dlopen is in libdl
#   before glibc 2.34.
# - A musl program is linked with -dynamic, without which zig links it statically, with no
#   dynamic loader to load the library. It must then need musl's libc.so alone, so that
#   nothing else in its process can supply a symbol that the library lacks.
# Usage: build_load_test <program> <glibc|musl> [<zig target> <zig target flags>]
# Uses CC, ZIG_TARGET, ZIG_TARGET_FLAGS, AR to find readelf, and SCRIPT_DIR.
build_load_test() {
    local program="${1:-}" libc="${2:-}"
    local target="${3:-${ZIG_TARGET:-}}" flags="${4-${ZIG_TARGET_FLAGS:-}}"
    if [ -z "$program" ] || [ -z "$libc" ]; then
        echo "ERROR: build_load_test needs the program to build and the C library, glibc or musl, as its arguments"
        return 1
    fi
    local var
    for var in CC AR SCRIPT_DIR target; do
        if [ -z "${!var:-}" ]; then
            echo "ERROR: build_load_test needs ${var} to be set"
            return 1
        fi
    done
    local -a before=() after=()
    case "$libc" in
        glibc) before=(-rdynamic); after=(-ldl) ;;
        musl)  before=(-dynamic) ;;
        *)
            echo "ERROR: build_load_test: unknown C library \"${libc}\" (glibc or musl)"
            return 1
            ;;
    esac

    echo "=== Building the load test program ${program} for ${target} ==="
    ZIG_TARGET="$target" ZIG_TARGET_FLAGS="$flags" "$CC" -O2 -Wall -Wextra "${before[@]}" \
        -o "$program" "${SCRIPT_DIR}"/linux-loadtest*.c "${after[@]}" || return
    # Only the trailing "ar" of the tool name is replaced (see verify_crypto_backend)
    local readelf="${AR%ar}readelf" headers dynamic interp needed
    if ! headers=$("$readelf" -W -l "$program") || ! dynamic=$("$readelf" -W -d "$program"); then
        echo "ERROR: could not read ${program} with ${readelf}"
        return 1
    fi
    interp=$(awk '/Requesting program interpreter:/ {sub(/.*interpreter: /, ""); sub(/\]$/, ""); print}' <<< "$headers")
    needed=$(awk '/\(NEEDED\)/ {name = $NF; gsub(/^\[|\]$/, "", name); printf "%s%s", sep, name; sep = " "}' <<< "$dynamic")
    if [ -z "$interp" ]; then
        echo "ERROR: ${program} is not dynamically linked: it names no dynamic loader (PT_INTERP)"
        return 1
    fi
    if [ "$libc" = musl ] && [ "$needed" != libc.so ]; then
        echo "ERROR: ${program} must need musl's libc.so alone, but it needs: ${needed:-(nothing)}"
        return 1
    fi
    echo "${program}: dynamic loader ${interp}, needs ${needed}"
}

# Run the load test program (see build_load_test) with a library linked against glibc, in a
# glibc process: natively for x64, and for arm64 and arm under QEMU user-mode emulation
# (qemu-aarch64, qemu-arm), with the dynamic loader and libraries of Ubuntu's cross runtime
# of glibc for the architecture (the packages libc6-arm64-cross and libc6-armhf-cross, which
# install /usr/aarch64-linux-gnu/lib/ld-linux-aarch64.so.1 and
# /usr/arm-linux-gnueabihf/lib/ld-linux-armhf.so.3). That needs an x86_64 Linux build
# machine. On macOS the test is skipped with a warning. Elsewhere, and where the emulator
# or the cross runtime is missing, it fails the build under GitHub Actions and is skipped
# with a warning otherwise.
# Usage: test_load_glibc <library> <program> [<qemu program> <glibc's dynamic loader>]
test_load_glibc() {
    local lib="${1:-}" program="${2:-}" qemu="${3:-}" loader="${4:-}"
    if [ -z "$lib" ] || [ -z "$program" ] || { [ -n "$qemu" ] && [ -z "$loader" ]; }; then
        echo "ERROR: test_load_glibc needs the library and the load test program as its arguments, and the QEMU program and glibc's dynamic loader to run them under emulation"
        return 1
    fi

    echo "=== Load test: ${lib} in a glibc process${qemu:+, under ${qemu}} ==="
    local machine absent=""
    machine="$(uname -s) $(uname -m)"
    if [ "$machine" != "Linux x86_64" ]; then
        absent="an x86_64 Linux build machine (this one is ${machine})"
    elif [ -n "$qemu" ]; then
        local emulator
        if ! emulator=$(command -v "$qemu" || command -v "${qemu}-static"); then
            absent="${qemu} (QEMU user-mode emulation; Debian/Ubuntu: qemu-user)"
        fi
        if [ ! -f "$loader" ]; then
            absent="${absent:+${absent} and }${loader} (from Ubuntu's cross runtime of glibc: libc6-arm64-cross or libc6-armhf-cross)"
        fi
    fi
    if [ -n "$absent" ]; then
        if [ "${GITHUB_ACTIONS:-}" = true ] && [ "$(uname -s)" != Darwin ]; then
            echo "ERROR: ${lib} cannot be load tested without ${absent}."
            return 1
        fi
        echo "WARNING: SKIPPING the load test of ${lib}, which needs ${absent}."
        echo "WARNING: nothing here has shown that ${lib} loads in a glibc process: run ${program} with it on Linux."
        return 0
    fi

    local -a run=()
    if [ -n "$qemu" ]; then
        run=("$emulator" "$loader" --library-path "${loader%/*}")
    fi
    case "$program" in
        */*) run+=("$program") ;;
        *)   run+=("./$program") ;;
    esac
    case "$lib" in
        */*) run+=("$lib") ;;
        *)   run+=("./$lib") ;;
    esac
    echo "${run[*]}"
    if ! "${run[@]}"; then
        echo "ERROR: ${lib} failed to load or work in a glibc process (see above)."
        return 1
    fi
}
