#!/bin/sh
# Unified build script for all platforms
# This script orchestrates building libarchive for all supported platforms

set -e

# Absolute, because the builds run after a cd into a temporary directory
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
# Where the Linux and macOS scripts leave their libraries (OUTPUT_DIR in each)
NATIVE_OUT="${HOME}/libarchive-native"

# Color output helpers. printf, not echo: bash's echo prints the escapes literally. The
# codes go through %b, which expands their escapes, and the message through %s, so that
# it is printed as given (a % or a backslash in it is not interpreted).
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() {
    printf '%b[INFO]%b %s\n' "$GREEN" "$NC" "$1"
}

log_warn() {
    printf '%b[WARN]%b %s\n' "$YELLOW" "$NC" "$1"
}

log_error() {
    printf '%b[ERROR]%b %s\n' "$RED" "$NC" "$1"
}

# Detect current platform
detect_platform() {
    case "$(uname -s)" in
        Linux*)     echo "linux" ;;
        Darwin*)    echo "macos" ;;
        CYGWIN*|MINGW*|MSYS*) echo "windows" ;;
        *)          echo "unknown" ;;
    esac
}

PLATFORM=$(detect_platform)
log_info "Detected platform: $PLATFORM"

# Parse command line arguments
BUILD_LINUX=0
BUILD_MACOS=0
BUILD_WINDOWS=0
PACKAGE=0

if [ $# -eq 0 ]; then
    # No arguments: build for current platform only
    case "$PLATFORM" in
        linux)   BUILD_LINUX=1 ;;
        macos)   BUILD_MACOS=1 ;;
        windows) BUILD_WINDOWS=1 ;;
    esac
else
    # Parse arguments
    for arg in "$@"; do
        case "$arg" in
            --all)      BUILD_LINUX=1; BUILD_MACOS=1; BUILD_WINDOWS=1; PACKAGE=1 ;;
            --linux)    BUILD_LINUX=1 ;;
            --macos)    BUILD_MACOS=1 ;;
            --windows)  BUILD_WINDOWS=1 ;;
            --package)  PACKAGE=1 ;;
            --help|-h)
                echo "Usage: $0 [options]"
                echo ""
                echo "Options:"
                echo "  --all       Build for all platforms and package"
                echo "  --linux     Build for Linux (x64, ARMv7, ARM64; glibc and musl)"
                echo "  --macos     Build for macOS (universal binary)"
                echo "  --windows   Build for Windows (x86, x64, arm64)"
                echo "  --package   Create and test NuGet package after building"
                echo "  --help      Show this help message"
                echo ""
                echo "If no options are specified, builds for current platform only."
                echo "PACKAGE_VERSION sets the version --package uses (default: 0.0.0-dev)."
                exit 0
                ;;
            *)
                log_error "Unknown option: $arg"
                echo "Use --help for usage information"
                exit 1
                ;;
        esac
    done
fi

# Create temporary build directory
BUILD_DIR="$(mktemp -d)"
trap 'rm -rf "$BUILD_DIR"' EXIT
cd "$BUILD_DIR"
log_info "Building in: $BUILD_DIR"

