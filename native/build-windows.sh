#!/bin/sh
# Build libarchive for Windows (x86, x64, arm64) using MinGW cross-compiler
# This script should be run on Linux with MinGW installed

set -e

# Detect platform and set up cross-compilation
ARCH="${ARCH:-x86_64}"

# Load shared configuration
. "$(dirname "$0")/build-config.sh"

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

# Check if MinGW is installed
if ! command -v ${MINGW_PREFIX}-gcc >/dev/null 2>&1; then
    echo "Error: ${MINGW_PREFIX}-gcc not found"
    echo "Please install MinGW cross-compiler for ${ARCH}"
    echo "  Ubuntu/Debian: sudo apt-get install mingw-w64"
    echo "  Fedora: sudo dnf install mingw64-gcc mingw32-gcc"
    exit 1
fi

# Windows-specific build settings
export CC="${MINGW_PREFIX}-gcc"
export CXX="${MINGW_PREFIX}-g++"
export AR="${MINGW_PREFIX}-ar"
export RANLIB="${MINGW_PREFIX}-ranlib"
export RC="${MINGW_PREFIX}-windres"
export STRIP="${MINGW_PREFIX}-strip"

export PREFIX="${PREFIX}-${ARCH}"
export CPPFLAGS="-I$PREFIX/include"
export CFLAGS="-O2 -fPIC ${ARCH_FLAGS}"
export CXXFLAGS="-O2 -fPIC ${ARCH_FLAGS}"
export LDFLAGS="-L$PREFIX/lib"

# Download all libraries if not already present
if [ ! -d "libarchive-${LIBARCHIVE_VERSION}" ]; then
    echo "Downloading library sources..."
    download_all_libraries
else
    echo "Using pre-downloaded library sources"
fi

# Build libiconv first (needed by libxml2)
echo "Building libiconv ${ICONV_VERSION}..."
cd libiconv-${ICONV_VERSION}

# Use specific configure flags to avoid mbrtowc conflicts with LLVM-MinGW
echo "Running configure with mbrtowc conflict fixes..."
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --disable-shared --enable-static --disable-nls \
    --disable-rpath --with-pic --disable-extra \
    ac_cv_func_mbrtowc=no ac_cv_func_wcrtomb=no \
    CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"

echo "Building libiconv and libcharset..."
# Export CC and AR so make uses them correctly
export CC AR
make -j$NCPU

# Verify build outputs exist and are valid archives before installing
echo "Checking libiconv build outputs..."
if [ ! -f "lib/.libs/libiconv.a" ]; then
    echo "ERROR: lib/.libs/libiconv.a not found after build"
    ls -la lib/.libs/ || echo "lib/.libs/ directory does not exist"
    exit 1
fi

# Verify it's an actual ar archive, not a libtool text file
if ! file lib/.libs/libiconv.a | grep -q "current ar archive"; then
    echo "ERROR: lib/.libs/libiconv.a is not a valid ar archive:"
    file lib/.libs/libiconv.a
    exit 1
fi

if [ ! -f "libcharset/lib/.libs/libcharset.a" ]; then
    echo "ERROR: libcharset/lib/.libs/libcharset.a not found after build"
    ls -la libcharset/lib/.libs/ || echo "libcharset/lib/.libs/ directory does not exist"
    exit 1
fi

if ! file libcharset/lib/.libs/libcharset.a | grep -q "current ar archive"; then
    echo "ERROR: libcharset/lib/.libs/libcharset.a is not a valid ar archive:"
    file libcharset/lib/.libs/libcharset.a
    exit 1
fi

# Install libraries from build directories
echo "Installing libiconv libraries..."
mkdir -p "$PREFIX/lib" "$PREFIX/include"
# Install actual archives from .libs, not libtool wrappers
cp lib/.libs/libiconv.a "$PREFIX/lib/"
cp include/iconv.h.inst "$PREFIX/include/iconv.h"
cp libcharset/lib/.libs/libcharset.a "$PREFIX/lib/"
cp libcharset/include/libcharset.h.inst "$PREFIX/include/libcharset.h"
cp libcharset/include/localcharset.h "$PREFIX/include/"

