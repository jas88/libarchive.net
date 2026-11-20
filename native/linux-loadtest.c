/*
 * Load test for a freshly linked libarchive.so, which test_load_musl and test_load_host in
 * build-linux-common.sh build and run.
 * Usage: loadtest <path to libarchive.so>
 * Exits non-zero unless the library loads with every symbol bound at once
 * (RTLD_NOW), a little of its API works, names are UTF-8 in the "C" locale, a XAR
 * archive written in memory reads back, and a Zstandard zip entry of 128 KiB reads
 * back in full.
 *
 * Names must be UTF-8 in the "C" locale, which the program never leaves, as a .NET process
 * never does: libarchive-narrow-utf8.patch is in the library (see "The libarchive patches"
 * in BUILD.md). Only the real XAR reader and writer can read back the XAR archive, using
 * libxml2, zlib and mbedTLS's SHA-1. The XAR writer keeps entry data in a temporary file
 * ($TMPDIR, or /tmp) until the archive is closed. The Zstandard zip entry, which the
 * program compresses into memory, reads back in full from there only if
 * libarchive-zip-zstd-end-of-entry.patch is in the library.
 */
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/types.h>
#include <wchar.h>

struct archive;
struct archive_entry;

#define ARCHIVE_OK 0
#define ARCHIVE_EOF 1
#define AE_IFREG 0100000

static void *library;

/* Store the address of the named function in *function, or say why it is missing */
static int find(const char *name, void *function, size_t size)
{
    void *address;

    dlerror();
    address = dlsym(library, name);
    if (address == NULL) {
        const char *error = dlerror();
        fprintf(stderr, "FAIL: %s: %s\n", name, error != NULL ? error : "null address");
        return 0;
    }
    /* POSIX guarantees that a data pointer can hold the address of a function */
    memcpy(function, &address, size);
    return 1;
}

#define FIND(name, function) find(name, &(function), sizeof (function))

/* "Größe.txt", composed (NFC), as UTF-8 and as Unicode code points */
static const char name_utf8[] = "Gr\xc3\xb6\xc3\x9f" "e.txt";
static const wchar_t name_wcs[] = { 'G', 'r', 0xF6, 0xDF, 'e', '.', 't', 'x', 't', 0 };

/*
 * Fail unless narrow strings are UTF-8: narrow to wide and back, and a zip entry
 * that keeps the name's UTF-8 bytes, with the UTF-8 flag (general purpose bit 11),
 * and reads back unchanged.
 */
