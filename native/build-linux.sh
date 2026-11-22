#!/bin/bash
# Build libarchive.so for one Linux runtime with zig cc, linked dynamically against that
# runtime's own C library: glibc for linux-x64, linux-arm64 and linux-arm, and musl for
# linux-musl-x64, linux-musl-arm64 and linux-musl-arm. Every configure is a cross
# configure, so the build runs on x86_64 and aarch64 Linux and macOS alike.
# Usage: build-linux.sh <rid>
# Leaves libarchive-<rid>.so, and loadtest-<rid>, the load test program built for it, in
# ~/libarchive-native.

set -e

usage() {
    echo "Usage: $0 <rid>"
    echo "<rid>: linux-x64, linux-arm64, linux-arm, linux-musl-x64, linux-musl-arm64 or linux-musl-arm"
}

# What each runtime is built for:
# - ZIG_TARGET, the zig target. A glibc target ends in the oldest glibc version whose
#   symbols the code may use: 2.28 (RHEL 8, the oldest glibc of any distribution that .NET
#   supports) for x64 and arm64, and 2.34, the first with a 64-bit time_t on 32-bit ARM,
#   for arm. zig's musl is 1.2.5, the musl of Alpine 3.21, the oldest Alpine that .NET
#   supports.
# - ZIG_TARGET_FLAGS, the target's own compiler flags, which zig-cc.sh adds to every
#   compilation. ARM_CPU makes the arm targets ARMv7-A with VFPv3-D16 and without NEON, the
#   baseline of Debian's armhf and Alpine's armv7, where zig's default for arm has NEON and
#   32 double registers. The glibc arm library is compiled with a 64-bit time_t and off_t,
#   as LibArchive.Net passes a 64-bit time_t to archive_entry_set_mtime, and musl's are
#   64-bit everywhere.
# - HOST, the GNU triplet that configure is given as --host.
# - LOADTEST_TARGET and LOADTEST_FLAGS, the zig target and flags of the load test program:
#   those of the library, apart from linux-arm (see below).
# - QEMU and LOADER, for a glibc library of another architecture than x64: the QEMU
#   user-mode emulator that runs its load test, and the dynamic loader of Ubuntu's cross
#   runtime of glibc that it runs the program with (see test_load_glibc).
# - STACK_GUARD, for the 32-bit ARM libraries: linux-arm-stack-guard.c, which gives the
#   stack protector a guard seeded from the process's random bytes, in place of zig's
#   constant one, and for glibc a __stack_chk_fail that ends the process as glibc's does
#   (see that file).
ARM_CPU="-mcpu=generic+v7a+vfp3d16-neon-vfp3-vfp3sp-d32"
QEMU=""
LOADER=""
STACK_GUARD=""
case "${1:-}" in
    linux-x64)
        LIBC=glibc ZIG_TARGET=x86_64-linux-gnu.2.28 ZIG_TARGET_FLAGS="" HOST=x86_64-linux-gnu
        ;;
    linux-arm64)
        LIBC=glibc ZIG_TARGET=aarch64-linux-gnu.2.28 ZIG_TARGET_FLAGS="" HOST=aarch64-linux-gnu
        QEMU=qemu-aarch64 LOADER=/usr/aarch64-linux-gnu/lib/ld-linux-aarch64.so.1
        ;;
    linux-arm)
        LIBC=glibc ZIG_TARGET=arm-linux-gnueabihf.2.34 HOST=arm-linux-gnueabihf
        ZIG_TARGET_FLAGS="$ARM_CPU -D_TIME_BITS=64 -D_FILE_OFFSET_BITS=64"
        QEMU=qemu-arm LOADER=/usr/arm-linux-gnueabihf/lib/ld-linux-armhf.so.3
        STACK_GUARD=linux-arm-stack-guard.c
        ;;
    linux-musl-x64)
        LIBC=musl ZIG_TARGET=x86_64-linux-musl ZIG_TARGET_FLAGS="" HOST=x86_64-linux-musl
        ;;
    linux-musl-arm64)
        LIBC=musl ZIG_TARGET=aarch64-linux-musl ZIG_TARGET_FLAGS="" HOST=aarch64-linux-musl
        ;;
    linux-musl-arm)
        LIBC=musl ZIG_TARGET=arm-linux-musleabihf ZIG_TARGET_FLAGS="$ARM_CPU" HOST=arm-linux-musleabihf
        STACK_GUARD=linux-arm-stack-guard.c
        ;;
    -h | --help)
        usage
        exit 0
        ;;
    *)
        usage >&2
        exit 2
        ;;
