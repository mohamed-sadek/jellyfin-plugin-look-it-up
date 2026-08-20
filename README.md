# Look it up

A Jellyfin plugin that scans subtitles for names (places, people, organizations), looks them up on Wikipedia, and shows a short popup at the moment they are mentioned during playback.

Example: a character says *"This is from France"* → popup: **"France: a country in Europe..."**

## Install from repository

In Jellyfin: **Dashboard → Plugins → Repositories → +**

| Field | Value |
|-------|-------|
| Repository name | Look it up |
| Repository URL | `https://raw.githubusercontent.com/mohamed-sadek/jellyfin-plugin-look-it-up/main/manifest.json` |

Then open **Catalog**, find **Look it up**, install, and restart Jellyfin.

### Web overlay

Server plugins cannot inject the player UI. Add this to your Jellyfin Web client (Custom JavaScript / script injector):

```html
<script src="/LookItUp/script.js"></script>
```

Do **not** pin an old `?v=1.2.xx` query string — that freezes the browser on an outdated overlay. The plugin already sends `Cache-Control: no-cache` on `script.js`.
## How it works

1. **Server plugin** finds an external `.srt` / `.vtt` subtitle for the playing item
2. Extracts capitalized proper nouns from each cue
3. Looks each unique name up on the Wikipedia summary API
4. Caches timed annotations
5. **Web overlay** (`/LookItUp/script.js`) watches playback time and shows popups

## Requirements

- Jellyfin **10.9+** (targets ABI `10.9.0.0`, `net8.0`)
- External subtitle files (`.srt` or `.vtt`) next to your media
- Jellyfin Web (for the popup overlay)

## Build

```powershell
dotnet build Jellyfin.Plugin.LookItUp/Jellyfin.Plugin.LookItUp.csproj -c Release
```

Copy the built DLL into your Jellyfin plugins folder, e.g.:

```text
%LOCALAPPDATA%\jellyfin\plugins\LookItUp_1.0.0.0\Jellyfin.Plugin.LookItUp.dll
```

Also add a `meta.json` in that folder (see `meta.json` in this repo). Restart Jellyfin after installing.

## API

| Method | Path | Description |
|--------|------|-------------|
| `GET` | `/LookItUp/{itemId}` | Get (or scan) annotations for an item |
| `POST` | `/LookItUp/{itemId}/scan` | Force rescan |
| `GET` | `/LookItUp/script.js` | Playback overlay script |

## Configuration

Dashboard → Plugins → Look it up:

- Enable / disable
- Wikipedia language (`en`, `fr`, …)
- Preferred subtitle languages
- Max annotations per item
- Minimum entity length
- Popup duration

## Limits (v1)

- External SRT/VTT only (embedded subtitle tracks are not extracted yet)
- Heuristic named-entity detection (capitalized phrases), not full NLP
- Wikipedia only (no AI explanations yet)
- Popups are Jellyfin Web–oriented
