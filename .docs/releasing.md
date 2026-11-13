# Releasing

Making a new version: the steps, releasing from VS Code, the release workflow, code signing, setting up GitHub, the build workflow, the portable exe and the installer.

## Steps

There is no version number to raise: the release's tag is its version. A release
build gets it from the tag (`v1.7.2` → 1.7.2), and every other build from the
newest version tag in git that the commit includes - after 1.7.1 is released, a
local build, the portable exe's name, the installer and Settings → About say 1.7.1
until 1.7.2 is tagged ([`DnnManager.csproj`](../DnnManager.csproj), target
`VersionFromGitTag`; without git or a tag it is 0.0.0, with a build warning).

1. In [`CHANGELOG.md`](../CHANGELOG.md), turn **Unreleased** into
   `## vX.Y.Z` (with an *Upgrading* note when settings or behaviour change).
2. Run the fast tests and the integration tests ([testing.md](testing.md)).
3. Write the release notes as `.docs/release-notes/vX.Y.Z.md` - the file's name is
   the release's tag and title. Every release uses the structure of
   [v1.6.0](release-notes/v1.6.0.md): a bold summary, *Highlights*, *Other changes*,
   *Upgrading*, *Tested*. With Claude Code, ask for "the release notes for X.Y.Z" -
   the `release-notes` skill (`.claude/skills/release-notes`) writes them from the
   changelog, the commits and the test results. By hand, start from the draft:
   `.github\scripts\release-notes.ps1 -Version X.Y.Z -OutFile .docs\release-notes\vX.Y.Z.md`.
   The notes are built into the exe (What's new after an update shows them), so
   they must be committed before the release is built.
4. Commit and push, then run the task **Release** (see
   [Release from VS Code](#release-from-vs-code)). It pushes the version tag, and
   GitHub's [release workflow](#the-release-workflow) builds the release from that
   tag: `DnnManager-X.Y.Z-x64.exe` (the portable exe), `DnnManager_Setup-X.Y.Z-x64.exe`, the
   [cross-platform app](cross-platform.md)'s packages for macOS and Linux
   (`DnnManager-X.Y.Z-macos-arm64`, `-macos-x64`, `-linux-x64`, `-linux-arm64`: the
   program itself, one file each, no zip) and `SHA256SUMS.txt`. It signs the exes once signing is
   set up, tries them, and puts everything on a draft release. Nothing is tested
   twice: [CI](#the-build-workflow) tests every commit you push, and the release
   workflow first waits for CI on the tagged commit and builds only once it has
   passed.
5. **Approve the job *Publish the release*** on the run's page on GitHub (the
   `publish` environment's required reviewer). It checks the tag and the files
   again and publishes the release. Every running DNN Manager then offers it
   with its **Update** button (see [The in-app update](#the-in-app-update)).

## Release from VS Code

**Ctrl+Shift+B → Release** (or **Terminal → Run Task…**) asks four
questions in VS Code's picker (the notes, the commit, the label, the platforms), then runs
[`.github/scripts/publish-release.ps1`](../.github/scripts/publish-release.ps1) in
the terminal. The pickers are filled fresh each time - from `.docs/release-notes`
and from GitHub - by the extension
[Tasks Shell Input](https://marketplace.visualstudio.com/items?itemName=augustocdias.tasks-shell-input)
(`augustocdias.tasks-shell-input`); VS Code offers to install it, as it's in
`.vscode/extensions.json`.

1. **Pick the release notes** from `.docs/release-notes`. Files without a tag come
   first, marked *next release*; released ones are marked *already released*.
   `v1.7.0.md` makes the tag and the release title `v1.7.0` (`v1.7.0-rc.1.md`
   makes a pre-release).
2. **Pick the commit** from the 20 newest on GitHub (`origin/<current branch>`,
   fetched first) - the newest is on top. Push before you release: a commit that
   is only on this PC is refused, because CI never tested it.
3. It stops when the tag is on GitHub already (or here, on another commit), when
   GitHub has published that version, or when the commit doesn't have its release
   notes or the release workflow (`.github/workflows/release.yml`). The release is
   built from that commit, and its notes are built into the exe.
4. **It tags the commit and pushes the tag at once**: no tests run on your PC and
   nothing waits there. Your terminal is free again within seconds.
5. **On GitHub, the release waits for CI.** The release workflow's first job
   ([`wait-for-ci.ps1`](../.github/scripts/wait-for-ci.ps1)) waits for CI on that
   commit, up to 90 minutes. It builds only once CI has passed. If CI fails or is
   cancelled, the release stops there, naming the failed jobs, and nothing is built.
   Nothing built on this PC is uploaded: the release workflow builds, signs (once
   [code signing](#code-signing) is set up), attests, tries and drafts it.
6. **Approve *Publish the release*** on the run's page once the draft is ready.

The task also asks two more questions:

- **Release label**, as GitHub names it:
  - **Latest**: the latest release. Every DNN Manager's **Update** button offers it.
  - **Pre-release**: not ready for everyone. Only people who download it from the release page get it.
  - **None**: published, but not the latest. Nobody is offered it.
- **Platforms**, tick one or more:
  - **Windows**: Setup and the portable exe.
  - **macOS** and **Linux**: the [cross-platform app](cross-platform.md)'s packages.

The script refuses two combinations, before anything is tagged:
- A version with a suffix (`2.0.0-rc1`) as **Latest**. The **Update** button would offer it to everyone.
- **Latest** without **Windows**. The **Update** button downloads the Windows files.

Both choices go into the tag's message (`Release-Label:` and `Release-Platforms:`
lines). The [release workflow](#the-release-workflow) reads them there, builds
only what you ticked and publishes the release with that label. A tag made by
hand, without those lines, builds every platform, and its version decides the
label: `v2.0.0-rc1` becomes a pre-release, `v2.0.0` the latest.

**A pre-release**, such as `2.0.0-rc1`: write its notes as
`.docs/release-notes/v2.0.0-rc1.md`, then run **Release** and choose
**Pre-release**. Release the final version (`v2.0.0.md`) the same way with
**Latest**: everyone is offered it, including those on the pre-release.

Outside VS Code, `-Label latest|prerelease|none -Platforms windows,macos,linux`
answers these two questions; without them, the script asks in the terminal.

The only credential it uses is the one Git pushes the tag with (see
[Set up GitHub](#set-up-github)). It asks GitHub's public API, without signing in,
whether the version is published
([`ReleaseCommon.psm1`](../.github/scripts/ReleaseCommon.psm1), which it shares
with the redo script). Outside VS Code,
`.github\scripts\publish-release.ps1` asks the same questions in the terminal, and
lists the recent commits to pick from; `-NotesFile .docs\release-notes\v1.7.0.md
-Commit latest` (or a hash) answers them.

The release's notes are `.docs/release-notes/vX.Y.Z.md` as
[`.github/scripts/release-notes.ps1`](../.github/scripts/release-notes.ps1) gives
them to GitHub: its relative links pointed at the tag's files. Without that file it
writes a draft in the same structure, from the version's `CHANGELOG.md` entry
(*Added* → *Highlights*, *Fixed* → `Fixed:` lines in *Other changes*, *Upgrading*,
*Tested*) or, without an entry, from the commit subjects since the previous tag
(`new:` → *Highlights*, `fix:` and `update:` → *Other changes*). Preview them with
`.github\scripts\release-notes.ps1 -Version 1.7.0 -OutFile notes.md`.

## The .NET SDK

[`global.json`](../global.json) names one SDK exactly, and that SDK decides the
versions of the packages it brings itself: the Native AOT compiler the launcher
restores (`Microsoft.DotNet.ILCompiler`) and the trimmer
(`Microsoft.NET.ILLink.Tasks`), which the lock files record. .NET gets a security
patch about once a month; a few days later GitHub's advisory makes the older
compiler a known vulnerability, and every restore - CI, the release workflow and
**Release** - fails with *NU1903*.

The VS Code task **Update .NET SDK**
([`.github/scripts/update-sdk.ps1`](../.github/scripts/update-sdk.ps1)) does the
whole move:

1. Asks Microsoft's release list for the newest SDK of `global.json`'s channel
   (`10.0`) and installs it with winget when this PC doesn't have it (Windows asks
   for administrator rights).
2. Puts the newest installed SDK of that channel in `global.json`.
3. Updates the lock files with it (`dotnet restore --force-evaluate`, the app's,
   the tests' and the launcher's).
4. Lists any package with a known vulnerability and the files that changed.

Then run the fast tests, commit and push, and release that commit. Run with
nothing new, it changes nothing. CI and the release workflow install the SDK
`global.json` names, so they follow the commit. **Release** says when
the commit you pick builds with an SDK older than the newest released one, and
what an *NU1903* from the launcher's restore means.

A vulnerability the task still lists is in one of the project's own packages (a
`PackageReference` in a `.csproj`, such as `Microsoft.Data.Sqlite`): raise its
version there and run the task again.

## Release a version again

When a release's workflow failed, or you didn't approve its draft and want more
changes in it, commit and push them, then run **Release** again and pick the same
version. The version picker shows each version as GitHub has it:

- **next release** / **not released**: no tag yet. It is released.
- **tagged, not published**: the tag is on GitHub, but the release isn't
  published. It is **released again** from the commit you pick: the old tag is
  deleted on GitHub and made again there, and the next run reuses the draft the
  earlier one left. The label and platforms are the ones you pick this time.
- **published pre-release**: **replaced** by the commit you pick. No **Update**
  button offers a pre-release, so only people who downloaded it by hand have it.
  The tag moves at once (its message says `Release-Replace: true`), and once CI has
  passed and everything is built, the release workflow deletes the published
  pre-release on GitHub and drafts the new build in its place.
- **published (Latest or None)**: refused. Every DNN Manager updates only to a
  *newer* version, so whoever installed it would never be offered the new build.
  Write the notes for the next version (the picker names it: `v1.8.7` → `v1.8.8`)
  and release that - see [A bad release](#a-bad-release). With GitHub's immutable
  releases switched on, a published pre-release's tag can't move either: then
  release the next version (`v2.0.0-rc2`) instead.

Released again, it is also refused while a release run on the tag hasn't ended
(queued, running, or waiting for *Publish the release*): wait for it, or reject
the publish job, first. And when GitHub can't be asked whether the version is
published, nothing is changed. A tag only on this PC (never pushed) is simply
replaced.

## A bad release

A release that turns out broken once it is published - people may have updated to
it already:

1. **Stop new updates to it at once:** on GitHub, edit the release and tick *Set as
   a pre-release*. DNN Manager and Setup offer only the latest release that isn't a
   pre-release, so nobody else gets it; its files stay for those who have it.
2. **Fix it as the next version** (`vX.Y.Z+1`) - a revert is enough - with the
   changelog and release notes saying what was wrong and that it is fixed, and
   release it as usual. Those who updated to the bad one are offered the fix;
   nobody is offered an older version, and a published version is never redone.
3. **A release that wasn't yours** (the GitHub credential or this PC
   compromised): revoke the credential Git uses (GitHub → Settings → Developer
   settings → Personal access tokens, or Applications), delete the release, and
   release a fixed version from a PC you trust; say what happened in its notes.
   A release the workflow didn't build has no attestation from it
   (`gh attestation verify <file> --repo Albadit/DnnManager.NET` fails).

## The release workflow

[`.github/workflows/release.yml`](../.github/workflows/release.yml) ("Release") runs
when a tag `vX.Y.Z` (or `vX.Y.Z-suffix`) is pushed - one run per tag at a time,
never cancelled half way. Its jobs run in this order:

```mermaid
flowchart LR
    plan["plan<br/>wait for CI, then label and platforms from the tag"] --> build["build<br/>Windows: publish"]
    plan --> macos["macOS<br/>executables"]
    plan --> linux["Linux<br/>tar.gz files"]
    build --> sign["sign<br/>sign, Setup, SHA256SUMS, attest"]
    macos --> sign
    linux --> sign
    sign --> smoke["smoke<br/>install, start, uninstall"]
    sign --> draft["draft<br/>upload, check digests"]
    smoke --> draft
    draft --> publish["publish<br/>approved by a reviewer"]
```

A platform you didn't tick is skipped: its job doesn't run, and the jobs after it
go on without it. Without **Windows**, there is no smoke test either.

- **Plan** - first [`wait-for-ci.ps1`](../.github/scripts/wait-for-ci.ps1) waits
  for [CI](#the-build-workflow) on the tagged commit, asking GitHub every 30
  seconds, up to 90 minutes. It goes on only once CI has passed. If CI fails or is
  cancelled, or no CI run starts within 10 minutes (a commit never pushed to a
  branch), the release stops there and nothing is built. Then
  [`release-plan.ps1`](../.github/scripts/release-plan.ps1) reads the
  tag's message from GitHub's API and sets what the run builds and how the release
  is labelled. A label that can't be released, such as a `-rc` version as Latest,
  fails here, before anything is built.
- **Windows** (build, on the `windows-2025` image) - checks the runner has Visual Studio's
  C++ build tools (the launcher's Native AOT needs them), restores in locked mode
  (the app's tests and the launcher), stamps the tag's version into the manifest,
  publishes the portable exe, and publishes the app and the launcher for Setup
  (`build.ps1 -PublishOnly`) - all unsigned. It runs the packages' code, so it gets
  no OpenID Connect token: it may only read the repository. No tests run in the
  release workflow - [CI](#the-build-workflow) ran them on the commit, and the
  plan job waited for its result.
- **macOS** and **Linux** - two jobs of their own, beside Windows; each calls the
  [Desktop workflow](../.github/workflows/desktop.yml) with it. That workflow
  publishes the [cross-platform app](cross-platform.md) on a `macos-15` runner for
  each Mac (signed ad hoc with `codesign -s -`, which is free; not notarized) and on
  `ubuntu-24.04` for each Linux, and packages it with
  [`package.ps1`](../src/DnnManager.Desktop/Packaging/package.ps1). Like the build,
  it may only read the repository.
- **Sign, package and attest** (environment `release`) - takes the build's files
  and the macOS and Linux packages;
  it restores no package and runs no test. When [code signing](#code-signing) is
  set up, it signs in to Azure just before the first file is signed, signs the
  portable exe, then builds Setup with the pinned Inno Setup (`build.ps1
  -SkipPublish -PinnedInno -SignScript …`), which signs `DnnManager.exe` and the
  launcher before they go in and has Inno sign Setup and its uninstaller; then it
  signs out of Azure. It checks that both exes and the launcher report the
  version (and are signed), writes `SHA256SUMS.txt` and the notes, and attests
  the provenance of both exes and the macOS and Linux packages
  (`actions/attest-build-provenance`, signed by GitHub through Sigstore): which
  workflow built them, from which tag and commit.
- **Try the files** (smoke) - on a fresh runner,
  [`smoke-test.ps1`](../.github/scripts/smoke-test.ps1) installs Setup silently
  into a temporary folder, checks `DnnManager.exe` and the launcher (a Native AOT
  build, no DLL beside it) report the version, starts DNN Manager through the
  launcher and waits for its window to stay up, uninstalls, then starts the
  portable exe the same way. A release whose files don't start isn't offered.
- **Draft the release** - only once CI, the signing and the smoke test have
  passed. [`upload-release.ps1 -Stage Draft`](../.github/scripts/upload-release.ps1)
  checks the files against `SHA256SUMS.txt` and creates the release as a draft
  for the commit the workflow built (`target_commitish` = `GITHUB_SHA`), with the
  notes and the files' SHA-256 under them. If a failed run left a draft for the
  tag, it reuses that draft and replaces its files. It uploads the files and
  checks each one's size and GitHub's own SHA-256 of it (its `digest`, what every
  DNN Manager checks an update against). A published release of the tag fails
  the job and changes nothing.
- **Publish the release** - in the environment `publish`: it waits until a
  required reviewer approves it on the run's page. Then `upload-release.ps1
  -Stage Publish` checks again that the tag still points at the commit that was
  built (`GITHUB_SHA`), that each file on the draft has the digest in
  `SHA256SUMS.txt` and that the draft has no other file, and publishes it with the
  label you chose. **Latest** is set only when the version is newer than the
  latest one, so a fix to an older line doesn't become what every DNN Manager is
  offered. **Pre-release** and **None** are never the latest.

A failure anywhere leaves at most a draft, which nobody is offered; a pushed tag
alone never publishes anything. Each job has only the permissions it needs. CI,
build and smoke only read the repository. Only the sign job may get an OpenID
Connect token and write attestations - for the attestation's certificate and for
Azure's sign-in when signing. Only the draft and publish jobs may write to the
repository, and that is for the release. The workflow's own `GITHUB_TOKEN` is its
only GitHub credential - no personal token is stored in the repository. Its
actions are pinned to commits, like CI's, check-outs leave no token in
`.git/config`, and `${{ }}` values reach its scripts only through environment
variables.

**Check a downloaded file** came from the workflow:
`gh attestation verify DnnManager_Setup-1.9.0-x64.exe --repo Albadit/DnnManager.NET`
shows the workflow, tag and commit that built it. Releases up to 1.8.1 were built
on the publisher's PC and have no attestation.

## Code signing

The exes aren't signed until a code-signing service is set up. The workflow is
ready for [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/)
(formerly Trusted Signing). **Until its variables are set, signing is off** and
every release is built unsigned. Once they are, the sign job signs in to Azure
with OpenID Connect - no secret is stored - right before the first file is
signed, and [`.github/scripts/sign.ps1`](../.github/scripts/sign.ps1) signs with
`signtool` and the Artifact Signing client (a pinned NuGet package, checked
against its SHA-512), timestamped. It signs the portable exe, the published
`DnnManager.exe` and the launcher before they go into Setup, and - through Inno's
`SignTool` - Setup and its uninstaller. With the variables set, a file that comes
out unsigned fails the run instead of being published; so does a half set-up
(some variables missing). Set the variable `REQUIRE_SIGNING` to `true` once
signing works: from then on a run without the signing variables fails rather
than release unsigned files.

To set it up:

1. **In Azure**: an Artifact Signing account and a certificate profile (Public
   Trust, after the publisher's identity validation). Note the account's endpoint
   (`https://<region>.codesigning.azure.net`), its name and the profile's name.
2. **In Microsoft Entra ID**: an app registration with a *federated credential*
   for GitHub Actions - organization `Albadit`, repository `DnnManager.NET`, entity
   *Environment*, name `release` (subject
   `repo:Albadit/DnnManager.NET:environment:release`). Give it the role
   *Artifact Signing Certificate Profile Signer* on the certificate profile.
3. **On GitHub, Settings → Environments**: open `release` (the first run makes it) or
   create it, and under *Deployment branches and tags* allow only the tag pattern
   `v*`. Then only a release run can get the Azure token.
4. **Settings → Secrets and variables → Actions → Variables** (of the repository or
   of the `release` environment): `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`,
   `AZURE_SUBSCRIPTION_ID`, `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`
   and `ARTIFACT_SIGNING_PROFILE`. They are identifiers, not secrets. Then
   `REQUIRE_SIGNING` = `true`.

Nothing else to change for the installer: `build.ps1 -SignScript <script>` defines
Inno's sign tool `dnnsign` and passes `/DSignToolName=dnnsign`, and `DnnManager.iss`
then sets `SignTool` and `SignedUninstaller=yes`. Inno refuses a file the sign tool
left unsigned, and `build.ps1` checks Setup's signature once more.

## Set up GitHub

Do these once, before the first release with the workflow, in the repository's
**Settings**:

- **Actions → General**: *Workflow permissions* - *Read repository contents and
  packages permissions* (each job asks for more itself), and *Allow GitHub Actions
  to create and approve pull requests* off. Under *Actions permissions*, tick
  *Require actions to be pinned to a full-length commit SHA*.
- **General → Releases**: turn on *release immutability*. Once a release is published,
  its files and tag can't change any more - a draft still can, which is how the
  workflow uploads them. This is what the scripts' refusal to redo a published
  version relies on.
- **Rules → Rulesets**:
  - a *branch* ruleset for the default branch (`main`): *Restrict deletions*,
    *Block force pushes* and *Require status checks to pass* (CI's **Test** and
    **Build**);
  - a *tag* ruleset for `refs/tags/v*`: *Restrict creations*, *Restrict updates*,
    *Restrict deletions* and *Block force pushes*.

  Add the *Repository admin* role to both bypass lists: **Release** creates
  the tag, and deletes it and makes it again to release an unpublished version again. Nobody else can push or move a version tag, so nobody else can
  start a release.
- **Environments** (the first release run makes both; set them up before it):
  - `publish` - under *Required reviewers* add yourself, and under *Deployment
    branches and tags* allow only the tag pattern `v*`. **Without a required reviewer the
    publish job runs as soon as the draft is ready** - this is the step that makes
    a person approve every release.
  - `release` - *Deployment branches and tags*: only `v*` (see
    [Code signing](#code-signing)).
- **Secrets and variables → Actions → Variables**: `REQUIRE_SIGNING` (`true`
  once signing is set up), and `CI_MAX_SKIPPED_TESTS` - the number of skipped
  integration tests the build workflow accepts ([below](#the-build-workflow)).
- **The Git credential on this PC** is all that releases need here: it pushes the
  branch and the tag, and no script reads it from Git or calls the API with it.
  Make it a *fine-grained personal access token* for this repository only
  (GitHub → Settings → Developer settings → Fine-grained tokens): *Contents: Read and
  write*, plus *Workflows: Read and write* only while you push changes to
  `.github/workflows`. Store it in Git Credential Manager in place of a broader
  sign-in, and give it an expiry date.

## The build workflow

[`.github/workflows/ci.yml`](../.github/workflows/ci.yml) ("CI") runs on every commit
pushed to a branch and by hand (**Actions → CI → Run workflow**) - not for a version
tag: the [release workflow](#the-release-workflow) doesn't run it or wait for it.
It has two jobs: **Test**, then **Build** once the tests passed. Build restores in
locked mode (exactly the packages
`packages.lock.json` names, with their hashes - the app's tests' and the
launcher's), builds in Release (the version from the newest tag, as every local
build) **with warnings as errors** (`-warnaserror`, XML-comment warnings
included: `DnnManager.csproj` has the compiler read its XML comments
(`GenerateDocumentationFile`), so a misplaced or broken one fails the build -
without asking a comment for every public member: CS1591 and CS1573 are off),
builds the launcher as
plain .NET (the release publishes it with Native AOT). Test runs the whole test
suite. It publishes nothing.

**Skipped tests are counted.** The integration tests are skipped (inconclusive)
where the runner lacks IIS Express, LocalDB or Linux containers. The step
*Count skipped tests* reads the results (`TestResults/tests.trx`), lists the
skipped ones in the run's summary, and fails the run when a test outside
`TestCategory=Integration` was skipped - a fast test must run everywhere - or
when more were skipped than the repository variable `CI_MAX_SKIPPED_TESTS`
allows (14 when it isn't set). Set it to the count the summary shows, so a newly
skipped integration test fails the run too.

What makes a build the same each time:

- **The .NET SDK** is exactly the one [`global.json`](../global.json) names
  (`rollForward: disable` - no newer patch) - both workflows install it from
  there, and a local build needs it too. See [The .NET SDK](#the-net-sdk) for
  moving to a newer one.
- **The runner** is a pinned image (`windows-2025`), not `windows-latest`: moving to
  a newer image is a change in review.
- **The packages** are in `packages.lock.json` (the app's and the tests') - a
  package change is a change to the lock file, in review. A restore that would
  change them fails in CI, in the release workflow, and in **Release**
  before it tags anything. After a package change, update the lock files with
  `dotnet restore tests/DnnManager.IntegrationTests --force-evaluate`. A build and
  a single-file publish restore the same packages: `DnnManager.csproj` turns on
  the single-file analyzer for every build, and with it the SDK's
  `Microsoft.NET.ILLink.Tasks`, which a single-file publish adds otherwise.
- **Where packages come from**: [`nuget.config`](../nuget.config) clears every other
  package source (a machine-wide or user NuGet.Config) and maps every package to
  nuget.org, so a package of the same name on another feed is never used.
- **The actions** are pinned to commits, not tags that can move;
  [Dependabot](../.github/dependabot.yml) proposes updates for them and the NuGet
  packages each month, which CI builds and tests.
- **Inno Setup** for a release is the pinned `Tools.InnoSetup` package, checked
  against its SHA-512 and extracted again on every build
  ([Build the installer](#build-the-installer)).

## The in-app update

Every DNN Manager from 1.7.0 on updates itself from GitHub's **latest release** (never a
draft or a pre-release - a `vX.Y.Z-rc.1` tag isn't offered) - see
[Update](user-guide.md#update). A release must keep what it relies on:

- **The file names**: `DnnManager_Setup-<version>-x64.exe` updates an installed DNN
  Manager, `DnnManager-<version>-x64.exe` a portable one
  ([`AppRelease.AssetFor`](../src/DnnManager.Infrastructure/Updates/AppRelease.cs)).
  Releases 1.7.7 to 1.8.x named the portable one `DnnManager_Portable-<version>-x64.exe`.
  From 2.0.0 it is `DnnManager-<version>-x64.exe` again: every DNN Manager looks for
  that name, 1.7.7 and newer after `DnnManager_Portable-`. The macOS and Linux
  files (`DnnManager-<version>-macos-arm64`, …) never match, because they don't end in `.exe`.
  Releases up to 1.7.6 had `DnnManagerSetup-<version>-x64.exe` and
  `DnnManager-<version>-x64.exe` - the only names DNN Manager 1.7.6 and older
  (and their Setups) look for. 1.7.7 was released under both names, so those
  versions could update to it; later releases have only the new names, so 1.7.6
  and older can't update past 1.7.7 by themselves - install a newer version by
  hand once. 1.7.7 and newer (and Setup, for **Repair** of an old version) find
  either name.
- **The version inside both files**: their *ProductVersion* must be the tag's version
  (the release workflow checks it before publishing) - a download whose version differs is refused.
- **GitHub's SHA-256** of each file (the asset's `digest`): a file GitHub lists no
  SHA-256 for isn't installed - by DNN Manager or by Setup. The release workflow
  checks GitHub's digest of every file it uploads.
- **Silent Setup**: the update runs Setup with `/SILENT /SUPPRESSMSGBOXES /NORESTART
  /NOCANCEL /SP- /CURRENTUSER` (or `/ALLUSERS`) - `DnnManager.iss` must keep
  installing without questions that way, and keep its `AppId`.
- **Setup's hand-over**: a Setup newer than 1.7.1 asks GitHub for the latest release as
  it starts. A new install and **Update** install that one, **Repair** the installed
  version (the release `v<installed version>`); for any version but its own, Setup
  downloads that release's `DnnManager_Setup-<version>-x64.exe` (`DnnManagerSetup-…` up to 1.7.6; GitHub's
  SHA-256, and a *file version* of `<version>.0`), checks the SHA-256 again right
  before it runs it from its own temporary folder (`{tmp}` - only administrators may
  write there when Setup is elevated), with `/SP- /HandedOver=1 /ALLUSERS` (or
  `/CURRENTUSER`) and `/Again=<n>`, and waits for it, its own wizard hidden. A Setup
  newer than 1.8.1 also gets `/ReturnToCaller=1`: it doesn't start itself again when
  done - the waiting one shows its first page. A newer Setup must keep `/HandedOver=1`
  meaning: don't ask GitHub, skip the license and the Repair / Uninstall page, and use
  a Setup mutex of its own
  (`SetupMutex=DnnManager.NET.Setup{param:HandedOver|}{param:Done|}{param:Again|}`) - the
  calling one is still running. Setup's log (`/LOG=<file>`) records what GitHub answered.
- **Back to Setup's first page**: after Repair or Update (from 1.7.6 on) Setup skips its
  Finished page and starts itself again with `/SP- /Done=Repaired` (or `Updated`,
  or `Uninstalled` after the uninstaller), plus `/CURRENTUSER` or `/ALLUSERS`. A
  Setup it hands over to gets `/Back=Repaired` (or `Updated`) to do the same; an
  older one ignores it and shows its Finished page. `/Done` and `/Back` accept only
  `Updated`, `Repaired` and `Uninstalled` - anything else counts as none -, since they
  go on the command line of a Setup with administrator rights. Setup starts its own
  exe again only while its SHA-256 is still the one it started with: directly once
  the installation has started, before that through `cmd /d /v:off /c start` (Inno's
  `Exec` refuses Setup's own exe until then), with the path checked for quotes,
  `%`, `!` and control characters.
- **A running DNN Manager**: Setup has no `AppMutex`, so it opens while DNN Manager
  runs. Just before replacing or removing the files (`PrepareToInstall`, and the
  uninstaller's `usUninstall` step - after its confirmation) it sets the event
  `DnnManager.NET.Quit`, which DNN Manager 1.7.6 and newer listen to
  ([`SingleInstance`](../src/DnnManager.Presentation/SingleInstance.cs)) and quit on
  without asking (an open dialog is closed, a running operation cancelled, the
  workspace saved), and waits up to 10 seconds for the mutex `DnnManager.NET.Running`
  to go. Still there (an older version, or no answer): it ends the process with
  PowerShell started through `runas` - with Setup's own administrator rights, or one
  UAC prompt for a `/CURRENTUSER` Setup - only `DnnManager.exe` in the install folder
  (a plain path, quoted as a PowerShell string), never the update helper
  (`DnnManager-update.exe`) - and waits 5 more seconds. Only then does it ask the
  user to quit it and **Retry**.
- **Uninstall**: the uninstaller removes the sign-in task (the one that starts this
  `DnnManager.exe` or the launcher beside it - asking for administrator rights when
  it must), then, unless it is silent, lists what stays and asks - *No* by default -
  whether to remove `Documents\DnnManager`, `%ProgramData%\DnnManager` (for all
  users only) and the saved credentials (`DnnManager/…`); never Docker's container
  or volume, the projects, IIS or the hosts file.

How it works: the running DNN Manager downloads and checks the file into
`%ProgramData%\DnnManager\temp\update\<version>\` - a folder only administrators can
change, as the helper runs from it with their rights ([security.md](security.md#updates)) -
notes the update (the `update` area of the `state` table in
`Documents\DnnManager\dnnmanager.db` - where the user is, the workspace saves as DNN
Manager closes), copies its own exe to `helper-…\` there and closes, starting that
copy with `--apply-update plan.json`. The copy
([`UpdateHelper`](../src/DnnManager.Infrastructure/Updates/UpdateHelper.cs)) waits for it
to exit, checks the downloaded file's size and SHA-256 again (the plan carries
them) while holding it open, runs Setup or swaps the portable exe (with a backup it
puts back if anything fails - an older backup left behind is deleted first), and
starts DNN Manager again; the new version restores the workspace and checks it is
the version the update meant to install. The helper waits for Setup however long it
takes. When the update fails, its log and Setup's are copied to
`Documents\DnnManager\logs\update-failed-<version>.log` (the newest two kept) - the
update folder is cleaned up - and **Show log** opens that file. DNN Manager 1.8.0 and older used
`%TEMP%\DnnManager-update\`; a newer one leaves that folder alone - deleting in
`%TEMP%` with administrator rights would follow wherever another program pointed it.
The helper is the *old* version's code, so a release can change the helper only for
the updates after it.

**Try it** with the VS Code task **Publish: update test**
([`.github/scripts/build-update-test.ps1`](../.github/scripts/build-update-test.ps1)): it
builds the working copy as a portable exe one version below GitHub's newest release
(1.6.0 → 1.5.9, 1.7.0 → 1.6.9, 2.0.0 → 1.9.9) into
`publish\update-test\DnnManager-<version>-x64.exe`. Start it and click **Update**: it
installs the real release over itself. `-Version 1.5.9` picks the version by hand.

## Publish the portable exe

One file, no .NET runtime needed on the target machine:

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PortableExe=true -o publish
```

`-p:PortableExe=true` names the exe `publish\DnnManager-<version>-x64.exe`
(without it, it's `DnnManager.exe`). The publish output holds only the program - no
launcher: a portable DNN Manager relaunches itself elevated directly, and extracts
its native DLLs to `%TEMP%\.net` when it starts (an accepted risk -
[security.md](security.md#known-open-risks)). Settings live in
`Documents\DnnManager` and are created on first start (see
[configuration.md](configuration.md)).

> **"Access to the path '...\publish\DnnManager.exe' is denied"** when publishing
> means the app is still running from `publish\`. Close it and publish again.

## Build the installer

```powershell
.\src\DnnManager.Installer\build.ps1
```

Publishes the app (self-contained, single file) into
`src\DnnManager.Installer\bin\app` and the launcher
([`src/DnnManager.Launcher`](../src/DnnManager.Launcher/DnnManager.Launcher.csproj),
`DnnManager-launcher.exe`, Native AOT) into `bin\launcher`, then compiles
[`src/DnnManager.Installer/DnnManager.iss`](../src/DnnManager.Installer/DnnManager.iss) with Inno Setup
into `publish\DnnManager_Setup-<version>-x64.exe`. The version
is the newest version tag in git, as for every local build (see [Steps](#steps)). It uses an installed Inno Setup 6
when there is one, otherwise - and always with `-PinnedInno`, as a release does - a
pinned copy (the `Tools.InnoSetup` package from nuget.org) - no admin rights needed.
Only the package is kept between builds, in `src\DnnManager.Installer\bin\tools`. On
every build it is checked against the SHA-512 nuget.org lists for it (`$innoSha512` in
`build.ps1`), and the compiler is extracted again from the bytes just checked. A
cached `ISCC.exe`, `Setup.e32` or `SetupLdr.e64` that something changed since the last
build is never run. To
move to a newer Inno Setup, change `$innoVersion` and `$innoSha512` together (the
hash is the `packageHash` of the version's entry in nuget.org's catalog). Everything made along the way (the published app, wizard images,
Inno Setup) is in `src\DnnManager.Installer\bin`; the finished Setup is in
`publish\`.
`-SkipPublish` reuses the last publish (`bin\app` and `bin\launcher`);
`-PublishOnly` only publishes them, unsigned, and stops (the release workflow's
build job); `-Iscc <path>` picks the compiler; `-Version 1.7.0` builds that version
instead of the tag's (the release workflow passes the tag's); `-SignScript <script>`
signs the published `DnnManager.exe` and the launcher and has Inno sign Setup and
its uninstaller (see [Code signing](#code-signing)).

**The launcher needs Visual Studio's C++ build tools** (*Desktop development with
C++*, or the Build Tools for Visual Studio with it): Native AOT links with them.
Without them, `-NoLauncher` builds a Setup without the launcher - DNN Manager then
starts as up to 1.8.1, and a launcher an earlier version installed is removed.
Never for a release.

The installer's `AppId` in `DnnManager.iss` identifies the installation for
upgrades and uninstall - never change it.

## Build on this PC: the publish task

**Ctrl+Shift+B → Publish** asks two things: **Portable** or **Setup**, then the
platforms, **Windows**, **macOS** and/or **Linux** (tick one or more). It runs
[`.github/scripts/publish-local.ps1`](../.github/scripts/publish-local.ps1), and
everything goes into a folder of its own in `publish\`, named
`DnnManager-<version>-<system>-<processor>`, with the newest version tag's version:

| | Portable | Setup |
|---|---|---|
| **Windows** | `DnnManager-<version>-windows-x64\DnnManager-<version>-x64.exe` ([above](#publish-the-portable-exe)) and `-windows-x86\…-x86.exe` (32-bit) | `DnnManager-<version>-windows-x64\DnnManager_Setup-<version>-x64.exe` ([above](#build-the-installer)) - 64-bit only. Without the C++ build tools it is built without the launcher (it says so; never for a release) |
| **macOS** | `DnnManager-<version>-macos-arm64\DnnManager.Desktop` and `-macos-x64` - from any system | `DnnManager-<version>-macos-arm64.zip` and `-macos-x64.zip` - only on a Mac |
| **Linux** | `DnnManager-<version>-linux-x64\DnnManager.Desktop` and `-linux-arm64` - from any system | `DnnManager-<version>-linux-x64.tar.gz` and `-linux-arm64.tar.gz` - only on Linux or a Mac |

The macOS and Linux programs are the [cross-platform app](cross-platform.md). A
portable one needs `chmod +x` on that system, and on a Mac also
`codesign --force --sign -`. A package that only another system can make is
named as not made, with where to make it: the **Desktop** workflow on GitHub
(*Actions → Desktop → Run workflow*) makes them all. The rest is still made.
A DNN Manager running from one of those folders locks its exe, so the task stops
before building and tells you to close it.

**32-bit Windows** gets only the portable exe. Setup installs 64-bit only
(`ArchitecturesAllowed=x64compatible` in `DnnManager.iss`, and the launcher is built
for x64), and releases carry x64 only. A 32-bit portable DNN Manager's
**Update** button looks for `DnnManager-<version>-x86.exe`, so it finds
no update on a release until releases carry one.

The other VS Code tasks (build, clean, the update test, the release tasks) are in
`.vscode/tasks.json`.
