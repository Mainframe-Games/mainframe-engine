#!/usr/bin/env bash
# Packages a GameHost game for players: a self-contained `dotnet publish` of its desktop project for one RID, then
#   osx-*     <Name>.app (the apphost in Contents/MacOS, the rest in Contents/Resources; Info.plist from project.mfproj,
#             .icns from a PNG, ad-hoc codesign) zipped as <exe>-<version>-<rid>.zip
#   win-*     <exe>.exe (GUI subsystem: no console window) + its files in <exe>-<version>-<rid>.zip
#   linux-*   <exe> + its files in <exe>-<version>-<rid>.tar.gz (also the dedicated server: run it with --headless)
# Usage: build/package-game.sh <Desktop.csproj> <rid> <out-dir> [--name "Display name"] [--exe Name] [--version v]
#        [--bundle-id id] [--copyright text] [--icon icon.png] [--sign "Developer ID Application: …"] [--notarize profile]
# The icon PNG becomes the .app's .icns (macOS sips + iconutil) and the Windows exe's icon (ImageMagick `magick`), unless
# the desktop project names its own: <MacAppIcon> ending in .icns (ADR 0182) and <ApplicationIcon> (.ico) win over the PNG.
# Defaults: name and version from the project.mfproj next to the desktop project's folder (one level up), the exe from
# the name without spaces, the bundle id com.mainframegames.<exe lowercased>, the icon from project.mfproj "window.icon".
# Without those tools the package has no icon (a warning on macOS). See docs/design/release.md (Games).
# Games on the MainframeEngine package run the copy in its tools/ folder (as bash "<path>", the package drops the
# executable bit): dotnet msbuild <Desktop.csproj> -getProperty:MainframePackageGameScript prints the path.
# macOS signing: ad-hoc by default. --sign (or $MF_SIGN_IDENTITY) signs every Mach-O and the bundle with that identity, the
# hardened runtime and a secure timestamp (entitlements for .NET: JIT and unsigned executable memory). --notarize (or
# $MF_NOTARY_PROFILE) then submits the zip with `xcrun notarytool --keychain-profile <profile> --wait`, staples the
# ticket and re-zips. The profile comes from `xcrun notarytool store-credentials <profile>` (Apple ID, team, app password).
set -euo pipefail
csproj="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"; rid="$2"; mkdir -p "$3"; out="$(cd "$3" && pwd)"; shift 3
project_dir="$(dirname "$(dirname "$csproj")")"
mfproj="$project_dir/project.mfproj"
[ -f "$mfproj" ] || { echo "error: no project.mfproj at $mfproj" >&2; exit 1; }
json() { sed -n "s/^  \"$1\": \"\(.*\)\",\{0,1\}$/\1/p" "$mfproj" | head -n 1; }

name="$(json name)"; version="$(json version)"; exe=""; bundle_id=""; copyright=""
sign_identity="${MF_SIGN_IDENTITY:-}"; notary_profile="${MF_NOTARY_PROFILE:-}"
icon="$(grep -o '"icon"[[:space:]]*:[[:space:]]*"[^"]*"' "$mfproj" | head -n 1 | sed 's/.*"\([^"]*\)"$/\1/')"
[ -n "$icon" ] && icon="$project_dir/$icon"
while [ $# -gt 0 ]; do
  case "$1" in
    --name) name="$2"; shift 2 ;;
    --exe) exe="$2"; shift 2 ;;
    --version) version="$2"; shift 2 ;;
    --bundle-id) bundle_id="$2"; shift 2 ;;
    --copyright) copyright="$2"; shift 2 ;;
    --icon) icon="$2"; shift 2 ;;
    --sign) sign_identity="$2"; shift 2 ;;
    --notarize) notary_profile="$2"; shift 2 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
done
[ -n "$name" ] || { echo "error: no \"name\" in $mfproj (pass --name)" >&2; exit 1; }
version="${version:-0.0.0}"
exe="${exe:-${name// /}}"
bundle_id="${bundle_id:-com.mainframegames.$(printf '%s' "$exe" | tr '[:upper:]' '[:lower:]')}"
assembly="$(sed -n 's:.*<AssemblyName>\(.*\)</AssemblyName>.*:\1:p' "$csproj" | head -n 1)"
assembly="${assembly:-$(basename "$csproj" .csproj)}"
csproj_value() { sed -n "s:.*<$1>\(.*\)</$1>.*:\1:p" "$csproj" | head -n 1; }
mac_icns="$(csproj_value MacAppIcon)"
case "$mac_icns" in *.icns) mac_icns="$(dirname "$csproj")/$mac_icns" ;; *) mac_icns="" ;; esac

