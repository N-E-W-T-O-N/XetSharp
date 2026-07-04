#!/usr/bin/env bash
# Produces the split NuGet package set into ./artifacts:
#   XetSharp                     (meta: managed dll + runtime.json, no native)
#   XetSharp.runtime.linux-x64   (only the x64 native)
#   XetSharp.runtime.linux-arm64 (only the arm64 native)
#
# A consumer restores the meta package; runtime.json pulls in ONLY the runtime package matching
# their RID, so no cross-arch bytes are downloaded.
set -euo pipefail
cd "$(dirname "$0")/.."
out="artifacts"

echo ">> release-building native libs (x64 + arm64)"
./scripts/build-native.sh --release x64 arm64

echo ">> packing"
dotnet pack XetSharp/XetSharp.csproj -c Release -o "$out"
dotnet pack packaging/XetSharp.runtime.linux-x64/XetSharp.runtime.linux-x64.csproj -c Release -o "$out"
dotnet pack packaging/XetSharp.runtime.linux-arm64/XetSharp.runtime.linux-arm64.csproj -c Release -o "$out"

echo ">> done:"
ls -1 "$out"/*.nupkg
