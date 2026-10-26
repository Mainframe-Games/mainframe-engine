#!/usr/bin/env bash
# Adds the editor in this folder to the desktop's application launcher (no root): a .desktop entry and its icons under
# ~/.local/share. Run it again after moving the folder; --uninstall removes both.
# Shipped as install.sh in the Linux release archive (build/package-editor.sh). The entry's name and StartupWMClass
# match the window class the editor sets (EditorBrand.AppId), so the task bar shows its icon.
set -euo pipefail

app_id="com.mainframegames.editor"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
data_home="${XDG_DATA_HOME:-$HOME/.local/share}"
desktop_file="$data_home/applications/$app_id.desktop"
icon_sizes=(32 48 256 512)

refresh() {
  if command -v update-desktop-database >/dev/null; then update-desktop-database -q "$data_home/applications" || true; fi
  # A newer folder time makes icon themes rescan; no icon-theme.cache here (it would go stale for other apps' icons).
  if [[ -d "$data_home/icons/hicolor" ]]; then touch "$data_home/icons/hicolor"; fi
}

if [[ "${1:-}" == "--uninstall" ]]; then
  rm -f "$desktop_file"
  for size in "${icon_sizes[@]}"; do rm -f "$data_home/icons/hicolor/${size}x${size}/apps/$app_id.png"; done
  refresh
  echo "Removed $desktop_file"
  exit 0
elif [[ $# -gt 0 ]]; then
  echo "usage: $0 [--uninstall]" >&2
  exit 2
fi

exe="$here/MainframeEngine.Editor"
if [[ ! -x "$exe" ]]; then
  echo "error: $exe not found or not executable; run this script from the extracted editor folder" >&2
  exit 1
fi
for size in "${icon_sizes[@]}"; do
  if [[ ! -f "$here/Assets/Brand/logo-$size.png" ]]; then
    echo "error: $here/Assets/Brand/logo-$size.png not found" >&2
    exit 1
  fi
done

# Desktop Entry spec: Exec quotes the program and escapes ", `, $ and \ inside the quotes with a backslash; then, like
# every string value (Path too), each backslash is escaped again. So `a$b` is written "a\\$b".
string_escape() { local v="$1"; printf '%s' "${v//\\/\\\\}"; }
exec_quoted="$exe"
exec_quoted="${exec_quoted//\\/\\\\}"
exec_quoted="${exec_quoted//\"/\\\"}"
exec_quoted="${exec_quoted//\`/\\\`}"
exec_quoted="${exec_quoted//\$/\\\$}"
exec_quoted="$(string_escape "\"$exec_quoted\"")"
path_escaped="$(string_escape "$here")"

# Quoted replacements: bash 5.2's patsub_replacement would otherwise expand & in a path.
entry="$(<"$here/$app_id.desktop.in")"
entry="${entry//@EXEC@/"$exec_quoted"}"
entry="${entry//@PATH@/"$path_escaped"}"
mkdir -p "$data_home/applications"
printf '%s\n' "$entry" > "$desktop_file"

for size in "${icon_sizes[@]}"; do
  mkdir -p "$data_home/icons/hicolor/${size}x${size}/apps"
  cp "$here/Assets/Brand/logo-$size.png" "$data_home/icons/hicolor/${size}x${size}/apps/$app_id.png"
done

refresh
echo "Installed $desktop_file"
echo "Mainframe Editor is now in the application launcher (some desktops show it after the next log-in)."
