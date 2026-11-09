# Changelog

## 0.1.4

- Adding, removing or moving a pin redraws the map sooner.
- Recording discoveries when nothing new was explored finishes sooner.
- Zoomed in, the detailed view stays sharp after a pin is added, removed, moved or checked:
  only the part close round that pin is drawn again, in place, without turning blurry
  meanwhile, and a view left zoomed in is caught up sooner after editing pins at a table.
- Swamp dead trees near a pin may now stand partly in front of it, as other trees do,
  instead of leaving a clearing round it. Your map is drawn again once after updating.

## 0.1.3

- Joining a server no longer switches the atlas off until the game is restarted: it waits
  for the server to name the world before preparing the map.

## 0.1.2

- Pointing at pins that overlap names the one in front, and a pin without a name no longer
  hides the name of a named pin beside it.
- Markers and pin names match the smaller pins drawn at the closest zoom, and overlapping
  markers in unexplored land stack with the southern one in front.
- Checked pins are scraped alike at every zoom.
- A dot just south of another pin is drawn in front of it instead of as a sliver behind it.
- Pins are drawn over waves, ice floes and sea creatures. Your map is drawn again once after
  updating.

## 0.1.1

- Zoomed in close, a pin's name now shows centred above the pin you point at, in larger,
  heavier lettering on a ribbon banner drawn in pen on the bare parchment, its ends folded back
  into swallowtails, so it reads over woods and mountains. Checked (crossed out) pins show no
  name, and pointing between one and an unchecked pin names the unchecked one.
- The map can be opened sooner after joining a world, mapped before or not (it starts preparing
  while the world loads), joining no longer hitches a frame while the saved map is read, and
  pointing at a pin no longer stutters the first time its name shows; its banner is ready almost
  at once.
- Recording discoveries several times in a row no longer redraws the map once for each record:
  only the latest pins are drawn.
- Background work no longer stalls, or locks up, waiting on itself, which could hold up the map
  after recording at a table.
- Pins placed, moved, removed or checked on the map you read at a table show on your atlas as
  soon as you close that map; newly explored land still needs "Record discoveries".
- Checking or unchecking a pin at the table now updates the map almost at once instead of
  redrawing the whole world, and other pin changes redraw it in well under half the time.
- After recording discoveries, close-up detail is kept where nothing changed instead of being
  drawn again everywhere, and none is drawn under the fog, so zoomed-in detail appears sooner.
- Close-ups are drawn ahead where the map is panned and zoomed out, and for the view the atlas
  reopens on, so they are usually ready when it opens.

## 0.1.0

- In worlds without a map, "Record discoveries" at a cartography table now also updates your map,
  and the map key unrolls it: a hand-inked map in the style of a classic fantasy novel, with
  unexplored lands left as blank parchment. A little beyond where you have explored the map
  shows on bare, untinted parchment in a paler, greyer ink, with no colour in its woods and
  peaks, a glimpse past what you know, and across that band the drawing thins out and stops, as
  if the cartographer did not know what came next: woods end in a few outlying trees, the sea's
  wave strokes give out, coasts and rivers simply end, and every tree and mountain is drawn whole
  or not at all. The game's own marks out in unexplored land, such as a boss a Vegvisir showed
  you, are drawn into the map with such a glimpse round them. Golden sparkles rise from the table while the map
  is being updated and burst once it is done. A setting turns the glimpse off, so the drawing
  stops along what you have explored and the game's marks out there show as plain markers.
- Every biome has a faint colour wash of its own under the ink, so biomes read apart at any zoom,
  and water is one even indigo wash ending crisply at the shore. The colours are thin glazes of pigments a northern illuminator of the
  period could have had.
- The parchment is aged all over: an uneven patina, and old water stains with tide lines, dried rings and clusters of small brown spots lying over everything on the map, ink and pins included, as on a real stained sheet.
- The parchment's edge is trimmed by hand: a slightly wandering cut with worn, rounded corners and
  a darker, handled rim.