esac
RID="$1"
LOADTEST="loadtest-${RID}"
LOADTEST_TARGET="$ZIG_TARGET"
LOADTEST_FLAGS="$ZIG_TARGET_FLAGS"
if [ "$RID" = linux-arm ]; then
    # The program needs glibc 2.28 only, so that where glibc is older than 2.34 it is the
    # library that fails to load, not the program before it: in Debian 11 (glibc 2.31), with
    # "version `GLIBC_2.32' not found", the first of the later versions the library needs.
    # The program has the 64-bit off_t of the library, but not its 64-bit time_t, which
    # needs glibc 2.34: no time_t passes between the two.
    LOADTEST_TARGET=arm-linux-gnueabihf.2.28
    LOADTEST_FLAGS="$ARM_CPU -D_FILE_OFFSET_BITS=64"
fi

# Set up isolated build directory
BUILD_DIR="${HOME}/libarchive-${RID}"
OUTPUT_DIR="${HOME}/libarchive-native"
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Create build and output directories
mkdir -p "$BUILD_DIR"
mkdir -p "$OUTPUT_DIR"

# Change to build directory
cd "$BUILD_DIR"

# Load shared configuration
# shellcheck source-path=SCRIPTDIR
. "${SCRIPT_DIR}/build-config.sh"

# Load the helpers shared by the Linux builds (tools, mbedTLS and libxml2 builds, final link,
# checks, load test)
# shellcheck source-path=SCRIPTDIR
. "${SCRIPT_DIR}/build-linux-common.sh"

echo "Setting up zig ${ZIG_VERSION} for ${RID} (${ZIG_TARGET})..."
# Download zig to the cache (does not unpack), then unpack it in the build directory
ZIG_ARCHIVE=$(download_zig)
ZIG_DIR="${ZIG_ARCHIVE##*/}"
ZIG_DIR="${ZIG_DIR%.tar.xz}"
rm -rf "$ZIG_DIR"
tar xJf "$ZIG_ARCHIVE"
export ZIG="${BUILD_DIR}/${ZIG_DIR}/zig"
if [ ! -x "$ZIG" ] || [ "$("$ZIG" version)" != "$ZIG_VERSION" ]; then
    echo "ERROR: ${ZIG} is not zig ${ZIG_VERSION}"
    exit 1
fi
# For zig-cc.sh, the compiler, which build_load_test also runs for its own target
export ZIG_TARGET ZIG_TARGET_FLAGS

# <host>-gcc, <host>-g++ and GNU binutils as <host>-ar and so on (see build-linux-common.sh)
setup_linux_tools "${BUILD_DIR}/bin" "$HOST"
export PATH="${BUILD_DIR}/bin:$PATH"
export CC="${BUILD_DIR}/bin/${HOST}-gcc"
export CXX="${BUILD_DIR}/bin/${HOST}-g++"
export AR="${BUILD_DIR}/bin/${HOST}-ar"
export RANLIB="${BUILD_DIR}/bin/${HOST}-ranlib"

# Keep PREFIX for our built libraries (same as before)
export PREFIX="${PREFIX:-$(pwd)/local}"

# Compiler flags (zig-cc.sh adds the target's own, the stack protector and -g0)
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS"
export CXXFLAGS="-fPIC -O2 $CPPFLAGS"
export LDFLAGS="-L$PREFIX/lib"
# pkg-config and the manifest tool for configure (see build-config.sh)
export_cross_build_tools

echo "Toolchain installed:"
"$CC" --version | head -n1
echo "zig ${ZIG_VERSION}, target ${ZIG_TARGET}${ZIG_TARGET_FLAGS:+ ${ZIG_TARGET_FLAGS}}"
echo ""

# Download and unpack fresh copies of all libraries
echo "Setting up library sources..."
download_all_libraries

# Apply the libarchive patches, in order (see "The libarchive patches" in BUILD.md)
patch_libarchive "${SCRIPT_DIR}" "libarchive-${LIBARCHIVE_VERSION}"

