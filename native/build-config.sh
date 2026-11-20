#!/bin/bash
# Shared build configuration for libarchive native library builds
# Source this file in platform-specific build scripts

# Library versions
LIBARCHIVE_VERSION="3.8.11"
# This project's patches to libarchive, in this directory, one for each change. Every
# build (macOS, Linux and Windows) applies them with patch_libarchive, in this order.
# Each entry is "<patch>:<marker>": once the patch is applied, every file that it
# changes carries its marker, which no other patch uses. A patch may change a file that
# an earlier one changes: its hunks are then against that file as the earlier one leaves
# it. See "The libarchive patches" in BUILD.md.
LIBARCHIVE_PATCHES="
    libarchive-narrow-utf8.patch:LIBARCHIVE_NET_NARROW_UTF8
    libarchive-lz4-init-stream-hc.patch:LIBARCHIVE_NET_LZ4_INIT_STREAM_HC
    libarchive-zip-aes192.patch:LIBARCHIVE_NET_ZIP_AES192
    libarchive-zip-zstd-end-of-entry.patch:LIBARCHIVE_NET_ZIP_ZSTD_END_OF_ENTRY
"
LZ4_VERSION="1.11.1"
ZSTD_VERSION="1.5.7"
# lzokay (MIT) provides lzop support in place of the GPL-licensed liblzo2: see
# build-lzo-shim.sh. It has no releases or tags, so it is pinned to a commit - the
# head of its default branch, which has the fix for building with current compilers.
LZOKAY_COMMIT="db2df1fcbebc2ed06c10f727f72567d40f06a2be"
LIBXML2_VERSION="2.15.4"
ZLIB_VERSION="1.4"
XZ_VERSION="5.8.4"
BZIP2_VERSION="1.0.8"
# mbedTLS (Linux builds only): crypto backend for ZIP AES encryption.
# Pinned to the 3.6 LTS line because libarchive cannot build against mbedTLS 4,
# which removed the public headers it includes; its configure would then fall back
# to the stub cryptor without an error. Upstream support for 3.6 ends 28 March 2027.
MBEDTLS_VERSION="3.6.8"

# SHA256 checksums for download verification
LIBARCHIVE_SHA256="a3dc0872b1b39da34dc18ef1b944f0f11ffd37425d70ae04be679279f1cf43d2"
LZ4_SHA256="683fed08a844ad012196381e40badf80c0bbe4962959761416d41c9e67d77ec7"
ZSTD_SHA256="eb33e51f49a15e023950cd7825ca74a4a2b43db8354825ac24fc1b7ee09e6fa3"
LZOKAY_SHA256="e5314402b590e656474c0087af1f9d6fac727cb7718011791cdab8a61245a066"
LIBXML2_SHA256="98087fd181d9070724f3fbc65c7377db03038eb92bd882374daff44940138821"
ZLIB_SHA256="4f11c512053d7bab8d9e9b89bde3575add3e4c50a383b42ef6cb837035fc5a7c"
XZ_SHA256="4ce24038fd4221e0d13bc1a2de7a4db56e90b92b3bf75321f6c14be73f65de4b"
BZIP2_SHA256="ab5a03176ee106d3f0fa90e381da478ddae405918153cca248e682cd0c4a2269"
MBEDTLS_SHA256="bc54b818e87f49c0b3281dd85d24adaede0e63b4e822e3fbd49c005a38b9595b"

# Bootlin toolchain release (Linux only). Renovate is configured to keep it current
# (.github/renovate.json); the GCC, musl and binutils versions in each release are
# listed at https://toolchains.bootlin.com/.
BOOTLIN_RELEASE="stable-2026.08-1"

# Bootlin toolchain URLs (exported for use in build scripts)
TOOLCHAIN_BASE_URL="https://toolchains.bootlin.com/downloads/releases/toolchains"
export TOOLCHAIN_X64_URL="${TOOLCHAIN_BASE_URL}/x86-64/tarballs/x86-64--musl--${BOOTLIN_RELEASE}.tar.xz"
export TOOLCHAIN_ARM_URL="${TOOLCHAIN_BASE_URL}/armv7-eabihf/tarballs/armv7-eabihf--musl--${BOOTLIN_RELEASE}.tar.xz"
export TOOLCHAIN_ARM64_URL="${TOOLCHAIN_BASE_URL}/aarch64/tarballs/aarch64--musl--${BOOTLIN_RELEASE}.tar.xz"

