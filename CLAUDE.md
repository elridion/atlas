# Atlas

Valheim mod for worlds without a map (`Game.m_noMap`). "Record discoveries" at a cartography table
also records a snapshot (explored map and saved pins) on the character; the map key shows that
snapshot as an ink drawing on parchment in the style of a classic fantasy-novel map. "Read map"
opens vanilla's interactive map, where pins are placed. Client-side: nothing is saved in the world.
It replaces shudnal's Nomap Printer, whose printing lagged badly at the table (it re-baked and
re-uploaded an 8192² texture and threw away ~700 MB of arrays per print).

Stack, build, verification and git workflow are shared by all mods: see `../CLAUDE.md` and
`../.claude/rules/`.

## Where things are

- `src/AtlasPlugin.cs` binds the config (section `Pins`: one switch per kind of the game's own
  marks, read when the pins are built, `AtlasView.Shown`; `PlayerPins` for the player's own
  pins, `UnexploredPins` for pins outside the explored land; section `Fog`: `Glimpse`),
  loads translations, applies the patches and calls `AtlasMap.Update` every frame. Location
  pins (`PinType.None`) count as traders when drawn as Trader, Tent or Cauldron, otherwise as
  other places.
- `src/AtlasMap.cs` the controller: per world it loads or paints the ink sheet, lays the fog for the
  current snapshot, and opens `AtlasView` on the map key.
