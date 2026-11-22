#!/bin/bash
# Fail unless each Linux library in a runtimes folder is the one built for its runtime:
# - the six Linux runtimes each have runtimes/<rid>/native/libarchive.so, and no other
#   runtimes/linux-*/native/libarchive.so is there;
# - its ELF class and machine are those of the runtime's architecture: ELF64 and X86-64 for
#   x64, ELF64 and AArch64 for arm64, ELF32 and ARM for arm;
# - the libraries that it needs (DT_NEEDED) are musl's libc.so alone for linux-musl-*, and
#   include glibc's libc.so.6, but not musl's libc.so, for the other linux-* runtimes.
# Nothing else would tell: in a musl process, musl's dynamic linker loads the glibc linux-x64
# library as well, since it takes libc.so.6 and libpthread.so.0 to be itself and provides
# every name that the library imports, so the tests on Alpine x64 pass with either library.
# The package job of .github/workflows/build.yml runs it on the runtimes that it assembles.
# Usage: verify-linux-runtimes.sh <runtimes folder>
# READELF names GNU readelf (default: readelf), which reads the files of every target alike.

set -u

if [ $# -ne 1 ]; then
    echo "Usage: $0 <runtimes folder>" >&2
    exit 2
fi
runtimes="${1%/}"
readelf="${READELF:-readelf}"

# Each Linux runtime, with the ELF class and machine of its architecture, as readelf -h names
# them, and the C library that it is linked against
RUNTIMES="linux-x64:ELF64:Advanced Micro Devices X86-64:glibc
linux-arm64:ELF64:AArch64:glibc
linux-arm:ELF32:ARM:glibc
linux-musl-x64:ELF64:Advanced Micro Devices X86-64:musl
linux-musl-arm64:ELF64:AArch64:musl
linux-musl-arm:ELF32:ARM:musl"

# The value of a field of an ELF header that readelf -h prints, such as "Class:  ELF64",
# without the padding
# Usage: elf_field <field> <readelf -h output>
elf_field() {
    awk -v name="$1" '{
        line = $0; sub(/^[ \t]+/, "", line)
        if (index(line, name ":") == 1) {
            value = substr(line, length(name) + 2); gsub(/^[ \t]+|[ \t]+$/, "", value)
            print value; exit
        }
    }' <<< "$2"
}

failed=0

# A library in the folder of a runtime that the list above does not have would go unchecked
shopt -s nullglob
for lib in "$runtimes"/linux-*/native/libarchive.so; do
    rid=$(basename "$(dirname "$(dirname "$lib")")")
    if ! awk -F: -v rid="$rid" '$1 == rid {found = 1} END {exit !found}' <<< "$RUNTIMES"; then
        echo "ERROR: ${lib} is in the folder of ${rid}, which is not one of the Linux runtimes that $0 checks"
        failed=1
    fi
done

while IFS=: read -r rid class machine libc; do
    lib="${runtimes}/${rid}/native/libarchive.so"
    echo "=== Verifying that ${lib} is built for ${rid} ==="
    if [ ! -f "$lib" ]; then
        echo "ERROR: ${lib} does not exist"
        failed=1
        continue
    fi
    if ! header=$("$readelf" -W -h "$lib") || ! dynamic=$("$readelf" -W -d "$lib"); then
        echo "ERROR: could not read ${lib} with ${readelf}"
        failed=1
        continue
    fi
    lib_class=$(elf_field Class "$header")
    lib_machine=$(elf_field Machine "$header")
    # The libraries that it needs, one per line, from readelf's "Shared library: [libc.so.6]"
    needed=$(awk '/\(NEEDED\)/ {name = $NF; gsub(/^\[|\]$/, "", name); print name}' <<< "$dynamic")
    needed_list=$(awk 'NF {printf "%s%s", sep, $0; sep = " "}' <<< "$needed")

    errors=0
    if [ "$lib_class" != "$class" ] || [ "$lib_machine" != "$machine" ]; then
        echo "ERROR: ${lib} is ${lib_class:-(no class)} ${lib_machine:-(no machine)}, where ${rid} needs ${class} ${machine}"
        errors=1
    fi
    if [ "$libc" = musl ]; then
        if [ "$needed" != "libc.so" ]; then
            echo "ERROR: ${lib} must need musl's libc.so alone (DT_NEEDED), as the library of ${rid}, but it needs: ${needed_list:-(nothing)}"
            errors=1
        fi
    else
        if ! awk '$0 == "libc.so.6" {found = 1} END {exit !found}' <<< "$needed" ||
            awk '$0 == "libc.so" {found = 1} END {exit !found}' <<< "$needed"; then
            echo "ERROR: ${lib} must need glibc's libc.so.6, and not musl's libc.so (DT_NEEDED), as the library of ${rid}, but it needs: ${needed_list:-(nothing)}"
            errors=1
        fi
    fi
    if [ "$errors" -ne 0 ]; then
        failed=1
    else
        echo "${lib_class} ${lib_machine}, linked against ${libc}: needs ${needed_list}"
    fi
done <<< "$RUNTIMES"
exit "$failed"
