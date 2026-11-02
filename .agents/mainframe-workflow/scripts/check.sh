#!/usr/bin/env bash
# Check a project against the workflow. Only files added or changed since the
# base count, so older history never needs migrating.
# Bash 3.2 safe: macOS still ships it.
#
# No `set -e`: errors are counted, not fatal. Text goes to `grep -q` as a
# here-string, because a pipe can kill the writer when grep exits early.
set -u

config=.agents/workflow.md
vendor=.agents/mainframe-workflow
lock=$vendor/.lock

errors=0

die() {
  echo "check: $*" >&2
  exit 1
}

error() {
  echo "error: $*"
  errors=$((errors + 1))
}

warning() {
  echo "warning: $*"
}

base=
while [ $# -gt 0 ]; do
  case "$1" in
    --base)
      [ $# -ge 2 ] || die "usage: check.sh [--base <ref>]"
      base=$2
      shift 2
      ;;
    --base=*)
      base=${1#--base=}
      shift
      ;;
    *) die "usage: check.sh [--base <ref>]" ;;
  esac
done

root=$(git rev-parse --show-toplevel) || die "not in a git repo"
cd "$root" || die "cannot enter $root"

is_commit() {
  git rev-parse --verify --quiet "$1^{commit}" >/dev/null
}

if [ -n "$base" ]; then
  is_commit "$base" || die "base not found: $base"
else
  for ref in origin/HEAD origin/main main; do
    if is_commit "$ref"; then
      base=$ref
      break
    fi
  done
  [ -n "$base" ] || die "no base found: fetch the default branch or pass --base <ref>"
fi
merge_base=$(git merge-base "$base" HEAD 2>/dev/null) || merge_base=$base

if command -v sha256sum >/dev/null 2>&1; then
  sha() { sha256sum; }
elif command -v shasum >/dev/null 2>&1; then
  sha() { shasum -a 256; }
else
  die "need sha256sum or shasum"
fi

# Hash without carriage returns so Windows checkouts match.
hash_file() {
  tr -d '\r' < "$1" | sha | cut -d' ' -f1
}

