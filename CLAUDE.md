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
  marks, read when the pins are built, `AtlasView.Shown`; the player's own pins always show),
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
  as modern stickers on the hand-drawn map. A pin drawn in elevation stands on its place as a
  tree or peak does, its foot on the spot (`PinGlyphs.Lift`, from each glyph's ground line in pin
  units; the images and the name layout are lifted, the holder stays on the place); plan signs
  (dot, stone circle) and traced sprites stay centred.
  `src/Ink/PinGlyphs.cs` draws the vanilla pin icons in pen, every glyph but the Sacrificial Stones inked solid with its details cut back to paper (the user's call; the Stones keep their outlined blocks): fire: campfire, house: longhouse,
  the "hammer" as Mjolnir, a solid pendant silhouette after the Skane hammer, not a work hammer;
  circle: dot; portal: dolmen; death: memorial stone with the yr rune on a mound, never a
  cross; checked: struck through with paper, `InkCanvas.Pin`, a red strike vanishing on a solid glyph). The game's own pins get map signs too: bed (spawn point), a
  solid horned skull in the language of the game's boss icon (Boss, from Vegvisirs), Hildir1-3
  as barrow (Smouldering Tombs), cave mouth (Howling Caverns) and tower (Sealed Towers); location
  pins by `LocationSpriteData.m_name`: `StartTemple` the Sacrificial Stones (below),
  vendor/Haldor a plain tied bag, Hildir a tent with pennant, BogWitch a cauldron,
  `AncientUpgradeStation` (the Forge of Potential) a ward stone on a plinth with a gem on top,
  after the game's own icon, which follows the vanilla ward. These five are all the locations
  vanilla's minimap lists icons for (`Minimap.m_locationIcons` in SoftRef bundle `17245031`,
  names checked against the SoftRef manifest).
  **Pins are drawn into the sheet** (the user's design), at a fixed size like every other glyph:
  `AtlasMap.KeepPins` builds the list (`SheetPin`: place, glyph, checked) from the snapshot's
  pen-drawn pins the settings show on explored ground (`AtlasView.InSheet`), plus the
  Sacrificial Stones from `ZoneSystem.GetLocationIcon("StartTemple")` (polled until found; a
  first painting waits up to `TempleWait` 15 s for it, as a client gets the icons from the
  server). `InkPainter.Paint` takes the list; each pin is a `Kind.Pin` glyph, part of the map
  (the user's design): sized for what it stands for against a tree of about 13 sheet pixels
  (`PinSheetSize`: campfire, bed and circle 10, a longhouse or tent about a tree at 16, a boss
  altar 18, a barrow 20, a cave or tower 22, the Sacrificial Stones 26), standing on its place by `PinGlyphs.Lift`. That
  size is levelled to the trees: close-ups draw pins smaller by their level of detail as they
  draw their trees (`InkCanvas.Pin` scales the box about the pin's foot by the canvas's pen
  scale, `FindMarks` sizes a tile's marks by `1 / lod`), so on a screen above 1080p, whose
  close-ups are 8x (lod 2), pins keep their size beside the trees. Inked by `InkCanvas.Pin` in red (`Paper.PinInkColor`, a dulled vermilion, as places were rubricated
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
  laid before any glyph and the pin's body would otherwise scrape them away. Pins also have a
  hand-drawn waver (`PinGlyphs.Waver`: every
  point moved by a smooth noise seeded by the pin's place, straight lines and outlines cut
  into short pieces so they bend; `InkCanvas.m_warp`). Its body scrapes the ink and body masks
  so the ground's own wash shows round it; checked pins are struck through. Recorded as `Op.Pin`, so
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
  every pin was too aggressive. Relief (peaks, hills, crags, volcanoes, `IsRelief`) is never
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
  The glimpse (the user's design): `Paper.Fog` lays its torn edge `GlimpseBand` 160 m out from
  the explored land and round every glimpsed mark (a distance field from the explored pixels and
  the marks), and writes a second texture, the glimpse, bare parchment whose opacity is the
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
  memory, and no custom shader is possible without asset bundles. Names are left off for
  now (`s_showNames`), to be given another pass. The preview takes `-Temple wx:wz` and
  `-Pin GLYPH:wx:wz[:checked]`.
  The Sacrificial Stones are a pin like the others (the user's call), `PinGlyphs.StoneCircle`
  in a 26 px box: a ring of standing stones round a low altar in the map's oblique view, each a
  block with a lit front, a solid east face and a thin top, drawn back to front, the ring's
  middle on the place. The stones take the peaks' rock wash, not the ground's (`PinMark.m_rock`,
  marked as Mountain body by `InkCanvas.Pin`), and the ground inside the ring is not cleared:
  only the stones scrape what lies behind them.
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
  (`screenshots/atlas-sample-15-pin-catalogue.png`). Names: bold (the regular weight was hard to read in game), letter-spacing, bare
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
- **Persisted config.** Section and key names (`Pins`, `BedPin`, `DeathPin`, `BossPins`,
  `TraderPins`, `HildirPins`, `PlacePins`): renaming one resets players' settings.
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
  - `Room` stops a stroke `WaveSlotGap` short of land, floes or the frame, dropping crests to fit,
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
  the sea serpent, the serpent coil and the leviathan in the open ocean (the Ocean biome outside
  `IsDeepnorth`, where the Serpent swims; the leviathan is decoration, not at real positions),
  the bonemaw serpent only in the Ashlands' sea (water of the Ashlands biome: vanilla's
  `GetBiome` gives the Ashlands before the ocean, the Deep North after it). The Deep North's sea
  stays blank. `IsAshlands` is not called: it runs `WorldGenerator`'s static constructor, which
  the offline harness cannot load. Each glyph at most `IllustrationsEach` 3 times. Candidates
  are the peaks of the open water: the distance from any land, islets included, and from the
  frame's inner ring, on `OpenCell` 4 px cells, with no farther cell within two; at least
  `OpenWaterMin` 90 px (about 470 m), ranked by that distance times a seed-set 0.85-1.15. In
  rank order a spot takes the illustration of its sea placed fewest times so far (ties from a
  seed-set first), if its box (measured by drawing the glyph on a scratch canvas) sits
  `IllustrationClear` 40 px past its half diagonal inside the open water, all four corners in
  the same sea, no ink at all within `IllustrationInkGap` 8 px (waves, floes) and
  `IllustrationSpacing` 600 px (about 3.2 km) from every other one; a world with fewer such
  spots gets fewer. Scale 0.85-1.15 and mirroring by hash. `SeaGlyphs.Draw` mirrors and scales
  the points itself (`Hand`), scrapes scaled with the shape, pens kept at their width so none
  falls under `Pen`; the leviathan is not mirrored but lays its profile, barnacles and tufts the
  other way round, since its hatched east flank is the map's shade. Drawn after the floes on
  the `Illustration` layer, one record group each, which close-ups replay whole after their
  floes.
  The fog: a glyph shows or hides whole. Its box is kept in `InkRecord.m_seaMarks` and in the
  cache's header; `Paper.Fog` lifts the fog over the box and `SeaMarkMargin` 60 m round it when
  at least `SeaMarkShown` half the box is explored, and lays it there when less is, before the
  blur, so the fog's torn edge (within about 53 m of the explored edge) never crosses the glyph (left to the fog alone, a
  pass over a glyph shows half a monster). The fog's edge is torn paper, not a fade (the user's
  design): the explored cover is blurred over `RipBlur` passes and fBm and fine noise move its
  middle within that band (ragged bays and tongues with fibres), crisp over half a fog pixel,
  with a paler rim just inside (the paper's torn core) and a faint ink-coloured shadow a fog
  pixel to the south-east, the light coming from the north-west as on the map's own shading.
  The fog is 2048² (10.5 m a pixel), so close in the tear softens; a finer fog would cost a
  four times bigger upload per recording. Since the box lies far out in open water, only water
  is shown or hidden; a pass along its edge shows as a rounded bulge or bite in the explored
  strip. `AtlasMap` opens the atlas only once the fog was laid with the uploaded sheet's marks
  (`s_fogMarks`).
  Each glyph lies in a slightly darker wash of the sea's blue so it stands out a little:
  `SeaWash` draws the glyph again from its mark (kind, sheet point, mirror and scale, also kept
  in the cache's header) at 2x on a scratch canvas that keeps every erase's coverage
  (`InkCanvas.m_scraped`), so the wash follows the scraped body, not the box, ripples or foam;
  the bonemaw's bare bones would only get a glow, so the water each arch encloses down to the
  waterline is washed too (`FillArches`). Box-blurred (about 0.7 sheet px), `Gain`, mottled, and
  kept within `Reach` 3 px of the box, inside what the fog shows or hides whole. A glaze
  `Paper.s_seaWash` (72, 100, 160 at 0.22): close-ups glaze it over their water
  (`Paper.SeaGlaze`, from `InkRecord.m_seaWashes`); the sheet's ink texture cannot glaze, so
  `InkWithMips` lays it as the colour and opacity that give the same glaze over clean water
  (`SeaInk`). The ink sheet itself is unchanged.
- Ring lines at fixed distances, short double curves in staggered rows, tildes tilted with the
  coast, tildes at scored points of the shore with a scatter at sea, and a band of short tildes
  along every coast all read badly; keep groups at the bends.
- Peaks stand on real summits: local height maxima within `SummitCells` that rise
  `SummitProminence` above their surroundings, sized by prominence, the glyph placed so the
  summit lies `SummitAnchor` 0.4 of its height up its centre line, not at its apex: the glyphs
  are drawn in elevation on a plan, and with the apex on the summit the whole body hangs south
  of it, over the next biome or the coast; the top rising over the land behind reads
  naturally, as glyphs in front hide those behind. Smaller filler peaks mass between and round
  them in staggered rows, overlapping them; only a filler within `SummitCore` 0.3 of a summit's
  width from its centre, whose height overlaps the summit's, is left out when it would hide
  more than the summit's lower `SummitShown` half from in front or rise above its apex from
  behind (`NearSummit`). A fixed clearance round every summit (0.55 of the two widths) dropped
  about 80% of the fillers and left large mountain areas bare, reading as low ground.
  Hills sit on real hilltops found the same way (in the Meadows, which roll more gently and are
  mostly wood, on a finer scale: `MeadowHilltopCells` 2, `MeadowHillProminence` 4 m), their foot at least half their
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
- Filler peaks are packed in staggered rows (16 G across, 9 G down, half-cell stagger) so ranges read as
  one dark mass, as on the classic map; with that overlap the front peak hides the lower half of
  the one behind, so the shaded wedge has to stay wide near the summit. Woods are packed so
  densely that only their southern edge shows trunks, also as on the classic map.
- Swamps and hills follow the classic map: the swamp fill is short flat marsh lines with
  seed-varied reed tufts on many, on a square, fully jittered grid, gathered into patches by a
  slow noise (`MarshPatch`) with thinner bog between (still 30% of cells; a near-empty bog reads
  too sparse, even rows over the whole swamp read as messy and ruled); hills are low lopsided
  domes with hatch dashes under the eastern side, often with a smaller dome behind.
  Marks are sized to their grid cell (reeds of two to four blades, `Reeds` taking the cell) and
  a mark that would touch an inked neighbour of higher priority is left out (`MarshMark`, from
  hashes and the patch noise alone, so the sheet and every tile agree); marks bigger than their
  cell with a jitter of nearly a cell pile into knots. Marks keep off woods (`IsOpenLand`).
  Clumps follow the real water: `WorldSource.m_swampWet` (1 in a pool, 0 at 1.5 m above the
  water, from the heights before the raise, blurred) makes 65% of the patch value and the noise
  35%; reeds favour the pools' rims; marks thin over the swamp's outermost few dozen metres
  (`m_swampField`, the swamp blurred, edge at 0.5); the wash glazes wet ground and raised pools
  a little toward the water's blue (`Paper.SwampPoolGlaze`), which shows the pools and lets
  brown stains stand out. Dead trees are tall ancient trees (11-14 T, a bowed trunk on splayed
  roots, two to four seed-set branches, some forked) that scrape under every limb. Marks sit on
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
  hatch of diagonal lines (`LavaHatch`, `Lava.cs`); volcanoes have no smoke curl or lava stream;
  crags are one or two small peaks in the mountains' hand (`Peak` with `body: false`, keeping
  the burnt ground's wash), since thin notched spires read as grass tufts; the ground is one
  even cinder stipple, denser by the lava, with no ticks or fissures. Streamlines or flow strokes
  in lava fields, smoke curls, spire ridges, fissure pairs and ash-cloud mottling read as
  doodles. Crags and volcanoes keep their whole base off lava (`ClearOfLava`); the peak above
  may rise in front of a lake behind it. A crag or volcano whose foot is clear but whose base
  would reach into lava is moved instead of left out: after every glyph that fits where it was
  scattered is placed, `ShiftOffLava` tries feet in rings a tenth of its width apart out to one
  width (`LavaShiftStep`, `LavaShiftReach`), straight away from the lava (the field's slope along
  the base) first, then 30, 60 and 90 degrees either side, and takes the first that passes every
  test (`VolcanoRoom`, `CragRoom`: land, biome, shore, column, `PeakOnLand`, frame) and keeps off
  the volcanoes and crags already placed (`AshCrowded`). Whether the ground calls for the glyph
  (`VolcanoChosen`, `CragChosen`: heights, slope, the cell's roll) is decided where it was
  scattered only. A moved glyph also keeps its base `LavaShiftMargin` off lava north and south
  and its whole body off lava: feet land at a lake's edge, and a crag a pixel clear of a lake, or
  a cone rising over a U-shaped lake with the lava's wash showing through its body, reads as
  standing in it. Glyphs scattered on lava itself stay out (pushed to the rim they sit in its
  bays). No summit or filler peak touches lava, so none moves.
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
  green in the Mistlands) into a field: copses and clearings under 12 and 16 map pixels dropped,
  then blurred, edge at 0.5. Trees stand only where their foot and crown centre are inside, so
  the outer crowns alone form the edge. Do not draw edge lines: a scalloped edge after the trees
  looks fuzzy with a gap inside it, and a plain line under the trees reads as height contours.
  Ground fills and hills keep `WoodClearance` from every wood.
  Every tree's body takes its biome's wood wash strictly, as a peak's takes the mountains'
  (`BodyKind.Wood`, `Paper.WoodTint`: the biome's tint plus `WoodWash`, none extra in the
  Mountains and the Deep North), so crowns at a wood's or a biome's edge keep their colour
  where the blurred wash fades. The biome is the one at the tree's foot (`BeginWood`), packed
  into the Wood plane's byte (`InkCanvas.WoodCode`, top 3 bits; coverage in the bottom 5),
  since a cached sheet is shown before the world is read. A round crown is marked inside its
  traced outline grown `BodyGrow` (`EraseCrown`), not over its wider scraped ellipse, which
  rings it with a halo; a pine over its scraped triangle. The sheet lays it as a glaze
  (`Paper.WoodInk`, over the biome's open wash), close-ups with `Paper.WoodGlaze`. Vanilla's mask
  (`Minimap.GetMaskColor`) marks all Black Forest land as forest, Meadows by
  `WorldGenerator.InForest`, Plains where the forest factor is under 0.8 and the Mistlands by a
  smoothstep of it, so the Black Forest is one solid wood with no invented clearings. (Cells
  under water keep the biome colour but no forest, so a histogram of the mask overstates open
  land.)
  The mask marks no forest in the Deep North, yet vanilla grows woods there: every Deep North
  tree in ZoneSystem's vegetation (`_LocationList_DeepNorth` in the `d59cfac` SoftRef bundle:
  `snowfirtree`, `snowfirtree_sparse`, `pinetree_snow`, `pinetree_snow_dead`, `snowfirtree_small`,
  roughly 70-115 per zone together) is `m_inForest` with forest factor 0 to 1.15, the Meadows'
  own `InForest` limit; only 5-10 small firs a zone stand at 1 to 2. So `WorldSource.MarkNorthForest`
  fills the mask's red for Deep North land where `GetForestFactor` < 1.15
  (`GeneratorSampler.DeepNorthForest`; the factor is the same in every world), and `Woods.Build`
  and `GeneratorSampler` keep that wood below `DeepNorthPeakHeight`, the ground drawn as peaks.
  Its trees are the Black Forest's conifers (same rule, salt 24). The trees' own `maxTilt` 30 is
  not applied, as for the other biomes. `Mathf.PerlinNoise` under the factor means the offline
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
  Peaks stand on the same ground, so their trees (the conifer rule, salt 24) keep off the peaks'
  bases (`ClearOfPeakBases`): a tree whose foot is south of a peak's foot, within its height plus
  `PeakBaseGap` and within `PeakBaseWidth` of the peak's width plus its crown, would be drawn in
  front of the base and is left out; trees north of a foot are hidden behind the peak. The peaks
  are recorded (`InkRecord.AddPeak`) so close-ups test the same. The mountain wash is not
  darkened under the woods. The preview's mountain woods sit where the game's do, as no forest
  factor is involved; alt biomes only show offline when forced (`--alt`, below).
- **Alt biomes** (Valheim 1.0's per-patch variants, chosen once per world from the seed) only
  change how many trees stand in the same wood shapes (`Grove` in `WorldSource.cs`).
  `WorldSource.Capture` keeps the `WorldGenerator` only when `m_world.m_biomeData.IsReady`
  (otherwise one warning per session and no groves); `Decode` on the worker reads
  `GetBiomeSector` for every map pixel's centre (a plain read of arrays built at world load) into
  `m_grove`, only on pixels whose decoded biome is the sector's. `GroveOf` maps `AltBiome.m_name`:
  Kalhygge Black Forest `ClearCut` (no trees; `IsClearCut` keeps it out of the canvas wood and
  the samplers' wood, so ground fills and clearances treat it as open land; its stones give way
  to `Stump`s on one sheet grid, salt 35, `StumpStep` 3.8 G: a short trunk stroke with two or
  three roots (solid trunks with a sawn top are too heavy, felled logs read as axes), gathered
  loosely into stands by a slow noise (`StumpStand` 40 px, `StumpOdds` 0.5 inside them, `StumpFloor`
  0.5 of that between), as an even scatter reads as a stipple and bare ground between stands as lumps.
  Stumps are drawn once every glyph is placed (`DrawStumps`, after `DrawScarps` on the sheet and
  in tiles) and keep `StumpClear` off every glyph's footprint (`ClearOfGlyphs`), so none stands
  on a hill. Between them a light stipple, `ClearCutStipple`, and hills on its hilltops
  by the normal hill rule, so the felled wood is not a blank; the Meadows' finer hilltop search
  crowds it); Pinetree/Rock Black
  Forest, Smalltree Meadows, Drake/Fortress Mountain, Lox/Death Plains `Sparse` (`SparseKeep` 0.5
  by the cell's hash `SparseRoll`); Wolf Mountain, Trees Mistlands `Dense` (the rule's grids are
  already 90-95% full, so `ScatterTrees` gives dense spots a grid of their own, `DenseSpacing`
  0.85 of the step, salt +`DenseSalt`, every cell taken, about 1.4-1.5 times the trees) and Bog
  Swamp `Dense` (dead trees at `BogDeadTreeOdds` 0.85 instead of 0.45); all others none. Spots
  take the grove from `WorldSource.GroveAt` on the sheet and in close-ups alike, which in
  close-ups falls back to the nearest pixel of the spot's own biome, so the generator's finer
  border does not leave a fringe of plain woods. Mountain woods are mostly hidden by the peaks'
  bases, so Wolf and Drake show little there. The preview cannot compute alt biomes
  (`UnityEngine.Random`): `run.ps1 -Alt 'NAME:wx:wz:radiusMetres',...` (`--alt`, repeatable) lays
  one on its biome's cells within the radius via `SetGroves`. Untested in game, including
  whether the data is always ready when the minimap is; a sheet cached in a session without the
  data keeps plain woods while close-ups show the variants.
- Ground fills on open land stay sparse, since the wash tells biomes apart. The Deep North is
  a deliberate near-blank snowfield with rare chevrons, like the classic map's northern waste,
  not snow stars, drifts, ice shards or cracked ice. Its woods (above) are the one dense mark;
  the open snow between stays blank. Chevrons keep off the shore, off the woods (`IsOpenLand`,
  as ground fills), off any ground high enough for peaks (searched from the world's heights,
  mostly south of the mark, since a peak rises north of its foot) and off each other (a
  hash-only neighbour test like the marsh marks'), so the sheet and tiles agree. Ice floes are
  filled with the Deep North's body wash, not left showing the sea's.
- The Mistlands keep their wash. Mist banks are veils of three wavy lines (middle longest, a
  slight tilt, a break or two) with no scraped band under them (bands read as heavy padding),
  laid in the valleys and thinning with height to `MistCeiling` (the game's mist clears on the
  heights), off steep ground. Banks are glyphs (`Kind.Mist`, `PlaceMist`), sorted in with the
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
  of the summits; a fixed trapezoid on every high pixel reads as fence posts. Their bodies are
  filled as the peaks' are: scraped and marked `BodyKind.Mist`, with the peaks' `ScrapeBase` and
  base fade, so the glyph in front decides. The fill is the Mistlands' own wash as its woods have
  it (`Paper.WoodTint(Land.Mistlands)`, the same as its tree crowns), so the rock is that
  land's colour, a shade deeper than its open ground; a separate rock hue reads as off from
  the Mistlands. On the sheet it is not laid opaque as a peak's body is: the clean-paper colour on the stained,
  patinated Mistlands reads lighter and lilac there while close-ups show it deeper than the
  ground, so `Paper.MistInk` lays it as the colour and opacity that give the rock's glaze over
  the Mistlands' wash (as `SeaInk` does for the sea wash).
  Scarps cover the steep rock, which would otherwise be bare paper even inside the forest mask:
  about 40% of Mistlands land is steeper than `SpireSlope`, where trees keep off, mist keeps
  off above 1.3 of it and filler spires stand on only a third of an 18 G grid.
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
  broad, slightly ragged canopy, `HandCrown` wobble a little raised), packed on a 7 T grid;
  extra lobes behind or beside the crown look lumpy and leave thin black crescents along its
  edge, and three round crowns on a stick read as lollipops.
- Every pen line is at least `Pen` (1 px) and full strength: thinner or fainter lines average to
  grey in the ink mips at the opening zoom. The Ashlands hatch is the one deliberate grey tone.
- Zoomed in past 1.5 screen pixels per sheet pixel, close-ups come from fixed 256² tiles
  (`DetailTiles`) at one level only, at every zoom: the finest the view reaches (4x, 8x above
  1080p or with a UI scale over 1), mipmapped on the worker and faded in over 0.2 s, laid over
  the sheet (opaque, under the fog). Do not pick the level by zoom: each level draws at its own
  level of detail, so trees and marks change as the zoom crosses levels. Do not show coarser
  levels while fine ones load either: levels replacing each other look scratchy. Close-ups
  start at `DetailFrom` or once the view takes at most `MaxVisible` 400 tiles, whichever is
  later (from the viewport's screen size, so panning never toggles them): above 1080p the 8x
  level has four times the tiles of 1080p's 4x over the same view, thousands at `DetailFrom`,
  so there they start at about 2.25 of the zoom. Tiles are drawn ahead of need, one per worker (half the cores, at most 4): the view
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
  `WorldGenerator.RenderRivers`) over land only, on a scraped band. Streams run from their low
  mouth (`p0`, 26-31 m) to the source and taper. Deep North streams are not kept by vanilla and so
  are not drawn. The offline harness cannot run the generator; its previews use streams laid out
  with the same rule on the cached heights, so in-game courses differ from the previews.
- Tall glyphs test their whole column with `IsLandColumn` as well as the shore margin at their
  foot, or peaks and trees on south-facing coasts or thin spits reach into the sea.
- Thresholds are set from the Velen world's height percentiles (Ashlands land p90 22 m above
  water, Deep North p90 59 m, Mountain p50 90 m).
- The offline preview in `tools/preview/` feeds vanilla's cached minimap files
  (`worlds_local/<world>/cacheMinimap*`, gzip of Color32 or half buffers) through `WorldSource` and
  `InkPainter` by reflection; painting the whole world takes about 2.5 s. It is a separate
  project, never shipped: `Atlas.csproj` removes `tools\**` from its items and the package holds
  only the DLL. `run.ps1 -World <name> -Views ...` renders into `tools/preview/img/` (ignored);
  `samples.ps1 -Publish` refreshes the 31 samples in `screenshots/` (16-21 are views forced to biome variants with `-Alt`) (committed). Refresh them
  after visual changes. It needs Valheim's Managed DLLs (`VALHEIM_INSTALL` overrides the Steam
  path) and BepInEx's core DLLs from a Gale profile copied beside the exe, which `run.ps1` does,
  and writes `System.Console`, as `assembly_valheim` has a global `Console` class.

## In game

Nothing has been confirmed in game yet: the table hooks, opening the interactive map in nomap, the
`Minimap.IsOpen` input blocking, the view's zoom and drag, pin sprites and labels, the fog edge,
reading the 4-bit forest mask texture with `GetPixels32`, and running the world reads off the
main thread.
