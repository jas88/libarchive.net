#!/bin/bash
set -euo pipefail

# Configuration
REPO_ROOT="${GITHUB_WORKSPACE:-$(pwd)}"
LINUX_SCRIPT="$REPO_ROOT/native/build-linux.sh"
MACOS_SCRIPT="$REPO_ROOT/native/build-macos.sh"
DRY_RUN="${DRY_RUN:-1}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Temporary files
UPDATES_FILE=$(mktemp)
trap "rm -f $UPDATES_FILE" EXIT

# Check for required tools
for cmd in curl jq gh git; do
    if ! command -v $cmd &> /dev/null; then
        echo -e "${RED}Error: $cmd is required but not installed${NC}"
        exit 1
    fi
done

# Function to check GitHub releases
check_github_release() {
    local repo=$1
    local url="https://api.github.com/repos/$repo/releases/latest"

    local version=$(curl -sf -H "Accept: application/vnd.github.v3+json" "$url" | \
                    jq -r '.tag_name // empty' | sed 's/^v//')

    echo "$version"
}

# Function to check web page for version
check_web_version() {
    local url=$1
    local pattern=$2

    local content=$(curl -sf "$url" || echo "")
    if [ -z "$content" ]; then
        echo ""
        return
    fi

    echo "$content" | grep -oE "$pattern" | \
        sed -E "s|$pattern|\1|" | \
        sort -V | tail -1
}

# Function to compare versions
version_gt() {
    local ver1=$1
    local ver2=$2

    # Use sort -V to compare versions
    [ "$ver2" = "$(echo -e "$ver1\n$ver2" | sort -V | head -n1)" ] && [ "$ver1" != "$ver2" ]
}

# Function to update script file
update_script() {
    local script=$1
    local dep_name=$2
    local old_ver=$3
    local new_ver=$4
    local url_pattern=$5

    local old_url=$(printf "$url_pattern" "$old_ver" "$old_ver")
    local new_url=$(printf "$url_pattern" "$new_ver" "$new_ver")

    # Handle %M placeholder for major.minor
    if [[ "$new_url" == *"%M"* ]]; then
        local major_minor=$(echo "$new_ver" | cut -d. -f1-2)
        new_url="${new_url//%M/$major_minor}"
    fi

    if [ "$DRY_RUN" = "1" ]; then
        echo "  Would update: $old_url -> $new_url"
    else
        sed -i.bak "s|$(echo "$old_url" | sed 's/[.[\*^$(){}?+|]/\\&/g')|$new_url|g" "$script"
        rm -f "$script.bak"
    fi
}

# Function to check single dependency
check_dependency() {
    local name=$1
    local current=$2
    local check_type=$3
    shift 3
    local check_args=("$@")

    printf "Checking %-12s (current: %-7s)... " "$name" "$current"

    local latest=""

    case "$check_type" in
        github)
            local repo="${check_args[0]}"
            latest=$(check_github_release "$repo")
            ;;
        web)
            local url="${check_args[0]}"
            local pattern="${check_args[1]}"
            latest=$(check_web_version "$url" "$pattern")
            ;;
        gnome)
            # Check for newer major.minor first
            local base_url="https://download.gnome.org/sources/libxml2/"
            local latest_mm=$(curl -sf "$base_url" | \
                grep -oE 'href="([0-9]+\.[0-9]+)/"' | \
                sed 's/href="//;s/\/"//g' | \
                sort -V | tail -1)

            local url="$base_url$latest_mm/"
            local pattern="${check_args[0]}"
            latest=$(check_web_version "$url" "$pattern")
            ;;
    esac

    if [ -z "$latest" ]; then
        echo -e "${RED}ERROR${NC}"
        return 1
    fi

    printf "latest: %-7s" "$latest"

    if version_gt "$latest" "$current"; then
        echo -e " ${YELLOW}⚠️  UPDATE AVAILABLE${NC}"
        echo "$name|$current|$latest" >> "$UPDATES_FILE"
        return 0
    else
        echo -e " ${GREEN}✓${NC}"
        return 0
    fi
}

# Main dependency checking
echo "Checking native dependency versions..."
echo ""

# libarchive
check_dependency "libarchive" "3.7.3" "github" "libarchive/libarchive"

# lz4
check_dependency "lz4" "1.9.4" "github" "lz4/lz4"

# zstd
check_dependency "zstd" "1.5.6" "github" "facebook/zstd"

# xz
check_dependency "xz" "5.4.6" "github" "tukaani-project/xz"

# zlib
check_dependency "zlib" "1.3.1" "web" \
    "https://zlib.net/" \
    'zlib-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.xz'

# libxml2
check_dependency "libxml2" "2.12.6" "gnome" \
    'libxml2-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.xz'

# lzo
check_dependency "lzo" "2.10" "web" \
    "https://www.oberhumer.com/opensource/lzo/download/" \
    'lzo-([0-9]+\.[0-9]+)\.tar\.gz'

# bzip2
check_dependency "bzip2" "1.0.8" "web" \
    "https://www.sourceware.org/pub/bzip2/" \
    'bzip2-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.gz'