- Woods are drawn where the game grows its forests, each one a dense mass of trees bounded by its
  outer crowns, with the open land around it kept clear. A clump of Meadows trees too small to
  draw as a wood stands as one or two lone trees on its spot. As in the game, no Meadows or Black
  Forest tree stands on ground steeper than 30 degrees; such bare slopes inside a wood take
  the open ground's marks (a light stipple in the Black Forest, tufts of grass in the Meadows)
  instead of lying as empty dark green. Lone gnarled oaks with round billowy crowns stand here
  and there on the open Meadows, about as often as the game grows its big oaks there, clear of
  the woods, hills, water and steep ground; grass never peeks out from behind one. Every tree carries its biome's wood
  colour, as the peaks carry the mountains' grey, right out to the edge of its wood.
- Every biome has its own pen marks: each mountain as one big jagged, hatched peak (more only where it has clearly separate summits) with low ridges round it, washed in the mountains' grey, ringed by foothills, grass and woods of
  round crowns in the Meadows, dark pine woods in the Black Forest, patches of marsh lines with reeds
  among many tall ancient dead trees on the Swamp's higher ground, a dry stipple and copses on the Plains, rolling hills on the real hilltops of the
  Meadows and Plains, each drawn a little differently (low or round, a long ridge, a double crest or a shoulder), gnarled trees over the whole of the
  Mistlands, thinner where the mist lies and sparse on the steep rock, veils of mist filling the hollows of its misty ground, black crags, some rising out of its shallow sea on a level waterline as the game's rock spires do, and small hatched scarps marking the steep rock between them (its many small pools kept as water), a cinder stipple, hatched vermilion lava fields wherever the game lays its lava, down to small pools, and spikes of rock standing out of its boiling sea in the Ashlands (no rock is drawn on its land, where the game scatters it at random and it would only mislead), and a
  near-blank snowfield with thin bands of snow marks in the Deep North, kept clear of the shore, the peaks and each other.
- The Deep North's snowy forests are drawn as woods of dense dark pines, as in the Black Forest,
  wherever the game grows its firs and pines there, up its high ground too; its snow marks keep
  to the open snow. Its high ground is drawn as the gentle uplands it is: a peak on each real
  summit and hills on its other hilltops, not whole mountain ranges.
- Biome variants show in their woods: a felled Black Forest is drawn with stands of stumps among its hills,
  a rocky Black Forest, where the game grows big boulders instead of its big trees, as open
  ground with clusters of shaded boulders, hills and a few lone pines clear of the shore,
  stippled as the Plains between them,
  variants where the game grows fewer trees get sparser woods (bog Swamps about a third of the
  dead trees, pine Black Forests, drake and fortress Mountains and hut Swamps about half, lox
  Plains about half as open grazing land, birch Meadows and death Plains three quarters), small-tree Meadows woods of
  small leafy trees, each a little crown with a three-lobed top, with only a rare big one among them, and those where it grows more (wolf Mountains, tree-filled
  Mistlands) get denser woods.
- The Mountains' fir woods are drawn too, as dark pine woods on the gentler slopes where the game
  grows its firs, from the lush woods at the mountains' feet up between the peaks and onto the
  lower slopes of the peaks and ridges, whose tops stay clear; the ground under the trees keeps
  the mountains' grey.
- Coastlines are drawn with a fine pen. Groups of up to seven level wave strokes mark the
  bays and headlands, and now and then a long straight coast: each starts right at the shore and
  runs out to sea, long and short strokes in turn, and a headland pointing north or south may
  have a stroke or two off its tip. Islets outside the Mistlands that lie clear of the coast are
  gathered into clusters, and each cluster gets a small group of level strokes sized to it, long
  and short in turn, each starting at an islet's shore or lying in the open water beside it,
  never crossing land; larger islands get the coast's waves instead. The Mistlands' islet fields,
  the tiniest specks on their own and the open sea get no waves. Lakes get straight
  lines instead, stacked on consecutive rows from one shore, each touching the shore and running
  into the lake, at the lake's bends and along its straight shores, never across it, so still
  water reads apart from the sea; ponds stay bare. Ice floes, white as the northern snow, lie where the game sets its ice: along the
  Deep North's shores and on its side of the deep channel at its border, not out on the open
  northern sea; the Deep North's lakes are washed a lighter blue. Rivers and
  streams are drawn as ink lines, except across the Ashlands, where the game carves none, and
  streams that run down into the Deep North, which the game never carves. Grass tufts keep clear of the water at shores and riverbanks.
