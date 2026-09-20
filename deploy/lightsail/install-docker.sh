#!/usr/bin/env bash
# Run with sudo on a fresh Ubuntu 24.04 instance.
set -euo pipefail
test "$(id -u)" = 0 || { echo 'Run with sudo.' >&2; exit 1; }
. /etc/os-release
test "$ID" = ubuntu || { echo 'This installer requires Ubuntu.' >&2; exit 1; }
apt-get update
apt-get install -y ca-certificates curl openssl
install -m 0755 -d /etc/apt/keyrings
curl -fsSL https://download.docker.com/linux/ubuntu/gpg -o /etc/apt/keyrings/docker.asc
chmod a+r /etc/apt/keyrings/docker.asc
cat > /etc/apt/sources.list.d/docker.sources <<EOF
Types: deb
URIs: https://download.docker.com/linux/ubuntu
Suites: ${UBUNTU_CODENAME:-$VERSION_CODENAME}
Components: stable
Architectures: $(dpkg --print-architecture)
Signed-By: /etc/apt/keyrings/docker.asc
EOF
apt-get update
apt-get install -y docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin
systemctl enable --now docker
install -d -m 0750 /opt/nordicandia
install -d -o 1654 -g 1654 -m 0700 /opt/nordicandia/data
install -d -o root -g 1654 -m 0750 /opt/nordicandia/certs
install -d -o root -g root -m 0700 /opt/nordicandia/backups
docker compose version
