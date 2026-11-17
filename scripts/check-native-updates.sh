#!/bin/bash
set -uo pipefail
# Note: Not using -e so individual check failures don't stop the entire script

# Configuration
REPO_ROOT="${GITHUB_WORKSPACE:-$(pwd)}"
CONFIG_FILE="$REPO_ROOT/native/build-config.sh"
DRY_RUN="${DRY_RUN:-1}"
# Token for GitHub API requests (see fetch_url): GITHUB_TOKEN, or else GH_TOKEN.
# Never printed, and only ever sent to https://api.github.com/.
GITHUB_API_TOKEN="${GITHUB_TOKEN:-${GH_TOKEN:-}}"

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m' # No Color

# Temporary files, all in one directory that is removed on exit, together with any
# temporary file replace_config has beside build-config.sh at that moment. A signal
# ends the script through exit, so that the EXIT trap also runs then.
CONFIG_TMP=""
WORK_DIR=$(mktemp -d) || { echo -e "${RED}Error: could not create a temporary directory${NC}"; exit 1; }
trap 'rm -rf "$WORK_DIR"; [ -z "$CONFIG_TMP" ] || rm -f "$CONFIG_TMP"' EXIT
trap 'exit 129' HUP
trap 'exit 130' INT
trap 'exit 143' TERM
UPDATES_FILE="$WORK_DIR/updates"   # name|old|new: every update found
APPLIED_FILE="$WORK_DIR/applied"   # name|old|new: updates written to build-config.sh
FAILED_CHECKS="$WORK_DIR/failed"   # name|reason: every failed check, failed updates included
if ! : > "$UPDATES_FILE" || ! : > "$APPLIED_FILE" || ! : > "$FAILED_CHECKS"; then
    echo -e "${RED}Error: could not create the temporary files in $WORK_DIR${NC}"
    exit 1
fi

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

# Append a line to one of the temporary files. A lost line would be a lost update or
# a lost failed check, so a failed write stops the script.
add_line() {
    printf '%s\n' "$2" >> "$1" || { echo -e "${RED}Error: could not write $1${NC}"; exit 1; }
}

# Record a failed check: the library's name and why it failed
record_failure() {
    add_line "$FAILED_CHECKS" "$1|$2"
}

# List the failed checks recorded so far
report_failed_checks() {
    local failed_count name reason
    failed_count=$(wc -l < "$FAILED_CHECKS" | tr -d ' ')
    echo -e "${YELLOW}⚠️  Warning: $failed_count dependency check(s) failed:${NC}"
    while IFS='|' read -r name reason; do
        echo "  - $name ($reason)"
    done < "$FAILED_CHECKS"
    echo ""
}

# Check for existing open PRs with native dependency updates.
# Returns 0 if there are none, 1 if there are (after listing them), and 2 if the
# lookup itself failed, so that a failed lookup is never mistaken for "none open".
check_existing_prs() {
    echo "Checking for existing native dependency update PRs..."

    # Open PRs that carry the "native" label and whose branch name starts with
    # "dependabot/native/", the prefix of the branches this script creates. Who
    # opened them is not checked.
    local existing_prs
    if ! existing_prs=$(gh pr list -R jas88/libarchive.net \
        --state open \
        --label "native" \
        --json number,title,headRefName \
        --jq '.[] | select(.headRefName | startswith("dependabot/native/")) | .number'); then
        return 2
    fi

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

# True if $1 is an https URL whose host is exactly api.github.com: the whole of
# what lies between "https://" and the first "/" must be "api.github.com", so a
# different host (api.github.com.example), user info, a port, or a URL that only
# contains this one further along does not match.
is_github_api_url() {
    case "$1" in
        https://api.github.com/*) return 0 ;;
        *) return 1 ;;
    esac
}

# Fetch a URL to stdout, failing on HTTP errors. Usage: fetch_url <url> [curl options]
# With GITHUB_API_TOKEN set, a request to the GitHub API carries it as a bearer
# token. curl reads that header from stdin, so the token is on no command line, and
# that request follows no redirect (no -L, and --max-redirs 0 in case an option
# asks for one), so the token can only reach the URL checked above. Every other
# request is made as before: it follows redirects and carries no token.
fetch_url() {
    local url=$1
    shift
    if [ -n "$GITHUB_API_TOKEN" ] && is_github_api_url "$url"; then
        printf 'Authorization: Bearer %s\n' "$GITHUB_API_TOKEN" | \
            curl -sf -H @- "$@" --max-redirs 0 "$url"
    else
        curl -sfL "$@" "$url"
    fi
}

