#!/usr/bin/env bash
# Makes a laptop behave like an always-on server: it keeps running with the lid closed, never suspends or
# hibernates, and Docker starts at boot. Run once, as root, on the laptop (Debian / Ubuntu with systemd):
#
#   sudo scripts/laptop-server-setup.sh
#
# Undo: sudo rm /etc/systemd/logind.conf.d/xerp-server.conf && sudo systemctl unmask sleep.target \
#       suspend.target hibernate.target hybrid-sleep.target && sudo systemctl restart systemd-logind
set -euo pipefail

if [[ $EUID -ne 0 ]]; then
    echo "Run as root: sudo $0" >&2
    exit 1
fi

echo "== Lid closed: keep running"
mkdir -p /etc/systemd/logind.conf.d
cat > /etc/systemd/logind.conf.d/xerp-server.conf <<'EOF'
[Login]
HandleLidSwitch=ignore
HandleLidSwitchExternalPower=ignore
HandleLidSwitchDocked=ignore
IdleAction=ignore
EOF
systemctl restart systemd-logind

echo "== Never suspend or hibernate"
systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target

echo "== Docker at boot"
if systemctl list-unit-files docker.service >/dev/null 2>&1; then
    systemctl enable --now docker
else
    echo "Docker is not installed: https://docs.docker.com/engine/install/" >&2
fi

echo "== Tailscale at boot"
if systemctl list-unit-files tailscaled.service >/dev/null 2>&1; then
    systemctl enable --now tailscaled
else
    echo "Tailscale is not installed: https://tailscale.com/download/linux" >&2
fi

echo "Done. Close the lid and check from another device: tailscale ping $(hostname)"