# Library download URLs
LIBARCHIVE_URL="https://github.com/libarchive/libarchive/releases/download/v${LIBARCHIVE_VERSION}/libarchive-${LIBARCHIVE_VERSION}.tar.xz"
LZ4_URL="https://github.com/lz4/lz4/archive/refs/tags/v${LZ4_VERSION}.tar.gz"
ZSTD_URL="https://github.com/facebook/zstd/releases/download/v${ZSTD_VERSION}/zstd-${ZSTD_VERSION}.tar.gz"
# GitHub's archive of the pinned commit, which unpacks into lzokay-${LZOKAY_COMMIT}/
LZOKAY_URL="https://github.com/AxioDL/lzokay/archive/${LZOKAY_COMMIT}.tar.gz"
# libxml2 URL uses major.minor as directory (e.g., 2.15.1 -> 2.15)
LIBXML2_MAJOR_MINOR="${LIBXML2_VERSION%.*}"
LIBXML2_URL="https://download.gnome.org/sources/libxml2/${LIBXML2_MAJOR_MINOR}/libxml2-${LIBXML2_VERSION}.tar.xz"
BZIP2_URL="https://www.sourceware.org/pub/bzip2/bzip2-${BZIP2_VERSION}.tar.gz"
# GitHub release asset (byte-identical to zlib.net's). zlib.net only hosts the latest
# release and intermittently serves bad content to CI runners under parallel load.
ZLIB_URL="https://github.com/madler/zlib/releases/download/v${ZLIB_VERSION}/zlib-${ZLIB_VERSION}.tar.xz"
XZ_URL="https://github.com/tukaani-project/xz/releases/download/v${XZ_VERSION}/xz-${XZ_VERSION}.tar.xz"
# mbedTLS is downloaded by the Linux build scripts themselves, not by
# download_all_libraries: macOS and Windows use their system crypto and must not
# fetch it. Its URL and checksum are therefore exported for use in those scripts.
# Only a .tar.bz2 release tarball is published.
export MBEDTLS_URL="https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-${MBEDTLS_VERSION}/mbedtls-${MBEDTLS_VERSION}.tar.bz2"
export MBEDTLS_SHA256

# Portable SHA256 helper (computes hash and compares directly;
# avoids --check flag which differs between GNU and BSD sha256sum)
sha256_compute() {
    local file="$1"
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$file" | cut -d' ' -f1
    elif command -v sha256sum >/dev/null 2>&1; then
        sha256sum "$file" | cut -d' ' -f1
    else
        echo "ERROR: No SHA256 tool found (need shasum or sha256sum)" >&2
        return 1
    fi
}

sha256_check() {
    local expected="$1"
    local file="$2"
    local actual
    actual=$(sha256_compute "$file") || return 1
    [ "$expected" = "$actual" ]
}

# Common build settings
export PREFIX="${PREFIX:-$(pwd)/local}"

