/*
 * Win32 CRT Replacement Functions - Header
 *
 * Declarations for Win32-based CRT function replacements.
 * These provide implementations that use native Win32 APIs
 * instead of UCRT, reducing DLL dependencies.
 */

#ifndef WIN32_CRT_H
#define WIN32_CRT_H

#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Heap functions */
void* malloc(size_t size);
void* calloc(size_t count, size_t size);
void* realloc(void* ptr, size_t size);
void free(void* ptr);
void* _aligned_malloc(size_t size, size_t alignment);
void _aligned_free(void* ptr);

/* Memory functions */
void* memcpy(void* dest, const void* src, size_t count);
void* memmove(void* dest, const void* src, size_t count);
void* memset(void* dest, int c, size_t count);
int memcmp(const void* ptr1, const void* ptr2, size_t count);
void* memchr(const void* ptr, int c, size_t count);
void bzero(void* s, size_t n);

/* String functions */
size_t strlen(const char* str);
size_t wcslen(const wchar_t* str);
char* strcpy(char* dest, const char* src);
wchar_t* wcscpy(wchar_t* dest, const wchar_t* src);
char* strncpy(char* dest, const char* src, size_t count);
wchar_t* wcsncpy(wchar_t* dest, const wchar_t* src, size_t count);
char* strcat(char* dest, const char* src);
char* strncat(char* dest, const char* src, size_t count);
int strcmp(const char* str1, const char* str2);
int wcscmp(const wchar_t* str1, const wchar_t* str2);
int strncmp(const char* str1, const char* str2, size_t count);
int wcsncmp(const wchar_t* str1, const wchar_t* str2, size_t count);
int wmemcmp(const wchar_t* ptr1, const wchar_t* ptr2, size_t count);
char* strchr(const char* str, int c);
wchar_t* wcschr(const wchar_t* str, wchar_t c);
char* strrchr(const char* str, int c);
wchar_t* wcsrchr(const wchar_t* str, wchar_t c);
char* strstr(const char* haystack, const char* needle);
wchar_t* wcsstr(const wchar_t* haystack, const wchar_t* needle);
char* strdup(const char* str);
wchar_t* wcsdup(const wchar_t* str);
char* _strdup(const char* str);
wchar_t* _wcsdup(const wchar_t* str);
size_t strspn(const char* str, const char* accept);
size_t strcspn(const char* str, const char* reject);
char* strpbrk(const char* str, const char* accept);

/* Environment functions */
char* getenv(const char* name);
wchar_t* _wgetenv(const wchar_t* name);
int _putenv(const char* envstring);

/* Filesystem functions */
int _wunlink(const wchar_t* path);
int _unlink(const char* path);
int remove(const char* path);
int _wremove(const wchar_t* path);
int _wmkdir(const wchar_t* path);
int _mkdir(const char* path);
int _wrmdir(const wchar_t* path);
int _rmdir(const char* path);
int rename(const char* oldname, const char* newname);
int _wrename(const wchar_t* oldname, const wchar_t* newname);
int _wchdir(const wchar_t* path);
int _chdir(const char* path);
wchar_t* _wgetcwd(wchar_t* buffer, int maxlen);
char* _getcwd(char* buffer, int maxlen);
int _access(const char* path, int mode);
int _waccess(const wchar_t* path, int mode);

/* Character classification */
int isspace(int c);
int isdigit(int c);
int isalpha(int c);
int isalnum(int c);
int isupper(int c);
int islower(int c);
int isxdigit(int c);
int isprint(int c);
int iscntrl(int c);
int ispunct(int c);
int isgraph(int c);
int tolower(int c);
int toupper(int c);

/* Note: Wide character classification functions (iswspace, iswdigit, etc.)
 * are implemented as macros in MinGW headers, so we don't override them.
 */

/* Utility functions */
void qsort(void* base, size_t num, size_t size, int (*compar)(const void*, const void*));
void* bsearch(const void* key, const void* base, size_t num, size_t size,
              int (*compar)(const void*, const void*));
int abs(int n);
long labs(long n);
long long llabs(long long n);

/* String to number conversion */
long strtol(const char* str, char** endptr, int base);
unsigned long strtoul(const char* str, char** endptr, int base);
long long strtoll(const char* str, char** endptr, int base);
unsigned long long strtoull(const char* str, char** endptr, int base);
int atoi(const char* str);
long atol(const char* str);
long long atoll(const char* str);
long wcstol(const wchar_t* str, wchar_t** endptr, int base);
unsigned long wcstoul(const wchar_t* str, wchar_t** endptr, int base);
int _wtoi(const wchar_t* str);

/* Wide to multibyte conversion */
size_t wcstombs(char* dest, const wchar_t* src, size_t max);
size_t mbstowcs(wchar_t* dest, const char* src, size_t max);
int wctomb(char* s, wchar_t wc);
int mbtowc(wchar_t* pwc, const char* s, size_t n);

#ifdef __cplusplus
}
#endif

#endif /* WIN32_CRT_H */