# The build machine's triplet, from libarchive's config.guess, for every configure's --build.
# CC is the compiler for the target, so config.guess must not see it: on Linux it compiles
# a test program with CC, if set, to tell glibc from musl.
BUILD_TRIPLET=$(unset CC CXX HOST_CC CC_FOR_BUILD; sh "libarchive-${LIBARCHIVE_VERSION}/build/autoconf/config.guess")
# One configure cache for each runtime, shared by its xz, libxml2 and libarchive
CONFIG_CACHE="$(get_config_cache "$RID")"
echo "Configuring for ${HOST} on ${BUILD_TRIPLET}, with the configure cache ${CONFIG_CACHE}"

# Build compression libraries (static only)
echo "Building lz4 ${LZ4_VERSION}..."
cd "lz4-${LZ4_VERSION}/lib"
make -j"$NCPU" liblz4.a "CC=$CC" "AR=$AR"
mkdir -p "$PREFIX/lib" "$PREFIX/include"
cp liblz4.a "$PREFIX/lib/"
cp lz4.h lz4hc.h lz4frame.h "$PREFIX/include/"
cd ../..

echo "Building zstd ${ZSTD_VERSION}..."
cd "zstd-${ZSTD_VERSION}/lib"
make -j"$NCPU" libzstd.a "CC=$CC" "AR=$AR"
mkdir -p "$PREFIX/lib" "$PREFIX/include"
cp libzstd.a "$PREFIX/lib/"
cp zstd.h zstd_errors.h zdict.h "$PREFIX/include/"
cd ../..

echo "Building bzip2 ${BZIP2_VERSION}..."
cd "bzip2-${BZIP2_VERSION}"
make -j"$NCPU" libbz2.a "CC=$CC" "AR=$AR" "RANLIB=$RANLIB" CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64"
mkdir -p "$PREFIX/lib" "$PREFIX/include"
cp libbz2.a "$PREFIX/lib/"
cp bzlib.h "$PREFIX/include/"
cd ..

# lzop (LZO1X) support: lzokay behind the liblzo2 API, in place of the GPL-licensed liblzo2
# shellcheck source-path=SCRIPTDIR
. "${SCRIPT_DIR}/build-lzo-shim.sh"
build_lzo_shim "${SCRIPT_DIR}/lzo-shim" "lzokay-${LZOKAY_COMMIT}" "$CXXFLAGS"

echo "Building zlib ${ZLIB_VERSION}..."
cd "zlib-${ZLIB_VERSION}"
# zlib's own configure: CHOST names the target system, which is not the build machine's
CHOST="$HOST" ./configure --static --prefix="$PREFIX"
# zlib's configure turns on its large file mode, -D_LARGEFILE64_SOURCE=1, wherever off64_t
# exists. In that mode gzguts.h undefines _FILE_OFFSET_BITS and _TIME_BITS, so that the gz*
# file functions, which libxml2 uses, call the open and fcntl of glibc's with a 32-bit off_t
# and time_t on 32-bit ARM, which verify_glibc_imports refuses. Every target's off_t is
# 64-bit already (linux-arm's by _FILE_OFFSET_BITS=64), so the mode is turned off: zlib's
# code then has the same off_t and time_t as the rest of the library.
sed 's/ -D_LARGEFILE64_SOURCE=1//g' Makefile > Makefile.no-lfs64
mv Makefile.no-lfs64 Makefile
if ! awk 'index($0, "_LARGEFILE64_SOURCE") {found = 1} END {exit found}' Makefile; then
    echo "ERROR: zlib's Makefile still defines _LARGEFILE64_SOURCE (see build-linux.sh)"
    exit 1
fi
make -sj"$NCPU" install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd "xz-${XZ_VERSION}"
./configure --cache-file="$CONFIG_CACHE" --build="$BUILD_TRIPLET" --host="$HOST" --with-pic --disable-shared --prefix="$PREFIX"
make -sj"$NCPU" install
cd ..

# Build and install the static libxml2 for XAR, with only what libarchive uses (see build-linux-common.sh)
build_libxml2 --cache-file="$CONFIG_CACHE" --build="$BUILD_TRIPLET" --host="$HOST"

