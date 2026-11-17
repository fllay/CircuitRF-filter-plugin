#!/usr/bin/env bash
# Build the circuitRF plugin binary into circuitrf-plugin/bin/<rid>/.
# Usage: tools/build-plugin.sh [rid ...]      (default: osx-arm64; also osx-x64, linux-x64, linux-arm64, win-x64)
# Needs the .NET 9 SDK. A macOS binary must be signed (ad hoc is enough) or Apple Silicon kills it on launch:
# on a Mac this script signs with codesign; elsewhere sign the result with `rcodesign sign in out`.
set -euo pipefail
cd "$(dirname "$0")/.."
for rid in "${@:-osx-arm64}"; do
  out="/tmp/filtsyn-plugin-$rid"
  dotnet publish src/FiltSyn.Plugin -c Release -r "$rid" --self-contained true \
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$out"
  exe=filtsyn-plugin; [[ $rid == win-* ]] && exe=filtsyn-plugin.exe
  mkdir -p "circuitrf-plugin/bin/$rid"
  cp "$out/$exe" "circuitrf-plugin/bin/$rid/$exe"
  if [[ $rid == osx-* ]] && command -v codesign >/dev/null; then
    codesign --force --sign - "circuitrf-plugin/bin/$rid/$exe"
  fi
  echo "built circuitrf-plugin/bin/$rid/$exe"
done
echo "Add each rid you built to \"run\" in circuitrf-plugin/plugin.json."
