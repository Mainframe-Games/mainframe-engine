#!/usr/bin/env bash
# Signs a macOS .app inside out (issue #105) and verifies it with codesign --verify --deep --strict: every Mach-O in
# Contents except the main executable first (the runtime's and the engine's dylibs in Contents/MonoBundle, helper
# executables), deepest first, then the bundle, which signs the main executable and seals everything else. No --deep
# signing: it would sign nested code with the bundle's options and entitlements (a helper needs its own).
# Usage: build/macos/sign-app.sh <App.app> [--identity "Developer ID Application: …"] [--entitlements <plist>]
#                                [--helper <path in Contents>=<entitlements plist>]...
#   --identity      default "-" (ad hoc: Apple Silicon refuses unsigned code; no hardened runtime, so no entitlements).
#                   A Developer ID signs with the hardened runtime and a secure timestamp; the main executable then gets
#                   --entitlements, by default the .NET ones (JIT, unsigned executable memory, library validation off).
#   --helper        a Mach-O executable signed with its own entitlements under a Developer ID (e.g. MacOS/mfplughost).
# Used by build/package-game.sh (also from the MainframeEngine package's tools/macos/) and build/package-editor.sh.
set -euo pipefail
app="${1:?usage: sign-app.sh <App.app> [--identity id] [--entitlements plist] [--helper path=plist]...}"; shift
identity="-"; entitlements=""; helpers=()
while [ $# -gt 0 ]; do
  case "$1" in
    --identity) identity="$2"; shift 2 ;;
    --entitlements) entitlements="$2"; shift 2 ;;
    --helper) helpers+=("$2"); shift 2 ;;
    *) echo "sign-app.sh: unknown option: $1" >&2; exit 2 ;;
  esac
done
app="${app%/}"
[ -d "$app/Contents" ] || { echo "sign-app.sh: $app is not an app bundle" >&2; exit 1; }
main="$(/usr/libexec/PlistBuddy -c "Print :CFBundleExecutable" "$app/Contents/Info.plist")"

options=(--force --sign "$identity")
if [ "$identity" != "-" ]; then
  options+=(--timestamp --options runtime)
  if [ -z "$entitlements" ]; then
    temp="$(mktemp -d)"; trap 'rm -rf "$temp"' EXIT
    entitlements="$temp/dotnet.entitlements"
    cat > "$entitlements" <<ENT
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
	<key>com.apple.security.cs.allow-jit</key><true/>
	<key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
	<key>com.apple.security.cs.disable-library-validation</key><true/>
</dict></plist>
ENT
  fi
fi

# codesign reports "replacing existing signature" for every file: show its output only when it fails.
sign() {
  local output
  output="$(codesign "${options[@]}" "$@" 2>&1)" || { echo "$output" >&2; echo "sign-app.sh: codesign failed: ${*: -1}" >&2; exit 1; }
}

helper_entitlements() {
  local entry
  for entry in ${helpers[@]+"${helpers[@]}"}; do
    if [ "$app/Contents/${entry%%=*}" = "$1" ]; then printf '%s' "${entry#*=}"; return; fi
  done
}

# Deepest paths first (inside out); regular files only (a symlink is sealed as a link, never signed).
while IFS= read -r f; do
  [ "$f" = "$app/Contents/MacOS/$main" ] && continue
  case "$(file -b "$f")" in *Mach-O*) ;; *) continue ;; esac # not `file | grep -q`: SIGPIPE under pipefail
  extra=()
  if [ "$identity" != "-" ] && [ -n "$(helper_entitlements "$f")" ]; then
    extra=(--entitlements "$(helper_entitlements "$f")")
  fi
  sign ${extra[@]+"${extra[@]}"} "$f"
done < <(find "$app/Contents" -type f | awk -F/ '{ print NF "\t" $0 }' | sort -rn -k1,1 | cut -f2-)

bundle=()
[ "$identity" != "-" ] && bundle=(--entitlements "$entitlements")
sign ${bundle[@]+"${bundle[@]}"} "$app"
codesign --verify --deep --strict --verbose=2 "$app"
