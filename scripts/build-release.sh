#!/usr/bin/env bash
# Builds and packs UaWebSocket in Release configuration into ./artifacts.
# Can be invoked from any directory (repo root, scripts/, etc.).
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &>/dev/null && pwd)"

PROJECT="$REPO_ROOT/src/UaWebSocket.csproj"
OUTPUT_DIR="$REPO_ROOT/artifacts"
CONFIGURATION="Release"

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Remove obj/bin instead of `dotnet clean`, which can fail against a stale
# project.assets.json left over from a previous/different TargetFramework.
rm -rf "$REPO_ROOT/src/obj" "$REPO_ROOT/src/bin"

echo "Building $PROJECT ($CONFIGURATION)..."
dotnet restore "$PROJECT"
dotnet build "$PROJECT" -c "$CONFIGURATION" --no-restore

echo "Packing..."
dotnet pack "$PROJECT" -c "$CONFIGURATION" --no-build -o "$OUTPUT_DIR"

echo "Packages created in $OUTPUT_DIR:"
ls -1 "$OUTPUT_DIR"