# Function to check GitHub releases
check_github_release() {
    local repo=$1
    local url="https://api.github.com/repos/$repo/releases/latest"

    local version
    version=$(fetch_url "$url" -H "Accept: application/vnd.github.v3+json" | \
                    jq -r '.tag_name // empty' | sed 's/^v//')

    # printf, not echo: a tag such as "-n" must reach the version check unchanged
    printf '%s\n' "$version"
}

# Function to check web page for version
check_web_version() {
    local url=$1
    local pattern=$2

    local content
    content=$(fetch_url "$url" || echo "")
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

# True if $1 is a plain dotted number of at most MAX_VERSION_LENGTH characters.
# A version taken from upstream is written into build-config.sh, which this script
# and every build source as shell code, and goes into sed expressions, the PR and the
# commit, so a release tag such as 1.0$(cmd) must never get that far. The digits are
# listed rather than written as a range, so that no locale can widen the set.
MAX_VERSION_LENGTH=32
valid_version() {
    local re='^[0123456789]+(\.[0123456789]+)*$'
    [ ${#1} -le "$MAX_VERSION_LENGTH" ] && [[ $1 =~ $re ]]
}

# Print a value from upstream quoted for the log: cut to 64 characters, then
# printf %q in the C locale, which escapes every byte that is not printable ASCII,
# so the value cannot inject terminal escapes or start a new log line.
log_quote() {
    local value=$1
    [ ${#value} -le 64 ] || value="${value:0:64}..."
    ( export LC_ALL=C; printf '%q' "$value" )
}

# Print the value that the copy of build-config.sh in $1 gives the variable named $2.
# The copy is sourced in a subshell, so that nothing in it changes this script's own
# variables, and the variable is unset first, so that only the copy can set it.
config_value() {
    local wanted_var=$2
    (
        unset "$wanted_var"
        # shellcheck source=/dev/null  # a copy of native/build-config.sh
        . "$1" >/dev/null 2>&1
        printf '%s' "${!wanted_var:-}"
    )
}

# Print the SHA256 of the file $2, computed by sha256_compute from the copy of
# build-config.sh in $1 (sourced in a subshell, as above). Fails without a SHA256 tool.
config_sha256() {
    (
        # shellcheck source=/dev/null  # a copy of native/build-config.sh
        . "$1" >/dev/null 2>&1
        sha256_compute "$2"
    )
}

# Replace build-config.sh with the file $1 so that it is never half written. The new
# content goes into a temporary file in the same directory, so that the rename cannot
# cross file systems; cp -p gives that file build-config.sh's mode (portably, unlike
# chmod --reference or stat), and mv -f then renames it over build-config.sh in one
# step. If any step fails, the temporary file is removed and 1 is returned, with
# build-config.sh exactly as it was. If the script is stopped meanwhile, the EXIT
# trap removes the temporary file.
replace_config() {
    CONFIG_TMP=$(mktemp "${CONFIG_FILE%/*}/.build-config.sh.XXXXXX") || { CONFIG_TMP=""; return 1; }
    if cp -p "$CONFIG_FILE" "$CONFIG_TMP" && cat "$1" > "$CONFIG_TMP" && mv -f "$CONFIG_TMP" "$CONFIG_FILE"; then
        CONFIG_TMP=""
        return 0
    fi
    rm -f "$CONFIG_TMP"
    CONFIG_TMP=""
    return 1
}

# Report and record an update that could not be applied
update_failed() {
    echo -e "  ${RED}ERROR: $2${NC}"
    echo -e "  ${RED}Leaving $1 out of the PR: build-config.sh keeps its current version and checksum${NC}"
    record_failure "$1" "update not applied: $2"
}

# Function to update a library's version and SHA256 checksum in build-config.sh.
# Both go into a copy of the file first, and the copy replaces build-config.sh only
# once it holds the new version and the checksum of the new version's tarball, and the
# update has been recorded for the PR (APPLIED_FILE; this is the only place that does).
# If any step fails, build-config.sh is left exactly as it was, the failure is recorded
# as a failed check, and 1 is returned so that the library is left out of the PR.
update_version() {
    local dep_name=$1
    local old_ver=$2
    local new_ver=$3

    local prefix
    prefix=$(echo "$dep_name" | tr '[:lower:]-' '[:upper:]_')
    local var_name="${prefix}_VERSION"
    local sha_var_name="${prefix}_SHA256"
    local url_var_name="${prefix}_URL"

    if [ "$DRY_RUN" = "1" ]; then
        echo "  Would update $var_name in build-config.sh: $old_ver -> $new_ver"
        echo "  Would update $sha_var_name checksum"
        return 0
    fi

    local candidate="$WORK_DIR/build-config.$dep_name"
    local tarball="$WORK_DIR/$dep_name.download"

    # The new version, in the copy
    if ! sed "s/^${var_name}=\"${old_ver}\"$/${var_name}=\"${new_ver}\"/" "$CONFIG_FILE" > "$candidate" ||
        ! grep -qxF "${var_name}=\"${new_ver}\"" "$candidate"; then
        update_failed "$dep_name" "could not change the ${var_name} line to ${var_name}=\"${new_ver}\""
        return 1
    fi

    # The new version's download URL, as the copy builds it
    local new_url
    new_url=$(config_value "$candidate" "$url_var_name")
    if [ -z "$new_url" ]; then
        update_failed "$dep_name" "build-config.sh does not set ${url_var_name}"
        return 1
    fi

    echo "  Downloading new version to compute SHA256..."
    if ! curl -fsSL "$new_url" -o "$tarball"; then
        update_failed "$dep_name" "could not download ${new_url}"
        return 1
    fi

    # Validate we got a 64-char hex string before writing
    local new_sha256
    new_sha256=$(config_sha256 "$candidate" "$tarball")
    if [[ ! "$new_sha256" =~ ^[0-9a-f]{64}$ ]]; then
        update_failed "$dep_name" "SHA256 computation failed (got: '${new_sha256}')"
        return 1
    fi

    # The new checksum, in the copy
    if ! grep -q "^${sha_var_name}=" "$candidate"; then
        update_failed "$dep_name" "build-config.sh has no ${sha_var_name} line"
        return 1
    fi
    if ! sed "s/^${sha_var_name}=\"[^\"]*\"$/${sha_var_name}=\"${new_sha256}\"/" "$candidate" > "$candidate.sha256" ||
        ! grep -qxF "${sha_var_name}=\"${new_sha256}\"" "$candidate.sha256"; then
        update_failed "$dep_name" "the ${sha_var_name} line is not of the form ${sha_var_name}=\"<sha256>\""
        return 1
    fi

    # Both are in place. Record the update for the PR first, so that a record that
    # cannot be written stops the script (add_line) before build-config.sh is touched.
    # Then the copy replaces build-config.sh, atomically (see replace_config). If that
    # fails, build-config.sh is unchanged but cannot be updated at all, so the script
    # stops; the record left behind is harmless, as this exits before any branch or
    # PR is created.
    add_line "$APPLIED_FILE" "$dep_name|$old_ver|$new_ver"
    if ! replace_config "$candidate.sha256"; then
        echo -e "  ${RED}ERROR: could not replace $CONFIG_FILE; it is unchanged${NC}"
        exit 1
    fi
    echo "  Updated $var_name: $new_ver"
    echo "  Updated $sha_var_name: $new_sha256"
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
            local latest_mm
            latest_mm=$(curl -sf "$base_url" | \
                grep -oE 'href="([0-9]+\.[0-9]+)/"' | \
                sed 's/href="//;s/\/"//g' | \
                sort -V | tail -1)

            # The directory name goes into a URL, so it must pass the version check
            # too. If it does not (or is empty), it is what gets reported below.
            if valid_version "$latest_mm"; then
                local url="$base_url$latest_mm/"
                local pattern="${check_args[0]}"
                latest=$(check_web_version "$url" "$pattern")
            else
                latest="$latest_mm"
            fi
            ;;
    esac

    if [ -z "$latest" ]; then
        echo -e "${RED}ERROR${NC}"
        record_failure "$name" "network error or version pattern mismatch"
        return 1
    fi

    # Every method's result is checked here, before it is compared, printed or
    # recorded as an update: anything else is a failed check, quoted for the log
    if ! valid_version "$latest"; then
        echo -e "${RED}ERROR${NC}"
        record_failure "$name" "unexpected version string $(log_quote "$latest")"
        return 1
    fi

    printf "latest: %-7s" "$latest"

    if version_gt "$latest" "$current"; then
        echo -e " ${YELLOW}⚠️  UPDATE AVAILABLE${NC}"
        add_line "$UPDATES_FILE" "$name|$current|$latest"
        return 0
    else
        echo -e " ${GREEN}✓${NC}"
        return 0
    fi
}

# Check for existing PRs first (skip if any are open)
check_existing_prs
case $? in
    0) ;;
    1) exit 0 ;;
    *)
        # Without the lookup, a second update PR could be opened next to an open one.
        # Only a dry run, which opens nothing, may carry on.
        if [ "$DRY_RUN" = "1" ]; then
            echo -e "${YELLOW}Warning: could not look up open native dependency PRs (see the gh error above)${NC}"
            echo -e "${YELLOW}Carrying on because this is a dry run; applying updates would stop here${NC}"
        else
            echo -e "${RED}Error: could not look up open native dependency PRs (see the gh error above)${NC}"
            echo -e "${RED}Stopping before changing anything, so that no duplicate PR can be opened${NC}"
            exit 1
        fi
        ;;
esac

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
CURRENT_BZIP2=$(read_current_version "BZIP2_VERSION")
CURRENT_MBEDTLS=$(read_current_version "MBEDTLS_VERSION")

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
    'zlib-([0-9]+\.[0-9]+(\.[0-9]+)?)\.tar\.xz' || true

# libxml2
check_dependency "libxml2" "$CURRENT_LIBXML2" "gnome" \
    'libxml2-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.xz' || true

# bzip2
check_dependency "bzip2" "$CURRENT_BZIP2" "web" \
    "https://www.sourceware.org/pub/bzip2/" \
    'bzip2-([0-9]+\.[0-9]+\.[0-9]+)\.tar\.gz' || true

# mbedTLS (Linux builds only). Deliberately not the "github" check: GitHub's latest
# release is the 4.x line, which libarchive cannot build against. Only a published
# release tarball of the 3.6 LTS line can match this pattern, so no other version
# can ever be proposed.
check_dependency "mbedtls" "$CURRENT_MBEDTLS" "web" \
    "https://api.github.com/repos/Mbed-TLS/mbedtls/releases?per_page=100" \
    'releases/download/mbedtls-(3\.6\.[0-9]+)/mbedtls-3\.6\.[0-9]+\.tar\.bz2' || true

# The Linux toolchains are not checked here: they are Bootlin prebuilt releases,
# selected by BOOTLIN_RELEASE in build-config.sh, which Renovate is configured to
# keep current (.github/renovate.json)
echo ""
echo "ℹ️  Note: the Linux toolchains (BOOTLIN_RELEASE in build-config.sh) are not checked here"
echo "   Renovate is configured to keep BOOTLIN_RELEASE current (.github/renovate.json)"
echo "   Bootlin releases: https://toolchains.bootlin.com/"

echo ""

# Report any failed checks
if [ -s "$FAILED_CHECKS" ]; then
    report_failed_checks
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
update_count=$(wc -l < "$UPDATES_FILE" | tr -d ' ')
echo -e "${YELLOW}$update_count update(s) found!${NC}"
echo ""

# Apply updates to build-config.sh. update_version records each update it applies in
# full (new version and its checksum) for the PR, and each one it cannot apply as a
# failed check.
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

# Nothing to commit if no update could be applied: no branch, commit or PR
if [ ! -s "$APPLIED_FILE" ]; then
    echo ""
    report_failed_checks
    echo -e "${RED}No update could be applied, so no PR was created${NC}"
    exit 1
fi

# Create branch and PR for the updates that were applied
applied_count=$(wc -l < "$APPLIED_FILE" | tr -d ' ')
branch_name="dependabot/native/$(date +%Y%m%d)-updates"
update_list=$(while IFS='|' read -r name old new; do
    echo "- Bump $name from $old to $new"
done < "$APPLIED_FILE")

if [ "$applied_count" = "1" ]; then
    first_update=$(head -1 "$APPLIED_FILE")
    IFS='|' read -r name old new <<< "$first_update"
    pr_title="Bump native $name from $old to $new"
else
    pr_title="Bump $applied_count native dependencies"
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
done < "$APPLIED_FILE")

### Build Configuration Updated
- \`native/build-config.sh\` - Updated version and SHA256 checksum variables

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

# Exit with error if any checks failed, updates that could not be applied included
# (even though we created a PR for the updates that were applied)
if [ -s "$FAILED_CHECKS" ]; then
    failed_count=$(wc -l < "$FAILED_CHECKS" | tr -d ' ')
    echo ""
    report_failed_checks
    echo -e "${RED}⚠️  Exiting with error: $failed_count dependency check(s) failed${NC}"
    echo "A PR was created for the updates that were applied, but please investigate failures."
    exit 1
fi
