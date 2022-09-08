#!/bin/sh

set -e
export NCPU=`nproc`
export PREFIX=`pwd`/local
export CPPFLAGS="-I$PREFIX/include -I$PREFIX/x86_64-linux-musl/include -I$PREFIX/lib/gcc/x86_64-linux-musl/9.2.0/include"
export CFLAGS="-fPIC -O2 $CPPFLAGS -static-libgcc"
export CXXFLAGS="-fPIC -O2 -I$PREFIX/x86_64-linux-musl/include/c++/9.2.0 -I$PREFIX/x86_64-linux-musl/include/c++/9.2.0/x86_64-linux-musl $CPPFLAGS -static-libstdc++ -static-libgcc -include sys/time.h"
export LDFLAGS="-L$PREFIX/lib -static"
export PATH="$PREFIX/bin:$PREFIX/x86_64-linux-musl/bin:$PATH"

curl -sL https://github.com/richfelker/musl-cross-make/archive/refs/tags/v0.9.9.tar.gz | tar xzf -
curl -sL https://github.com/libarchive/libarchive/releases/download/v3.6.1/libarchive-3.6.1.tar.xz | tar xJf -
curl -sL https://github.com/lz4/lz4/archive/refs/tags/v1.9.4.tar.gz | tar xzf -
curl -sL https://github.com/facebook/zstd/releases/download/v1.5.2/zstd-1.5.2.tar.gz | tar xzf -
curl -sL http://www.oberhumer.com/opensource/lzo/download/lzo-2.10.tar.gz | tar xzf -
curl -sL https://gitlab.gnome.org/GNOME/libxml2/-/archive/v2.9.14/libxml2-v2.9.14.tar.bz2 | tar xjf -
curl -sL https://www.sourceware.org/pub/bzip2/bzip2-latest.tar.gz | tar xzf -
curl -sL https://zlib.net/zlib-1.2.12.tar.xz | tar xJf -
curl -sL https://tukaani.org/xz/xz-5.2.5.tar.xz | tar xJf -

cd musl-cross-make-0.9.9
cat > config.mak <<EOC
TARGET=x86_64-linux-musl
COMMON_CONFIG += --disable-nls
GCC_CONFIG += --disable-libitm
GCC_CONFIG += --enable-default-pie
EOC
make -sj$NCPU install OUTPUT=$PREFIX 2>&1 >musl.log || cat musl.log

export CC=x86_64-linux-musl-gcc
export CXX=x86_64-linux-musl-g++
cd ..
make -j$NCPU -sC lz4-1.9.4 install
make -j$NCPU -sC zstd-1.5.2 install
make -j$NCPU -sC bzip2-1.0.8 install PREFIX=$PREFIX CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64" CC=$CC

cd lzo-2.10
./configure --prefix=$PREFIX
make -sj$NCPU install

cd ../zlib-1.2.12
./configure --static --prefix=$PREFIX
make -sj$NCPU install

cd ../xz-5.2.5
./configure --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install

cd ../libxml2-v2.9.14
./autogen.sh --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-1.2.12 --with-lzma=$PREFIX/../xz-5.2.5
make -sj$NCPU install

cd ../libarchive-*
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS=-L$PREFIX
./configure --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2
make -sj$NCPU install

cd ..
gcc -shared -o libarchive.so -Wl,--whole-archive local/lib/libarchive.a -Wl,--no-whole-archive local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a local/x86_64-linux-musl/lib/libc.a -nostdlib

cat > test.c <<EOT
#include <stdio.h>
#include <dlfcn.h>
int main() {
   printf("libarchive.so=%p\n",dlopen("./libarchive.so",RTLD_NOW));
   return 0;
}
EOT
gcc -o test test.c
./test
file libarchive.so
ldd libarchive.so

gcc -o nativetest native/nativetest.c local/lib/libarchive.a -Llocal/lib -Ilocal/include -llz4 -lzstd -lbz2
./nativetest
