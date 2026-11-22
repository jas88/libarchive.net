/*
 * The load test's checks of archives in files (see linux-loadtest.h): check_file_io has the
 * library write archives to files, in a directory that it makes in $TMPDIR or /tmp and then
 * removes, and read them back. In a glibc process the library's file functions are glibc's,
 * so these checks fail if it calls them with arguments laid out otherwise than glibc's are.
 */
#define _GNU_SOURCE
#define _FILE_OFFSET_BITS 64
#include <dirent.h>
#include <errno.h>
#include <stdarg.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#include <sys/types.h>
#include <unistd.h>

#include "linux-loadtest.h"

/* ARCHIVE_READDISK_NO_XATTR, ARCHIVE_READDISK_NO_ACL and ARCHIVE_READDISK_NO_FFLAGS */
#define READDISK_BEHAVIOR (0x0010 | 0x0020 | 0x0040)

/* The libarchive functions that this file's checks use, with their types: load_file_api finds them */
#define FILE_API(X) \
    X(archive_error_string, const char *, (struct archive *)) \
    X(archive_entry_new, struct archive_entry *, (void)) \
    X(archive_entry_free, void, (struct archive_entry *)) \
    X(archive_entry_pathname, const char *, (struct archive_entry *)) \
    X(archive_entry_set_pathname, void, (struct archive_entry *, const char *)) \
    X(archive_entry_filetype, mode_t, (struct archive_entry *)) \
    X(archive_entry_set_filetype, void, (struct archive_entry *, unsigned int)) \
    X(archive_entry_set_perm, void, (struct archive_entry *, mode_t)) \
    X(archive_entry_size, int64_t, (struct archive_entry *)) \
    X(archive_entry_set_size, void, (struct archive_entry *, int64_t)) \
    X(archive_write_new, struct archive *, (void)) \
    X(archive_write_set_format_by_name, int, (struct archive *, const char *)) \
    X(archive_write_open_filename, int, (struct archive *, const char *)) \
    X(archive_write_header, int, (struct archive *, struct archive_entry *)) \
    X(archive_write_data, ssize_t, (struct archive *, const void *, size_t)) \
    X(archive_write_data_block, ssize_t, (struct archive *, const void *, size_t, int64_t)) \
    X(archive_write_finish_entry, int, (struct archive *)) \
    X(archive_write_close, int, (struct archive *)) \
    X(archive_write_free, int, (struct archive *)) \
    X(archive_write_disk_new, struct archive *, (void)) \
    X(archive_write_disk_set_options, int, (struct archive *, int)) \
    X(archive_read_new, struct archive *, (void)) \
    X(archive_read_support_filter_all, int, (struct archive *)) \
    X(archive_read_support_format_all, int, (struct archive *)) \
    X(archive_read_open_filename, int, (struct archive *, const char *, size_t)) \
    X(archive_read_open_filenames, int, (struct archive *, const char **, size_t)) \
    X(archive_read_open_FILE, int, (struct archive *, FILE *)) \
    X(archive_read_next_header, int, (struct archive *, struct archive_entry **)) \
    X(archive_read_data, ssize_t, (struct archive *, void *, size_t)) \
    X(archive_read_free, int, (struct archive *)) \
    X(archive_read_disk_new, struct archive *, (void)) \
    X(archive_read_disk_set_behavior, int, (struct archive *, int)) \
    X(archive_read_disk_open, int, (struct archive *, const char *)) \
    X(archive_read_next_header2, int, (struct archive *, struct archive_entry *)) \
    X(archive_read_disk_descend, int, (struct archive *))

#define FILE_API_POINTER(name, type, parameters) type (*name) parameters;
static struct { FILE_API(FILE_API_POINTER) } la;

#define FILE_API_FIND(name, type, parameters) && FIND(#name, la.name)
static int load_file_api(void)
{
    return 1 FILE_API(FILE_API_FIND);
}

/* The entries of every archive that check_files writes: a short one, one larger than the blocks
   in which libarchive reads a file (64 KiB), so that reading the archive takes several reads and
   seeks or skips across them, and a short one after it */
#define LARGE_SIZE (192 * 1024)
static unsigned char large_data[LARGE_SIZE];
static const struct test_entry {
    const char *name;
    const void *data;
    size_t size;
} test_entries[] = {
    { "first.txt", "first entry\n", 12 },
    { "large.bin", large_data, LARGE_SIZE },
    { "last.txt", "last entry\n", 11 },
};
#define TEST_ENTRIES (sizeof test_entries / sizeof test_entries[0])