static int check_utf8_names(void)
{
    struct archive_entry *(*entry_new)(void);
    void (*entry_free)(struct archive_entry *);
    void (*set_pathname)(struct archive_entry *, const char *);
    const char *(*pathname)(struct archive_entry *);
    const wchar_t *(*pathname_w)(struct archive_entry *);
    void (*copy_pathname_w)(struct archive_entry *, const wchar_t *);
    void (*set_filetype)(struct archive_entry *, unsigned int);
    void (*set_perm)(struct archive_entry *, mode_t);
    void (*set_size)(struct archive_entry *, int64_t);
    struct archive *(*write_new)(void);
    int (*set_format_zip)(struct archive *);
    int (*write_open_memory)(struct archive *, void *, size_t, size_t *);
    int (*write_header)(struct archive *, struct archive_entry *);
    int (*write_close)(struct archive *);
    int (*write_free)(struct archive *);
    struct archive *(*read_new)(void);
    int (*support_format_zip)(struct archive *);
    int (*open_memory)(struct archive *, const void *, size_t);
    int (*next_header)(struct archive *, struct archive_entry **);
    const char *(*error_string)(struct archive *);
    int (*read_free)(struct archive *);
    static unsigned char buf[65536];
    size_t used = 0, len = sizeof name_utf8 - 1;
    struct archive *a;
    struct archive_entry *entry;
    const wchar_t *w;
    const char *p;
    int ok = 1;

    if (!FIND("archive_entry_new", entry_new) || !FIND("archive_entry_free", entry_free)
        || !FIND("archive_entry_set_pathname", set_pathname)
        || !FIND("archive_entry_pathname", pathname)
        || !FIND("archive_entry_pathname_w", pathname_w)
        || !FIND("archive_entry_copy_pathname_w", copy_pathname_w)
        || !FIND("archive_entry_set_filetype", set_filetype)
        || !FIND("archive_entry_set_perm", set_perm) || !FIND("archive_entry_set_size", set_size)
        || !FIND("archive_write_new", write_new)
        || !FIND("archive_write_set_format_zip", set_format_zip)
        || !FIND("archive_write_open_memory", write_open_memory)
        || !FIND("archive_write_header", write_header) || !FIND("archive_write_close", write_close)
        || !FIND("archive_write_free", write_free) || !FIND("archive_read_new", read_new)
        || !FIND("archive_read_support_format_zip", support_format_zip)
        || !FIND("archive_read_open_memory", open_memory)
        || !FIND("archive_read_next_header", next_header)
        || !FIND("archive_error_string", error_string) || !FIND("archive_read_free", read_free))
        return 0;

    entry = entry_new();
    if (entry == NULL) {
        fprintf(stderr, "FAIL: archive_entry_new returned NULL\n");
        return 0;
    }
    set_pathname(entry, name_utf8);
    w = pathname_w(entry);
    if (w == NULL || wcscmp(w, name_wcs) != 0) {
        fprintf(stderr, "FAIL: archive_entry_pathname_w does not decode the UTF-8 name\n");
        ok = 0;
    }
    copy_pathname_w(entry, name_wcs);
    p = pathname(entry);
    if (p == NULL || strcmp(p, name_utf8) != 0) {
        fprintf(stderr, "FAIL: archive_entry_pathname does not encode the wide name as UTF-8\n");
        ok = 0;
    }

    set_pathname(entry, name_utf8);
    set_filetype(entry, AE_IFREG);
    set_perm(entry, 0644);
    set_size(entry, 0);
    a = write_new();
    if (a == NULL) {
        fprintf(stderr, "FAIL: archive_write_new returned NULL\n");
        ok = 0;
    } else if (set_format_zip(a) != ARCHIVE_OK
        || write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
        || write_header(a, entry) != ARCHIVE_OK || write_close(a) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: writing a zip entry: %s\n", p != NULL ? p : "(no error string)");
        ok = 0;
    }
    if (a != NULL)
        write_free(a);
    entry_free(entry);
    if (!ok)
        return 0;
    /* Local file header: flags at 6 (bit 11 is UTF-8), name length at 26, name at 30 */
    if (used < 30 + len || memcmp(buf, "PK\3\4", 4) != 0 || (buf[7] & 0x08) == 0
        || (size_t)buf[26] != len || buf[27] != 0 || memcmp(buf + 30, name_utf8, len) != 0) {
        fprintf(stderr, "FAIL: the zip local file header lacks the UTF-8 flag or the UTF-8 name\n");
        return 0;
    }
    a = read_new();
    if (a == NULL) {
        fprintf(stderr, "FAIL: archive_read_new returned NULL\n");
        ok = 0;
    } else if (support_format_zip(a) != ARCHIVE_OK || open_memory(a, buf, used) != ARCHIVE_OK
        || next_header(a, &entry) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: reading the zip entry: %s\n", p != NULL ? p : "(no error string)");
        ok = 0;
    } else if ((p = pathname(entry)) == NULL || strcmp(p, name_utf8) != 0) {
        fprintf(stderr, "FAIL: the zip entry's name does not read back as the same UTF-8\n");
        ok = 0;
    }
    if (a != NULL)
        read_free(a);
    if (ok)
        printf("OK: names are UTF-8 in the \"C\" locale\n");
    return ok;
}

