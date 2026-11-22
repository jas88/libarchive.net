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
# - A query that only prints something is given the target without its glibc version: zig
#   rejects a versioned target there ("version '.2.28' in target triple ... is invalid"), and
#   libtool's configure would print that error for "$CC -print-search-dirs". A call is a
#   query if it has a -print-* option, -dumpmachine, -dumpversion or --version, or is -v on
#   its own, and nothing else but options: no input file, no "-" for standard input, no
#   library (-l...) and none of -c, -S, -E, -o and -shared. A call that also compiles or
#   links takes the path below, with all of its flags, and fails there if zig rejects it.
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
builds=no
if [ "$#" -eq 1 ] && [ "$1" = -v ]; then
    query=yes
fi
# Each argument is shifted off the front and, unless dropped, put back at the end. Any word
# that is not an option counts as an input, as does the argument of an option given as a
# word of its own (such as "-I dir"), which errs towards the full flags.
for arg do
    shift
    case $arg in
        -static-libgcc | -static-libstdc++) continue ;;
        -print-* | -dumpmachine | -dumpversion | --version) query=yes ;;
        -c | -S | -E | -o* | -shared | -l* | -) builds=yes ;;
        -*) ;;
        *) builds=yes ;;
    esac
    set -- "$@" "$arg"
done

if [ "$query" = yes ] && [ "$builds" = no ]; then
    exec "$ZIG" "$mode" -target "${ZIG_TARGET%%.*}" "$@"
fi
# shellcheck disable=SC2086  # a list of flags, split on purpose (see above)
exec "$ZIG" "$mode" -target "$ZIG_TARGET" $ZIG_TARGET_FLAGS -fstack-protector-strong "$@" -g0
