# Tarkov Map Companion

A second-monitor map for Escape from Tarkov that shows where you are, which way you are facing,
and how to get to the exit you picked.

It works by watching your screenshot folder. Tarkov writes your position and camera rotation into
the *filename* of every screenshot you take, so pressing the screenshot key updates the map.

![The app showing Shoreline with an exit selected](docs/screenshot-dark.png)

## This is not a cheat

The app never reads the game's memory, hooks the process, injects anything, modifies a game file,
or talks to the game in any way. Everything it knows comes from files the game itself wrote to your
disk, and it only ever reads them:

- the **name** of a screenshot, for your position and facing;
- optionally the **picture**, for the exit list you already had on screen;
- optionally the **log** Tarkov writes as it runs, for which map is loading.

It cannot show you anything you did not already capture yourself, and it cannot see other players.

The log is the newest of the three and the only one that is not something you pressed a key to
create, so it is off by default and has its own switch in Settings. What it is used for is narrow:
the line where the game names the map it is about to load, and the line where you gain control. It
is a plain text file in your Tarkov folder that you can open in Notepad, it contains nothing about
anybody else's position, and nothing is written back to it.

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

**Only the exits you actually have.** Tarkov offers a different subset of a map's exits every raid,
depending on where you spawned — Customs has 27, and a given raid might give you four. Tick
**Read exits from screenshot** in the exits panel, bring the extraction list up in game (double-tap
<kbd>O</kbd> by default) and take a screenshot: the app reads the list off the picture and fades
every exit the game did not offer you.

Exits are faded, never hidden, and stay clickable — the list comes from character recognition, and
a misread should cost you a dim marker, not a missing one. Anything it reads but cannot place is
named in the panel rather than quietly dropped. Readings accumulate through a raid, so a screenshot
that catches the panel part-way through opening can only add to what is already known, and the whole
lot is thrown away when the raid ends or you change map. It uses the OCR engine built into Windows,
so it adds nothing to the download, and it costs about 25 ms per screenshot.

Verified from 1024x768 to 5120x1440, across 4:3, 5:4, 16:9, 21:9 and 32:9, for both PMC and Scav
lists. Small frames are upscaled before reading, because below about 900 lines the panel's text gets
small enough that whole rows go unseen rather than misread. If a read ever goes wrong,
`--read-exits` prints every stage of it.

**Guide line and focus mode.** Pick an exit and a solid line is drawn to it, labeled with the
distance and how far you have to turn (`348 m, 18° right`). Its color is yours to choose. Clicking
the same exit again lets it go. Turn on **Focus Exit** and the view frames you and the exit
together, tightening as you close in so the screen only shows what matters. Turning it off puts your
previous view back exactly as it was.

Selecting an exit lists what it needs, and **Hide conditions** folds that away when four requirement
lines under a co-op extract are pushing everything else off screen. The warning marker in the list
and the ring on the map stay either way — hiding the clutter should not hide the fact that there is
a condition.

**Route markers.** Press **Mark Route**, then click the map in the order you mean to visit — quest
objectives, a stash worth the detour, wherever you dropped something. Each click leaves a numbered
pin; press **Mark Route** again, or <kbd>Esc</kbd>, to finish. Arrowheads march along the route
toward the next pin, so which way around it goes is legible without reading the numbers. The guide
line walks the route in order and only goes back to pointing at your exit once the route is done, so
the exit can stay selected the whole time without getting in the way. Focus mode frames the next
marker rather than the exit, for the same reason.

A pin retires once you come within 50 m of it, adjustable in **Settings**. By default it shows as
reached for one screenshot and is gone on the next: the confirmation is the point, since a pin that
simply vanishes leaves you unsure whether you got close enough or had misplaced it. The other
option removes it the moment you are inside the radius. **Clear marks** drops the whole route and
hands the line back to the exit.

**Smooth camera.** With **Follow Player** on, the view glides to your new position instead of
jumping. A jump costs you your bearings — you have to find yourself on the map again every
screenshot — where a move you can follow does not. It applies to focus mode's reframing too, and
never to your own panning and zooming, which stay locked to the pointer. Toggle it in **Settings**.

Panning and zooming do **not** switch following off. Looking at a corner of the map between
screenshots is exactly when you expect to be put back, and the app used to quietly disarm itself
instead. Only the button turns it off, and it lights up while it is on so being recentered is never
a surprise.

