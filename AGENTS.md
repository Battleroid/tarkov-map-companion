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
dotnet test                                     # 703 tests
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
- **Localization keys are the contract.** `StringsEn.json` and `StringsZh.json` must have identical
  key sets. `Localizer.Pick(english, chinese)` is the one place a language decision is made; call
  sites must not branch on language themselves.
- **Do not trust the vendored data.** `Data/Snapshots/` holds a json.tarkov.dev payload and a wiki
  scrape, and it has shipped real defects. `PoiBuilder` normalizes three (mis-assigned switch ids,
  coincident same-name extracts, code-named extracts), each pinned in `PoiTests.cs` /
  `UpstreamDataFixesTests.cs`. A refresh that changes the shape should fail the suite, not silently
  reintroduce one.
- `.gitattributes` is load-bearing: `*.sln`/`*.bat`/`*.ps1` CRLF, everything else LF.
- PowerShell mangles UTF-8 here. Read or compare JSON with `python`, not `ConvertFrom-Json`.

## Current state

Fork of `Battleroid/tarkov-map-companion`; `master` at `d84e569`, pushed to
`BlueMess/tarkov-map-companion-self-improv`. Build clean, 703/703 tests pass. The four local commits
(Simplified Chinese + three data fixes) are **not yet an open PR** — a bilingual description is
drafted at `%TEMP%\opencode\PR_DESCRIPTION.md`, base `Battleroid/tarkov-map-companion:master`.

On branch **`websync`** (four commits on top of `d84e569`): an embedded, read-only LAN mirror of the
map. `src/TarkovMapCompanion/WebSync/` holds a Kestrel server (`WebSyncServer`, all host work on a
null-`SynchronizationContext` worker behind a serialized reconciler, 2 s bounded stop), a
`StateBroadcaster` (snapshot builder + 2 Hz player throttle + WS client table), the DTOs
(`WebSyncState`), `LanAddress`, `QrCode`, and the single-file browser client
(`Assets/index.html`). Spec and its corrections: `docs/websync-implementation-spec.md`. Routes:
`GET /`, `/api/state`, `/map/{id}.svg`, `/tiles/{id}/{z}/{x}/{y}.png`, `WS /ws`; every route but `/`
and `/assets/*` requires `?key=`, else 404. Wire the enable switch through
`MapSession.ApplyWebSyncSettings()`. Tiles are cached under `Path.Combine("tiles", NormalizedName)`
to match `TileMapSource` (the bare `"tiles"` category is wrong). Publish is now ~62 MB (the
ASP.NET Core shared framework).

## Open questions

- **Trailer Park** — two coincident records carry different Chinese names (拖车停车场 /
  拖车停车场工人棚屋) and research says they are two exits, but the merge folds them into one. Owner
  chose to keep current behaviour.
- **`Gate Near Medblock` -> `17号仓库`**, **`Reserve Checkpoint` -> `Scav检查站`** — upstream's English
  and Chinese disagree for those keys; left as-is because the in-game Chinese could not be verified
  without a game install. Needs someone who can read the client.
- **`_smoke/`** — 45 files / 7.8 MB of untracked scratch. Delete or ignore; undecided.