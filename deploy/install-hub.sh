#!/usr/bin/env sh
# Install (or update) the harbor hub as a systemd user service on this machine, plus the tray at login.
#   deploy/install-hub.sh            # from the repository root or anywhere in it
# Needs: .NET 10 SDK, bun (UI build), harbor-pg running (deploy/install-pg.sh). The hub config is
# copied once from ./harbor.json to ~/.config/harbor/hub.json (mode 600: it holds the database password).
# The service runs while you're logged in; `loginctl enable-linger "$USER"` keeps it up when you're not.
set -eu
repo=$(cd "$(dirname "$0")/.." && pwd)
share="$HOME/.local/share/harbor"
cfgdir="$HOME/.config/harbor"

echo "building the UI and the hub..."
(cd "$repo/ui" && bun install --frozen-lockfile >/dev/null && bun run build >/dev/null)
dotnet publish "$repo/src/Harbor" -c Release -r linux-x64 --self-contained -p:PublishSingleFile=true -o "$share/bin.new" --nologo -v q
dotnet publish "$repo/src/Harbor.Tray" -c Release -r linux-x64 -o "$share/bin.new" --nologo -v q
rm -rf "$share/ui" && cp -r "$repo/ui/dist" "$share/ui"

if [ ! -f "$cfgdir/hub.json" ]; then
    mkdir -p "$cfgdir"
    cp "$repo/harbor.json" "$cfgdir/hub.json"
    chmod 600 "$cfgdir/hub.json"
    echo "config: $cfgdir/hub.json (copied from harbor.json)"
fi

systemctl --user stop harbor-hub 2>/dev/null || true
rm -rf "$share/bin" && mv "$share/bin.new" "$share/bin"
mkdir -p "$HOME/.config/systemd/user" "$HOME/.config/autostart" "$HOME/.local/bin"
cp "$repo/deploy/harbor-hub.service" "$HOME/.config/systemd/user/"
ln -sf "$share/bin/harbor-tray" "$HOME/.local/bin/harbor-tray"
cp "$repo/deploy/harbor-tray.desktop" "$HOME/.config/autostart/"
# where this install came from, for the tray's "Update harbor..."
printf '{ "repo": "%s", "installedAt": "%s" }\n' "$repo" "$(date -Iseconds)" > "$share/install.json"
systemctl --user daemon-reload
systemctl --user enable --now harbor-hub
echo "harbor-hub: $(systemctl --user is-active harbor-hub). Logs: journalctl --user -u harbor-hub -f"

# a running tray keeps the old build until restarted; restart it in the background, detached
if pgrep -x harbor-tray >/dev/null 2>&1; then
    pkill -x harbor-tray || true
    if [ -n "${DISPLAY:-}${WAYLAND_DISPLAY:-}" ]; then
        setsid -f "$HOME/.local/bin/harbor-tray" >/dev/null 2>&1 </dev/null
        echo "tray: restarted"
    else
        echo "tray: stopped (no display here); start it with: harbor-tray &"
    fi
else
    echo "tray: starts at login; start it now with: harbor-tray &"
fi
