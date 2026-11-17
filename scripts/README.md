# Native Dependency Update Checker

`check-native-updates.sh` looks upstream for newer releases of native libraries
whose versions are pinned in `native/build-config.sh`. When it finds any, it
reports them and, unless it is a dry run, updates the pins it can and opens one
pull request for them. `.github/workflows/check-native-dependencies.yml` runs it
weekly.

`native/build-config.sh` is the source of truth for the pinned versions and their
SHA256 checksums, so neither is repeated here.

## How it works

1. **Look for an open update pull request.** The script asks `gh` for open pull
   requests in `jas88/libarchive.net` that carry the `native` label and whose
   branch name starts with `dependabot/native/`. If there is one, it lists it and
   exits without checking anything, dry run or not. Merge or close that pull
   request to get a new one. If the lookup itself fails (for example, `gh` is not
   authenticated), a dry run prints a warning and carries on. Otherwise the script
   stops before changing anything, so that it cannot open a second pull request.
2. **Compare versions.** It reads each current version from
   `native/build-config.sh` and finds the latest upstream version with one of the
   [check methods](#check-methods). Only a plain dotted number of at most 32
   characters (`^[0-9]+(\.[0-9]+)*$`, after the `github` method has removed a
   leading `v`) is accepted as a version, because versions are written into a file
   that this script and every build source as shell code; anything else is
   reported as a failed check ("unexpected version string", escaped for the log)
   and is never compared or written. A version that sorts higher under `sort -V`
   is an update. A check that finds no version (network error or pattern mismatch)
   is reported as failed, and the remaining checks still run.
3. **Update the pins.** For each update it rewrites the `<NAME>_VERSION` line
   (`<NAME>` being the library's name in upper case) in a copy of that file,
   downloads the new tarball from the `<NAME>_URL` that the copy sets, and
   rewrites the copy's `<NAME>_SHA256` line with that tarball's checksum. Only then
   does the copy replace the file, in one step: it is written to a temporary file
   in `native/`, which takes the file's mode and is renamed over it with `mv -f`.
   If any of this fails (a line cannot be rewritten, `<NAME>_URL` is not set, the
   download fails or no SHA256 can be computed), the file keeps that library's
   current version and checksum, and the library is left out of the pull request
   and reported as a failed check. If the replacement itself fails, the temporary
   file is removed and the script stops before step 4 with exit status 1, leaving
   the file as it was before that update (updates applied earlier in the run stay
   in it).
4. **Open a pull request.** If at least one update was applied, it creates the
   branch `dependabot/native/<YYYYMMDD>-updates` from the commit that is checked
   out, commits `native/build-config.sh`, pushes the branch to `origin` and opens
   a pull request in `jas88/libarchive.net` with the labels `dependencies` and
   `native`, which must already exist. All updates applied in one run share that
   pull request, and only they appear in its title, body and commit. Its title is
   `Bump native <name> from <old> to <new>` for one update and
   `Bump <N> native dependencies` for several. If no update could be applied, it
   creates no branch, commit or pull request.

A dry run, which is the default, does steps 1 and 2, prints what it would update
and stops. Setting `DRY_RUN=0` enables steps 3 and 4.

`native/build-config.sh` is the only file the script changes or commits; its
temporary file in `native/` exists only while that file is being replaced. Neither
the script nor the workflow merges the pull request.

The script exits 1 if a required tool is missing, if it cannot create or write its
temporary files, if it cannot replace `native/build-config.sh` (step 3), if the pull
request lookup in step 1 fails outside a dry run, if a `git` or `gh` command in
step 4 fails, or if any check failed, an update that could not be applied included.
In the last case any pull request for the other updates is still opened first, and
a dry run that found updates exits 0 regardless. A run stopped by SIGHUP, SIGINT or
SIGTERM removes its temporary files and exits 129, 130 or 143. In every other case
it exits 0.

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
  the `web` method to that directory. The directory name must pass the same
  version check.

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
  Without either, every update fails in step 3.
- A dry run still queries GitHub and the upstream download sites. It does not edit
  files, create a branch or open a pull request. It also works without `gh`
  authentication, after the warning from step 1.
- If `GITHUB_TOKEN` is set and not empty, or failing that `GH_TOKEN`, the version
  checks whose URL is on `https://api.github.com/` (every `github` check, and the
  `web` check for mbedTLS) send it as a bearer token, which avoids the low rate
  limit for unauthenticated requests. Those requests follow no redirects. None of
  the script's other requests, tarball downloads included, carries the token, and
  it is never printed. (`gh` reads the same variables for its own requests.)
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
  `GITHUB_TOKEN`, for `gh` and the GitHub API requests. No step uses the
  workflow's default token, because a branch pushed or a pull request opened with
  that token does not start other workflows, so the build would never run on the
  update pull request. The workflow therefore grants the default token no
  permissions. Commits are authored as `github-actions[bot]`.
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
- **Toolchains are not checked.** The Linux builds use Bootlin prebuilt toolchains
  (<https://toolchains.bootlin.com/>), selected by `BOOTLIN_RELEASE` in
  `native/build-config.sh`. The script does not check that release and only prints
  a note saying so. Renovate is configured to keep it current
  (`.github/renovate.json`).
- **Authenticated requests do not follow redirects.** With a token, a GitHub API
  answer that redirects, as it does for a renamed repository, is reported as a
  failed check. Update the repository named in the script to fix it.
- **The repository is fixed.** The pull request lookup and creation always target
  `jas88/libarchive.net`, whatever `origin` points at.
