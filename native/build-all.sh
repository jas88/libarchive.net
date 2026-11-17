#!/bin/sh
# Unified build script for all platforms
# This script orchestrates building libarchive for all supported platforms

set -e

# Absolute, because the builds run after a cd into a temporary directory
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
# Where the Linux and macOS scripts leave their libraries (OUTPUT_DIR in each)
NATIVE_OUT="${HOME}/libarchive-native"

# Color output helpers
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

log_info() {
    echo "${GREEN}[INFO]${NC} $1"
}

log_warn() {
    echo "${YELLOW}[WARN]${NC} $1"
}

log_error() {
    echo "${RED}[ERROR]${NC} $1"
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
                echo "  --linux     Build for Linux (x64, ARMv7, ARM64)"
                echo "  --macos     Build for macOS (universal binary)"
                echo "  --windows   Build for Windows (x86, x64, arm64)"
                echo "  --package   Create NuGet package after building"
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
    log_info "Building for Linux (x64, ARMv7, ARM64)..."
    if [ "$PLATFORM" != "linux" ]; then
        log_warn "Cross-compilation for Linux not yet supported on $PLATFORM"
        log_warn "Skipping Linux build"
    else
        # build-linux.sh builds x64, build-linux-<arch>.sh the others; each leaves
        # libarchive-linux-<arch>.so in $NATIVE_OUT. The libraries use musl-libc, so
        # each one goes to both its linux-<arch> and its linux-musl-<arch> folder.
        # No x86: LibArchive.Net never loads a Linux x86 library.
        for arch in x64 arm arm64; do
            if [ "$arch" = x64 ]; then
                ./build-linux.sh
            else
                "./build-linux-$arch.sh"
            fi
            for rid in "linux-$arch" "linux-musl-$arch"; do
                mkdir -p "$REPO_ROOT/LibArchive.Net/runtimes/$rid/native"
                cp "$NATIVE_OUT/libarchive-linux-$arch.so" "$REPO_ROOT/LibArchive.Net/runtimes/$rid/native/libarchive.so"
            done
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

# Create NuGet package
if [ $PACKAGE -eq 1 ]; then
    log_info "Creating NuGet package..."
    cd "$REPO_ROOT"

    # Determine version. Not from the command line: its arguments are all options.
    VERSION="${PACKAGE_VERSION:-0.0.0-dev}"

    log_info "Building .NET project..."
    dotnet build --configuration Release --nologo

    log_info "Running tests..."
    dotnet test --nologo --configuration Release

    log_info "Creating package version $VERSION..."
    dotnet pack LibArchive.Net/LibArchive.Net.csproj -o . "-p:PackageVersion=$VERSION" --nologo --configuration Release

    log_info "Package created:"
    ls -lh LibArchive.Net.*.nupkg
fi

log_info "Build complete!"
