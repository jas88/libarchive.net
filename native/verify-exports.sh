#!/bin/bash
# Fail unless each Linux libarchive.so named exports libarchive's API and nothing else:
# - every symbol that it defines for other code to bind to (its defined dynamic symbols) is
#   one of libarchive's archive_* symbols, as the version script linux-exports.map allows;
# - it exports every function that LibArchive.Net calls in it: the entry point of each
#   [LibraryImport("archive")] and [DllImport("archive", ...)] in LibArchive.Net/*.cs, which
#   is the EntryPoint the attribute names, or else the name of the method that it declares,
#   apart from those in WINDOWS_ONLY below.
# build-linux.sh runs it on the library that it links, and the package job of
# .github/workflows/build.yml on the six that it packs.
# Usage: verify-exports.sh <library>...
# READELF names GNU readelf (default: readelf), which reads the files of every target alike.

set -u

if [ $# -eq 0 ]; then
    echo "Usage: $0 <library>..." >&2
    exit 2
fi
readelf="${READELF:-readelf}"
sources="$(cd "$(dirname "$0")/.." && pwd)/LibArchive.Net"
# The functions that LibArchive.Net imports but calls on Windows only, and that libarchive
# declares and exports on Windows only (#if defined(_WIN32) && !defined(__CYGWIN__) in
# archive.h), so that no Linux library has them
WINDOWS_ONLY="archive_read_open_filenames_w"

shopt -s nullglob
files=("$sources"/*.cs)
if [ ${#files[@]} -eq 0 ]; then
    echo "ERROR: ${sources} has no C# sources (*.cs), from which to read what LibArchive.Net imports"
    exit 1
fi

# The entry points that LibArchive.Net imports from "archive", one per line, from the
# attributes of its P/Invoke declarations. Without an EntryPoint, the declaration is the
# first line after the attribute that is not another attribute, a comment or blank, and the
# method's name is the first name in it that is followed by "(".
imports=$(awk '
    /^[ \t]*\[(LibraryImport|DllImport)\("archive"[,)]/ {
        if (match($0, /EntryPoint *= *"[^"]*"/)) {
            name = substr($0, RSTART, RLENGTH)
            sub(/^EntryPoint *= *"/, "", name)
            sub(/"$/, "", name)
            print name
            pending = 0
        } else {
            pending = 1
            where = FILENAME ":" FNR
        }
        next
    }
    pending && /^[ \t]*(\[|\/\/|$)/ { next }
    pending {
        if (match($0, /[A-Za-z_][A-Za-z0-9_]*[ \t]*\(/)) {
            name = substr($0, RSTART, RLENGTH)
            sub(/[ \t]*\($/, "", name)
            print name
        } else {
            print "(no method declared after " where ")"
        }
        pending = 0
    }' "${files[@]}" | sort -u)
count=$(awk 'NF {n++} END {print n + 0}' <<< "$imports")
if [ "$count" -eq 0 ]; then
    echo "ERROR: no function that LibArchive.Net imports from \"archive\" was found in ${sources}/*.cs"
    exit 1
fi
others=$(awk '!/^archive_[A-Za-z0-9_]+$/ {print "  " $0}' <<< "$imports")
if [ -n "$others" ]; then
    echo "ERROR: these entry points, read from the [LibraryImport(\"archive\")] and [DllImport(\"archive\", ...)]"
    echo "attributes in ${sources}/*.cs, are not libarchive functions (archive_*):"
    echo "$others"
    exit 1
fi
# The imports that the Linux libraries must export: all but WINDOWS_ONLY, each of which must
# still be one, so that the list cannot outlive the code that needs it
stale=$(awk -v list="$WINDOWS_ONLY" 'BEGIN { n = split(list, names, " "); for (i = 1; i <= n; i++) windows[names[i]] = 1 }
    { imported[$0] = 1 } END { for (name in windows) if (!(name in imported)) print "  " name }' <<< "$imports")
if [ -n "$stale" ]; then
    echo "ERROR: these functions are in WINDOWS_ONLY in $0, but LibArchive.Net does not import them:"
    echo "$stale"
    exit 1
fi
required=$(awk -v list="$WINDOWS_ONLY" 'BEGIN { n = split(list, names, " "); for (i = 1; i <= n; i++) windows[names[i]] = 1 }
    NF && !($0 in windows)' <<< "$imports")
needed=$(awk 'NF {n++} END {print n + 0}' <<< "$required")

failed=0
for lib in "$@"; do
    echo "=== Verifying the exports of ${lib} ==="
    if ! symbols=$("$readelf" -W --dyn-syms "$lib"); then
        echo "ERROR: could not read the dynamic symbols of ${lib} with ${readelf}"
        failed=1
        continue
    fi
    # Each defined global or weak dynamic symbol, without the "@<version>" of a versioned one.
    # The name is the last field, or the one before it when readelf ends the line with the
    # version's index, "(2)"; the section index (UND for an undefined symbol) comes before it.
    exports=$(awk '$1 ~ /^[0-9]+:$/ && ($5 == "GLOBAL" || $5 == "WEAK" || $5 == "UNIQUE") {
            name = $NF; ndx = $(NF - 1)
            if (name ~ /^\([0-9]+\)$/) { name = $(NF - 1); ndx = $(NF - 2) }
            if (ndx != "UND") { sub(/@.*/, "", name); print name }
        }' <<< "$symbols" | sort -u)
    total=$(awk 'NF {n++} END {print n + 0}' <<< "$exports")
    foreign=$(awk '!/^archive_/ {print "  " $0}' <<< "$exports")
    missing=$(awk 'NR == FNR { exported[$0] = 1; next } !($0 in exported) { print "  " $0 }' \
        <(printf '%s\n' "$exports") - <<< "$required")
    if [ -n "$foreign" ]; then
        echo "ERROR: ${lib} exports these symbols, which are not libarchive's (archive_*):"
        echo "$foreign"
        failed=1
    fi
    if [ -n "$missing" ]; then
        echo "ERROR: ${lib} does not export these functions, which LibArchive.Net imports:"
        echo "$missing"
        failed=1
    fi
    if [ -z "$foreign" ] && [ -z "$missing" ]; then
        echo "${total} exported symbols, all archive_*, among them the ${needed} of the ${count} functions that LibArchive.Net imports that are not Windows-only (${WINDOWS_ONLY})"
    fi
done
exit "$failed"
