# Release Process

Use this process when publishing a new version from `dev` to `master`.

## 1. Confirm Current State

- Confirm the current branch is `dev` and the working tree is clean with `git status`.
- List commits that `dev` has ahead of `master`:

```bash
git log master..dev --oneline
```

- Show the commit list to the user and confirm all commits should be released.

## 2. Open The Pull Request

- PR title: English, concise, 60 characters or fewer.
- PR description: English, with `Summary` and `Test plan` sections.
- Before writing the PR description, read full commit bodies:

```bash
git log master..dev --format=full
```

- Base the PR description on the detailed commit information, not only the one-line subjects.
- Do not add AI tool attribution or collaboration notes.

## 3. Merge The Pull Request

- Preserve full history: do not squash and do not rebase.
- Do not delete the `dev` branch.
- Merge with `--no-ff` and write an English merge commit message:

```bash
git checkout master
git merge --no-ff dev -m "merge: <subject>

- point one
- point two"
git push origin master
```

## 4. Choose Release Version

- Check recent tags:

```bash
git tag --sort=-v:refname | head -5
```

- Follow the MinVer release tag format and increment the version, for example `v0.5.0` to `v0.6.0`.
- Release tags must be `vMAJOR.MINOR.PATCH` or `vMAJOR.MINOR.PATCH-prerelease`; the Windows installer version drops the leading `v`.
- Preferred path: run the `Release` workflow manually from GitHub Actions, enter the version, and choose `draft` / `prerelease` as needed. The workflow builds the Windows installer, creates the tag when missing, and creates the GitHub Release.

Manual workflow input accepts either `vX.Y.Z` or `X.Y.Z`.

## 5. Optional Local Tag Path

If releasing by local tag instead of manual workflow, use an English annotated tag message matching the merge commit summary:

```bash
git tag -a vX.Y.Z -m "<English summary>"
git push origin vX.Y.Z
```

Pushing the tag starts `.github/workflows/release.yml`. The workflow fetches full Git history/tags, resolves the version with MinVer, and fails if the tag and MinVer version do not match.

## 6. Review The GitHub Release

The workflow currently builds only the Windows installer and attaches the `.exe` plus `sha256sums.txt` to the GitHub Release.

Review the generated GitHub Release notes after the workflow finishes. If publishing public release notes, rewrite them manually in Chinese with `新增功能` and `问题修复` sections.

```bash
gh release view vX.Y.Z --web
```

## 7. Return To Dev

```bash
git checkout dev
git log master..dev --oneline
```

The final command should show no unreleased commits.

## Rules

- Commit messages: English, specific, no emoji, no `Co-Authored-By`.
- PR title and description: English.
- GitHub Release notes: Chinese.
- Release notes are handwritten and should not depend on GitHub auto-generation.
