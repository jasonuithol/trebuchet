# valslöngva

A Valheim mod manager, and the first real application written in Trebuchet. It installs
BepInEx, browses Thunderstore, installs a mod and everything it depends on with one click,
and starts the game with or without mods. Windows and Linux; the UI is a browser tab.

The Trebuchet sources are this folder. `apps/valslongva-host/` is the C# that wraps them: a
static `Host` class the externs bind to, and an ASP.NET process that serves the UI and maps
the `Api` service's methods to routes.

## Running it

Everything below is from `compiler/`, with `treb` meaning `dotnet run --project src/treb --`.

```
treb test ../apps/valslongva                       # 15 domain properties and 10 end-to-end scenarios over fakes
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
dotnet publish -c Release -r linux-x64 --self-contained     # or win-x64: one folder, one executable
```

## What it does

- **Finds the game** through every Steam library in `libraryfolders.vdf`, or a folder you set.
- **Installs BepInEx** from Thunderstore's `denikson-BepInExPack_Valheim`, merging the pack's
  folder into the game root.
- **Browses Thunderstore** from the community listing (12,000 packages, 170 MB of JSON),
  cached on disk for six hours; search, sort by downloads, rating, recency, or name.
- **Installs with one click**: the dependency closure is resolved against the listing, newest
  pinned version of each, dependencies before dependents, skipping what is already
  satisfied. Each package unpacks into one folder, `BepInEx/plugins/<Namespace-Name>/`, its
  `config/` files merge into `BepInEx/config/`, and nothing else touches the game root.
- **Updates, disables, enables, and uninstalls.** Disabling moves the package folder to
  `valslongva/disabled/`; uninstalling keeps the config files.
- **Plays modded or vanilla.** On Windows the `doorstop_config.ini` flag decides and Steam
  launches the game; on Linux the loader's `start_game_bepinex.sh` preloads doorstop, so modded
  runs the script and vanilla runs the executable, with Steam already running.

What is installed is an append-only log, `valslongva/library.jsonl` in the game folder, one
JSON event per line, replayed on every request. Settings and the catalogue cache live in the
platform's local data folder (`~/.local/share/valslongva`, `%LOCALAPPDATA%\valslongva`).

## Layout

| Folder | Layer |
|---|---|
| `domain/` | Pure: versions and dependency strings, the Thunderstore listing, the library fold, the install plan, the file placement convention, doorstop and Steam text, launch commands. Every module has properties. |
| `application/` | Ports as shapes (`FileSystem`, `Http`, `Archive`, `Processes`, `Platform`, `Clock`), one error type, and the services: settings, locator, catalogue, search, installer, launcher. |
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
language and compiler.

## Not in this version

Profiles, download progress, a mod's README in the UI, the C++ target (the app is a .NET
program by design), and Steam launch options on Linux (the script is run directly instead).
