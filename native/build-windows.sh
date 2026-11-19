#!/bin/sh
# Build libarchive for Windows (x86, x64, arm64) using MinGW cross-compiler
# This script should be run on Linux with MinGW installed

set -e

# Detect platform and set up cross-compilation
ARCH="${ARCH:-x86_64}"

# Load shared configuration
. "$(dirname "$0")/build-config.sh"

# Absolute path of this script's directory: the lzop shim sources are found relative to
# it after the build has changed directory
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

case "$ARCH" in
    x86_64|x64)
        MINGW_PREFIX="x86_64-w64-mingw32"
        OUTPUT_NAME="archive-x64.dll"
        ARCH_FLAGS=""
        ;;
    i686|x86)
        MINGW_PREFIX="i686-w64-mingw32"
        OUTPUT_NAME="archive-x86.dll"
        ARCH_FLAGS="-m32"
        ;;
    aarch64|arm64)
        MINGW_PREFIX="aarch64-w64-mingw32"
        OUTPUT_NAME="archive-arm64.dll"
        ARCH_FLAGS=""
        ;;
    *)
        echo "Error: Unsupported architecture: $ARCH"
        echo "Supported: x86_64, i686, aarch64"
        exit 1
        ;;
esac

echo "Building for Windows ${ARCH} using ${MINGW_PREFIX}..."

# The compilers. Debian and Ubuntu install each MinGW GCC in two thread models,
# <prefix>-gcc-win32 and <prefix>-gcc-posix, and point <prefix>-gcc at one of them
# through update-alternatives. Use the win32 ones by name where they exist: that fixes
# the thread model (see THIRD-PARTY-NOTICES.txt), and the build does not depend on the
# alternatives links, which a package cache restored without the packages' install
# scripts lacks. Elsewhere (other distributions, LLVM-MinGW) use the plain names.
MINGW_CC="${MINGW_PREFIX}-gcc"
MINGW_CXX="${MINGW_PREFIX}-g++"
if command -v "${MINGW_CC}-win32" >/dev/null 2>&1; then
    MINGW_CC="${MINGW_CC}-win32"
fi
if command -v "${MINGW_CXX}-win32" >/dev/null 2>&1; then
    MINGW_CXX="${MINGW_CXX}-win32"
fi

# Check if MinGW is installed
if ! command -v "${MINGW_CC}" >/dev/null 2>&1; then
    echo "Error: ${MINGW_PREFIX}-gcc not found"
    echo "Please install MinGW cross-compiler for ${ARCH}"
    echo "  Ubuntu/Debian: sudo apt-get install mingw-w64"
    echo "  Fedora: sudo dnf install mingw64-gcc mingw32-gcc"
    exit 1
fi
# The lzop shim is C++: GCC's g++ for x86 and x64, LLVM-MinGW's Clang wrapper for arm64
if ! command -v "${MINGW_CXX}" >/dev/null 2>&1; then
    echo "Error: ${MINGW_PREFIX}-g++ not found"
    echo "Please install the MinGW C++ cross-compiler for ${ARCH}"
    echo "  Ubuntu/Debian: sudo apt-get install mingw-w64"
    echo "  Fedora: sudo dnf install mingw64-gcc-c++ mingw32-gcc-c++"
    exit 1
fi

# Windows-specific build settings
if command -v sccache >/dev/null 2>&1; then
    export CC="sccache ${MINGW_CC}"
    export CXX="sccache ${MINGW_CXX}"
else
    export CC="${MINGW_CC}"
    export CXX="${MINGW_CXX}"
fi
echo "C compiler: ${MINGW_CC}; C++ compiler: ${MINGW_CXX}"
export AR="${MINGW_PREFIX}-ar"
export RANLIB="${MINGW_PREFIX}-ranlib"
export RC="${MINGW_PREFIX}-windres"
export STRIP="${MINGW_PREFIX}-strip"

export PREFIX="${PREFIX}-${ARCH}"
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-O2 -fPIC ${ARCH_FLAGS}"
export CXXFLAGS="-O2 -fPIC ${ARCH_FLAGS}"
export LDFLAGS="-L$PREFIX/lib"

# Download and unpack fresh copies of all libraries, as the other build scripts do, even
# where an earlier run left them unpacked: that run has patched libarchive, and
# patch_libarchive refuses sources that are already patched.
echo "Setting up library sources..."
download_all_libraries

# Every narrow string UTF-8, whatever the locale (see "The libarchive patch" in BUILD.md)
patch_libarchive "${SCRIPT_DIR}" "libarchive-${LIBARCHIVE_VERSION}"

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
cd lz4-${LZ4_VERSION}/lib
make -j$NCPU liblz4.a "CC=$CC" "AR=$AR"
mkdir -p $PREFIX/lib $PREFIX/include
cp liblz4.a $PREFIX/lib/
cp lz4.h lz4hc.h lz4frame.h $PREFIX/include/
cd ../..

