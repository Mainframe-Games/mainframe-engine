#!/usr/bin/env bash
# Packages a GameHost game for players: a self-contained `dotnet publish` of its desktop project for one RID, then
#   osx-*     <Name>.app (Info.plist from project.mfproj, .icns from a PNG, ad-hoc codesign) zipped as <exe>-<version>-<rid>.zip
#   win-*     <exe>.exe + its files in <exe>-<version>-<rid>.zip
#   linux-*   <exe> + its files in <exe>-<version>-<rid>.tar.gz (also the dedicated server: run it with --headless)
# Usage: build/package-game.sh <Desktop.csproj> <rid> <out-dir> [--name "Display name"] [--exe Name] [--version v]
#        [--bundle-id id] [--copyright text] [--icon icon.png]
# The icon PNG becomes the .app's .icns (macOS sips + iconutil) and the Windows exe's icon (ImageMagick `magick`).
# Defaults: name and version from the project.mfproj next to the desktop project's folder (one level up), the exe from
# the name without spaces, the bundle id com.mainframegames.<exe lowercased>, the icon from project.mfproj "window.icon".
# Without those tools the package has no icon (a warning on macOS). See docs/design/release.md (Games).
set -euo pipefail
csproj="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; rid="$2"; mkdir -p "$3"; out="$(cd "$3" && pwd)"; shift 3
project_dir="$(dirname "$(dirname "$csproj")")"
mfproj="$project_dir/project.mfproj"
[ -f "$mfproj" ] || { echo "error: no project.mfproj at $mfproj" >&2; exit 1; }
json() { sed -n "s/^  \"$1\": \"\(.*\)\",\{0,1\}$/\1/p" "$mfproj" | head -n 1; }

name="$(json name)"; version="$(json version)"; exe=""; bundle_id=""; copyright=""
icon="$(sed -n 's/^    "icon": "\(.*\)",\{0,1\}$/\1/p' "$mfproj" | head -n 1)"
[ -n "$icon" ] && icon="$project_dir/$icon"
while [ $# -gt 0 ]; do
  case "$1" in
    --name) name="$2"; shift 2 ;;
    --exe) exe="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --bundle-id) bundle_id="$2"; shift 2 ;;
    --copyright) copyright="$2"; shift 2 ;;
    --icon) icon="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done
[ -n "$name" ] || { echo "error: no \"name\" in $mfproj (pass --name)" >&2; exit 1; }
version="${version:-0.0.0}"
exe="${exe:-${name// /}}"
bundle_id="${bundle_id:-com.mainframegames.$(printf '%s' "$exe" | tr '[:upper:]' '[:lower:]')}"
assembly="$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$csproj" | head -n 1)"
assembly="${assembly:-$(basename "$csproj" .csproj)}"

stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
publish="$stage/publish"
echo "Publishing $name $version for $rid…"
extra=()
if [[ "$rid" == win-* ]] && [ -n "$icon" ] && [ -f "$icon" ] && command -v magick >/dev/null; then
  # The exe's icon is a build input on Windows (ApplicationIcon); multi-size .ico from the PNG.
  magick "$icon" -define icon:auto-resize=256,128,64,48,32,16 "$stage/icon.ico"
  extra+=("-p:ApplicationIcon=$stage/icon.ico")
fi
dotnet publish "$csproj" -c Release -r "$rid" --self-contained true -p:PublishTrimmed=false -o "$publish" --nologo -v quiet ${extra[@]+"${extra[@]}"}
# The apphost carries the managed entry point's path, so renaming it to the player-facing name keeps it working.
case "$rid" in
  win-*) [ "$assembly" = "$exe" ] || mv "$publish/$assembly.exe" "$publish/$exe.exe" ;;
  *) [ "$assembly" = "$exe" ] || mv "$publish/$assembly" "$publish/$exe"; chmod +x "$publish/$exe" ;;
esac
# The engine's own natives (enet, mfrmlui, mfsvg) are copied for every platform; keep only this RID's.
for lib in enet mfrmlui mfsvg; do
  case "$rid" in
    win-*) rm -f "$publish/lib$lib.so" "$publish/lib$lib.dylib" ;;
    linux-*) rm -f "$publish/$lib.dll" "$publish/lib$lib.dylib" ;;
    osx-*) rm -f "$publish/$lib.dll" "$publish/lib$lib.so" ;;
  esac
done

base="$exe-$version-$rid"
case "$rid" in
  osx-*)
    app="$stage/$name.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    cp -R "$publish"/. "$app/Contents/MacOS/"
    icon_key=""
    if [ -n "$icon" ] && [ -f "$icon" ] && command -v iconutil >/dev/null && command -v sips >/dev/null; then
      set_dir="$stage/icon.iconset"; mkdir -p "$set_dir"
      for s in 16 32 128 256 512; do
        sips -z "$s" "$s" "$icon" --out "$set_dir/icon_${s}x${s}.png" >/dev/null
        sips -z $((s * 2)) $((s * 2)) "$icon" --out "$set_dir/icon_${s}x${s}@2x.png" >/dev/null
      done
      iconutil -c icns "$set_dir" -o "$app/Contents/Resources/icon.icns"
      icon_key="<key>CFBundleIconFile</key><string>icon.icns</string>"
    else
      echo "warning: no app icon (needs a PNG and macOS sips/iconutil)" >&2
    fi
    cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
	<key>CFBundleName</key><string>$name</string>
	<key>CFBundleDisplayName</key><string>$name</string>
	<key>CFBundleIdentifier</key><string>$bundle_id</string>
	<key>CFBundleExecutable</key><string>$exe</string>
	$icon_key
	<key>CFBundlePackageType</key><string>APPL</string>
	<key>CFBundleShortVersionString</key><string>$version</string>
	<key>CFBundleVersion</key><string>$version</string>
	<key>NSHumanReadableCopyright</key><string>$copyright</string>
	<key>LSMinimumSystemVersion</key><string>11.0</string>
	<key>NSHighResolutionCapable</key><true/>
	<key>LSApplicationCategoryType</key><string>public.app-category.games</string>
</dict>
</plist>
PLIST
    # Ad-hoc signature: Apple Silicon refuses unsigned native code. Notarization needs a Developer ID (not done here).
    if command -v codesign >/dev/null; then codesign --force --deep --sign - "$app" >/dev/null; fi
    (cd "$stage" && rm -f "$out/$base.zip" && ditto -c -k --norsrc --noextattr --keepParent "$name.app" "$out/$base.zip" 2>/dev/null || zip -qry "$out/$base.zip" "$name.app")
    echo "$out/$base.zip"
    ;;
  win-*)
    mv "$publish" "$stage/$base"
    (cd "$stage" && rm -f "$out/$base.zip" && zip -qr "$out/$base.zip" "$base")
    echo "$out/$base.zip"
    ;;
  linux-*)
    mv "$publish" "$stage/$base"
    tar -C "$stage" -czf "$out/$base.tar.gz" "$base"
    echo "$out/$base.tar.gz"
    ;;
  *) echo "unsupported RID: $rid" >&2; exit 1 ;;
esac