# Persistent caches, shared by every build on this machine, in a directory of their own
# under the user's cache directory: $XDG_CACHE_HOME, or ~/.cache when that is unset or
# not an absolute path (as the XDG Base Directory specification requires). A generic
# name directly in the home directory can be a folder of the user's own: the download
# cache used to be ~/downloads, which on macOS's default case-insensitive file system
# is the user's Downloads folder. .github/workflows/build.yml caches the same paths.
case "${XDG_CACHE_HOME:-}" in
    /*) LIBARCHIVE_NET_CACHE_DIR="${XDG_CACHE_HOME}/libarchive-net" ;;
    *)  LIBARCHIVE_NET_CACHE_DIR="${HOME}/.cache/libarchive-net" ;;
esac

# Download cache directory (persistent across builds)
export DOWNLOAD_CACHE="${LIBARCHIVE_NET_CACHE_DIR}/downloads"

# Configure cache directory (persistent across builds, shared per host triplet)
export CONFIG_CACHE_DIR="${LIBARCHIVE_NET_CACHE_DIR}/config-cache"

# For a build with a cross toolchain (the Linux and Windows builds): name two tools that
# configure looks for under the host triplet and, not finding them, takes unprefixed
# from the build machine, warning "using cross tools not prefixed with host triplet".
# pkg-config is the build machine's, confined to the .pc files under $PREFIX as a cross
# pkg-config would be. libtool's manifest tool (an mt.exe that embeds manifests): none
# of the MinGW or Bootlin toolchains has one, and an mt on the build machine is the tape
# drive program, so it is ":", libtool's own value for none. Call it once PREFIX has its
# final value.
# Usage: export_cross_build_tools
export_cross_build_tools() {
    export PKG_CONFIG=pkg-config
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"
    export MANIFEST_TOOL=:
}

# Get the config.cache file path for a given host triplet
# Usage: get_config_cache [host-triplet]
# If no host is specified, uses "native"
get_config_cache() {
    local host="${1:-native}"
    mkdir -p "$CONFIG_CACHE_DIR"
    echo "$CONFIG_CACHE_DIR/config.cache.${host}"
}

# Function to download and extract a library
# Downloads to cache if not present, then unpacks fresh copy
download_library() {
    local url="$1"
    local name="$2"
    local dir_name="$3"
    local expected_sha256="${4:-}"

    # Extract archive filename from URL
    local archive_name="${url##*/}"
    local cache_file="${DOWNLOAD_CACHE}/${archive_name}"

    # Create cache directory if it doesn't exist
    mkdir -p "$DOWNLOAD_CACHE"

    # Download to cache if not already present, verifying the checksum. A mismatch
    # (e.g. an upstream serving an error page with HTTP 200) discards the file and
    # retries, so one bad response does not fail the build.
    local max_retries=3
    local attempt=1
    while :; do
        if [ ! -f "$cache_file" ]; then
            echo "Downloading ${name} to cache (attempt ${attempt}/${max_retries})..."
            if ! curl -fsSL "$url" -o "$cache_file"; then
                rm -f "$cache_file"
                if [ $attempt -ge $max_retries ]; then
                    echo "ERROR: Failed to download ${name} after $max_retries attempts"
                    echo "Please check network connectivity or try again later"
                    echo "URL: $url"
                    return 1
                fi
                attempt=$((attempt + 1))
                sleep $((attempt * 2))
                continue
            fi
            echo "Download successful"
        else
            echo "Using cached ${name}..."
        fi

        # No checksum provided: accept the file as-is
        [ -z "$expected_sha256" ] && break

        echo "Verifying ${name} checksum..."
        if sha256_check "$expected_sha256" "$cache_file"; then
            break
        fi
        local actual
        actual=$(sha256_compute "$cache_file") || actual="(unable to compute)"
        echo "WARNING: SHA256 checksum mismatch for ${name}"
        echo "Expected: $expected_sha256"
        echo "Got:      $actual"
        rm -f "$cache_file"
        if [ $attempt -ge $max_retries ]; then
            echo "ERROR: SHA256 checksum mismatch for ${name} after $max_retries attempts"
            echo "URL: $url"
            return 1
        fi
        attempt=$((attempt + 1))
        sleep $((attempt * 2))
    done

    # Delete any existing unpacked directory to ensure clean start
    rm -rf "$dir_name"

    # Unpack from cache
    echo "Unpacking ${name}..."
    case "${url##*.}" in
        xz)  tar xJf "$cache_file" ;;
        bz2) tar xjf "$cache_file" ;;
        *)   tar xzf "$cache_file" ;;
    esac
}

