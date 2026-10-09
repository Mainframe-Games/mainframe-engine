#!/usr/bin/env bash
# The Forest benchmark (docs/design/forest.md#benchmark): builds Release, flies the benchmark spline at 1920 × 1080 with a
# fixed 60 Hz step and VSync off, prints frame-time percentiles, writes artifacts/forest-bench/<time>.json and compares
# p50/p99 with Examples/Forest/benchmark-baseline.json (fails when more than 10 % slower, or when a frame allocated).
# Quiet machine only; the display must be awake. Usage: build/forest-bench.sh [--write-baseline] [game args...]
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
dotnet build "$repo/Examples/Forest/Forest.slnx" -c Release -v q -nologo
bin="$repo/Examples/Forest/Forest.Desktop/bin/Release/net10.0"
mkdir -p "$repo/artifacts/forest-bench"
out="$repo/artifacts/forest-bench/forest-bench-$(date +%Y%m%d-%H%M%S).json"
cd "$bin"
dotnet Forest.Desktop.dll --fixed-fps 60 --no-vsync --no-log-file \
  ++ --benchmark --resolution 1920x1080 --no-capture --no-audio --out "$out" \
  --baseline "$repo/Examples/Forest/benchmark-baseline.json" "$@"
