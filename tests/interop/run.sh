#!/usr/bin/env bash
# Checks the Windows client's protocol stack against omaclip's own Go code, in both directions:
# HTTPS sync (TLS trust, passphrase header, manifest checksum, payload download) and mDNS discovery.
# Needs Go and the .NET SDK. Runs on Linux or macOS (the code under test is OS-independent).
set -euo pipefail
cd "$(dirname "$0")"
OMACLIP_REF="${OMACLIP_REF:-v0.10.0}"
PASS="interop passphrase"
WORK="$(mktemp -d)"
trap 'kill $(jobs -p) 2>/dev/null || true; rm -rf "$WORK"' EXIT

if [ ! -d .omaclip ]; then
  git clone --quiet --depth 1 --branch "$OMACLIP_REF" https://github.com/rhemvi/omaclip .omaclip
fi
(cd omaclip-peer && go build -o "$WORK/omaclip-peer" .)
dotnet build InteropCli -c Release -o "$WORK/cli" --nologo -v quiet >/dev/null
CLI=(dotnet "$WORK/cli/InteropCli.dll")

fail() { echo "FAIL: $*"; exit 1; }
expect() { grep -qF -- "$2" <<<"$1" || { echo "$1"; fail "expected: $2"; }; }
wait_for_port() { for _ in $(seq 50); do grep -q listening "$1" 2>/dev/null && { awk '/listening/{print $2}' "$1"; return; }; sleep 0.2; done; fail "no server in $1"; }
lan_ip() { hostname -I 2>/dev/null | awk '{print $1}' || true; }

echo "== omaclip serves, Windows client fetches and discovers"
"$WORK/omaclip-peer" serve "$PASS" 0 >"$WORK/go.log" 2>&1 &
GO_PID=$!
PORT=$(wait_for_port "$WORK/go.log")
OUT=$("${CLI[@]}" fetch "$PASS" "127.0.0.1:$PORT")
expect "$OUT" 'text "hello from omaclip <&> ✓" 2026-11-21T12:00:00.123456789Z'
expect "$OUT" 'image image/png 11-bytes'
expect "$OUT" 'second fetch: unchanged'
if "${CLI[@]}" fetch "wrong passphrase" "127.0.0.1:$PORT" >/dev/null 2>&1; then fail "a wrong passphrase was accepted"; fi
OUT=$("${CLI[@]}" discover "$PASS" 8)
expect "$OUT" "peer omaclip-interop-$PORT"
kill -INT $GO_PID; wait $GO_PID 2>/dev/null || true

echo "== Windows client serves, omaclip fetches and discovers"
mkfifo "$WORK/stdin"
"${CLI[@]}" serve "$PASS" 0 <"$WORK/stdin" >"$WORK/net.log" 2>&1 &
exec 3>"$WORK/stdin"
PORT=$(wait_for_port "$WORK/net.log")
OUT=$("$WORK/omaclip-peer" fetch "$PASS" "127.0.0.1:$PORT")
expect "$OUT" 'windows text "hello from windows <&> ✓"'
expect "$OUT" 'windows image image/png 16-bytes-base64'
IP=$(lan_ip)
if [ -n "$IP" ]; then
  OUT=$("$WORK/omaclip-peer" fetch "$PASS" "$IP:$PORT")
  expect "$OUT" 'windows text "hello from windows <&> ✓"'
fi
if timeout 15 "$WORK/omaclip-peer" fetch "wrong passphrase" "127.0.0.1:$PORT" >/dev/null 2>&1; then fail "omaclip accepted a wrong passphrase"; fi
OUT=$("$WORK/omaclip-peer" discover "$PASS" 8)
expect "$OUT" "peer windows-interop-$PORT"
grep -q "omaclip-discover-1@" "$WORK/net.log" || { cat "$WORK/net.log"; fail "Windows client did not discover omaclip"; }
exec 3>&-

echo "All interop checks passed against omaclip $OMACLIP_REF."
