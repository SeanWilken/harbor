#!/usr/bin/env sh
# Update harbor on this Linux machine: fast-forward the checkout it was installed from, then re-run
# the installer (rebuild, keep config, restart the hub and the tray). The tray's "Update harbor..." runs this.
set -eu
repo=$(cd "$(dirname "$0")/.." && pwd)
echo "Updating $repo ..."
if ! git -C "$repo" pull --ff-only; then
    echo "git pull --ff-only failed (local changes or diverged?); resolve in $repo and re-run" >&2
    exit 1
fi
"$repo/deploy/install-hub.sh"
echo "Done. This window can be closed."