# Remove any libtool .la files that might confuse linkers (especially lld)
rm -f "$PREFIX/lib"/*.la

# Verify libraries are proper archives
echo "=== Verifying libiconv installation ==="
if [ -f "$PREFIX/lib/libiconv.a" ]; then
    file "$PREFIX/lib/libiconv.a"
    ${AR} t "$PREFIX/lib/libiconv.a" | head -5
    echo "✓ libiconv.a built successfully"
else
    echo "ERROR: libiconv.a not found"
    exit 1
fi

if [ -f "$PREFIX/include/iconv.h" ]; then
    echo "✓ iconv.h installed successfully"
else
    echo "ERROR: iconv.h not found"
    exit 1
fi

# libcharset is required - must be built successfully
echo "=== Verifying libcharset installation ==="
if [ -f "$PREFIX/lib/libcharset.a" ]; then
    file "$PREFIX/lib/libcharset.a"
    ${AR} t "$PREFIX/lib/libcharset.a" | head -5
    echo "✓ libcharset.a built successfully"
else
    echo "ERROR: libcharset.a not found - required dependency for libxml2"
    exit 1
fi

cd ..

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
cd lz4-${LZ4_VERSION}/lib
make -j$NCPU liblz4.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp liblz4.a $PREFIX/lib/
cp lz4.h lz4hc.h lz4frame.h $PREFIX/include/
cd ../..

echo "Building bzip2 ${BZIP2_VERSION}..."
cd bzip2-${BZIP2_VERSION}
make -j$NCPU libbz2.a CC=$CC AR=$AR RANLIB=$RANLIB CFLAGS="$CFLAGS -D_FILE_OFFSET_BITS=64"
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
# Touch autotools-generated files to prevent rebuild attempts
touch aclocal.m4 configure Makefile.in */Makefile.in */*/Makefile.in 2>/dev/null || true
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --disable-shared --with-pic \
    --disable-xz --disable-xzdec --disable-lzmadec --disable-lzmainfo \
    --disable-lzma-links --disable-scripts --disable-doc \
    --disable-nls --disable-rpath
make -j$NCPU install
rm -f "$PREFIX/lib"/*.la
cd ..

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared --with-pic
make -j$NCPU install
rm -f "$PREFIX/lib"/*.la
cd ..

echo "Building zstd ${ZSTD_VERSION}..."
cd zstd-${ZSTD_VERSION}/lib
make -j$NCPU libzstd.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp libzstd.a $PREFIX/lib/
cp zstd.h zstd_errors.h zdict.h $PREFIX/include/
cd ../..

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
# Set explicit iconv flags to avoid libtool static library warnings
export ICONV_CFLAGS="-I$PREFIX/include"
export ICONV_LIBS="-liconv -lcharset"
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared \
    --with-iconv=$PREFIX --with-zlib=$PREFIX --with-lzma=$PREFIX \
    --without-python --without-catalog --without-debug \
    --without-http --without-ftp --without-threads \
    --without-icu --without-history
make -j$NCPU install
rm -f "$PREFIX/lib"/*.la
cd ..

echo "Building libarchive ${LIBARCHIVE_VERSION}..."
cd libarchive-${LIBARCHIVE_VERSION}
# Set PKG_CONFIG_LIBDIR so pkg-config only looks at our prefix
export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"
# For static linking tests, autoconf needs all dependencies in LDFLAGS
export LDFLAGS="$LDFLAGS -L$PREFIX/lib -lz -llzma"
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --enable-silent-rules --disable-dependency-tracking \
    --enable-static --disable-shared \
    --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip \
    --disable-acl --disable-xattr \
    --enable-posix-regex-lib=libc \
    --with-pic --with-zlib --with-bz2lib --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-xml2 \
    --without-expat
make -j$NCPU install
rm -f "$PREFIX/lib"/*.la
cd ..

echo "Creating Windows DLL..."
# Verify all libraries exist before linking
for lib in libarchive libxml2 libiconv libcharset libz liblzma liblzo2 libzstd liblz4 libbz2; do
    if [ ! -f "$PREFIX/lib/${lib}.a" ]; then
        echo "ERROR: $PREFIX/lib/${lib}.a not found!"
        exit 1
    fi
    echo "$PREFIX/lib/${lib}.a: $(ls -lh $PREFIX/lib/${lib}.a | awk '{print $5}')"
done
# Use --start-group for all dependency libraries to allow multi-pass symbol resolution
# This is needed because libxml2 depends on libz, liblzma, and libiconv
${CC} -shared -o ${OUTPUT_NAME} \
    -Wl,--whole-archive \
    $PREFIX/lib/libarchive.a \
    -Wl,--no-whole-archive \
    -Wl,--start-group \
    $PREFIX/lib/libxml2.a \
    $PREFIX/lib/libiconv.a \
    $PREFIX/lib/libcharset.a \
    $PREFIX/lib/libz.a \
    $PREFIX/lib/liblzma.a \
    $PREFIX/lib/liblzo2.a \
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

echo "Stripping debug symbols..."
${STRIP} ${OUTPUT_NAME} || echo "WARNING: strip failed, continuing with unstripped DLL"
ls -lh ${OUTPUT_NAME}

echo "Windows ${ARCH} build complete: ${OUTPUT_NAME}"
