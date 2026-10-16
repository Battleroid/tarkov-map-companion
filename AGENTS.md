# Tarkov Map Companion — agent notes

## What this is

A Windows desktop companion for Escape from Tarkov. It reads the **filenames** of screenshots the
game already writes, finds the player on a map from them, and draws a guide line to an exit. It
never reads memory, hooks the process, or talks to the game, and it uploads nothing.

## Running it

The .NET 8 SDK here is user-local and **not on PATH** — every session needs:

```powershell
$env:DOTNET_ROOT="$env:USERPROFILE\.dotnet"
$env:PATH="$env:USERPROFILE\.dotnet;$env:PATH"
dotnet test
dotnet run --project src/TarkovMapCompanion     # or: Run.bat
scripts\publish.ps1                             # single-file publish -> publish\
```

`global.json` pins 8.0.421 (`rollForward: latestFeature`); without it nothing builds. To diagnose
without a window: `--render-test`, `--read-exits`, `--find-screenshots`, `--find-logs`.

**Re-run `scripts\publish.ps1` after every change that should be visible in the app.** `publish\` is
gitignored, so it silently goes stale and the user keeps launching a binary from before the fix — a
WebSync LAN-address fix was verified in the Debug build while the shipped `publish\` exe still had
the old behaviour. The user runs the published exe for hands-on testing, so a green `dotnet test`
does not mean what they are running is current.

## Stack

.NET 8 · Avalonia 11 · SkiaSharp · Svg.Skia. Windows-only (uses the built-in Windows OCR).
CI runs on `windows-latest` and includes a real `--render-test` smoke test.

## Conventions

- `src/TarkovMapCompanion/{Data,Views,Localization,Tools,Settings}` + `tests/TarkovMapCompanion.Tests/`.
- **Control `Name`s in `PreferencesWindow.axaml` are load-bearing.** The code-behind finds controls by
  name, so renaming one breaks the binding silently — the setting still saves but the control never
  updates. The 2026-10-14 rebuild into six category pages moved thirteen panels and changed no `Name`
  for exactly this reason. The dialog also applies every edit live; there is no OK/Cancel.
- **Localization keys are the contract.** `StringsEn.json` and `StringsZh.json` must have identical
  key sets. `Localizer.Pick(english, chinese)` is the one place a language decision is made; call
  sites must not branch on language themselves. Data that is *always* English (POI names, map keys,
  wiki note keys) must be reached through the English accessor — comparing it against a display
  string only works while the UI happens to be in English. The remaining contract debt is in
  `docs/localization-ocr-chinese-backlog.md`.
- **The Chinese OCR gap is a four-link chain, not a matching bug.** Assessed and phased in
  `docs/localization-ocr-chinese-backlog.md` (C1–C5). Only the threshold step is still blocked, on
  real Chinese-client screenshots. Two traps are written up there and are easy to re-invent:
  fixing *only* the candidate set leaves the feature silently dead, because `ExitAvailability`
  stores the matched **candidate string** while `Includes` looks up the **English** name — so
  candidates have to become a surface→canonical map, and `dotnet test` can still pass. And the
  2020 (0.12.2) screenshots in that doc are fine for string shapes but **must not** be used to
  tune thresholds: the map data has moved on, so the unmatched rows are version drift wearing the
  costume of a code bug.
- **Do not trust the vendored data.** `Data/Snapshots/` holds a json.tarkov.dev payload and a wiki
  scrape, and it has shipped real defects. `PoiBuilder` normalizes three (mis-assigned switch ids,
  coincident same-name extracts, code-named extracts), each pinned in `PoiTests.cs` /
  `UpstreamDataFixesTests.cs`. A refresh that changes the shape should fail the suite, not silently
  reintroduce one. `season-documents.json` is a third source with a different trust profile: its
  coordinates were fitted against this repo's own artwork, so they are approximate by construction,
  and its per-point `confirmed` flag is the only confidence signal there is — never present an
  unconfirmed point as if it had been surveyed.
- `.gitattributes` is load-bearing: `*.sln`/`*.bat`/`*.ps1` CRLF, everything else LF.
- PowerShell mangles UTF-8 here. Read or compare JSON with `python`, not `ConvertFrom-Json`.

## Current state

Fork of `Battleroid/tarkov-map-companion`, pushed to `BlueMess/tarkov-map-companion-self-improv`.
**All work lands on `master`; there are no feature branches left.** It has delivered WebSync, the
vendored-data fixes, Simplified Chinese (`40613e8`), the preferences dialog rebuilt as a
six-category rail, and Simplified Chinese as the client default. Build clean, tests green.

The client defaults to Simplified Chinese, set in three places that must move together —
`AppSettings.Language`, the `Normalize()` fallback for a hand-edited file, and the `Localizer`
static initializer. An existing `settings.json` that names a language keeps it, so only new installs
move.

WebSync is an embedded, read-only LAN mirror of the map, built as
M1-M4 (skeleton + snapshot + token, basemap endpoints, WebSocket + client page, settings pane + QR)
and then hardened against a real phone. `src/TarkovMapCompanion/WebSync/` holds
a Kestrel server (`WebSyncServer`, all host work on a null-`SynchronizationContext` worker behind a
serialized reconciler, 2 s bounded stop), a `StateBroadcaster` (snapshot builder + 2 Hz player
throttle + WS client table), the DTOs (`WebSyncState`), `LanAddress`, `QrCode`, and the
single-file browser client (`Assets/index.html`). Spec and its corrections:
`docs/websync-implementation-spec.md`. Routes: `GET /`, `/api/state`, `/map/{id}.svg`,
`/tiles/{id}/{z}/{x}/{y}.png`, `WS /ws`; every route but `/` and `/assets/*` requires `?key=`, else
404. Wire the enable switch through `MapSession.ApplyWebSyncSettings()`. Tiles are cached under
`Path.Combine("tiles", NormalizedName)` to match `TileMapSource` (the bare `"tiles"` category is
wrong). Publish is now ~62 MB (the ASP.NET Core shared framework).

**Season documents ship as one layer.** `PoiKind.SeasonDocuments` draws season-1 ("KORD BREACH")
TerraGroup document spawns from `Data/Snapshots/season-documents.json` — 334 points over 11 maps,
321 confirmed plus 13 unconfirmed drawn dimmed with a `?`. It rides the existing
`Enum.GetValues<PoiKind>()` switch generation and is independent of quest selection.
`AppSettings` went to version 3 so an older `poiLayers` map backfills the new key instead of leaving
the layer dark. The upstream source is Chinese-only, so `Name` and `NameZh` carry the same string —
a deliberate exception to the English-canonical rule, because there is no English source to keep
canonical.

**Every `PoiBuilder.Build` call site must pass the store.** The layer has no other entry point, so a
call site that omits `seasonDocuments` builds a map with the layer silently missing — no error, no
warning, just absent markers. `--render-test` prints per-kind POI counts and `SeasonDocuments=<n>` in
that line is the smoke check; it caught exactly this after the main path was already wired.

**The snapshot's refresh pipeline lives outside this repo**, at `../_work/season-docs/` (scrape →
namefit calibration → merge → `export_companion.mjs`), next to the map artwork it calibrates against
in `_work/season-docs/maps/`. Nothing there is version-controlled, and the default paths are
relative to the scripts themselves. A new season means re-running that pipeline; do not hand-edit the
JSON, and do not assume a missing directory means the data was lost.

### Traps this branch paid for (do not rediscover them)

- **The client page had no cache headers and nobody noticed for several rounds.** `GET /` serves an
  embedded resource whose URL never changes, so browsers heuristically cached the HTML and every
  fix appeared to do nothing on the phone while local testing looked green. It now sends
  `Cache-Control: no-store, must-revalidate` + `Pragma: no-cache`. If a client change "does not take
  effect", check this first, then the published exe, before reading any drawing code.
- **`position:fixed; inset:0` sizes to the layout viewport, not the visual one.** On a phone the URL
  bar makes them differ, so a canvas sized by `inset:0` is displayed taller than the `innerHeight`
  its backing store and `setTransform(dpr,…)` assumed — every marker stretched while the DOM
  artwork was not. `#stage` and `#cv` are now pinned to the measured `vw`/`vh` in `measure()`.
  Desktop never reproduces this: the two viewports coincide.
- **Headless Chrome is not a phone.** `--window-size=390,844 --force-device-scale-factor=2` yields a
  780x1688 PNG while the page lays out at `innerWidth=491` — the screenshot silently crops the page,
  which invents "the artwork is clipped" findings. Always confirm `innerWidth`/`innerHeight`/`dpr`
  from the page before trusting a headless capture, and prefer the real device for anything visual.
- **`document.importNode` copies; `appendChild` moves.** Draining a parsed document with
  `while(root.firstChild) f.appendChild(document.importNode(root.firstChild,true))` never
  terminates and freezes the main thread — timers stop, so *no* timeout fires and the page just looks
  stuck on "loading".
- **Canvas 2D state is cumulative.** `save()/restore()` pairs can be balanced and still leak
  `globalAlpha`/`lineWidth`/`font`/`fillStyle`. Everything drawn on the marker layer now runs inside a
  `scoped()` save/restore, and `draw()` resets its transform and paint baseline first.
- **Sizes copied from the desktop are not phone sizes.** `PoiOverlay`'s radii are CSS pixels on a
  ~1920 px window; on a 366 px phone they read ~5x too large. The client scales marker geometry by
  `clamp(vw/1000, 0.6, 1.0)`.
- **The client has no JS test coverage at all.** The tests exercise the server; a single line of
  client JavaScript froze the page for several releases and nothing caught it. `?selfcheck=1` asserts
  the canvas transform survives a rotate and a dashed ring; that is the only automated guard, and it
  is thin.
- **Round-trip the map data, not just the formulas.** `gameToBase` reproduces `baseRect` exactly from
  the snapshot's `bounds`, so a coordinate bug is not where a misalignment lives. What can differ is
  the rect the artwork is stretched onto, the layer's own sizing, and the viewport the two layers are
  measured against.

Rendering cost is a known, **unaddressed** problem: measured 15-43 ms per frame (headless Skia CPU,
1600x1100; shoreline full overlays 43.13, bare 14.82), and the 20 fps `MapClock` runs continuously
whenever `WaypointOverlay.Advance()` sees >= 2 waypoints (`AnimateArrows` defaults true), i.e. ~86%
of a core for the whole raid. Idle is ~0 (event-driven repaints). WebSync is *not* the cost driver.
Findings and the prioritized optimization list (P1 arrow animation, P2 per-frame overlay allocs +
O(n^2) `LabelPlacer`, P3 re-rasterize on zoom, P4 memory, P5 background chores) are written up in
`docs/render-performance-backlog.md` — that file is the brief for a future optimization pass, not
finished work. Re-measure with `--render-test <map> <out.png> 1600 1100 [bare]` before and after.

## Open questions

- **Trailer Park** — two coincident records carry different Chinese names (拖车停车场 /
  拖车停车场工人棚屋) and research says they are two exits, but the merge folds them into one. Owner
  chose to keep current behaviour.
- **`Gate Near Medblock` -> `17号仓库`**, **`Reserve Checkpoint` -> `Scav检查站`** — upstream's English
  and Chinese disagree for those keys; left as-is because the in-game Chinese could not be verified
  without a game install. Needs someone who can read the client.