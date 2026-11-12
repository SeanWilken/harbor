# Installing harbor

Three roles: one **hub**, **agents** on every machine, and the **tray** on desktops. All
traffic stays on the tailnet; agents only connect out.

## 1. Hub

```sh
deploy/install-pg.sh     # once: PostgreSQL in a podman quadlet
deploy/install-hub.sh    # hub + its own agent as a systemd user service, tray at login
```

`install-hub.sh` builds the UI and the hub, installs them under `~/.local/share/harbor`, copies
`harbor.json` to `~/.config/harbor/hub.json` (mode 600) on the first run, and enables `harbor-hub`.
Re-run it to update. Optional:

- `loginctl enable-linger "$USER"`: keep the hub running when you're logged out.
- HTTPS for phones/iPads: `sudo tailscale serve --bg --https=7443 http://127.0.0.1:7425` (README).

## 2. Pair a machine

On the hub: tray → **Pair a device…**, or the web UI → Pending → **pair a device**. It shows a
one-time code (10 minutes, one use) and the hub address agents should use.

## 3. Windows (tray + agent)

Needs the .NET 10 SDK and Git (`winget install Microsoft.DotNet.SDK.10 Git.Git`) and Tailscale
logged in to the same tailnet. From a checkout of this repository:

```powershell
powershell -ExecutionPolicy Bypass -File deploy\install-windows.ps1 -Hub http://100.101.1.10:7420 -Code ABCD-EFGH
```

It builds `harbor-agent.exe` and `harbor-tray.exe` into `%LOCALAPPDATA%\harbor\bin`, writes
`%USERPROFILE%\.config\harbor\config.json` (roots: whichever of `~\source`, `~\Documents`,
`~\Developer`, `~\repos`, `C:\src` exist; pass `-Roots` to choose), adds a Startup shortcut and starts
the tray. The tray runs the agent hidden (no console window) and restarts it if it exits; "Agent log"
in its menu shows the agent's output. Without `-Code`, approve the machine in the hub's Pending tab,
or use the tray's **Pair with hub…** later.

Check: `& "$env:LOCALAPPDATA\harbor\bin\harbor-agent.exe" status`.

**Updates:** every agent reports its harbor build (git commit). When the hub knows newer harbor
commits (from any machine's clone, including unpushed local ones), the machine card shows "N newer"
and the tray shows it with an **Update harbor…** item. On Windows that runs
`deploy\update-windows.ps1`: `git pull --ff-only` in the checkout the installer ran from, then the
installer again (config kept). The tray's **Start at login** item toggles the Startup shortcut.

## 4. Linux machines and the Raspberry Pi (agent only)

```sh
dotnet publish src/Harbor.Agent -c Release -r linux-arm64 -o out   # linux-x64 for PCs
scp out/harbor-agent pi:~/.local/bin/
# on the machine:
harbor-agent pair ABCD-EFGH --hub http://100.101.1.10:7420 --config ~/.config/harbor/harbor.json
```

Then edit `roots` in that config, install `deploy/harbor-agent.service` (systemd user unit; steps
inside), and `loginctl enable-linger "$USER"` on a headless machine. `harbor-agent status` shows the
tether state; on a desktop add the tray (`deploy/harbor-tray.desktop`).

## What's not verified yet

- The Windows script and the tray on real Windows (both build for win-x64 here; neither has run on
  Windows).
- The tray icon on Raspberry Pi OS's panel.