/* The one entry of the XAR test archive */
static const char xar_name[] = "xar-test.txt";
static const char xar_data[] = "XAR load test\n";

/*
 * Fail unless a XAR archive written to memory reads back: one entry, its name and its
 * data. libarchive built without libxml2 (or zlib, MD5 and SHA-1) has stubs in place of
 * its XAR reader and writer, which return ARCHIVE_WARN with the message "Xar not
 * supported on this platform".
 */
static int check_xar(void)
{
    struct archive_entry *(*entry_new)(void);
    void (*entry_free)(struct archive_entry *);
    void (*set_pathname)(struct archive_entry *, const char *);
    const char *(*pathname)(struct archive_entry *);
    void (*set_filetype)(struct archive_entry *, unsigned int);
    void (*set_perm)(struct archive_entry *, mode_t);
    void (*set_size)(struct archive_entry *, int64_t);
    struct archive *(*write_new)(void);
    int (*set_format_xar)(struct archive *);
    int (*write_open_memory)(struct archive *, void *, size_t, size_t *);
    int (*write_header)(struct archive *, struct archive_entry *);
    ssize_t (*write_data)(struct archive *, const void *, size_t);
    int (*write_close)(struct archive *);
    int (*write_free)(struct archive *);
    struct archive *(*read_new)(void);
    int (*support_format_xar)(struct archive *);
    int (*open_memory)(struct archive *, const void *, size_t);
    int (*next_header)(struct archive *, struct archive_entry **);
    ssize_t (*read_data)(struct archive *, void *, size_t);
    const char *(*error_string)(struct archive *);
    int (*read_free)(struct archive *);
    static unsigned char buf[65536];
    char data[64];
    size_t used = 0, len = sizeof xar_data - 1, got = 0;
    struct archive *a;
    struct archive_entry *entry;
    const char *p;
    ssize_t n;
    int result, ok = 1;

    if (!FIND("archive_entry_new", entry_new) || !FIND("archive_entry_free", entry_free)
        || !FIND("archive_entry_set_pathname", set_pathname)
        || !FIND("archive_entry_pathname", pathname)
        || !FIND("archive_entry_set_filetype", set_filetype)
        || !FIND("archive_entry_set_perm", set_perm) || !FIND("archive_entry_set_size", set_size)
        || !FIND("archive_write_new", write_new)
        || !FIND("archive_write_set_format_xar", set_format_xar)
        || !FIND("archive_write_open_memory", write_open_memory)
        || !FIND("archive_write_header", write_header) || !FIND("archive_write_data", write_data)
        || !FIND("archive_write_close", write_close) || !FIND("archive_write_free", write_free)
        || !FIND("archive_read_new", read_new)
        || !FIND("archive_read_support_format_xar", support_format_xar)
        || !FIND("archive_read_open_memory", open_memory)
        || !FIND("archive_read_next_header", next_header) || !FIND("archive_read_data", read_data)
        || !FIND("archive_error_string", error_string) || !FIND("archive_read_free", read_free))
        return 0;

    entry = entry_new();
    if (entry == NULL) {
        fprintf(stderr, "FAIL: archive_entry_new returned NULL\n");
        return 0;
    }
    set_pathname(entry, xar_name);
    set_filetype(entry, AE_IFREG);
    set_perm(entry, 0644);
    set_size(entry, (int64_t)len);
    a = write_new();
    if (a == NULL) {
        fprintf(stderr, "FAIL: archive_write_new returned NULL\n");
        ok = 0;
    } else if ((result = set_format_xar(a)) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: archive_write_set_format_xar returned %d: %s\n", result,
            p != NULL ? p : "(no error string)");
        ok = 0;
    } else if (write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
        || write_header(a, entry) != ARCHIVE_OK || write_data(a, xar_data, len) != (ssize_t)len
        || write_close(a) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: writing a XAR entry: %s\n", p != NULL ? p : "(no error string)");
        ok = 0;
    }
    if (a != NULL)
        write_free(a);
    entry_free(entry);
    if (!ok)
        return 0;
    if (used < 4 || memcmp(buf, "xar!", 4) != 0) {
        fprintf(stderr, "FAIL: the XAR archive does not start with the XAR magic number\n");
        return 0;
    }

    a = read_new();
    if (a == NULL) {
        fprintf(stderr, "FAIL: archive_read_new returned NULL\n");
        return 0;
    }
    if ((result = support_format_xar(a)) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: archive_read_support_format_xar returned %d: %s\n", result,
            p != NULL ? p : "(no error string)");
        ok = 0;
    } else if (open_memory(a, buf, used) != ARCHIVE_OK || next_header(a, &entry) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: reading the XAR entry: %s\n", p != NULL ? p : "(no error string)");
        ok = 0;
    } else if ((p = pathname(entry)) == NULL || strcmp(p, xar_name) != 0) {
        fprintf(stderr, "FAIL: the XAR entry's name does not read back\n");
        ok = 0;
    } else {
        n = 0;
        while (got < sizeof data && (n = read_data(a, data + got, sizeof data - got)) > 0)
            got += (size_t)n;
        if (n < 0) {
            p = error_string(a);
            fprintf(stderr, "FAIL: reading the XAR entry's data: %s\n", p != NULL ? p : "(no error string)");
            ok = 0;
        } else if (got != len || memcmp(data, xar_data, len) != 0) {
            fprintf(stderr, "FAIL: the XAR entry's data does not read back\n");
            ok = 0;
        } else if ((result = next_header(a, &entry)) != ARCHIVE_EOF) {
            p = error_string(a);
            fprintf(stderr, "FAIL: archive_read_next_header after the XAR entry returned %d, not ARCHIVE_EOF: %s\n",
                result, p != NULL ? p : "(no error string)");
            ok = 0;
        }
    }
    read_free(a);
    if (ok)
        printf("OK: a XAR archive written to memory reads back\n");
    return ok;
}

