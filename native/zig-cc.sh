#!/bin/sh
# The C and C++ compiler of the Linux builds. build-linux.sh links <host>-gcc and <host>-g++
# in its tool directory to this file and uses them as CC and CXX. Called by a name that
# ends in "++" it runs "zig c++", and otherwise "zig cc", for the target ZIG_TARGET (such as
# x86_64-linux-gnu.2.28, in which 2.28 is the oldest glibc that the code may need), with
# that target's own flags, ZIG_TARGET_FLAGS. build-linux.sh exports both, and ZIG, the zig
# program.
#
# What it changes:
# - -static-libgcc and -static-libstdc++ are dropped. They mean something to GCC only, and
#   zig links its own compiler-rt into everything that it links.
# - A query that only prints something (any -print-* option, -dumpmachine, -dumpversion,
#   --version, or -v on its own) is given the target without its glibc version: zig rejects
#   a versioned target there ("version '.2.28' in target triple ... is invalid"), and
#   libtool's configure would print that error for "$CC -print-search-dirs".
# - Everything else is compiled with -fstack-protector-strong, which zig leaves off, and
#   with -g0 after all the other arguments: zig compiles with debug information unless told
#   not to, which would make the libraries several times their size.
#
# It stays POSIX sh, and splits ZIG_TARGET_FLAGS, a list of flags in one string, into words
# on purpose; set -f stops the split from also expanding wildcards.
set -f

if [ -z "${ZIG:-}" ] || [ -z "${ZIG_TARGET:-}" ]; then
    echo "${0##*/}: ZIG and ZIG_TARGET must be set (see native/zig-cc.sh)" >&2
    exit 1
fi

case "${0##*/}" in
    *++) mode=c++ ;;
    *)   mode=cc ;;
esac

query=no
if [ "$#" -eq 1 ] && [ "$1" = -v ]; then
    query=yes
fi
# Each argument is shifted off the front and, unless dropped, put back at the end
for arg do
    shift
    case $arg in
        -static-libgcc | -static-libstdc++) continue ;;
        -print-* | -dumpmachine | -dumpversion | --version) query=yes ;;
    esac
    set -- "$@" "$arg"
done

if [ "$query" = yes ]; then
    exec "$ZIG" "$mode" -target "${ZIG_TARGET%%.*}" "$@"
fi
# shellcheck disable=SC2086  # a list of flags, split on purpose (see above)
exec "$ZIG" "$mode" -target "$ZIG_TARGET" $ZIG_TARGET_FLAGS -fstack-protector-strong "$@" -g0