# Print the SHA256 that a checksum file in sha256sum format ("<hash>  <file name>")
# gives for the file named $1. Fails, printing nothing, unless the first line of the
# checksum file $2 holds a well-formed hash and names that file.
# Usage: sha256_for <file-name> <checksum-file>
sha256_for() {
    local wanted="$1"
    local sum_file="$2"
    local hash=""
    local file=""
    [ -f "$sum_file" ] || return 1
    # read fails at a missing final newline, but has still set the fields
    read -r hash file < "$sum_file" || [ -n "$hash" ] || return 1
    [ "${#hash}" -eq 64 ] || return 1
    case "$hash" in
        *[!0-9a-fA-F]*) return 1 ;;
    esac
    # A leading "*" marks a hash computed in binary mode
    [ "${file#\*}" = "$wanted" ] || return 1
    printf '%s\n' "$hash" | tr 'A-F' 'a-f'
}

# Succeed if the file $2 has the SHA256 $1; otherwise say on stderr what it has
# Usage: sha256_verify <expected-sha256> <file>
sha256_verify() {
    local actual
    actual=$(sha256_compute "$2") || return 1
    [ "$1" = "$actual" ] && return 0
    echo "WARNING: SHA256 mismatch for ${2}" >&2
    echo "Expected: $1" >&2
    echo "Got:      ${actual}" >&2
    return 1
}

# Download a URL to a file by way of a temporary file beside it, which is renamed to
# the file only once the given command, run with the temporary file as its last
# argument, accepts it. The file therefore never exists half written or unverified: a
# download that fails, is rejected or is interrupted leaves at most a "<file>.tmp.XXXXXX"
# file, which nothing reads (an interrupted one is left behind, and can be deleted).
# The command's standard output is discarded.
# Usage: download_verified <url> <file> <command> [<argument>...]
download_verified() {
    local url="$1"
    local dest="$2"
    local tmp
    shift 2
    tmp=$(mktemp "${dest}.tmp.XXXXXX") || return 1
    if curl -fsSL "$url" -o "$tmp" && "$@" "$tmp" >/dev/null && mv -f "$tmp" "$dest"; then
        return 0
    fi
    rm -f "$tmp"
    return 1
}

# Download a Bootlin toolchain to the download cache (does not unpack), verified against
# the checksum file Bootlin publishes beside each tarball (<release>.sha256 beside
# <release>.tar.xz). Prints the path of the cached tarball, the only thing written to
# stdout, for the build script to unpack.
#
# The checksum file is cached beside the tarball, so a cached tarball, including one
# restored by CI's download cache, is verified again on every use without a download.
# A cached tarball that does not match is deleted and downloaded again, and a new
# download only replaces it once verified (see download_verified).
#
# What the published checksum protects against: a download that was cut short or
# corrupted, an error page or other wrong content served in the tarball's place, and a
# cached file damaged since it was downloaded. What it does not protect against: a
# compromise of toolchains.bootlin.com itself, since the checksum comes from the same
# server as the tarball, and whoever can replace one can replace the other. Hashes
# pinned in this file would close that gap, but Renovate bumps BOOTLIN_RELEASE
# (.github/renovate.json) and cannot compute them: see "Bootlin toolchains" in BUILD.md.
# Usage: download_toolchain <url> <name>
download_toolchain() {
    local url="$1"
    local name="$2"
    local archive_name="${url##*/}"
    local cache_file="${DOWNLOAD_CACHE}/${archive_name}"
    local sum_url="${url%.tar.*}.sha256"
    local sum_file="${cache_file}.sha256"
    local max_retries=3
    local attempt=1
    local expected

    mkdir -p "$DOWNLOAD_CACHE" || return 1

    while :; do
        # Bootlin's checksum of the tarball: the cached copy if it is well formed and
        # names this tarball, otherwise a fresh download (an unusable copy is replaced)
        if ! expected=$(sha256_for "$archive_name" "$sum_file"); then
            rm -f "$sum_file"
            echo "Downloading Bootlin's checksum of the ${name} toolchain..." >&2
            if ! download_verified "$sum_url" "$sum_file" sha256_for "$archive_name" \
                || ! expected=$(sha256_for "$archive_name" "$sum_file"); then
                echo "WARNING: no usable checksum of ${archive_name} at ${sum_url}" >&2
                expected=""
            fi
        fi

        if [ -n "$expected" ]; then
            if [ -f "$cache_file" ]; then
                if sha256_verify "$expected" "$cache_file"; then
                    echo "Using cached ${name} toolchain (matches Bootlin's checksum)" >&2
                    break
                fi
                echo "WARNING: the cached ${name} toolchain does not match Bootlin's checksum: downloading it again" >&2
                rm -f "$cache_file"
            fi
            echo "Downloading ${name} toolchain to cache (attempt ${attempt}/${max_retries})..." >&2
            if download_verified "$url" "$cache_file" sha256_verify "$expected"; then
                echo "Toolchain download verified against Bootlin's checksum" >&2
                break
            fi
            # A mismatch can also come from a stale cached checksum: fetch it again too
            rm -f "$sum_file"
        fi

        if [ "$attempt" -ge "$max_retries" ]; then
            echo "ERROR: Failed to download and verify the ${name} toolchain after ${max_retries} attempts" >&2
            echo "URL: ${url}" >&2
            echo "Checksum URL: ${sum_url}" >&2
            return 1
        fi
        attempt=$((attempt + 1))
        echo "Retrying the ${name} toolchain download..." >&2
        sleep $((attempt * 2))
    done

    echo "$cache_file"
}