/*
 * Fail unless a zip entry of 128 KiB, compressed with Zstandard, reads back in full from
 * memory, which libarchive reads as a seekable source: its data ends just as the zip
 * reader's buffer, of ZSTD_DStreamOutSize() bytes, fills.
 */
static int check_zip_zstd_end(void)
{
    struct archive_entry *(*entry_new)(void);
    void (*entry_free)(struct archive_entry *);
    void (*set_pathname)(struct archive_entry *, const char *);
    void (*set_filetype)(struct archive_entry *, unsigned int);
    void (*set_perm)(struct archive_entry *, mode_t);
    void (*set_size)(struct archive_entry *, int64_t);
    struct archive *(*write_new)(void);
    int (*set_format_zip)(struct archive *);
    int (*set_options)(struct archive *, const char *);
    int (*write_open_memory)(struct archive *, void *, size_t, size_t *);
    int (*write_header)(struct archive *, struct archive_entry *);
    ssize_t (*write_data)(struct archive *, const void *, size_t);
    int (*write_close)(struct archive *);
    int (*write_free)(struct archive *);
    struct archive *(*read_new)(void);
    int (*support_format_zip)(struct archive *);
    int (*open_memory)(struct archive *, const void *, size_t);
    int (*next_header)(struct archive *, struct archive_entry **);
    ssize_t (*read_data)(struct archive *, void *, size_t);
    const char *(*error_string)(struct archive *);
    int (*read_free)(struct archive *);
    static unsigned char data[131072], buf[65536], got[4096];
    size_t used = 0, total = 0, i;
    struct archive *a;
    struct archive_entry *entry;
    const char *p;
    ssize_t n = 0;
    int ok = 1;

    if (!FIND("archive_entry_new", entry_new) || !FIND("archive_entry_free", entry_free)
        || !FIND("archive_entry_set_pathname", set_pathname)
        || !FIND("archive_entry_set_filetype", set_filetype)
        || !FIND("archive_entry_set_perm", set_perm) || !FIND("archive_entry_set_size", set_size)
        || !FIND("archive_write_new", write_new)
        || !FIND("archive_write_set_format_zip", set_format_zip)
        || !FIND("archive_write_set_options", set_options)
        || !FIND("archive_write_open_memory", write_open_memory)
        || !FIND("archive_write_header", write_header) || !FIND("archive_write_data", write_data)
        || !FIND("archive_write_close", write_close) || !FIND("archive_write_free", write_free)
        || !FIND("archive_read_new", read_new)
        || !FIND("archive_read_support_format_zip", support_format_zip)
        || !FIND("archive_read_open_memory", open_memory)
        || !FIND("archive_read_next_header", next_header) || !FIND("archive_read_data", read_data)
        || !FIND("archive_error_string", error_string) || !FIND("archive_read_free", read_free))
        return 0;

    for (i = 0; i < sizeof data; i++)
        data[i] = (unsigned char)"Zstandard zip entry\n"[i % 20];
    entry = entry_new();
    if (entry == NULL) {
        fprintf(stderr, "FAIL: archive_entry_new returned NULL\n");
        return 0;
    }
    set_pathname(entry, "zstd.txt");
    set_filetype(entry, AE_IFREG);
    set_perm(entry, 0644);
    set_size(entry, (int64_t)sizeof data);
    a = write_new();
    if (a == NULL) {
        fprintf(stderr, "FAIL: archive_write_new returned NULL\n");
        ok = 0;
    } else if (set_format_zip(a) != ARCHIVE_OK || set_options(a, "zip:compression=zstd") != ARCHIVE_OK
        || write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
        || write_header(a, entry) != ARCHIVE_OK
        || write_data(a, data, sizeof data) != (ssize_t)sizeof data || write_close(a) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: writing the Zstandard zip entry: %s\n", p != NULL ? p : "(no error string)");
        ok = 0;
    }
    if (a != NULL)
        write_free(a);
    entry_free(entry);
    if (!ok)
        return 0;
    /* Local file header: compression method at 8, 93 for Zstandard */
    if (used < 30 || memcmp(buf, "PK\3\4", 4) != 0 || buf[8] != 93 || buf[9] != 0) {
        fprintf(stderr, "FAIL: the zip entry is not compressed with Zstandard\n");
        return 0;
    }
    a = read_new();
    if (a == NULL) {
        fprintf(stderr, "FAIL: archive_read_new returned NULL\n");
        ok = 0;
    } else if (support_format_zip(a) != ARCHIVE_OK || open_memory(a, buf, used) != ARCHIVE_OK
        || next_header(a, &entry) != ARCHIVE_OK) {
        p = error_string(a);
        fprintf(stderr, "FAIL: reading the Zstandard zip entry: %s\n", p != NULL ? p : "(no error string)");
        ok = 0;
    } else {
        while (ok && (n = read_data(a, got, sizeof got)) > 0) {
            if ((size_t)n > sizeof data - total || memcmp(got, data + total, (size_t)n) != 0) {
                fprintf(stderr, "FAIL: the Zstandard zip entry's data differs at or after byte %zu\n", total);
                ok = 0;
            }
            total += (size_t)n;
        }
        if (ok && n < 0) {
            p = error_string(a);
            fprintf(stderr, "FAIL: reading the Zstandard zip entry after %zu of %zu bytes: %s\n", total,
                sizeof data, p != NULL ? p : "(no error string)");
            ok = 0;
        } else if (ok && total != sizeof data) {
            fprintf(stderr, "FAIL: the Zstandard zip entry read back as %zu bytes, not %zu\n", total,
                sizeof data);
            ok = 0;
        }
    }
    if (a != NULL)
        read_free(a);
    if (ok)
        printf("OK: a Zstandard zip entry of 128 KiB reads back from memory\n");
    return ok;
}

