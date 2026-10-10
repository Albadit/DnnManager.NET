# ADR 0003 - What an update is trusted on

**Status:** accepted (2026-10); amended (2026-10) - releases built in CI with
build provenance; amended again (2026-10) - publishing approved by a reviewer,
the tag checked again, signing in a job of its own, a smoke test, the launcher

## Context

DNN Manager updates itself from GitHub's latest release, and the update runs with
Administrator rights. Until 1.8.0:

- the downloaded file was checked against GitHub's SHA-256 of it *only when GitHub
  listed one*;
- it was downloaded to `%TEMP%`, and the elevated helper read its plan and ran the
  file from there - a folder every program of the user's can change - without
  checking it again;
- a release became "latest" the moment the release script finished, while CI only
  started testing its tag;
- the exes aren't signed.

Up to 1.8.1, the published files were also built on the publisher's PC. CI rebuilt
and tested the tag, but it was the PC's build that was uploaded, with a broad
GitHub credential that the release script read from Git. Anyone who controlled
that PC, or the tools it cached (the extracted Inno Setup compiler), controlled
what every DNN Manager installed - and nothing showed where a file had come from.

## Decision

- **The file of the release, or nothing:** a release that lists no SHA-256 isn't
  installed, by DNN Manager or by Setup.
- **Nothing between the check and the run:** the update is downloaded into
  `%ProgramData%\DnnManager\temp\update` (only administrators can change it), and
  the helper checks the file's size and SHA-256 again right before it copies or
  runs it, holding it open meanwhile.
- **Built by GitHub, from the tag:** the
  [release workflow](../releasing.md#the-release-workflow)
  ([`release.yml`](../../.github/workflows/release.yml)) builds both exes from the
  pushed tag on a pinned runner image. It uses the SDK `global.json` names, the
  packages `packages.lock.json` names (from nuget.org only, through `nuget.config`)
  and the pinned Inno Setup, which is extracted again from its hash-checked package
  on every build. Its actions are pinned to commits. The publisher's PC only runs
  the fast tests and pushes the tag; nothing it builds is uploaded.
- **Provenance:** the workflow attests both exes
  (`actions/attest-build-provenance`, signed by GitHub through Sigstore), so
  `gh attestation verify <file> --repo Albadit/DnnManager.NET` shows which
  workflow, tag and commit produced a file.
- **Tested and tried before it is offered:** the release stays a draft until the
  whole test suite (CI, called on the tag), the build, the signing and a smoke test
  have passed - Setup installed on a fresh runner, DNN Manager started through its
  launcher, uninstalled, and the portable exe started. Only then are the files
  uploaded to a draft for the commit that was built, and GitHub's digest of each
  checked.
- **A person approves the publish:** the publish job runs in the GitHub
  environment `publish`, which needs a required reviewer's approval. Right before
  publishing it checks again that the tag still points at the commit that was
  built and that the draft holds exactly the files in `SHA256SUMS.txt`. A release
  becomes the latest only when its version is newer than the latest one.
- **Least privilege:** each job gets only the permissions it needs. The build job,
  which runs the packages' and the tests' code, gets no OpenID Connect token; the
  sign job, which restores no package and runs no test, is the only one that can
  sign in to Azure (just before the first file is signed) and attest. Only the
  draft and publish jobs may write to the repository. The workflow's own
  `GITHUB_TOKEN` is its only GitHub credential. The release scripts read no token:
  Git's credential pushes the tag, and the GitHub API is asked without signing in.
- **A published version never changes:** the release workflow and
  `redo-release.ps1` refuse to touch a published release. A change to one gets a
  new version number. With GitHub's immutable releases turned on, neither its files
  nor its tag can change any more.
- **Signing, ready to switch on:** with Azure Artifact Signing's repository
  variables set, the workflow signs `DnnManager.exe`, the launcher
  (`DnnManager-launcher.exe`), the portable exe, Setup and its uninstaller
  (Authenticode, timestamped), and fails rather than publish an unsigned file -
  and with `REQUIRE_SIGNING` set, a run without the variables fails too. It signs
  in to Azure with OpenID Connect, and there is no signing secret in the
  repository ([Code signing](../releasing.md#code-signing)).

## Consequences

- An update can't be swapped on this PC by a program without administrator rights,
  and a release nobody tested isn't offered.
- A compromised publisher PC can no longer slip its own build into a release:
  what is published is what the workflow built from the tag, and the attestation
  shows it. Releases up to 1.8.1 have no attestation.
- **What remains trusted is whoever can push a version tag and approve the
  publish job**, or change the workflow and then tag. A tag alone builds a draft;
  publishing waits for the `publish` environment's reviewer - once the owner has
  added one on GitHub (without it the job runs at once). DNN Manager installs any
  release whose file matches GitHub's SHA-256. The tag and branch rulesets and the
  environments ([Set up GitHub](../releasing.md#set-up-github)) limit who that is.
  The attestation shows where a file came from, but DNN Manager doesn't check it.
- **The sign job still compiles Setup**: Inno Setup (the pinned, hash-checked
  copy) runs in the job that can sign in to Azure - an accepted residual risk.
- **Until signing is set up, the gap stays:** the exes are unsigned, Windows
  SmartScreen warns about them, and the helper can't check a signer before it runs
  an update. The next steps are to set up Artifact Signing (the workflow is ready
  for it), then have the update helper and Setup check that the downloaded file is
  signed by the publisher's certificate. Until then the exes' missing signature
  stays under [Known open risks](../security.md#known-open-risks).