/* The directory that check_file_io makes for the checks' files; a path in it fits in PATH_SIZE
   bytes, and a description of what was done with that path in WHAT_SIZE */
#define WORK_DIR_SIZE 256
#define PATH_SIZE (2 * WORK_DIR_SIZE)
#define WHAT_SIZE (PATH_SIZE + 64)
static char work_dir[WORK_DIR_SIZE];
/* What check_file_io appends to the temporary directory's name for mkdtemp */
static const char work_dir_template[] = "/libarchive-loadtest-XXXXXX";

/* Say what failed, followed by the library's error message for a, if a is not null */
__attribute__((format(printf, 2, 3)))
static int fail(struct archive *a, const char *format, ...)
{
    const char *error = a != NULL ? la.archive_error_string(a) : NULL;
    va_list ap;

    fputs("FAIL: ", stderr);
    va_start(ap, format);
    vfprintf(stderr, format, ap);
    va_end(ap);
    if (a != NULL)
        fprintf(stderr, ": %s", error != NULL ? error : "(no error string)");
    fputc('\n', stderr);
    return 0;
}

/* Write the test entries to path, in the named format, as LibArchiveWriter writes a file */
static int write_archive(const char *format, const char *path)
{
    struct archive *a = la.archive_write_new();
    struct archive_entry *entry;
    size_t i;
    int ok;

    if (a == NULL)
        return fail(NULL, "archive_write_new returned NULL");
    ok = la.archive_write_set_format_by_name(a, format) == ARCHIVE_OK
        && la.archive_write_open_filename(a, path) == ARCHIVE_OK;
    for (i = 0; ok && i < TEST_ENTRIES; i++) {
        if ((entry = la.archive_entry_new()) == NULL) {
            ok = 0;
            break;
        }
        la.archive_entry_set_pathname(entry, test_entries[i].name);
        la.archive_entry_set_filetype(entry, AE_IFREG);
        la.archive_entry_set_perm(entry, 0644);
        la.archive_entry_set_size(entry, (int64_t)test_entries[i].size);
        ok = la.archive_write_header(a, entry) == ARCHIVE_OK
            && la.archive_write_data(a, test_entries[i].data, test_entries[i].size)
                == (ssize_t)test_entries[i].size;
        la.archive_entry_free(entry);
    }
    if (ok)
        ok = la.archive_write_close(a) == ARCHIVE_OK;
    if (!ok)
        fail(a, "writing a %s archive to %s", format, path);
    la.archive_write_free(a);
    return ok;
}

/*
 * Read the archive that a is open on, and fail unless its entries are the test entries, in order,
 * with their data, besides any directories (the ISO 9660 reader returns the root directory). With
 * skip set, read the data of the last entry only, so that the reader skips the others' data.
 */
static int read_entries(struct archive *a, const char *what, int skip)
{
    static unsigned char data[LARGE_SIZE + 1];
    const struct test_entry *expected;
    struct archive_entry *entry;
    const char *name;
    size_t i, got;
    ssize_t n;
    int result;

    for (i = 0; i < TEST_ENTRIES; i++) {
        expected = &test_entries[i];
        while ((result = la.archive_read_next_header(a, &entry)) == ARCHIVE_OK
            && la.archive_entry_filetype(entry) == AE_IFDIR)
            ;
        if (result != ARCHIVE_OK)
            return fail(a, "%s: archive_read_next_header for %s returned %d", what, expected->name, result);
        if ((name = la.archive_entry_pathname(entry)) == NULL || strcmp(name, expected->name) != 0) {
            fprintf(stderr, "FAIL: %s: entry %zu is named %s, not %s\n", what, i + 1,
                name != NULL ? name : "(null)", expected->name);
            return 0;
        }
        if (skip && i + 1 < TEST_ENTRIES)
            continue;
        got = 0;
        n = 0;
        while (got < sizeof data && (n = la.archive_read_data(a, data + got, sizeof data - got)) > 0)
            got += (size_t)n;
        if (n < 0)
            return fail(a, "%s: reading the data of %s", what, expected->name);
        if (got != expected->size || memcmp(data, expected->data, got) != 0) {
            fprintf(stderr, "FAIL: %s: %s reads back as %zu bytes that are not its %zu\n", what,
                expected->name, got, expected->size);
            return 0;
        }
    }
    if ((result = la.archive_read_next_header(a, &entry)) != ARCHIVE_EOF)
        return fail(a, "%s: archive_read_next_header after the last entry returned %d, not ARCHIVE_EOF",
            what, result);
    return 1;
}

