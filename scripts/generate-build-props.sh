#!/bin/bash
# Generate directory-specific Directory.Build.props files with dynamic target framework values
# based on .NET SDK version. Locally, out-of-date files are rewritten for you to commit. In CI they are
# never committed or pushed (a pull request's checkout is a detached HEAD with a read-only token):
# the step shows the required change and fails, so the update goes into the pull request itself.

set -e

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
cd "$REPO_ROOT"

# Create a temporary project to query SDK properties
TEMP_DIR=$(mktemp -d)
TEMP_PROJ="$TEMP_DIR/temp.csproj"

cat > "$TEMP_PROJ" << 'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
</Project>
EOF

# Get NETCoreAppMaximumVersion from SDK
MAX_VERSION=$(dotnet msbuild "$TEMP_PROJ" -getProperty:NETCoreAppMaximumVersion 2>/dev/null | tail -1 | tr -d ' ')

# Clean up temp project
rm -rf "$TEMP_DIR"

# Extract major version (e.g., "10.0" -> "10")
MAX_MAJOR=$(echo "$MAX_VERSION" | cut -d. -f1)

echo "Detected .NET SDK maximum version: $MAX_VERSION (major: $MAX_MAJOR)"

# Determine the oldest .NET major version to target from the SDK's major version. The library targets
# netstandard2.0 plus net${MIN_MAJOR}.0 through net${MAX_MAJOR}.0: every .NET version still in support
# when that SDK major version ships. Majors ship each November, and a version whose support ends in the
# same November counts as end-of-life. LTS (even) versions get 3 years of support and STS (odd) ones 2
# years (since .NET 9), so .NET 8 and 9 end Nov 2026, .NET 10 and 11 Nov 2028, .NET 12 and 13 Nov 2030,
# and the oldest target is the last LTS version before the SDK's: the .NET 11 SDK gives net10.0;net11.0
# and the .NET 13 SDK net12.0;net13.0. An SDK missing from this table targets only its own version.
if [ "$MAX_MAJOR" -eq 9 ] || [ "$MAX_MAJOR" -eq 10 ]; then
    MIN_MAJOR=8
elif [ "$MAX_MAJOR" -eq 11 ] || [ "$MAX_MAJOR" -eq 12 ]; then
    MIN_MAJOR=10
elif [ "$MAX_MAJOR" -eq 13 ] || [ "$MAX_MAJOR" -eq 14 ]; then
    MIN_MAJOR=12
else
    # Fallback for unknown versions
    MIN_MAJOR=$MAX_MAJOR
fi

# Build list of supported frameworks (netstandard2.0 first for broad compatibility)
FRAMEWORKS="netstandard2.0"
for v in $(seq "$MIN_MAJOR" "$MAX_MAJOR"); do
    FRAMEWORKS="${FRAMEWORKS};net${v}.0"
done

echo "Target frameworks for library: $FRAMEWORKS"
echo "Target frameworks for tests: net${MAX_MAJOR}.0;net462"

CHANGES_MADE=false

# Generate LibArchive.Net/Directory.Build.props for library (multi-targeting)
LIB_PROPS="LibArchive.Net/Directory.Build.props"
TEMP_LIB=$(mktemp)
cat > "$TEMP_LIB" << EOF
<Project>
  <!-- Import parent props -->
  <Import Project="\$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '\$(MSBuildThisFileDirectory)../'))" />

  <!-- Library multi-targets netstandard2.0 plus all non-EOL .NET versions -->
  <!-- Auto-generated based on SDK version by scripts/generate-build-props.sh -->
  <PropertyGroup>
    <TargetFrameworks>$FRAMEWORKS</TargetFrameworks>
  </PropertyGroup>
</Project>
EOF

if ! diff -q "$LIB_PROPS" "$TEMP_LIB" > /dev/null 2>&1; then
    echo "$LIB_PROPS needs updating for current .NET SDK version"
    mv "$TEMP_LIB" "$LIB_PROPS"
    CHANGES_MADE=true
else
    rm -f "$TEMP_LIB"
fi

# Generate Test.LibArchive.Net/Directory.Build.props for tests (latest .NET + net462)
TEST_PROPS="Test.LibArchive.Net/Directory.Build.props"
TEMP_TEST=$(mktemp)
cat > "$TEMP_TEST" << EOF
<Project>
  <!-- Import parent props -->
  <Import Project="\$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '\$(MSBuildThisFileDirectory)../'))" />

  <!-- Test projects target latest .NET plus net462 for Mono testing -->
  <!-- Auto-generated based on SDK version by scripts/generate-build-props.sh -->
  <PropertyGroup>
    <TargetFrameworks>net${MAX_MAJOR}.0;net462</TargetFrameworks>
  </PropertyGroup>
</Project>
EOF

if ! diff -q "$TEST_PROPS" "$TEMP_TEST" > /dev/null 2>&1; then
    echo "$TEST_PROPS needs updating for current .NET SDK version"
    mv "$TEMP_TEST" "$TEST_PROPS"
    CHANGES_MADE=true
else
    rm -f "$TEMP_TEST"
fi

# If changes were made in CI, show what is needed and fail; locally, leave the rewritten files to commit
if [ "$CHANGES_MADE" = true ]; then
    if [ -n "$CI" ]; then
        git --no-pager diff -- "$LIB_PROPS" "$TEST_PROPS"
        for f in "$LIB_PROPS" "$TEST_PROPS"; do
            if ! git diff --quiet -- "$f"; then
                echo "::error file=$f::$f does not match the .NET $MAX_VERSION SDK. Run ./scripts/generate-build-props.sh locally and commit the result."
            fi
        done
        echo "ERROR: Directory.Build.props files are out of date for the .NET $MAX_VERSION SDK (diff above)."
        echo "Run ./scripts/generate-build-props.sh locally and commit the changes."
        exit 1
    else
        echo "Updated props files locally. Please commit the changes."
        exit 0
    fi
else
    echo "All Directory.Build.props files are up to date"
    exit 0
fi
