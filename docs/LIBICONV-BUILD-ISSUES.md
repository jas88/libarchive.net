# libiconv LLVM-MinGW Cross-Compilation Solutions

## Problem Analysis

Based on investigation of the libiconv build issues with LLVM-MinGW cross-compilation, the following problems were identified:

### 1. mbrtowc Function Signature Conflict

**Root Cause**: libiconv provides a fallback declaration of `mbrtowc` in `lib/loop_wchar.h`:
```c
extern size_t mbrtowc ();  // Incorrect signature
```

But LLVM-MinGW's `wchar.h` provides the correct signature:
```c
size_t __cdecl mbrtowc(wchar_t * __restrict__ _DstCh, const char * __restrict__ _SrcCh, size_t _SizeInBytes, mbstate_t * __restrict__ _State);
```

This causes a compilation error:
```
error: conflicting types for 'mbrtowc'; have 'size_t(void)' {aka 'long long unsigned int(void)'}
```

### 2. Static Library Archive Format Issues

**Root Cause**: The manual compilation approach creates archives that LLVM's `ld.lld` linker cannot recognize:
```
ld.lld: error: unknown file type: libiconv.a
```

## Solutions

### Solution 1: Configure Flags to Fix mbrtowc Conflict

Add specific configure flags to disable the problematic mbrtowc fallback:

```bash
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --disable-shared --enable-static --disable-nls \
    --disable-rpath --with-pic \
    ac_cv_func_mbrtowc=no \
    ac_cv_func_wcrtomb=no \
    CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"
```

**Key flags:**
- `ac_cv_func_mbrtowc=no` - Force libiconv to not use mbrtowc
- `ac_cv_func_wcrtomb=no` - Force libiconv to not use wcrtomb
- `--disable-rpath` - Avoid rpath issues with cross-compilation

### Solution 2: Alternative Approach - Use System libiconv

For Windows builds, consider using the system's built-in iconv support or Windows Codepage APIs instead:

```bash
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --disable-shared --enable-static --disable-nls \
    --disable-rpath --with-pic \
    --disable-extra \
    CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"
```

### Solution 3: Patch libiconv Source

Create a patch to fix the mbrtowc conflict in `lib/loop_wchar.h`:

```diff
@@ -36,9 +36,11 @@
 # define BUF_SIZE 64  /* assume MB_LEN_MAX <= 64 */
   /* Some systems, like BeOS, have multibyte encodings but lack mbstate_t.  */
+#if !defined(__MINGW32__) && !defined(__MINGW64__)
   extern size_t mbrtowc ();
 # ifdef mbstate_t
 #  define mbrtowc(pwc, s, n, ps) (mbrtowc)(pwc, s, n, 0)
 #  define mbsinit(ps) 1
 # endif
+#endif
```

### Solution 4: Use Alternative Build Method

Instead of manual compilation, use a modified build approach:

```bash
# After configure runs and generates headers, build using make but with modifications
make -j$NCPU CC=$CC AR=$AR \
    CFLAGS="$CFLAGS -DHAVE_CONFIG_H -DBUILDING_LIBICONV -UHAVE_MBRTOWC -UHAVE_WCRTOMB" \
    lib/libiconv.a

# Or build individual objects with proper flags
cd lib
$CC -c $CFLAGS -I../include -I. -DHAVE_CONFIG_H -DBUILDING_LIBICONV \
    -UHAVE_MBRTOWC -UHAVE_WCRTOMB \
    iconv.c
$AR rcs libiconv.a *.o
```

### Solution 5: Use Pre-built libiconv for Windows

Consider using a pre-built Windows libiconv package:

```bash
# Download pre-built libiconv for Windows
# Example using vcpkg (if available)
vcpkg install libiconv:x64-windows-static
```

## Recommended Implementation

Based on the investigation, **Solution 1** is recommended as it's the cleanest approach:

### Modified build-windows.sh section for libiconv:

```bash
# Build libiconv with proper configure flags
echo "Building libiconv ${ICONV_VERSION}..."
cd libiconv-${ICONV_VERSION}

# Use specific configure flags to avoid mbrtowc conflicts
./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
    --disable-shared --enable-static --disable-nls \
    --disable-rpath --with-pic --disable-extra \
    ac_cv_func_mbrtowc=no ac_cv_func_wcrtomb=no \
    CC=$CC CFLAGS="$CFLAGS" LDFLAGS="$LDFLAGS" CPPFLAGS="$CPPFLAGS"

# Build only the library, skip programs
make -j$NCPU CC=$CC AR=$AR lib/libiconv.a

# Install only library components
make install-lib LIBRARIES=lib/libiconv.a INCLUDES=include/iconv.h

cd ..
```

## Additional Recommendations

### 1. Verify Archive Format

After building, verify the archive format:

```bash
# Check if archive is properly formatted
file "$PREFIX/lib/libiconv.a"
${MINGW_PREFIX}-nm "$PREFIX/lib/libiconv.a" | head -5

# Test linking with a simple program
${MINGW_PREFIX}-gcc -o test-iconv test.c -L"$PREFIX/lib" -liconv
```

### 2. Consider Alternative Dependencies

If libiconv continues to be problematic, consider:

1. **Disable iconv support in libxml2**:
   ```bash
   ./configure --host=${MINGW_PREFIX} --prefix=$PREFIX \
       --without-iconv --with-zlib=$PREFIX --with-lzma=$PREFIX
   ```

2. **Use win-iconv** - A Windows-native iconv implementation
3. **Use libxml2's built-in iconv fallback**

### 3. LLVM-MinGW Specific Considerations

- LLVM-MinGW uses different C library headers than traditional MinGW
- Always test with the specific MinGW version that will be used in production
- Consider using traditional GCC-based MinGW if LLVM-MinGW continues to cause issues

## Testing the Solution

To test the recommended solution:

```bash
# Apply the fix in build-windows.sh
ARCH=x86_64 ./build-windows.sh

# Verify libiconv was built correctly
if [ -f "local-x86_64/lib/libiconv.a" ]; then
    echo "✓ libiconv built successfully"
    file "local-x86_64/lib/libiconv.a"
    x86_64-w64-mingw32-nm "local-x86_64/lib/libiconv.a" | grep iconv | head -3
else
    echo "✗ libiconv build failed"
fi
```

This comprehensive solution addresses both the mbrtowc signature conflict and the archive format issues, providing multiple approaches to resolve the libiconv build problems with LLVM-MinGW cross-compilation.