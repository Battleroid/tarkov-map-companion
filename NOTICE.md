# Third-party data and licenses

The application code is MIT licensed (see [LICENSE](LICENSE)). It bundles data produced by other
projects, which keeps its own terms.

## Map geometry — tarkov.dev

`src/TarkovMapCompanion/Data/Snapshots/maps.json` is a copy of `src/data/maps.json` from
[the-hideout/tarkov-dev](https://github.com/the-hideout/tarkov-dev), MIT licensed,
Copyright (c) 2019 Oskar Risberg.

## Point-of-interest data — tarkov.dev

`src/TarkovMapCompanion/Data/Snapshots/mapdata.json.gz` is derived from
`https://json.tarkov.dev/regular/maps`, published by the same project. Regenerate with
`--fetch-data`.

## Map artwork

Map images are downloaded at runtime from `assets.tarkov.dev` and cached locally; they are not
redistributed in this repository. They remain the work of their authors:

| Author | Maps |
| --- | --- |
| Shebuka | Customs, Factory, Ground Zero, Interchange, Lighthouse, Reserve, Shoreline, Streets of Tarkov, Terminal, Woods |
| Tarkov.dev | The Lab, The Labyrinth |
| TarkovBOT.eu | Icebreaker |

## Exit conditions — Escape from Tarkov Wiki

`src/TarkovMapCompanion/Data/Snapshots/extract-notes.json` contains extraction requirements
gathered from the [Escape from Tarkov Wiki](https://escapefromtarkov.fandom.com), which is licensed
[CC BY-SA 3.0](https://creativecommons.org/licenses/by-sa/3.0/). Only the short structured
requirement fields are used; the wiki's free-text notes are not reproduced. Regenerate with
`--fetch-wiki`.

## Trademarks

Escape from Tarkov is a trademark of Battlestate Games. This project is unofficial and is not
affiliated with, endorsed by, or connected to Battlestate Games.
