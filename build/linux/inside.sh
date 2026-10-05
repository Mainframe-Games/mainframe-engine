#!/usr/bin/env bash
# Runs inside the build/linux/Dockerfile container (started by build/linux/run.sh): the commands of ci.yml's
# build-test (ubuntu) and render-tests jobs. /src is the fresh copy of the checkout, /work the cached build tree.
set -euo pipefail
suite="$1"
shift

# Sync into the cached tree, keeping its bin/obj so rebuilds are incremental.
rsync -a --delete --exclude 'bin/' --exclude 'obj/' --exclude 'artifacts/' --exclude 'TestResults/' /src/ /work/
cd /work
git config --global --add safe.directory '*'
rm -rf artifacts TestResults

uname -m
vulkaninfo --summary 2>/dev/null | grep -E 'deviceName|driverInfo' || true

dotnet restore MainframeEngine.slnx
dotnet tool restore
dotnet build MainframeEngine.slnx -c Release --no-restore -warnaserror -p:CompileShaders=false

# Under x86_64 emulation (Rosetta) the parallel unit-test process occasionally stalls with every thread idle (seen
# right after ~190 tests, while tests start processes, SDL and sockets in parallel; never on native x64 CI). Blame
# turns a stall into an aborted run, and an aborted run (not a failed test) is retried once.
hang=(--blame-hang-timeout 5m --blame-hang-dump-type none)
run_tests() {
  local log
  log="$(mktemp)"
  if dotnet test "$@" 2>&1 | tee "$log"; [ "${PIPESTATUS[0]}" -eq 0 ]; then return 0; fi
  if grep -q "Test Run Aborted" "$log"; then
    echo "::: The test process stalled or crashed (x86_64 emulation?); running $1 once more."
    dotnet test "$@"
    return
  fi
  return 1
}

status=0
if [ "$suite" = all ] || [ "$suite" = unit ]; then
  run_tests Tests/MainframeEngine.Tests -c Release --no-build "${hang[@]}" \
    --logger "trx;LogFileName=unit-tests.trx" --results-directory TestResults "$@" || status=1
  dotnet run --project Tools/MainframeEngine.L10n -c Release --no-build -- \
    check --dir MainframeEngine.Sandbox/Content/locale --msgfmt || status=1
  run_tests Tests/MainframeEngine.Editor.Tests -c Release --no-build "${hang[@]}" \
    --logger "trx;LogFileName=editor-tests.trx" --results-directory TestResults "$@" || status=1
fi

if [ "$suite" = all ] || [ "$suite" = render ]; then
  export RENDER_TEST_ARTIFACTS=/work/artifacts/render-tests
  xvfb-run -a -s "-screen 0 1280x1024x24" \
    dotnet test Tests/MainframeEngine.RenderTests -c Release --no-build "${hang[@]}" \
      --logger "trx;LogFileName=render-tests.trx" --logger "console;verbosity=normal" \
      --results-directory TestResults "$@" || status=1

  new="artifacts/render-tests/new-goldens"
  if [ -d "$new" ] && [ -n "$(find "$new" -name '*.png')" ]; then
    echo "Frames without a golden (comparison skipped; review, then copy from artifacts/linux/render-tests/new-goldens/):"
    (cd "$new" && find . -name '*.png' | sed 's|^\./|  |' | sort)
  else
    echo "Every compared frame has a golden."
  fi
fi

mkdir -p /out
[ -d TestResults ] && cp -R TestResults /out/
[ -d artifacts/render-tests ] && cp -R artifacts/render-tests /out/
exit "$status"
