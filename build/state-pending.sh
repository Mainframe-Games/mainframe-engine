#!/usr/bin/env bash
# Lists the status changes merged since the last state sync (ADR 0280, .agents/workflow.md#state-sync): every log
# added since then, with its "## State changes" section. These are not in docs/milestones.md or
# memory/context/current-state.md yet; read them with those files, and a state sync applies them.
#
#   bash build/state-pending.sh
set -u

# The last sync is the newest commit whose subject starts "State: sync"; before the first one, the commit that added
# this script.
since=$(git log -1 --format=%H -E --grep='^State: sync' HEAD)
[ -n "$since" ] || since=$(git log --diff-filter=A --format=%H -- build/state-pending.sh | tail -n 1)
if [ -z "$since" ]; then
  echo "state-pending: no state sync yet"
  exit 0
fi

logs=$( {
  git diff --name-only --diff-filter=A "$since" -- memory/logs
  git ls-files --others --exclude-standard -- memory/logs
} | grep -E '^memory/logs/.*\.md$' | sort -u)

if [ -z "$logs" ]; then
  echo "state-pending: nothing since $(git log -1 --format='%h %s' "$since")"
  exit 0
fi

echo "Logs since $(git log -1 --format='%h %s' "$since"):"
while IFS= read -r log; do
  echo
  echo "== $log"
  section=$(tr -d '\r' < "$log" | awk '
    /^## / { inside = ($0 ~ /^## State changes[ \t]*$/); next }
    inside { print }
  ' | sed '/./,$!d')
  if [ -n "$section" ]; then
    printf '%s\n' "$section"
  else
    echo "(no State changes section)"
  fi
done <<< "$logs"