# Normalise autotools timestamps for a freshly unpacked release tarball.
#
# Release tarballs ship a complete, self-consistent set of generated files
# (aclocal.m4, configure, config.h.in, Makefile.in, build-aux/ltmain.sh). If their
# mtimes are out of dependency order, make fires the autotools rebuild rules and
# demands the exact tool versions the tarball was built with - e.g.
# "automake-1.17: command not found".
#
# Rewriting the files instead (aclocal && automake && autoconf) is worse: it
# regenerates aclocal.m4 from the *host* libtool.m4 while leaving the tarball's
# ltmain.sh in place, so a host libtool newer than the tarball's fails with
# "libtool: Version mismatch error ... definition of this LT_INIT comes from".
#
# Stamping the files in dependency order keeps the tarball's own generated files
# and stops any rebuild rule from firing, so neither the host automake version
# nor the host libtool version can affect the build.
# Timestamp for stamping tier N, as a touch -t argument. Tiers are one minute
# apart: make compares mtimes at second granularity, so a whole minute between
# tiers leaves no room for two tiers to be seen as equal. The day itself is
# arbitrary - only the relative order matters - and is overridable for testing.
autotools_stamp_tier() {
    printf '%s%02d%02d' "${AUTOTOOLS_STAMP_DAY:-20200101}" "$(($1 / 60))" "$(($1 % 60))"
}

normalize_autotools_timestamps() {
    local dir="$1"
    [ -d "$dir" ] || return 0
    [ -f "$dir/configure" ] || return 0

    echo "Normalising autotools timestamps in ${dir}..."

    # Applied oldest -> newest. The ascending tier numbers are the contract: they
    # mirror the autotools dependency graph, so a new file class must be inserted
    # at the tier its dependencies require, not appended.
    #
    #   0 every input       the whole tree, see below
    #   1 aclocal.m4        depends on tier 0
    #   2 configure         depends on tiers 0-1
    #   3 config.h.in       depends on tiers 0-1, and must not predate configure
    #   4 Makefile.in       depends on tiers 0-1
    #
    # Tier 0 deliberately backdates the entire tree rather than an enumerated list of
    # source patterns. automake makes Makefile.in depend on every fragment that
    # Makefile.am includes, and those are not named predictably - xz's
    # src/liblzma/Makefile.am pulls in seven */Makefile.inc files, none of which match
    # a configure.ac/Makefile.am/*.m4 pattern. Missing one silently lets the rebuild
    # rule fire and reintroduces the host-automake dependency. Backdating everything
    # first means no input can outrank a generated file, whatever the package includes.
    #
    # touch -h stamps a symlink itself rather than its target. find does not follow
    # symlinks while walking, but touch dereferences by default, so without -h a
    # symlink pointing out of the tree would silently backdate a file elsewhere on the
    # system. None of the tarballs currently ship symlinks; -h keeps that from becoming
    # a problem if one ever does.
    find "$dir" -exec touch -h -t "$(autotools_stamp_tier 0)" {} +
    find "$dir" -name 'aclocal.m4' -exec touch -h -t "$(autotools_stamp_tier 1)" {} +
    find "$dir" -name 'configure'  -exec touch -h -t "$(autotools_stamp_tier 2)" {} +
    find "$dir" \( -name 'config.h.in' -o -name 'config.hin' -o -name '*.h.in' \) \
        -exec touch -h -t "$(autotools_stamp_tier 3)" {} +
    find "$dir" -name 'Makefile.in' -exec touch -h -t "$(autotools_stamp_tier 4)" {} +
}