# The file without carriage returns or fenced code.
body() {
  tr -d '\r' < "$1" | awk '
    { line = $0; sub(/^[ \t]+/, "", line) }
    fence != "" { if (index(line, fence) == 1) fence = ""; next }
    line ~ /^```/ || line ~ /^~~~/ { fence = substr(line, 1, 3); next }
    { print }
  '
}

config_value() { # <key>
  tr -d '\r' < "$config" | awk -v key="$1:" '
    NR == 1 { if ($0 !~ /^---[ \t]*$/) exit; next }
    /^---[ \t]*$/ { exit }
    index($0, key) == 1 {
      value = substr($0, length(key) + 1)
      sub(/^[ \t]+/, "", value)
      sub(/^#.*$/, "", value)
      sub(/[ \t]+#.*$/, "", value)
      sub(/[ \t]+$/, "", value)
      gsub(/["\047]/, "", value)
      sub(/\/$/, "", value)
      print value
      exit
    }
  '
}

decisions=
decision_naming=
proposals=
plans=
logs=
state=
state_max_lines=

if [ ! -f "$config" ]; then
  error "$config missing"
elif [ "$(tr -d '\r' < "$config" | head -n 1)" != "---" ]; then
  error "$config: no frontmatter"
else
  decisions=$(config_value decisions)
  decision_naming=$(config_value decision_naming)
  proposals=$(config_value proposals)
  plans=$(config_value plans)
  logs=$(config_value logs)
  state=$(config_value state)
  state_max_lines=$(config_value state_max_lines)
fi

case "$decision_naming" in
  '' | numbered | dated | slug) ;;
  *) error "$config: bad decision_naming: $decision_naming" ;;
esac

if [ ! -f "$lock" ]; then
  error "$lock missing"
else
  locked=
  while IFS= read -r line; do
    line=${line%$'\r'}
    case "$line" in
      '' | '#'*) continue ;;
    esac
    want=${line%%  *}
    path=${line#*  }
    locked="$locked$path
"
    if [ ! -f "$path" ]; then
      error "$path: locked file missing; edited by hand, fix upstream"
    elif [ "$(hash_file "$path")" != "$want" ]; then
      error "$path: hash differs from the lock; edited by hand, fix upstream"
    fi
  done < "$lock"

  while IFS= read -r path; do
    [ "$path" = "$lock" ] && continue
    grep -qxF -- "$path" <<< "$locked" ||
      error "$path: not in the lock; added by hand, fix upstream"
  done < <(find "$vendor" -type f)
fi

date_re='[0-9]{4}-[0-9]{2}-[0-9]{2}'
slug_re='[a-z0-9][a-z0-9-]*'
numbered_name="^[0-9]{4}-$slug_re\.md\$"
dated_name="^$date_re-$slug_re\.md\$"
slug_name="^$slug_re\.md\$"
log_name="^$date_re(-[^/]*)?\.md\$"
decision_heading='^## (The d|D)ecision[[:space:]]*$'
problem_heading='^## Problem[[:space:]]*$'
steps_heading='^## Steps[[:space:]]*$'

has_status() { # <text>
  grep -qF '**Status:**' <<< "$1"
}

has_scenario() { # <text>
  awk '
    /^## / { inside = ($0 ~ /^## Scenarios[ \t]*$/); next }
    inside && /^[ \t]*[-*+][ \t]+[^ \t]/ { found = 1 }
    END { exit !found }
  ' <<< "$1"
}

has_summary() { # <text>
  awk '
    NR == 1 { if ($0 !~ /^---[ \t]*$/) exit; next }
    /^---[ \t]*$/ { closed = 1; exit }
    /^summary:/ { found = 1 }
    END { exit !(found && closed) }
  ' <<< "$1"
}

# Also looks at the base, so two branches taking the same number collide
# before either lands.
check_number() { # <file>
  local file=$1 name=${1##*/} dir=${1%/*} number other
  number=${name%%-*}
  while IFS= read -r other; do
    other=${other##*/}
    case "$other" in
      "$number"-*)
        if [ "$other" != "$name" ]; then
          error "$file: duplicate number, also used by $other"
          return
        fi
        ;;
    esac
  done < <(ls "$dir"; git ls-tree --name-only "$base" "$dir/")
}

check_decision() { # <file>
  local file=$1 name=${1##*/} text
  case "$decision_naming" in
    numbered)
      if [[ ! $name =~ $numbered_name ]] || [[ $name =~ $dated_name ]]; then
        error "$file: bad decision name, want NNNN-slug.md"
      else
        check_number "$file"
      fi
      ;;
    dated)
      [[ $name =~ $dated_name ]] || error "$file: bad decision name, want YYYY-MM-DD-slug.md"
      ;;
    slug)
      [[ $name =~ $slug_name ]] || error "$file: bad decision name, want slug.md"
      ;;
  esac
  text=$(body "$file")
  has_status "$text" || error "$file: decision missing **Status:**"
  grep -qE "$decision_heading" <<< "$text" || error "$file: decision missing ## Decision"
}

check_proposal() { # <file>
  local file=$1 text
  text=$(body "$file")
  grep -qE "$problem_heading" <<< "$text" || error "$file: proposal missing ## Problem"
  has_scenario "$text" || error "$file: proposal needs ## Scenarios with at least one bullet"
}

check_plan() { # <file>
  local file=$1 name=${1##*/} text
  [[ $name =~ $dated_name ]] || error "$file: bad plan name, want YYYY-MM-DD-slug.md"
  text=$(body "$file")
  has_status "$text" || error "$file: plan missing **Status:**"
  grep -qE "$steps_heading" <<< "$text" || error "$file: plan missing ## Steps"
}

check_log() { # <file>
  local file=$1 name=${1##*/}
  [[ $name =~ $log_name ]] || error "$file: bad log name, want a YYYY-MM-DD date first"
  has_summary "$(body "$file")" || error "$file: log missing frontmatter summary:"
}

check_links() { # <file>
  local file=$1 dir target
  dir=$(dirname "$file")
  while IFS= read -r target; do
    target=${target#](}
    target=${target%)}
    case "$target" in
      '<'*)
        target=${target#<}
        target=${target%%>*}
        ;;
      *) target=${target%%[[:space:]]*} ;;
    esac
    target=${target%%#*}
    target=${target%%\?*}
    case "$target" in
      '' | /*) continue ;;
    esac
    [[ $target =~ ^[A-Za-z][A-Za-z0-9+.-]*: ]] && continue
    target=${target//%20/ }
    [ -e "$dir/$target" ] || error "$file: broken link: $target"
  done < <(body "$file" | sed 's/`[^`]*`//g' | grep -oE '\]\([^)]*\)')
}

in_folder() { # <folder> <file>
  [ -n "$1" ] || return 1
  case "$2" in
    "$1"/*.md) return 0 ;;
  esac
  return 1
}

is_skipped() { # <file>
  case "${1##*/}" in
    README.md | index.md | TEMPLATE.md) return 0 ;;
  esac
  return 1
}

changes=$(git -c core.quotePath=false diff --name-status -M "$merge_base") ||
  die "git diff failed against $merge_base"
untracked=$(git -c core.quotePath=false ls-files --others --exclude-standard) ||
  die "git ls-files failed"

# A rename counts as changed, not added, so moving an old file is free.
added=$untracked
touched=$untracked
while IFS=$'\t' read -r kind first second; do
  case "$kind" in
    '' | D*) ;;
    A*)
      added="$added
$first"
      touched="$touched
$first"
      ;;
    R* | C*)
      touched="$touched
$second"
      ;;
    *)
      touched="$touched
$first"
      ;;
  esac
done <<< "$changes"

while IFS= read -r file; do
  [ -n "$file" ] || continue
  is_skipped "$file" && continue
  in_folder "$decisions" "$file" && check_decision "$file"
  in_folder "$proposals" "$file" && check_proposal "$file"
  in_folder "$plans" "$file" && check_plan "$file"
  in_folder "$logs" "$file" && check_log "$file"
done <<< "$added"

while IFS= read -r file; do
  [ -n "$file" ] || continue
  is_skipped "$file" && continue
  if in_folder "$decisions" "$file" || in_folder "$proposals" "$file" ||
    in_folder "$plans" "$file" || in_folder "$logs" "$file"; then
    check_links "$file"
  fi
done <<< "$touched"

if [ -n "$state" ] && [ -f "$state" ] && [[ $state_max_lines =~ ^[0-9]+$ ]]; then
  lines=$(wc -l < "$state")
  if [ "$lines" -gt "$state_max_lines" ]; then
    warning "$state: $((lines)) lines, over state_max_lines ($state_max_lines)"
  fi
fi

if [ "$errors" -gt 0 ]; then
  echo "check: $errors error(s)"
  exit 1
fi
echo "check: ok (base $base)"
