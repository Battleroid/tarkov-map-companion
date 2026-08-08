# Tarkov Map Companion

A second-monitor map for Escape from Tarkov that shows where you are, which way you are facing,
and how to get to the exit you picked.

It works by watching your screenshot folder. Tarkov writes your position and camera rotation into
the *filename* of every screenshot you take, so pressing the screenshot key updates the map.

![The app showing Shoreline with an exit selected](docs/screenshot-dark.png)

## This is not a cheat

The app never reads the game's memory, hooks the process, injects anything, modifies a game file,
or talks to the game in any way. Its only input is the name of a file the game itself wrote to
your disk. It cannot show you anything you did not already capture yourself, and it cannot see
other players.

## Install

Download the latest **`TarkovMapCompanion-vX.Y.Z-win-x64.exe`** from
[Releases](https://github.com/Battleroid/tarkov-map-companion/releases) and run it.

There is nothing to install. .NET is bundled, and the maps and exit data are baked into the
executable, so it works with no internet connection.

> Windows SmartScreen will warn about an unsigned executable the first time. Choose **More info**
> then **Run anyway**. Every release is built by GitHub Actions from a tag in this repo, so you can
> check the build log if you would rather see where the binary came from.

## What it does

**Position and heading.** Take a screenshot in raid and your marker moves. The trail behind you
shows where you have been this raid, and the status bar shows your coordinates, facing, the in-raid
clock, and how long the raid has been running.

**Raid-aware trail.** Tarkov's in-raid clock runs at 7x real time, so the app can tell one raid
from the next by whether the two clocks stay in step. Yesterday's route is never stitched onto
today's, and nothing older than the map's raid length is drawn.

**Exits, with their conditions.** Every exit is shown, colored by faction. Conditional exits are
ringed on the map and flagged in the list, and selecting one tells you what it needs:

- Cliff Descent — *Red Rebel ice pick · Paracord · No armor vest equipped*
- Sewer Manhole — *No backpack equipped*
- Dorms V-Ex — *5000 Roubles per player · Maximum of 4 players*
- Smugglers' Boat — *Note with code word Voron*

**Guide line and focus mode.** Pick an exit and a line is drawn to it, labeled with the distance
and how far you have to turn (`348 m, 18° right`). Turn on **Focus exit** and the view frames you
and the exit together, tightening as you close in so the screen only shows what matters. Turning it
off puts your previous view back exactly as it was.

**Exit filter and nearest-first.** "Running as PMC" hides the Scav-only exits you cannot use — on
Customs that is 16 of 31 gone. **Nearest first** reorders the list by distance from your last known
position, and every exit shows its distance either way.

**Spawn heatmap.** Where PMCs, Scavs, AI PMCs and bosses can start. The radius is set in game
meters, so zooming changes how much you see rather than what the data says, and each band is scaled
against its own peak so a sparse group stays visible next to a dense one.

**Other layers.** Loot containers, hazards, locked doors, switches, mounted guns, BTR stops, boss
zones and transits, each independently toggleable.

**Screenshot cleanup.** Optional and **off by default**. When enabled it keeps only the newest N
screenshots, or removes each one after reading it. Deleted files go to the Recycle Bin, never
outside the watched folder, and never anything that does not match Tarkov's own screenshot naming.

**All 13 maps.** Customs, Factory, Ground Zero, Icebreaker, Interchange, The Lab, The Labyrinth,
Lighthouse, Reserve, Shoreline, Streets of Tarkov, Terminal, Woods.

**Floor switching, including the ground floor.** The map artwork stacks floors as opaque geometry,
so an underground level is hidden behind the ground floor. Turning **Ground** off is what reveals
it — Factory's Tunnels being the obvious case.

## First run

The app watches `Documents\Escape from Tarkov\Screenshots` by default. If yours is elsewhere, set
it in **Settings**. Pick your map from the dropdown; if a position lands somewhere the current map
cannot contain, the app offers to switch.

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/Battleroid/tarkov-map-companion
cd tarkov-map-companion
dotnet test              # 211 tests
Run.bat                  # or: dotnet run --project src/TarkovMapCompanion
```

To produce the same single-file executable the releases ship:

```
scripts\publish.ps1
```

### Extra command-line modes

| Command | What it does |
| --- | --- |
| `--render-test <map> [out.png] [w] [h] [floors] [nobase] [bare]` | Renders a map to a PNG with no window. The quickest way to check a coordinate change. `floors` is a comma-separated list, `nobase` hides the ground floor, `bare` drops the markers. |
| `--fetch-data [out]` | Rebuilds the embedded POI snapshot from tarkov.dev. |
| `--fetch-wiki [out]` | Rebuilds the embedded exit conditions from the EFT wiki. |

## Where the data comes from

Map artwork, geometry, exits, spawns and loot positions come from the
[tarkov.dev](https://tarkov.dev) community project — specifically
[`tarkov-dev`](https://github.com/the-hideout/tarkov-dev) for the geometry (MIT) and
`json.tarkov.dev` for the point-of-interest data. Exit *conditions* are not in that data and are
taken from the [Escape from Tarkov Wiki](https://escapefromtarkov.fandom.com) (CC BY-SA).

Map authors are credited individually in the app's About screen: **Shebuka** (10 maps),
**Tarkov.dev** (The Lab, The Labyrinth) and **TarkovBOT.eu** (Icebreaker).

Exit conditions are community-maintained and can lag a patch. You can correct them without
rebuilding by creating `%APPDATA%\TarkovMapCompanion\extract-notes.json`; entries there override
the bundled ones per exit.

## Where it stores things

| Path | Contents |
| --- | --- |
| `%APPDATA%\TarkovMapCompanion\settings.json` | Your preferences. Hand-editable. |
| `%APPDATA%\TarkovMapCompanion\extract-notes.json` | Optional overrides for exit conditions. |
| `%LOCALAPPDATA%\TarkovMapCompanion\cache\` | Downloaded maps and tiles. Safe to delete. |

## License

Code is [MIT](LICENSE). Bundled third-party data keeps its own terms — see
[NOTICE.md](NOTICE.md).

Escape from Tarkov is a trademark of Battlestate Games. This is an unofficial fan-made tool with no
connection to Battlestate Games.