# Function to download all libraries
# Always unpacks fresh copies from cache
download_all_libraries() {
    # Explicit "|| return 1": callers may run this where set -e does not apply
    # (e.g. after "&&"), and a failed download must never fall through to a build.
    download_library "$LIBARCHIVE_URL" "libarchive" "libarchive-${LIBARCHIVE_VERSION}" "$LIBARCHIVE_SHA256" || return 1
    download_library "$LZ4_URL" "lz4" "lz4-${LZ4_VERSION}" "$LZ4_SHA256" || return 1
    download_library "$ZSTD_URL" "zstd" "zstd-${ZSTD_VERSION}" "$ZSTD_SHA256" || return 1
    download_library "$LZOKAY_URL" "lzokay" "lzokay-${LZOKAY_COMMIT}" "$LZOKAY_SHA256" || return 1
    download_library "$LIBXML2_URL" "libxml2" "libxml2-${LIBXML2_VERSION}" "$LIBXML2_SHA256" || return 1
    download_library "$BZIP2_URL" "bzip2" "bzip2-${BZIP2_VERSION}" "$BZIP2_SHA256" || return 1
    download_library "$ZLIB_URL" "zlib" "zlib-${ZLIB_VERSION}" "$ZLIB_SHA256" || return 1
    download_library "$XZ_URL" "xz" "xz-${XZ_VERSION}" "$XZ_SHA256" || return 1

    # Stop autotools rebuild rules firing in any unpacked tarball. Without this the
    # build depends on the host's automake/libtool versions matching the ones each
    # tarball was released with, which breaks whenever a runner image updates them.
    # libxml2 is excluded: it is built via its own autogen.sh, which deliberately
    # runs a full, self-consistent autoreconf (libtoolize included).
    normalize_autotools_timestamps "libarchive-${LIBARCHIVE_VERSION}"
    normalize_autotools_timestamps "xz-${XZ_VERSION}"
}

# Print the files that the patch $1 changes, one per line, as patch -p1 finds them: the
# path in each of its "+++ b/<path>" lines, without any timestamp after a tab. Fails if
# the patch has no such line.
# Usage: libarchive_patch_files <patch>
libarchive_patch_files() {
    awk 'substr($0, 1, 6) == "+++ b/" {
            path = substr($0, 7)
            sub(/\t.*/, "", path)
            print path
            n++
        }
        END { exit (n == 0) }' "$1"
}

