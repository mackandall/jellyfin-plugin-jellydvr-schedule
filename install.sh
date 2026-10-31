#!/bin/bash
# Installs the built plugin into Jellyfin and restarts the server (stop, copy, start). Run with sudo.
set -euxo pipefail
SRC="$(dirname "$(readlink -f "$0")")/bin/Release/net10.0"
PLUGINS=/var/lib/jellyfin/plugins
# The folder Jellyfin loads: the one with this plugin's DLL and the highest version in its
# name (a catalog install makes "<Name>_<version>"); JellyDVR Schedule_1.0.2.0 when there's none yet.
DEST=$(ls -d "$PLUGINS"/*/Jellyfin.Plugin.DvrSchedule.dll 2>/dev/null | xargs -r -d '\n' -n1 dirname | sort -t_ -k2 -V | tail -1 || true)
DEST=${DEST:-$PLUGINS/JellyDVR Schedule_1.0.2.0}
# Stop first: overwriting the DLL under a running server makes it fail on shutdown
# ("Bad IL range") because the runtime still reads code from the replaced file.
systemctl stop jellyfin
# Start again even if a copy step fails, so a failed install never leaves Jellyfin down.
trap 'systemctl start jellyfin' EXIT
mkdir -p "$DEST"
# Older copies of this plugin would be loaded instead of (or beside) this one: remove them.
ls -d "$PLUGINS"/*/Jellyfin.Plugin.DvrSchedule.dll 2>/dev/null | xargs -r -d '\n' -n1 dirname | grep -vxF "$DEST" | xargs -r -d '\n' rm -rf || true
cp "$SRC/Jellyfin.Plugin.DvrSchedule.dll" "$SRC/meta.json" "$SRC/icon.png" "$DEST/"
chown -R jellyfin:jellyfin "$DEST"
ls -la "$DEST"
