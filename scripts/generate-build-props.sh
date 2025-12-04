#!/bin/bash
# Generate directory-specific Directory.Build.props files with dynamic target framework values
# based on .NET SDK version. If any files differ from what's in git, commit and push, then exit with error.

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

# Determine minimum supported major version based on SDK version
# .NET 8 LTS until Nov 2026, .NET 10 LTS until Nov 2028
# We support: current LTS (8) through current SDK version
if [ "$MAX_MAJOR" -eq 9 ] || [ "$MAX_MAJOR" -eq 10 ]; then
    MIN_MAJOR=8
elif [ "$MAX_MAJOR" -eq 11 ] || [ "$MAX_MAJOR" -eq 12 ]; then
    MIN_MAJOR=10
elif [ "$MAX_MAJOR" -eq 13 ]; then
    MIN_MAJOR=11
else
    # Fallback for unknown versions
    MIN_MAJOR=$MAX_MAJOR
fi

# Build list of supported frameworks (netstandard2.0 first for broad compatibility)
FRAMEWORKS="netstandard2.0"
for v in $(seq $MIN_MAJOR $MAX_MAJOR); do
    FRAMEWORKS="${FRAMEWORKS};net${v}.0"
done

echo "Target frameworks for library: $FRAMEWORKS"
echo "Target framework for tests: net${MAX_MAJOR}.0"

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

# Generate Test.LibArchive.Net/Directory.Build.props for tests (single target = latest)
TEST_PROPS="Test.LibArchive.Net/Directory.Build.props"
TEMP_TEST=$(mktemp)
cat > "$TEMP_TEST" << EOF
<Project>
  <!-- Import parent props -->
  <Import Project="\$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '\$(MSBuildThisFileDirectory)../'))" />

  <!-- Test projects target only the latest .NET version -->
  <!-- Auto-generated based on SDK version by scripts/generate-build-props.sh -->
  <PropertyGroup>
    <TargetFramework>net${MAX_MAJOR}.0</TargetFramework>
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

# If changes were made and we're in CI, commit and push
if [ "$CHANGES_MADE" = true ]; then
    if [ -d .git ] && [ -n "$CI" ]; then
        git config user.name "github-actions[bot]"
        git config user.email "github-actions[bot]@users.noreply.github.com"
        git add "$LIB_PROPS" "$TEST_PROPS"
        git commit -m "Update Directory.Build.props files for .NET SDK version"
        git push
        echo "ERROR: Directory.Build.props files were out of date and have been updated."
        echo "The changes have been committed and pushed. Please retry the workflow."
        exit 1
    else
        echo "Updated props files locally. Please commit the changes."
        exit 0
    fi
else
    echo "All Directory.Build.props files are up to date"
    exit 0
fi