**Nearest first.** Reorders the exit list by distance from your last known position; every exit
shows its distance either way. The Exits tab shows all of them, because which faction can use an
exit is written on its own row and a list that disagreed with the map was worse than a longer list.
Settings still has the faction filter for anyone who only ever runs one.

**Names that get out of each other's way.** Exit, quest, note and teammate labels are placed by
one pass that knows about all of them, so a name lands where no other name already is. Whichever
overlay asks first keeps the spot it wanted; the rest are nudged up, down or to the other side of
their marker, and anything moved far enough to be ambiguous gets a hairline back to what it names.
Labels baked into the map artwork are not something the app can move, so those can still be sat on.

**Spawn heatmap.** Where PMCs, Scavs, AI PMCs and bosses can start. The radius is set in game
meters, so zooming changes how much you see rather than what the data says, and each band is scaled
against its own peak so a sparse group stays visible next to a dense one.

**Other layers.** Loot containers, hazards, locked doors, switches, mounted guns, BTR stops, boss
zones and transits, each independently toggleable. Layers and the heatmap live in a **Layers** panel
floating over the bottom-left of the map, alongside **Levels** — what is drawn on the map is a thing
about the map, so that is where the switches are.

**Screenshot cleanup.** Optional and **off by default**. When enabled it keeps only the newest N
screenshots, or removes each one after reading it. Deleted files go to the Recycle Bin, never
outside the watched folder, and never anything that does not match Tarkov's own screenshot naming.

**All 13 maps.** Customs, Factory, Ground Zero, Icebreaker, Interchange, The Lab, The Labyrinth,
Lighthouse, Reserve, Shoreline, Streets of Tarkov, Terminal, Woods.

**Floor switching, including the ground floor.** The map artwork stacks floors as opaque geometry,
so an underground level is hidden behind the ground floor. Turning **Ground** off is what reveals
it — Factory's Tunnels being the obvious case. The **Levels** control floats over the top-left of
the map and stays collapsed until you need it, since most maps have nothing to switch and a whole
side pane was a lot of window for two checkboxes.

**Map detection.** Pick your map from the dropdown, or let a position that cannot be on the current
map offer you the right one. It proposes rather than switches, because several maps overlap in world
coordinates and a wrong auto-switch mid-raid is worse than no suggestion. Switching automatically is
available in **Settings** if you would rather.

**Quests it already knows you are on.** With the game's log switched on, the app reads the trader
messages Tarkov writes and follows along: accepting a quest ticks it, handing it in unticks it. The
task id rides along in the notification, so this is exact rather than guessed. On the development
machine it reconstructed 73 active quests out of a week of logs on first run.

It only knows what the logs kept, so a quest accepted before the oldest surviving log looks
untouched — ticking by hand still works and is never undone by the log saying nothing. What it works
out is cached, so a cleaned log folder costs the history rather than everything.

**Sync from game** throws the hand-picked list away and tracks exactly what the log says is open.
Following along as events arrive cannot fix a list that has already drifted — a quest handed in
while the app was closed never produces an event to untick it — so this is the "start again from
what the game knows" button. **Active here** filters the list to the quests the game has open that
have an objective on the map you are looking at, which is the question you actually ask in a raid.

**Read a quest properly.** Click a task's name and it opens in a pane on the left: objectives at a
size you can actually read, one per block, each saying whether it is on the map in front of you and
carrying its own **Route** button. Prerequisites are listed with a tick against the ones already
done, keys are grouped by the map they open something on with the one for *this* map first, and
each objective names the items it wants — with their pictures, and including the alternatives its
own wording glosses over, since "Obtain the item: Rye croutons" also accepts Emelya rye croutons.
The pane resizes and closes.

**Tick objectives off as you do them.** Each objective in the pane has its own box; a ticked one is
struck through there and its markers come off the map entirely, so what is drawn is what is left to
do. That is the one place this app hides rather than dims — everywhere else the hedging exists
because the app is guessing, and a tick is you stating your own progress. Nothing in any log Tarkov writes says how far into a quest you are — the
notification log carries a quest changing state but never a condition inside one — so this is a
note to yourself, and it persists as one. **Clear the ticks** resets a task for a new wipe.

