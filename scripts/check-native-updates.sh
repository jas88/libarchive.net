#!/bin/bash
set -uo pipefail
# Note: Not using -e so individual check failures don't stop the entire script

# Configuration
REPO_ROOT="${GITHUB_WORKSPACE:-$(pwd)}"
CONFIG_FILE="$REPO_ROOT/native/build-config.sh"
DRY_RUN="${DRY_RUN:-1}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Temporary files
UPDATES_FILE=$(mktemp)
FAILED_CHECKS=$(mktemp)
trap "rm -f $UPDATES_FILE $FAILED_CHECKS" EXIT

# Check for required tools
for cmd in curl jq gh git; do
    if ! command -v $cmd &> /dev/null; then
        echo -e "${RED}Error: $cmd is required but not installed${NC}"
        exit 1
    fi
done

# Read current versions from build-config.sh
read_current_version() {
    local var_name=$1
    grep "^${var_name}=" "$CONFIG_FILE" | sed 's/^[^=]*="\([^"]*\)"/\1/'
}

# Check for existing open PRs with native dependency updates
check_existing_prs() {
    echo "Checking for existing native dependency update PRs..."

    # Search for open PRs with the "native" label created by github-actions
    local existing_prs
    existing_prs=$(gh pr list -R jas88/libarchive.net \
        --state open \
        --label "native" \
        --json number,title,headRefName \
        --jq '.[] | select(.headRefName | startswith("dependabot/native/")) | .number' 2>/dev/null || echo "")

    if [ -n "$existing_prs" ]; then
        local pr_count
        pr_count=$(echo "$existing_prs" | wc -l | tr -d ' ')
        echo -e "${YELLOW}Found $pr_count existing native dependency PR(s):${NC}"
        for pr in $existing_prs; do
            local pr_title
            pr_title=$(gh pr view "$pr" -R jas88/libarchive.net --json title --jq '.title' 2>/dev/null || echo "Unknown")
            echo "  - PR #$pr: $pr_title"
        done
        echo ""
        echo -e "${YELLOW}Skipping update check - please review and merge existing PR(s) first${NC}"
        echo "To force a new PR, close or merge the existing ones."
        return 1
    fi

    echo -e "${GREEN}No existing native dependency PRs found${NC}"
    return 0
}

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

