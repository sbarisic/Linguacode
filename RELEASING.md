# Manual releases and updates

Linguacode uses Velopack 1.2.161 for per-user Windows x64 installation and updates.
There is no CI/CD release pipeline. Work and commits stay on `main`.

## Prepare a release locally

Close development copies of the app. Commit all tracked changes on `main`, write
release notes, then run from the repository:

```powershell
.\eng\Release.ps1 -Version 0.1.0 -ReleaseNotes path/to/release-notes.md
```

The command restores the pinned packaging tool, rebuilds the solution into fresh
staging, runs the automated suite, and packages the complete self-contained app.
Optional model tests are excluded from this release command; run model acceptance
separately when changing the engines. Files are written to
`artifacts/releases/<version>`. Nothing is uploaded, tagged, pushed, or published.

Use this script rather than `dotnet run`: the latter puts child processes in a
Windows job that prevents the audio-service smoke test from starting its detached
service. The script builds the release tool and executes its DLL directly.

Versions must be stable `major.minor.patch` values. The command rejects dirty
tracked changes, another branch, an existing local tag, and an existing output
directory. Verify that the version is also unused on GitHub. Never replace a
published package with different contents under the same version.

Each release contains the installer, full update package, `releases.win-x64.json`,
asset metadata, release notes, build commit, checksums, and an upload checklist.
Native engines and the update helper are included; models and user data are not.
Full packages are used; delta updates and portable releases are not supported yet.

## Publish manually

1. Test the installer and recording on your machine.
2. Open https://github.com/sbarisic/Linguacode/releases and create a draft release.
3. Use tag `v<version>` at the exact commit in `build-info.json`.
4. Attach every asset named in `UPLOAD-CHECKLIST.txt`, including `SHA256SUMS.txt`.
5. Add the release notes, leave prerelease disabled, then publish and mark latest.

Draft releases remain invisible to the updater. A Git push alone does not offer
an update. Public downloads need no GitHub token in the application. See
[Velopack GitHub distribution](https://docs.velopack.io/distributing/self-hosting).

## Installation and user controls

The package ID is `Linguacode.App`; files install under
`%LOCALAPPDATA%\Linguacode.App`, with a Start menu shortcut. Application data stays
under `%LOCALAPPDATA%\Linguacode`, or the directory selected with `--data-dir`.
Do not put user data inside the application installation. Uninstalling the app
does not remove its separate recordings, models, or settings.

Configuration → Updates shows version, release notes, size, and progress. Automatic
checks are enabled by default and run at most once per day; Check now bypasses the
interval and can show skipped versions. Downloads require a click. A downloaded
update is never applied during ordinary startup or exit: use Restart and update.

Updating is refused while work is active or another installed window or command
is running. Idle audio services must confirm shutdown. An installation-wide gate
blocks new installed processes until replacement finishes. Restart preserves the
data-directory argument. Developer builds do not self-update.

Download verification uses the release's SHA-256 and size, and the package is
checked again before installation. HTTPS/GitHub account control is the initial
distribution trust boundary; unsigned packages do not establish publisher identity.
Initial installers are unsigned and Windows may show an unknown-publisher warning.
To sign future releases, configure a trusted certificate locally and set
`LINGUACODE_SIGN_PARAMS` to the signtool parameters expected by Velopack. Do not
commit credentials or signing secrets.

There is no custom recovery, rollback, or startup watchdog. Errors are displayed
in the app; external installer failures are saved to
`<data-directory>/updates/apply-result.json` and shown on the next manual launch.
Repair a failed release manually or publish a newer version.

## Local update acceptance

No VM or public release is needed. Build two versions with
`-p:EnableUpdateSmoke=true`, separate `-p:DesktopOutputPath=<staging>/` directories,
and explicit `Version`/`InformationalVersion` values. Package each with the pinned
`dotnet vpk pack` tool using package ID `Linguacode.App`, main executable
`Linguacode.exe`, channel/runtime `win-x64`, and `--delta None`.

```powershell
dotnet run --project tests/Linguacode.UpdateAcceptance -c Release -- <new-test-directory> <first-Setup.exe> <second-feed-directory>
```

This opt-in harness refuses to run if Linguacode.App is already installed. It uses
an isolated install and data directory, verifies download and explicit restart,
checks that normal startup defers installation and data survives, then uninstalls
only the test copy. Evidence stays in the test directory. Test-only startup probes
are excluded from normal release builds. Do not publish smoke-test packages.

Physical capture, listening, and final installer acceptance remain user checks.
