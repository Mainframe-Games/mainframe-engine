#!/usr/bin/env bash
# Zips Examples/Demo for a release (MainframeEngine.Demo-v<version>.zip in <out-dir>).
# Usage: build/package-demo.sh <version> <out-dir>. Kept for CI and old scripts: see build/package-example.sh.
exec "$(dirname "$0")/package-example.sh" Demo "$@"