# Function to update version variable in build-config.sh
update_version() {
    local dep_name=$1
    local old_ver=$2
    local new_ver=$3

    local config_script="$REPO_ROOT/native/build-config.sh"
    local var_name=$(echo "$dep_name" | tr '[:lower:]-' '[:upper:]_')_VERSION

    if [ "$DRY_RUN" = "1" ]; then
        echo "  Would update $var_name in build-config.sh: $old_ver -> $new_ver"
    else
        # Use sed to update the version variable
        if [[ "$OSTYPE" == "darwin"* ]]; then
            # macOS requires empty string after -i for in-place edit
            sed -i '' "s/^${var_name}=\"${old_ver}\"$/${var_name}=\"${new_ver}\"/" "$config_script"
        else
            # Linux GNU sed
            sed -i "s/^${var_name}=\"${old_ver}\"$/${var_name}=\"${new_ver}\"/" "$config_script"
        fi
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
        echo "$name" >> "$FAILED_CHECKS"
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

# Check for existing PRs first (skip if any are open)
if ! check_existing_prs; then
    exit 0
fi

# Main dependency checking
echo ""
echo "Checking native dependency versions..."
echo ""

# Read current versions from build-config.sh
CURRENT_LIBARCHIVE=$(read_current_version "LIBARCHIVE_VERSION")
CURRENT_LZ4=$(read_current_version "LZ4_VERSION")
CURRENT_ZSTD=$(read_current_version "ZSTD_VERSION")
CURRENT_XZ=$(read_current_version "XZ_VERSION")
CURRENT_ZLIB=$(read_current_version "ZLIB_VERSION")
CURRENT_LIBXML2=$(read_current_version "LIBXML2_VERSION")
CURRENT_LZO=$(read_current_version "LZO_VERSION")
CURRENT_BZIP2=$(read_current_version "BZIP2_VERSION")

# libarchive
check_dependency "libarchive" "$CURRENT_LIBARCHIVE" "github" "libarchive/libarchive" || true

# lz4
check_dependency "lz4" "$CURRENT_LZ4" "github" "lz4/lz4" || true

# zstd
check_dependency "zstd" "$CURRENT_ZSTD" "github" "facebook/zstd" || true

# xz
check_dependency "xz" "$CURRENT_XZ" "github" "tukaani-project/xz" || true

# zlib
check_dependency "zlib" "$CURRENT_ZLIB" "web" \
    "https://zlib.net/" \
    'zlib-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.xz' || true

# libxml2
check_dependency "libxml2" "$CURRENT_LIBXML2" "gnome" \
    'libxml2-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.xz' || true

# lzo
check_dependency "lzo" "$CURRENT_LZO" "web" \
    "https://www.oberhumer.com/opensource/lzo/download/" \
    'lzo-([0-9]+\.[0-9]+)\.tar\.gz' || true

# bzip2
check_dependency "bzip2" "$CURRENT_BZIP2" "web" \
    "https://www.sourceware.org/pub/bzip2/" \
    'bzip2-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.gz' || true

# musl toolchain versions are reference only (we use Bootlin prebuilt toolchains)
# These checks are informational - actual toolchain versions come from Bootlin
echo ""
echo "ℹ️  Note: musl/gcc/binutils versions in build-config.sh are reference only"
echo "   Actual toolchains come from Bootlin prebuilt stable releases"
echo "   To update toolchains, see Bootlin releases: https://toolchains.bootlin.com/"

echo ""

# Report any failed checks
if [ -s "$FAILED_CHECKS" ]; then
    failed_count=$(wc -l < "$FAILED_CHECKS")
    echo -e "${YELLOW}⚠️  Warning: $failed_count dependency check(s) failed:${NC}"
    while read -r name; do
        echo "  - $name (network error or version pattern mismatch)"
    done < "$FAILED_CHECKS"
    echo ""
fi

# Check if any updates were found
if [ ! -s "$UPDATES_FILE" ]; then
    if [ -s "$FAILED_CHECKS" ]; then
        echo -e "${RED}No updates found, but some checks failed${NC}"
        exit 1
    else
        echo -e "${GREEN}All dependencies are up to date ✓${NC}"
        exit 0
    fi
fi

# Process updates
update_count=$(wc -l < "$UPDATES_FILE")
echo -e "${YELLOW}$update_count update(s) found!${NC}"
echo ""

# Apply updates to build-config.sh
while IFS='|' read -r name old new; do
    echo "Updating $name: $old -> $new"
    update_version "$name" "$old" "$new"
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
if ! git checkout -b "$branch_name"; then
    echo -e "${RED}✗ Failed to create branch${NC}"
    exit 1
fi

echo "Committing changes..."
if ! git add "$REPO_ROOT/native/build-config.sh"; then
    echo -e "${RED}✗ Failed to stage changes${NC}"
    exit 1
fi

if ! git commit -m "$pr_title" -m "$update_list"; then
    echo -e "${RED}✗ Failed to commit changes${NC}"
    exit 1
fi

echo "Pushing to origin..."
if ! git push -u origin "$branch_name"; then
    echo -e "${RED}✗ Failed to push branch to origin${NC}"
    echo "This could be due to:"
    echo "  - Network issues"
    echo "  - Insufficient permissions"
    echo "  - Branch already exists"
    exit 1
fi

# Create PR body
pr_body=$(cat <<EOF
## Native Dependency Updates

$update_list

### Changes
$(while IFS='|' read -r name old new; do
    echo "- **$name**: \`$old\` → \`$new\`"
done < "$UPDATES_FILE")

### Build Configuration Updated
- \`native/build-config.sh\` - Updated version variables

### Testing
CI will rebuild native libraries with updated versions and run tests on all platforms.

### Notes
- This PR was automatically generated by the native dependency checker
- Please review changes and ensure compatibility before merging
- Native libraries will be rebuilt by CI/CD pipeline
EOF
)

echo "Creating pull request..."
if echo "$pr_body" | gh pr create -R jas88/libarchive.net \
    --title "$pr_title" \
    --body-file - \
    --label "dependencies" \
    --label "native"; then
    echo -e "${GREEN}✓ Pull request created successfully${NC}"
else
    pr_exit_code=$?
    echo -e "${RED}✗ Failed to create pull request (exit code: $pr_exit_code)${NC}"
    echo "This could be due to:"
    echo "  - Missing GitHub authentication (check GITHUB_TOKEN)"
    echo "  - Network issues"
    echo "  - Missing labels (ensure 'dependencies' and 'native' labels exist)"
    echo "  - Insufficient permissions"
    exit 1
fi

# Exit with error if any checks failed (even though we created PRs for successful checks)
if [ -s "$FAILED_CHECKS" ]; then
    failed_count=$(wc -l < "$FAILED_CHECKS")
    echo ""
    echo -e "${RED}⚠️  Exiting with error: $failed_count dependency check(s) failed${NC}"
    echo "PR(s) were created for successful checks, but please investigate failures."
    exit 1
fi