**What to take with you.** Above the task list, the Quests tab totals up what everything you have
ticked needs on the map you are looking at: the keys, and the items you have to be carrying rather
than the ones you hand over at a counter, each with how many. Items add up across tasks, because
planting one spends it; keys never do, because using one does not. Planting an MS2000 needs the marker in your rig; handing
in five MP-133s does not mean taking five shotguns into Customs, and the list knows the difference.

**Your level, without typing it in.** Tarkov writes your level to no log — checked across nineteen
log folders: no experience, no level-up line, and while the group notifications do carry a level it
is every member's except your own. What is knowable is a floor: a quest requiring level 42 cannot
have been accepted at 41, so the highest requirement among the quests the log has seen you take is
a level you are at least at. **From log** applies it, and only ever upward — the estimate lags the
truth, and pulling down a number you typed in would hide tasks you can actually take. On the
development machine it reads 52 from a week of logs.

**Quest tracking.** The **Quests** tab lists all 510 tasks, grouped by trader, with a search box and
filters for *on this map*, *at or below my level*, and *Kappa*. Tick one and its objectives are drawn
on the map: zones as their actual footprint rather than a dot, so a "visit" objective reads as *stand
in this room* instead of *somewhere near here*. Where a quest item can spawn in several places, each
is a hollow ring rather than a confident marker — you should not walk past four of them.

Only ticked tasks draw, and that is the design rather than a shortcut: Lighthouse alone has 169
positioned objectives. Ticking is by hand because which quests you have accepted lives in your
profile on BSG's servers, and this app reads files rather than accounts.

Press **Route** on a task, or click one of its markers on the map, and its objectives become numbered
route markers — which means they inherit arrival detection, the guide line, and sharing to your
squad in your color. Objectives with nowhere to be are still listed in the reading pane, because
"hand over 5 MP-133" is worth reading next to the map even though it is not on it.

**Write on the map.** Press **Add Note**, click a spot, type. Labels are saved to disk and stay on
the map you wrote them on, so the names your squad actually uses — *Big Red*, *the sniper shack*,
*where the marked key spawns* — end up on the map instead of in everyone's head. Click one of your
own to rename it, or delete it from the **Notes** tab.

**Import** merges a file rather than replacing yours, so somebody who has labeled every building on
Streets can hand theirs over without costing you your own. It reads the app's own export format, and
also a plain `map,x,z,text` list, because the realistic source of a few hundred building names is a
spreadsheet rather than hand-written JSON.

**Share my notes with the party** is off by default and sends only your own. A teammate's notes
appear in their color, are never written to your file, and go when the session does — a set of
labels is something built up over weeks, not a plan for the next ten minutes, and pushing it onto
three other people's maps the moment they join is not a thing to do without being asked.

**The map, before you have taken a single screenshot.** Tick **Read the game's log** in Settings
and the app follows the log Tarkov writes as it runs. The game names the map it is loading between
twenty seconds and two minutes before you have control, so the second monitor is already on the
right map when you spawn, and the map switch no longer waits for you to take a screenshot and be
somewhere only one map could contain.

It also knows when a raid starts, which is a cleaner signal than the clock heuristic: the previous
raid's trail goes the moment you gain control rather than a screenshot or two later. Nothing is
cleared when you get back to the menu, because looking over where the fight went is the one thing
people do with the map afterward.

Off by default, and it stops reading the file the moment you untick it. The install is found from
the path the game records in its own Unity log, which works even on a second drive where the
registry has nothing; if that fails, set the folder yourself. `--find-logs` prints every place that
was tried, what the parser made of the newest log, and any location name this build does not
recognize — which is the thing to paste into an issue if the map stops switching after a patch.

**Squad positions.** The **Party** panel floats over the top-right of the map, collapsed to a pill
until you open it. Press **Host session** and the app hands you a code like `K7QM4-3FHB9-8TZR9-M4X7Q`.
Anyone who pastes it into **Join** appears on your map, and you on theirs. People can join part-way
through a raid and get everybody's current positions straight away, and you can stop and restart a
session at any point if something goes wrong. Collapsed, the pill still shows how many of you there
are, and it opens itself when there is a code to copy or something has gone wrong.

**Connections that admit when they are dead.** Both ends exchange a heartbeat every five seconds and
give up on a link that has gone silent for twenty-one. TCP will otherwise hold a connection open long
after it has stopped carrying anything — a router drops the mapping, a laptop sleeps, a link
flaps — and both ends sit in a blocking read believing the squad is fine. Each roster row shows the
round trip from that heartbeat, so a teammate whose link is struggling is visible before they vanish.