stage="$(mktemp -d)"; trap 'rm -rf "$stage"' EXIT
publish="$stage/publish"
echo "Publishing $name $version for ${rid}…"
extra=()
# Windows: a GUI-subsystem exe (MainframeGame.props switches the launcher to WinExe), so players get no console window.
[[ "$rid" == win-* ]] && extra+=("-p:MainframeWindowsGui=true")
if [[ "$rid" == win-* ]] && [ -z "$(csproj_value ApplicationIcon)" ] && [ -n "$icon" ] && [ -f "$icon" ] && command -v magick >/dev/null; then
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
# A checkout build copies the engine's natives (enet, mfrmlui, mfsvg, steam_api) and both Steamworks builds
# (steamworks/win64, steamworks/posix) for every platform; keep only this RID's. A package build has only this RID's.
for lib in enet mfrmlui mfsvg; do
  case "$rid" in
    win-*) rm -f "$publish/lib$lib.so" "$publish/lib$lib.dylib" ;;
    linux-*) rm -f "$publish/$lib.dll" "$publish/lib$lib.dylib" ;;
    osx-*) rm -f "$publish/$lib.dll" "$publish/lib$lib.so" ;;
  esac
done
case "$rid" in
  win-*) rm -rf "$publish/libsteam_api.so" "$publish/libsteam_api.dylib" "$publish/steamworks/posix" ;;
  linux-*) rm -rf "$publish/steam_api64.dll" "$publish/libsteam_api.dylib" "$publish/steamworks/win64" ;;
  osx-*) rm -rf "$publish/steam_api64.dll" "$publish/libsteam_api.so" "$publish/steamworks/win64" ;;
esac
# steam_appid.txt lets a game start Steam outside the client (GameHost writes it in development runs only); a player
# build must never carry one.
if [ -e "$publish/steam_appid.txt" ]; then
  echo "error: steam_appid.txt in the publish output ($publish); remove it from the project's copied files" >&2
  exit 1
fi

base="$exe-$version-$rid"
case "$rid" in
  osx-*)
    app="$stage/$name.app"
    mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
    # Apple seals only Mach-O code in Contents/MacOS (a managed DLL there fails codesign --verify --strict): the apphost
    # goes there and everything else (assemblies, natives, content) in Contents/Resources. The apphost finds its app
    # through a symlink beside it; it resolves the real path, so Resources is the app's base directory.
    cp -R "$publish"/. "$app/Contents/Resources/"
    mv "$app/Contents/Resources/$exe" "$app/Contents/MacOS/$exe"
    ln -s "../Resources/$assembly.dll" "$app/Contents/MacOS/$assembly.dll"
    icon_key=""
    if [ -n "$mac_icns" ] && [ -f "$mac_icns" ]; then
      cp "$mac_icns" "$app/Contents/Resources/icon.icns"
      icon_key="<key>CFBundleIconFile</key><string>icon.icns</string>"
    elif [ -n "$icon" ] && [ -f "$icon" ] && command -v iconutil >/dev/null && command -v sips >/dev/null; then
      # bash, not the file's own mode: the MainframeEngine package (tools/) does not keep the executable bit.
      bash "$(dirname "$0")/macos/png-to-icns.sh" "$icon" "$app/Contents/Resources/icon.icns"
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
    zip_app() { (cd "$stage" && rm -f "$out/$base.zip" && ditto -c -k --norsrc --noextattr --keepParent "$name.app" "$out/$base.zip" 2>/dev/null || zip -qry "$out/$base.zip" "$name.app"); }
    if [ -n "$sign_identity" ]; then
      # Developer ID: inside out (every Mach-O, then the bundle), hardened runtime, the .NET entitlements.
      ent="$stage/entitlements.plist"
      cat > "$ent" <<ENT
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
	<key>com.apple.security.cs.allow-jit</key><true/>
	<key>com.apple.security.cs.allow-unsigned-executable-memory</key><true/>
	<key>com.apple.security.cs.disable-library-validation</key><true/>
</dict></plist>
ENT
      while IFS= read -r -d '' f; do
        if file -b "$f" | grep -q "Mach-O"; then
          codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$sign_identity" "$f"
        fi
      done < <(find "$app/Contents" -type f -print0)
      codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$sign_identity" "$app"
      codesign --verify --deep --strict "$app"
    elif command -v codesign >/dev/null; then
      # Ad-hoc signature: Apple Silicon refuses unsigned native code; players see Gatekeeper's warning until notarized.
      codesign --force --deep --sign - "$app" >/dev/null
      codesign --verify --deep --strict "$app"
    fi
    zip_app
    if [ -n "$notary_profile" ]; then
      [ -n "$sign_identity" ] || { echo "error: --notarize needs --sign (a Developer ID identity)" >&2; exit 1; }
      xcrun notarytool submit "$out/$base.zip" --keychain-profile "$notary_profile" --wait
      xcrun stapler staple "$app"
      zip_app # the stapled bundle
    fi
    echo "$out/$base.zip"
    ;;
  win-*)
    mv "$publish" "$stage/$base"
    (cd "$stage" && rm -f "$out/$base.zip" && zip -qr "$out/$base.zip" "$base")
    echo "$out/$base.zip"
    ;;
  linux-*)
    mv "$publish" "$stage/$base"
    # No macOS metadata (extended attributes, AppleDouble files): GNU tar on Linux warns about every one.
    COPYFILE_DISABLE=1 tar --no-xattrs -C "$stage" -czf "$out/$base.tar.gz" "$base"
    echo "$out/$base.tar.gz"
    ;;
  *) echo "unsupported RID: $rid" >&2; exit 1 ;;
esac
