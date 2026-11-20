#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <archive.h>
#include <archive_entry.h>

/* "Größe.txt", composed (NFC), as UTF-8 and as Unicode code points */
static const char name_utf8[] = "Gr\xc3\xb6\xc3\x9f" "e.txt";
static const wchar_t name_wcs[] = { 'G', 'r', 0xF6, 0xDF, 'e', '.', 't', 'x', 't', 0 };

/* A string from libarchive that can be NULL (an error or version string), for printing */
static const char *text(const char *s)
{
  return s != NULL ? s : "(null)";
}

/*
 * Fail unless libarchive's narrow strings are UTF-8 (native/libarchive-narrow-utf8.patch).
 * This program never calls setlocale(), so it runs in the "C" locale, as a .NET process
 * does, whose character set is ASCII.
 */
static int check_utf8_names(void)
{
  static char buf[65536];
  size_t used = 0, len = sizeof name_utf8 - 1;
  struct archive *a;
  struct archive_entry *e;
  const wchar_t *w;
  const char *p;
  int ok = 1;

  /* Narrow to wide and back: archive_wstring_append_from_mbs, archive_string_append_from_wcs */
  e = archive_entry_new();
  if (e == NULL) {
    printf("FAIL: archive_entry_new returned NULL\n");
    return 0;
  }
  archive_entry_set_pathname(e, name_utf8);
  w = archive_entry_pathname_w(e);
  if (w == NULL || wcscmp(w, name_wcs) != 0) {
    printf("FAIL: archive_entry_pathname_w does not decode the UTF-8 name\n");
    ok = 0;
  }
  archive_entry_copy_pathname_w(e, name_wcs);
  p = archive_entry_pathname(e);
  if (p == NULL || strcmp(p, name_utf8) != 0) {
    printf("FAIL: archive_entry_pathname does not encode the wide name as UTF-8\n");
    ok = 0;
  }

  /* A zip entry keeps the name's UTF-8 bytes, with the UTF-8 flag, and reads back unchanged */
  archive_entry_set_pathname(e, name_utf8);
  archive_entry_set_filetype(e, AE_IFREG);
  archive_entry_set_perm(e, 0644);
  archive_entry_set_size(e, 0);
  a = archive_write_new();
  if (a == NULL) {
    printf("FAIL: archive_write_new returned NULL\n");
    ok = 0;
  } else if (archive_write_set_format_zip(a) != ARCHIVE_OK
      || archive_write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
      || archive_write_header(a, e) != ARCHIVE_OK
      || archive_write_close(a) != ARCHIVE_OK) {
    printf("FAIL: writing the zip entry: %s\n", text(archive_error_string(a)));
    ok = 0;
  }
  if (a != NULL)
    archive_write_free(a);
  archive_entry_free(e);
  if (!ok)
    return 0;
  /* Local file header: general purpose flags at 6 (bit 11 is UTF-8), name length at 26, name at 30 */
  if (used < 30 + len || memcmp(buf, "PK\3\4", 4) != 0 || (buf[7] & 0x08) == 0
      || (size_t)(unsigned char)buf[26] != len || buf[27] != 0 || memcmp(buf + 30, name_utf8, len) != 0) {
    printf("FAIL: the zip local file header lacks the UTF-8 flag or the UTF-8 name\n");
    return 0;
  }
  a = archive_read_new();
  if (a == NULL) {
    printf("FAIL: archive_read_new returned NULL\n");
    return 0;
  }
  if (archive_read_support_format_zip(a) != ARCHIVE_OK
      || archive_read_open_memory(a, buf, used) != ARCHIVE_OK
      || archive_read_next_header(a, &e) != ARCHIVE_OK) {
    printf("FAIL: reading the zip entry: %s\n", text(archive_error_string(a)));
    ok = 0;
  } else if ((p = archive_entry_pathname(e)) == NULL || strcmp(p, name_utf8) != 0) {
    printf("FAIL: the zip entry's name does not read back as the same (NFC) UTF-8\n");
    ok = 0;
  }
  archive_read_free(a);
  if (ok)
    printf("UTF-8 names: OK\n");
  return ok;
}

