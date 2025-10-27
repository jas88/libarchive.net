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
./configure --host=${MINGW_PREFIX} --with-pic --disable-shared --prefix=$PREFIX --disable-scripts --disable-doc
make -j$NCPU install
cd ..

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX --disable-shared
make -j$NCPU install
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
./configure --host=${MINGW_PREFIX} --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX --with-lzma=$PREFIX
make -j$NCPU install
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
    $PREFIX/lib/liblzo2.a \
    $PREFIX/lib/libzstd.a \
    $PREFIX/lib/liblz4.a \
    $PREFIX/lib/libbz2.a \
    -Wl,--end-group \
    -static -static-libgcc -static-libstdc++ \
    -lws2_32 -lbcrypt -lkernel32

echo "Testing DLL..."
file ${OUTPUT_NAME}
echo "Stripping debug symbols..."
${STRIP} ${OUTPUT_NAME} || echo "WARNING: strip failed, continuing with unstripped DLL"
ls -lh ${OUTPUT_NAME}

echo "Windows ${ARCH} build complete: ${OUTPUT_NAME}"
