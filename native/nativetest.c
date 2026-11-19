#include <stdio.h>
#include <string.h>
#include <wchar.h>
#include <archive.h>
#include <archive_entry.h>

/* "Größe.txt", composed (NFC), as UTF-8 and as Unicode code points */
static const char name_utf8[] = "Gr\xc3\xb6\xc3\x9f" "e.txt";
static const wchar_t name_wcs[] = { 'G', 'r', 0xF6, 0xDF, 'e', '.', 't', 'x', 't', 0 };

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
  if (archive_write_set_format_zip(a) != ARCHIVE_OK
      || archive_write_open_memory(a, buf, sizeof buf, &used) != ARCHIVE_OK
      || archive_write_header(a, e) != ARCHIVE_OK
      || archive_write_close(a) != ARCHIVE_OK) {
    printf("FAIL: writing the zip entry: %s\n", archive_error_string(a));
    ok = 0;
  }
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
  if (archive_read_support_format_zip(a) != ARCHIVE_OK
      || archive_read_open_memory(a, buf, used) != ARCHIVE_OK
      || archive_read_next_header(a, &e) != ARCHIVE_OK) {
    printf("FAIL: reading the zip entry: %s\n", archive_error_string(a));
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

int main(int argc,char **argv) {
  int ok=1;
  printf("archive_zlib_version=%s\n",archive_zlib_version());
  ok = (archive_zlib_version() != NULL)?ok:0;
  printf("archive_liblzma_version=%s\n",archive_liblzma_version());
  ok = (archive_liblzma_version() != NULL)?ok:0;
  printf("archive_bzlib_version=%s\n",archive_bzlib_version());
  ok = (archive_bzlib_version() != NULL)?ok:0;
  printf("archive_liblz4_version=%s\n",archive_liblz4_version());
  ok = (archive_liblz4_version() != NULL)?ok:0;
  printf("archive_libzstd_version=%s\n",archive_libzstd_version());
  ok = (archive_libzstd_version() != NULL)?ok:0;
  ok = check_utf8_names()?ok:0;
  return ok==0;
}
