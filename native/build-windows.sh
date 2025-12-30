#!/bin/sh
# Build libarchive for Windows (x86, x64, arm64) using MinGW cross-compiler
# This script should be run on Linux with MinGW installed

set -e

# Get absolute path to script directory
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

# Detect platform and set up cross-compilation
ARCH="${ARCH:-x86_64}"

# Load shared configuration
. "${SCRIPT_DIR}/build-config.sh"

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
# Use function sections to enable dead code elimination with --gc-sections
# Use -fno-unique-section-names to reduce section name bloat
export CFLAGS="-O2 -fPIC ${ARCH_FLAGS} -ffunction-sections -fdata-sections -fno-unique-section-names"
export CXXFLAGS="-O2 -fPIC ${ARCH_FLAGS} -ffunction-sections -fdata-sections -fno-unique-section-names"
export LDFLAGS="-L$PREFIX/lib"

# Download all libraries if not already present
if [ ! -d "libarchive-${LIBARCHIVE_VERSION}" ]; then
    echo "Downloading library sources..."
    download_all_libraries
else
    echo "Using pre-downloaded library sources"
fi

# Initialize static library verification file
export STATIC_LIBS_FILE="$(pwd)/static-libs-${ARCH}.txt"
echo "Static Library Verification Report" > "$STATIC_LIBS_FILE"
echo "Platform: Windows ${ARCH} (MinGW)" >> "$STATIC_LIBS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$STATIC_LIBS_FILE"
echo "" >> "$STATIC_LIBS_FILE"

# Build compression libraries
echo "Building lz4 ${LZ4_VERSION}..."
cd lz4-${LZ4_VERSION}/lib
make -j$NCPU liblz4.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp liblz4.a $PREFIX/lib/
cp lz4.h lz4hc.h lz4frame.h $PREFIX/include/
cd ../..
verify_static_lib "$PREFIX/lib/liblz4.a" "${MINGW_PREFIX}-nm"

echo "Building bzip2 ${BZIP2_VERSION}..."
cd bzip2-${BZIP2_VERSION}
make -sj$NCPU libbz2.a CC=$CC AR=$AR RANLIB=$RANLIB CFLAGS="$CFLAGS -w -D_FILE_OFFSET_BITS=64"
# Verify library was built and has symbols
ls -lh libbz2.a
${MINGW_PREFIX}-nm -g libbz2.a | grep BZ2_bzCompressInit || echo "WARNING: BZ2_bzCompressInit not found in libbz2.a"
mkdir -p $PREFIX/lib $PREFIX/include
cp libbz2.a $PREFIX/lib/
cp bzlib.h $PREFIX/include/
cd ..
verify_static_lib "$PREFIX/lib/libbz2.a" "${MINGW_PREFIX}-nm"

echo "Building zlib ${ZLIB_VERSION}..."
cd zlib-${ZLIB_VERSION}
CHOST=${MINGW_PREFIX} ./configure --static --prefix=$PREFIX
make -j$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libz.a" "${MINGW_PREFIX}-nm"

echo "Building xz ${XZ_VERSION}..."
cd xz-${XZ_VERSION}
./configure --quiet --cache-file=$(get_config_cache ${MINGW_PREFIX}) --host=${MINGW_PREFIX} --with-pic --disable-shared --prefix=$PREFIX --disable-scripts --disable-doc
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzma.a" "${MINGW_PREFIX}-nm"

echo "Building lzo ${LZO_VERSION}..."
cd lzo-${LZO_VERSION}
./configure --quiet --cache-file=$(get_config_cache ${MINGW_PREFIX}) --host=${MINGW_PREFIX} --prefix=$PREFIX --disable-shared
make -sj$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/liblzo2.a" "${MINGW_PREFIX}-nm"

echo "Building zstd ${ZSTD_VERSION}..."
cd zstd-${ZSTD_VERSION}/lib
make -j$NCPU libzstd.a CC=$CC AR=$AR
mkdir -p $PREFIX/lib $PREFIX/include
cp libzstd.a $PREFIX/lib/
cp zstd.h zstd_errors.h zdict.h $PREFIX/include/
cd ../..
verify_static_lib "$PREFIX/lib/libzstd.a" "${MINGW_PREFIX}-nm"

