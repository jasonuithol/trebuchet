# valslöngva HTTP API

Instructions for a person or an AI coding agent driving valslöngva from outside its page.
The running program serves this file at `/api.md`.

## Finding it

valslöngva listens on `http://localhost:5173` unless it was started with another port as its
first argument. Probe with `GET /pulse`; a reply like `{"steam":"Connected","version":"0.2.8"}`
means it is there. If nothing answers, start the installed program (`~/.local/share/valslongva/app/valslongva`
on Linux, `%LOCALAPPDATA%\Programs\valslongva\valslongva.exe` on Windows) with `--no-browser`
to keep it from opening a tab. A second start on a busy port opens the existing page and exits.

Everything is JSON over plain HTTP on localhost; there is no authentication. Requests with a
body send `content-type: application/json`. Success is 200 with a JSON body, or 204 with none.
Failure is a JSON object `{"error": "<kind>", "message": "<text>"}` with status 400
(`BadRequest`), 404 (`NotFound`), 409 (`Conflict`), or 500 (`Io`, `Network`, `Internal`; an
`Internal` error is a caught panic, and the program is still fine).

## Rules worth knowing before you call anything

- `POST /launch` starts Valheim on the user's machine. Do not call it unless the user asked you to play.
- `POST /selfupdate` may replace the program and restart it; the port stays the same, in-flight requests are lost.
- `POST /setup` installs or removes the program itself, not mods.
- `POST /refresh` downloads Thunderstore's whole listing (about 170 MB) and takes a while. The
  catalogue refreshes itself when older than six hours; you rarely need this.
- Installing, enabling, disabling and switching profiles move files in the game folder. Files are
  backed up under `<game>/valslongva/backup/` before an update, reinstall or uninstall; nothing
  the user had is deleted.
- Package names are Thunderstore full names, `Namespace-Name`, for example `ValheimModding-Jotunn`.
  Results that list what was installed use `Namespace-Name-Version`.

## Reading state

| Route | Returns |
|---|---|
| `GET /pulse` | `{steam, version}`: `steam` is `NotRunning`, `NotConnected` or `Connected`. Cheap; poll this. |
| `GET /status` | The whole picture (below). |
| `GET /installed` | Array of installed mods: `{pkg, version, enabled, latestVersion, installedAt, adopted, summary, onIn}`; `summary` is the catalogue entry (as in search) or null before the catalogue is loaded; `onIn` names the profiles in which the mod is on. |
| `GET /search?query=&sort=&deprecated=&offset=&limit=` | `{items, total, offset, limit}` over the catalogue. `sort` is `downloads` (default), `rating`, `updated` or `name`; `deprecated=true` includes deprecated packages; `limit` is at most 200. Each item: `{fullName, name, owner, description, latestVersion, downloads, ratingScore, categories, icon, isDeprecated, dateUpdated, dependencies, installedVersion, enabled}`. |
| `GET /package?name=Namespace-Name` | `{summary, versions, packageUrl}`: the search item, every version number, and the Thunderstore page. |
| `GET /profiles` | `{active, profiles: [{name, mods, launchArgs}]}`. `mods` are package names enabled in that profile. |
| `GET /setup` | Where the program lives: `{installed, path, launcher, runningInstalled, portable}`. |
| `GET /memory` | `{managedMb, workingSetMb, serverGc}`. |
| `GET /api.md` | This document. |

`GET /status` fields:

| Field | Meaning |
|---|---|
| `os` | `linux` or `windows`. |
| `version` | This program's version. |
| `gameDir`, `gameProblem` | The Valheim folder, or why none was found. `candidates` lists where it looked. |
| `loader`, `loaderPresent`, `loaderDetected`, `loaderLatest`, `loaderIcon` | BepInEx: the pack version valslöngva installed (null if none), whether its core dll exists at all, that dll's own file version, the newest pack on Thunderstore, and the pack's icon URL. |
| `steam` | As in `/pulse`. |
| `installed` | How many managed mods there are. |
| `adopted` | Packaged folders found in `BepInEx/plugins` on this request and taken under management. |
| `unmanaged` | Entries in `BepInEx/plugins` that are not managed: `{entry, isFolder, pluginName, pluginVersion, guid, suggestion, onThunderstore, suggestedVersion}`. `suggestion` is a package the entry could be managed as; `onThunderstore` says whether its version is one Thunderstore has (false means a local build). |
| `catalogueFetchedAt`, `cataloguePackages`, `catalogueStale` | The Thunderstore catalogue on disk and whether it is older than six hours. |
| `launchArgs`, `profile`, `profileCount` | The active profile's launch arguments, its name, and how many profiles there are. `Default` is the profile every game folder starts with. |

