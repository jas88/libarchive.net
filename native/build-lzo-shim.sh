#!/bin/sh
# In-process lzop (LZO1X) support for the native library builds, without liblzo2
# Source this file in platform-specific build scripts, after build-config.sh
#
# libarchive's lzop filters are written against liblzo2, which is GPL-licensed and so
# cannot be linked into the native libraries. build_lzo_shim builds a stand-in
# liblzo2.a from lzokay (MIT, pinned in build-config.sh) and the sources in lzo-shim/,
# which implement the part of the liblzo2 API libarchive uses. libarchive's own
# --with-lzo2 configure checks then find it, so libarchive needs no patch.
#
# libarchive's configure treats an unusable liblzo2 as an absent one and quietly
# falls back to running an external lzop program, so every build script must also
# call verify_lzo_support on what it linked.
#
# This file is sourced by build-windows.sh under plain sh and must stay POSIX: no
# arrays, no here-strings, and no "local" - the functions have subshell bodies
# instead, which also keeps their "cd" and "set -f" from reaching the caller. Every
# step ends in "|| exit", because callers may run these where set -e does not apply
# (after "&&" or in an "if"), and a failed step must never fall through to the next.

# Compiler flags for lzokay and the shim, added after the platform's own flags.
#
# The Linux libraries are linked with -nostdlib against a static C library only, so
# the objects must not need a C++ runtime: no exception tables or personality
# routine, no RTTI, no guard variables for static locals. Hidden visibility keeps the
# lzo_* and lzokay symbols out of the libraries' export tables: this is not liblzo2
# (the work memory sizes differ), so nothing outside the library may ever bind to it.
# lzokay reads its input through uint16_t pointers, hence -fno-strict-aliasing.
LZO_SHIM_CXXFLAGS="-std=c++14 -fno-exceptions -fno-rtti -fno-threadsafe-statics -fvisibility=hidden -fno-strict-aliasing"

# Extended regular expression matching the symbols of a C++ runtime, as nm prints
# them: the C++ ABI support routines, the exception personality and unwinder,
# operator new and delete, anything in namespace std, and vtables and type
# information. Not anchored at the start, because Mach-O and 32-bit Windows put an
# underscore in front of every symbol.
LZO_SHIM_CXX_RUNTIME_SYMBOLS='(__cxa_|__gxx_personality|_Unwind_|__dynamic_cast|_Zn[wa]|_Zd[la]|_ZSt|_ZNK?St|_ZT[VIS])'

