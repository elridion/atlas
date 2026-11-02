# Atlas

Worlds without a map get a hand-inked atlas in the style of a classic fantasy novel: parchment, a
heavy coastline with ripples on the open sea, and small pen marks for every biome. Only what you
have recorded at a cartography table is drawn; the rest of the world stays blank parchment.

Atlas only works in worlds without a map (the `nomap` world modifier). With the map turned on,
nothing changes.

## How it works

| Where | What happens |
| --- | --- |
| Cartography table, "Record discoveries" | As in the game, your discoveries are written to the table and you learn what others wrote there. Atlas also updates your atlas with everything you and the table know, and with your pins. |
| Cartography table, "Read map" | Opens the game's interactive map, as in a world with a map. Place, name, check and remove pins here. Close it with the map key or Escape. |
| Map key (M) anywhere | Unrolls your atlas as it was when you last recorded your discoveries. Close it with the map key or Escape. |

The atlas is a drawing, not a live map: it does not show where you stand, and land you explore or
pins you place appear only after your next visit to a cartography table.

## Controls

| Input | Action |
| --- | --- |
| Mouse wheel | Zoom in and out around the pointer |
| Left mouse, drag | Move the sheet |
| Right mouse | Back to the opening view |
| Gamepad left stick | Move the sheet |
| Gamepad right stick, up and down | Zoom |
| Map key, Escape, gamepad B | Close |

## The pen marks

Every biome except the mountains has a ground fill of its own (the first mark in each row), so you can tell them apart even where the woods are thin. The Deep North is the exception that proves it: a near-blank snowfield.

| Biome | Marks |
| --- | --- |
| Ocean | A heavy coastline ringed by ripple lines that fade out over open water; wave marks further out |
| Meadows | Tufts of grass, dense woods of egg-shaped trees, groups of open hills |
| Black Forest | Stones on the forest floor under dense dark pines, with a few clearings |
| Swamp | Wavelets and still water, marsh tufts, crooked dead trees |
| Mountain | Ranges of overlapping peaks with a solid shaded eastern face, sized by height, ringed by foothills |
| Plains | A stipple of dry ground, a few lone round trees, open hills |
| Mistlands | Banks of mist drifting across the woods, giant three-crowned trees, hatched rock pillars |
| Ashlands | Burnt, hatched ground, lava cracks and a few large smoking volcanoes |
| Deep North | A near-blank snowfield crossed by thin bands of ice ridges, high peaks and ice floes in the northern sea |

## Settings

None. Atlas uses your map key and the game's own pin icons and fonts.

## Notes

- Client-side: install it on your own game; the server and other players do not need it.
- The atlas for a world is drawn once, in the background, the first time you play there with
  Atlas, and kept in `BepInEx/cache/elridion.atlas`. Until it is ready, the map key says so.
- Your recorded discoveries and pins are saved on your character, per world. Removing Atlas leaves
  them unused; nothing in the world is changed.
- Other map mods that also change the cartography table or open a map in nomap worlds (such as
  Nomap Printer) do the same job and should not be installed alongside Atlas.

## Credits

- The icon is "Treasure map" by Lorc from [game-icons.net](https://game-icons.net), licensed under
  [CC BY 3.0](https://creativecommons.org/licenses/by/3.0/).
- The idea of a printed map at the cartography table comes from shudnal's Nomap Printer. The
  drawing style follows the classic fantasy-novel maps, as analysed in Scott Turner's
  [Here Dragons Abound](https://heredragonsabound.blogspot.com/).
