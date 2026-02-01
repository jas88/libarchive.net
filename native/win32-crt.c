/*
 * Win32 CRT Replacement Functions
 *
 * This file provides Win32 implementations of common C runtime functions
 * to reduce dependency on UCRT DLLs. These symbols are linked before UCRT
 * so they take precedence.
 *
 * Eliminates dependencies on:
 * - api-ms-win-crt-heap-l1-1-0.dll (malloc, free, calloc, realloc)
 * - api-ms-win-crt-private-l1-1-0.dll (memcpy, memmove, memset, memcmp, memchr)
 * - api-ms-win-crt-environment-l1-1-0.dll (getenv, _wgetenv)
 * - Most of api-ms-win-crt-string-l1-1-0.dll
 * - Most of api-ms-win-crt-filesystem-l1-1-0.dll
 */

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <stddef.h>

/* Prevent compiler from using intrinsics - we want our implementations called */
#pragma function(memcpy, memmove, memset, memcmp)

/*
 * ============================================================================
 * Heap Functions (api-ms-win-crt-heap-l1-1-0.dll)
 * ============================================================================
 */

void* malloc(size_t size) {
    if (size == 0) size = 1;  /* malloc(0) should return valid pointer */
    return HeapAlloc(GetProcessHeap(), 0, size);
}

void* calloc(size_t count, size_t size) {
    size_t total = count * size;
    if (total == 0) total = 1;
    return HeapAlloc(GetProcessHeap(), HEAP_ZERO_MEMORY, total);
}

void* realloc(void* ptr, size_t size) {
    if (ptr == NULL) {
        return malloc(size);
    }
    if (size == 0) {
        HeapFree(GetProcessHeap(), 0, ptr);
        return NULL;
    }
    return HeapReAlloc(GetProcessHeap(), 0, ptr, size);
}

void free(void* ptr) {
    if (ptr != NULL) {
        HeapFree(GetProcessHeap(), 0, ptr);
    }
}

/* _aligned variants for SIMD code */
void* _aligned_malloc(size_t size, size_t alignment) {
    /* Simple alignment implementation using extra space */
    void* raw = malloc(size + alignment + sizeof(void*));
    if (!raw) return NULL;
    void* aligned = (void*)(((size_t)raw + sizeof(void*) + alignment - 1) & ~(alignment - 1));
    ((void**)aligned)[-1] = raw;
    return aligned;
}

void _aligned_free(void* ptr) {
    if (ptr != NULL) {
        free(((void**)ptr)[-1]);
    }
}

/*
 * ============================================================================
 * Memory Functions (api-ms-win-crt-private-l1-1-0.dll)
 * ============================================================================
 */

void* memcpy(void* dest, const void* src, size_t count) {
    char* d = (char*)dest;
    const char* s = (const char*)src;
    while (count--) {
        *d++ = *s++;
    }
    return dest;
}

void* memmove(void* dest, const void* src, size_t count) {
    char* d = (char*)dest;
    const char* s = (const char*)src;
    if (d < s) {
        while (count--) {
            *d++ = *s++;
        }
    } else if (d > s) {
        d += count;
        s += count;
        while (count--) {
            *--d = *--s;
        }
    }
    return dest;
}

void* memset(void* dest, int c, size_t count) {
    unsigned char* d = (unsigned char*)dest;
    while (count--) {
        *d++ = (unsigned char)c;
    }
    return dest;
}

int memcmp(const void* ptr1, const void* ptr2, size_t count) {
    const unsigned char* p1 = (const unsigned char*)ptr1;
    const unsigned char* p2 = (const unsigned char*)ptr2;
    while (count--) {
        if (*p1 != *p2) {
            return *p1 - *p2;
        }
        p1++;
        p2++;
    }
    return 0;
}

void* memchr(const void* ptr, int c, size_t count) {
    const unsigned char* p = (const unsigned char*)ptr;
    while (count--) {
        if (*p == (unsigned char)c) {
            return (void*)p;
        }
        p++;
    }
    return NULL;
}

/* bzero is used by some libraries */
void bzero(void* s, size_t n) {
    memset(s, 0, n);
}

/*
 * ============================================================================
 * String Functions (api-ms-win-crt-string-l1-1-0.dll)
 * ============================================================================
 */

size_t strlen(const char* str) {
    const char* s = str;
    while (*s) s++;
    return s - str;
}

size_t wcslen(const wchar_t* str) {
    const wchar_t* s = str;
    while (*s) s++;
    return s - str;
}

char* strcpy(char* dest, const char* src) {
    char* d = dest;
    while ((*d++ = *src++));
    return dest;
}

