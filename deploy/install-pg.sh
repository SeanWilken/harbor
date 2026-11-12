#!/usr/bin/env sh
# Creates the password secret (once), installs the quadlet and starts harbor-pg.
# Prints the connection string to put in harbor.json under hub.database.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
if ! podman secret exists harbor-pg-password 2>/dev/null; then
  head -c 24 /dev/urandom | base64 | tr -d '/+=' | podman secret create harbor-pg-password - >/dev/null
fi
mkdir -p "$HOME/.config/containers/systemd"
cp "$here/harbor-pg.container" "$HOME/.config/containers/systemd/"
systemctl --user daemon-reload
systemctl --user start harbor-pg
pw=$(podman secret inspect --showsecret --format '{{.SecretData}}' harbor-pg-password)
echo "Host=127.0.0.1;Port=5432;Database=harbor;Username=harbor;Password=$pw"
