---
name: release-notes
description: >-
  Writes the release notes for the next DNN Manager release as docs/release-notes/vX.Y.Z.md, in the structure every
  release uses (bold summary, Highlights, Other changes, Upgrading, Tested, link to the changelog), from the
  CHANGELOG.md entry, the commits since the previous tag and real test results - every claim checked against the
  code - and the release commit's message, ready to paste. The release workflow publishes that file as the GitHub
  release's notes. Use when asked to write, draft,
  update or review release notes, prepare a release or "the next release", or turn the Unreleased changelog entry
  into notes.
---

# Release notes

The notes for a release are **`docs/release-notes/vX.Y.Z.md`**. When the tag `vX.Y.Z` is pushed, the release
workflow publishes that file unchanged as the GitHub release's notes (`.github/scripts/release-notes.ps1`), so it is
written before the tag. [`docs/release-notes/v1.6.0.md`](../../../docs/release-notes/v1.6.0.md) is the model: match
its structure, length and tone.

Readers are **people who use DNN Manager**, deciding whether to update and what changes for them - not developers.

## Workflow

1. **Find the version and the range.** The version is the one asked for, or `<Version>` in `DnnManager.csproj` when it
   is already raised. The previous release is the newest tag: `git describe --tags --abbrev=0`.
2. **Collect what changed:**
   - the `## Unreleased` (or `## vX.Y.Z`) entry in `CHANGELOG.md` - the main source, written by hand;
   - `git log --no-merges --format="%h %s%n%b" <previous tag>..HEAD` - for what the changelog misses;
   - the diff (`git diff --stat <previous tag>..HEAD`, then the files) wherever an entry is unclear.
3. **Verify every claim against the code**: UI labels exactly as in the XAML, settings keys and defaults, paths,
   numbers. Drop what you can't confirm, or ask. Never describe a change that isn't in the range.
4. **Get the test results** - run the fast tests and read the counts:
   `dotnet test tests\DnnManager.IntegrationTests --filter "TestCategory!=Integration" --artifacts-path <temp folder>`.
   Compare with the previous notes' *Tested* section ("up from N"). Add the integration tests or manual checks only
   if they were really run - ask the owner what was tested by hand.
5. **Write `docs/release-notes/vX.Y.Z.md`** in the structure below. The file name is the tag and the release title,
   so it must be exactly `v` + the version (`v1.7.0.md`, or `v1.7.0-rc.1.md` for a pre-release). If the changelog
   entry is still `## Unreleased`, offer to rename it `## vX.Y.Z`.
6. **Check it**: every relative link resolves, the changelog anchor exists, and
   `.github\scripts\release-notes.ps1 -Version X.Y.Z -OutFile <temp>\notes.md` shows the notes as GitHub will get
   them (relative links turned into links to the tag's files).
7. **Write the commit message** for the release commit (see [Commit message](#commit-message)) and give it in the
   report, ready to paste.
8. Report what the notes say, the commit message, and anything left unverified. Don't commit, tag or publish - the
   owner does, with the VS Code task **release (GitHub)** (`.github/scripts/publish-release.ps1`, see
   `docs/releasing.md`), which picks this file and a commit, builds both exes and publishes the release.

## Structure

```markdown
# DNN Manager X.Y.Z

**The release in a few words.** One or two sentences on what it does for the user.

## Highlights

### A theme, named by what the user gets
- What they can do now, and where (**UI name** in bold).

### Another theme
- ...

## Other changes
- Smaller changes, one line each.
- Fixed: a bug, described by what the user saw.

## Upgrading
Install over X.Y-1.x as usual. Your settings keep their format.
- Anything the user must do once, or that now works differently.

## Tested
- What was checked by hand, against what (e.g. a real DNN 10.3.3 site).
- All N fast automated tests pass, up from M. New ones cover ...

Full list of changes: [CHANGELOG.md](../../CHANGELOG.md#vxyz)
```

| Part | Rules |
|---|---|
| **Title** | `# DNN Manager X.Y.Z`, followed directly by the summary - no date, release link or download links (GitHub shows those next to the notes). |
| **Summary** | Starts with a bold phrase of 3-7 words, then 1-2 plain sentences. No version number, no "This release…". |
| **Highlights** | 3-6 `###` themes, the most important first; 2-7 bullets each. Group by what the user gets, not by code layer. |
| **Other changes** | Everything else worth knowing, one line each. Bug fixes go last, as `Fixed: …`. |
| **Upgrading** | Always present. First line: `Install over X.Y.x as usual.` plus whether settings keep their format or are upgraded. Bullets only for actions or changed behaviour - removed features, renamed settings, a step to run once. |
| **Tested** | Only real results. Leave the section out rather than guess. |
| **Changelog link** | Always the last line - nothing after it: no `---` separator, footer or note about earlier version numbers. The anchor is the heading in lower case without dots: `## v1.7.0` → `#v170`. Changelog headings carry no date. |

## Style

- Write for the user: "you", present tense, what they can do and where. Name UI elements exactly as on screen, in
  **bold**; files, folders, settings and commands in `code`.
- Concrete over vague: "folder sizes are only measured while the **Size** column is shown", not "improved
  performance". Give numbers only when measured.
- No commit hashes, class names, internal refactors or dependency bumps - unless they change what the user sees
  (then say what changes).
- No emoji, no marketing words ("powerful", "seamless", "exciting"), no filler introductions.
- UTF-8 without BOM, CRLF line endings, like the rest of the repository.

## Commit message

One line - no body. It follows the repository's release commits (`git log --grep "^release:"`):

```text
release: vX.Y.Z - <the Highlights' themes, lower case, comma-separated>
```

- `release: vX.Y.Z - ` and the release's themes, a few words each, in the order of the Highlights - e.g.
  `release: v1.7.0 - self-update, workspace restore, keyboard and command palette, VS Code look, customizable layout`.
- No full stop, no body, no bullets: what changed is in the notes and the changelog.
- Give it in a `text` code block in the report, so it can be pasted into `git commit` or the VS Code commit box as is.