/* The one entry of the XAR test archive */
static const char xar_name[] = "xar-test.txt";
static const char xar_data[] = "XAR native test\n";

/*
 * Fail unless a XAR archive written to memory reads back: one entry, its name and its data.
 * libarchive built without libxml2 (or zlib, MD5 and SHA-1) has stubs in place of its XAR
 * reader and writer, which return ARCHIVE_WARN with the message "Xar not supported on this
 * platform". The XAR writer keeps entry data in a temporary file until the archive is closed.
 */
static int check_xar(void)
{
  static char buf[65536];
  char data[64];
  size_t used = 0, len = sizeof xar_data - 1, got = 0;
  struct archive *a;
  struct archive_entry *e;
  const char *p;
  la_ssize_t n;
  int r, ok = 1;

  e = archive_entry_new();
  if (e == NULL) {
    printf("FAIL: archive_entry_new returned NULL\n");
    return 0;
  }
  archive_entry_set_pathname(e, xar_name);
  archive_entry_set_filetype(e, AE_IFREG);
  archive_entry_set_perm(e, 0644);
  archive_entry_set_size(e, (la_int64_t)len);
  a = archive_write_new();
  if (a == NULL) {
    printf("FAIL: archive_write_new returned NULL\n");
    ok = 0;
  } else if ((r = archive_write_set_format_xar(a)) != ARCHIVE_OK) {
    printf("FAIL: archive_write_set_format_xar returned %d: %s\n", r, text(archive_error_string(a)));
    ok = 0;
  } else if (archive_write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
      || archive_write_header(a, e) != ARCHIVE_OK
      || archive_write_data(a, xar_data, len) != (la_ssize_t)len
      || archive_write_close(a) != ARCHIVE_OK) {
    printf("FAIL: writing the XAR entry: %s\n", text(archive_error_string(a)));
    ok = 0;
  }
  if (a != NULL)
    archive_write_free(a);
  archive_entry_free(e);
  if (!ok)
    return 0;
  if (used < 4 || memcmp(buf, "xar!", 4) != 0) {
    printf("FAIL: the XAR archive does not start with the XAR magic number\n");
    return 0;
  }

  a = archive_read_new();
  if (a == NULL) {
    printf("FAIL: archive_read_new returned NULL\n");
    return 0;
  }
  if ((r = archive_read_support_format_xar(a)) != ARCHIVE_OK) {
    printf("FAIL: archive_read_support_format_xar returned %d: %s\n", r, text(archive_error_string(a)));
    ok = 0;
  } else if (archive_read_open_memory(a, buf, used) != ARCHIVE_OK
      || archive_read_next_header(a, &e) != ARCHIVE_OK) {
    printf("FAIL: reading the XAR entry: %s\n", text(archive_error_string(a)));
    ok = 0;
  } else if ((p = archive_entry_pathname(e)) == NULL || strcmp(p, xar_name) != 0) {
    printf("FAIL: the XAR entry's name does not read back\n");
    ok = 0;
  } else {
    n = 0;
    while (got < sizeof data && (n = archive_read_data(a, data + got, sizeof data - got)) > 0)
      got += (size_t)n;
    if (n < 0) {
      printf("FAIL: reading the XAR entry's data: %s\n", text(archive_error_string(a)));
      ok = 0;
    } else if (got != len || memcmp(data, xar_data, len) != 0) {
      printf("FAIL: the XAR entry's data does not read back\n");
      ok = 0;
    } else if ((r = archive_read_next_header(a, &e)) != ARCHIVE_EOF) {
      printf("FAIL: archive_read_next_header after the XAR entry returned %d, not ARCHIVE_EOF: %s\n",
          r, text(archive_error_string(a)));
      ok = 0;
    }
  }
  archive_read_free(a);
  if (ok)
    printf("XAR round trip: OK\n");
  return ok;
}

/*
 * Fail unless a zip entry of 128 KiB, compressed with Zstandard, reads back in full from memory,
 * which libarchive reads as a seekable source (native/libarchive-zip-zstd-end-of-entry.patch).
 * Its data ends just as the zip reader's buffer, of ZSTD_DStreamOutSize() bytes, fills.
 */
