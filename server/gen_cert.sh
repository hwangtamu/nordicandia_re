#!/usr/bin/env bash
# Generate a self-signed TLS cert (and PFX) for the patched server hostnames.
# Usage: ./gen_cert.sh [prod-host] [staging-host]
set -e
PROD="${1:-ab.10-0-2-2.sslip.io}"
STAGING="${2:-abcde.10-0-2-2.sslip.io}"
OUT="$(dirname "$0")/certs"
mkdir -p "$OUT"
cd "$OUT"

openssl req -x509 -newkey rsa:2048 -keyout key.pem -out cert.pem -days 3650 -nodes \
  -subj "/CN=${PROD}" \
  -addext "subjectAltName=DNS:${PROD},DNS:${STAGING}"

openssl pkcs12 -export -out nordicandia-server.pfx -inkey key.pem -in cert.pem -passout pass:nordpass

echo
echo "cert: $OUT/cert.pem"
echo "pfx : $OUT/nordicandia-server.pfx  (password: nordpass)"
echo
echo "Run the server with TLS on :443 (needs root/CAP_NET_BIND_SERVICE):"
echo "  sudo -E NORD_CERT_PFX=$OUT/nordicandia-server.pfx NORD_CERT_PWD=nordpass \\"
echo "       dotnet bin/Release/net10.0/Nordicandia.Server.dll"
echo
echo "Optionally redirect privileged port to the h2c dev port instead:"
echo "  sudo iptables -t nat -A PREROUTING -p tcp --dport 443 -j REDIRECT --to-port 8443"