echo "Building libxml2 ${LIBXML2_VERSION}..."
cd libxml2-${LIBXML2_VERSION}
# --without-iconv: Windows has native encoding support, avoids needing libiconv
./configure --cache-file=$(get_config_cache ${MINGW_PREFIX}) --host=${MINGW_PREFIX} --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --without-iconv --with-zlib=$PREFIX --with-lzma=$PREFIX
make -j$NCPU install
cd ..
verify_static_lib "$PREFIX/lib/libxml2.a" "${MINGW_PREFIX}-nm"

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
verify_static_lib "$PREFIX/lib/libarchive.a" "${MINGW_PREFIX}-nm"

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
# Use .def file to export only functions needed by libarchive.net
# Disable automatic symbol export so only .def symbols are exported
# Use --gc-sections for dead code elimination
# Note: LLD automatically preserves CRT init sections on Windows since they're
# referenced by the entry point stub that calls DllMain
${CC} -shared -o ${OUTPUT_NAME} \
    -Wl,--gc-sections \
    -Wl,--exclude-all-symbols \
    "${SCRIPT_DIR}/libarchive.def" \
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

# Generate dependency verification report
DEPS_FILE="$(pwd)/dependencies-${ARCH}.txt"

echo "=== Dependency Verification ===" > "$DEPS_FILE"
echo "Platform: Windows ${ARCH} (MinGW)" >> "$DEPS_FILE"
echo "Date: $(date -u +%Y-%m-%dT%H:%M:%SZ)" >> "$DEPS_FILE"
echo "" >> "$DEPS_FILE"

echo "=== DLL Dependencies ===" >> "$DEPS_FILE"
${MINGW_PREFIX}-objdump -p ${OUTPUT_NAME} | grep "DLL Name:" >> "$DEPS_FILE" || echo "No external DLL dependencies" >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Exported Symbols (API) ===" >> "$DEPS_FILE"
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep " T " | awk '{print $3}' | sort >> "$DEPS_FILE"

echo "" >> "$DEPS_FILE"
echo "=== Imported Symbols (by DLL) ===" >> "$DEPS_FILE"
# Use nm to find undefined symbols (imports) - these are the actual runtime dependencies
# Format: "U symbolname" for undefined/imported symbols
${MINGW_PREFIX}-nm ${OUTPUT_NAME} 2>/dev/null | grep " U " | awk '{print $2}' | sort -u >> "$DEPS_FILE"

# Count imports
echo "" >> "$DEPS_FILE"
echo "=== Import Summary ===" >> "$DEPS_FILE"
IMPORT_COUNT=$(${MINGW_PREFIX}-nm ${OUTPUT_NAME} 2>/dev/null | grep -c " U " || echo 0)
echo "Total imported symbols: $IMPORT_COUNT" >> "$DEPS_FILE"

