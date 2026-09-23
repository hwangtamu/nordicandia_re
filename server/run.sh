#!/usr/bin/env bash
# Build + run the Nordicandia private server and smoke-test it.
set -e
export DOTNET_ROOT=${DOTNET_ROOT:-/tmp/retools/dotnet10}
export PATH="$DOTNET_ROOT:$PATH"
cd "$(dirname "$0")"

dotnet build Nordicandia.Server -c Release -v q
dotnet build Nordicandia.TestClient -c Release -v q

echo "Starting server on :50051 ..."
dotnet Nordicandia.Server/bin/Release/net10.0/Nordicandia.Server.dll > /tmp/nordicandia-server.log 2>&1 &
SPID=$!
trap 'kill $SPID 2>/dev/null || true' EXIT
sleep 6

echo "Running smoke test ..."
dotnet Nordicandia.TestClient/bin/Release/net10.0/Nordicandia.TestClient.dll http://localhost:50051

echo
echo "Server log tail:"
tail -n 8 /tmp/nordicandia-server.log
