#!/usr/bin/env bash
# Builds the xetcore_native shared library for one or more Linux targets.
#
#   ./scripts/build-native.sh                    # debug, host (linux-x64) — for local dev/test
#   ./scripts/build-native.sh x64 arm64          # debug, both Linux targets
#   ./scripts/build-native.sh --release x64 arm64 # release (stripped) — for packaging
#
# Output locations (consumed by XetSharp.csproj and the packaging/ runtime projects):
#   debug   x64   -> target/debug/libxetcore_native.so
#   debug   arm64 -> target/aarch64-unknown-linux-gnu/debug/libxetcore_native.so
#   release x64   -> target/x86_64-unknown-linux-gnu/release/libxetcore_native.so
#   release arm64 -> target/aarch64-unknown-linux-gnu/release/libxetcore_native.so
#
# Cross prerequisites (once): cmake, zig, cargo-zigbuild, and the rust targets:
#   apt-get install -y cmake python3-pip
#   python3 -m pip install --break-system-packages ziglang
#   cargo install cargo-zigbuild
#   rustup target add aarch64-unknown-linux-gnu x86_64-unknown-linux-gnu
set -euo pipefail
cd "$(dirname "$0")/.."

release=0
targets=()
for a in "$@"; do
  case "$a" in
    --release) release=1 ;;
    x64|arm64) targets+=("$a") ;;
    *) echo "unknown arg '$a' (expected: --release | x64 | arm64)" >&2; exit 2 ;;
  esac
done
[ ${#targets[@]} -eq 0 ] && targets=("x64")

flag=""; [ "$release" = 1 ] && flag="--release"
for t in "${targets[@]}"; do
  case "$t" in
    x64)
      if [ "$release" = 1 ]; then
        echo ">> linux-x64 (cargo build --release --target x86_64-unknown-linux-gnu)"
        cargo build --release --target x86_64-unknown-linux-gnu
      else
        echo ">> linux-x64 (cargo build)"
        cargo build
      fi
      ;;
    arm64)
      echo ">> linux-arm64 (cargo zigbuild $flag --target aarch64-unknown-linux-gnu)"
      rustup target add aarch64-unknown-linux-gnu >/dev/null 2>&1 || true
      cargo zigbuild $flag --target aarch64-unknown-linux-gnu
      ;;
  esac
done
