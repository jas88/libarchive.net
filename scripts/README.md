# Native Dependency Update Checker

Automated tool for checking and updating native library dependencies in libarchive.net.

## Overview

This script monitors 8 native C/C++ libraries that are statically linked into libarchive.net:

| Library | Current | Check Method | Update Frequency |
|---------|---------|--------------|------------------|
| libarchive | 3.7.3 | GitHub Releases | High |
| lz4 | 1.9.4 | GitHub Releases | Medium |
| zstd | 1.5.6 | GitHub Releases | High |
| xz/liblzma | 5.4.6 | GitHub Releases | Medium |
| zlib | 1.3.1 | Web Scrape (zlib.net) | Low |
| libxml2 | 2.12.6 | GNOME Releases | Medium |
| lzo | 2.10 | Web Scrape | Very Low |
| bzip2 | 1.0.8 | Web Scrape (uses "latest") | Very Low |

## Usage

### Manual Check (Local)

```bash
# Dry run - check for updates without creating PR
perl scripts/check-native-updates.pl

# Actual run - creates PR if updates found
DRY_RUN=0 perl scripts/check-native-updates.pl
```

### Automated Check (GitHub Actions)

The workflow runs weekly on Mondays at 9:00 AM UTC:

- **Scheduled**: `.github/workflows/check-native-dependencies.yml`
- **Manual trigger**: Via workflow_dispatch with optional dry_run parameter

When updates are found, the script:
1. Updates version numbers in both `native/build-linux.sh` and `native/build-macos.sh`
2. Creates a new branch: `dependabot/native/<libraries>`
3. Commits the changes
4. Opens a PR with:
   - Clear title: "Bump native <library> from X to Y"
   - Detailed description listing all updates
   - Labels: `dependencies`, `native`

## How It Works

### Version Detection

**GitHub Releases API** (libarchive, lz4, zstd, xz):
```perl
GET https://api.github.com/repos/{owner}/{repo}/releases/latest
```

**Web Scraping** (zlib, lzo, bzip2):
- Downloads HTML page
- Extracts version from tarball filenames using regex
- Returns highest version found

**GNOME Releases** (libxml2):
- First checks for newer major.minor series
- Then finds latest patch version in that series

### Update Process

1. **Version Comparison**: Uses Perl's `version` module for semver comparison
2. **Script Updates**: Replaces download URLs in both build scripts
3. **PR Creation**: Uses GitHub CLI (`gh`) to create pull request
4. **CI Validation**: Existing CI/CD builds native libraries and runs tests

### Error Handling

- Network failures: Warns and continues with other dependencies
- Version parsing errors: Skips that dependency
- Git failures: Aborts with error message
- All errors logged to console

## Dependencies

### Perl Modules
```bash
# Ubuntu/Debian
sudo apt-get install libwww-perl libjson-pp-perl libfile-slurp-perl

# macOS
cpan install LWP::UserAgent JSON::PP File::Slurp
```

### System Tools
- `git` - Version control operations
- `gh` - GitHub CLI for PR creation (must be authenticated)

## Configuration

Edit `check-native-updates.pl` to modify:

- **Check URLs**: Update if upstream moves locations
- **Version Regex**: Adjust pattern matching for version extraction
- **URL Patterns**: Change download URL templates
- **PR Labels**: Customize labels applied to PRs

## Security Considerations

- Script runs with GitHub Actions bot credentials
- Only updates version numbers in build scripts
- Actual binary builds happen in CI after human review
- No automatic merging - PRs require manual approval

## Troubleshooting

### "Failed to fetch GitHub releases"
- Check GitHub API rate limits (60/hour unauthenticated)
- Set `GITHUB_TOKEN` environment variable for higher limits

### "Failed to update in script"
- URL pattern may have changed
- Check if old URL still matches format in build scripts

### "Failed to create PR"
- Ensure `gh` CLI is authenticated: `gh auth status`
- Check repository permissions for PR creation

## Future Enhancements

- [ ] Add email notifications for update failures
- [ ] Support for custom version constraints (e.g., "no major updates")
- [ ] Integration with security advisory databases (CVE checking)
- [ ] Automated testing before PR creation
- [ ] Support for other package managers (Conan, vcpkg)

## Examples

### Check Output (No Updates)
```
Checking native dependency versions...
Checking bzip2 (current: 1.0.8)... latest: 1.0.8 ✓
Checking libarchive (current: 3.7.3)... latest: 3.7.3 ✓
Checking libxml2 (current: 2.12.6)... latest: 2.12.6 ✓
Checking lz4 (current: 1.9.4)... latest: 1.9.4 ✓
Checking lzo (current: 2.10)... latest: 2.10 ✓
Checking xz (current: 5.4.6)... latest: 5.4.6 ✓
Checking zlib (current: 1.3.1)... latest: 1.3.1 ✓
Checking zstd (current: 1.5.6)... latest: 1.5.6 ✓

All dependencies are up to date ✓
```

### Check Output (Updates Found)
```
Checking native dependency versions...
Checking libarchive (current: 3.7.3)... latest: 3.7.4 ⚠️  UPDATE AVAILABLE
Checking zstd (current: 1.5.6)... latest: 1.5.7 ⚠️  UPDATE AVAILABLE

2 updates found!

=== DRY RUN: Would create PR ===
Branch: dependabot/native/libarchive-zstd
Title: Bump 2 native dependencies
Body:
## Native Dependency Updates

- Bump libarchive from 3.7.3 to 3.7.4
- Bump zstd from 1.5.6 to 1.5.7

### Changes
- **libarchive**: `3.7.3` → `3.7.4`
- **zstd**: `1.5.6` → `1.5.7`
...
```

## License

Same as libarchive.net (BSD-2-Clause)