/* A reader with every filter and format, as LibArchiveReader makes */
static struct archive *new_reader(void)
{
    struct archive *a = la.archive_read_new();

    if (a == NULL)
        fail(NULL, "archive_read_new returned NULL");
    else if (la.archive_read_support_filter_all(a) != ARCHIVE_OK
        || la.archive_read_support_format_all(a) != ARCHIVE_OK) {
        fail(a, "enabling every filter and format");
        la.archive_read_free(a);
        a = NULL;
    }
    return a;
}

/* Read the archive at path as LibArchiveReader reads a file (archive_read_open_filename) */
static int read_path(const char *path, int skip)
{
    struct archive *a = new_reader();
    char what[WHAT_SIZE];
    int ok;

    if (a == NULL)
        return 0;
    snprintf(what, sizeof what, "%s, read from its path%s", path, skip ? ", skipping data" : "");
    ok = la.archive_read_open_filename(a, path, 10240) == ARCHIVE_OK
        ? read_entries(a, what, skip) : fail(a, "%s: archive_read_open_filename", what);
    la.archive_read_free(a);
    return ok;
}

/* Read the archive at path through a FILE (archive_read_open_FILE), which seeks with fseeko */
static int read_stream(const char *path)
{
    struct archive *a;
    char what[WHAT_SIZE];
    FILE *f = fopen(path, "rb");
    int ok;

    snprintf(what, sizeof what, "%s, read from a FILE", path);
    if (f == NULL) {
        fprintf(stderr, "FAIL: %s: fopen: %s\n", what, strerror(errno));
        return 0;
    }
    if ((a = new_reader()) == NULL) {
        fclose(f);
        return 0;
    }
    ok = la.archive_read_open_FILE(a, f) == ARCHIVE_OK
        ? read_entries(a, what, 0) : fail(a, "%s: archive_read_open_FILE", what);
    la.archive_read_free(a);
    fclose(f);
    return ok;
}

/*
 * Split the archive at path into three volumes, <path>.1 to <path>.3, and read them as
 * LibArchiveReader reads a multi-volume archive (archive_read_open_filenames)
 */
#define VOLUMES 3
static int read_volumes(const char *path)
{
    char names[VOLUMES][PATH_SIZE + 8], what[WHAT_SIZE];
    const char *list[VOLUMES + 1];
    unsigned char *content;
    struct archive *a;
    struct stat st;
    size_t size = 0, start, end, i;
    FILE *f;
    int ok;

    snprintf(what, sizeof what, "%s, read as %d volumes", path, VOLUMES);
    if (stat(path, &st) != 0 || (f = fopen(path, "rb")) == NULL) {
        fprintf(stderr, "FAIL: %s: opening the archive: %s\n", what, strerror(errno));
        return 0;
    }
    if ((content = malloc((size_t)st.st_size + 1)) != NULL)
        size = fread(content, 1, (size_t)st.st_size + 1, f);
    ok = content != NULL && !ferror(f) && size == (size_t)st.st_size;
    fclose(f);
    if (!ok) {
        fprintf(stderr, "FAIL: %s: the archive cannot be read\n", what);
        free(content);
        return 0;
    }
    for (i = 0; ok && i < VOLUMES; i++) {
        start = size * i / VOLUMES;
        end = size * (i + 1) / VOLUMES;
        snprintf(names[i], sizeof names[i], "%s.%zu", path, i + 1);
        list[i] = names[i];
        if ((f = fopen(names[i], "wb")) == NULL || fwrite(content + start, 1, end - start, f) != end - start
            || fclose(f) != 0) {
            fprintf(stderr, "FAIL: %s: writing %s: %s\n", what, names[i], strerror(errno));
            ok = 0;
        }
    }
    free(content);
    if (!ok)
        return 0;
    list[VOLUMES] = NULL;
    if ((a = new_reader()) == NULL)
        return 0;
    ok = la.archive_read_open_filenames(a, list, 10240) == ARCHIVE_OK
        ? read_entries(a, what, 0) : fail(a, "%s: archive_read_open_filenames", what);
    la.archive_read_free(a);
    return ok;
}