- `src/AtlasView.cs` the window (parchment, ink and fog `RawImage`s plus pin markers) with zoom and
  drag. Pins are drawn like the sheet's own glyphs: a pen line round a body with no terrain ink,
  filled in the wash of the biome under the pin (`Paper.PinBodyColor`: the biome's glaze on clean
  paper, as a peak's body takes the mountains', water's on water; biome and water from the
  sheet's `WorldSource`). A black icon on a parchment ring and a flat underlay behind names read
  as modern stickers on the hand-drawn map. Where a pin's place lies on its glyph
  (`PinGlyphs.StandOf`, `Lift`; the images and the name layout are lifted, the holder stays on
  the place) follows the map's oblique view: a glyph drawn in elevation is at the middle of its
  footprint, which lies behind its foot (ground line) by half its depth on the ground
  (`PlanHalfDepth`, pin units, from its drawn width and plan shape: round things as deep as
  wide, the longhouse 0.4, the bed half, the grave and Mjolnir, which stands on its head, a
  third) foreshortened by `GroundTilt` 0.42 (the Stones' ring's own squash), plus `StandBias`
  1.25 for every standing glyph: with no cast shadow the eye places a pin nearer its mass (the
  user's call on the Stones). No glyph has a raise of its own; the Stones fall out of the same
  rule (their altar is the footprint's middle). Emblems put their mass centre on the place: the
  dot, and the boss's horned skull (`BossCentre`, its measured ink centroid). Traced sprites stay
  centred. Glyphs still sort by their foot (`Sink`).
  `src/Ink/PinGlyphs.cs` draws the vanilla pin icons in pen, every glyph inked solid with its details cut back to paper (the user's call): fire: campfire, a
  two-tongued flame a line of paper in front of two long low crossed logs with square ends sawn a
  little off square (the user's design; round caps read as crossed bones), house: longhouse, a
  dragon hall seen side-on, a low wall under a big gabled roof whose ridge runs out at each end
  into a carved dragon head looking outward, the eave a line of paper and a paper door on the
  long side toward the east end (the user's design; a gable end read as a modern home button,
  hipped side views as a bungalow),
  the "hammer" as Mjolnir, a solid pendant silhouette after the Skane hammer, not a work hammer;
  circle: dot; portal: a ring of six solid stones sunk onto a thin stepped slab, open at the bottom where the
  opening meets the slab, every joint radial, the long top stone without a joint at the crown
  (the user's design); death: a grave of two round-topped steles on a plinth, the shorter one
  set forward east of the taller, one behind the other in the oblique view (the user's design),
  never a cross; checked: the ink scraped away in part (the user's design, `PinGlyphs.Abrade`,
  below), not struck through: a red strike vanishes on a solid glyph). The game's own pins get map signs too: bed (spawn point; a long low bed seen from the side with
  a sleeper under the blanket, pillow and body hump, one outline, the user's design), a
  solid horned skull in the language of the game's boss icon (Boss, from Vegvisirs), Hildir1-3
  as barrow (Smouldering Tombs), cave mouth (Howling Caverns) and tower (Sealed Towers); location
  pins by `LocationSpriteData.m_name`: `StartTemple` the Sacrificial Stones (below),
  vendor/Haldor a tied bag whose tie is a line of paper through the neck (`TraderTie`, through
  the overlap of the grown tuft and sack, so the tuft stands apart; a pen lower it read as a
  lidded jar), Hildir a round bell tent, its cone over a low wall a line of paper apart
  (the hem line), a swallowtail pennant flying from the apex itself with no pole
  (the user's design; a triangle tent read as an "A" or a peak), BogWitch a round-bellied cauldron (rim narrower than
  the belly, one line of paper under the rim, stubby feet, two big bubbles; the user's design),
  `AncientUpgradeStation` (the Forge of Potential) a ball on a tapering pillar with a stepped base and a paper V (the user's sketch),
  after the game's own icon, which follows the vanilla ward. These five are all the locations
  vanilla's minimap lists icons for (`Minimap.m_locationIcons` in SoftRef bundle `17245031`,
  names checked against the SoftRef manifest).
  Every line of paper cut through a pin (the gaps round parts inked in front, the portal's
  joints, the tie, the tower's slit, the barrow's lintel, the skull's teeth) has one width,
  `PinGlyphs.Cut` = `PaperCut` 0.5625 of the paper pen (the user's call, the tent's hem line):
  the pen is a sheet pixel in every box, so that is 0.675 sheet px on every pin and about
  1.9 px in 4x close-ups. Doors, eyes, the cave mouth and the Forge's V are shapes, not cuts.
  The portal's stones are inked touching and their joints scraped along the radii: as a gap
  between two outlines, each side wavered on its own and the joints came out half as wide.
  The campfire, longhouse, bag, tent, cauldron and bed are drawn in the hand (`PinGlyphs.Hand`,
  `HandInFront`, `HandGrown`, `HandCut` for paper cuts, the user's choice): corners rounded over
  `HandRound` of the outline pen (the tent's cloth softer, `TentRound`; the user's call)
  (`RoundCorners`, a tip stays a tip) and edges traced in uneven pieces nudged a hair off the
  line (`HandTrace`, salted per part), and no two edges ruled parallel, since exact outlines
  read as machine-made beside a hand-drawn map. Nudges over `HandNudge` read serrated in
  close-ups.
  **Pins are drawn into the sheet** (the user's design), at a fixed size like every other glyph:
  `AtlasMap.KeepPins` builds the list (`SheetPin`: place, glyph, checked) from the snapshot's
  pen-drawn pins the settings show (`AtlasView.InSheet`). `InkPainter.Paint` takes the list; each pin is a `Kind.Pin` glyph, part of the map
  (the user's design): sized for what it stands for against a tree of about 13 sheet pixels
  (`PinSheetSize`: the dot 10, a campfire, bed or grave 12, Mjolnir or the cauldron 13, a
  longhouse, tent, portal, Haldor's bag or the Forge about a tree at 16, a boss
  altar 18, a barrow 20, a cave or tower 22, the Sacrificial Stones 26), standing on its place by `PinGlyphs.Lift`. That
  size is levelled to the trees: close-ups draw pins smaller by their level of detail as they
  draw their trees (`InkCanvas.Pin` scales the box about the pin's foot by the canvas's pen
  scale, `FindMarks` sizes a tile's marks by `1 / lod`), so at the 4x close-ups (lod 1.41)
  pins keep their size beside the trees. Inked by `InkCanvas.Pin` in red (`Paper.PinInkColor`, a dulled vermilion, as places were rubricated
  on maps of the period; black pins in the terrain's ink were lost in woods and ranges and at
  the opening zoom), on a plane of its own (`InkCanvas.m_pinInk`, the sheet's sixth plane,
  scraped by glyphs drawn in front like black ink), with the map's own pen for inner lines and
  1.5 of it for the outline (`PinGlyphs.SheetOutline`, as a peak's flanks), passed to
  `PinGlyphs.Draw` in pin units. The pin's body takes no fill of its own: the ground's wash
  shows inside it (the user chose that over clean parchment and a thin red glaze). Trees behind
  a pin whose outline would show less than `HiddenShown` a quarter outside its body are left
  out (`DropHiddenBehindMarks`): scraped round the pin, their slivers read as stray marks.
  Meadow grass tufts over a pin's box that stand in front of it are inked again after the
  glyphs (`m_markGrass`, `DrawGrassOverMarks`, sheet and close-ups), since the ground fills are
  laid before any glyph is inked and the pin's body would otherwise scrape them away. Pins also have a
  hand-drawn waver (`PinGlyphs.Waver`: every
  point moved by a smooth noise seeded by the pin's place, straight lines and outlines cut
  into short pieces so they bend; `InkCanvas.m_warp`). Its body scrapes the ink and body masks
  so the ground's own wash shows round it. A checked pin (vanilla's `PinData.m_checked`, which
  the player toggles) looks as if someone tried to scrape its paint away (`PinGlyphs.Abrade`,
  called by `InkCanvas.Pin` when struck): the whole glyph keeps `AbradeRubbed` 0.72 of its ink,
  then one ragged swath along a scraping direction 20-40 degrees up to the east lifts it in
  streaks to `AbradeGhost` 0.3, the deepest grooves to `AbradeGroove` 0.06. Only the ink is
  lifted, never the body, so the silhouette still names the pin and the ground's wash shows
  through the grooves. Partial cover on the pin plane is a paler red over the wash, not grey,
  so the pen rule below does not apply: the rubbed-back glyph keeps checked pins paler at the
  opening zoom, where the streaks average out. The swath and streaks are in pin units and pens
  (streaks about a pen across, so at least a pixel on the sheet), through the pin's waver and
  seeded by its place, so sheet and close-ups scrape alike. Markers take the same look per
  glyph (`AtlasView.Textures` with `isChecked`: the body shared, the pen of a sheet pin of
  their size, a seed from the glyph's key). Recorded as `Op.Pin`, so
  close-ups redraw it sharp. Pins are sorted in with the trees and glyphs by their feet, so
  relief and trees in front may cover part of one, together up to `MarkCover` 45% of it (the
  user's rule). Cover is measured on `CoverSamples` 12 x 12 points of the pin's box, only those
  of its solid core (`CoverBody`: the `PinGlyphs` mark's body at 12 px, `PinBody`, eroded by
  one point, so thin parts such as the boss's horns and the gap round every line do not count;
  counted in, the horns let trees hide the whole skull; a glyph left with under
  `CoverCoreLeast` points keeps its whole body), each covered when a glyph in
  front draws over it (`Covers`: a round crown's scraped ellipse, a hill's dome, the triangle of
  a pine or other relief), counted once however many cover it. Box overlaps instead overstated
  a tree's cover so far that one tree used up the share and pins stood clear of the woods.
  Relief goes first and is never left out (`OrderMarks`: the relief drawn after a pin that
  overlaps it, north first, each adding the points it newly covers to `m_cover`;
  where the next would pass `MarkCover` the pin is drawn after it instead, `m_order`). Trees
  get the rest (`LimitMarkCover`: those drawn after the pin's order, north first, the rest
  left out). The record keeps each pin's order and relief cover (`m_pinOrders`,
  `m_pinCovers`), and a close-up scatters every tree that can reach a pin near it
  (`ScatterArea`, `PinInfluence` round each pin, the ground sampled that far too), so tiles
  decide alike. Any other glyph drawn after a pin whose footprint reaches its
  core (`MarkCore` 0.3 of its box in from each side) is left out (`ClearOfMarks` in `Add`, the
  rest removed after placement; cores filed in `PeakCell` squares). A broad clearing round
  every pin was too aggressive. Relief (peaks, hills, spires, sea thorns, `IsRelief`) is never
  left out, as a range must still read as one (the user's requirement); a pin drawn in front of
  a peak scrapes only what lies under its body.
  Close-ups take the list from `InkRecord.m_pins` and decide the same. The list's key (`SheetPin.Key`, order-free) is kept with the sheet in the cache;
  a different key paints the world again in the background (about 6 s offline), the old sheet
  showing until the new one is uploaded and the table sparkling meanwhile; the close-up tiles
  start over with the new record. The game's own marks are drawn into the sheet wherever they
  are (`AtlasView.InSheet`, `IsGameMark`); out in unexplored land the fog opens a glimpse round
  them (`Glimpsed`, `AtlasMap.s_glimpses`). The player's own pins in unexplored land and traced
  icons (other mods') stay markers in `AtlasView`, sized as the sheet's pins in sheet units
  (`InkPainter.PinSheetSize`), scaling with the map, not kept at a screen size.
  **Dev commands player marker** (a debugging aid, the user's request): the atlas otherwise
  never shows where the player stands. With `Terminal.m_cheat` set (vanilla's `devcommands`
  toggle, a public static bool; read live every frame, never cached, as it can be toggled with
  the atlas open) `AtlasView.UpdatePlayerMarker` shows a copy of vanilla's large map arrow
  (`Minimap.m_largeMarker`'s `Image`: sprite, colour and size) on the player, turned
  `-yaw` of the main camera as vanilla's `UpdatePlayerMarker` turns it (the player's own
  transform without a camera). It is a modern overlay, not part of the map: a child of the
  viewport over the whole sheet (dirt and trimmed edge included), under the hover name,
  placed at `m_position + place * m_scale` with `place` the marker mapping `SetPins` uses,
  so it keeps its screen size at every zoom. Made the first time it is needed after `Build`
  (`NewPlayerMarker`; no sprite logs a warning and leaves it off); per frame it only
  compares and sets position, rotation and active state, no allocation. A failure logs once
  and turns the marker off for the session (`s_playerMarkerFailed`), not the atlas.
  Untested in game.
  The glimpse (the user's design): `Paper.Fog` lays a band `GlimpseBand` 160 m out from the
  explored land and round every glimpsed mark (a distance field from the explored pixels and
  the marks, `BandDepth`), across which the drawing thins out and stops (below, "Where the
  drawing stops"), and writes a second texture, the glimpse, bare parchment whose opacity is the
  unexplored land softened at the explored edge. `AtlasView` lays it over the paper's wash and
  under the ink, so the band shows the map on untinted parchment; close-ups lay it over their
  wash before the glyphs' bodies (`DetailTile.Render`, `DetailTiles.SetGlimpse`, which drops
  every tile drawn with the old fog). In the glimpse the glyphs' bodies (crowns, peaks) and the
  sea illustrations' wash give way to the parchment and the terrain's ink turns greyer
  (`Paper.GlimpseInkColor`); pins keep their red (the user's design). The sheet bakes that into
  its ink texture (`Paper.InkWithMips` takes the glimpse), so `AtlasMap` keeps the sheet's planes
  for the session (`s_sheet`, about 100 MB) and lays the texture again whenever the fog lays a new
  glimpse (`KeepMips`, `s_glimpseSerial`); the first is laid only once the fog is there, so
  login does not lay it twice. A second texture split from the ink would cost as much in video
  memory, and no custom shader is possible without asset bundles. The always-on name
  layout is left off (`s_showNames`); instead the hovered pin's name shows beside it
  (`AtlasView.UpdateHover`: bold vanilla pin font in `Paper.PinInkColor` on a plain copy of
  its material with no outline, underlay or glow; the user's design). The preview takes `-Temple wx:wz` and
  `-Pin GLYPH:wx:wz[:checked]`, and `-PinCross` marks their places (below).
  The Sacrificial Stones are a pin like every other, with no special treatment anywhere (the
  user's call, made twice): the game's own location pin for `StartTemple` from the snapshot,
  shown by `PlacePins`, drawn into the sheet and glimpsed like Haldor or the Forge, inked solid
  red. Do not add them from `ZoneSystem` apart from the snapshot, keep them out of markers by a
  special case, or give them a fill of their own (they had the peaks' rock wash and outlined
  blocks): they read as a different, parchment-filled pin at low zoom. `PinGlyphs.StoneCircle`
  in a 26 px box: a ring of solid standing stones round a low altar in the map's oblique view,
  drawn back to front a line of paper apart. The ring's centre is on the location (the user's
  call: standing on the front of the ring put the ring north of it), `StandBias` over the altar
  (`StonesCentreY`) as every standing glyph stands over its footprint's middle, but the glyph is sorted by the front of the ring (`StonesFoot`, `PinGlyphs.Sink`: the
  `Glyph`'s `m_y` lies that far below the place, `PinBox` adds it back), so a tree between the
  altar and the front stones is drawn behind the ring, not over its front.
  The ground inside the ring is not cleared: only the stones scrape what lies behind them.
  Anything else is traced from its sprite and stays a marker (other mods' locations and
  pins: silhouette alpha over 200 as the body, a pen line on its edge, clearly darker inner
  parts inked); every body reaches `Gap` past the lines, the engraver's break that keeps trees
  off the outline: for pins drawn into the sheet half a pen past the ink (`SheetGap`), the
  halo a tree's crown scrapes round its own line, so pins and trees stand off each other alike.
  The game's own pins never lie in water (beds, altars, dungeons, traders, the Stones), so the
  catalogue shows them on land only. Vanilla `PinType` (from assembly_valheim): Icon0-4, Death, Bed, Shout, None,
  Boss, Player, RandomEvent, Ping, EventArea, Hildir1-3, Memorial. Its `AddPin` call sites:
  player pins, profile pins (bed and last death, `UpdateProfilePins`), location pins
  (`PinType.None`), events, players, pings and shouts (all passing), and `DiscoverLocation`
  (bosses, Hildir); nothing places `Memorial`. The snapshot keeps saved pins plus the unsaved
  ones that mark places: location pins (`m_locationPins`), `m_spawnPointPin` and `m_deathPin`,
  which a saved-only filter leaves off the atlas. Daymark's dungeon and deposit pins are
  ordinary Icon2 and Icon3 pins. `Preview --pincat` renders the glyph table
  (`screenshots/atlas-sample-28-pin-catalogue.png`); `Preview --pinref` shows where each
  glyph's place lies on it (`atlas-sample-29-pin-reference.png`, below). Names: bold (the regular weight was hard to read in game), letter-spacing, bare
  ink over a gap of their own letters grown a little (a TextMeshPro underlay used only as a
  `Mask`, paper behind it), placed by `PinLayout` at the clearest of the eight classic positions
  by the sheet's ink density (`InkDensity`, a summed-area table built before the sheet is
  uploaded; unexplored ground counts as clear), bosses and traders first, hidden when nothing
  fits, recomputed on a zoom change over 3%. Do not use a soft disc, an enlarged icon, red icons
  on labels or shifted copies (not one colour) to set names apart. A UI parent draws before its
  children, hence a holder per pin. The preview's `pins` views draw test pins with a stand-in
  font; traced sprites and the masks are only seen in game.
- `src/TablePatches.cs` the `MapTable.OnRead`/`OnWrite` postfixes and the `Minimap.IsOpen` postfix.
- `src/InteractiveMap.cs` opens vanilla's large map in a nomap world.
- `src/TableSparkles.cs` golden motes over the table while the atlas updates after "Record
  discoveries", bursting and fading once it is done (vanilla's scribble and chime play before
  the atlas has caught up). A local particle system with a mote texture made in code on a copy
  of `vfx_odin_despawn`'s particle material; untested in game.
- `src/Snapshot.cs` the recorded exploration and pins, in `Player.m_customData`.
- `src/Paper.cs` parchment, the biome wash, fog and the ink texture's pixel data (mip chain).
- `src/SheetEdge.cs` the parchment's outer edge, trimmed by hand (the user's design, chosen
  over a deckle and an out-of-square skin edge): a cut wandering a few pixels in and out
  (`Wander`, `Uneven`, `Fibre`), worn round corners of `CornerMin`-`CornerMax`, a few small
  rounded nicks (never a V, which reads as a tear), the vellum thinning over `Fringe` with a
  browned cut line, and a soft shadow south-east on the backdrop. The sheet's layers stay
  square: `AtlasView`'s `Edge`, the last child of the sheet (after `Dirt`), holds four strips
  (north and south the full width, west and east between, `Depth` in and `Pad` out, `Texels` 2
  a sheet pixel, mipmapped) in the backdrop's colour over what lies outside the cut, so paper,
  ink, glimpse, close-ups, fog, markers and dirt are cut alike and the edge moves and scales
  with the sheet. Their texture holds only the shadow's factor and the cover; `RawImage.color`
  is the frame's `s_border`, the flat colour behind the sheet, so they match it exactly (not
  `Alpha8`: its RGB samples as black). A UI `Mask` would cut with a hard 1-bit edge (the stock
  shader's alpha clip) and per-layer alpha would leave each layer's edge at its own resolution,
  so **the backdrop behind the sheet must stay one flat colour**; a textured backdrop needs
  another approach. The darker worn rim is a stain in the dirt (`AddRim`, from `Paper.Dirt`).
  The cut lies at most about 15 px in, keeping at least 40 px (about 210 m) of bare margin off
  the frame's ring where it comes nearest. Built once per world on the workers (own salt,
  `Salt`), uploaded once (`AtlasMap.KeepEdge`, about 12 MB with mips); a failed upload only
  leaves the square edge. Not cached, so `InkCache.Version` does not change with it. The
  preview's `trim:fit`, `trim:north` and `trim:corner` views show it over the backdrop.
- `src/Ink/`: `WorldSource.cs` vanilla minimap data, `Woods.cs` the wood field, `InkPainter.cs`
  the drawing pipeline and scatter rules, `Glyphs.cs` the
  pen glyphs, `InkCanvas.cs` the anti-aliased rasterizer, `InkRecord.cs` the recorded pen calls
  and their layers, `DistanceField.cs` distance transform and marching squares, `Noise.cs`
  hashes and value noise, `InkCache.cs` the per-world disk cache.
- Detail tiles: `DetailTiles.cs` the tile pyramid (what to draw, workers, textures),
  `DetailTile.cs` composites one, `InkPainter.Tile.cs` draws it, `TileGrid.cs` the ground sampled
  for it, `SampleCache.cs` the shared samples, `WorldSampler.cs` the world generator and minimap
  samplers.
- `assets/translations/<Language>.json` (`atlas_*` keys); `tools/make_icon.ps1` generates
  `package/icon.png`; `hexium.json` Hexium listing metadata.

## Invariants that must not break

- **Client-only.** `NetworkCompatibility(NotEnforced, VersionStrictness.None)` and Hexium
  `install_location` `client` are deliberate exceptions to the workspace rule: nothing other peers
  see changes. Adding networked state ends that.
- **Persisted identifiers.** Plugin GUID `elridion.atlas`; the custom data keys
  `elridion.atlas.explored.<worldUid>` (ZPackage: size, pixel size, gzip of the explored bits) and
  `elridion.atlas.pins.<worldUid>` (ZPackage: version 1, count, then per pin position, name, type,
  sprite name, checked) must stay readable once released.
- **Nothing heavy on the main thread.** The main thread only copies vanilla's three minimap textures
  once per world (`WorldSource.Capture`) and uploads finished pixels: the ink sheet once per world
  (`LoadRawTextureData`, made unreadable after), the washed paper once per world, the 2048² fog
  once per recording. Decoding the world, painting, the wash, the fog, parchment, mips, the
  cache and close-up tiles run on the atlas's own threads (`Workers`: half the cores, at most 4,
  below normal priority), never the shared ThreadPool, and `Parallel.For` passes
  `Workers.LoopOptions()`, so the atlas never takes every core from the game right after login.
  Work there never blocks on another atlas task (`.Result` or `Wait` on one not yet done): with
  one worker it would wait on a task queued behind itself; chain with `Workers.Then`. Worker
  code must not call Unity APIs except
  pure math (`Mathf`, `Vector2`); `Mathf.HalfToFloat` is native, hence `WorldSource.HalfToFloat`.
- **Deterministic drawing.** Glyph placement hashes the world seed, a per-rule salt and the grid
  cell (`Noise`), never a shared `System.Random`, so tuning one rule never moves another's glyphs.
  Any change to the drawing bumps `InkCache.Version`, or players keep the old cached sheet;
  it counts up from 1 (the first release's version). Only the exact version is read, so any
  other, the development sheets' 23 to 26 included, is drawn again.
- **Persisted config.** Section and key names (`Pins`, `PlayerPins`, `BedPin`, `DeathPin`,
  `BossPins`, `TraderPins`, `HildirPins`, `PlacePins`, `UnexploredPins`; `Fog`, `Glimpse`):
  renaming one resets players' settings.
- **Docs follow code.** `package/README.md` keeps the settings table in sync with
  `AtlasPlugin.Awake`, and the table behaviour, controls and pen mark tables
  in sync with `TablePatches`, `AtlasView` and the scatter rules in `InkPainter.PlaceGlyphs` and
  `DrawBackground` (and `DrawIllustrations`).

## The drawing

- Source data is vanilla's minimap (`m_mapTexture` biome colours, `m_heightTexture`,
  `m_forestMaskTexture`), 2048² at 12 m, read once `m_hasGenerated` is set. Biome colours come
  from the live `Minimap` fields (the prefab overrides the code defaults: meadows is 146,167,92 in
  game, mountain and Deep North come from `m_mountainColor` and `m_deepnorthColor`, the ocean is
  vanilla's hard-coded white). Biomes that share a colour (by default Ocean, Mountain and Deep
  North are all white) are told apart by height and `WorldGenerator.IsDeepnorth`.
- Changing worlds cancels a drawing still in progress (`AtlasMap.s_cancel`, checked between the
  painter's stages), so a quick relog does not run two paintings at once.
- The canvas is 4096² over ±10800 m (5.27 m per pixel), y running south; textures run north, so
  `Paper.InkWithMips` flips it. The frame is a double ring at vanilla's world edge (10500 m).
- **Atlas is a navigation tool**, meant to be as accurate as vanilla's map but only as current as
  the last table visit. Accuracy wins over looks.
- The coastline is traced (marching squares) on vanilla's own heights, unsmoothed, keeping every
  island and lake; only single 12 m pixels are dropped, and swamp pools are raised to land before
  (`SwampFloor`, below). Water joined to the
  world's edge (`MarkSea`, four-way flood fill) is the sea and gets the water lines, as do wide
  lakes; every shore, sea or inland, has the same pen (a thinner lake shore reads as unexplained
  inland lines; the water's blue tells lakes apart). The coast pen is 1.4 px on the sheet, barely
  heavier than the waves as on the classic map; rivers are 1.2. Close-ups draw the coast and
  waves with the same `Pen` as their trees, under the tile's pen scale: thinner screen-pixel
  pens for them read as a lighter hand than the glyphs beside them. Water is one even indigo wash (`Paper.WaterColor`; shading by depth is too exact for a
  hand-tinted map). Rivers and the coast are drawn before the glyphs, so crowns over a bank or
  shore hide the line instead of being cut by it, and trees keep off river courses
  (`m_riverMask` on the sheet, `MarkTileRivers` from the record in close-ups) so streams still
  show; land tints are blurred among land only, and the water's edge is anti-aliased over one
  pixel by the ground's local slope, so the blue ends at the water line (a fixed height ramp
  spills it far inland over flat beaches). Streams get an 8 m band on the sheet
  (`Paper.Rivers`); rivers show by their heights, since the generator's river width includes the
  banks. Close-ups lay their own water from their fine heights over the land-only wash
  (`Paper.Wash.m_land`) and need no stream band.
- The palette (paper, ink, biome washes, water) follows pigments of the period: aged calfskin
  paper, aged iron-gall ink, and thin glazes of green earth, verdigris with indigo, yellow and
  red ochre, folium, chalk and soot, and indigo, all muted earth tones. Values are estimates from
  Raman/XRF pigment studies of Insular and Nordic manuscripts, not measured colours. Do not blur
  the coast or delete small islands and lakes for looks: accuracy comes first, so swamps are
  busy by nature. The offline harness can overlay vanilla's own 12 m water boundary in red to
  check the coast. The water lines follow the 1980 redrawn Middle-earth map (the 1954 fold-out
  has no wave strokes, only doubled coasts and a blank sea).
- **No waterline is drawn**, but `TraceWaterLines` still traces one, because the waves find the
  coast's bends on that trace. It is traced `WaterLine` out on the distance from land and from
  `m_noLine`, the water that carries no line: channels (water over `ChannelReach` from any water
  `WideWater` from land, straits and inlets under about 115 m), ponds and small lakes. So at a
  strait's mouth it turns across the water instead of stopping. Islets under `SmallIsletArea`
  (150) count as water (`LineLand`); much lower, the trace wraps round the Mistlands' islet
  fringes, so their bends fall on islets and the main coast stays bare.
- **Sea waves** (`DrawWaves`) come in groups at the coast's pivotal points.
  Accents (`FindAccents`) are the trace's sharpest bends: turn over `AccentTurn` across
  `AccentWindow` either side, i.e. bay heads and headland tips (`BendAccent` tells them apart by
  how the point bulges from the line round it). They are taken strongest first, `AccentSpacing`
  apart, and then on a straight stretch of about `AccentPlain` one with `AccentPlainOdds`, only
  where it faces east or west. Land masses under `MajorLandCells` (250 map cells) get no accent
  (`Accentable`) and are no shore (`FindShore`); they are islets with groups of their own
  (below). Do not draw the coast's strokes as if islets were absent and cut them where an islet
  stands, at the coast or under islet clusters. A group (`DrawGroup`) has `WaveGroupMin` to
  `WaveGroupMax` strokes on rows ruled `WaveRowPitch` apart, strictly level, long
  (`WaveLongCrests`, a crest more by seed) and short (`WaveShortCrests`) in turn by row. A lake's
  or islet cluster's group has 3 to 7 strokes: its strokes wait in `m_group` and a group with
  room for fewer than its hand's `m_floor` (`GroupFloor` 3) is left out (`EndGroup`). The sea's
  hand has floor 0, so sea groups may draw one or two strokes where room is short (a floor of 3
  drops about one sea group in ten).
  - Islets outside the Mistlands (`DrawIsletWaves`) are clustered and each cluster gets one
    group round it. Islets are the land masses under `MajorLandCells` found in `MajorLand`, with
    their cells' centroid and bounds (`m_isletCell` maps each cell to its islet); one mostly in
    the Mistlands is left out, as its islet fields are dense and stay blank.
    **Not an islet**: masses of `MajorLandCells` 250 and up (about 36,000 m², some 190 m across),
    such as those in channels off the main coast, read as part of the coast and get the coast's
    groups at their bends. An islet any of whose own pixels lies within `IsletCoastGap` 12 px
    (about 63 m, a coast stroke's reach) of such land sits in the coast's own waves and is left
    out of the clusters (`NearCoast`); it gets no group but still bounds every stroke. Its bounds
    alone are not enough: their corners take in the edge of headlands beside them.
    `ClusterIslets` links islets whose bounds come within `IsletLink` 16 px (84 m; the ocean
    sample's islets lie 3-25 px apart) by single linkage, and splits a cluster wider or taller
    than `IsletClusterSpan` 48 px by linking it again at half the distance (`SplitCluster`,
    down to `IsletLinkMin`); unsplit, islet fields chain into clusters up to 180 px across.
    A cluster needs an islet of `WavedIsletCells` 12 map cells (the start's outer islets are
    13-22, the specks round them 2-6); specks only join one. Its centre is the centroid of its
    cells. `PlaceClusterGroup` sizes the group to the cluster: its size is the side of a square
    of its cells (8 px for 12 cells, 26 for 134, 36 for 249); `GroupFloor` 3 strokes up to
    `IsletSizeBase` 12 px and one more every `IsletSizeStep` 10, `IsletStrokesMax` 5 at most;
    long rows `IsletShortCrests` 3 plus one every `IsletSizeLong` 12 px (4 to `IsletLongCrests`
    6) and short rows two crests shorter, never under 3, so rows alternate long and short as the
    coast's do. No stroke crosses or touches land (strokes drawn through islets with the islets
    on top show through the land). Rows are the coast's ruled rows, from the centroid's row out,
    below and above in turn, from a row over the cluster's top to one under its bottom, until
    the budget is spent (`IsletRow`). A row through the cluster's land gets one stroke off its
    east or west shore, the side swapping row by row (the group's first side by seed), so the
    rows' centres sit left and right of the islet in turn round one centre; strokes off both
    sides of a row read as one line under the islet. It starts as a coast stroke does,
    `WaveShoreGap` off the shore, and runs out until it would come within `WaveShoreGap` of any
    land (round, so above and below its row too) less `IsletChannelGap` 10 px (`IsletRoom`), so
    channels between islets stay open (at 5 px strokes fill a 13 px channel); if that side has
    no room the other is tried. A row that crosses under `IsletTipWidth` 3 px of land, or runs
    along a spike (no land a pixel above or below just inside the shore), takes no stroke: it
    would run out of a point. A row clear of the land gets one stroke in open water centred on
    the cluster, swayed `IsletSway` 0.9 crest (0.5-1.3 of it by seed) east or west in turn and,
    on a chain whose cells spread `IsletElongation` times as far one way as across and that
    slants more than `IsletLevelChain` (25 degrees), shifted along it by its distance from the
    centre (at most `IsletLean` px a px and half the cluster's width; a near-level chain is not
    leaned); it keeps `WaveFloatClear` off land and `IsletChannelGap` of water beyond either end
    (`OpenRow`). Strokes stay level, never tilted, and claim their rows (`Claim`), so groups
    keep off each other and the coast's. Strokes off a shore are recorded with `IsletAnchor` (2)
    times their direction: close-ups move them to their own shore as coast strokes and keep
    `TileIsletBand` 1 px above and below the row clear of land too (`TileRoom`). Clusters go
    largest first, each `IsletSpacing` 40 px from the coast's accents that drew a group (spacing
    from every accent leaves many clusters bare). Small groups centred under and over each islet
    read as isolated stacks, and groups at the islets' bends miss most islets.
  - On a coast facing east or west each stroke starts at the coast `WaveShoreGap` off it
    (`Anchored`, shore searched `AccentReach` round the accent) and runs out to sea.
  - A headland pointing north or south (normal `NorthSouth` times steeper than level) gets one
    or `WaveFloating` strokes floating off its tip (`Floating`, `WaveFloatClear` from land) and
    the rest off its flanks.
  - A bay opening north or south gets its strokes across it from either shore in turn.
  - `Room` stops a stroke `WaveSlotGap` short of land, sea thorns or the frame (not floes, which are drawn over the waves), dropping crests to fit,
    never under two; `Claim` keeps strokes in a row apart.
- Each wave is also kept as an `InkRecord.Wave` (position, length, crests, phase, lift and
  anchor; every stroke is level, so no direction is kept).
  Close-ups do not replay the sheet's strokes but redraw them (`DrawTileWaves`) in the close-up's
  pen. An anchored stroke is moved to the close-up's own coast (`TileShore`,
  within `TileWaveSearch`, `TileWaveGapPens` clear) and its crests cut to the water ahead;
  replayed strokes would stand a few sheet pixels off the finer coast. Anchors outside the
  tile's sampled ground stay put.
- **Lakes** get straight hairlines instead of waves, distinct from the sea's, in two sizes
  (`m_lakeTier`, `MarkWideLakes`): large lakes, middle at least `LakeWaveWidth` (11 px, about
  58 m) from the shore, with the lake `WaveHand` (lift 0, `LakeDash` units, `LakeLongDashes`
  6/`LakeShortDashes` 3, `LakeGroupMin`-`LakeGroupMax` lines), and small lakes from
  `LakeMinWidth` (8 px, about 42 m, so a 9 px lake such as the plains lake near -2900, 2436 gets
  lines) with the stepped-down hand (`SmallLakeDash`, `SmallLakeLongDashes` 5/
  `SmallLakeShortDashes` 3, `StepGroupMin`-`StepGroupMax`). `DrawLakeLines` (per size) draws
  groups at the bends of the lake's shore (`FindBends`, sharpest first), then on its straight
  shores facing east or west about every `LakePlain` 60 px along the trace (so a long arm with
  no bend is not bare), retrying `LakeRetry` further on where a spot takes no group; only a
  drawn group keeps the next `AccentSpacing` away. A group (`PlaceLakeGroup`) is one stack on
  consecutive rows from one shore: the first line's shore within `LakeFirstReach` of the accent,
  each next row's within `LakeDrift` of the row beside it, out from the middle until a row has
  no line; both shores and the accent's row and the rows either side are tried and the stack
  with the most line is drawn. Each line starts at the shore and touches it, `LakeOverlap` into
  the coast's pen, close-ups moving it onto their own coast with no gap. A line reaches at most
  `LakeReach` of the way to the far shore and never has fewer than `LakeLeastDashes` 3
  (`WaveHand.m_least`); two-dash lines read as stray hairlines. A lake with no group drawn gets
  one off its west or east shore level with its middle. Lake shores get no sea groups, nor one
  lake size's lines the other's shore. A small lake has no water `WideWater` from land, so the
  channel rule would count all of it as `m_noLine`, with no trace and no bends; small lakes are
  exempt from that rule (`TraceWaterLines`). Do not give lakes dense ripple rows in the middle,
  bare water, a small sea-style group, an inner line round the shore, or groups from either
  shore in turn. Water under `LakeMinWidth` (ponds, chains of forest ponds, swamp-side pools)
  stays bare.
- **Sea illustrations** (`SeaGlyphs`, placed by `DrawIllustrations` in `InkPainter.Sea.cs`):
  the sea serpent, the serpent coil and the leviathan in the open ocean (the Ocean biome, where
  the Serpent swims, the open northern sea included; the leviathan is decoration, not at real
  positions), the bonemaw serpent only in the Ashlands' sea (water of the Ashlands biome:
  vanilla's `GetBiome` gives the Ashlands before the ocean, the Deep North after it). The Deep
  North's own water (`WorldSource.m_iceWater`, below; the minimap's shared white decodes it as
  Ocean) stays blank but for its floes. `IsAshlands` is not called: it runs `WorldGenerator`'s static constructor, which
  the offline harness cannot load. Each glyph at most `IllustrationsEach` 3 times. Candidates
  are the peaks of the open water: the distance from any land, islets included, and from the
  frame's inner ring, on `OpenCell` 4 px cells, with no farther cell within two; at least
  `OpenWaterMin` 60 px (about 315 m; the user's choice over 90 and 75), ranked by that distance
  times a seed-set factor 0.85-1.15. They are taken in farthest-point order (the user's choice,
  `NextFigureSpot`): the best-ranked first, then each time the remaining spot with the highest
  min(distance to the nearest figure placed, `FrameNeighbour` 2 x its distance to the frame's
  inner ring) x its seed factor x sqrt(open water / `OpenWaterMin`), ties to the lower cell
  index. Ranked by open water alone the figures crowded the outer ocean and the Ashlands' rim
  (about 56% within 2 km of the frame on Velen) and left interior basins bare; without the
  frame term the order runs straight to the rim, and the square root keeps a region's figure
  in its roomier pocket. Figures now sit in somewhat narrower water (mean open water about
  97 px, was 117) and a view shows two of a kind a little more often (about a quarter of
  opening views, was about a sixth). In that
  order a spot takes the illustration of its sea placed fewest times so far (ties from a
  seed-set first), skipping any kind already within `SameKindSpacing` 2000 px (about 10.5 km;
  `KindNear`: two copies of one figure in the opening view read as stamped) and skipping the
  spot when every kind of its sea is near, if its box (measured by drawing the glyph on a
  scratch canvas) sits `IllustrationClear` 40 px past its half diagonal inside the open water,
  all four corners in the same sea, no ink at all within `IllustrationInkGap` 8 px (waves,
  floes) and `IllustrationSpacing` 600 px (about 3.2 km) from every other one; a world with
  fewer such spots gets fewer (8-9 on the local worlds). Below 60 px `OpenWaterMin` changes
  nothing: the clearance (66-73 px) and the kind rule cap the count. Scale 0.85-1.15 and mirroring by hash. `SeaGlyphs.Draw` mirrors and scales
  the points itself (`Hand`), scrapes scaled with the shape, pens kept at their width so none
  falls under `Pen`; the leviathan is not mirrored but lays its profile, barnacles and tufts the
  other way round, since its hatched east flank is the map's shade. Drawn after the floes on
  the `Illustration` layer, one record group each, which close-ups replay whole after their
  floes.
  **Stand-ins** (the user's design): every spot a figure was considered for and ruled out,
  whatever the reason (spacing, the kind rule, the count cap, the box's clearance, a corner in
  another sea, ink near), is a candidate, sorted back into rank order (not the figures'
  farthest-point order, so they keep their own spread; `PlaceStandIns`). One in the Ashlands'
  sea takes boiling water (`SeaGlyphs.Boil`), one in the open ocean a leaping fish
  (`SeaGlyphs.Fish`); the Deep North's own water none, and lakes are never candidates (their own hand
  of straight hairlines, a navigation map keeps inland water plain). A stand-in needs its own
  clear water, the figure rule scaled to a mark about 20 px across: open water from land and
  islets of its box's half diagonal plus `StandInOpenClear` 24 px and never under
  `StandInOpenMin` 40 px, all four box corners in the same sea, the box inside the frame
  (`InsideRing`), no ink within `StandInClear` 16 px (coast, waves, islets, figures, other
  stand-ins), `StandInFigureGap` 150 px from every figure's spot and `StandInSpacing` 400 px
  (about 2.1 km) from every other stand-in. Every candidate already has `OpenWaterMin`, so the
  ink clearance and the spacings bind. Velen gets 9 figures, 10 boils and 28 fish. Boils come
  only so: there is no scatter of its own over the hot sea. Each stand-in is a `SeaMark` (kinds 4
  and 5, `FigureKinds` 4 marks where the figures end) with its fixed box
  (`SeaGlyphs.StandInBox`), one Illustration group with its spot's y as foot, so the fog rule
  below applies unchanged. Its shape comes from a hash of its sheet point (`StandInSeed`, salt
  64), so `SeaWash` and close-ups draw it again from the mark alone; the cache's header format is
  unchanged. Two stand-ins on one `OpenCell` row share a foot, so `FogEdge.HidesSeaGroup` also
  checks that the hidden mark's box reaches the close-up.
  A leaping fish is drawn in the boil mark's water language (`LeapingFish`): a back line broken
  by the fish, an unbroken front line, a shorter lower line, all strictly level and `Pen`, a
  small solid spindle about 7 px long with a short forked tail (strokes along its middle, no
  seams), splash drops where it left the water. Variants a third each by seed: arched on the
  way down, two in tandem (the second smaller and still rising), diving back in head first.
  Caught level at the top of the arc with a big fork tail it read as a swallow; solid and short
  it does not read as the serpent's tube-like coils.
  The fog: a sea mark (figure or stand-in) is drawn only when it lies wholly in the coloured,
  explored part of the map (the user's rule): its box and `SeaMarkMargin` 70 m round it all
  explored (`Paper.SeaMarkSeen`, sampled every `SeaMarkStep` 6 m), past the glimpse's softening
  of the explored edge (about 21 m) and its wash's reach (the wedge, up to 8 px or 42 m under a
  figure's waterline), so neither the glimpse's parchment nor the
  fog lies over any of it, Glimpse on or off. Any other is left out whole: `Paper.Fog` lists it
  in `FogEdge.m_hiddenSea` (its sheet box and its record group's foot), `InkWithMips` clears its
  box from the ink (nothing else is inked within `IllustrationInkGap` of it) and lays no wash,
  and close-ups skip its group (`HidesSeaGroup`) and wash. The water round it stays as the fog
  lays it. Do not show a mark across the band or lift the fog or glimpse over it: shown at half
  explored, a serpent's head stood on bare parchment or under the fog, and laying fog over a
  hidden one's explored part bit into explored sea. Its box is kept in `InkRecord.m_seaMarks`
  and in the cache's header. `AtlasMap` opens the atlas only once the fog was laid with the
  uploaded sheet's marks (`s_fogMarks`).
  **Where the drawing stops** (the user's design, chosen over a torn-paper edge, a plain fade, a
  crisp line and a pencilled coast): both sides of the edge are the same parchment, so the
  edge reads as the cartographer drawing less and less where they knew less and then stopping,
  as on maps of the period where a coast simply ends in blank paper. No tear, rim or shadow (a
  tear is damage to the sheet; a white rim and an ink-brown shadow read as chalk and pencil, the
  user's call); a soft fade reads as a game's fog of war. `Paper.Fog` works on the band's depth
  (`BandDepth`: 0 on explored land, 1 at the band's end), blurred twice and moved in and out by a
  slow fBm (`EdgeGrain` 16 fog pixels, `EdgeWobble` 3 either way: bays and tongues, no fibres).
  The lines (coast, rivers, ground marks) fade over the band's last `EdgeFade` 0.12 and half
  that past it; explored pixels are never covered. From `ThinFrom` 0.3 of the band on, trees,
  dead trees and wave strokes standing beyond the explored land are left out one by one, more
  towards the end (`FogEdge.m_thin`, against a hash of the mark's foot, salt `ThinSalt`), and
  every one the fog would cut is left out whole (`CutByFog`), so woods end in a few outlying
  trees and the sea's strokes give out. Relief (peaks, hills, spires, sea thorns) is never
  thinned (holes in a range read as damage) but shown or left out whole (`Decide`, the user's
  design): one the band's end would cut is drawn whole when its foot stands on the drawn side
  or it reaches back into explored land (`MostlyBeyond`), the fog lifted over its rough shape
  (`LiftFog`; a mountain seen from afar is sketched whole, so it may show its own height past the
  band), else left out. Trees are judged after the lifts, against the fog as laid, or those in
  front of a lifted peak showed in its opening. The marks are `SheetMarks` (`InkRecord.m_marks`,
  also in the disk cache): every tree, every other glyph but pins and mist with its record group,
  every wave stroke, each with its footprint, drawing order and rough shape (`MarkShape`, from
  `InkPainter.FogShape`: a crown's ellipse, a pine's or peak's triangle, a hill's dome, else the
  box). `Paper.Fog` returns the decisions as a `FogEdge` (per mark, plus the fog's opacity and
  the thinning as planes). `InkWithMips` clears the marks left out from the sheet's ink and
  bodies at the sheet's own resolution (`HiddenInk`), except where a kept mark drawn after one
  (south of it) lies over it, so the trees in front keep their crowns; the pins' plane is never
  cleared. A mark behind a hidden one stays without the part the hidden one scraped when the
  sheet was painted. Laid as fog-resolution parchment patches instead, they nicked neighbouring
  peaks. Close-ups leave the marks out exactly (`DetailTiles.SetGlimpse` takes the `FogEdge`):
  their own trees by `CutByFog` on the planes, the sheet's glyph groups and wave strokes by
  index (`FogEdge.HiddenGroups`, `InkRecord.Wave.m_index`), only for a record drawn with the
  same pins (`FogEdge.Matches`, `SheetMarks.m_key`). With the `Glimpse` setting off the band
  ends on the explored land's own edge: the fade starts on the explored pixels and the fog closes
  about 30 m past them, and the glimpse takes the fog's opacity before the lifts, so no wash
  shows round a lifted peak. About 0.8 s offline for the fog with Velen's 25,600 trees, 2,800
  glyphs and 4,400 wave strokes, and about 0.5 s more for the sheet's ink. The fog is 2048²
  (10.5 m a pixel), so close in its edge softens over a pixel; a finer fog would cost a four
  times bigger upload per recording.
  Each mark has two washes (`SeaWash`, the user's "take 3"), so it stands out from the sea: a
  wedge of deeper blue in the water under its waterline, its shadow trailing east (the map's
  shade side), and an iron grey on a figure's body above the water. `SeaWash` draws the glyph
  again from its mark (kind, sheet point, mirror and scale, also kept in the cache's header) at
  2x on a scratch canvas that keeps every erase's coverage (`InkCanvas.m_scraped`). The body is
  that scraped coverage cut at the waterline (`m_y`), so it follows the body, not the box,
  ripples or foam. The wedge is one triangle across the columns the glyph scrapes within
  `BreakBand` 2 px above the waterline (where it breaks the surface), padded `WedgePad`, its top
  on the waterline, `WedgeDepth` 0.25 of its width deep (5-8 px), its tip moved `WedgeEast` 0.3
  of the width east, fading to `WedgeTip` 0.15; shallower or centred it read as a stain. The
  bonemaw is bare bone and gets the wedge only; the leaping fish too. Boiling water gets its
  wedge and the grey filling its bubbles' insides (the user's call; `BoilBubbleEllipses`, each
  bubble to its ring's middle, the ring's ink over the edge). A stand-in's wedge is built from
  its front water line (`StandInWater`, about 0.3 of the line either side of its centre),
  `StandInGrow` 1.3 times as wide and deep and raised `StandInLift` 1.2 px (`BuildStandIn`), so
  it shows under the small marks. Both washes are box-blurred (about 0.7 sheet px), mottled and
  faded at their area's edge, the body with `Gain`, within `Reach` 10 px of the box, inside the
  explored margin a shown mark needs. Glazes `Paper.s_seaBody` (iron grey 92, 92, 96 at 0.36;
  neutral to cool, so over the indigo it reads blue-grey, never land's green or ochre or a pin's
  red; a glaze can only darken, so no paler-than-sea body) and `Paper.s_seaWedge` (72, 100, 160
  at 0.30), the wedge over the body: close-ups glaze them over their water
  (`Paper.SeaMarkGlaze`, from `InkRecord.m_seaWashes`); the sheet's ink texture cannot glaze, so
  `InkWithMips` lays each as the colour and opacity that give the same glaze over clean water
  (`SeaInk`), body then wedge, premultiplied, under the ink. On stained paper sheet and
  close-ups differ by a level or two. On the Ashlands' warm water the wedge reads violet-blue.
  The ink sheet itself is unchanged.
- **The Ashlands' boiling sea** is warmed as vanilla's map warms it: vanilla's mask keeps its
  ocean gradient in blue under water (0 at the biome's edge, 1 far out), so
  `WorldSource.BuildAshSea` takes it on Ashlands and Ocean water only (blue in a swamp or forest
  pool would leave red specks), averaged over water and blurred `AshSeaBlur` passes so the shore
  takes the water's heat, not a lava field's (`m_ashSea`). `Paper.WaterColor` with a world point
  glazes `s_ashSea` (burnt red ochre, 0.26) over the water by that heat, on the sheet and in
  close-ups alike, so it fades in with vanilla's gradient and no seam shows at the biome's edge.
  Boiling water (`SeaGlyphs.BoilMark`, the user's design) stands there only as a stand-in
  (above): bubbles rising from a continuous front wave line, with three strictly level lines of
  one wave seen in perspective (`BoilPiece`: crests fixed on the sheet, `BoilWave` long and
  `BoilLift` high, each row `BoilTrainShift` of a wave on from the one behind). Two variants,
  half each by seed (the user's pick of four): a column of three bubbles shrinking and swaying
  as they rise (a speck over most), and a big bubble with two small ones rising either side
  and a speck over the middle (front line `BoilClusterWidth` 1.08 wider); two side by side read
  weaker and a bursting bubble read as a "U". The back row is drawn at `BoilScaleBack` 0.8,
  broken by every bubble it would cross with a long and a short piece; the front, unbroken and
  15-18 px wide, runs under the big bubble at scale 1; the lower, about half as wide, at
  `BoilScaleLow` 1.2. Wavelength, lift and length scale together and the gaps between rows are
  foreshortened alike (`BoilBackRise` 3.6, `BoilLowDrop` 4.4). Every line is `Pen`. Lone wave
  strokes far out read as birds; under the bubbles they read as boiling water. Its bubbles are
  washed grey inside and it has the stand-ins' blue wedge (above). Scattered on a grid of its
  own over the hot sea (the old rule) it read as a pattern; the user asked for very sparse.
- Ring lines at fixed distances, short double curves in staggered rows, tildes tilted with the
  coast, tildes at scored points of the shore with a scatter at sea, and a band of short tildes
  along every coast all read badly; keep groups at the bends.
- **One mountain, one peak** (`InkPainter.Massifs.cs`, the user's design): in game a Mountain
  patch is mostly one broad massif whose rough top carries a bump every 100 m or so, and a peak
  on every local summit (highest within 72 m) plus a dense filler of peaks broke it into a
  field of them. Each massif, a connected patch of Mountain land above water (grouped over all
  its land: grouped by the ground over `PeakHeight` alone it fell apart into many small
  massifs), gets one dominant peak on its highest point, `MassifPeakShare` 0.6 of its span
  (the side of a square of its area), `MassifPeakMin` 16 to `MassifPeakMax` 56 G, its seed
  drawn again until its shape is at least `MassifPeakTall` of the tallest, so the top never
  comes out broad and low. Another summit gets a peak only when it is clearly one (the user's
  call: a mountain with several clear summits gets several peaks): `SecondProminence` 35 m of
  true prominence (its height over the saddle joining it to higher ground, by union-find over
  the massif's pixels highest first) and `SecondIsolation` 400 m from every peak already on
  its massif, 22 to 34 G by prominence and at most `SecondShare` 0.75 of the top's width
  (about 5 on Velen; 60 m gave one in the world). Every summit's peak is placed so the summit
  lies `SummitAnchor` 0.4 of its height up its centre line, not at its apex: the glyphs are
  drawn in elevation on a plan, and with the apex on the summit the whole body hangs south of
  it, over the next biome or the coast; the top rising over the land behind reads naturally,
  as glyphs in front hide those behind. The rest of the high ground over `PeakHeight` carries
  **ridges** (`Kind.Ridge`, `InkPainter.Ridge`, the user's design "D" of two design rounds):
  stretches of mountain wall with no summit of their own, a near level jagged crest, a gentle
  west slope and a steep east end, hachures hanging down the front, no solid wedge (the
  peak's mark of a summit), varied by seed (slope length, crest tilt and jag rhythm, a saddle,
  a sheer or ledged east end, hachure spacing and reach). They stand on staggered rows
  (`RidgeStepX` 30 by `RidgeStepY` 12 G, jitter 0.6, `RidgeOdds` 0.9), `RidgeMin` 26 to
  `RidgeMax` 40 G wide by height, at most `RidgeShare` 0.85 of their massif's peak, moved
  `RidgePull` 0.12 of the way towards the massif's middle (the user's call: it closes the
  range up), and never hide more than a summit peak's lower `RidgeShown` 0.3 from in front
  or rise over its apex from behind, along their whole length (`RidgeHidesSummit`). Ridges are
  relief like peaks (never thinned or left out for pins, trees off their bases, crags off
  them). Placed on the sheet only; close-ups replay them. The Deep North keeps its own summit
  rule (`PlaceSummitsAndHills`) and the Mountains' hills below `PeakHeight` are unchanged. The
  earlier dense filler (16 by 9 G rows of peaks, "one dark mass") is gone: with fewer, lower
  bodies more of the stained ground's wash shows between them, so massifs read warmer.
  Hills sit on real hilltops found the same way (`HilltopCells` 4, `HillProminence` 6 m, one rule
  for all hill country, the user's choice: the game builds Meadows and Plains heights with the same
  code and their open land has the same relief, so the Meadows' finer search, radius 2 and 4 m,
  was dropped and they show fewer, larger rises), their foot at least half their
  width from woods (water is tested on the whole shape instead). Every drawn part of a hill
  (both domes' outlines and insides, the hatch dashes) keeps `HillWaterGap` off water and river
  courses (`ProbeHill`, `HillDryAt`; the probe shares `DomeOutline`/`DomeDash` with the
  drawing). A hill that would come closer first drops its domes behind (`Glyph.m_lone`), then
  shrinks by `HillShrinkStep` down to `HillMinScale`, crown kept on the hilltop (`FitHill`,
  `HillFoot`): on a navigation map a smaller hill on its real top beats a full-size one beside
  it. Failing that, after every hill that fits is placed, `ShiftHill` moves its hilltop as the
  Ashlands glyphs are moved off lava (the shared `ShiftFoot`: rings of `HillShiftStep` of its
  width out to `HillShiftReach`, away from the water its full shape touches first, then
  `ShiftAngles` either side), onto land of its biome no more than `HillShiftDrop` of its
  prominence below the top, foot off woods and `HillGap` clear of every hill and summit peak
  placed (`m_hillBoxes`), at every size `FitHill` tries.
- Glyphs are scattered on jittered grids per rule, sorted by their foot (north first) and drawn so
  that each scrapes its outline back to parchment before inking: southern glyphs hide northern ones.
- Woods are packed so densely that only their southern edge shows trunks, as on the classic map.
- Swamps and hills follow the classic map: the swamp fill is short flat marsh lines with
  seed-varied reed tufts on many, on a square, fully jittered grid, gathered into patches by a
  slow noise (`MarshPatch`) with thinner bog between (still 30% of cells; a near-empty bog reads
  too sparse, even rows over the whole swamp read as messy and ruled); hills are low lopsided
  domes with hatch dashes under the eastern side, often with a smaller dome behind.
  Marks are sized to their grid cell (reeds of two to four blades, `Reeds` taking the cell) and
  a mark that would touch an inked neighbour of higher priority is left out (`MarshMark`, from
  hashes and the patch noise alone, so the sheet and every tile agree); marks bigger than their
  cell with a jitter of nearly a cell pile into knots. Marks keep off woods (`IsOpenLand`).
  Clumps follow the real water: `WorldSource.m_swampWet` (1 at `SwampShallow` 1.5 m below the
  water, 0 at `SwampDry` 1 m above it, from the heights before the raise, blurred; the marsh
  surface tops out about 4 m up and two thirds of it lie under water, so wetness from the water
  line down was 1 over most of the swamp) makes 65% of the patch value (`wet * 1.4`) and the
  noise 35%; reeds favour the pools' rims; marks thin over the swamp's outermost few dozen
  metres (`m_swampField`, the swamp blurred, edge at 0.5); the wash glazes wet ground and raised
  pools toward the water's blue (`Paper.SwampPoolGlaze` 0.6, set so the swamp's mean glaze, about
  0.27, stayed where it was before the wetness was re-ranged), which shows the pools and lets
  brown stains stand out.
  **Dead trees** are the swamp's ancient trees: tall (11-14 T), a bowed trunk on splayed roots,
  two to four seed-set branches, some forked, scraping under every limb (`Glyphs.DeadTree`). The
  game grows `swamptree1` (40 a zone) and `swamptree2` (10-20) from 0.5 m below the water up,
  and 59-64% of a swamp's land lies that high, so they stand on `WorldSource.m_swampGround`
  (swamp ground at `SwampTreeLine` -0.5 m and up, from the heights before the pools are
  raised; tested on the raised heights they stood in former pools), clear of the water
  (`PlaceDeadTrees`): one candidate per `DeadTreeStep` 9 T cell (salt 27) with
  `DeadTreeOdds` 0.8 times the grove's `KeepOf`, a foot kept outside an ellipse of
  `DeadTreeGapX` 6.5 by `DeadTreeGapY` 8 T round every tree already placed (row by row, so
  every run agrees), so neighbours stand close, one in front may hide the foot of the one behind,
  and their limbs never tangle. About 750 a world (Velen 727, demoo 786; the user asked for more
  than the 12 T cell and 9 T gap gave, Velen 497 and demoo 509, and one per 30 T cell at 0.45
  before that, about 110), so the swamp reads as wooded with marsh lines between. The gap
  ellipse binds before the grid: a 10 T cell alone gave Velen only 614, a 9 T cell 697. The user chose
  these dead trees over a wood of living swamp trees with low mossy crowns, which was tried and
  rejected. They are sheet glyphs (one record group each) that close-ups replay, and the fog's
  edge thins them as trees (`FogMark.m_thinned`). Marks sit on
  a cell of 1.5 `MarshStep` and close-ups draw them at 0.75 of the pen: smaller cells at the
  tile's pen close the tufts up into lumps. A swamp's water is raised to
  `WorldSource.SwampFloor` above the water line, in the decoded minimap and in
  `GeneratorSampler`, so swamps are one marsh with no inland pools, except deep water (over
  `SwampShallow` 1.5 m) joined to the sea (`WorldSource.MarkSwampSea`, flooded from the map's
  edge through the water that stays water): raising that too turns the flooded fringe of swamps
  beside the sea into land.
- Broadleaf, plains and giant-tree crowns are hand-traced (`HandCrown`: a closed loop whose
  radius wobbles by seed, with a slight overlap), with seed-varied size, aspect, trunk length,
  lean and shade span; perfect ellipses read as stamped. Pines vary the same way: height and
  width, a leaning tip, a bowed west flank, an uneven base, trunk and shade split by seed.
- The Ashlands are sober, as on a serious survey: lava fields are toned with an even engraved
  hatch of diagonal lines (`LavaHatch`, `Lava.cs`). They are vanilla's own lava: the minimap
  mask's blue on Ashlands land is the lava weight of `GetAshlandsHeight` (cheap: 2 octaves of
  the fine cellular crackle instead of 3), large fBm patches at 0.01/m past `LerpStep(0.7, 1)`,
  squared, overlay-blended with the crackle, gated to ground over about 4 m above the water and
  sunk 2-11 m. The game reads the weight three ways: it burns over 0.6 (`Heightmap.IsLava`),
  no cliff stands over 0.5 and no Ashlands tree over 0.2 (their vegetation-mask windows).
  `Woods.BuildLava` draws lava from 0.2 (`LavaWeight` 51), the lava ground's edge as the game's
  own trees see it, and drops only patches and holes under `MinLava` 12 cells, blurred once so
  veins a pixel or two wide survive. On demoo's Ashlands: over 0.6 16.5% of the land, over 0.5
  18.2%, over 0.2 26.5%; drawn now about 24% (it was 5% at 110 cells and four blurs, then 15%
  at 0.5, 12 cells and two blurs; the user saw more lava in game than either). Downloaded
  seed maps (valheim-map.world) glow over far more of the Ashlands than the mask; they are no
  measure of lava.
  **What the game stands in the Ashlands** (read from the `d59cfac` SoftRef bundle's
  ZoneVegetation entries, altitude relative to the water, counts per 64 m zone; none on lava,
  vegetation mask = lava weight under 0.5, except `UnstableLavaRock`): `cliff_ashlands8` 1 a
  zone in groups of 1-3 within 30 m at -25 to -11 m and -10 to 0 m (sunk 1-2 m: the rock thorns
  standing in the sea), `cliff_ashlands5` 3-5 at -2 to +1 m (the shoreline) and 1-3 at 0-100 m,
  `cliff_ashlands6` 3-6 at -5 to +10 m, tilt 10-40, `cliff_ashlands3` 2-3 at 0-40 m, tilt
  10-40; `cliff_ashlands1`, 4, 7, 9 disabled; `rock_ashlands2`, `UnstableLavaRock`, trees
  `tree_ashlands1-4` at 1-12 m. There are no volcanoes, as vegetation or as locations
  (`CharredFortress`, `CharredRuins1-4`, `CharredTowerRuins1-3`, `FortressRuins`,
  `AshlandRuins`, `MorgenHole1-3`, `VoltureNest`, `SulfurArch`, `PlaceofMystery1-3`,
  `FaderLocation`): the high ground (land tops out about 60 m) is the ridge `GetAshlandsHeight`
  raises across the middle, where the lava gathers. So the atlas draws no volcanoes (they were
  placed on ground over 22 m, i.e. the lava plateau). These rocks are not in the minimap's
  heights and the game places them per zone at random as zones load, so glyphs for them are
  representative, as trees are.
  **Sea thorns** (`Kind.Thorn`, `Glyphs.Thorns`, the user's call): one to three slightly hooked
  spikes of the Ashlands' rock, the smaller behind, scattered (`ThornStep` 16 G, `ThornOdds`
  0.5, salt 37) in Ashlands sea water 0.5 to `ThornDepth` 25 m deep, foot `ThornWater` 5 px from
  land, inside the frame; drawn with the glyphs, before the waves, which keep off them (their
  boxes join `m_floeSet`). Each stands on a strictly level waterline across its foot (the boil
  marks' water language), the front one longer with a shorter line `ThornLowDrop` below set
  east, so they read as rising out of the water. Relief for pins and the fog (whole or not at
  all, `MarkShape.Spire`). The game's own density is about one group a zone, far denser; the
  Ashlands' shallow sea runs well out from the land, so the thorns fill the whole band up to
  the neighbouring biomes. Untested in game. Replaying the game's own placement
  (`ZoneSystem.PlaceVegetation`: `UnityEngine.Random` seeded per zone and prefab name, then
  height, tilt and block checks on the zone's mesh) was looked into and dropped (the user's
  call): about one group a 64 m zone is far too dense to draw, so the drawn set would be thinned
  anyway, `UnityEngine.Random` runs on the main thread only, and neither the preview nor this
  session can check the result.
  **No rock on land** (the user's call): the Ashlands' land carries no crags. The game's rock
  there is vegetation placed at random per zone, mostly on the shoreline (`cliff_ashlands5`, 6)
  and less on inland slopes (`cliff_ashlands3`), so any crag on land could only be a
  representative scatter, and players would take it for a landmark to steer by. Crags drawn
  until then used no summits, a slope test (0.25) that fit only 5-6% of the low Ashlands, so
  most came from a 10% roll on flat ground, and they kept off only the large lava fields, so a
  sixth to a third stood on lava the game keeps its rock off. The rock stands only in the sea
  (the thorns above), where it reads as decoration. The ground is one
  even cinder stipple, denser by the lava, with no ticks or fissures. Streamlines or flow strokes
  in lava fields, smoke curls, spire ridges, fissure pairs and ash-cloud mottling read as
  doodles. Sea thorns are filled with the Ashlands' rock (`Glyphs.AshBody`, the
  user's call, as peaks take the mountains' wash and Mistlands spires theirs): the Ashlands'
  wood wash, a shade deeper than the open burnt ground, marked on the Wood plane under the
  Ashlands' code (the Ashlands grow no woods; the code took a fourth bit, coverage now four
  bits) and laid opaque on the sheet (`Paper.WoodInk`), so water under a body does not
  show through. Ash bodies have no base fade or skirt: they are cut at the foot line.
- Biome washes follow the ground exactly, but every peak's body is filled strictly with the
  mountains' wash, or the Deep North's for peaks standing there: `Peak` erases its outline
  itself, since a convex hull lets the fill spill past sags and notches, in one pass
  (`ScrapeBody`, `InkCanvas.EraseOutline`, an `Op.EraseOutline` in the record: exact coverage of
  a concave outline, flanks grown `BodyGrow` 0.4 pen under their ink, base on the foot line); a
  fan of triangles from a hub leaves a faint centre line up to the apex. It marks body with
  `InkCanvas.m_markBody`, which writes one of four body masks (`BodyKind` Mountain, North,
  Mist, Wood, one plane each; any later erase clears the others, so the glyph in front
  decides). The Mistlands' pinnacles fill their bodies the same way with `BodyKind.Mist`
  (below), and trees with `BodyKind.Wood` (see the woods). The body
  is the peak's wash down to where the peak, seen as a cone in the map's oblique view, meets
  the ground: the near half of its base ellipse, at the foot line under the middle and
  `ConeTilt` 0.5 of its half width higher at the feet, so the arc scales with the peak. Below
  it the ground's own wash shows, and the body fades in over `InkCanvas.TintSoft` 6 px above it,
  the fade following the arc. Under the spine the arc dips below the foot line by the fade plus
  `ConeDrop` 0.15 of the half width, so the body is full at the foot there and its wash runs
  out below it as a rounded skirt. Nothing ever lowers a body mask: `BaseFade` sets the fade
  (a `Keep` tint) before the body is scraped, so the body is marked already faded, and `Skirt`
  raises the part below the foot line; overlapping peaks keep the stronger wash. Lowering the
  mask after marking also faded the wash of a peak behind and cut it with straight edges and
  hard-cornered blocks (`BaseArc`, `Skirt`,
  `InkCanvas.Tint`, an `Op.Tint` in the record, which lowers the mask without touching the
  ink; the body lies only inside the outline, so the arc leaves no edge beyond it). A level
  fade band or a fixed-size curve does not read as a cone's base.
  A skirt running the mountain wash out below the foot instead shows its own edges on open
  ground such as a clear-cut, whatever its shape (straight, tapered, elliptical), and a hill in
  front of a peak cuts its shape and foot line into it. The fade is laid after the body: laid
  before, it would be overwritten. Before the body, peaks and pinnacles scrape a band from a pen
  inside their base to `BaseScrape` 1.25 pen below the foot (`ScrapeBase`, no body mark): the
  glyphs behind stand north of the foot, but their round caps, a giant tree's roots and a crag's
  uneven feet reach a little past it, so dots and slivers of the peaks and trees behind would
  show just under the base line. It scrapes ink only (`BodyKind.Keep`): clearing the masks there
  left a light line across the base. Mountain ground keeps the mountain wash even where
  no peak is drawn, so the biome still reads as mountains; do not give it the neighbours' wash.
  The sheet is ink then the four masks (`InkPainter.Paint`, `SheetPlanes` 5 planes of 4096²,
  80 MB, cached together); `Paper.InkWithMips` reads the planes in place and fills bodies with
  `Paper.BodyColor` under the ink, and close-ups glaze `Paper.BodyGlaze` over the stained
  parchment, replaying the kind from the record. A kind byte beside one shared mask would save
  the Mist plane (16 MB) but cannot hold a pixel shared by two kinds, where a spire stands in
  front of a peak. Carrying the mountain tint north spills it
  over plains with no peaks. Peaks are tested for land at both base ends and both flanks
  (`PeakOnLand`), not only their centre, so coastal peaks do not hang over the sea. A summit's
  peak needs land only up to the summit (`reach` = `SummitAnchor`): above it the peak rises
  over whatever lies behind, water included. A summit's peak whose base would reach water
  (a summit on a narrow island or spit) tries the summit at other heights on its centre line,
  nearest first (`s_summitAnchors`), then shrinks by `SummitShrink` up to `SummitShrinkSteps`
  sizes before it is left out (`PlaceSummitPeak`): a lone summit may be its island's only
  high ground.
- Peaks vary by seed, like the classic map's mountains: a jagged ridge line (a plain crag, a
  shoulder on the east, or a twin summit on the west), the east face a solid wedge for the top
  `PeakWedge` of the flank and slanting hatch strokes below, some with a spur down the lit face;
  proportions from broad and low to tall and narrow (`PeakHeightOf`, shared with summit
  placement, which sets the foot so the apex lands on the summit), blunt flat tops, a snow line
  on tall peaks, and three hatching hands (slanting, level, steep; dense or open). The body is
  erased as the outline itself (above). Broadleaf and plains crowns have their eastern part
  filled (`Shade`, `CrownShade`), as the pines fill their eastern half.
- Hills vary by seed as the peaks do, so a hilly plain does not read as one stamp repeated,
  still the classic map's low lopsided domes in the same hand. `DomeOutline` sets
  the proportions (height 0.22-0.37 of the width, the taller ones fuller-sided), the crest off
  centre (0.38-0.62) with each flank its own steepness, and the crest style: one dome (half),
  a long low ridge with a flattish back sinking a little to one end, a double crest with a
  softened saddle (either top the taller), or a lower knoll run into one flank (`DomeRise`).
  Every outline has a slight two-frequency wobble, nothing at the feet, and is inked as a pen
  would trace it, hand-drawn as the peaks are: its points
  unevenly spaced and nudged off the curve so it runs in short facets, the line swelling over
  the crest and thinning toward the feet, one pen lift along the back set down a hair off with
  a small overshoot, short tails at the feet, and each hatch dash at its own angle. Domes behind
  (`DomesBehind`, `DomeBehind`): none (40%), one (mostly west, sometimes east) or two (one each
  side, the second smaller and further back), at seed-set offsets and sizes, and only single or
  shouldered. Four hatching hands under the east flank of the eastern top (`DomeDash`): a
  sparse long pair, three tapering, four or five short, three long; slant by seed, never below
  the foot. Each dash starts exactly on the inked outline, no gap and no overshoot: `DomeDash`
  slides it along its own line up to the first crossing with the stroked segments
  (`DomeSegment`, the same lift, tails and facets `Dome` inks), so its start sits on the
  stroke's centre line and its round end, no wider than the outline's pen, stays inside it;
  length and angle are kept. Hung a fixed gap below the ideal curve, dashes leave gaps of every
  size under the faceted, wobbled line. A dome scrapes its silhouette before inking, as peaks
  and trees do, but in one pass with `InkCanvas.EraseOutline` (`ScrapeDome`, outline down to
  the foot line): `InkCanvas.Erase` only fills convex polygons, so erasing the wobbled,
  saddled outline whole would leave the line of a hill behind showing through. Pens stay
  1.1-1.3 and `Pen`. The crown is the outline's highest point, wherever the crest puts it:
  `HillFoot` sets the foot from it so the crown lands exactly on the hilltop.
- The sheet carries stains (`Paper.Stains`) modelled on domain warping (Quilez),
  coffee-ring physics, Curtis et al. watercolour glazing and conservation notes on foxing, so
  they look natural and lie over everything drawn. Stains are optical density: the paper is
  multiplied by `pow(tint, D)` per channel (brown for blots, redder for foxing), densities add
  and are softly capped. Blots (old water stains with re-wetting rims, drop rings) are
  domain-warped, turned and stretched, with a tide line that rises sharply at the edge and fades
  inward, at least 1.5 paper pixels wide. Foxing comes in Gaussian clusters, biased toward the
  sheet's edge, plus lone spots, each a ragged core with a soft halo. Blots and foxing are the
  topmost layer of the view (`Paper.Dirt`, the `Dirt` RawImage above the pins and names, made
  once per world in `AtlasMap.KeepDirt`), so they cover ink, close-ups, fog, pins and names
  alike. An overlay can only blend, so each pixel is the colour and opacity that give the
  stain's exact multiply over clean paper; over ink it lifts the black a little, as water
  stains fade ink. The preview lays it over every view last (`--nodirt` leaves it out). The
  biome wash and the water's blue are glazes (`Paper.Filter`: the tint relative to clean paper
  multiplies the paper); on clean paper the colours are unchanged. Water stops at the frame;
  beyond it is bare margin. An uneven patina (`Paper.Age`, warped fBm blotches, worked out
  every 4 paper pixels) browns the whole parchment so the map does not look too clean; it stays
  in the paper, since laid on top it would lift every ink line, and pin bodies take it from the
  paper at their place (`Paper.AgedPin`). The parchment is made once per world
  (`AtlasMap.Parchment`, shared by fog and wash); the stains' blots and rows run in parallel
  (about 1.4 s offline, 4.3 s single-threaded).
- A faint watercolour wash per biome lies under the ink (`Paper.Washed`, darker in woods except
  the Deep North's, whose chalk a stronger wash would whiten, so its snow keeps one colour, and
  the Mountains', which keep the peaks' grey so the biome still reads as mountains; pale on
  water, blurred so it bleeds softly across edges); black ink alone is hard to read. The fog
  keeps the bare parchment, so unexplored land shows no colour. The paper is built from the
  decoded world each session, not cached; it is ready about a second after the world's minimap.
- Woods are shapes, not scattered trees, so forest and open land do not read as a mix.
  `Woods.Build` turns vanilla's forest mask (red in Meadows, Black Forest, Plains;
  all Mistlands land, below) into a field: copses and clearings under 12 and 16 map pixels dropped,
  then blurred, edge at 0.5. Trees stand only where their foot and crown centre are inside, so
  the outer crowns alone form the edge. Do not draw edge lines: a scalloped edge after the trees
  looks fuzzy with a gap inside it, and a plain line under the trees reads as height contours.
  Ground fills and hills keep `WoodClearance` from every wood.
  **Copses** (finding #14, the user's call): the Meadows' red is `WorldGenerator.InForest`
  (forest factor under 1.15) at the pixel's centre, where the game's beeches and birches grow
  (about ten small beeches and a big tree or two a 12 m pixel), so a dropped Meadows patch (most
  of its pixels Meadows) keeps its trees: `Woods.Build` lists them (`WorldSource.m_copses`, map
  pixels) and `InkPainter.PlaceCopses` draws a broadleaf (a small tree in a small-tree grove) on
  each, the sheet and every close-up from those pixels at their level's size, never from the
  generator's finer factor. One tree a patch, two from `CopsePair` 3 pixels, on the pair nearest
  `CopseGap` 2 pixels apart (a crown's width, so they read as one clump) with their middle
  nearest the patch's centre. Only pixels whose eight neighbours are land (`Inland`) and not
  under the softened field (over 0.5: that wood's own trees cover them) count. The pixel lies
  midway between foot and crown centre (`CopseLift` 0.3 of the 8 T height), moved up to
  `CopseJitter` 0.3 of a pixel either way. The trees take the woods' tests (shore 3, rivers, the
  land column, `SteepGround`), keep off hills and peaks (`ClearOfPlaced`) and pins (`Add`),
  count in pin cover and thin at the fog's band as every tree. Of the patches only a few
  count: on Velen 136 dropped Meadows patches give 22 tree pixels and 1 tree drawn, on demoo
  104, 18 and 8; most are beach a pixel or two wide 0-2 m above the water (the mask ignores
  altitude), the rest steep ground or under hills. In game close-ups take the Meadows'
  generator factor only where the sheet's field is above 0 (within two pixels of its woods,
  `GeneratorSampler`), so the copses it would find elsewhere do not double them. The Plains'
  dropped patches (factor under 0.8) are not drawn: unchecked against its trees' thresholds.
  **Steep ground**: no Meadows or Black Forest tree grows on ground steeper than 30 degrees in
  game (`maxTilt` 30, the oaks 20), so the Meadows' broadleaves (a small-tree grove's small trees
  too), the Black Forest's conifers and a boulder field's lone pines keep off broken ground:
  where most land map pixels within `SteepWindow` 2 round the spot are `TreeTilt` 0.577 (tan 30)
  or steeper (`WorldSource.MostlySteep`, from the world's map pixels, so close-ups agree with the
  sheet; the user's choice over a per-pixel test, as the Mountains' firs have a window). About
  4-9% of that land is that steep per pixel; valley sides steep across their whole width stay
  bare (the user kept that: the game's finer mesh is steeper still).
  The Mountains' (whose field already weighs the slope), the Deep North's and the Mistlands'
  woods do not apply it.
  **Lone oaks** (`Oaks.cs`, `Kind.Oak`; the user's pick, F "gnarled" of a design round at 1.5x,
  `OakBig`): the game's `oak1_meadows` grows on open Meadows (forest factor 1 to 3, i.e. outside
  the woods, `Random.Range(0, 2)` a zone, about one per two 64 m zones; `maxTilt` 20). The atlas
  draws a representative scatter at the game's rate, not the real oaks: reading the trees' ZDOs
  would be exact on a host, but the user chose the scatter after that research. A bowed, gnarled
  trunk with a broken stub and splayed roots under a round billowy crown (a core ellipse and
  round puffs traced as one outline, `PuffOutline`), its east side shaded narrower than a
  broadleaf's (the round crown filled near solid at sheet size). `PlaceOaks` runs after the
  Meadows' woods and copses (`PlaceTrees`), on one sheet grid (`OakStep` 96 m, `OakOdds` 0.56,
  `OakJitter` 0.85, salt `OakSalt` 44, `sheetGrid`), so the same oaks stand at every zoom; a
  cell tries up to `OakTries` 7 hashed places and takes the first that fits (the large crown
  fits few clearings), a candidate giving way to a higher-priority neighbour's possible places
  within `OakGap` 14 sheet px (`OakCrowded`, hashes only). It fits (`Fits`) with its foot on open
  Meadows (`IsOpenLand`, shore 4, `WoodClearance` from woods), off ground mostly steeper than
  20 degrees (`MostlySteep` with `OakTilt` 0.364, the game's own limit), its crown's middle and
  both flanks off woods, rivers and water, its column on land, its box clear of the copses'
  trees, the hills and peaks (`ClearOfPlaced`) and the pins (`Add`). 20 oaks on Velen (22 with
  the woods' 30-degree test). A round crown to `HideCoveredCrowns` and the pins' cover
  (`OakCrown`), a crown to the fog, a tree to close-ups; `GlyphBox` 5 by 9 T times `OakBig`.
  **Grass and oaks** (the user's rule): the Meadows' tufts are laid once every glyph is placed
  (`DrawMeadowGrass` and `DrawWoodlessGround`, after `DrawBoulders`, sheet and close-ups; still
  before any glyph is inked), from that level's own oaks (`FindOaks`, after the pins' and the
  fog's removals in close-ups). `TuftByOak`: a tuft whose foot is north of an oak's foot and
  whose strokes come within 1.5 pens of its crown's ellipse, trunk or roots
  (`TuftTouchesOak`) is left out, as the oak would cut it into a stub; one south of the foot whose
  box meets the oak's is drawn and inked again after the glyphs (`m_frontGrass`,
  `DrawGrassOverMarks`), standing over the oak's foot as a pin's tufts in front do. Laid after
  the rivers now, which no longer scrape it, a tuft keeps its whole footprint on land (the
  user's request; tufts at shores and banks stood half on the water): each end of both blades
  and the points `TuftWaterClear` 1.5 sheet px round it (thinned by the level's pen) must be
  land and off the river courses (`Tuft`, `TuftEndDry`: `IsLandAt`, `OnRiver`), against the
  sheet's land or a close-up's own water. It drops 20 to 40 of Velen's
  sheet tufts offline.
  **Wood ground with no trees** (the user's request, sample 14): the wash darkens the whole wood
  field (`Paper.Washed`, `WoodWash` where the field is over 0.5), but the trees keep off steep
  ground, the shore, rivers and the land column's water, so steep Black Forest slopes (between
  the Mountains' peaks and the coast at -3370, -700 on Velen, 34 of 35 wood cells mostly steep;
  in all about 7% of Velen's Black Forest wood and 6% of its Meadows') lay as an empty dark green.
  The wash is kept (the ground is forest in vanilla's mask, the slope only stops the trees) and
  `DrawWoodlessGround` gives it its biome's open fill, once every glyph is placed (after
  `DrawBoulders`, sheet and tiles): in the Black Forest and the Plains the Plains' dot
  (`WoodlessStep` 4 G grid, `WoodlessStipple` 0.45, salt `WoodlessSalt` 43, 2 px off the shore),
  in the Meadows the open meadows' tufts on a finer sheet grid of their own (the user's request:
  on the open meadows' 8 G grid at 0.3 a steep strip got one tuft or none; `WoodlessGrassStep`
  4 G, `WoodlessGrassJitter` 0.6, `WoodlessGrassOdds` 0.6, salt `WoodlessGrassSalt` 45, 3 sheet
  px off the shore, `m_markGrass` over pins, the oaks' grass rule above), each only where no
  Meadows, Black Forest or Plains tree's (lone oaks aside: they stand on open ground)
  footprint lies within `WoodlessGap` 6 T (the biome at the tree's foot from the world's map
  pixels, so Mountain firs hidden behind peaks do not block it), so a wood's own gaps and its
  margins stay bare. Each level of detail tests its own trees, as the boulder field's stipple
  does. It does not test glyph boxes (`ClearOfGlyphs`): a peak's box takes in the open ground
  beside its flanks and blocked nearly every dot on the coast strip; glyph bodies scrape what
  lies under them, as for the open ground's fills. Off rivers.
  Every tree's body takes its biome's wood wash strictly, as a peak's takes the mountains'
  (`BodyKind.Wood`, `Paper.WoodTint`: the biome's tint plus `WoodWash`, none extra in the
  Mountains and the Deep North), so crowns at a wood's or a biome's edge keep their colour
  where the blurred wash fades. The biome is the one at the tree's foot (`BeginWood`), packed
  into the Wood plane's byte (`InkCanvas.WoodCode`, top 4 bits; coverage in the bottom 4),
  since a cached sheet is shown before the world is read. A round crown is marked inside its
  traced outline grown `BodyGrow` (`EraseCrown`), not over its wider scraped ellipse, which
  rings it with a halo; a pine over its scraped triangle. The sheet lays it as a glaze
  (`Paper.WoodInk`, over the biome's open wash), close-ups with `Paper.WoodGlaze`. Vanilla's mask
  (`Minimap.GetMaskColor`) marks all Black Forest land as forest, Meadows by
  `WorldGenerator.InForest`, Plains where the forest factor is under 0.8 and the Mistlands by a
  smoothstep of it, so the Black Forest is one solid wood with no invented clearings. The
  Mistlands' green (`1 - SmoothStep(1.1, 1.3, ff)`) is not woods but the mist: the game's
  "Mist area" vegetation takes the same factor under 1.15, while most Yggdrasil shoots ignore
  the factor and the two that use it need 1 to 4. So all Mistlands land is wood (`Woods.Build`,
  `GeneratorSampler` 1), and the green (from `MistArea` 215, factor under 1.15, about 71% of
  Mistlands land) is read as the mist area (`Spot.m_mist`, from the sheet's map pixel in
  close-ups too): mist banks lie only there and giant trees are thinner there (below). The
  Mistlands' ground keeps its open wash (`Paper.Washed` gives it no `WoodWash`), so its
  crowns and crags stay a shade deeper than the ground. (Cells
  under water keep the biome colour but no forest, so a histogram of the mask overstates open
  land.)
  The mask marks no forest in the Deep North, yet vanilla grows woods there: every Deep North
  tree in ZoneSystem's vegetation (`_LocationList_DeepNorth` in the `d59cfac` SoftRef bundle:
  `snowfirtree`, `snowfirtree_sparse`, `pinetree_snow`, `pinetree_snow_dead`, `snowfirtree_small`,
  roughly 70-115 per zone together) is `m_inForest` with forest factor 0 to 1.15, the Meadows'
  own `InForest` limit; only 5-10 small firs a zone stand at 1 to 2. So `WorldSource.MarkNorthForest`
  fills the mask's red for Deep North land where `GetForestFactor` < 1.15
  (`GeneratorSampler.DeepNorthForest`; the factor is the same in every world), at every height:
  every Deep North tree takes altitude 0.2-1000 m, and the high ground is gentle uplands, not
  mountains (`GetDeepNorthHeight` is base + 0.1 + 0.1 noise, without the Mountains' doubling,
  ridge noise or `BaseHeightTilt` roughness; of its ground over 45 m only 6-11% is steeper than
  30 degrees at 12 m pixels, of the Mountains' over 50 m 55%). So the Deep North gets no filler
  peaks: over `DeepNorthSummitHeight` 45 m a real summit (the summit rule, 53-63 a world) takes a
  peak and its other hilltops hills (`PlaceSummitsAndHills`, `HilltopCells`, `HillProminence`,
  half a width off woods as everywhere), the user's choice over summits alone or slope-gated
  fillers (those landed on sea cliffs and the border moat's ramp). Snow marks keep off those
  summits and hilltops only (`NorthTops`, found once per world from its heights and kept on
  the `WorldSource` for the sheet and every close-up, since ground fills are drawn before the
  glyphs are placed). It used to fill all ground over 45 m (16-20% of the Deep North) with
  peaks and cut the woods there.
  Its trees are the Black Forest's conifers (same rule, salt 24). The trees' own `maxTilt` 30 is
  not applied there (the user's scope: only the Meadows and the Black Forest take it, below). `Mathf.PerlinNoise` under the factor means the offline
  harness cannot compute it: it sets `WorldSource.s_forestFactor` to Perlin's improved noise,
  which agrees with vanilla's Meadows mask on only about 71% of cells, so preview woods in the
  Deep North are in the wrong places (their character and density are about right).
  The mask marks no forest in the Mountains either, yet firs grow there: `firtree_mountain`
  (5-20 a zone, scale 1.5-3) and `firtree_small_mountain` (30 a zone, scale 0.3-0.7) in the
  `d59cfac` bundle's main vegetation list, Mountain biome, `m_inForest` off (so no forest
  factor; the small firs' 1.05-1.15 thresholds are ignored), altitude 2 (0) to 280 m above
  `p.y - 30`, `maxTilt` 30. The alt biomes change counts only: Wolf Mountain adds
  `firtree_mountain2` (10-20), Drake and Fortress Mountain block `firtree_mountain`, the small
  firs stay everywhere (the atlas draws them sparse or dense, see alt biomes below). Gentle ground (12 m central differences, under 30 degrees) is about 45%
  of mountain land in every height band and mixed pixel by pixel; mountain land under
  `PeakHeight` 50 m is only about 2%, so a height split like the Deep North's would draw almost
  no woods. `WorldSource.MarkMountainForest` therefore fills the mask's red where most land
  within `MountainFirWindow` 2 pixels (60 m) is under 280 m and gentle; per pixel it would draw
  specks of trees among the peaks. Close-ups sample the sheet's own field (`GeneratorSampler`),
  since slope needs neighbours and the generator's finer heights would bring back the specks.
  Peaks and ridges stand on the same ground, and the woods climb their lower slopes (the user's
  call; `ClearOfPeakBases`, the conifer rule, salt 24): a tree standing in front of a peak's or
  ridge's lower part or below its foot, within `PeakBaseWidth` of its width plus the crown, is
  kept only while its whole crown stays within `SlopeTop` 0.35 of the glyph's height over its
  foot (0.5 covered too much of the low ridges, whose height is about a pine's), so the upper
  part stays clear and the mountain still reads; a tree whose foot is higher up is hidden
  behind the glyph. A tree in front of that lower part and inside the glyph's body (narrowing
  up the slope) is drawn just after the glyph instead of by its own foot (`SlopeOrder`,
  `Glyph.m_onSlope`, `m_order` `SlopeOrderGap` past the glyph's foot plus `SlopeOrderStep` a
  pixel further south, so trees on one slope keep their order), so it stands on the slope
  rather than being scraped away behind it; `Order` gives that order to the sheet's drawing,
  the close-ups' (their tree sort and the replay of the sheet's groups) and the pins' cover
  rules. Feet, widths and heights are recorded (`InkRecord.AddPeak`) so close-ups decide the
  same. The mountain wash is not
  darkened under the woods. The preview's mountain woods sit where the game's do, as no forest
  factor is involved; alt biomes only show offline when forced (`--alt`, below).
- **Alt biomes** (Valheim 1.0's per-patch variants, chosen once per world from the seed) only
  change how many trees stand in the same wood shapes (`Grove` in `WorldSource.cs`).
  `WorldSource.Capture` keeps the `WorldGenerator` only when `m_world.m_biomeData.IsReady`
  (otherwise one warning per session and no groves); `Decode` on the worker reads
  `GetBiomeSector` for every map pixel's centre (a plain read of arrays built at world load) into
  `m_grove`, only on pixels whose decoded biome is the sector's. `GroveOf` maps `AltBiome.m_name`:
  Kalhygge Black Forest `ClearCut` (no trees; `IsOpenGrove`, `WorldSource.IsOpen`, keeps it out
  of the canvas wood and the samplers' wood, so ground fills and clearances treat it as open
  land; it gets `Stump`s on one sheet grid, salt 35, `StumpStep` 3.8 G: a short trunk stroke with two or
  three roots (solid trunks with a sawn top are too heavy, felled logs read as axes), gathered
  loosely into stands by a slow noise (`StumpStand` 40 px, `StumpOdds` 0.5 inside them, `StumpFloor`
  0.5 of that between), as an even scatter reads as a stipple and bare ground between stands as lumps.
  Stumps are drawn once every glyph is placed (`DrawStumps`, after `DrawScarps` on the sheet and
  in tiles) and keep `StumpClear` off every glyph's footprint (`ClearOfGlyphs`), so none stands
  on a hill. Between them a light stipple, `ClearCutStipple`, and hills on its hilltops
  by the hill rule, so the felled wood is not a blank); Rock Black Forest `Rocky`, open land as the clear-cut is (`IsOpen`; hills on its
  hilltops; `ReadGroves` keeps the lowest value, so felling outranks it and it outranks
  thinning): the game blocks `firtree_many_blackforest` (40 a zone), `Pinetree_01_blackforest`
  (60) and `firtree_blackforest` (5), leaving only `firtree_blackforest2` (1-2) of the big
  trees, and adds `rock_4_blackforest` 30, `rock_3_blackforest` 15 (scale 3-6, house-sized)
  and `rock4_blackforest` 3, so it is a boulder field, not a half wood. Its boulders
  (`Boulder`: a dome of nine uneven facets rounded once, base flatter than top, scraped to
  parchment, the part east of a seed-tilted chord filled solid, about 3.5 G wide, `BoulderPair`
  0.3 with a smaller one beside it, drawn north one first) are placed as the stumps are
  (`DrawBoulders`, after `DrawStumps`, one sheet grid `BoulderStep` 5 G, salt 39, gathered into
  scree by a `BoulderStand` 30 px noise, `BoulderOdds` 0.6 inside and `BoulderFloor` 0.2 of that
  between, `BoulderClear` off every glyph's footprint, off rivers, and off water by their
  sheet-sized half width plus `BoulderShore` 1.5 sheet pixels, so none stands on the coast's line
  or against the lake lines on the water beside it; the sheet size, not the level's, so every zoom
  keeps the same ones); about 2.2 G wide they read as pebbles. Its lone pines are conifers on a per-level `LonePineStep` 14 T grid, salt 40,
  `LonePineOdds` 0.15, on its open land clear of every non-tree glyph placed before
  (`ClearOfPlaced`; close-ups load the record's glyph boxes before their trees for it). Its
  open ground takes the Plains' dry stipple too (the user's call, so it is not bare paper;
  `DrawRockStipple`, the Plains' 5 G grid and odds, `RockStipple` 0.45, salt 41), drawn after
  the boulders: `DrawBoulders` files each boulder's footprint (both stones, a little below the
  foot) with the glyph boxes once it has placed them all, and a dot keeps `RockStippleClear`
  0.6 G off every footprint (`ClearOfGlyphs`: boulders, lone pines, hills) and 2 px off the
  shore, at each level of detail against that level's own boulders and trees. Kalhygge keeps
  its own sparser `ClearCutStipple`. The Black Forest has no other ground fill:
  vanilla's mask marks all its land as forest (its old stones on open ground never drew);
  Bog Swamp `Thin` (`ThinKeep` 0.33 by the cell's hash `SparseRoll`): vanilla blocks
  `swamptree1` and `2` (about 55 ancient trees a zone) and adds `swamptree5` and `6` (about 18)
  and another 60 small dead firs (not drawn, too small); Pinetree Black Forest, Drake/Fortress
  Mountain and Hut Swamp `Sparse` (`SparseKeep` 0.5, same hash; Hut Swamp blocks `swamptree1`
  and `2` and adds `swamptree3` (20) and `4` (7), about 0.49 of the ancient trees); the swamp's variants thin its dead trees, whose odds
  are multiplied by `KeepOf`; Lox Plains `Grazed` (`LoxKeep` 0.5, same hash; the user's call,
  sparser than the game as open grazing land, raised from 0.35); Death Plains and Birch Meadows `Light`
  (`LightKeep` 0.75, same hash): the Plains variants block only `birch2_aut_heath` (10 a zone, scale 1-1.5) and keep
  `birch1_aut_plains` (30), and Birch Meadows blocks `beech1_meadows` but adds
  `birch1/2_meadows2` (10 + 5-9), `beech5_meadows` (10) and `oak2_meadows` (1-2), about 0.75 of
  the normal ~47 big trees (its birches also grow on ground the minimap shows open; not drawn,
  the user's call); Smalltree Meadows `Smalltree`: vanilla blocks `beech1_meadows` (40),
  `birch1` and `birch2` and adds 100 `beech_small3/4_meadows` and bushes, so its big trees drop
  to about 0.17 (`beech2_meadows` 5-10 and the oaks) while it has more trees than a plain wood;
  `SmalltreeBig` 0.085 of its spots (hash `SmalltreeRoll`; the game's share of 0.17 halved by
  the user's call, as that many big crowns read as too many) stay full-size broadleaves and
  `SmalltreeKeep` 0.75 of the rest (hash `SmalltreeKeepRoll` 97) take the small-tree glyph,
  `Kind.SmallTree` (`SmallTrees.cs`: one small crown whose top is three rounded lobes, the
  classic map's leafy-tree sign, on a short trunk, its east side shaded over `SmallShade` 0.8 of a
  broadleaf's span; the user's pick, F of a design round of seven). It is about 0.7-0.9 of a
  broadleaf's footprint on purpose: the pen is a sheet pixel at every zoom, so crowns under about
  3 T fill solid, and a full broadleaf's shade made the small crowns read black. With a small
  tree on every spot the lobed crowns overlapped heavily in places (the user's call), hence the
  keep share. It is a round crown to `HideCoveredCrowns` and the pins' cover (`SmallCrown`, the
  ellipse from its underside to its lobes' tops), a crown to the fog (`FogShape`) and a tree to
  close-ups (`IsTree`), and its body takes the wood wash (`BeginWood`); Wolf Mountain, Trees Mistlands `Dense` (the rule's grids are
  already 90-95% full, so `ScatterTrees` gives dense spots a grid of their own, `DenseSpacing`
  0.85 of the step, salt +`DenseSalt`, every cell taken, about 1.4-1.5 times the trees); all others none.
  `ReadGroves` keeps the lowest `Grove` value, so the enum runs from felling to thickening
  (`Thin`, `Grazed`, then `Sparse`) and `Dense` stays last (`SetGroves` sizes by it). Spots
  take the grove from `WorldSource.GroveAt` on the sheet and in close-ups alike, which in
  close-ups falls back to the nearest pixel of the spot's own biome, so the generator's finer
  border does not leave a fringe of plain woods. Mountain woods are mostly hidden by the peaks'
  bases, so Wolf and Drake show little there. The preview cannot compute alt biomes
  (`UnityEngine.Random`): `run.ps1 -Alt 'NAME:wx:wz:radiusMetres',...` (`--alt`, repeatable) lays
  one on its biome's cells within the radius via `SetGroves`. Untested in game, including
  whether the data is always ready when the minimap is; a sheet cached in a session without the
  data keeps plain woods while close-ups show the variants.
- Ground fills on open land stay sparse, since the wash tells biomes apart. The Deep North is
  a deliberate near-blank snowfield with rare snow marks (chevrons in thin winding bands,
  `SnowMarkScale`, `OnSnowMarkBand`), like the classic map's northern waste, not snow stars,
  drifts, ice shards or cracked ice. They are a mark of the snow, not a landform: the game has
  no ice or pressure ridges on land, so code and docs call them snow marks. Its woods (above)
  are the one dense mark; the open snow between stays blank. Snow marks keep off the shore, off the woods (`IsOpenLand`,
  as ground fills), off the Mountains' peak ground and the Deep North's summits and hilltops
  (searched mostly south of the mark, since a peak rises north of its foot) and off each other (a
  hash-only neighbour test like the marsh marks'), so the sheet and tiles agree. Ice floes are
  filled with the Deep North's body wash, not left showing the sea's. They lie where the game
  sets its ice (`ice1_deepnorth`, `ice_rock1_deepnorth`: Deep North vegetation only, at least
  1 m of water): `GetBiome` gives the Ocean before the Deep North wherever the base height is at
  most 0.02, so the open northern sea is Ocean with no ice, and the Deep North's own water is its
  shallow coast (mostly within 24-36 m of land), the north side of the border moat
  (`CreateDeepNorthGap` pulls the land under for 400 m either side of the border; the minimap's
  heights already include it) and its lakes. `WorldSource.MarkIce` marks sea water (flooded from
  the map's edge) where `IsDeepnorth` holds and the height over the gap is at least `IceFloor`
  14 m (Ocean is at most about 4 m, Deep North water 23 m and more, nothing between on Velen
  or demoo), grown `IceReach` 4 map pixels (`m_iceWater`). Floes are scattered on a `FloeStep`
  10 G grid at `FloeOdds` 0.5 there, their outline `FloeShoreGap` 1.5 px off land
  (`FloeRadius`). About 70% of the floes used to stand on the open Ocean. They are drawn after
  the waves and scrape them; the waves are laid as if the ice were not there (the user's call:
  the floes win), so `m_floeSet` holds only the sea thorns and the spires in the water. Lakes get no floes (the user's
  call) but a lighter water glaze (`WorldSource.m_northLake`, Deep North water not joined to the
  map's edge, at `Paper.NorthLakeWater` 0.55 of the water's strength, sheet and close-ups).
- The Mistlands keep their wash. Mist banks are veils of three wavy lines (middle longest, a
  slight tilt, a break or two) with no scraped band under them (bands read as heavy padding),
  laid only in the mist area (`Spot.m_mist`, above) and off steep ground, in the hollows: the
  odds `MistOdds` 0.9 fall with the bank's height over the lowest ground within `MistReach` 5
  map pixels (60 m; `WorldSource.FloorAt`, never below the water), all kept up to `MistFull`
  6 m, none from `MistClear` 18 m. The game's `ParticleMist` emits above the lowest ground
  within 50 m and up to about 10 m over its emitter, which stands on the lower slopes about
  1.5-9.5 m over the biome's base, so its mist fills hollows and the knolls stand out of it; a
  ceiling of absolute height (75 m) did nothing, as only 0.4% of the land rises that high.
  Banks are found on the sheet only; close-ups replay them from the record. Banks are glyphs (`Kind.Mist`, `PlaceMist`), sorted in with the
  trees and crags by their lowest line, so whatever stands in front hides them; drawn over
  everything, with boxes round every spire kept clear, they leave only stray lines. There is no
  `Mist` ink layer. A bank's foot is its bottom line's level (`MistGap` below its centre), and
  the bank is drawn `MistFoot` north of it, the deepest its lines dip with tilt and wave:
  centred `MistGap` above the foot, the bottom line runs up to 2.7 sheet px south of it, under
  the feet of glyphs drawn after it, and shows as a hairline along a peak's or crag's base.
  Sorting banks by that lowest point instead lays them over many more crowns and across the
  peaks' lower bodies.
  Its small inland pools stay water, every one with its own shore: filled in as ground, the
  Mistlands lost the many little lakes that are part of their look.
  A crag whose drawn shape overlaps a mountain peak's is left out (`DropSpiresOnPeaks`): where
  the Mountains meet the Mistlands both stand on the same steep ground, and crags among the
  peaks laid their rock, wedge and hatching across the peaks' bodies.
  Crags (`Spire`) are pinnacles in the peaks' hand (`Pinnacle`: 0.45-0.62 of their height wide,
  jagged flanks, a ledge, some twin tips, a solid wedge down `SpireWedge` of the east face, a
  smaller one or two behind), on real summits (`PlaceSummitSpire`: highest within `SpireCells`,
  `SpireProminence` metres, placed at `SummitAnchor` as peaks are). A summit crag's foot must be
  Mistlands, or a crag on a border summit plants its base and rock in the biome to the south;
  such a crag slides north along its centre line (`SpireAnchorSteps`, down to its foot on the
  summit), so border summits, often a Mistlands fringe a pixel or two wide, keep their crag
  rising over the Mistlands. Both base ends must be land and the lower `SpireLandBase` 0.3 of
  it over land (the top may rise over water), or it shrinks by `SpireShrink` up to
  `SpireShrinkSteps` sizes, else is left out. Filler crags stand only on ground steeper than
  1.3 `SpireSlope`, clear
  of the summits; a fixed trapezoid on every high pixel reads as fence posts.
  **Spires in the water**: the game's `Mistlands_RockSpire1` stands down to 10 m under the water
  line, so crags also stand in the Mistlands' sea where it is at most `SpireDepth` 10 m deep
  (`WetSpireFoot`: sea water of the Mistlands at the foot, `SpireWater` 4 px off land, the box
  across the foot and under it on water). Filler crags take `SpireWaterOdds` 0.25 of the 18 G
  grid's water cells there (the game's density is not representable; demoo gets 14, Velen 16
  at 0.2, against some 1,260-1,310 on land); a summit whose crag fits nowhere on land at any
  size (an islet's) tries the same sizes and slides with its foot in the shallows (about 75 on
  Velen), rising out of the water over the islet. A wet crag (`Glyph.m_wet`) is drawn as the sea
  thorns are: its pinnacles' feet level, no base fade or skirt, a strictly level waterline
  across each foot reaching `SpireReachMin`-`SpireReachMax` of its width past either foot (those
  behind half as far), and under the front one a shorter line set east (`ThornLowDrop`). Its box
  (`GlyphBox`) is 0.62 of its height either side. It stays relief (fog and pins as before), and
  the waves keep off it as off the thorns (its box joins `m_floeSet`), though the Mistlands'
  islet fields get no waves anyway. Sheet only, replayed by close-ups. Their bodies are
  filled as the peaks' are: scraped and marked `BodyKind.Mist`, with the peaks' `ScrapeBase` and
  base fade, so the glyph in front decides. The fill is the Mistlands' own wash as its woods have
  it (`Paper.WoodTint(Land.Mistlands)`, the same as its tree crowns), so the rock is that
  land's colour, a shade deeper than its open ground; a separate rock hue reads as off from
  the Mistlands. On the sheet it is not laid opaque as a peak's body is: the clean-paper colour on the stained,
  patinated Mistlands reads lighter and lilac there while close-ups show it deeper than the
  ground, so `Paper.MistInk` lays it as the colour and opacity that give the rock's glaze over
  the Mistlands' wash (as `SeaInk` does for the sea wash).
  Scarps cover the steep rock, which would otherwise be nearly bare paper: about 63% of
  Mistlands land is steeper than `SpireSlope` (the sheet's 12 m pixels), where giant trees
  stand only sparsely, mist keeps off above 1.3 of it and filler spires stand on only a third
  of an 18 G grid.
  `DrawScarps` (salt 19, so sheet and tiles draw it alike) puts a survey cliff sign (`Scarp`: a
  slightly broken edge along the contour, four or five hachures downhill, longest in the middle)
  on ground of `SpireSlope` and up, `ScarpStep` 6 G, staggered, `ScarpOdds`, `ScarpShore` off
  the water, giving way to a higher-priority neighbour within `ScarpGap` of their lengths
  (`ScarpCrowded`, hashes only). Its edge follows `Downhill`, the heights a map pixel either
  side (the world's on the sheet, the tile's own in close-ups), so the marks follow the terrain.
  Scarps keep off mist banks' lines (`ClearOfMist`; the banks are found before the ground fills,
  `FindMist`, and recorded, `InkRecord.AddMist`, for close-ups), off river courses, and
  `ScarpClear` off every placed glyph's drawn footprint (`ClearOfGlyphs`, `GlyphBox`; a giant
  tree's as its crown and its trunk apart, `GiantCrownFoot`, `GiantTrunkHalf`): placed among
  the trees and spires they overpopulate the map. So they run after `PlaceGlyphs` (in
  close-ups after `PlaceTrees`), still on the background layer under every glyph; close-ups test
  their own trees and the sheet's other glyphs from `InkRecord.AddGlyphBox`.
  Giant trees are Yggdrasil shoots (a kinked trunk on flared roots, one or two branches, one
  broad, slightly ragged canopy, `HandCrown` wobble a little raised), packed on a 7 T grid
  (`GiantOdds` 0.9, `GiantMistOdds` 0.6 in the mist area: the game grows about 126 shoots a
  zone there and 226 outside it) on flat ground; the big shoots (`YggaShoot1-4`, tilt up to
  80 degrees, a terrain delta of 2-3 m) grow on the rough ground too, so steep ground gets a
  sparse grid of its own (`GiantSteepStep` 16 T, `GiantSteepOdds` 0.5, salt 38), sparse so the
  scarps keep most of the rock (`ClearOfGlyphs`). Flat or steep is the sheet's slope at the
  spot's map pixel (`SheetSlope`) in close-ups too: their own slope, over an 8-16 m baseline,
  finds more steep ground than the sheet's 24 m;
  extra lobes behind or beside the crown look lumpy and leave thin black crescents along its
  edge, and three round crowns on a stick read as lollipops.
- Every pen line is at least `Pen` (1 px) and full strength: thinner or fainter lines average to
  grey in the ink mips at the opening zoom. The Ashlands hatch is the one deliberate grey tone.
- Close-ups need the painted record, so the world is painted in the background right after login even behind a cached sheet (about 15 s in game; started only on the first opening, close-ups came 30 s after joining, the user's call). In game a 256² close-up took about 85 ms on a worker (timing build, 1440p, 8x tiles): sampling 14, ink 15 (80+ in dense woods), colour 42, mipmaps 1. The colour pass now reads each paper value once per pixel (`DetailTile.Render`), pixel for pixel the same. Skipping trees whose footprint misses the tile saved nothing (clipped strokes are cheap); the ink cost is in the trees on the tile.
- Zoomed in past 1.25 screen pixels per sheet pixel, close-ups come from fixed 256² tiles
  (`DetailTiles`) at one level only, at every zoom: the finest the view reaches, at most 4x (an 8x level above
  1080p took four times the tiles for a view and filled in 6 to 15 s in game, so it was dropped,
  the user's call; there 4x tiles are magnified a little at the deepest zoom), mipmapped on the worker and faded in over 0.2 s, laid over
  the sheet (opaque, under the fog). Do not pick the level by zoom: each level draws at its own
  level of detail, so trees and marks change as the zoom crosses levels. Do not show coarser
  levels while fine ones load either: levels replacing each other look scratchy. Close-ups
  start at `DetailFrom` 1.25 or once the view takes at most `MaxVisible` 520 tiles, whichever is
  later (from the viewport's screen size, so panning never toggles them): above 1080p the view
  takes more tiles than at 1080p,
  so there they start at about 1.3 screen pixels a sheet pixel at 1440p, 2 at 4K (lowered from 1.5 and 400 when the user asked for close-ups a little earlier). Tiles are drawn ahead of need, one per worker (half the cores, at most 4): the view
  from the pointer outward and a ring of one tile round it; zoomed out, up to 48 tiles round the
  pointer, where a zoom lands. At most 3 textures are uploaded a frame and 320 kept (about
  110 MB with mips), or the tiles wanted plus 160 when that is more. Tiles sample their ground `TileMargin` beyond their edge and scatter glyphs
  from cells `GlyphReach` beyond it, so neighbours draw the same glyphs and margins and seams do
  not show. The sheet's non-tree glyphs are recorded as groups with their feet
  (`InkRecord.BeginGroup`), and a tile sorts them in with its own trees and draws all from north
  to south; replayed over all the trees, spires behind trees would cover them. The ground fills,
  trees and coast are drawn afresh (`InkPainter.PaintTile`) from a `TileGrid` sampled every few
  tile pixels; everything else is replayed from the `InkRecord` (every pen call of the painting
  with its `InkLayer`, about 720k calls, 20 MB, indexed in 64 px cells). Because only painting
  yields the record, the world is painted once per session in the background even when the
  sheet came from the disk cache with the current pins, but then only after the cache has
  loaded and the atlas has first been opened, and that painting does not write the cache again.
- Tiles sample the world generator (`GeneratorSampler`: `GetBiome`, `GetBiomeHeight` and the
  forest factor, exactly what vanilla's minimap is built from), which vanilla itself calls off
  the main thread from `HeightmapBuilder`; `Mathf.PerlinNoise` underneath means the offline
  harness cannot run it and uses `MinimapSampler` instead. A failing generator falls back to the
  minimap once. Samples sit on world-anchored points at 1, 2, 4, 8 or 16 m (the spacing nearest
  3 tile pixels: 8 m at 2x, 4 m at 4x, 2 m at 8x) and are kept per world in a `SampleCache` of
  32² chunks (about 1200, 45 MB at most, least recently used dropped), so neighbouring tiles and
  zooming back only sample new ground. Generator speed is unmeasured in game.
- The tile's level of detail is the square root of half the zoom (1 at 2x, 2 at 8x): ground fills
  and trees shrink and pack by it, and every pen width and glyph margin thins by it
  (`InkCanvas.m_penScale`, `m_pen`), recorded layers included. Trees at full scale (lod 4 at 8x)
  are true to life but read as a uniform bubble texture. Scatter grids are anchored to the sheet
  and hashed per level, level 1 as the sheet, so glyphs do not move as the view pans. The
  Meadows' grass is the exception (`Scatter`'s `sheetGrid`): one 8 G grid hashed as level 1 and
  its shore and wood margins in sheet pixels at every level, only the tuft shrinking with the
  level, so the same few tufts stand at the same places on the sheet and in every close-up; on
  per-level 12 G grids they were a fresh set at each zoom and nearly absent on the sheet and at 2x.
- Rivers and streams come from `WorldGenerator.GetRivers()`/`GetStreams()`, copied in
  `WorldSource.Capture` and inked along vanilla's own sine curve (`RiverPath.PointAt`, from
  `WorldGenerator.RenderRivers`) over land only, on a scraped band. None is drawn over the
  Ashlands (`WorldSource.HasRiverBed`, sheet ink and the paper's stream band): every biome's
  height function calls `AddRivers` but `GetAshlandsHeight`, so courses crossing it have no bed. Streams run from their low
  mouth (`p0`, 26-31 m) to the source and taper. A stream whose mouth is in the Deep North is
  dropped at capture (`WorldSource.AddRivers`): vanilla carves streams with
  `RenderRivers(..., SkipDeepNorth)`, which skips those, yet `GetStreams` still returns them, and
  the Deep North's carved streams come from a second pass (`PlaceStreams` with `isDN` true) whose
  list is thrown away, so the atlas draws no Deep North streams. Replaying that pass would need
  `UnityEngine.Random` on the main thread and an exact copy of vanilla's search; not done.
  The offline harness cannot run the generator and draws no rivers or streams at all (the river
  channels in its previews are the heights' own water), so none of this shows offline.
- Tall glyphs test their whole column with `IsLandColumn` as well as the shore margin at their
  foot, or peaks and trees on south-facing coasts or thin spits reach into the sea.
- Thresholds are set from the Velen world's height percentiles (Ashlands land p90 22 m above
  water, Deep North p90 59 m, Mountain p50 90 m).
- The offline preview in `tools/preview/` feeds vanilla's cached minimap files
  (`worlds_local/<world>/cacheMinimap*`, gzip of Color32 or half buffers) through `WorldSource` and
  `InkPainter` by reflection; painting the whole world takes about 2.5 s. It is a separate
  project, never shipped: `Atlas.csproj` removes `tools\**` from its items and the package holds
  only the DLL. `run.ps1 -World <name> -Views ...` renders into `tools/preview/img/` (ignored);
  `samples.ps1 -Publish` refreshes the 40 samples in `screenshots/` (committed): 01-03 the world,
  region and start island, then each biome's close and wide view followed by its variants that the
  atlas draws differently, forced over the biome's own wide view with `-Alt` (one run per set of
  variants of different biomes; Bog and Hut Swamp in separate runs), 26-27 pins, 28 the pin
  catalogue, 29 the pin reference, 30 the sea mark catalogue
  (`Preview --seacat`). Pin places: `Preview --pinref <png>` draws every pen-drawn glyph (all
  `PinGlyphs` kinds but `Traced`) on plain parchment with a 1 px cyan crosshair on its place:
  the sheet 1:1, its pixels 8x, a 4x and an 8x close-up, whose cross carries a ruler in the
  glyph's own pin units (box / `PinUnits`, as `InkCanvas.Pin` draws it there). Each is placed by the painter's own
  `FindMarks`, `DrawGlyph` and `PinBox` (`Lift`, `Sink`) at level of detail 1, and the close-ups
  replay that box through `InkCanvas.Pin` as `InkRecord.Replay` does, so the cross shows the
  drawing's truth: the footprint's middle of elevation glyphs, the dot's and skull's centre. It prints
  each glyph's ink extent round its place (`PINREF`, 8x close-up px). `run.ps1 -PinCross`
  (`--pincross`) draws the same cross on every `-Pin` and `-Temple` place in any view but `trim`,
  by that view's own sheet-to-image mapping: a full cross from 3x up, arms pointing in from
  outside the glyph below (the `pins` views' test markers are not crossed). A new alt biome in `GroveOf` gets a sample there. Refresh them
  after visual changes. It needs Valheim's Managed DLLs (`VALHEIM_INSTALL` overrides the Steam
  path) and BepInEx's core DLLs from a Gale profile copied beside the exe, which `run.ps1` does,
  and writes `System.Console`, as `assembly_valheim` has a global `Console` class.

## In game

Nothing has been confirmed in game yet: the table hooks, opening the interactive map in nomap, the
`Minimap.IsOpen` input blocking, the view's zoom and drag, pin sprites and labels, the fog edge,
reading the 4-bit forest mask texture with `GetPixels32`, running the world reads off the
main thread, and the sheet's trimmed edge (its strips' upload with their own mip chain, their
match with the frame's colour, shimmer while zooming).
