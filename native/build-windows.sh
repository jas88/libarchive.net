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
    --disable-shared --enable-static --disable-nls \
    --disable-rpath --with-pic --disable-extra \
    ac_cv_func_mbrtowc=no ac_cv_func_wcrtomb=no \
    CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"

echo "Building libiconv library..."
# Build only the static library, skip programs and tests
make -j$NCPU CC=$CC AR=$AR lib/libiconv.a || {
    echo "✗ Standard build failed, attempting alternative build method..."
    cd lib

    # Alternative: build individual objects with fixed flags
    echo "Building individual libiconv objects..."
    OBJECTS=""
    for src in iconv.c; do
        echo "Compiling $src..."
        if ${CC} -c ${CFLAGS} -I../include -I../libcharset/include -I. \
            -DHAVE_CONFIG_H -DBUILDING_LIBICONV \
            -UHAVE_MBRTOWC -UHAVE_WCRTOMB \
            $src -o ${src%.c}.o; then
            OBJECTS="$OBJECTS ${src%.c}.o"
        else
            echo "✗ Failed to compile $src"
            exit 1
        fi
    done

    echo "Creating libiconv.a from objects..."
    ${AR} rcs libiconv.a $OBJECTS
    if [ -f libiconv.a ]; then
        echo "✓ Manual libiconv compilation succeeded"
        file libiconv.a
    else
        echo "✗ Manual libiconv compilation failed"
        exit 1
    fi
    cd ..
}

echo "Building libcharset directly (included in libiconv build)..."
cd libcharset/lib
if [ -f ../lib/config.h ]; then
    echo "✓ libcharset config available"
    if ${CC} -c ${CFLAGS} -I../lib/include -I. -I../lib/include \
        -DBUILDING_LIBCHARSET -DHAVE_CONFIG_H localcharset.c relocatable.c; then
        echo "✓ Manual libcharset compilation succeeded"
        ${AR} rcs libcharset.a localcharset.o relocatable.o
        touch Makefile  # Prevent make from running
        cd ../..
    else
        echo "✗ Manual libcharset compilation failed - required dependency for libxml2"
        exit 1
    fi
else
    echo "ERROR: libcharset config.h not found - configure may have failed"
    exit 1
fi


# Install manually if files exist
if [ -f lib/libiconv.a ]; then
    echo "Installing libiconv libraries..."
    mkdir -p "$PREFIX/lib" "$PREFIX/include"
    cp lib/libiconv.a "$PREFIX/lib/"
    cp include/iconv.h "$PREFIX/include/"
fi

if [ -f libcharset/lib/libcharset.a ]; then
    cp libcharset/lib/libcharset.a "$PREFIX/lib/"
    cp libcharset/include/libcharset.h libcharset/include/localcharset.h "$PREFIX/include/"
fi

# Verify libraries are proper archives
echo "=== Verifying libiconv installation ==="
if [ -f "$PREFIX/lib/libiconv.a" ]; then
    file "$PREFIX/lib/libiconv.a"
    ${AR} t "$PREFIX/lib/libiconv.a" | head -5
    echo "✓ libiconv.a built successfully"
else
    echo "⚠  libiconv.a not found - will proceed without iconv"
fi

if [ -f "$PREFIX/include/iconv.h" ]; then
    echo "✓ iconv.h installed successfully"
else
    echo "⚠  iconv.h not found - will proceed without iconv"
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
./configure --host=${MINGW_PREFIX} --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-iconv=$PREFIX --with-zlib=$PREFIX --with-lzma=$PREFIX
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
    --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-bsdunzip \
    --enable-posix-regex-lib=libc \
    --with-pic --with-zlib --with-bz2lib --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-xml2
make -j$NCPU install
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