# Apply the patches in LIBARCHIVE_PATCHES, one at a time and in order, to libarchive
# sources that download_all_libraries has just unpacked: each applies to the sources as
# the ones before it leave them. Each must apply exactly: -F 0 allows no fuzz, so a hunk
# only applies where all of its context matches (an offset is fine), and -N refuses
# sources that already look patched. Every file that a patch changes, as its "+++ b/"
# lines name them, must then carry the patch's marker. Sources that any of the patches
# has already been applied to are refused before anything is changed. An error is one
# line that starts "ERROR: " and names the patch at fault, if there is one. Every build
# calls this, the Windows ones included, and so does scripts/check-native-updates.sh, on
# each libarchive release that it would propose (it reports that error line).
# Usage: patch_libarchive <directory of this file> <unpacked libarchive directory>
# Like the functions in build-lzo-shim.sh, it has a subshell body, stays POSIX (this file
# is sourced by build-windows.sh under plain sh) and ends every step in "|| exit".
patch_libarchive() (
    # LIBARCHIVE_PATCHES and the lists of files are split into words on purpose; stop the
    # shell from also expanding wildcards in them
    set -f

    if [ $# -ne 2 ]; then
        echo "ERROR: usage: patch_libarchive <native script directory> <libarchive directory>"
        exit 1
    fi
    if ! command -v patch >/dev/null 2>&1; then
        echo "ERROR: patch_libarchive needs the patch program"
        exit 1
    fi
    if [ ! -d "$2" ]; then
        echo "ERROR: $2 does not exist: unpack libarchive (download_all_libraries) first"
        exit 1
    fi

    # Check every patch before applying any. Sources that an earlier build left behind are
    # already patched; patch -N would refuse them too, but as if a patch no longer applied
    # to this libarchive release, and only once the patches before it had been applied
    count=0
    for entry in $LIBARCHIVE_PATCHES; do
        name=${entry%%:*}
        marker=${entry#*:}
        if [ -z "$name" ] || [ -z "$marker" ] || [ "$name" = "$entry" ]; then
            echo "ERROR: \"${entry}\" in LIBARCHIVE_PATCHES is not of the form <patch>:<marker>"
            exit 1
        fi
        if [ ! -f "$1/$name" ]; then
            echo "ERROR: ${name}, in LIBARCHIVE_PATCHES, is not in $1"
            exit 1
        fi
        if ! files=$(libarchive_patch_files "$1/$name"); then
            echo "ERROR: ${name} names no file to change (it has no \"+++ b/\" line)"
            exit 1
        fi
        for file in $files; do
            if grep -qF -e "$marker" "$2/$file" 2>/dev/null; then
                echo "ERROR: $2 is already patched ($file has ${marker}, the marker of ${name}): unpack it afresh (download_all_libraries) first"
                exit 1
            fi
        done
        count=$((count + 1))
    done
    if [ "$count" -eq 0 ]; then
        echo "ERROR: LIBARCHIVE_PATCHES lists no patch"
        exit 1
    fi

    for entry in $LIBARCHIVE_PATCHES; do
        name=${entry%%:*}
        marker=${entry#*:}
        echo "Applying ${name} to $2..."
        if ! patch -p1 -N -F 0 -d "$2" < "$1/$name"; then
            echo "ERROR: ${name} does not apply to libarchive ${LIBARCHIVE_VERSION}: rework it for this release (see \"The libarchive patches\" in native/BUILD.md)"
            exit 1
        fi
        files=$(libarchive_patch_files "$1/$name") || exit
        for file in $files; do
            if ! grep -qF -e "$marker" "$2/$file"; then
                echo "ERROR: $2/$file lacks the changes of ${name} (marker ${marker})"
                exit 1
            fi
        done
        echo "${name} applied"
    done
)

# Detect number of CPU cores
if command -v nproc >/dev/null 2>&1; then
    NCPU=$(nproc)
elif command -v sysctl >/dev/null 2>&1; then
    NCPU=$(sysctl -n hw.ncpu)
else
    NCPU=4
fi
export NCPU

echo "Build configuration loaded:"
echo "  libarchive: ${LIBARCHIVE_VERSION}"
echo "  lz4: ${LZ4_VERSION}"
echo "  zstd: ${ZSTD_VERSION}"
echo "  lzokay: ${LZOKAY_COMMIT}"
echo "  libxml2: ${LIBXML2_VERSION}"
echo "  zlib: ${ZLIB_VERSION}"
echo "  xz: ${XZ_VERSION}"
echo "  bzip2: ${BZIP2_VERSION}"
echo "  Bootlin toolchains (Linux): ${BOOTLIN_RELEASE}"
echo "  CPUs: ${NCPU}"
echo "  PREFIX: ${PREFIX}"
echo "  Download cache: ${DOWNLOAD_CACHE}"
echo "  Configure cache: ${CONFIG_CACHE_DIR}"
