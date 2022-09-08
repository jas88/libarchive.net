#include <stdio.h>
#include <archive.h>

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
  return ok==0;
}