**Shift-click to ping.** Marks a spot for the whole squad, with a pulse, your name, and a countdown.
It lasts 30 seconds and clears itself, so nothing has to be tidied up mid-raid, and it makes a noise
so a teammate notices while they are looking at the game rather than the map. The rings keep
radiating for the whole 30 seconds, calming as it ages rather than stopping — a ping matters most to
someone who looks at the map fifteen seconds later and has to find it among three others. Turn the
sound off in **Settings** if you would rather. Pings work solo too, as a scratch mark that expires
on its own.

Teammates are only drawn when they are on the same map as you — a position from another raid means
nothing in this one's coordinates. They stay in the roster, labeled with where they actually are.
Every marker also carries its age and fades as it gets older, because peers only report when they
take a screenshot: a minute-old dot drawn at full strength reads as "he is there now", which is how
you end up trusting an angle nobody is covering.

Each teammate leaves a short trail — five positions by default, spaced out so it shows which way
they have been drifting rather than every step. And when someone is outside the view, an arrow at
the edge points at them with their name and distance, so zooming in on your own corner does not
mean losing track of everyone else. A teammate whose position has gone properly stale gets no arrow
at all, because pointing insistently at where somebody was three minutes ago is worse than saying
nothing.

**Shared routes.** Your route markers go out to the squad in your color, and each pin disappears
for everybody the moment *you* reach it — nobody else's arrival touches it, so there is nothing to
agree on and no radius to guess at. Teammates' routes are drawn smaller and fainter than your own,
named once at the first pin, and they **never** redirect your guide line or move your camera. A
feature that lets somebody else steer your view would be a griefing tool rather than a convenience.
Turn it off in **Settings**, which also withdraws whatever you have already shared rather than
freezing it on their maps.

**Everyone's color is their own.** The color you pick is sent to the squad rather than worked out
from your place in the roster, so every client draws you the same — including your pings. Two
clients used to be able to disagree the moment somebody left, which is exactly the confusion colors
are supposed to prevent.

There is no server. The code contains the host's address and a shared key, so the squad connects
directly and the traffic is encrypted with a key nobody else has. Everyone dials the host, which
means only one person needs to be reachable from outside their router — and the host does not even
have to be playing.

> **Everyone needs v0.8.0 or newer to share a session.** Colors and routes changed the wire format,
> and the key derivation moved with it, so an older build cannot connect at all rather than
> connecting and then failing halfway through a raid. Failing at the door is the kinder half of a
> break that was going to happen either way.

**If you have to forward a port**, it is one: **`TCP 24601`, inbound, to the hosting PC**. Only the
host needs it; people joining you open nothing. While hosting, the panel says exactly where you are
reachable — `Hosting on 203.0.113.4:24601 (TCP)` — and turns amber with the forwarding details on
hover when your router would not open it for itself. The same line is in **Settings** before you
ever need it. The port is configurable there if 24601 clashes with something, and hosting refuses to
start rather than quietly moving to another port — a forward pinned to a number that has silently
changed is the worst kind of broken. `--party-test` tells you which case you are in.

> **Worth thinking about before you use it.** Position sharing keeps the app's "never touches the
> game" property, but it does change who knows what. With your own squad it is hard to object to —
> you could already say "I'm at Dorms" over voice, and this is only more precise. But the app cannot
> tell a squad from a group of strangers coordinating, because it has no view into the game's party
> system. That is a judgement call it cannot make for you.

**Minimap.** Press **Minimap** for a small always-on-top window that can sit over the game itself,
for when you have only one screen. It draws everything the main map does — your position and trail,
exits, routes, squad, pings — at a range you set in game meters, and it **always** centers on you.
There is no follow toggle: a minimap you can pan away from your own position is just a small map,
and the point of this one is that a glance answers "what is around me" with no interaction at all.

Drag it anywhere, resize from the corner, scroll to change the range, and set how solid it is with
the slider in its header. **Settings** has a click-through option that lets the mouse reach the game
underneath — that one lives there rather than on the minimap, because switching it on makes the
window unclickable, including whatever would switch it back off.

It is still just a window. Nothing is drawn into the game, nothing is injected, and it shows only
what the main window already had.

**Light and dark, at a readable size.** Monospaced throughout, with a text-size slider, and an
always-on-top toggle for when it shares a screen with something else.

