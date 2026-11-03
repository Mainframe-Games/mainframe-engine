#!/usr/bin/env bash
# Runs inside the build/linux/Dockerfile container (started by build/linux/run.sh): the commands of ci.yml's
# build-test (ubuntu) and render-tests jobs. /src is the fresh copy of the checkout, /work the cached build tree.
set -euo pipefail
suite="$1"
shift

# One run at a time: every checkout and worktree shares /work (one warm build tree), so a second run waits here until
# the first ends (the lock is the open file: released however that run ends, even if its container is killed). The
# lock and its owner note are excluded from the sync below, which would otherwise delete them.
exec 9>>/work/.linux-tests.lock
if ! flock -n 9; then
  echo "::: Another Linux test run is using the shared build volume ($(cat /work/.linux-tests.owner 2>/dev/null || echo "owner unknown"))."
  echo "::: Waiting for it to finish (Ctrl-C gives up; docker ps --filter ancestor=mainframe-linux-tests lists the runs)..."
  flock 9
fi
echo "${HOST_CHECKOUT:-unknown checkout}, container $(hostname), since $(date -u +%H:%M:%SZ)" > /work/.linux-tests.owner

# The results directory (the host's artifacts/linux): emptied here, not on the host, since an earlier run's files are
# root's on a Linux host; at the end they are handed to the host user (HOST_OWNER, uid:gid, from run.sh).
find /out -mindepth 1 -delete

# Sync into the cached tree, keeping its bin/obj so rebuilds are incremental. By content, not time: the tree was last
# built from whichever checkout ran before this one, and tar keeps the host's mtimes, so an older-dated source would
# lose to that checkout's newer outputs (MSBuild's Inputs/Outputs and PreserveNewest copies compare timestamps; that is
# how a run once tested another checkout's .spv). --checksum without -t (-a minus times) rewrites only the files whose
# content differs and stamps them with the current time, so they are newer than every output; the rest keep theirs.
rsync -rlpgoD --checksum --delete --exclude 'bin/' --exclude 'obj/' --exclude 'artifacts/' --exclude 'TestResults/' \
  --exclude '/.linux-tests.*' /src/ /work/
# A tree last synced by time (an older inside.sh, from a checkout not yet on this one; its --delete also removes this
# marker) can hold sources dated before outputs built from other content: stamp every source once (one slow rebuild).
if [ ! -e /work/.linux-tests.synced ]; then
  echo "::: The build volume was last synced by an older build/linux/inside.sh; restamping its sources (full rebuild)."
  find /work \( -name bin -o -name obj \) -prune -o -type f ! -name '.linux-tests.*' -exec touch {} +
  touch /work/.linux-tests.synced
fi
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
    check --dir Examples/Demo/Assets/locale --msgfmt || status=1
  run_tests Tests/MainframeEngine.Editor.Tests -c Release --no-build "${hang[@]}" \
    --logger "trx;LogFileName=editor-tests.trx" --results-directory TestResults "$@" || status=1
  run_tests Tests/MainframeEngine.Video.Tests -c Release --no-build "${hang[@]}" \
    --logger "trx;LogFileName=video-tests.trx" --results-directory TestResults "$@" || status=1
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
[ -z "${HOST_OWNER:-}" ] || chown -R "$HOST_OWNER" /out || true # never fails the run
exit "$status"