wchar_t* wcscpy(wchar_t* dest, const wchar_t* src) {
    wchar_t* d = dest;
    while ((*d++ = *src++));
    return dest;
}

char* strncpy(char* dest, const char* src, size_t count) {
    char* d = dest;
    while (count && (*d++ = *src++)) count--;
    while (count--) *d++ = '\0';
    return dest;
}

wchar_t* wcsncpy(wchar_t* dest, const wchar_t* src, size_t count) {
    wchar_t* d = dest;
    while (count && (*d++ = *src++)) count--;
    while (count--) *d++ = L'\0';
    return dest;
}

char* strcat(char* dest, const char* src) {
    char* d = dest;
    while (*d) d++;
    while ((*d++ = *src++));
    return dest;
}

char* strncat(char* dest, const char* src, size_t count) {
    char* d = dest;
    while (*d) d++;
    while (count-- && (*d++ = *src++));
    if (count == (size_t)-1) *(d-1) = '\0';
    return dest;
}

int strcmp(const char* str1, const char* str2) {
    while (*str1 && *str1 == *str2) {
        str1++;
        str2++;
    }
    return (unsigned char)*str1 - (unsigned char)*str2;
}

int wcscmp(const wchar_t* str1, const wchar_t* str2) {
    while (*str1 && *str1 == *str2) {
        str1++;
        str2++;
    }
    return *str1 - *str2;
}

int strncmp(const char* str1, const char* str2, size_t count) {
    while (count && *str1 && *str1 == *str2) {
        str1++;
        str2++;
        count--;
    }
    if (count == 0) return 0;
    return (unsigned char)*str1 - (unsigned char)*str2;
}

int wcsncmp(const wchar_t* str1, const wchar_t* str2, size_t count) {
    while (count && *str1 && *str1 == *str2) {
        str1++;
        str2++;
        count--;
    }
    if (count == 0) return 0;
    return *str1 - *str2;
}

int wmemcmp(const wchar_t* ptr1, const wchar_t* ptr2, size_t count) {
    while (count--) {
        if (*ptr1 != *ptr2) {
            return *ptr1 - *ptr2;
        }
        ptr1++;
        ptr2++;
    }
    return 0;
}

char* strchr(const char* str, int c) {
    while (*str) {
        if (*str == (char)c) return (char*)str;
        str++;
    }
    return (c == '\0') ? (char*)str : NULL;
}

wchar_t* wcschr(const wchar_t* str, wchar_t c) {
    while (*str) {
        if (*str == c) return (wchar_t*)str;
        str++;
    }
    return (c == L'\0') ? (wchar_t*)str : NULL;
}

char* strrchr(const char* str, int c) {
    const char* last = NULL;
    while (*str) {
        if (*str == (char)c) last = str;
        str++;
    }
    return (c == '\0') ? (char*)str : (char*)last;
}

wchar_t* wcsrchr(const wchar_t* str, wchar_t c) {
    const wchar_t* last = NULL;
    while (*str) {
        if (*str == c) last = str;
        str++;
    }
    return (c == L'\0') ? (wchar_t*)str : (wchar_t*)last;
}

char* strstr(const char* haystack, const char* needle) {
    if (!*needle) return (char*)haystack;
    for (; *haystack; haystack++) {
        const char* h = haystack;
        const char* n = needle;
        while (*h && *n && *h == *n) {
            h++;
            n++;
        }
        if (!*n) return (char*)haystack;
    }
    return NULL;
}

wchar_t* wcsstr(const wchar_t* haystack, const wchar_t* needle) {
    if (!*needle) return (wchar_t*)haystack;
    for (; *haystack; haystack++) {
        const wchar_t* h = haystack;
        const wchar_t* n = needle;
        while (*h && *n && *h == *n) {
            h++;
            n++;
        }
        if (!*n) return (wchar_t*)haystack;
    }
    return NULL;
}

char* strdup(const char* str) {
    size_t len = strlen(str) + 1;
    char* dup = (char*)malloc(len);
    if (dup) memcpy(dup, str, len);
    return dup;
}

wchar_t* wcsdup(const wchar_t* str) {
    size_t len = (wcslen(str) + 1) * sizeof(wchar_t);
    wchar_t* dup = (wchar_t*)malloc(len);
    if (dup) memcpy(dup, str, len);
    return dup;
}

/* MSVC-specific names */
char* _strdup(const char* str) {
    return strdup(str);
}

wchar_t* _wcsdup(const wchar_t* str) {
    return wcsdup(str);
}