# Group by prefix to show which libraries they likely come from
echo "" >> "$DEPS_FILE"
echo "=== Import Categories ===" >> "$DEPS_FILE"
${MINGW_PREFIX}-nm ${OUTPUT_NAME} 2>/dev/null | grep " U " | awk '{print $2}' | sort -u | awk '
    /^__imp_/ { sub(/^__imp_/, ""); }
    /^_*BCrypt/ { bcrypt++; next }
    /^_*(Get|Set|Create|Delete|Close|Read|Write|Find|Load|Free|Virtual|Heap|Local|Global|Query|Format|Multi|Wide|Sleep|Wait|Enter|Leave|Initialize|Terminate|Rtl|Tls|Interlocked)/ { kernel32++; next }
    /^_*(WSA|send|recv|socket|connect|bind|listen|accept|select|gethost|getaddr|inet_|hton|ntoh)/ { ws2_32++; next }
    /^_*(__acrt|__std|_errno|_invalid_parameter|_crt|_initterm|_seh|_c_exit|_cexit|_exit|_amsg|_set_|_get_|_matherr|_controlfp|_fmode|_commode)/ { ucrt_private++; next }
    /^_*(malloc|free|calloc|realloc|_malloc|_free|_calloc|_realloc|_expand|_msize)/ { ucrt_heap++; next }
    /^_*(fopen|fclose|fread|fwrite|fseek|ftell|fflush|fgets|fputs|fprintf|fscanf|fgetc|fputc|feof|ferror|clearerr|rewind|tmpfile|tmpnam|remove|rename|stdin|stdout|stderr|_fileno|_fdopen|_wfopen)/ { ucrt_stdio++; next }
    /^_*(printf|sprintf|snprintf|vprintf|vsprintf|vsnprintf|scanf|sscanf|puts|gets|getchar|putchar)/ { ucrt_stdio++; next }
    /^_*(memcpy|memmove|memset|memcmp|memchr|strlen|strcpy|strncpy|strcat|strncat|strcmp|strncmp|strchr|strrchr|strstr|strtok|strdup|_strdup|wcs|_wcs|mbstowcs|wcstombs)/ { ucrt_string++; next }
    /^_*(time|mktime|localtime|gmtime|strftime|difftime|clock|_time|_mktime|_localtime|_gmtime|_strftime)/ { ucrt_time++; next }
    /^_*(strtol|strtoul|strtoll|strtoull|strtod|strtof|atoi|atol|atoll|atof|_strtoi64|_strtoui64)/ { ucrt_convert++; next }
    /^_*(getenv|_putenv|_wgetenv|_wputenv|environ|_environ)/ { ucrt_env++; next }
    /^_*(sin|cos|tan|asin|acos|atan|atan2|sinh|cosh|tanh|exp|log|log10|pow|sqrt|ceil|floor|fabs|fmod|ldexp|frexp|modf)/ { ucrt_math++; next }
    /^_*(qsort|bsearch|abs|labs|llabs|div|ldiv|lldiv|rand|srand)/ { ucrt_utility++; next }
    /^_*(stat|_stat|fstat|_fstat|access|_access|chmod|_chmod|mkdir|_mkdir|rmdir|_rmdir|chdir|_chdir|getcwd|_getcwd|unlink|_unlink)/ { ucrt_filesystem++; next }
    /^_*(setlocale|localeconv|_setlocale|_create_locale)/ { ucrt_locale++; next }
    { other++ }
    END {
        if (bcrypt) printf "bcrypt.dll: %d\n", bcrypt
        if (kernel32) printf "KERNEL32.dll: %d\n", kernel32
        if (ws2_32) printf "WS2_32.dll: %d\n", ws2_32
        if (ucrt_heap) printf "ucrt-heap: %d\n", ucrt_heap
        if (ucrt_stdio) printf "ucrt-stdio: %d\n", ucrt_stdio
        if (ucrt_string) printf "ucrt-string: %d\n", ucrt_string
        if (ucrt_time) printf "ucrt-time: %d\n", ucrt_time
        if (ucrt_convert) printf "ucrt-convert: %d\n", ucrt_convert
        if (ucrt_env) printf "ucrt-environment: %d\n", ucrt_env
        if (ucrt_math) printf "ucrt-math: %d\n", ucrt_math
        if (ucrt_utility) printf "ucrt-utility: %d\n", ucrt_utility
        if (ucrt_filesystem) printf "ucrt-filesystem: %d\n", ucrt_filesystem
        if (ucrt_locale) printf "ucrt-locale: %d\n", ucrt_locale
        if (ucrt_private) printf "ucrt-private: %d\n", ucrt_private
        if (other) printf "other/uncategorized: %d\n", other
    }
' >> "$DEPS_FILE"

echo "=== Checking DLL dependencies ==="
${MINGW_PREFIX}-objdump -p ${OUTPUT_NAME} | grep "DLL Name:" || echo "No external DLL dependencies"

# Fail on unexpected DLL dependencies
# Allow: Windows system DLLs and Universal CRT (api-ms-win-crt-*) which is standard on Windows 10+
# Note: grep returns 1 when no matches found (which is success for us), so add || true
UNEXPECTED=$(${MINGW_PREFIX}-objdump -p ${OUTPUT_NAME} | grep "DLL Name:" | grep -viE "KERNEL32.dll|WS2_32.dll|BCRYPT.dll|ADVAPI32.dll|ntdll.dll|api-ms-win-crt-" || true)
if [ -n "$UNEXPECTED" ]; then
    echo "ERROR: Unexpected DLL dependencies found:"
    echo "$UNEXPECTED"
    exit 1
fi
echo "Dependency check passed: only Windows system DLLs linked"

echo "=== Inspecting symbols (before stripping) ==="
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep -c " T " | xargs echo "Defined symbols:"
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep -c " U " | xargs echo "Undefined symbols:"
${MINGW_PREFIX}-nm ${OUTPUT_NAME} | grep -E "(__udivdi3|__umoddi3|__divdi3|__moddi3)" || echo "No 64-bit division intrinsics found"

echo "Stripping debug symbols..."
${STRIP} ${OUTPUT_NAME} || echo "WARNING: strip failed, continuing with unstripped DLL"
ls -lh ${OUTPUT_NAME}

# Copy verification files to a common location (will be collected by build-all.sh)
echo "Verification files saved:"
echo "  $DEPS_FILE"
echo "  $STATIC_LIBS_FILE"

echo "Windows ${ARCH} build complete: ${OUTPUT_NAME}"