- A few sea monsters are inked out in wide stretches of open water, spread over the seas rather
  than crowded along the map's rim, as on old maps:
  sea serpents and serpent coils where the game's Serpents swim, the odd leviathan, and the
  bonemaw's bare bones in the Ashlands' boiling sea; the Deep North's icy waters stay empty. Each appears
  at most three times, spread over the map, some facing the other way and a little larger or
  smaller, its body washed iron grey above the water and a wedge of deeper blue in the water
  under it, trailing east like its shadow, so it stands out from the sea round it (the
  bonemaw's bare bones get the blue wedge alone). Where open water had room for a monster but
  none fitted, the sea may show a smaller sign instead, well apart from each other: a fish
  leaping from a short level wave in the open ocean, and a simmering spot in the Ashlands'
  boiling sea, bubbles washed grey rising from a short level wave, with a wave behind and one
  in front as if seen across the water; each has a little blue wedge under it too. Monsters and
  these signs are drawn only once you have explored all the sea round them, so they are never
  cut by the edge of the unexplored or seen half on bare parchment.
- The Ashlands' boiling sea takes a faint reddish-brown warmth, as on the game's own map, fading
  in where the game's does.
- "Read map" at a cartography table opens the game's own interactive map, where you can place and
  edit pins. Your pins appear on your map the next time you record your discoveries, drawn with
  the map's own pen: a campfire, a longhouse, Thor's hammer Mjolnir, a dot, the stone portal as a
  ring of stones standing on a low slab, two gravestones one behind the other where you died,
  and other icons traced in outline. The game's own marks are drawn as map signs too: your bed, your last
  death, a horned skull for a boss's altar, a barrow, a cave mouth and a tower for Hildir's
  requests, a bag for Haldor, a tent for Hildir's
  camp, a cauldron for the Bog Witch and a stone pillar crowned with a ball for the Forge of Potential; other mods'
  places are traced in outline. Pins are inked into the map itself in solid red, as places were
  marked on old maps, their details (a door, the gap between stones, the flame's tongue) left in bare paper,
  in its own wavering pen,
  each as large as what it stands for beside the trees (a campfire smaller than a tree, a
  longhouse about one, a tower, a cave or the Sacrificial Stones larger): standing on its place
  among the trees and peaks, which may cover part of it but never more than about half, filled in the
  colour of the ground round it, and sharp at every zoom; on checked pins the paint looks
  half scraped away, paler and scuffed, the icon still recognisable. Pins out in land you have
  not explored, and other mods' icons, stay markers over the parchment. When your pins change,
  the map is drawn again in the background. Point at a pin to see its name, written beside it in
  bold in the pin's red.
- The Sacrificial Stones are a pin like the others, drawn as a ring of standing stones round a
  low altar.
- Settings choose which of the game's own marks the map shows: your bed, your last death,
  boss altars, the traders, Hildir's dungeons and other places the game marks, the Sacrificial
  Stones among them. Two more hide your own pins and any pins out in land you have not
  explored. All are on by default.
- The map is drawn once per world in the background and kept, so opening it never stalls the game.
  Until it is ready the map key says "The ink hasn't dried yet."
- Zooming in redraws the part in view at that zoom from the world itself: coasts and wood edges
  follow the land to within a few metres, and woods fill with more, finer trees. Close-ups take
  over a little sooner as you zoom in, are ready shortly after you join rather than after you
  first open the map, and fill in faster. Close-ups are drawn
  ahead of time in small tiles around the pointer, so zooming in rarely waits.
- With the game's dev commands on (`devcommands` in the console), the map shows where you stand
  and which way you face, with the game's own map arrow, so the drawing can be checked against
  the world. It never shows in normal play.