size_t strspn(const char* str, const char* accept) {
    const char* s = str;
    while (*s) {
        const char* a = accept;
        while (*a && *a != *s) a++;
        if (!*a) break;
        s++;
    }
    return s - str;
}

size_t strcspn(const char* str, const char* reject) {
    const char* s = str;
    while (*s) {
        const char* r = reject;
        while (*r && *r != *s) r++;
        if (*r) break;
        s++;
    }
    return s - str;
}

char* strpbrk(const char* str, const char* accept) {
    while (*str) {
        const char* a = accept;
        while (*a) {
            if (*a++ == *str) return (char*)str;
        }
        str++;
    }
    return NULL;
}

/*
 * ============================================================================
 * Environment Functions (api-ms-win-crt-environment-l1-1-0.dll)
 * ============================================================================
 */

/* Thread-local storage for getenv buffer
 * Use __thread for GCC/MinGW, __declspec(thread) for MSVC */
#ifdef __GNUC__
static __thread char getenv_buffer[32768];
static __thread wchar_t wgetenv_buffer[32768];
#else
static __declspec(thread) char getenv_buffer[32768];
static __declspec(thread) wchar_t wgetenv_buffer[32768];
#endif

char* getenv(const char* name) {
    DWORD len = GetEnvironmentVariableA(name, getenv_buffer, sizeof(getenv_buffer));
    if (len == 0 || len >= sizeof(getenv_buffer)) {
        return NULL;
    }
    return getenv_buffer;
}

wchar_t* _wgetenv(const wchar_t* name) {
    DWORD len = GetEnvironmentVariableW(name, wgetenv_buffer, sizeof(wgetenv_buffer)/sizeof(wchar_t));
    if (len == 0 || len >= sizeof(wgetenv_buffer)/sizeof(wchar_t)) {
        return NULL;
    }
    return wgetenv_buffer;
}

int _putenv(const char* envstring) {
    /* Format: NAME=value or NAME= (to delete) */
    char* eq = strchr(envstring, '=');
    if (!eq) return -1;

    size_t namelen = eq - envstring;
    char* name = (char*)malloc(namelen + 1);
    if (!name) return -1;
    memcpy(name, envstring, namelen);
    name[namelen] = '\0';

    const char* value = eq + 1;
    BOOL result = SetEnvironmentVariableA(name, *value ? value : NULL);
    free(name);
    return result ? 0 : -1;
}

/*
 * ============================================================================
 * Filesystem Functions (api-ms-win-crt-filesystem-l1-1-0.dll)
 * ============================================================================
 */

int _wunlink(const wchar_t* path) {
    return DeleteFileW(path) ? 0 : -1;
}

int _unlink(const char* path) {
    return DeleteFileA(path) ? 0 : -1;
}

int remove(const char* path) {
    return DeleteFileA(path) ? 0 : -1;
}

int _wremove(const wchar_t* path) {
    return DeleteFileW(path) ? 0 : -1;
}

int _wmkdir(const wchar_t* path) {
    return CreateDirectoryW(path, NULL) ? 0 : -1;
}

int _mkdir(const char* path) {
    return CreateDirectoryA(path, NULL) ? 0 : -1;
}

int _wrmdir(const wchar_t* path) {
    return RemoveDirectoryW(path) ? 0 : -1;
}

int _rmdir(const char* path) {
    return RemoveDirectoryA(path) ? 0 : -1;
}

int rename(const char* oldname, const char* newname) {
    return MoveFileA(oldname, newname) ? 0 : -1;
}

int _wrename(const wchar_t* oldname, const wchar_t* newname) {
    return MoveFileW(oldname, newname) ? 0 : -1;
}

int _wchdir(const wchar_t* path) {
    return SetCurrentDirectoryW(path) ? 0 : -1;
}

int _chdir(const char* path) {
    return SetCurrentDirectoryA(path) ? 0 : -1;
}

wchar_t* _wgetcwd(wchar_t* buffer, int maxlen) {
    if (buffer == NULL) {
        buffer = (wchar_t*)malloc(maxlen * sizeof(wchar_t));
        if (!buffer) return NULL;
    }
    DWORD len = GetCurrentDirectoryW(maxlen, buffer);
    if (len == 0 || len >= (DWORD)maxlen) {
        return NULL;
    }
    return buffer;
}

char* _getcwd(char* buffer, int maxlen) {
    if (buffer == NULL) {
        buffer = (char*)malloc(maxlen);
        if (!buffer) return NULL;
    }
    DWORD len = GetCurrentDirectoryA(maxlen, buffer);
    if (len == 0 || len >= (DWORD)maxlen) {
        return NULL;
    }
    return buffer;
}