echo "Building bzip2 ${BZIP2_VERSION}..."
cd bzip2-${BZIP2_VERSION}
make -j$NCPU libbz2.a "CC=$CC" "AR=$AR" "RANLIB=$RANLIB" CFLAGS="$CFLAGS -D_FILE_OFFSET_BITS=64"
# Verify library was built and has symbols
ls -lh libbz2.a
${MINGW_PREFIX}-nm -g libbz2.a | grep BZ2_bzCompressInit || echo "WARNING: BZ2_bzCompressInit not found in libbz2.a"
mkdir -p $PREFIX/lib $PREFIX/include
cp libbz2.a $PREFIX/lib/
cp bzlib.h $PREFIX/include/
cd ..

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
CHOST=${MINGW_PREFIX} ./configure --static --prefix=$PREFIX
make -j$NCPU install
cd ..

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --cache-file=$(get_config_cache ${MINGW_PREFIX}) --host=${MINGW_PREFIX} --with-pic --disable-shared --prefix=$PREFIX --disable-scripts --disable-doc
make -j$NCPU install
cd ..

# lzop (LZO1X) support: lzokay behind the liblzo2 API, in place of the GPL-licensed liblzo2
# shellcheck source-path=SCRIPTDIR disable=SC1091
. "${SCRIPT_DIR}/build-lzo-shim.sh"
build_lzo_shim "${SCRIPT_DIR}/lzo-shim" "lzokay-${LZOKAY_COMMIT}" "$CXXFLAGS"

echo "Building zstd ${ZSTD_VERSION}..."
cd zstd-${ZSTD_VERSION}/lib
make -j$NCPU libzstd.a "CC=$CC" "AR=$AR"
mkdir -p $PREFIX/lib $PREFIX/include
cp libzstd.a $PREFIX/lib/
cp zstd.h zstd_errors.h zdict.h $PREFIX/include/
cd ../..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
# --without-iconv: Windows has native encoding support, avoids needing libiconv.
# libxml2 has had no lzma support (and no --with-lzma option) since 2.14.
./configure --cache-file=$(get_config_cache ${MINGW_PREFIX}) --host=${MINGW_PREFIX} --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --without-iconv --with-zlib=$PREFIX
make -j$NCPU install
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
# Set PKG_CONFIG_LIBDIR so pkg-config only looks at our prefix
export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"
# For static linking tests, autoconf needs all dependencies in LDFLAGS
export LDFLAGS="$LDFLAGS -L$PREFIX/lib -lz -llzma"
# Use libarchive-specific cache to avoid conflicts from modified LDFLAGS/PKG_CONFIG_LIBDIR
./configure --cache-file=$(get_config_cache ${MINGW_PREFIX}-libarchive) --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared \
    --disable-bsdtar --disable-bsdcat --disable-bsdcpio \
    --enable-posix-regex-lib=libc \
    --with-pic --with-zlib --with-bz2lib --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-xml2
make -j$NCPU install
cd ..

echo "Creating Windows DLL..."
# Verify all libraries exist before linking
for lib in libarchive libxml2 libz liblzma liblzo2 libzstd liblz4 libbz2; do
    if [ ! -f "$PREFIX/lib/${lib}.a" ]; then
        echo "ERROR: $PREFIX/lib/${lib}.a not found!"
        exit 1
    fi
    echo "$PREFIX/lib/${lib}.a: $(ls -lh $PREFIX/lib/${lib}.a | awk '{print $5}')"
done
# Use --start-group for all dependency libraries to allow multi-pass symbol resolution
# This is needed because libxml2 depends on libz and liblzma
${CC} -shared -o ${OUTPUT_NAME} \
    -Wl,--whole-archive \
    $PREFIX/lib/libarchive.a \
    -Wl,--no-whole-archive \
    -Wl,--start-group \
    $PREFIX/lib/libxml2.a \
    $PREFIX/lib/libz.a \
    $PREFIX/lib/liblzma.a \
    "$PREFIX/lib/liblzo2.a" \
    $PREFIX/lib/libzstd.a \
    $PREFIX/lib/liblz4.a \
    $PREFIX/lib/libbz2.a \
    -Wl,--end-group \
    -static -static-libgcc -static-libstdc++ \
    -lws2_32 -lbcrypt -lkernel32

echo "Testing DLL..."
file ${OUTPUT_NAME}

echo "=== Checking DLL dependencies ==="
${MINGW_PREFIX}-objdump -p ${OUTPUT_NAME} | grep "DLL Name:" || echo "No external DLL dependencies"

echo "=== Inspecting symbols (before stripping) ==="
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep -c " T " | xargs echo "Defined symbols:"
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep -c " U " | xargs echo "Undefined symbols:"
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep -E "(__udivdi3|__umoddi3|__divdi3|__moddi3)" || echo "No 64-bit division intrinsics found"

# Fail the build unless lzop support was linked in. This has to run before the DLL is
# stripped (see build-lzo-shim.sh).
verify_lzo_support "${OUTPUT_NAME}"

echo "Stripping debug symbols..."
${STRIP} ${OUTPUT_NAME} || echo "WARNING: strip failed, continuing with unstripped DLL"
ls -lh ${OUTPUT_NAME}

echo "Windows ${ARCH} build complete: ${OUTPUT_NAME}"
