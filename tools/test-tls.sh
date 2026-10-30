#!/usr/bin/env bash
# Live TLS test: generates a self-signed certificate, starts a TLS TERN peer and
# an HTTPS-ish server, then runs tools/tls/probe.tern in the interpreter and in a
# compiled binary and diffs the two outputs.
#
# The golden suite covers the option checks and the failure paths; this covers
# the handshake, the certificate checks and http.exchange over TLS, which need a
# real certificate. It needs openssl and python3, so it is not part of
# tools/test.sh.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
PORT="${TERN_TLS_PORT:-9461}"
HTTP_PORT="$((PORT + 1))"
WEB_PORT="$((PORT + 2))"
SERVERS=()

cleanup() {
  for pid in "${SERVERS[@]:-}"; do
    kill "$pid" 2>/dev/null || true
  done
  rm -rf "$WORK"
}
trap cleanup EXIT

echo "tls: generating a certificate in $WORK"
openssl req -x509 -newkey rsa:2048 -nodes -keyout "$WORK/key.pem" -out "$WORK/cert.pem" \
  -days 2 -subj "/CN=localhost" -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
  >/dev/null 2>&1

python3 "$HERE/tools/tls/server.py" "$PORT" echo "$WORK/cert.pem" "$WORK/key.pem" &
SERVERS+=($!)
python3 "$HERE/tools/tls/server.py" "$HTTP_PORT" http "$WORK/cert.pem" "$WORK/key.pem" &
SERVERS+=($!)
python3 "$HERE/tools/tls/server.py" "$WEB_PORT" web "$WORK/cert.pem" "$WORK/key.pem" &
SERVERS+=($!)

# Wait for both ports to answer a connection instead of sleeping blindly.
for port in "$PORT" "$HTTP_PORT" "$WEB_PORT"; do
  for _ in $(seq 1 50); do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then
      exec 3>&-
      break
    fi
    sleep 0.1
  done
done

sed -e "s#__PORT__#$PORT#g" -e "s#__HTTP_PORT__#$HTTP_PORT#g" -e "s#__WEB_PORT__#$WEB_PORT#g" \
  -e "s#__CERT__#$WORK/cert.pem#g" \
  "$HERE/tools/tls/probe.tern" > "$WORK/probe.tern"

echo "tls: running the probe in the interpreter"
(cd "$HERE" && ./tools/tern "$WORK/probe.tern") > "$WORK/interpreter.out" 2>&1

echo "tls: compiling and running the probe natively"
python3 "$HERE/native/ternc.py" "$WORK/probe.tern" -o "$WORK/probe" >/dev/null
"$WORK/probe" > "$WORK/native.out" 2>&1

if diff -u "$WORK/interpreter.out" "$WORK/native.out"; then
  echo "tls: both back ends agree"
  cat "$WORK/interpreter.out"
else
  echo "tls: the back ends disagree" >&2
  exit 1
fi
