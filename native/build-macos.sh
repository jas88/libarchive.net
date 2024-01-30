#!/bin/sh -v

set -e

brew install autoconf automake

export PREFIX=`pwd`/local
export NCPU=`sysctl -n hw.ncpu`
export CONFIGCACHE=`pwd`/configcache
export CPPFLAGS="-I$PREFIX/include"
export LDFLAGS="-L$PREFIX/lib -liconv"
export CFLAGS="-fPIC -O2 -D_FILE_OFFSET_BITS=64 -arch arm64 -arch x86_64"

curl -sL https://github.com/libarchive/libarchive/releases/download/v3.7.2/libarchive-3.7.2.tar.xz | tar xJf -
curl -sL https://github.com/lz4/lz4/archive/refs/tags/v1.9.4.tar.gz | tar xzf -
curl -sL https://github.com/facebook/zstd/releases/download/v1.5.5/zstd-1.5.5.tar.gz | tar xzf -
curl -sL http://www.oberhumer.com/opensource/lzo/download/lzo-2.10.tar.gz | tar xzf -
curl -sL https://gitlab.gnome.org/GNOME/libxml2/-/archive/v2.10.3/libxml2-v2.10.3.tar.bz2 | tar xjf -
curl -sL https://www.sourceware.org/pub/bzip2/bzip2-latest.tar.gz | tar xzf -
curl -sL https://zlib.net/zlib-1.3.tar.xz | tar xJf -
curl -sL https://tukaani.org/xz/xz-5.4.0.tar.xz | tar xJf -

make -j$NCPU -sC lz4-1.9.4 install PREFIX=$PREFIX CFLAGS="$CFLAGS"
make -j$NCPU -sC bzip2-1.0.8 install PREFIX=$PREFIX CFLAGS="$CFLAGS"

cd lzo-2.10
./configure --cache-file=$CONFIGCACHE --prefix=$PREFIX
make -sj$NCPU install

cd ../zlib-1.3
./configure --static --prefix=$PREFIX
make -sj$NCPU install
cd ../xz-5.4.0
./configure --cache-file=$CONFIGCACHE --with-pic --disable-shared --prefix=$PREFIX
make -sj$NCPU install
cd ../libxml2-v2.10.3
./autogen.sh --enable-silent-rules --disable-shared --enable-static --prefix=$PREFIX --without-python --with-zlib=$PREFIX/../zlib-1.3 --with-lzma=$PREFIX/../xz-5.4.0
make -sj$NCPU install

make -j$NCPU -sC ../zstd-1.5.5 install

cd ../libarchive-*
export LIBXML2_PC_CFLAGS=-I$PREFIX/include/libxml2
export LIBXML2_PC_LIBS="-L$PREFIX -lxml2"
./configure --cache-file=$CONFIGCACHE --prefix=$PREFIX --enable-silent-rules --disable-dependency-tracking --enable-static --disable-shared --disable-bsdtar --disable-bsdcat --disable-bsdcpio --disable-rpath --enable-posix-regex-lib=libc --enable-xattr --enable-acl --enable-largefile --with-pic --with-zlib --with-bz2lib --with-libb2 --with-iconv --with-lz4 --with-zstd --with-lzma --with-lzo2 --with-cng
make -sj$NCPU install
cd ..

clang -arch arm64 -arch x86_64 -dynamiclib -shared -o libarchive.dylib -Wl,-force_load local/lib/libarchive.a local/lib/libbz2.a local/lib/libz.a local/lib/libxml2.a local/lib/liblzma.a local/lib/liblzo2.a local/lib/libzstd.a local/lib/liblz4.a -liconv
gcc -o nativetest native/nativetest.c local/lib/libarchive.a -Llocal/lib -Ilocal/include -llz4 -lzstd -liconv -lbz2
./nativetest
