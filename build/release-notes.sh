#!/usr/bin/env bash
# Prints a release's notes: GitHub's generated notes with "What's Changed" grouped under headers by each PR title's
# "Area:" prefix (Shaders/Vulkan → Rendering, Demo/Forest/Neon → Examples, ...; no known prefix → Other), headers in a
# fixed order, each bullet "* <title> by @<user> (#<n>)". New Contributors and Full Changelog are kept as generated.
#   build/release-notes.sh <tag> [target-commitish] [previous-tag]
# The tag need not exist yet. Without a previous tag GitHub picks the latest release. Repository: $GITHUB_REPOSITORY,
# else the checkout's. Requires gh (authenticated). See docs/design/release.md#release-notes.
set -euo pipefail

tag="${1:?usage: build/release-notes.sh <tag> [target-commitish] [previous-tag]}"
target="${2:-}"
previous="${3:-}"
repo="${GITHUB_REPOSITORY:-$(gh repo view --json nameWithOwner -q .nameWithOwner)}"

args=(-f "tag_name=${tag}")
[[ -n "$target" ]] && args+=(-f "target_commitish=${target}")
[[ -n "$previous" ]] && args+=(-f "previous_tag_name=${previous}")
body="$(gh api "repos/${repo}/releases/generate-notes" "${args[@]}" -q .body)"

# Header order; then each PR title prefix (lower case) → its header.
headers=("Rendering" "Physics" "Audio" "Navigation" "UI" "Core" "Editor" "Examples" "Build & Tooling" "Docs" "Other")
declare -A category=(
  [rendering]=Rendering [shaders]=Rendering [vulkan]=Rendering [post]=Rendering [particles]=Rendering
  [text]=Rendering [gizmos]=Rendering [sky]=Rendering [shadows]=Rendering
  [physics]=Physics [ragdoll]=Physics [joints]=Physics
  [audio]=Audio [music]=Audio [zzfx]=Audio
  [navigation]=Navigation
  [ui]=UI [rmlui]=UI [settings]=UI
  [core]=Core [engine]=Core [content]=Core [scene]=Core [nodes]=Core [math]=Core [animation]=Core [paths]=Core
  [resources]=Core [serialization]=Core [saves]=Core [networking]=Core [steamworks]=Core [steam]=Core
  [localization]=Core [l10n]=Core [recording]=Core [profiler]=Core [input]=Core [project]=Core [gamehost]=Core
  ["gameplay toolkit"]=Core
  [editor]=Editor ["project manager"]=Editor [video]=Editor ["video mode"]=Editor [inspector]=Editor
  [demo]=Examples [forest]=Examples [neon]=Examples [examples]=Examples
  [build]=Build\ \&\ Tooling [release]=Build\ \&\ Tooling [ci]=Build\ \&\ Tooling [tests]=Build\ \&\ Tooling
  [tools]=Build\ \&\ Tooling [workflow]=Build\ \&\ Tooling [template]=Build\ \&\ Tooling [templates]=Build\ \&\ Tooling
  [natives]=Build\ \&\ Tooling [nuget]=Build\ \&\ Tooling [export]=Build\ \&\ Tooling ["linux install.sh"]=Build\ \&\ Tooling
  [docs]=Docs ["docs site"]=Docs [readme]=Docs
)
declare -A grouped=()

bullet='^\* (.*) by @([^ ]+) in https://[^ ]+/pull/([0-9]+)$'
emit_groups() {
  local header first=1
  for header in "${headers[@]}"; do
    [[ -n "${grouped[$header]:-}" ]] || continue
    ((first)) || echo
    first=0
    printf '### %s\n%s' "$header" "${grouped[$header]}"
  done
  grouped=()
}

in_changes=0
while IFS= read -r line; do
  if [[ "$line" == "## What's Changed" ]]; then
    in_changes=1
    echo "$line"
    echo
    continue
  fi
  if ((in_changes)) && [[ "$line" == "## "* || "$line" == "**Full Changelog**"* ]]; then
    in_changes=0
    emit_groups
    echo
    echo
  fi
  if ((in_changes)); then
    if [[ "$line" =~ $bullet ]]; then
      title="${BASH_REMATCH[1]}" user="${BASH_REMATCH[2]}" number="${BASH_REMATCH[3]}"
      header=Other
      if [[ "$title" == *": "* ]]; then
        prefix="${title%%: *}"
        header="${category[${prefix,,}]:-Other}"
      fi
      grouped[$header]+="* ${title} by @${user} (#${number})"$'\n'
    fi
    continue
  fi
  echo "$line"
done <<<"$body"
((in_changes)) && emit_groups
exit 0