# Build lzokay and the liblzo2 shim, and install them as liblzo2.a and <lzo/*.h>.
# Usage: build_lzo_shim <shim source directory> <unpacked lzokay directory> <flags>
# <flags> are the platform's own C++ compiler flags as one string (optimisation,
# -fPIC, -m32, -arch ...); LZO_SHIM_CXXFLAGS are added to them.
# Uses PREFIX, CXX (default: c++) and AR (default: ar), and the ranlib and nm beside
# AR. Compiles in ./lzokay-build, so the current directory must be the build directory.
build_lzo_shim() (
    # Compiler commands and flags are split into words on purpose; stop the shell
    # from also expanding wildcards in them.
    set -f

    if [ $# -ne 3 ]; then
        echo "ERROR: usage: build_lzo_shim <shim source directory> <lzokay directory> <flags>"
        exit 1
    fi
    if [ -z "${PREFIX:-}" ]; then
        echo "ERROR: build_lzo_shim needs PREFIX to be set"
        exit 1
    fi
    shim_dir=$(cd "$1" && pwd) || exit
    lzokay_dir=$(cd "$2" && pwd) || exit
    platform_flags=$3
    cxx=${CXX:-c++}
    ar=${AR:-ar}
    # ${ar%ar}nm replaces only the trailing "ar" of the tool name. ${ar/ar/nm} would
    # rewrite the first "ar" in the whole path instead, such as the one in a
    # ".../libarchive-linux-x64/..." toolchain directory, and is not POSIX either.
    nm="${ar%ar}nm"
    ranlib="${ar%ar}ranlib"
    if ! command -v patch >/dev/null 2>&1; then
        echo "ERROR: build_lzo_shim needs the patch program"
        exit 1
    fi

    echo "Building the lzop shim (lzokay ${LZOKAY_COMMIT:-})..."
    # Always start from the pristine lzokay sources: the object files are specific to
    # one target, and the patch below cannot be applied twice.
    rm -rf lzokay-build || exit
    mkdir lzokay-build || exit
    cp "$lzokay_dir/lzokay.cpp" "$lzokay_dir/lzokay.hpp" lzokay-build/ || exit
    cd lzokay-build || exit

    # lzokay's decompressor trusts its input further than it should; see the patch.
    patch -p1 < "$shim_dir/lzokay-bounds-checks.patch" || exit

    # The shim's own <lzo/*.h> come first, ahead of any -I in the platform flags, so
    # that it is always compiled against the headers it is installed with.
    # shellcheck disable=SC2086  # $cxx, $platform_flags and the flag list are word lists
    $cxx -I"$shim_dir/include" -I. $platform_flags $LZO_SHIM_CXXFLAGS -c lzokay.cpp -o lzokay.o || exit
    # shellcheck disable=SC2086
    $cxx -I"$shim_dir/include" -I. $platform_flags $LZO_SHIM_CXXFLAGS -c "$shim_dir/lzo-shim.cpp" -o lzo-shim.o || exit

    mkdir -p "$PREFIX/lib" "$PREFIX/include/lzo" || exit
    rm -f "$PREFIX/lib/liblzo2.a" || exit
    # Archived, then indexed by ranlib on its own, which is what "ar rcs" does in one
    # step: with universal macOS objects, Apple's ar warns that an archive it indexes
    # itself "will be fat and ar(1) will not be able to operate on it", and ranlib
    # makes the same archive without a warning.
    "$ar" rc "$PREFIX/lib/liblzo2.a" lzokay.o lzo-shim.o || exit
    "$ranlib" "$PREFIX/lib/liblzo2.a" || exit
    cp "$shim_dir/include/lzo/lzoconf.h" "$shim_dir/include/lzo/lzo1x.h" "$PREFIX/include/lzo/" || exit

    # Fail here, with the symbol names in the log, rather than let libarchive's
    # configure silently decide that there is no usable liblzo2.
    if ! undefined=$("$nm" -u "$PREFIX/lib/liblzo2.a"); then
        echo "ERROR: could not read the symbols of $PREFIX/lib/liblzo2.a with ${nm}"
        exit 1
    fi
    echo "Undefined symbols of the object files in liblzo2.a:"
    printf '%s\n' "$undefined"
    if printf '%s\n' "$undefined" | grep -E "$LZO_SHIM_CXX_RUNTIME_SYMBOLS"; then
        echo "ERROR: liblzo2.a needs the C++ runtime symbols listed above."
        echo "The native libraries are not linked against a C++ runtime, so it must not."
        exit 1
    fi
    echo "liblzo2.a needs no C++ runtime"
)

# Verify that a linked library has in-process lzop support, failing the build otherwise.
# Usage: verify_lzo_support <library> [nm option...]
# Any further arguments are passed to nm in front of the library, for example
# "-arch arm64" to look at one slice of a universal macOS library.
# Uses AR (default: ar) to find the toolchain's nm. Run it before the library is
# stripped: the shim's symbols are not exported, so stripping removes them.
verify_lzo_support() (
    if [ $# -lt 1 ] || [ -z "$1" ]; then
        echo "ERROR: verify_lzo_support needs the library to check as its argument"
        exit 1
    fi
    lib=$1
    shift
    ar=${AR:-ar}
    nm="${ar%ar}nm"

    echo "=== Verifying lzop support in ${lib} $* ==="
    if ! symbols=$("$nm" "$@" "$lib"); then
        echo "ERROR: could not read the symbols of ${lib} with ${nm}"
        exit 1
    fi
    # libarchive only refers to this function when its configure found <lzo/lzo1x.h>
    # and liblzo2, and the linker only takes it out of liblzo2.a when libarchive
    # refers to it. Mach-O and 32-bit Windows add the leading underscore.
    # awk reads the whole list: "grep -q" would stop at the match and close the pipe,
    # and where SIGPIPE is ignored (as under GitHub Actions) printf then fails with a
    # "write error: Broken pipe" for the rest of the list.
    if ! printf '%s\n' "$symbols" |
        awk '/ [Tt] _?lzo1x_decompress_safe$/ { found = 1 } END { exit !found }'; then
        echo "ERROR: lzo1x_decompress_safe is not defined in ${lib}."
        echo "libarchive was built without liblzo2, so it would read lzop data only by running an external lzop program."
        exit 1
    fi
    echo "lzo1x_decompress_safe is defined: lzop data is decompressed in-process"
)

# Verify that a library linked with -nostdlib resolves everything the lzop shim needs,
# failing the build otherwise. Only for the Linux builds (GNU nm, ELF): such a library
# links even with symbols left undefined, and has no dependencies to supply them, so
# it would then fail to load. The Windows and macOS links fail by themselves instead.
# Usage: verify_lzo_shim_resolved <library>
# Uses PREFIX and AR.
verify_lzo_shim_resolved() (
    if [ $# -ne 1 ] || [ -z "$1" ]; then
        echo "ERROR: verify_lzo_shim_resolved needs the library to check as its argument"
        exit 1
    fi
    if [ -z "${PREFIX:-}" ] || [ -z "${AR:-}" ]; then
        echo "ERROR: verify_lzo_shim_resolved needs PREFIX and AR to be set"
        exit 1
    fi
    lib=$1
    nm="${AR%ar}nm"

    echo "=== Verifying that ${lib} resolves everything the lzop shim needs ==="
    if ! undefined=$("$nm" -D -u "$lib") || ! defined=$("$nm" --defined-only "$lib") ||
        ! needed=$("$nm" -u "$PREFIX/lib/liblzo2.a"); then
        echo "ERROR: could not read the symbols of ${lib} and $PREFIX/lib/liblzo2.a with ${nm}"
        exit 1
    fi

    if printf '%s\n' "$undefined" | grep -E "$LZO_SHIM_CXX_RUNTIME_SYMBOLS"; then
        echo "ERROR: ${lib} has the unresolved C++ runtime symbols listed above and would fail to load."
        exit 1
    fi

    # Every symbol the shim's object files import ("U"; weak references may stay
    # unresolved) must be defined in the library, whatever its name. The global
    # offset table is the one exception: the linker supplies it without always
    # leaving a symbol behind. One awk reads both lists, the defined names (the last
    # field of each line) up to a line "--", and nm's list of the shim's imports
    # after it: a "grep -q" per symbol closed the pipe of the defined list at a match,
    # and printf then failed with "write error: Broken pipe" where SIGPIPE is ignored.
    missing=$( { printf '%s\n' "$defined"; echo --; printf '%s\n' "$needed"; } |
        awk '!imports && $0 == "--" { imports = 1; next }
            !imports { if (NF) defined[$NF] = 1; next }
            $1 == "U" && $2 != "_GLOBAL_OFFSET_TABLE_" && !($2 in defined) { print $2 }' |
        sort -u)
    if [ -n "$missing" ]; then
        printf '%s\n' "$missing"
        echo "ERROR: ${lib} does not define the symbols listed above, which the lzop shim needs, and would fail to load."
        exit 1
    fi
    echo "No unresolved C++ runtime symbols, and every symbol the lzop shim needs is defined"
)
