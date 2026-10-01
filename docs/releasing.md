# Release process

How a Maktaby release is cut, versioned, built and published.

Releases are **tag-driven**, and GitHub Actions does the rest — you never build or upload an installer by hand.

---

## 1. The one manual step: push a tag

```bash
# Beta — from develop
git checkout develop && git pull
git tag -a v1.0.33-beta.1 -m "1.0.33-beta.1"
git push origin v1.0.33-beta.1        # -> GitHub PRE-RELEASE

# Production — from main
git checkout main && git pull
git merge develop                      # or merge via a PR
git tag -a v1.0.33 -m "1.0.33"
git push origin v1.0.33                # -> GitHub RELEASE
```

| Tag format | Branch it must be cut from | Publishes as |
| --- | --- | --- |
| `v1.0.33-beta.1` | `develop` | GitHub **pre-release** |
| `v1.0.33` | `main` | GitHub **release** |

The **push of the tag** is what starts the build. Nothing else is required by hand.

### Why tags and not branches

[MinVer](https://github.com/adamralph/minver) already derives the assembly version from the nearest tag, so the tag is the single source of truth for both the version number and the build input. Building on every push to `develop` would produce an installer per commit and force a version to be invented for each one.

Because the prerelease label lives *in the tag name*, the installer filename, the assembly version, and the GitHub pre-release flag can never disagree — there is no second place where the channel is configured.

### The branch rule is enforced, not just documented

`.github/workflows/release.yml` verifies the tagged commit is reachable from the expected branch. A `-beta` tag that is not yet on `develop` (or a plain tag not on `main`) **fails the run** with an explicit error instead of quietly shipping to the wrong channel.

---

## 2. Automatic version numbers

Typing versions by hand is optional. `build_publish.ps1` derives the next version from the last tag and performs every step above in one go: clean-tree check, branch push, tag creation, tag push, then `build.ps1 -Action Publish`.

```powershell
# On develop: tags, pushes and builds the next beta
.\build_publish.ps1

# On main: tags, pushes and builds the next stable release
.\build_publish.ps1

# See what it would do, change nothing
.\build_publish.ps1 -DryRun

# Deliberate major/minor cut — overrides derivation entirely
.\build_publish.ps1 -Version 1.1.0-beta.1
.\build_publish.ps1 -Version 2.0.0
```

The branch picks the channel; the last tag decides where in that channel you are:

| Last tag | On `develop` | On `main` |
| --- | --- | --- |
| `1.0.32-beta` | `1.0.32-beta.1` | `1.0.32` |
| `1.0.32-beta.1` | `1.0.32-beta.2` | `1.0.32` |
| `1.0.32-beta.9` | `1.0.32-beta.10` | `1.0.32` |
| `1.0.32` (stable) | `1.0.33-beta.1` | `1.0.33` |

The key property: **a beta series holds its core still.** `1.0.32-beta`, `1.0.32-beta.1` … `1.0.32-beta.9` all target `1.0.32`, and that is exactly what `main` ships when it promotes. So a stable release is never a number that no beta was tested under.

Two details worth knowing:

- The counter is parsed as a number, so `beta.10` correctly follows `beta.9` — never lexicographic, where `beta.10` would sort *below* `beta.2`.
- If `HEAD` is already tagged with a label matching the branch's channel, the script **reuses** that tag instead of creating a duplicate.

> `build_publish.ps1` requires a clean working tree and only runs on `main` or `develop`. It still pushes the tag, because that push is what starts the workflow.

---

## 3. What the release workflow does

`.github/workflows/release.yml`, on a `v*` tag push:

1. **Classifies the tag** into a channel (`-beta.N` → pre-release, plain → stable) and derives the expected branch.
2. **Checks out the tag with full history** (`fetch-depth: 0`). MinVer needs the tags — without them every build reports `0.0.0-alpha.0.1`.
3. **Enforces the branch policy** via `git merge-base --is-ancestor`.
4. **Installs the toolchain**: .NET 10 SDK, and Inno Setup (preinstalled on the GitHub Windows images, with a Chocolatey fallback so a runner-image change cannot break a release).
5. **Runs `build.ps1 -Action Publish`**, which publishes `src/Maktaby/Maktaby.csproj` and compiles `installer.iss`.
6. **Collects the installer**, renames it to `Maktaby-<version>-x64-setup.exe`, and computes its SHA-256.
7. **Publishes the GitHub release** with auto-generated changelog notes and the runtime requirements.

### Two safeguards worth knowing about

- **The job fails if no installer was produced.** `build.ps1` only *warns* when `iscc` is missing and still exits `0`, so a silent "success" with no artifact would otherwise ship an empty release page. The workflow asserts the file exists and reports whether the problem was a missing compiler or an `OutputDir` mismatch.
- **Stable releases carry a version-less alias.** Each non-prerelease also publishes `Maktaby-latest-x64-setup.exe` (byte-identical), giving a permanent always-current URL:

  ```
  https://github.com/ezhassen/Maktaby/releases/latest/download/Maktaby-latest-x64-setup.exe
  ```

  Pre-releases do **not** get the alias, because that endpoint resolves to the newest non-prerelease — publishing it on a beta would make the URL serve an *older* build than the one the user just picked.

---

## 4. The CI workflow

`.github/workflows/ci.yml` runs on every push and pull request to `main` and `develop`. It only **compiles** — no installer, no release — so a broken branch is caught long before you try to tag it.

It builds `src/Maktaby/Maktaby.csproj` rather than the whole solution. Building a project automatically builds its `ProjectReference` closure, so `Maktaby`, `Maktaby.LiveWallpaper`, `Maktaby.WidgetSdk`, `Maktaby.Native` and `Maktaby.Shared` all still compile. Only `Maktaby.WidgetsCreator` — the standalone widget-editor tool, not on the app's runtime path — is skipped.

Because CI checks the same project the release publishes, a green CI genuinely means the tag will build.

---

## 5. Before the first public release

The installers are currently **unsigned**, which is what triggers most SmartScreen "unrecognized app" warnings on a freshly published setup. `installer.iss` has a `SignTool` line ready to uncomment, and a comment there documents the one-time configuration. Worth doing before the repo goes public.
