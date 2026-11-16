#!/bin/bash
# Write a NuGet config for testing a freshly packed LibArchive.Net package.
#
# Usage: write-package-test-nuget-config.sh <local-package-dir> <output-dir>
#
# Creates <output-dir>/nuget.config and an empty <output-dir>/packages global
# packages folder. Package source mapping restricts LibArchive.Net to
# <local-package-dir>, so the tests can only resolve the package just built: never a
# copy already in a global packages folder, and never the same version published on
# nuget.org (e.g. when a tag build is re-run after release). All other packages
# come from nuget.org.

set -euo pipefail

if [ $# -ne 2 ]; then
    echo "Usage: $0 <local-package-dir> <output-dir>" >&2
    exit 2
fi

# Native absolute path (Windows-style under Git Bash, where dotnet needs it)
abs_dir() {
    (cd "$1" && { pwd -W 2>/dev/null || pwd; })
}

# Under Git Bash, arguments may be native Windows paths (e.g. $RUNNER_TEMP is
# D:\a\_temp); convert them to POSIX form for the shell commands below.
if command -v cygpath >/dev/null 2>&1; then
    set -- "$(cygpath -u "$1")" "$(cygpath -u "$2")"
fi

if [ ! -d "$1" ]; then
    echo "ERROR: local package directory '$1' does not exist" >&2
    exit 1
fi
if ! ls "$1"/LibArchive.Net.*.nupkg >/dev/null 2>&1; then
    echo "ERROR: no LibArchive.Net.*.nupkg found in '$1'" >&2
    exit 1
fi

LOCAL_SOURCE=$(abs_dir "$1")
mkdir -p "$2"
OUTPUT_DIR=$(abs_dir "$2")
PACKAGES_DIR="$OUTPUT_DIR/packages"
rm -rf "$2/packages"
mkdir -p "$2/packages"

cat > "$2/nuget.config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="${PACKAGES_DIR}" />
  </config>
  <packageSources>
    <clear />
    <add key="local-build" value="${LOCAL_SOURCE}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="local-build">
      <package pattern="LibArchive.Net" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
EOF

echo "Wrote $2/nuget.config (LibArchive.Net from ${LOCAL_SOURCE} only; packages in ${PACKAGES_DIR})"
