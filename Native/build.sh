#!/usr/bin/env bash
# Local build of the native libraries (macOS / Linux). Windows: see docs/design/natives.md.
#
#   Native/build.sh [--build-dir DIR] [--stage] [cmake args...]
#
#   --build-dir DIR  build tree (default: Native/build, git-ignored)
#   --stage          copy the built libraries into MainframeEngine/runtimes/<rid>/native/ and refresh
#                    Native/natives.lock (macOS stages the universal dylibs under osx-arm64 AND osx-x64)
#
# macOS builds a universal (arm64 + x86_64) binary with a 11.0 deployment target.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
build_dir="$repo_root/Native/build"
stage=0
extra_args=()

while [[ $# -gt 0 ]]; do
	case "$1" in
		--build-dir) build_dir="$2"; shift 2 ;;
		--stage) stage=1; shift ;;
		*) extra_args+=("$1"); shift ;;
	esac
done

generator=()
if command -v ninja >/dev/null 2>&1; then
	generator=(-G Ninja)
fi

platform_args=()
case "$(uname -s)" in
	Darwin)
		platform_args=("-DCMAKE_OSX_ARCHITECTURES=arm64;x86_64" -DCMAKE_OSX_DEPLOYMENT_TARGET=11.0)
		rids=(osx-arm64 osx-x64)
		libs=(libenet.dylib libmfrmlui.dylib libmfsvg.dylib)
		;;
	Linux)
		rids=(linux-x64)
		libs=(libenet.so libmfrmlui.so libmfsvg.so)
		;;
	*)
		echo "Unsupported host: $(uname -s). On Windows use the commands in docs/design/natives.md." >&2
		exit 1
		;;
esac

cmake -S "$repo_root/Native" -B "$build_dir" "${generator[@]}" -DCMAKE_BUILD_TYPE=Release \
	"${platform_args[@]}" "${extra_args[@]+"${extra_args[@]}"}"
cmake --build "$build_dir" --config Release --parallel
ctest --test-dir "$build_dir" --build-config Release --output-on-failure

if [[ $stage -eq 1 ]]; then
	staged=()
	for rid in "${rids[@]}"; do
		dest="$repo_root/MainframeEngine/runtimes/$rid/native"
		mkdir -p "$dest"
		for lib in "${libs[@]}"; do
			found="$(find "$build_dir" -name "$lib" -type f -not -path '*/CMakeFiles/*' | head -n 1)"
			if [[ -z "$found" ]]; then
				echo "skip $lib (not built in $build_dir)"
				continue
			fi
			cp "$found" "$dest/$lib"
			staged+=("$dest/$lib")
			echo "staged $dest/$lib"
		done
	done
	"$repo_root/Native/natives-lock.sh" update --stamp ${staged[@]+"${staged[@]}"}
fi
