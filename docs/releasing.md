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

`.github/workflows/release.yml`, on a `v*` tag push, in three jobs:

**`validate`** — classifies the tag into a channel (`-beta.N` → pre-release, plain → stable), derives the expected branch, and enforces the branch policy via `git merge-base --is-ancestor`. It runs **once**, before anything is built, so all four build legs and the release notes cannot disagree about the version or the channel.

**`build`** — a four-way matrix, one leg per target:

| Target | Asset | Publish |
| --- | --- | --- |
| `win-x64` · lean | `Maktaby-<version>-x64-setup.exe` | `--self-contained false` |
| `win-x64` · full | `Maktaby-<version>-x64-setup-full.exe` | `--self-contained true` |
| `win-arm64` · lean | `Maktaby-<version>-arm64-setup.exe` | `--self-contained false` |
| `win-arm64` · full | `Maktaby-<version>-arm64-setup-full.exe` | `--self-contained true` |

Each leg installs the toolchain (.NET 10 SDK; Inno Setup with a Chocolatey fallback), runs `build.ps1 -Action Publish -Runtime <rid> -Flavor <flavor>`, and uploads its installer as an artifact. `fail-fast: false`, so one broken target still reports the status of the other three.

**`release`** — downloads all four artifacts, refuses to publish if any is missing, composes the notes (a download table with real sizes, a SHA-256 block for all four, requirements), merges the auto-generated changelog, and creates the release with `gh release create`.

### Where the changelog starts

The commit list is a `compare` range, and the **baseline is chosen by channel**, not by "the newest tag":

| Release | Baseline | Result |
| --- | --- | --- |
| `v1.0.33-beta.1` | the immediately preceding tag (`v1.0.32`) | everything developed since the last stable |
| `v1.0.33-beta.2` | `v1.0.33-beta.1` | only what changed in that beta |
| `v1.0.33` (stable) | the previous **stable** tag (`v1.0.32`) | the aggregate of the whole beta series |

A stable release is the promotion of a series, so its notes must read as the whole series at once — comparing against `v1.0.33-beta.N` (which *is* the newest tag) would hide everything the betas already contained. Tags come from the GitHub API (`gh api --paginate`, since a single page is capped at 100) and are ordered with `sort -V`, so `beta.10` correctly follows `beta.9`; the baseline is the tag immediately *below* the released tag in that order, never simply the highest one.

The four targets are declared **once**, in `TARGETS_JSON` at the top of the workflow. The build matrix and the notes composer both read it, so the table in the release notes cannot drift from what was actually built.

### Two flavours, and why both

`lean` is the framework-dependent publish: a fraction of the download size, but the machine needs the matching .NET Desktop Runtime. `full` is self-contained — roughly four times the size and nothing to install first. The lean asset keeps its original name, so existing download links, the site's older cached markup and the parked winget manifests all keep resolving.

The **in-app updater** has to know which one a user is running, because silently upgrading a lean install to the full one would replace a working setup with an 80 MB one the user never chose. `Core/Services/InstallFlavorInfo` detects it from how the process was published (whether `System.Private.CoreLib` was loaded from beside the app, falling back to `includedFrameworks` in the `.runtimeconfig.json`), and `UpdateService.SelectInstaller` prefers the matching arch **and** flavour, falling back to the other flavour only when the release ships just one.

### Three safeguards worth knowing about

- **The job fails if no installer was produced.** `build.ps1` only *warns* when `iscc` is missing and still exits `0`, so a silent "success" with no artifact would otherwise ship an empty release page. Each leg asserts the exact expected filename exists and reports whether the problem was a missing compiler, an `OutputDir` mismatch or a rename. `build.ps1` itself also checks `iscc`'s exit code *and* that the file it claims to have written is there.
- **The release is all-or-nothing.** `build.ps1` writes the release asset name into the file name itself, so the workflow looks the installer up by exact path rather than globbing "the newest `.exe`" — which on a reused runner could pick up a previous build. The release job then re-checks that all four expected names are present before it publishes anything.
- **There is no version-less `latest` alias.** Earlier releases also published `Maktaby-latest-x64-setup.exe`; it is gone. `/releases/latest/download/` resolves to the newest *non-prerelease*, so during a beta series the alias pointed at an older build than the one the user had deliberately chosen. Download from the release page, or let the in-app updater pick — it already knows the visitor's architecture and flavour.

