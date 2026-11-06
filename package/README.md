# Atlas

Nomap worlds get a hand-inked map in the style of a classic fantasy novel. Record your discoveries at a cartography table, then press the map key to unroll it.

## Features

- Only what you recorded at a cartography table is drawn; the rest stays blank parchment, and
  the drawing thins out and stops a little past what you know.
- Every biome in its own pen marks and colour wash: woods, peaks, marshes, lava fields, snowfields,
  rivers, wave strokes and the odd sea monster. Biome variants show in their woods.
- Your pins and the game's own marks (bed, last death, bosses, traders, Hildir's dungeons) are
  inked into the map in red. Zoom in close and point at one to see its name.
- "Read map" at the table opens the game's interactive map for placing and editing pins.
- Zoom in for close-ups redrawn from the world itself, accurate to a few metres.
- Drawn in the background and cached, so opening the map never stalls the game.

## How to use

- At a cartography table, "Record discoveries" also updates your map; golden sparkles rise from
  the table until it is done.
- The map key unrolls your map. Mouse wheel zooms, left drag moves, right click resets the view;
  on a gamepad the left stick moves and the right stick zooms. Map key, Escape or B closes it.
- The map is not live: it shows no player position, and new land and pins appear after your next
  visit to a table.

## Installation

- Client-side: the server and other players do not need it.
- Works only in worlds with the `nomap` world modifier. With the map on, nothing changes.
- Do not combine with other mods that put a map on the cartography table in nomap worlds, such as
  Nomap Printer.
- Uninstalling is safe: your recorded discoveries and pins stay unused on your character and
  nothing in the world is changed. The drawn maps are cached in `BepInEx/cache/elridion.atlas`
  and can be deleted.

## Configuration

Settings are in `BepInEx/config/elridion.atlas.cfg` (or a config manager). They choose what the
map shows; a change redraws it in the background.

| Section | Key | Default | Description |
| --- | --- | --- | --- |
| Pins | `PlayerPins` | true | Your own pins. |
| Pins | `BedPin` | true | Your bed (spawn point). |
| Pins | `DeathPin` | true | Where you last died. |
| Pins | `BossPins` | true | Boss altars a Vegvisir revealed. |
| Pins | `TraderPins` | true | Haldor, Hildir and the Bog Witch. |
| Pins | `HildirPins` | true | The dungeons of Hildir's requests. |
| Pins | `PlacePins` | true | The Sacrificial Stones, the Forge of Potential and places other mods mark. |
| Pins | `UnexploredPins` | true | Pins outside the land you have explored. |
| Fog | `Glimpse` | true | A band past your explored land in paler ink, with the game's marks out there drawn in. Off, the drawing stops at your explored land. |

## Credits

- The icon is "Treasure map" by Lorc from [game-icons.net](https://game-icons.net), licensed under
  [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).
- Inspired by shudnal's Nomap Printer, which first gave nomap worlds a printed map at the
  cartography table.
- The drawing style follows the classic fantasy-novel maps, as analysed in Scott Turner's
  [Here Dragons Abound](https://heredragonsabound.blogspot.com/).