**Your marker, your color.** Pick from ten named colors and set the marker size, in **Settings**.
The same color is what your squad sees you as, and while a session is running the picker marks the
colors somebody else is already using — nobody is stopped from picking one, and nobody is ever
silently reassigned, but the moment to mention a clash is while you are choosing.

## First run

The app looks for `Escape from Tarkov\Screenshots` under Documents, and under OneDrive if OneDrive
has relocated your Documents folder, preferring whichever one actually has screenshots in it. If it
still points somewhere wrong, press **Find** in **Settings**, or **Browse** to set it by hand. The
line under the folder says how many Tarkov screenshots it can see — if that says zero after you
have taken some in raid, the game is writing somewhere else and **Find** will locate it. Pick your map from the dropdown; if a position lands somewhere the current map
cannot contain, the app offers to switch.

## Building from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
git clone https://github.com/Battleroid/tarkov-map-companion
cd tarkov-map-companion
dotnet test              # 413 tests
Run.bat                  # or: dotnet run --project src/TarkovMapCompanion
```

To produce the same single-file executable the releases ship:

```
scripts\publish.ps1
```

### Extra command-line modes

| Command | What it does |
| --- | --- |
| `--render-test <map> [out.png] [w] [h] [floors] [nobase] [bare] [marks]` | Renders a map to a PNG with no window. The quickest way to check a coordinate change. `floors` is a comma-separated list, `nobase` hides the ground floor, `bare` drops the markers, `marks` lays a route of waypoints across it. |
| `--fetch-data [out]` | Rebuilds the embedded POI snapshot from tarkov.dev. |
| `--fetch-tasks [out]` | Rebuilds the embedded quest snapshot from tarkov.dev. |
| `--fetch-wiki [out]` | Rebuilds the embedded exit conditions from the EFT wiki. |
| `--find-screenshots` | Prints every place Tarkov screenshots might be and what is in each. What to run when the map never moves. |
| `--find-logs [folder] [all]` | Prints where Tarkov's own logs were found, what the parser made of the newest one, and any location name this build does not recognize. What to run when the map stops switching. `all` reads every launch's log rather than only the newest. |
| `--party-test [name] [local]` / `--party-test join <code> [name]` | Hosts or joins a position-sharing session from the console. Says whether this network can host at all, which is the one part that cannot be tested any other way. `local` uses loopback so two processes on one machine can talk. |
| `--read-exits <screenshot.png> [map] [whole]` | Reads the extraction panel out of one screenshot and prints every stage: the raw text, the rows it grouped, and which exits it matched. What to run when a read goes wrong. |

## Where the data comes from

Map artwork, geometry, exits, spawns and loot positions come from the
[tarkov.dev](https://tarkov.dev) community project — specifically
[`tarkov-dev`](https://github.com/the-hideout/tarkov-dev) for the geometry (MIT) and
`json.tarkov.dev` for the point-of-interest data. Exit *conditions* are not in that data and are
taken from the [Escape from Tarkov Wiki](https://escapefromtarkov.fandom.com) (CC BY-SA).

Item pictures come from `assets.tarkov.dev`, one file per item, addressed entirely by the item's
BSG id — which the quest snapshot already carries, so there is no index to download and nothing to
keep in sync. They are fetched the first time an item is shown and cached on disk from then on;
about 2.6 KB each, and under a megabyte for every item the whole snapshot references. With
**Allow network** off, names show and pictures do not.

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
| `%LOCALAPPDATA%\TarkovMapCompanion\cache\icons\` | Item pictures, one per item id. Safe to delete; they come back as needed. |
| `%LOCALAPPDATA%\TarkovMapCompanion\cache\app.log` | What the app was doing, including why it stopped. Worth attaching to a bug report. |

Party activity is logged on both sides with a shared tag derived from the session secret, like
`[party ecb8932e host]`. Both ends compute the same tag without either sending it, so two people's
logs can be lined up side by side — and a tag that differs is itself the answer, because it means
somebody pasted an older code. Addresses are logged with the last octet masked, so a pasted log
does not hand out anyone's IP.

## License

Code is [MIT](LICENSE). Bundled third-party data keeps its own terms — see
[NOTICE.md](NOTICE.md).

Escape from Tarkov is a trademark of Battlestate Games. This is an unofficial fan-made tool with no
connection to Battlestate Games.
