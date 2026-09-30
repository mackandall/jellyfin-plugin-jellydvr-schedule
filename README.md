# JellyDVR Schedule (Jellyfin plugin)

SageTV-style recording priorities and conflict handling for Jellyfin 12. Jellyfin stores a priority on series
recordings but never uses it: when more recordings are scheduled than there are tuner inputs, whichever starts
last simply fails. This plugin looks ahead and resolves those conflicts by priority.

Part of the JellyDVR set alongside Comskip Commercial Segments, Episode History, JellyDVR Space, JellyDVR Live and
XMLTV Guide Cleaner.

## How it works

- **Priority:** an ordered list of your series recordings (highest first). One-off recordings (a single programme
  chosen by hand) outrank every series by default. JellyDVR Live buffers are left out: they give way on their own.
- **Conflicts:** every half hour (and 30 s after a recording is scheduled or cancelled) it looks at the next 7 days
  of recordings, padding included, for times needing more inputs than there are. Recordings on the same channel at
  the same time share one input. Recordings already running are never touched.
- **Resolving,** lowest priority first: move the recording to another airing of the same episode (same guide
  programme id) that fits, otherwise skip it. Moves create a one-off recording of the other airing; skips cancel
  the episode (series episodes are also disarmed, working around Jellyfin 12 recording cancelled episodes anyway).
- **Preview mode** (default) only shows the conflicts and what would be done.

## Settings (Dashboard → Plugins → JellyDVR Schedule)

- **Mode** — Preview (default) or Active.
- **Tuner inputs that can record at once** — 0 uses Jellyfin's tuner count.
- **Look ahead** (days, default 7) and **Check every** (minutes, default 30).
- **One-off recordings outrank every series** (on).
- **Move to another airing of the same episode when one fits** (on).
- **Series priority** — reorder with ▲ ▼.

## Install

```
dotnet build -c Release
sudo ./install.sh   # restarts Jellyfin
```