int main(int argc, char **argv)
{
    const char *(*version_string)(void);
    const char *(*version_details)(void);
    struct archive *(*read_new)(void);
    int (*support_filter_all)(struct archive *);
    int (*support_format_all)(struct archive *);
    int (*open_memory)(struct archive *, const void *, size_t);
    int (*next_header)(struct archive *, struct archive_entry **);
    const char *(*error_string)(struct archive *);
    int (*read_free)(struct archive *);
    struct archive *reader;
    struct archive_entry *entry;
    const char *text;
    int result, failed = 0;

    if (argc != 2) {
        fprintf(stderr, "usage: %s <path to libarchive.so>\n", argv[0]);
        return 2;
    }
    library = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if (library == NULL) {
        fprintf(stderr, "FAIL: dlopen %s: %s\n", argv[1], dlerror());
        return 1;
    }
    if (!find("archive_version_string", &version_string, sizeof version_string)
        || !find("archive_version_details", &version_details, sizeof version_details)
        || !find("archive_read_new", &read_new, sizeof read_new)
        || !find("archive_read_support_filter_all", &support_filter_all, sizeof support_filter_all)
        || !find("archive_read_support_format_all", &support_format_all, sizeof support_format_all)
        || !find("archive_read_open_memory", &open_memory, sizeof open_memory)
        || !find("archive_read_next_header", &next_header, sizeof next_header)
        || !find("archive_error_string", &error_string, sizeof error_string)
        || !find("archive_read_free", &read_free, sizeof read_free))
        return 1;

    text = version_string();
    if (text == NULL || strncmp(text, "libarchive ", 11) != 0) {
        fprintf(stderr, "FAIL: archive_version_string returned %s\n", text != NULL ? text : "NULL");
        return 1;
    }
    printf("archive_version_string: %s\n", text);
    text = version_details();
    if (text == NULL) {
        fprintf(stderr, "FAIL: archive_version_details returned NULL\n");
        return 1;
    }
    printf("archive_version_details: %s\n", text);

    /* Read an empty input with every filter and format enabled: each of them bids on
       it, and the "empty" format must win and report the end of the archive. */
    reader = read_new();
    if (reader == NULL) {
        fprintf(stderr, "FAIL: archive_read_new returned NULL\n");
        return 1;
    }
    if ((result = support_filter_all(reader)) != ARCHIVE_OK) {
        fprintf(stderr, "FAIL: archive_read_support_filter_all returned %d\n", result);
        failed = 1;
    }
    if ((result = support_format_all(reader)) != ARCHIVE_OK) {
        fprintf(stderr, "FAIL: archive_read_support_format_all returned %d\n", result);
        failed = 1;
    }
    if (!failed && (result = open_memory(reader, "", 0)) != ARCHIVE_OK) {
        text = error_string(reader);
        fprintf(stderr, "FAIL: archive_read_open_memory returned %d: %s\n", result,
            text != NULL ? text : "(no error string)");
        failed = 1;
    }
    if (!failed && (result = next_header(reader, &entry)) != ARCHIVE_EOF) {
        text = error_string(reader);
        fprintf(stderr, "FAIL: archive_read_next_header on an empty input returned %d, not ARCHIVE_EOF: %s\n",
            result, text != NULL ? text : "(no error string)");
        failed = 1;
    }
    if ((result = read_free(reader)) != ARCHIVE_OK) {
        fprintf(stderr, "FAIL: archive_read_free returned %d\n", result);
        failed = 1;
    }
    if (!check_utf8_names())
        failed = 1;
    if (!check_xar())
        failed = 1;
    if (!check_zip_zstd_end())
        failed = 1;
    if (failed)
        return 1;
    printf("OK: %s loaded with RTLD_NOW and read an empty archive\n", argv[1]);
    return 0;
}