static int check_zip_zstd_end(void)
{
  static char data[131072], buf[65536], got[4096];
  size_t used = 0, total = 0, i;
  struct archive *a;
  struct archive_entry *e;
  la_ssize_t n = 0;
  int ok = 1;

  for (i = 0; i < sizeof data; i++)
    data[i] = "Zstandard zip entry\n"[i % 20];
  e = archive_entry_new();
  if (e == NULL) {
    printf("FAIL: archive_entry_new returned NULL\n");
    return 0;
  }
  archive_entry_set_pathname(e, "zstd.txt");
  archive_entry_set_filetype(e, AE_IFREG);
  archive_entry_set_perm(e, 0644);
  archive_entry_set_size(e, sizeof data);
  a = archive_write_new();
  if (a == NULL) {
    printf("FAIL: archive_write_new returned NULL\n");
    ok = 0;
  } else if (archive_write_set_format_zip(a) != ARCHIVE_OK
      || archive_write_set_options(a, "zip:compression=zstd") != ARCHIVE_OK
      || archive_write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
      || archive_write_header(a, e) != ARCHIVE_OK
      || archive_write_data(a, data, sizeof data) != (la_ssize_t)sizeof data
      || archive_write_close(a) != ARCHIVE_OK) {
    printf("FAIL: writing the Zstandard zip entry: %s\n", text(archive_error_string(a)));
    ok = 0;
  }
  if (a != NULL)
    archive_write_free(a);
  archive_entry_free(e);
  if (!ok)
    return 0;
  /* Local file header: compression method at 8, 93 for Zstandard */
  if (used < 30 || memcmp(buf, "PK\3\4", 4) != 0 || buf[8] != 93 || buf[9] != 0) {
    printf("FAIL: the zip entry is not compressed with Zstandard\n");
    return 0;
  }
  a = archive_read_new();
  if (a == NULL) {
    printf("FAIL: archive_read_new returned NULL\n");
    return 0;
  }
  if (archive_read_support_format_zip(a) != ARCHIVE_OK
      || archive_read_open_memory(a, buf, used) != ARCHIVE_OK
      || archive_read_next_header(a, &e) != ARCHIVE_OK) {
    printf("FAIL: reading the Zstandard zip entry: %s\n", text(archive_error_string(a)));
    ok = 0;
  } else {
    while (ok && (n = archive_read_data(a, got, sizeof got)) > 0) {
      if ((size_t)n > sizeof data - total || memcmp(got, data + total, (size_t)n) != 0) {
        printf("FAIL: the Zstandard zip entry's data differs at or after byte %zu\n", total);
        ok = 0;
      }
      total += (size_t)n;
    }
    if (ok && n < 0) {
      printf("FAIL: reading the Zstandard zip entry after %zu of %zu bytes: %s\n", total,
          sizeof data, text(archive_error_string(a)));
      ok = 0;
    } else if (ok && total != sizeof data) {
      printf("FAIL: the Zstandard zip entry read back as %zu bytes, not %zu\n", total, sizeof data);
      ok = 0;
    }
  }
  archive_read_free(a);
  if (ok)
    printf("Zstandard zip entry of 128 KiB: OK\n");
  return ok;
}

int main(void) {
  int ok=1;
  printf("archive_zlib_version=%s\n",text(archive_zlib_version()));
  ok = (archive_zlib_version() != NULL)?ok:0;
  printf("archive_liblzma_version=%s\n",text(archive_liblzma_version()));
  ok = (archive_liblzma_version() != NULL)?ok:0;
  printf("archive_bzlib_version=%s\n",text(archive_bzlib_version()));
  ok = (archive_bzlib_version() != NULL)?ok:0;
  printf("archive_liblz4_version=%s\n",text(archive_liblz4_version()));
  ok = (archive_liblz4_version() != NULL)?ok:0;
  printf("archive_libzstd_version=%s\n",text(archive_libzstd_version()));
  ok = (archive_libzstd_version() != NULL)?ok:0;
  ok = check_utf8_names()?ok:0;
  ok = check_xar()?ok:0;
  ok = check_zip_zstd_end()?ok:0;
  return ok==0;
}
