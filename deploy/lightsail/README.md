# Lightsail deployment

One Ubuntu 24.04 x86-64 instance runs one backend container. No AWS keys,
registry credentials, database service, or load balancer are required.
GitHub Actions builds and tests the image, then produces a downloadable bundle.
The image contains backend code only; never upload saves, keys, or game assets to GitHub.

## Provision and install

1. Create a Lightsail Linux/Ubuntu 24.04 instance (1 GB / $7 per month is the
   initial small-server option; capacity has not been benchmarked). Attach a static IPv4.
2. Restrict SSH 22 and game TCP 443 to approved source IPs in Lightsail's firewall.
   Review IPv6 rules separately. Do not expose 50051. Login currently trusts supplied
   identifiers rather than verifying Steam tickets/passwords, so do not open game
   access to everyone. Docker published ports can bypass UFW; use Lightsail's firewall.
3. Download the successful `Build Lightsail image` workflow artifact using
   `gh run download RUN_ID -n nordicandia-lightsail-COMMIT_SHA` on your computer.
   Transfer the bundle to the instance using SCP. No GitHub token goes to AWS.
4. On the instance, run `sudo bash install-docker.sh`, then
   `sha256sum -c SHA256SUMS` and `sudo docker load -i nordicandia-server.tar.gz`.
5. Copy `compose.yaml` to `/opt/nordicandia/compose.yaml` and copy `env.example`
   to `/opt/nordicandia/.env`. Set its image tag from `image-tag.txt` and certificate
   password. Protect `.env` with `sudo chmod 600 /opt/nordicandia/.env`.

## Certificate and saved game

Install the TLS certificate as `/opt/nordicandia/certs/server.pfx` with ownership
`root:1654` and mode `640`. Kestrel handles HTTP/1.1 and HTTP/2 directly on port 8443,
mapped to host 443. Use a certificate for your chosen hostname and automate renewal
plus PFX conversion and container restart. For the already patched private client,
a self-signed certificate also works (its certificate validation is disabled);
this does not provide server identity verification.

Stop the local game/server before copying the current `world.json` into
`/opt/nordicandia/data/world.json`. Keep a local backup and change the uploaded
file's ownership to `1654:1654` and mode to `600`. Do not copy lock files or logs.
Never overwrite a running cloud server's save with an older local copy.

## Start and verify

From `/opt/nordicandia`, run `sudo docker compose up -d` and test
`curl --cacert /path/to/server.crt https://localhost/healthz` for a localhost test
certificate, or use your real hostname with a matching trusted certificate.
After the cloud firewall is restricted, set `NORD_BIND_IP=0.0.0.0` in `.env` and
run `sudo docker compose up -d` to accept IPv4 connections from allowed players.

Patch a fresh desktop copy to the static IP or hostname (port 443) with
`server/prepare-desktop.mjs`; reapply `server/patch-desktop-magic-find.mjs` with
multiplier `10` if desired. Verify login, existing characters, merchant purchases,
realtime gameplay, relogin, and persistence after a container/host restart.
The CI check covers TLS, gRPC merchant operations, and save retention on container
recreation; the actual desktop gameplay test remains necessary.

## Back up and update

Back up `data/world.json` to a timestamped file before updates; copy backups off
the instance as well. For a consistent backup, stop the container, copy the save,
then start it. Do not run two instances against the same save directory.

Load a new image, change only `NORD_IMAGE` in `.env`, and run `sudo docker compose
up -d`. The data/certificates remain in host directories. To roll back, select
the previous image tag; if a future update migrates the save format, restore its
matching backup while the server is stopped. Retain the previous image and bundle.
The restart policy starts the service after a host reboot. Logs rotate at 3 x 10 MB.
Snapshots and excess outbound traffic can add charges beyond the instance plan.