## Changing things

| Route | Body | Returns | Does |
|---|---|---|---|
| `POST /install` | `{"name": "Namespace-Name"}` | `["Namespace-Name-Version", ...]` | Installs the newest version and its dependency closure, BepInEx included. Already-current packages are skipped; an empty array means nothing was needed. |
| `POST /link` | `{"url": "ror2mm://v1/install/thunderstore.io/Namespace/Name/1.2.3/"}` | `{package, version, installed}` | The same install, from a Thunderstore "Install with Mod Manager" link. The newest version is installed whatever the link names. |
| `POST /loader` | none | as `/install` | Installs or updates BepInEx alone. |
| `POST /reinstall` | `{"name"}` | as `/install` | Replaces a package's files with the newest Thunderstore version; the old files go to a backup. |
| `POST /uninstall` | `{"name"}` | 204 | Removes the package's files (backed up); config files stay. Drops it from every profile. |
| `POST /enable`, `POST /disable` | `{"name"}` | 204 | Moves the package between `BepInEx/plugins` and `valslongva/disabled`. Changes the active profile. |
| `POST /adopt` | `{"entry", "name"}` | 204 | Manages an unmanaged entry as the named package, without touching its files. |
| `POST /launch` | `{"mode": "modded"}` or `"vanilla"` | `"modded"` / `"vanilla"` | Starts the game. Refuses (409) unless Steam is connected and, for modded, BepInEx is present. |
| `POST /steam` | none | the Steam state | Starts the Steam client if it is not running. Signing in is the user's. |
| `POST /gamepath` | `{"path": "/where/Valheim"}` | `/status` | Sets the game folder; an empty path goes back to searching the Steam libraries. |
| `POST /launchargs` | `{"args": "-console"}` | `/status` | Sets the active profile's launch arguments. One line; double quotes group a value with spaces. |
| `POST /profilecreate` | `{"name"}` | `/profiles` | Adds a profile as a copy of the active one. Does not switch to it. |
| `POST /profileswitch` | `{"name"}` | `/profiles` | Makes a profile active, enabling and disabling mods to match it. |
| `POST /profileremove` | `{"name"}` | `/profiles` | Removes a profile; `Default` cannot be removed; removing the active one activates `Default`. |
| `POST /refresh` | none | `/status` | Downloads the Thunderstore listing again. |
| `POST /selfupdate` | none | `"UpToDate"`, `"NotUpdatable"`, or `{"tag": "Updated", "version"}` | Updates the program from GitHub releases, then restarts. |
| `POST /setup` | `{"action": "install"}` or `"remove"` | `{path, isInstalled}` | Copies the program to the user's programs folder with a launcher entry, or removes that. |

## A typical session

```sh
curl -s localhost:5173/pulse
curl -s 'localhost:5173/search?query=jotunn&limit=3' | jq '.items[] | {fullName, latestVersion, installedVersion}'
curl -s -X POST localhost:5173/install -H 'content-type: application/json' -d '{"name":"ValheimModding-Jotunn"}'
curl -s localhost:5173/installed | jq '.[] | {pkg, version, enabled}'
curl -s -X POST localhost:5173/profilecreate -H 'content-type: application/json' -d '{"name":"Building"}'
curl -s -X POST localhost:5173/profileswitch -H 'content-type: application/json' -d '{"name":"Building"}'
curl -s -X POST localhost:5173/disable -H 'content-type: application/json' -d '{"name":"ValheimModding-Jotunn"}'
curl -s localhost:5173/profiles
```

## Where things are on disk

- Game folder: `<game>/BepInEx/plugins/<Namespace-Name>/` per package; `<game>/valslongva/library.jsonl`
  (the append-only log of what is installed), `profiles.json`, `disabled/`, `backup/`.
- Program data: `~/.local/share/valslongva` or `%LOCALAPPDATA%\valslongva`: `settings.json`
  (game folder choice), `catalogue.json` and `catalogue.fetched`.

The same API runs under the Trebuchet dev server (`treb serve ../apps/valslongva --root test --api api --port 5081`)
against an in-memory game folder, which is the safe place to try destructive calls.
