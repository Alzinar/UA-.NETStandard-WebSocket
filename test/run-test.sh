#!/usr/bin/env bash
# Builds and runs the TestServer/TestClient WebSocket integration test end-to-end.
# Can be invoked from any directory (repo root, test/, etc.).
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" &>/dev/null && pwd)"
REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." &>/dev/null && pwd)"

SERVER_DIR="$REPO_ROOT/test/TestServer"
CLIENT_DIR="$REPO_ROOT/test/TestClient"
SERVER_LOG="$(mktemp)"
CLIENT_LOG="$(mktemp)"

SERVER_PID=""

cleanup() {
  if [[ -n "$SERVER_PID" ]] && kill -0 "$SERVER_PID" 2>/dev/null; then
    kill "$SERVER_PID" 2>/dev/null || true
    for _ in $(seq 1 10); do
      kill -0 "$SERVER_PID" 2>/dev/null || break
      sleep 0.5
    done
    kill -9 "$SERVER_PID" 2>/dev/null || true
    wait "$SERVER_PID" 2>/dev/null || true
  fi
  rm -rf "$SERVER_LOG" "$CLIENT_LOG"
}
trap cleanup EXIT

# start with fresh self-signed certificates every run to avoid stale-trust issues
# across different hosts/hostnames.
rm -rf "$SERVER_DIR/pki" "$CLIENT_DIR/pki"

echo "Building TestServer and TestClient..."
dotnet build "$SERVER_DIR" --nologo -v quiet
dotnet build "$CLIENT_DIR" --nologo -v quiet

# run the built DLLs directly (not `dotnet run`) so the process we background/kill
# below is the actual app, not a wrapper that may not forward signals to it.
SERVER_DLL="$(find "$SERVER_DIR/bin" -name 'TestServer.dll' -print -quit)"
CLIENT_DLL="$(find "$CLIENT_DIR/bin" -name 'TestClient.dll' -print -quit)"

echo "Starting server..."
(cd "$SERVER_DIR" && exec dotnet "$SERVER_DLL") >"$SERVER_LOG" 2>&1 &
SERVER_PID=$!

READY=0
for _ in $(seq 1 30); do
  if grep -q "Server - Started\." "$SERVER_LOG" 2>/dev/null; then
    READY=1
    break
  fi
  if ! kill -0 "$SERVER_PID" 2>/dev/null; then
    break
  fi
  sleep 1
done

if [[ "$READY" -ne 1 ]]; then
  echo "Server did not start in time. Log:"
  cat "$SERVER_LOG"
  exit 1
fi

echo "Running client..."
set +e
(cd "$CLIENT_DIR" && dotnet "$CLIENT_DLL") >"$CLIENT_LOG" 2>&1
CLIENT_EXIT=$?
set -e

cat "$CLIENT_LOG"

if [[ "$CLIENT_EXIT" -eq 0 ]]; then
  echo "TEST PASSED"
else
  echo "TEST FAILED (client exit code $CLIENT_EXIT)"
  echo "Server log:"
  cat "$SERVER_LOG"
fi

exit "$CLIENT_EXIT"