# Copy native directory for builds (a real copy: hard links cannot cross file systems)
cp -R "$SCRIPT_DIR"/* .

# Build for Linux
if [ $BUILD_LINUX -eq 1 ]; then
    log_info "Building for Linux (x64, ARMv7, ARM64; glibc and musl)..."
    if [ "$PLATFORM" != "linux" ] && [ "$PLATFORM" != "macos" ]; then
        log_warn "Building for Linux is not supported on $PLATFORM, only on Linux and macOS"
        log_warn "Skipping Linux build"
    else
        # build-linux.sh <rid> builds the library of one runtime, linked against its own
        # C library (glibc's linux-<arch>, musl's linux-musl-<arch>), and leaves
        # libarchive-<rid>.so in $NATIVE_OUT. It builds in a directory of its own, so it
        # runs from this checkout. No x86: LibArchive.Net never loads a Linux x86 library.
        for rid in linux-x64 linux-arm64 linux-arm linux-musl-x64 linux-musl-arm64 linux-musl-arm; do
            "$SCRIPT_DIR/build-linux.sh" "$rid"
            mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/$rid/native"
            cp "$NATIVE_OUT/libarchive-$rid.so" "$REPO_ROOT/LibArchive.Net/runtimes/$rid/native/libarchive.so"
        done
        log_info "Linux libraries installed to LibArchive.Net/runtimes/linux-<arch>/native/ and linux-musl-<arch>/native/"
    fi
fi

# Build for macOS
if [ $BUILD_MACOS -eq 1 ]; then
    log_info "Building for macOS (universal: x86_64 + arm64)..."
    if [ "$PLATFORM" != "macos" ]; then
        log_warn "Cross-compilation for macOS not supported on $PLATFORM"
        log_warn "Skipping macOS build"
    else
        ./build-macos.sh
        # Universal binary supports both x64 and arm64, so copy to both RID folders
        mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/osx-x64/native"
        mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/osx-arm64/native"
        cp "$NATIVE_OUT/libarchive.dylib" "$REPO_ROOT/LibArchive.Net/runtimes/osx-x64/native/"
        cp "$NATIVE_OUT/libarchive.dylib" "$REPO_ROOT/LibArchive.Net/runtimes/osx-arm64/native/"
        log_info "macOS library installed to LibArchive.Net/runtimes/osx-x64/native/ and osx-arm64/native/"
    fi
fi

# Build for Windows
if [ $BUILD_WINDOWS -eq 1 ]; then
    log_info "Building for Windows (x86, x64, arm64)..."
    if [ "$PLATFORM" != "linux" ]; then
        log_warn "Windows builds require Linux with MinGW cross-compiler"
        log_warn "Skipping Windows builds"
    else
        # Check for MinGW
        if ! command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
            log_error "MinGW cross-compiler not found"
            log_error "Install with: sudo apt-get install mingw-w64"
            exit 1
        fi

        # Build x64
        log_info "Building Windows x64..."
        ARCH=x86_64 ./build-windows.sh
        mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/win-x64/native"
        cp archive-x64.dll "$REPO_ROOT/LibArchive.Net/runtimes/win-x64/native/archive.dll"

        # Build x86
        log_info "Building Windows x86..."
        rm -rf local-* bzip2-* libarchive-* libxml2-* lz4-* lzokay-* xz-* zlib-* zstd-*
        ARCH=i686 ./build-windows.sh
        mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/win-x86/native"
        cp archive-x86.dll "$REPO_ROOT/LibArchive.Net/runtimes/win-x86/native/archive.dll"

        # Build arm64
        log_info "Building Windows arm64..."
        rm -rf local-* bzip2-* libarchive-* libxml2-* lz4-* lzokay-* xz-* zlib-* zstd-*
        ARCH=aarch64 ./build-windows.sh
        mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/win-arm64/native"
        cp archive-arm64.dll "$REPO_ROOT/LibArchive.Net/runtimes/win-arm64/native/archive.dll"

        log_info "Windows libraries installed to LibArchive.Net/runtimes/win-*/native/"
    fi
fi

