# ADR 0003 - What an update is trusted on

**Status:** accepted (2026-10)

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

## Decision

- **The file of the release, or nothing:** a release that lists no SHA-256 isn't
  installed, by DNN Manager or by Setup.
- **Nothing between the check and the run:** the update is downloaded into
  `%ProgramData%\DnnManager\temp\update` (only administrators can change it), and
  the helper checks the file's size and SHA-256 again right before it copies or
  runs it, holding it open meanwhile.
- **Tested before it is offered:** [`publish-release.ps1`](../../.github/scripts/publish-release.ps1)
  keeps the release a draft until CI has passed on its tag, and checks GitHub's
  digest of every file it uploads. `-SkipTests` and `-SkipCi` are for pre-releases,
  which nobody is offered.

## Consequences

- An update can't be swapped on this PC by a program without administrator rights,
  and a release nobody tested isn't offered.
- What is trusted is still the GitHub release: whoever can publish one (the
  publisher's GitHub credential) can publish an update every DNN Manager installs.
  **Code signing** (Authenticode, with the helper checking the signer) is the next
  step - it needs a certificate or a signing service, and goes into `build.ps1`
  and `DnnManager.iss` (`SignTool`). Until then it is listed under
  [Known open risks](../security.md#known-open-risks).
- Building releases in CI (with build provenance) instead of on the publisher's
  PC remains open: today CI rebuilds and tests the tag, and the PC's build is
  what is published.
