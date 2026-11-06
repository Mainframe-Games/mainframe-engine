#!/usr/bin/env bash
# Fails when a branch edits the shared status files outside a state sync (ADR 0280, .agents/workflow.md#state-sync).
# Feature PRs put their status changes in their log's "## State changes" section instead; a `*state-sync*` branch
# applies them. A branch that changes this script (the rule itself) may edit them too.
#
#   bash build/state-guard.sh [--base <ref>]   (default origin/main, else main; CI passes the PR's base)
set -u

guarded=(docs/milestones.md memory/context/current-state.md)

base=
case "${1:-}" in
  --base) base=${2:-} ;;
  --base=*) base=${1#--base=} ;;
  '') ;;
  *) echo "usage: state-guard.sh [--base <ref>]" >&2; exit 2 ;;
esac
if [ -z "$base" ]; then
  for ref in origin/main main; do
    git rev-parse --verify --quiet "$ref^{commit}" >/dev/null && { base=$ref; break; }
  done
fi
[ -n "$base" ] || { echo "state-guard: no base; pass --base <ref>" >&2; exit 2; }

merge_base=$(git merge-base "$base" HEAD) || { echo "state-guard: no merge base with $base" >&2; exit 2; }
# Committed changes on the branch, plus uncommitted ones when run locally before a commit.
changed=$( {
  git diff --name-only "$merge_base" HEAD
  git diff --name-only HEAD
  git ls-files --others --exclude-standard
} | sort -u)

branch=${GITHUB_HEAD_REF:-$(git branch --show-current)}
case "$branch" in
  *state-sync*) echo "state-guard: ok (state sync branch $branch)"; exit 0 ;;
esac
if grep -qxF build/state-guard.sh <<< "$changed"; then
  echo "state-guard: ok (this branch changes the rule itself)"
  exit 0
fi

# The text with every Markdown link target emptied: a change that only repoints links (a doc moved, as when a proposal
# ships from docs/design/future/) is allowed.
without_targets() {
  sed -E 's/\]\([^)]*\)/]()/g'
}

bad=0
for file in "${guarded[@]}"; do
  if grep -qxF "$file" <<< "$changed"; then
    if [ -f "$file" ] &&
      [ "$(git show "$merge_base:$file" 2>/dev/null | without_targets)" = "$(without_targets < "$file")" ]; then
      echo "state-guard: $file: link targets only, ok"
      continue
    fi
    echo "error: $file: edited outside a state sync. Restore it (git checkout $merge_base -- $file) and put the change"
    echo "       in this branch's log under '## State changes' (.agents/workflow.md#state-sync)."
    bad=1
  fi
done
[ "$bad" -eq 0 ] && echo "state-guard: ok"
exit "$bad"