# Create the NuGet package and test it, as CI does (the package and test-net462 jobs in
# .github/workflows/build.yml): the tests run against the package, not the project
if [ $PACKAGE -eq 1 ]; then
    log_info "Creating NuGet package..."
    cd "$REPO_ROOT"

    # Determine version. Not from the command line: its arguments are all options.
    VERSION="${PACKAGE_VERSION:-0.0.0-dev}"
    # Packed into an empty directory of its own, so that it holds this package only
    PACKAGE_DIR="$BUILD_DIR/package"
    NUGET_TEST_DIR="$BUILD_DIR/package-test"

    # MinVer sets the version from the git tags, over any PackageVersion given, so the
    # version goes in as MinVer's own override (which must be a SemVer 2.0 version)
    log_info "Creating package version $VERSION..."
    dotnet pack LibArchive.Net/LibArchive.Net.csproj -o "$PACKAGE_DIR" "-p:MinVerVersionOverride=$VERSION" --nologo --configuration Release

    # The tests ask for the version as packed, read from the file name: it can differ
    # from $VERSION (build metadata, after a +, is not part of a package version)
    PACKAGE_FILE=""
    for f in "$PACKAGE_DIR"/LibArchive.Net.*.nupkg; do
        [ -f "$f" ] || continue
        if [ -n "$PACKAGE_FILE" ]; then
            log_error "dotnet pack created more than one package in $PACKAGE_DIR"
            exit 1
        fi
        PACKAGE_FILE="$f"
    done
    if [ -z "$PACKAGE_FILE" ]; then
        log_error "dotnet pack created no LibArchive.Net package in $PACKAGE_DIR"
        exit 1
    fi
    PACKAGE_NAME="${PACKAGE_FILE##*/}"
    PACKED_VERSION="${PACKAGE_NAME#LibArchive.Net.}"
    PACKED_VERSION="${PACKED_VERSION%.nupkg}"

    log_info "Testing package version $PACKED_VERSION..."
    # LibArchive.Net may only come from $PACKAGE_DIR (not a cache or nuget.org); see the script
    ./scripts/write-package-test-nuget-config.sh "$PACKAGE_DIR" "$NUGET_TEST_DIR"
    dotnet restore Test.LibArchive.Net/Test.LibArchive.Net.csproj --configfile "$NUGET_TEST_DIR/nuget.config" -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$PACKED_VERSION"

    # The modern .NET test target, from the project (scripts/generate-build-props.sh sets it for the SDK)
    if ! TEST_TFMS=$(dotnet msbuild Test.LibArchive.Net/Test.LibArchive.Net.csproj -getProperty:TargetFrameworks \
        -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$PACKED_VERSION"); then
        printf '%s\n' "$TEST_TFMS"
        log_error "Could not read the TargetFrameworks of Test.LibArchive.Net/Test.LibArchive.Net.csproj"
        exit 1
    fi
    TEST_TFMS=$(printf '%s\n' "$TEST_TFMS" | tail -1 | tr -d ' \r')
    TEST_TFM=$(printf '%s\n' "$TEST_TFMS" | tr ';' '\n' | grep -E '^net[0-9]+\.[0-9]+$' | head -1)
    if [ -z "$TEST_TFM" ]; then
        log_error "No modern .NET target framework found in test project TargetFrameworks '$TEST_TFMS'"
        exit 1
    fi
    log_info "Testing modern target framework: $TEST_TFM (from '$TEST_TFMS')"
    dotnet build Test.LibArchive.Net/Test.LibArchive.Net.csproj --framework "$TEST_TFM" --configuration Release --no-restore --nologo -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$PACKED_VERSION"
    dotnet test Test.LibArchive.Net/Test.LibArchive.Net.csproj --framework "$TEST_TFM" --configuration Release --no-build --nologo -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$PACKED_VERSION"

    # The SDK runs .NET Framework tests on Windows only
    if [ "$PLATFORM" = "windows" ]; then
        log_info "Testing .NET Framework 4.6.2 (net462)..."
        dotnet test Test.LibArchive.Net/Test.LibArchive.Net.csproj --framework net462 --configuration Release --no-restore --nologo -p:UseLocalPackage=true "-p:LibArchiveNetVersion=$PACKED_VERSION"
    else
        log_info "Skipping the .NET Framework 4.6.2 (net462) tests, which run on Windows only"
    fi

    # Only a package that has passed the tests is put in the repository root
    cp "$PACKAGE_FILE" .
    log_info "Package created:"
    ls -lh "$PACKAGE_NAME"
fi

log_info "Build complete!"
