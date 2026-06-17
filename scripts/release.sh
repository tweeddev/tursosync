#!/usr/bin/env bash
#
# release.sh — cut a TursoSync release.
#
# Reads the version from Directory.Build.props (the source of truth), tags it `vX.Y.Z`, and pushes the tag.
# GitHub's release.yml then builds every RID native (release + FTS, stripped), packs all three packages at
# that version, and publishes to NuGet via trusted publishing — no API key.
#
# Flow:
#   1. bump <Version> in Directory.Build.props, commit, push to main
#   2. rig release   (or: scripts/release.sh)
#
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

ver="$(grep -oE '<Version>[^<]+' Directory.Build.props | head -1 | sed -E 's/.*<Version>//')"
[ -n "$ver" ] || { echo "release: no <Version> found in Directory.Build.props" >&2; exit 1; }
tag="v$ver"

branch="$(git rev-parse --abbrev-ref HEAD)"
[ "$branch" = "main" ] || { echo "release: run from 'main' (currently on '$branch')" >&2; exit 1; }
[ -z "$(git status --porcelain)" ] || { echo "release: working tree not clean — commit the version bump first" >&2; exit 1; }

git fetch origin -q
[ "$(git rev-parse HEAD)" = "$(git rev-parse origin/main)" ] || { echo "release: local main is not in sync with origin/main" >&2; exit 1; }
if git rev-parse "$tag" >/dev/null 2>&1; then
  echo "release: tag $tag already exists (bump <Version> first)" >&2; exit 1
fi

echo "Releasing TursoSync $tag …"
git tag -a "$tag" -m "TursoSync $ver"
git push origin "$tag"
echo "Pushed $tag. CI is building + publishing — watch with:"
echo "  gh run watch \$(gh run list -w release.yml -L1 --json databaseId -q '.[0].databaseId')"
