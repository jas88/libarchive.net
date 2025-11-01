#!/bin/bash
# Test script for libiconv build solutions with LLVM-MinGW

set -e

echo "=== Testing libiconv LLVM-MinGW Build Solutions ==="

# Check if MinGW is available
if ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
    echo "ERROR: MinGW not found. Please install mingw-w64"
    exit 1
fi

# Set up test environment
TEST_DIR="$(pwd)/test-libiconv-build"
rm -rf "$TEST_DIR"
mkdir -p "$TEST_DIR"
cd "$TEST_DIR"

# Download libiconv if not present
ICONV_VERSION="1.17"
if [ ! -d "libiconv-${ICONV_VERSION}" ]; then
    echo "Downloading libiconv ${ICONV_VERSION}..."
    curl -fsSL "https://ftp.gnu.org/pub/gnu/libiconv/libiconv-${ICONV_VERSION}.tar.gz" | tar xzf -
fi

cd libiconv-${ICONV_VERSION}

# Set up MinGW variables
MINGW_PREFIX="x86_64-w64-mingw32"
CC="${MINGW_PREFIX}-gcc"
AR="${MINGW_PREFIX}-ar"
PREFIX="$(pwd)/local"
CFLAGS="-O2 -fPIC"
CPPFLAGS="-I$PREFIX/include"
LDFLAGS="-L$PREFIX/lib"

echo "Testing different build approaches..."

# Test 1: Configure with mbrtowc disabled
echo -e "\n=== Test 1: Configure with mbrtowc disabled ==="
mkdir -p build-test-1
cd build-test-1

../configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --disable-shared --enable-static --disable-nls \
    --disable-rpath --with-pic --disable-extra \
    ac_cv_func_mbrtowc=no ac_cv_func_wcrtomb=no \
    CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"

echo "✓ Configure completed successfully"
if make -j$(nproc) CC=$CC AR=$AR lib/libiconv.a; then
    echo "✓ Build succeeded with configure flags"
    file lib/libiconv.a
    $AR t lib/libiconv.a | head -3
else
    echo "✗ Build failed with configure flags"
fi
cd ..

# Test 2: Manual compilation with flags
echo -e "\n=== Test 2: Manual compilation with fixed flags ==="
mkdir -p build-test-2
cd build-test-2

# Copy config files from test 1 if they exist
if [ -f ../build-test-1/config.h ]; then
    cp ../build-test-1/config.h .
    cp ../build-test-1/include/iconv.h ../include/ 2>/dev/null || true
fi

# Generate config if not available
if [ ! -f config.h ]; then
    echo "Generating config.h..."
    ../configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
        --disable-shared --enable-static --disable-nls \
        ac_cv_func_mbrtowc=no ac_cv_func_wcrtomb=no \
        CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"
    cp config.h ../build-test-2/
fi

mkdir -p lib
cd lib

echo "Compiling iconv.c with fixed flags..."
if $CC -c $CFLAGS -I../../include -I../../libcharset/include -I. \
    -DHAVE_CONFIG_H -DBUILDING_LIBICONV \
    -UHAVE_MBRTOWC -UHAVE_WCRTOMB \
    ../../lib/iconv.c -o iconv.o; then
    echo "✓ iconv.c compilation succeeded"

    if $AR rcs libiconv.a iconv.o; then
        echo "✓ Archive creation succeeded"
        file libiconv.a

        # Test if archive is recognizable
        echo "Testing archive recognition..."
        if $AR t libiconv.a >/dev/null 2>&1; then
            echo "✓ Archive is properly formatted"
        else
            echo "✗ Archive format is not recognized"
        fi
    else
        echo "✗ Archive creation failed"
    fi
else
    echo "✗ iconv.c compilation failed"
fi
cd ../../

# Test 3: Test with a simple program (if libiconv.a was built)
echo -e "\n=== Test 3: Test linking with simple program ==="
test_iconv_lib=""
for dir in build-test-1/lib build-test-2/lib; do
    if [ -f "$dir/libiconv.a" ]; then
        test_iconv_lib="$dir/libiconv.a"
        break
    fi
done

if [ -n "$test_iconv_lib" ]; then
    echo "Testing libiconv.a: $test_iconv_lib"

    # Create a simple test program
    cat > test_iconv.c << 'EOF'
#include <stdio.h>
#include <stdlib.h>
#include "../libiconv-1.17/include/iconv.h"

int main() {
    iconv_t cd = iconv_open("UTF-8", "ASCII");
    if (cd == (iconv_t)-1) {
        printf("iconv_open failed (expected for test)\n");
        return 1;
    }
    iconv_close(cd);
    printf("iconv test completed successfully\n");
    return 0;
}
EOF

    echo "Compiling test program..."
    if $CC -o test_iconv test_iconv.c -I../libiconv-1.17/include \
        "$test_iconv_lib" 2>/dev/null; then
        echo "✓ Test program compiled successfully"
        echo "✓ libiconv.a can be linked properly"
    else
        echo "✗ Test program compilation failed"
        echo "This indicates a static library format issue"
    fi
else
    echo "⚠ No working libiconv.a found to test linking"
fi

echo -e "\n=== Summary ==="
echo "Test 1 (configure flags): $([ -f build-test-1/lib/libiconv.a ] && echo "✓ PASSED" || echo "✗ FAILED")"
echo "Test 2 (manual build): $([ -f build-test-2/lib/libiconv.a ] && echo "✓ PASSED" || echo "✗ FAILED")"

# Show working solution
for dir in build-test-1 build-test-2; do
    if [ -f "$dir/lib/libiconv.a" ]; then
        echo -e "\n=== Working Solution Found ==="
        echo "Directory: $dir"
        echo "Archive file: $dir/lib/libiconv.a"
        file "$dir/lib/libiconv.a"
        echo "Size: $(ls -lh "$dir/lib/libiconv.a" | awk '{print $5}')"
        echo "Contents:"
        $AR t "$dir/lib/libiconv.a" | head -5
        break
    fi
done

cd ../..
echo -e "\nTest completed. Results available in: $TEST_DIR"