### If a release job fails

- **One matrix leg failed.** The other three still report. Re-run the workflow with `workflow_dispatch` and the same tag; nothing is published until all four succeed, so a failed run leaves no partial release.
- **`iscc` is missing or too old.** The workflow fails with an explicit message. The bilingual installer needs `Languages\Arabic.isl`, which ships with **Inno Setup 6.5.0+**. On a dev machine, upgrade Inno Setup, or vendor the translation as `installer\languages\Arabic.isl` and point the `[Languages]` entry at it.

---

## 4. The CI workflow

`.github/workflows/ci.yml` runs on every push and pull request to `main` and `develop`. It only **compiles** — no installer, no release — so a broken branch is caught long before you try to tag it.

It has two jobs:

- **`build`** — `src/Maktaby/Maktaby.csproj`, default RID. Building a project automatically builds its `ProjectReference` closure, so `Maktaby`, `Maktaby.LiveWallpaper`, `Maktaby.WidgetSdk`, `Maktaby.Native` and `Maktaby.Shared` all still compile. Only `Maktaby.WidgetsCreator` — the standalone widget-editor tool, not on the app's runtime path — is skipped.
- **`build-arm64`** — the same project with `--runtime win-arm64`. Each RID is a separate restore with its own runtime pack and its own resolved native assets, so a plain build only exercises x64; an ARM64-specific break would otherwise first appear on a tag, where it is most expensive.

Because CI checks the same project the release publishes, a green CI genuinely means the tag will build.

---

## 5. Before the first public release

The installers are currently **unsigned**, which is what triggers most SmartScreen "unrecognized app" warnings on a freshly published setup. `installer.iss` has a `SignTool` line ready to uncomment, and a comment there documents the one-time configuration. Worth doing before the repo goes public.

---

## 6. The installer is bilingual

`installer.iss` declares a `[Languages]` section:

```ini
Name: "en"; MessagesFile: "compiler:Default.isl,{#MyRepoRoot}\installer\languages\EnglishCustom.isl"
Name: "ar"; MessagesFile: "compiler:Languages\Arabic.isl,{#MyRepoRoot}\installer\languages\ArabicCustom.isl"
```

English is listed first and stays the default when nothing matches the user's UI language; `Arabic.isl` is Inno Setup's own official translation and already carries `RightToLeft=yes`, so the whole wizard mirrors without extra work. The two files next to it supply the strings the wizard cannot translate itself — the "Launch Maktaby" and "Start Maktaby on Windows startup" checkboxes are built in `[Code]` with `TNewCheckBox` and pull their caption from `{cm:...}`.

Two traps worth knowing before editing this:

- **A relative `MessagesFile` resolves against `SourceDir`, not against `installer.iss`.** That is why the paths go through `{#MyRepoRoot}` from the generated include.
- **Anything in the script's own `[CustomMessages]` section overrides the message files**, for every language. That section is deliberately empty so it cannot silently undo the Arabic translations.

### Silent installs and the Windows startup entry

The in-app updater runs the installer with `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /MAKTABY-UPDATE`. The `/MAKTABY-UPDATE` flag matters: **Inno Setup 6 calls `InitializeWizard` for silent installs too**, so the same two checkboxes exist with their defaults, and the startup checkbox is ticked. Without the flag, every update would re-run `--enable-startup` and switch Windows startup back on for a user who had deliberately turned it off — silently, through a path they never chose. `IsUpdateRun` in `installer.iss` is what suppresses it.