/*
 * Fail unless archives that the library writes to files, in each of these formats, read back
 * whole through every way of reading a file that libarchive has: from its path, also skipping
 * the data of all but the last entry, as consecutive volumes, and from a FILE. Seeking and
 * skipping in a file read from its path take lseek, as do the 7-Zip, XAR and ISO 9660 writers
 * in their temporary files; reading from a FILE takes fseeko and ftello.
 */
static int check_files(void)
{
    static const char *const formats[][2] = {
        { "zip", "zip" }, { "7zip", "7z" }, { "xar", "xar" }, { "iso9660", "iso" }, { "ustar", "tar" },
    };
    char path[PATH_SIZE];
    size_t i;
    int ok = 1;

    for (i = 0; i < sizeof formats / sizeof formats[0]; i++) {
        snprintf(path, sizeof path, "%s/test.%s", work_dir, formats[i][1]);
        if (!write_archive(formats[i][0], path)) {
            ok = 0;
            continue;
        }
        if (read_path(path, 0) & read_path(path, 1) & read_volumes(path) & read_stream(path))
            printf("OK: %s: an archive written to a file reads back from its path, as volumes and from a FILE\n",
                formats[i][0]);
        else
            ok = 0;
    }
    return ok;
}

/* Count the files in the work directory, and with remove set, remove them; -1 if it cannot be read */
static long work_dir_files(int remove)
{
    char path[PATH_SIZE];
    struct dirent *de;
    DIR *dir = opendir(work_dir);
    long count = 0;

    if (dir == NULL) {
        fprintf(stderr, "FAIL: opendir %s: %s\n", work_dir, strerror(errno));
        return -1;
    }
    while ((de = readdir(dir)) != NULL)
        if (strcmp(de->d_name, ".") != 0 && strcmp(de->d_name, "..") != 0) {
            count++;
            snprintf(path, sizeof path, "%s/%s", work_dir, de->d_name);
            if (remove)
                unlink(path);
        }
    closedir(dir);
    return count;
}

/*
 * Fail unless the library writes a file to disk (archive_write_disk) that has data in its middle
 * only, for which it seeks to the data with lseek and then extends the file to its size with
 * ftruncate. LibArchive.Net uses neither this nor archive_read_disk (check_read_disk), but in a
 * glibc process these are glibc's functions too.
 */
#define SPARSE_SIZE 100000
#define SPARSE_OFFSET 40000
static int check_write_disk(void)
{
    static const char middle[] = "only the middle of this file has data\n";
    static unsigned char content[SPARSE_SIZE + 1], expected[SPARSE_SIZE];
    char path[PATH_SIZE];
    struct archive *a;
    struct archive_entry *entry;
    size_t got, len = sizeof middle - 1;
    FILE *f;
    int ok;

    snprintf(path, sizeof path, "%s/disk.bin", work_dir);
    if ((entry = la.archive_entry_new()) == NULL || (a = la.archive_write_disk_new()) == NULL) {
        la.archive_entry_free(entry);    /* which does nothing with NULL */
        return fail(NULL, "archive_entry_new or archive_write_disk_new returned NULL");
    }
    la.archive_entry_set_pathname(entry, path);
    la.archive_entry_set_filetype(entry, AE_IFREG);
    la.archive_entry_set_perm(entry, 0644);
    la.archive_entry_set_size(entry, SPARSE_SIZE);
    ok = la.archive_write_disk_set_options(a, 0) == ARCHIVE_OK && la.archive_write_header(a, entry) == ARCHIVE_OK
        && la.archive_write_data_block(a, middle, len, SPARSE_OFFSET) == ARCHIVE_OK
        && la.archive_write_finish_entry(a) == ARCHIVE_OK && la.archive_write_close(a) == ARCHIVE_OK;
    if (!ok)
        fail(a, "writing %s with archive_write_disk", path);
    la.archive_write_free(a);
    la.archive_entry_free(entry);
    if (!ok)
        return 0;
    if ((f = fopen(path, "rb")) == NULL) {
        fprintf(stderr, "FAIL: fopen %s: %s\n", path, strerror(errno));
        return 0;
    }
    got = fread(content, 1, sizeof content, f);
    fclose(f);
    memcpy(expected + SPARSE_OFFSET, middle, len);
    if (got != SPARSE_SIZE || memcmp(content, expected, SPARSE_SIZE) != 0) {
        fprintf(stderr, "FAIL: %s, written with archive_write_disk, is %zu bytes, not %d, or does not"
            " have its data at %d and zeros elsewhere\n", path, got, SPARSE_SIZE, SPARSE_OFFSET);
        return 0;
    }
    printf("OK: archive_write_disk writes data in the middle of a file and extends it to its size\n");
    return 1;
}

