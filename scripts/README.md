# Native Dependency Update Checker

`check-native-updates.sh` looks upstream for newer releases of native libraries
whose versions are pinned in `native/build-config.sh`. When it finds any, it
reports them and, unless it is a dry run, updates the pins and opens one pull
request. `.github/workflows/check-native-dependencies.yml` runs it weekly.

`native/build-config.sh` is the source of truth for the pinned versions and their
SHA256 checksums, so neither is repeated here.

## How it works

1. **Look for an open update pull request.** The script asks `gh` for open pull
   requests in `jas88/libarchive.net` that carry the `native` label and whose
   branch name starts with `dependabot/native/`. If there is one, it lists it and
   exits without checking anything, dry run or not. Merge or close that pull
   request to get a new one. If the lookup itself fails, the script carries on as
   though none were open.
2. **Compare versions.** It reads each current version from
   `native/build-config.sh` and finds the latest upstream version with one of the
   [check methods](#check-methods). A version that sorts higher under `sort -V`
   is an update. A check that finds no version (network error or pattern mismatch)
   is reported as failed, and the remaining checks still run.
3. **Update the pins.** For each update it rewrites the `<NAME>_VERSION` line in
   that file (`<NAME>` being the library's name in upper case), downloads the new
   tarball from `<NAME>_URL` and rewrites the `<NAME>_SHA256` line with that
   tarball's checksum. If the download or the checksum fails, the version stays
   bumped, the old checksum stays in place and the output asks for it to be
   corrected by hand.
4. **Open a pull request.** It creates the branch
   `dependabot/native/<YYYYMMDD>-updates` from the commit that is checked out,
   commits `native/build-config.sh`, pushes the branch to `origin` and opens a
   pull request in `jas88/libarchive.net` with the labels `dependencies` and
   `native`, which must already exist. All updates from one run share that pull
   request. Its title is `Bump native <name> from <old> to <new>` for one update
   and `Bump <N> native dependencies` for several.

A dry run, which is the default, does steps 1 and 2, prints what it would update
and stops. Setting `DRY_RUN=0` enables steps 3 and 4.

`native/build-config.sh` is the only file the script edits or commits. Neither the
script nor the workflow merges the pull request.

The script exits 1 if a required tool is missing, if a `git` or `gh` command in
step 4 fails, or if any check failed. In the last case any pull request for the
other updates is still opened first, and a dry run that found updates exits 0
regardless. In every other case it exits 0.

## Check methods

Each library is registered by a `check_dependency` call in the "Main dependency
checking" part of the script. Those calls are the authoritative list of what is
checked and how:

```text
check_dependency <name> <current-version> <method> <method-arguments>
```

- `github <owner>/<repo>` uses the tag of the repository's latest GitHub release
  (`releases/latest` in the GitHub API) with any leading `v` removed. libarchive
  and zstd are two of the libraries checked this way.
- `web <url> <pattern>` fetches `<url>` and applies the extended regular
  expression `<pattern>` to it. The first capture group must be the version, and
  the highest match is used. The pattern is also substituted into a `sed`
  expression delimited by `|`, so it cannot contain that character. zlib and bzip2
  are two of the libraries checked this way.
- `gnome <pattern>` only works for libxml2, because its GNOME download directory
  is hard-coded. It finds the newest `<major>.<minor>` directory there and applies
  the `web` method to that directory.

## Running it locally

Run it from the repository root. The script expects `native/build-config.sh` under
`$GITHUB_WORKSPACE`, or under the current directory if that variable is not set.

```bash
# Dry run (the default): report available updates and change nothing
bash scripts/check-native-updates.sh

# Update native/build-config.sh, push a branch and open a pull request
DRY_RUN=0 bash scripts/check-native-updates.sh
```

- `curl`, `jq`, `gh` and `git` must be installed, even for a dry run. Applying
  updates also needs `shasum` or `sha256sum`, to compute the new checksums.
- A dry run still queries GitHub and the upstream download sites. It does not edit
  files, create a branch or open a pull request.
- Only `DRY_RUN=1`, or an unset or empty `DRY_RUN`, gives a dry run. Any other
  value applies the updates.
- With `DRY_RUN=0`, `gh` must be authenticated and allowed to open pull requests,
  `origin` must accept the push, and the new branch is left checked out.

## Scheduled workflow

`.github/workflows/check-native-dependencies.yml` runs the script on
`ubuntu-latest`:

- **Schedule.** Every Monday at 09:00 UTC. It can also be started by hand
  (`workflow_dispatch`).
- **Mode.** Scheduled runs use `DRY_RUN=0`. So do manual runs, unless their
  `dry_run` input is set to `true`.
- **Credentials.** The repository is checked out with the `WORKFLOW_TOKEN` secret,
  a personal access token, and the same secret is passed to the script as
  `GITHUB_TOKEN`. No step uses the workflow's default token, because a branch
  pushed or a pull request opened with that token does not start other workflows,
  so the build would never run on the update pull request. Commits are authored
  as `github-actions[bot]`.
- **Outcome.** The step summary only states that the check completed. Updates and
  failed checks are in the job log. The job fails whenever the script exits 1,
  which includes a run that opened a pull request but had a failed check.

## Adding a library

For a library called `foo`:

1. In `native/build-config.sh`, define `FOO_VERSION`, `FOO_SHA256` and a `FOO_URL`
   that is built from `FOO_VERSION`. Write the first two as `NAME="value"` on a
   line of their own: double quotes, nothing after the closing quote. The script
   reads and rewrites those lines with patterns anchored to that form.
2. In `check-native-updates.sh`, read the current version alongside the others and
   add a check after the existing ones, using one of the
   [check methods](#check-methods):

   ```bash
   CURRENT_FOO=$(read_current_version "FOO_VERSION")
   check_dependency "foo" "$CURRENT_FOO" "github" "<owner>/<repo>" || true
   ```

   The name passed to `check_dependency` decides which variables are used: it is
   upper-cased, `-` becomes `_`, and `_VERSION`, `_SHA256` and `_URL` are appended.
3. Do a dry run and check the latest version reported for `foo`.

## Limitations

- **mbedTLS stays on one release series.** GitHub's latest mbedTLS release is from
  a newer major line that libarchive cannot build against, so mbedTLS does not use
  the `github` method. It uses `web` with a pattern that only matches release
  tarballs of the pinned LTS series, so no other series is ever proposed. Moving
  to another series means editing that pattern.
- **lzokay is not checked.** lzokay, which provides lzop support, publishes no
  releases, so `native/build-config.sh` pins it to a commit (`LZOKAY_COMMIT`) and
  to the checksum of GitHub's archive of that commit (`LZOKAY_SHA256`). It has no
  `LZOKAY_VERSION` and no `check_dependency` call, so the script never looks at
  it. Check its commit history by hand. When the pin moves, also check that
  `native/lzo-shim/lzokay-bounds-checks.patch` still applies and is still needed.
- **Toolchains are not checked.** The script only prints a note: the musl, GCC and
  binutils versions in `native/build-config.sh` are for reference, and the real
  toolchains are Bootlin prebuilt releases (<https://toolchains.bootlin.com/>).
- **The repository is fixed.** The pull request lookup and creation always target
  `jas88/libarchive.net`, whatever `origin` points at.
