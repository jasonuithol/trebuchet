# valslöngva

A Valheim mod manager, and the first real application written in Trebuchet. It installs
BepInEx, browses Thunderstore, installs a mod and everything it depends on with one click,
and starts the game with or without mods. Windows and Linux; the UI is a browser tab.

The Trebuchet sources are this folder. `apps/valslongva-host/` is the C# that wraps them: a
static `Host` class the externs bind to, and an ASP.NET process that serves the UI and maps
the `Api` service's methods to routes.

## Getting it

Builds for Linux and Windows are on the releases page:
https://github.com/jasonuithol/trebuchet/releases/latest. Unpack, run, and the
browser opens; Settings has "Install on this computer" for a launcher entry.

## Running it

Everything below is from `compiler/`, with `treb` meaning `dotnet run --project src/treb --`.

```
treb test ../apps/valslongva                       # domain properties and end-to-end scenarios over fakes, 65 in all
treb serve ../apps/valslongva --root test --api api --port 5081     # the UI against an in-memory world
dotnet build ../apps/valslongva-host/Externs
treb serve ../apps/valslongva --root main --api api --port 5082 \
     --with ../apps/valslongva-host/Externs/bin/Debug/net8.0/Valslongva.Externs.dll   # the real thing, interpreted
```

The compiled application:

```
cd ../apps/valslongva-host
treb emit ../valslongva --out Generated --host --reference Externs/Externs.csproj
dotnet run                                          # opens http://localhost:5173/ui/
./publish.sh        # dist/valslongva-linux-x64.tar.gz and dist/valslongva-win-x64.zip
```

The published build is one self-contained executable with the `ui/` folder beside it: unpack,
run, and the browser opens. It is also its own installer: Settings has "Install on this
computer", which copies it to the user's programs folder and adds a Start menu entry on Windows
or an app-launcher entry on Linux, no administrator rights involved; "Remove from this computer"
undoes only that. `valslongva install` and `valslongva remove` do the same from a terminal.

## What it does

- **Finds the game** through every Steam library in `libraryfolders.vdf`, or a folder you set.
- **Installs BepInEx** from Thunderstore's `denikson-BepInExPack_Valheim`, merging the pack's
  folder into the game root. The loader has its own line in the UI with three states: absent,
  detected but not installed by valslöngva (with the dll's file version), or installed at a
  pack version, each with its own action.
- **Browses Thunderstore** from the community listing (12,000 packages, 170 MB of JSON),
  cached on disk for six hours; search, sort by downloads, rating, recency, or name.
- **Installs with one click**: the dependency closure is resolved against the listing, newest
  pinned version of each, dependencies before dependents, skipping what is already
  satisfied. Each package unpacks into one folder, `BepInEx/plugins/<Namespace-Name>/`, its
  `config/` files merge into `BepInEx/config/`, and nothing else touches the game root.
- **Finds what was already there.** Every look at the game folder adopts Thunderstore-packaged
  folders it did not install (folder name and manifest agree), lists loose dlls and other
  folders as unmanaged with what their `BepInPlugin` attribute says, offers "manage as" when a
  catalogue package has exactly that name, and notices managed mods removed by hand. A loose
  dll's companions (`.pdb`, `.dll.mdb`, `.xml` and anything else sharing its stem) count as part
  of it, so managing it takes them along.
- **Updates, disables, enables, and uninstalls.** Disabling moves the package folder to
  `valslongva/disabled/`; uninstalling keeps the config files. Before an update, reinstall, or
  uninstall, the package's recorded files move to a dated folder under `valslongva/backup/`,
  so nothing that was in the game folder is ever destroyed.
- **Checks Steam first.** A process check says whether the client is running and the tail of its
  `logs/connection_log.txt` says whether it is signed in; the Play buttons wait for both.
- **Keeps itself current.** A published build asks where GitHub's latest-release address redirects when the page opens,
  and when a newer one exists it downloads it, swaps itself, and restarts on the same port.
  `VALSLONGVA_NO_UPDATE=1` turns that off; a developer's run never updates itself.
- **Does not make you wait.** The page shows what it showed last time at once, a catalogue older
  than six hours is served as it is and refreshed behind the page, and Steam's state is polled
  every three seconds.
- **Plays modded or vanilla.** On Windows the `doorstop_config.ini` flag decides and Steam
  launches the game; on Linux the loader's `start_game_bepinex.sh` preloads doorstop, so modded
  runs the script and vanilla runs the executable, with Steam already running.
- **Keeps profiles.** A profile is a set of enabled mods and launch arguments. Every game
  folder starts with one, Default, which stays out of the way; Settings → Profiles lists them,
  adds one as a copy of the current profile, switches (which moves mod folders to match), and
  removes. What you install, enable, or disable changes the active profile.
- **Answers Thunderstore's "Install with Mod Manager" button.** Installing the program
  registers it for `ror2mm://` links on both platforms; the button opens valslöngva's page,
  which installs the package and its dependencies.
- **Has an API for scripts and agents.** Every page action is a plain HTTP route on localhost;
  `API.md` (also served at `/api.md`) describes them for a person or an AI coding agent.
- **Passes extra launch arguments.** Settings has a "Launch arguments" line (`-console`,
  `-window-mode exclusive`, and so on; double quotes group a value with spaces). It goes on the
  command line on Linux and inside Steam's `steam://run/892970//…/` URL on Windows, where the
  client appends it to the game's own launch options.

What is installed is an append-only log, `valslongva/library.jsonl` in the game folder, one
JSON event per line, replayed on every request. Settings and the catalogue cache live in the
platform's local data folder (`~/.local/share/valslongva`, `%LOCALAPPDATA%\valslongva`).

## Layout

| Folder | Layer |
|---|---|
| `domain/` | Pure: versions and dependency strings, the Thunderstore listing, the library fold, the install plan, the file placement convention, doorstop and Steam text, launch commands. Every module has properties. |
| `application/` | Ports as shapes (`FileSystem`, `Http`, `Archive`, `Processes`, `SteamClient`, `Platform`, `Clock`), one error type, and the services: settings, locator, catalogue, search, installer, Steam status, launcher, profiles. |
| `infrastructure/` | Externs to `Valslongva.Host`, the adapters over them, and in-memory fakes. |
| `api/` | The `Api` service: routes by method name. |
| `host/` | Roots `main` and `test`, the fixture world, and scenario handlers. |
| `ui/` | One HTML file with React from a CDN. |

Trebuchet features in use, for the reader who came for those: shapes as ports satisfied
structurally by both adapters and fakes; `Result` with `?` end to end and `supervise` at the
API edge; an event-sourced library with `json.encode` and `json.decode`; lenient JSON binding
of Thunderstore's snake_case; an `Ord` instance on `Version` used with `<` and `>=`; list and
tuple patterns; local functions; property tests on every domain module and scenario handlers
that compose the whole application from fakes; composition roots for the real and the test
world; externs with exception mapping. Strategy §7.28 lists what building it changed in the
language and compiler; `DESIGN.md` beside this file records the application's own design decisions.

## Not in this version

Download progress, a mod's README in the UI, installing a version other than the newest, and the
C++ target (the app is a .NET program by design). Steam's own launch options are not read on Linux, where the script is run
directly; the Launch arguments setting is the equivalent and works on both platforms.
