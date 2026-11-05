# Atlas

Worlds without a map get a hand-inked map in the style of a classic fantasy novel: parchment
under a faint watercolour wash, a heavy coastline shaded by short wave strokes, woods drawn as
bounded masses of trees, and small pen marks for every biome. Only what you
have recorded at a cartography table is drawn; the rest of the world stays blank parchment.

Atlas only works in worlds without a map (the `nomap` world modifier). With the map turned on,
nothing changes.

## How it works

| Where | What happens |
| --- | --- |
| Cartography table, "Record discoveries" | As in the game, your discoveries are written to the table and you learn what others wrote there. Atlas also updates your map with everything you and the table know, and with your pins; golden sparkles rise from the table until it is done. |
| Cartography table, "Read map" | Opens the game's interactive map, as in a world with a map. Place, name, check and remove pins here. Close it with the map key or Escape. |
| Map key (M) anywhere | Unrolls your map as it was when you last recorded your discoveries. Close it with the map key or Escape. |

The map is a drawing, not a live map: it does not show where you stand, and land you explore or
pins you place appear only after your next visit to a cartography table. A narrow band round
what you explored shows on bare parchment in a paler, greyer ink, a glimpse of what lies beyond.
Across it the drawing thins out and stops, as if the cartographer did not know what came next:
woods end in a few outlying trees, coasts and rivers simply end, and a mountain is drawn whole or
not at all.

## Controls

| Input | Action |
| --- | --- |
| Mouse wheel | Zoom in and out around the pointer |
| Left mouse, drag | Move the sheet |
| Right mouse | Back to the opening view |
| Point at a pin | Shows the pin's name |
| Gamepad left stick | Move the sheet |
| Gamepad right stick, up and down | Zoom |
| Map key, Escape, gamepad B | Close |

## The pen marks

Each biome has a faint colour wash of its own under the ink, a little darker in the woods, so
you can tell the biomes apart at any zoom. Woods are drawn where the game grows its forests: each
one a dense mass of trees bounded by its outer crowns, with open land around it. Every tree
carries its biome's wood colour, as the peaks carry the mountains' grey. The open land of most
biomes gets a light ground fill (the first mark in each row); the Deep North's open land is a
near-blank snowfield.

Biome variants change their woods too. A felled Black Forest (Kalhygge) has no trees, only a few
stands of stumps, hills and a light stipple. Pine and rocky Black Forests, small-tree Meadows, drake and fortress Mountains
and lox and death Plains have sparser woods, about half the trees. Wolf Mountains and tree-filled
Mistlands have denser woods, and bog Swamps more dead trees.

| Biome | Marks |
| --- | --- |
| Ocean | A fine coastline traced on the game's own water line, and groups of up to seven level wave strokes at bays, headlands and here and there along straight coasts, each starting at the shore and running out to sea, long and short in turn; islets outside the Mistlands that lie clear of the coast are gathered into clusters, each with a small group of level strokes sized to it, long and short in turn, starting at an islet's shore or lying in the open water beside it and never crossing land, larger islands getting the coast's waves instead, while the Mistlands' islet fields, lone specks and the open sea get no waves. Far out in the widest open water a few sea monsters are inked, at most three of each: sea serpents, serpent coils and the odd leviathan in the open ocean, the bonemaw's bare bones in the Ashlands' boiling sea, which is warmed reddish-brown as on the game's map and simmers in a very few spots far out (bubbles rising from a short level wave), none in the northern sea; each lies in a slightly darker wash of the sea's blue, laid in its body, and shows whole once its stretch of sea is mostly explored. Water is washed in indigo; lakes, ponds and swamp pools share the same shore line, and lakes get stacks of straight lines instead of waves, each touching the shore and running into the lake, at its bends and along its straight shores, while ponds stay bare |
| Meadows | Tufts of grass on open ground, woods of egg-shaped trees shaded on their eastern side, groups of rolling hills on the real hilltops |
| Black Forest | Stones on open ground, woods of dense dark pines |
| Swamp | Short marsh lines with tufts of reeds, gathered round the real pools and thinning toward the edge, tall ancient dead trees; its pools read as marsh under a faint blue glaze, only deep water open to the sea stays water |
| Mountain | Real summits as large jagged peaks, some with shoulders or twin summits, their eastern faces hatched, smaller peaks massed between them, foothills on the real hilltops below, woods of dense dark pines on the gentler ground below 280 m where the game grows its firs, kept clear of the peaks' bases |
| Plains | A stipple of dry ground, copses of small round shaded trees, rolling hills on the real hilltops, each drawn a little differently: low or round, a long ridge, a double crest or a shoulder, with or without smaller hills behind |
| Mistlands | Veils of mist lying in the valleys over woods of gnarled, broad-crowned trees, black crags on the summits and the broken ground, and small scarps with hachures falling downhill on the open steep rock between them; its many small pools read as ground, only its lakes and the sea stay water |
| Ashlands | Burnt, ash-clouded ground stippled with cinders, thickest by the lava; red lava fields toned with fine engraved hatching; small rocky peaks and volcano cones with open craters |
| Deep North | A near-blank snowfield crossed by thin bands of ice ridges, woods of dense dark pines where the game grows its snowy forests, high peaks and snow-white ice floes in the northern sea |
| Rivers | Wide rivers show as water between two banks; narrow rivers and streams as ink lines that taper toward their source |
| Sacrificial Stones | A ring of standing stones round a low altar, where you first arrived |

## Settings

Atlas uses your map key. Your pins are inked into the map itself in solid red, in its own pen
(the game's five pin icons and the death marker in their own hand-drawn form, details such as a
door left in bare paper), each as large as what it stands for beside the trees; on checked pins
the paint looks half scraped away; pins in land you have not explored and other mods' icons stay
markers over the parchment. Point at a pin to see its name. The game's own marks are drawn as
map signs too. Each kind can be switched on or off in `BepInEx/config/elridion.atlas.cfg` (or a
config manager); a change redraws the map in the background.

| Section | Key | Default | Description |
| --- | --- | --- | --- |
| Pins | `PlayerPins` | true | Show the pins you placed yourself (the five icons of the pin menu) on the map. |
| Pins | `BedPin` | true | Show your bed (spawn point) on the map. |
| Pins | `DeathPin` | true | Show where you last died on the map. |
| Pins | `BossPins` | true | Show the boss altars a Vegvisir revealed on the map. |
| Pins | `TraderPins` | true | Show the traders (Haldor, Hildir, the Bog Witch) on the map. |
| Pins | `HildirPins` | true | Show the dungeons of Hildir's requests on the map. |
| Pins | `PlacePins` | true | Show the Sacrificial Stones, the Forge of Potential and the places other mods mark on the map. |
| Pins | `UnexploredPins` | true | Show pins that lie outside the land you have explored. Off, only pins on explored land show. |
| Fog | `Glimpse` | true | Show a band of the map on bare parchment past the land you have explored, and the game's own marks out in unexplored land (bosses, traders) drawn into the map with a glimpse round them. Off, the fog ends at the explored land and those marks show as markers on it. |

## Notes

- Client-side: install it on your own game; the server and other players do not need it.
- The map for a world is drawn in the background the first time you play there with Atlas,
  and again whenever your pins change, and kept in `BepInEx/cache/elridion.atlas`. Until it is
  ready, the map key says the ink hasn't dried yet.
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
