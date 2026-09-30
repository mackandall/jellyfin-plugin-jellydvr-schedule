#!/bin/bash
# Installs the built plugin into Jellyfin and restarts the server (stop, copy, start). Run with sudo.
set -euxo pipefail
SRC="$(dirname "$(readlink -f "$0")")/bin/Release/net10.0"
DEST=/var/lib/jellyfin/plugins/DvrSchedule_1.0.0.0
# Stop first: overwriting the DLL under a running server makes it fail on shutdown
# ("Bad IL range") because the runtime still reads code from the replaced file.
systemctl stop jellyfin
# Start again even if a copy step fails, so a failed install never leaves Jellyfin down.
trap 'systemctl start jellyfin' EXIT
mkdir -p "$DEST"
cp "$SRC/Jellyfin.Plugin.DvrSchedule.dll" "$SRC/meta.json" "$SRC/icon.png" "$DEST/"
chown -R jellyfin:jellyfin "$DEST"
ls -la "$DEST"
