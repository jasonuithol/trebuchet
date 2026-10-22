# valslöngva design

valslöngva is a Valheim mod manager and the first application written in Trebuchet. It is a testbed
for the language, but it is also a product with its own users, and the two are documented apart.
This file records the product decisions: what the manager does, what it refuses to do, and why.
What building it taught the language and the compiler is in the Trebuchet strategy document,
section 7.28; the README has the feature list, the module layout, and how to run it.

## Shape

The onion layout: a pure `domain/` with a property per decision, `application/` services over port
shapes (`FileSystem`, `Http`, `Archive`, `Processes`, `SteamClient`, `SelfInstall`, `Platform`,
`Clock`), `infrastructure/` with one host implementation and one fake of each port, an `api/`
service whose methods are the HTTP routes, and two roots: `main` over the host, `test` over the
fakes. Every scenario composes the whole application from the fakes and drives it through the API.
What is installed is an append-only log, `valslongva/library.jsonl` in the game folder, replayed on
every request; nothing else is state. The install convention is one folder per package under
`BepInEx/plugins/<Namespace-Name>/`, `config/` merged, the loader pack merged into the game root.

## The loader is not a mod

First use showed that BepInEx wants its own line, not a row in the catalogue: the status reports three states, absent, detected in the game folder but not installed by valslöngva (with the dll's file version, read by a host extern), or installed by it at a pack version, and each state has its own verb: install, install over it, update. Installing a mod still brings the loader as a dependency, and an unmanaged loader is refreshed by that, which the line says.

## Steam must be up and signed in

The game will not start without the client, so the status carries a `SteamState` (not running, running but not connected, connected), the launcher refuses the other two, and the UI shows the Steam mark next to the Play buttons in grey, amber, or green. Running is a process check behind a host extern; connected is read from the tail of the client's own `logs/connection_log.txt`, whose last bracketed state is the truth, parsed by a pure function with properties. When Steam is not running, Play starts it (`steam://open/main`), waits for the pulse to report a sign-in, and then launches; when it is running but not signed in, Play buzzes and shakes the Steam mark instead of sitting disabled. There is no separate Start Steam button because Play is the only reason to want one.

## What was already there

A game folder has a history before the manager arrives, so every status request scans `BepInEx/plugins` and sorts each top-level entry three ways, by a pure function with properties. A folder named `Namespace-Name` whose Thunderstore `manifest.json` names the same package is that package beyond doubt, and is adopted silently: a `ModAdopted` event, the folder's files recorded, managed from then on. Anything else, a loose dll or a folder without a manifest, is reported as unmanaged and left alone; its `BepInPlugin` attribute (guid, name, version, read from the assembly metadata by a host extern without loading it) is shown, and when a catalogue package has exactly that name the UI offers "manage as", which the user confirms. Guessing further was rejected: the listing carries no guids or file names, so a name heuristic would be wrong often enough to cost trust. The scan also notices a managed mod whose recorded files are all gone and records its removal. The log stays the single source of truth; the scan only appends to it, and appends nothing on the second look.

## Nothing the user had is destroyed

The first real use found the gap: a plugin named DayNight4010 at 2.0.3 was matched by name to the package Ezvay-DayNight4010, whose newest is 2.0.1, when it had come from Ezvay-DayNight, whose newest is 2.0.3; the manager offered "replace", replaced, and deleted the only copy. The name heuristic did exactly what the previous section said it would. Two changes follow. A suggestion now prefers, among packages whose name is the plugin's or a prefix of it, one that has the plugin's exact version, and only then an exact name. And every file the log recorded for a package moves to a dated folder under `valslongva/backup` before an update, reinstall, or uninstall touches it; configs never move; the UI names a local build as such when its version is not one Thunderstore has, and replacing an adopted mod takes a second click that says where the old files go.

## What happens without being asked

Three things change behind the page. The browser keeps the last list, status, and installed set, so a visit paints at once; the catalogue service answers with whatever it has, memory or the disk copy however old, reports `catalogueStale`, and the page asks for the refresh as a second, non-blocking request. Steam is the user's to start and stop, so a `pulse` of Steam's state and the program's version is polled every three seconds rather than read once. And the program keeps itself current: `Updater` asks where GitHub's `releases/latest` address redirects (0.2.0 read the release list from the API, which allows sixty anonymous requests an hour per address and was exhausted on the day it shipped; a redirect has no such limit), a pure `updateFromLatest(current, os, location)` with properties turns the tag in that address into a version and this platform's download address or into nothing, and the unpacked folder goes to a `SelfInstall` port whose host implementation swaps the executable (rename-over on Linux, rename-aside on Windows) and restarts on the same port; the pulse sees the version change and reloads the page. Settings also has "Check for updates", which says what it found. A developer's run reports itself not updatable and never touches itself.

## Launch arguments

A one-line setting, "Launch arguments", goes to the game on every Play. It is stored as the user typed it and split only at launch by a pure `splitArgs` (whitespace separates, double quotes group), so the settings file stays readable and the splitting has properties. On Linux the words are appended to the script or the executable; on Windows Steam does the launching, and its `steam://run/<id>//<args>/` form carries them percent-encoded, the client adding them to the game's own launch options. The `Processes.run` port takes an arguments vector rather than a joined string, so the host never re-parses a command line.

## Profiles

A profile is a named set of enabled mods and the game's launch arguments. The library log stays
the truth of what is on disk; a profile says what the disk should look like while it is active,
so switching is the difference between two sets (`transition`, with a property that applying it
reaches the wanted set and touches nothing both sets share) carried out as ordinary enables and
disables, which append to the log as always. Profiles live in `<game>/valslongva/profiles.json`,
a small document rewritten whole rather than a second log, and every read reconciles it with the
library: the active profile takes exactly what is enabled, and every profile forgets a mod that
is no longer installed. That one rule means install, enable, disable, adopt and uninstall need
no profile code at all. The first read makes the Default profile from whatever is enabled and the
launch arguments saved before profiles existed, so an existing game folder gains profiles without
noticing. A new profile is a copy of the active one ("add" means "vary from here") and does not
become active by itself; Default cannot be removed; removing the active profile activates
Default. Default is deliberately invisible on the main screen so a novice never meets the idea;
only a non-Default active profile shows a chip beside the Play buttons, and the Profiles panel is
behind a button in Settings. Mods are not pinned per profile: one installed version of a package
serves every profile, and a profile only chooses whether it is on.

## Thunderstore's install button

Thunderstore's "Install with Mod Manager" button opens `ror2mm://v1/install/thunderstore.io/Owner/Name/Version/`
and leaves the rest to whichever program registered the scheme. Installing the program registers
it: the `.desktop` entry takes `%u` and `MimeType=x-scheme-handler/ror2mm` with `xdg-mime` making
it the default on Linux, and a per-user `HKCU\Software\Classes\ror2mm` key on Windows; removing the
program undoes both. The link is parsed by a pure function with properties and installed through
the same path as a click on the Browse tab. The host does nothing with the link but open the page
with it as a query parameter: if the program is already running, the second process opens the
existing page that way and exits; if not, it starts and opens the page the same way. The page
posts the link to `/link` and shows the result like any other install. The newest version is
installed whatever the link names, because the installer has one notion of "install", and a
link usually names the newest anyway; installing an older named version is listed as not done.

## An API for agents

Every page action is a plain HTTP route on localhost, which was true from the first day because the
API service's method names are the routes. `API.md` describes them for a person or an AI coding
agent: how to find the running program, the rules (launch starts the game, selfupdate restarts the
process, what moves files), every route's body and reply, and a typical session in `curl`. The
program serves the same file at `/api.md`, so an agent on the user's machine can read it from the
running instance rather than from a checkout.

## Shipping it

`dotnet publish` makes one self-contained executable per platform (about 40 MB compressed, the UI folder beside it), and that file is the installer: it runs from wherever it was downloaded, and "Install on this computer" in its settings copies it to the user's programs folder and adds a Start menu entry on Windows or a `.desktop` launcher on Linux, with no administrator rights and a "Remove" that undoes only that. Installing the program is host business, so it is C# in the host project, not Trebuchet; the mod manager never knows where it lives. `publish.sh` builds both platforms from Linux; a release is a GitHub release tagged `valslongva-vX.Y.Z` with the two archives attached, and `appVersion()` in the domain and `<Version>` in the host project move together. A second launch finds the running instance on its port and opens that instead of failing.

## Four gigabytes

The system monitor showed the running program at 4 GB, and three causes stacked. The ASP.NET default is the server garbage collector, one heap per core, which on sixteen cores kept 3 to 4 GB of dead decode garbage; a desktop program wants the workstation collector, and that alone brought it to 1.1 GB. The catalogue kept every field of every one of 98,000 versions, 417 MB live, when all the program ever asks is the newest version in full, the total downloads, and which version numbers exist; a `Package` condensed from each `Listing` at index time, with a property that condensing keeps exactly those answers, brought the live set to 58 MB and a warm search from 230 ms to under 10. And a refresh fetched the 170 MB listing as one response string, whose pooled buffers stayed rented for minutes; it is now streamed to a file beside the old copy, read from there through the `readChunks` port, and swapped in only once it has decoded. The host compacts the heap after a load or a refresh. The remaining peak, the decoder's, was the language runtime's problem and is recorded in the strategy document. Resident memory is about 200 MB and the peak while loading 170 MB of JSON is 233 MB.