# Download, build and install the static mbedTLS crypto library (see build-linux-common.sh)
build_mbedtls

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd "libarchive-${LIBARCHIVE_VERSION}"
# --with-mbedtls --without-openssl: use the static mbedTLS built above for ZIP AES, and for
# the MD5 and SHA-1 digests that XAR needs (neither glibc nor musl has them).
# --with-xml2 --without-expat: XAR through the libxml2 built above, which configure finds
# with pkg-config. expat can read XAR but not write it.
./configure --cache-file="$CONFIG_CACHE" --build="$BUILD_TRIPLET" --host="$HOST" --prefix="$PREFIX" --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip --enable-posix-regex-lib=libc --with-pic --with-lzo2 --disable-shared --enable-static --with-mbedtls --without-openssl --with-xml2 --without-expat
# Fail the build unless configure enabled the XAR reader and writer (see build-linux-common.sh)
verify_xar_configured config.h
make -sj"$NCPU" install
cd ..

echo "Creating final shared library..."
# libarchive.a and the libraries it uses (and on 32-bit ARM, linux-arm-stack-guard.c), linked
# against the C library, failing on any unresolved symbol, and exporting archive_* only (see
# build-linux-common.sh)
link_libarchive libarchive.so ${STACK_GUARD:+"${SCRIPT_DIR}/${STACK_GUARD}"}

# Strip the debugging information that zig builds into its compiler-rt and glibc's
# libc_nonshared.a, before any check reads the library, keeping its symbol table, and fail
# the build if any debugging section is left (see build-linux-common.sh)
strip_debug_info libarchive.so
verify_no_debug_info libarchive.so

# Fail the build unless the mbedTLS crypto backend was linked in (see build-linux-common.sh)
verify_crypto_backend libarchive.so

# Fail the build unless the XAR reader and writer were linked in, with libxml2 (see build-linux-common.sh)
verify_xar_support libarchive.so

# Fail the build unless the library needs nothing but its C library, and loads with the
# oldest version of it that the runtime supports (see build-linux-common.sh)
if [ "$LIBC" = glibc ]; then
    # The glibc version that ends the zig target
    verify_glibc_imports libarchive.so "${ZIG_TARGET#*.}"
else
    verify_musl_imports libarchive.so
fi

# Fail the build unless the library exports libarchive's API alone, with every function that
# LibArchive.Net imports (see verify-exports.sh)
READELF="${AR%ar}readelf" "${SCRIPT_DIR}/verify-exports.sh" libarchive.so

# On 32-bit ARM, fail the build unless the stack protector's guard is the one that
# linux-arm-stack-guard.c seeds when the library loads, and its __stack_chk_fail the one that
# ends the process as the C library's own does: linux-arm-stack-guard.c's for glibc, and zig's
# compiler-rt's for musl (see build-linux-common.sh)
if [ -n "$STACK_GUARD" ]; then
    verify_stack_guard libarchive.so
    verify_stack_chk_fail libarchive.so "$LIBC"
fi

# The load test program, which is kept beside the library for CI to run with it in
# containers. A glibc library is load tested here too, in a glibc process: natively for
# x64, and under QEMU user-mode emulation for arm64 and arm (see build-linux-common.sh).
build_load_test "$LOADTEST" "$LIBC" "$LOADTEST_TARGET" "$LOADTEST_FLAGS"
if [ "$LIBC" = glibc ]; then
    test_load_glibc libarchive.so "$LOADTEST" ${QEMU:+"$QEMU" "$LOADER"}
else
    echo "${RID} is a musl library, which this build does not run: CI runs ${LOADTEST} with it in Alpine containers."
fi
file libarchive.so

# Fail the build unless lzop support was linked in and needs nothing the library lacks (see build-lzo-shim.sh)
verify_lzo_support libarchive.so
verify_lzo_shim_resolved libarchive.so

echo "Copying output to ${OUTPUT_DIR}..."
cp libarchive.so "${OUTPUT_DIR}/libarchive-${RID}.so"
cp "$LOADTEST" "${OUTPUT_DIR}/${LOADTEST}"

echo "Cleaning up build directory..."
cd /
rm -rf "${BUILD_DIR}"

echo "Linux ${RID} build complete: ${OUTPUT_DIR}/libarchive-${RID}.so and ${OUTPUT_DIR}/${LOADTEST}"
