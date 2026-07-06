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

## 4. Tag And Push

- Check recent tags:

```bash
git tag --sort=-v:refname | head -5
```

- Follow the existing tag format and increment the version, for example `v0.5.0` to `v0.6.0`.
- Use an English annotated tag message matching the merge commit summary.

```bash
git tag -a vX.Y.Z -m "<English summary>"
git push origin vX.Y.Z
```

## 5. Create The GitHub Release

- Write release notes manually in Chinese. Do not use `--generate-notes`.
- Use two sections: `新增功能` and `问题修复`.
- The `release.yml` workflow builds platform packages after the tag push and attaches artifacts to the release.

```bash
gh release create vX.Y.Z --title "vX.Y.Z" --notes "中文 release notes"
```

## 6. Return To Dev

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
