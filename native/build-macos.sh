#!/bin/sh

set -e
export PREFIX=`pwd`/local
export NCPU=`sysctl -n hw.ncpu`
export CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64 -arch arm64 -arch x86_64"

curl -sL https://github.com/libarchive/libarchive/releases/download/v3.6.1/libarchive-3.6.1.tar.xz | tar xJf -
curl -sL https://gitlab.gnome.org/GNOME/libxml2/-/archive/v2.9.14/libxml2-v2.9.14.tar.bz2 | tar xjf -
curl -sL https://www.sourceware.org/pub/bzip2/bzip2-latest.tar.gz | tar xzf -
curl -sL https://zlib.net/zlib-1.2.12.tar.xz | tar xJf -
curl -sL https://tukaani.org/xz/xz-5.2.5.tar.xz | tar xJf -

make -j$NCPU -sC bzip2-1.0.8 install PREFIX=$PREFIX
cd zlib-1.2.12
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
export LIBXML2_PC_LIBS="-L$PREFIX -lxml2"
./configure --prefix=$PREFIX --disable-bsdtar --disable-bsdcat --disable-bsdcpio --enable-posix-regex-lib=libc --with-pic --with-sysroot --with-lzo2
make -sj$NCPU install
cd ..

clang -arch arm64 -arch x86_64 -dynamiclib -o libarchive.dylib -Wl,-force_load local/lib/libarchive.a local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a -liconv
