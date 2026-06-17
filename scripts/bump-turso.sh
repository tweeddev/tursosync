#!/usr/bin/env bash
#
# bump-turso.sh — pin the Turso engine version in turso-engine.json.
#
# The Turso engine (native turso_sync_sdk_kit) is not vendored; CI fetches + builds it at the pinned ref.
# This script resolves a tag to its commit SHA and writes the pin file. CI/release read that file, so a
# bump is a normal reviewable change (the scheduled engine-bump workflow opens a PR; CI validates the new
# engine before you merge — important because the C ABI is beta).
#
# Usage:
#   scripts/bump-turso.sh                # latest tag in the currently-pinned pre-release series
#   scripts/bump-turso.sh latest         # same
#   scripts/bump-turso.sh v0.7.0-pre.9   # an explicit tag
#
# Requires: gh (authenticated) + jq-free (uses gh --jq).
set -euo pipefail

repo="tursodatabase/turso"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
file="$root/turso-engine.json"
target="${1:-latest}"

current_tag="$(grep -oE '"tag"[[:space:]]*:[[:space:]]*"[^"]+"' "$file" | grep -oE 'v[0-9][^"]*' || true)"

resolve_latest() {
  # Highest tag in the same series as currently pinned, e.g. v0.7.0-pre.8 -> prefix "v0.7.0-pre."
  local prefix="${current_tag%.*}."
  local escaped="${prefix//./\\.}"
  gh api "repos/$repo/tags" --paginate --jq '.[].name' \
    | grep -E "^${escaped}[0-9]+$" \
    | sort -V | tail -1
}

case "$target" in
  latest|"") tag="$(resolve_latest)" ;;
  *) tag="$target" ;;
esac
[ -n "$tag" ] || { echo "could not resolve a tag (current: '$current_tag')" >&2; exit 1; }

# Resolve tag -> commit SHA (dereference annotated tags).
obj_type="$(gh api "repos/$repo/git/refs/tags/$tag" --jq '.object.type')"
obj_sha="$(gh api "repos/$repo/git/refs/tags/$tag" --jq '.object.sha')"
if [ "$obj_type" = "tag" ]; then
  sha="$(gh api "repos/$repo/git/tags/$obj_sha" --jq '.object.sha')"
else
  sha="$obj_sha"
fi

cat > "$file" <<JSON
{
  "repository": "$repo",
  "tag": "$tag",
  "sha": "$sha"
}
JSON

echo "Pinned $repo @ $tag ($sha) → $file"