/* _access and _waccess - check file accessibility */
int _access(const char* path, int mode) {
    DWORD attrs = GetFileAttributesA(path);
    if (attrs == INVALID_FILE_ATTRIBUTES) return -1;
    /* mode 0=exist, 2=write, 4=read, 6=read+write */
    if ((mode & 2) && (attrs & FILE_ATTRIBUTE_READONLY)) return -1;
    return 0;
}

int _waccess(const wchar_t* path, int mode) {
    DWORD attrs = GetFileAttributesW(path);
    if (attrs == INVALID_FILE_ATTRIBUTES) return -1;
    if ((mode & 2) && (attrs & FILE_ATTRIBUTE_READONLY)) return -1;
    return 0;
}

/*
 * ============================================================================
 * Character Classification (api-ms-win-crt-string-l1-1-0.dll)
 * ============================================================================
 */

int isspace(int c) {
    return (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\f' || c == '\v');
}

int isdigit(int c) {
    return (c >= '0' && c <= '9');
}

int isalpha(int c) {
    return ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'));
}

int isalnum(int c) {
    return (isalpha(c) || isdigit(c));
}

int isupper(int c) {
    return (c >= 'A' && c <= 'Z');
}

int islower(int c) {
    return (c >= 'a' && c <= 'z');
}

int isxdigit(int c) {
    return (isdigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'));
}

int isprint(int c) {
    return (c >= 0x20 && c <= 0x7e);
}

int iscntrl(int c) {
    return ((c >= 0 && c < 0x20) || c == 0x7f);
}

int ispunct(int c) {
    return (isprint(c) && !isalnum(c) && !isspace(c));
}

int isgraph(int c) {
    return (isprint(c) && c != ' ');
}

int tolower(int c) {
    if (c >= 'A' && c <= 'Z') return c + ('a' - 'A');
    return c;
}

int toupper(int c) {
    if (c >= 'a' && c <= 'z') return c - ('a' - 'A');
    return c;
}

/* Note: Wide character classification functions (iswspace, iswdigit, etc.)
 * are implemented as macros in MinGW headers, so we don't override them.
 * towlower/towupper are actual functions we can override.
 */

/*
 * ============================================================================
 * Utility Functions
 * ============================================================================
 */

/* qsort using simple insertion sort (good enough for libarchive's small arrays) */
void qsort(void* base, size_t num, size_t size, int (*compar)(const void*, const void*)) {
    char* arr = (char*)base;
    char* temp = (char*)malloc(size);
    if (!temp) return;

    for (size_t i = 1; i < num; i++) {
        memcpy(temp, arr + i * size, size);
        size_t j = i;
        while (j > 0 && compar(arr + (j - 1) * size, temp) > 0) {
            memcpy(arr + j * size, arr + (j - 1) * size, size);
            j--;
        }
        memcpy(arr + j * size, temp, size);
    }
    free(temp);
}

/* bsearch */
void* bsearch(const void* key, const void* base, size_t num, size_t size,
              int (*compar)(const void*, const void*)) {
    const char* arr = (const char*)base;
    size_t low = 0, high = num;
    while (low < high) {
        size_t mid = low + (high - low) / 2;
        int cmp = compar(key, arr + mid * size);
        if (cmp < 0) {
            high = mid;
        } else if (cmp > 0) {
            low = mid + 1;
        } else {
            return (void*)(arr + mid * size);
        }
    }
    return NULL;
}

/* abs/labs */
int abs(int n) {
    return n < 0 ? -n : n;
}

long labs(long n) {
    return n < 0 ? -n : n;
}

long long llabs(long long n) {
    return n < 0 ? -n : n;
}

/*
 * ============================================================================
 * String to Number Conversion (api-ms-win-crt-convert-l1-1-0.dll)
 * ============================================================================
 */

long strtol(const char* str, char** endptr, int base) {
    const char* p = str;
    long result = 0;
    int negative = 0;

    /* Skip whitespace */
    while (isspace((unsigned char)*p)) p++;

    /* Handle sign */
    if (*p == '-') { negative = 1; p++; }
    else if (*p == '+') p++;

    /* Detect base */
    if (base == 0) {
        if (*p == '0') {
            if (p[1] == 'x' || p[1] == 'X') { base = 16; p += 2; }
            else { base = 8; p++; }
        } else {
            base = 10;
        }
    } else if (base == 16 && *p == '0' && (p[1] == 'x' || p[1] == 'X')) {
        p += 2;
    }

    /* Convert */
    while (*p) {
        int digit;
        if (*p >= '0' && *p <= '9') digit = *p - '0';
        else if (*p >= 'a' && *p <= 'z') digit = *p - 'a' + 10;
        else if (*p >= 'A' && *p <= 'Z') digit = *p - 'A' + 10;
        else break;
        if (digit >= base) break;
        result = result * base + digit;
        p++;
    }

    if (endptr) *endptr = (char*)p;
    return negative ? -result : result;
}

unsigned long strtoul(const char* str, char** endptr, int base) {
    return (unsigned long)strtol(str, endptr, base);
}

long long strtoll(const char* str, char** endptr, int base) {
    const char* p = str;
    long long result = 0;
    int negative = 0;

    while (isspace((unsigned char)*p)) p++;
    if (*p == '-') { negative = 1; p++; }
    else if (*p == '+') p++;

    if (base == 0) {
        if (*p == '0') {
            if (p[1] == 'x' || p[1] == 'X') { base = 16; p += 2; }
            else { base = 8; p++; }
        } else {
            base = 10;
        }
    } else if (base == 16 && *p == '0' && (p[1] == 'x' || p[1] == 'X')) {
        p += 2;
    }

    while (*p) {
        int digit;
        if (*p >= '0' && *p <= '9') digit = *p - '0';
        else if (*p >= 'a' && *p <= 'z') digit = *p - 'a' + 10;
        else if (*p >= 'A' && *p <= 'Z') digit = *p - 'A' + 10;
        else break;
        if (digit >= base) break;
        result = result * base + digit;
        p++;
    }

    if (endptr) *endptr = (char*)p;
    return negative ? -result : result;
}

unsigned long long strtoull(const char* str, char** endptr, int base) {
    return (unsigned long long)strtoll(str, endptr, base);
}

int atoi(const char* str) {
    return (int)strtol(str, NULL, 10);
}

long atol(const char* str) {
    return strtol(str, NULL, 10);
}

long long atoll(const char* str) {
    return strtoll(str, NULL, 10);
}

/* Wide string to number */
long wcstol(const wchar_t* str, wchar_t** endptr, int base) {
    const wchar_t* p = str;
    long result = 0;
    int negative = 0;

    while (iswspace(*p)) p++;
    if (*p == L'-') { negative = 1; p++; }
    else if (*p == L'+') p++;

    if (base == 0) {
        if (*p == L'0') {
            if (p[1] == L'x' || p[1] == L'X') { base = 16; p += 2; }
            else { base = 8; p++; }
        } else {
            base = 10;
        }
    } else if (base == 16 && *p == L'0' && (p[1] == L'x' || p[1] == L'X')) {
        p += 2;
    }

    while (*p) {
        int digit;
        if (*p >= L'0' && *p <= L'9') digit = *p - L'0';
        else if (*p >= L'a' && *p <= L'z') digit = *p - L'a' + 10;
        else if (*p >= L'A' && *p <= L'Z') digit = *p - L'A' + 10;
        else break;
        if (digit >= base) break;
        result = result * base + digit;
        p++;
    }

    if (endptr) *endptr = (wchar_t*)p;
    return negative ? -result : result;
}

unsigned long wcstoul(const wchar_t* str, wchar_t** endptr, int base) {
    return (unsigned long)wcstol(str, endptr, base);
}

int _wtoi(const wchar_t* str) {
    return (int)wcstol(str, NULL, 10);
}

/*
 * ============================================================================
 * Wide to Multibyte Conversion
 * ============================================================================
 */

size_t wcstombs(char* dest, const wchar_t* src, size_t max) {
    int result = WideCharToMultiByte(CP_UTF8, 0, src, -1, dest, (int)max, NULL, NULL);
    return result > 0 ? (size_t)(result - 1) : (size_t)-1;
}

size_t mbstowcs(wchar_t* dest, const char* src, size_t max) {
    int result = MultiByteToWideChar(CP_UTF8, 0, src, -1, dest, (int)max);
    return result > 0 ? (size_t)(result - 1) : (size_t)-1;
}

int wctomb(char* s, wchar_t wc) {
    if (s == NULL) return 0;
    return WideCharToMultiByte(CP_UTF8, 0, &wc, 1, s, 6, NULL, NULL);
}

int mbtowc(wchar_t* pwc, const char* s, size_t n) {
    if (s == NULL) return 0;
    int result = MultiByteToWideChar(CP_UTF8, 0, s, (int)n, pwc, 1);
    return result > 0 ? result : -1;
}