/* Fail unless the library finds every file in the work directory, with its size, through
   archive_read_disk, which lists the directory with readdir and asks for its file system's
   properties with fstatfs and fstatvfs */
static int check_read_disk(void)
{
    const char *name;
    struct archive *a;
    struct archive_entry *entry;
    long files, found = 0;
    struct stat st;
    int result, ok;

    if ((files = work_dir_files(0)) < 0)
        return 0;
    if ((entry = la.archive_entry_new()) == NULL || (a = la.archive_read_disk_new()) == NULL) {
        la.archive_entry_free(entry);
        return fail(NULL, "archive_entry_new or archive_read_disk_new returned NULL");
    }
    ok = la.archive_read_disk_set_behavior(a, READDISK_BEHAVIOR) == ARCHIVE_OK
        && la.archive_read_disk_open(a, work_dir) == ARCHIVE_OK;
    if (!ok)
        fail(a, "archive_read_disk_open %s", work_dir);
    while (ok && (result = la.archive_read_next_header2(a, entry)) != ARCHIVE_EOF) {
        name = la.archive_entry_pathname(entry);
        if (result != ARCHIVE_OK || name == NULL) {
            ok = fail(a, "archive_read_next_header2 in %s returned %d", work_dir, result);
        } else if (strcmp(name, work_dir) == 0 && la.archive_entry_filetype(entry) == AE_IFDIR) {
            la.archive_read_disk_descend(a);
        } else if (strncmp(name, work_dir, strlen(work_dir)) != 0 || stat(name, &st) != 0
            || (int64_t)st.st_size != la.archive_entry_size(entry)) {
            fprintf(stderr, "FAIL: archive_read_disk found %s in %s, %jd bytes, which is not one of its files\n",
                name, work_dir, (intmax_t)la.archive_entry_size(entry));
            ok = 0;
        } else {
            found++;
        }
    }
    if (ok && found != files) {
        fprintf(stderr, "FAIL: archive_read_disk found %ld files in %s, not its %ld\n", found, work_dir, files);
        ok = 0;
    }
    la.archive_read_free(a);
    la.archive_entry_free(entry);
    if (ok)
        printf("OK: archive_read_disk finds the %ld files in a directory, with their sizes\n", files);
    return ok;
}

/*
 * Make the work directory, fill the large test entry with data that does not compress, run the
 * checks of this file, each whatever the others found, and remove the directory and its files
 */
int check_file_io(void)
{
    const char *tmp = getenv("TMPDIR");
    uint32_t x = 2463534242u;
    size_t i;
    int ok;

    if (!load_file_api())
        return 0;
    if (tmp == NULL || *tmp == '\0')
        tmp = "/tmp";
    i = (size_t)snprintf(work_dir, sizeof work_dir, "%s%s", tmp, work_dir_template);
    if (i >= sizeof work_dir) {
        /* The name, the template and the terminating NUL must fit; sizeof counts the NUL */
        fprintf(stderr, "FAIL: the temporary directory %s has a name longer than %d bytes\n", tmp,
            (int)(sizeof work_dir - sizeof work_dir_template));
        return 0;
    }
    if (mkdtemp(work_dir) == NULL) {
        fprintf(stderr, "FAIL: mkdtemp %s: %s\n", work_dir, strerror(errno));
        return 0;
    }
    for (i = 0; i < LARGE_SIZE; i++) {
        x ^= x << 13;
        x ^= x >> 17;
        x ^= x << 5;
        large_data[i] = (unsigned char)x;
    }
    ok = check_files();
    if (!check_write_disk())
        ok = 0;
    if (!check_read_disk())
        ok = 0;
    if (work_dir_files(1) < 0 || rmdir(work_dir) != 0) {
        fprintf(stderr, "FAIL: removing %s: %s\n", work_dir, strerror(errno));
        ok = 0;
    }
    return ok;
}