# musl toolchain (Linux only)
check_dependency "musl" "1.2.5" "web" \
    "https://musl.libc.org/releases/" \
    'musl-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.gz'

check_dependency "gcc" "9.4.0" "web" \
    "https://ftp.gnu.org/gnu/gcc/" \
    'gcc-([0-9]+\.[0-9]+\.[0-9]+)/'

check_dependency "binutils" "2.44" "web" \
    "https://ftp.gnu.org/gnu/binutils/" \
    'binutils-([0-9]+\.[0-9]+)\.tar'

echo ""

# Check if any updates were found
if [ ! -s "$UPDATES_FILE" ]; then
    echo -e "${GREEN}All dependencies are up to date ✓${NC}"
    exit 0
fi

# Process updates
update_count=$(wc -l < "$UPDATES_FILE")
echo -e "${YELLOW}$update_count update(s) found!${NC}"
echo ""

# Apply updates to both scripts
while IFS='|' read -r name old new; do
    case "$name" in
        libarchive)
            url_pattern='https://github.com/libarchive/libarchive/releases/download/v%s/libarchive-%s.tar.xz'
            ;;
        lz4)
            url_pattern='https://github.com/lz4/lz4/archive/refs/tags/v%s.tar.gz'
            ;;
        zstd)
            url_pattern='https://github.com/facebook/zstd/releases/download/v%s/zstd-%s.tar.gz'
            ;;
        xz)
            url_pattern='https://github.com/tukaani-project/xz/releases/download/v%s/xz-%s.tar.xz'
            ;;
        zlib)
            url_pattern='https://zlib.net/zlib-%s.tar.xz'
            ;;
        libxml2)
            url_pattern='https://download.gnome.org/sources/libxml2/%%M/libxml2-%s.tar.xz'
            ;;
        lzo)
            url_pattern='https://www.oberhumer.com/opensource/lzo/download/lzo-%s.tar.gz'
            ;;
        bzip2)
            url_pattern='https://www.sourceware.org/pub/bzip2/bzip2-%s.tar.gz'
            ;;
        musl|gcc|binutils)
            # Update build-config.sh for toolchain versions
            config_script="$REPO_ROOT/native/build-config.sh"
            var_name=$(echo "$name" | tr '[:lower:]' '[:upper:]')_VERSION
            if [ "$DRY_RUN" = "1" ]; then
                echo "  Would update $var_name in build-config.sh: $old -> $new"
            else
                sed -i.bak "s/^${var_name}=\"${old}\"$/${var_name}=\"${new}\"/" "$config_script"
                rm -f "$config_script.bak"
            fi
            continue
            ;;
    esac

    echo "Updating $name: $old -> $new"
    update_script "$LINUX_SCRIPT" "$name" "$old" "$new" "$url_pattern"
    update_script "$MACOS_SCRIPT" "$name" "$old" "$new" "$url_pattern"
done < "$UPDATES_FILE"

# Create PR if not dry run
if [ "$DRY_RUN" = "1" ]; then
    echo ""
    echo "=== DRY RUN MODE ==="
    echo "To apply changes and create PR, run with: DRY_RUN=0"
    exit 0
fi

# Create branch and PR
branch_name="dependabot/native/$(date +%Y%m%d)-updates"
update_list=$(while IFS='|' read -r name old new; do
    echo "- Bump $name from $old to $new"
done < "$UPDATES_FILE")

if [ "$update_count" = "1" ]; then
    first_update=$(head -1 "$UPDATES_FILE")
    IFS='|' read -r name old new <<< "$first_update"
    pr_title="Bump native $name from $old to $new"
else
    pr_title="Bump $update_count native dependencies"
fi

# Git operations
echo ""
echo "Creating branch: $branch_name"
git checkout -b "$branch_name"

echo "Committing changes..."
git add "$LINUX_SCRIPT" "$MACOS_SCRIPT" "$REPO_ROOT/native/build-config.sh"
git commit -m "$pr_title" -m "$update_list"

echo "Pushing to origin..."
git push -u origin "$branch_name"

# Create PR body
pr_body=$(cat <<EOF
## Native Dependency Updates

$update_list

### Changes
$(while IFS='|' read -r name old new; do
    echo "- **$name**: \`$old\` → \`$new\`"
done < "$UPDATES_FILE")

### Build Scripts Updated
- \`native/build-config.sh\`
- \`native/build-linux.sh\`
- \`native/build-macos.sh\`

### Testing
CI will rebuild native libraries and run tests on all platforms.

### Notes
- This PR was automatically generated by the native dependency checker
- Please review changes and ensure compatibility before merging
- Native libraries will be rebuilt by CI/CD pipeline
EOF
)

echo "Creating pull request..."
echo "$pr_body" | gh pr create -R jas88/libarchive.net \
    --title "$pr_title" \
    --body-file - \
    --label "dependencies" \
    --label "native"

echo -e "${GREEN}✓ Pull request created successfully${NC}"
