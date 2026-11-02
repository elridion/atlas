# Atlas

Valheim mod for worlds without a map (`Game.m_noMap`). "Record discoveries" at a cartography table
also records a snapshot (explored map and saved pins) on the character; the map key shows that
snapshot as an ink drawing on parchment in the style of a classic fantasy-novel map. "Read map"
opens vanilla's interactive map, where pins are placed. Client-side: nothing is saved in the world.
It replaces shudnal's Nomap Printer for the user, whose printing lagged badly at the table (it
re-baked and re-uploaded an 8192² texture and threw away ~700 MB of arrays per print).

Stack, build, verification and git workflow are shared by all mods: see `../CLAUDE.md` and
`../.claude/rules/`.

## Where things are

- `src/AtlasPlugin.cs` loads translations, applies the patches and calls `AtlasMap.Update` every frame.
- `src/AtlasMap.cs` the controller: per world it loads or paints the ink sheet, lays the fog for the
  current snapshot, and opens `AtlasView` on the map key.
- `src/AtlasView.cs` the window (parchment, ink and fog `RawImage`s plus pin markers) with zoom and
  drag. Pins follow a research pass on cartographic point labels (Imhof, QGIS, Mapbox, Esri,
): the icon inked black at 26 units on a thin 2-unit parchment outline of its solid
  pixels (alpha over 200, so a soft rim does not widen it; a fixed width, since a proportional
  one swamped the hammer and the dot), names in regular weight with letter-spacing and a slim,
  slightly see-through underlay, placed by `LayoutLabels` beside the icon (right, top right,
  left, above, below, keeping the last position), bosses and traders first, hidden when nothing
  fits, recomputed on a zoom change over 3%. The user rejected a soft disc, the icon enlarged,
  red icons on labels, and shifted copies (not one colour). A UI parent draws before its
  children, hence a holder per pin.
- `src/TablePatches.cs` the `MapTable.OnRead`/`OnWrite` postfixes and the `Minimap.IsOpen` postfix.
- `src/InteractiveMap.cs` opens vanilla's large map in a nomap world.
- `src/TableSparkles.cs` golden motes over the table while the atlas updates after "Record
  discoveries", bursting and fading once it is done (vanilla's
  scribble and chime play before the atlas has caught up). A local particle system with a mote
  texture made in code on a copy of `vfx_odin_despawn`'s particle material; untested in game.
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
  once per recording. Decoding the world, painting, the wash, the fog, parchment, mips and the
  cache run on `Task.Run`. Worker code must not call Unity APIs except
  pure math (`Mathf`, `Vector2`); `Mathf.HalfToFloat` is native, hence `WorldSource.HalfToFloat`.
- **Deterministic drawing.** Glyph placement hashes the world seed, a per-rule salt and the grid
  cell (`Noise`), never a shared `System.Random`, so tuning one rule never moves another's glyphs.
  Any change to the drawing bumps `InkCache.Version`, or players keep the old cached sheet.
- **Docs follow code.** `package/README.md` keeps the table behaviour, controls and pen mark tables
  in sync with `TablePatches`, `AtlasView` and the scatter rules in `InkPainter.PlaceGlyphs` and
  `DrawBackground`.

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
  island, lake and swamp pool; only single 12 m pixels are dropped. Water joined to the world's
  edge (`MarkSea`, four-way flood fill) is the sea and gets ripples; every shore, sea or inland,
  has the same pen (a 1 px lake shore read as unexplained inland lines to the;
  the water's blue now tells lakes apart). The coast pen is 1.4 px on the sheet and 2.2 screen pixels in
  close-ups, barely heavier than the waves as on the classic map (it was 2.1 and 3.5, the
  map's heaviest line; rivers thinned with it to 1.2). Water
  is one even indigo wash (`Paper.WaterColor`; a pale-to-dark shading by depth was dropped as
  too exact for a hand-tinted map). Rivers and the coast are drawn before the
  glyphs, so crowns over a bank or shore hide the line instead of being cut by it, and trees
  keep off river courses (`m_riverMask` on the sheet, `MarkTileRivers` from the record in
  close-ups) so streams still show; land
  tints are blurred among land only, and the water's edge is anti-aliased over one pixel by the
  ground's local slope, so the blue ends at the water line (a fixed 1.5 m height ramp spilled it
  far inland over flat beaches). Streams get an 8 m band on the sheet
  (`Paper.Rivers`); rivers show by their heights, since the generator's river width includes the
  banks. Close-ups lay their own water from their fine heights over the land-only wash
  (`Paper.Wash.m_land`) and need no stream band.
- The palette (paper, ink, biome washes, water) was rechecked against pigments of the period by
  a research pass: aged calfskin paper, aged iron-gall ink, and thin glazes of green
  earth, verdigris with indigo, yellow and red ochre, folium, chalk and soot, and indigo, all
  muted earth tones. Values are estimates from Raman/XRF pigment studies of Insular and Nordic
  manuscripts, not measured colours. An earlier version blurred the coast and deleted small islands and
  lakes for looks; that was undone for accuracy, so swamps are busy by nature. The offline
  harness can overlay vanilla's own 12 m water boundary in red to check the coast. The sea is
  shaded by wave accents (`DrawWaveAccents`): groups of three to six strictly level tildes
  stacked away from the shore at chosen points, as the classic map accents its coasts.
  Points just off the shore are scored by the share of land on a disc round them
  (`ShoreScore`: high in bays, inlets and straits, low off capes, both accented; a straight
  coast scores low) and by river mouths; where the shore lies level with a row (east- or
  west-facing coasts, within `WaveAnchorReach`), the stroke starts right at the shore
  (`WaveShoreGap` off it) and runs out to sea, the reference's whole style,
  and such points score `WaveBesideBonus` more; the best get groups, kept `WaveGroupGap` apart, and
  straight coasts a lone stroke now and then. Strokes never touch land or each other (a
  sheet-only taken grid, deterministic in score order); islets under `MajorLandCells` get two
  strokes at most; the open sea is blank (far-sea marks read as birds). Close-ups replay them
  at their own 1.5 screen-pixel pen (`TileRipplePixels`), dropping strokes on the close-up's
  land. Two earlier forms were rejected: short double curves in staggered rows
  thinning out to sea, and, after a review against the classic map (`atlas-wave-review.md`),
  six rows of tildes following the coast and tilted with it ("even dumber": the reference's
  strokes are strictly horizontal). Ring lines at fixed distances from the coast came before
  them; all keep the `Ripple` layer.
- Peaks stand on real summits: local height maxima within `SummitCells` that rise
  `SummitProminence` above their surroundings, sized by prominence, the glyph placed so its apex
  is on the summit. Smaller filler peaks mass between them in staggered rows and keep clear of
  the summits. Hills sit on real hilltops found the same way.
- Glyphs are scattered on jittered grids per rule, sorted by their foot (north first) and drawn so
  that each scrapes its outline back to parchment before inking: southern glyphs hide northern ones.
- Filler peaks are packed in staggered rows (16 G across, 9 G down, half-cell stagger) so ranges read as
  one dark mass, as on the classic map; with that overlap the front peak hides the lower half of
  the one behind, so the shaded wedge has to stay wide near the summit. Woods are packed so
  densely that only their southern edge shows trunks. Both came from an art review against the
  classic map style that the user liked.
- Swamps and hills follow the user's reference snippet of the classic map: the
  swamp fill is short flat marsh lines with seed-varied reed tufts on many, on a square, fully
  jittered grid, gathered into patches by a slow noise (`MarshPatch`) with thinner bog between (still 30% of
  cells; a near-empty bog read as too sparse); even rows over the whole swamp read as messy and
  ruled to the user; hills are low
  lopsided domes with hatch dashes under the eastern side, often with a smaller dome behind.
  Marks are sized to their grid cell (reeds of two to four blades, `Reeds` taking the cell) and
  a mark that would touch an inked neighbour of higher priority is left out (`MarshMark`, from
  hashes and the patch noise alone, so the sheet and every tile agree); marks bigger than their
  cell with a jitter of nearly a cell had piled into knots (swamp review,
  `atlas-swamp-review.md`, and the). Marks keep off woods (`IsOpenLand`).
  Clumps follow the real water: `WorldSource.m_swampWet` (1 in a pool, 0 at 1.5 m above the
  water, from the heights before the raise, blurred) makes 65% of the patch value and the noise
  35%; reeds favour the pools' rims; marks thin over the swamp's outermost few dozen metres
  (`m_swampField`, the swamp blurred, edge at 0.5); the wash glazes wet ground and raised pools
  a little toward the water's blue (`Paper.SwampPoolGlaze`), which shows the pools and lets
  brown stains stand out. Dead trees are tall ancient trees (11-14 T, a bowed trunk on splayed
  roots, two to four seed-set branches, some forked) that scrape under every limb. Marks sit on
  a cell of 1.5 `MarshStep` and close-ups draw them at 0.75 of the pen: smaller cells at the
  tile's pen closed the tufts up into lumps. A swamp's water is raised to
  `WorldSource.SwampFloor` above the water line, in the decoded minimap and in
  `GeneratorSampler`, so swamps are one marsh with no pools (inland pools
  shown as water were unwanted), except deep water (over `SwampShallow` 1.5 m) joined to the
  sea (`WorldSource.MarkSwampSea`, flooded from the map's edge through the water that stays
  water): raising that too turned the flooded fringe of swamps beside the sea into land (on
  Velen 3,889 raised cells touched other water).
- Broadleaf, plains and giant-tree crowns are hand-traced (`HandCrown`: a closed loop whose
  radius wobbles by seed, with a slight overlap), with seed-varied size, aspect, trunk length,
  lean and shade span; perfect ellipses read as stamped. Pines got the same
  treatment at the user's ask: varied height and width, a leaning tip, a bowed west flank, an
  uneven base, trunk and shade split by seed.
- The Ashlands were made sober after the user found them "doodly" (the map is
  meant to read as a serious survey): lava fields are toned with an even engraved hatch of
  diagonal lines (`LavaHatch`, `Lava.cs`) instead of dashed streamlines; volcanoes lost their
  smoke curl and lava stream; crags are one or two small peaks in the mountains' hand (`Peak`
  with `body: false`, keeping the burnt ground's wash) instead of thin notched spires that read
  as grass tufts; the ground is one even cinder stipple, denser by the lava, with no ticks or
  fissures. Crags and volcanoes keep their whole base off lava (`ClearOfLava`); the peak above
  may rise in front of a lake behind it (user: "peaks yes, bases not"). Before that, a second pass from a Fable art review (4/10) had made them: volcanoes at mountain scale in
  the peaks' hand (concave flanks, notched crater, small wedge and hatch, S-curve smoke, a lava
  stream), kept a cone's width apart and off the coast; lava fields from 110 cells up with
  streamlines along each field's contour and a darker vermilion glaze clipped to its edge after
  the blur; a burnt-sienna wash mottled with grey ash clouds; crag ridges of 3-6 notched,
  hatched spires kept off lava; fissure pairs; a cinder stipple of two sizes and ticks, denser by
  the lava. Before that, after the user found them bad: a sparse cinder
  stipple instead of a diagonal hatch, lava fields from `Woods.BuildLava` (vanilla's patchy lava
  mask, kept from 40 cells and blurred 4 passes) outlined and filled with flow strokes on their
  own `Lava` layer (`Lava.cs`, sheet only, replayed by close-ups) over a vermilion wash, crag
  clusters (`Crag`) on steep ground, and more volcanoes. The old lava-crack glyph is gone.
- Biome washes follow the ground exactly, but every peak's body is filled strictly with the
  mountains' wash, or the Deep North's for peaks standing there: `Peak`
  erases its outline (a fan of slightly grown triangles from a hub low in its middle, since a
  convex hull let the fill spill past sags and notches) with `InkCanvas.m_markBody`, which
  writes one of two body masks (`BodyKind`; any later erase clears both, so the glyph in front
  decides). The body stays fully the peak's wash; below the foot a hairline skirt
  (`InkCanvas.Tint`, an `Op.Tint` in the record, `PeakSkirt` 2 sheet px) raises the mask
  without touching the ink, from `PeakBleed` to nothing, so the base is not cut hard. Fading the
  body out at the base was the wrong way round (the base leads into the other
  biome, not the other biome into the mountain), and a skirt 30% of the peak deep (`cc994a8`)
  overwrote the outline before the body was scraped, so bodies became trapezoids: peaks read as
  holes and peaks behind showed through the ones in front. The skirt is now laid after the
  body. Mountain ground keeps the mountain wash even where no peak is drawn (
  the biome must still read as mountains); giving it the neighbours' wash instead was tried and
  reverted the same day. The sheet is ink then the two masks (`InkPainter.Paint`, cached together); `Paper.InkWithMips` fills bodies with
  `PeakColor` under the ink, and close-ups glaze `Paper.PeakGlaze` over the stained parchment,
  replaying the flag from the record. A 300 m carry of the mountain tint north, tried first,
  spilled it over plains with no peaks. Peaks
  are tested for land at both base ends and both flanks (`PeakOnLand`), not only their centre,
  so coastal peaks do not hang over the sea.
- Peaks vary by seed, after the user asked for mountains like the classic map's: a
  jagged ridge line (a plain crag, a shoulder on the east, or a twin summit on the west), the
  east face a solid wedge for the top `PeakWedge` of the flank and slanting hatch strokes below,
  some with a spur down the lit face; then, on the user's ask for more variation, proportions
  from broad and low to tall and narrow (`PeakHeightOf`, shared with summit placement, which sets
  the foot so the apex lands on the summit), blunt flat tops, a snow line on tall peaks, and
  three hatching hands (slanting, level, steep; dense or open). The body is erased as the
  outline's convex hull. Broadleaf and plains crowns have
  their eastern part filled (`Shade`, `CrownShade`), as the pines fill their eastern half.
- The parchment carries stains (`Paper.Stains`, user's request), reworked the same
  day from a research pass (Quilez domain warping, coffee-ring physics, Curtis et al. watercolour
  glazing, conservation notes on foxing) after the user asked for stains that look more natural
  and carry into the painted parts. Stains are optical density: the paper is multiplied by
  `pow(tint, D)` per channel (brown for blots, redder for foxing), densities add and are softly
  capped. Blots (old water stains with re-wetting rims, drop rings) are domain-warped, turned
  and stretched, with a tide line that rises sharply at the edge and fades inward, at least
  1.5 paper pixels wide. Foxing comes in Gaussian clusters, biased toward the sheet's edge, plus
  lone spots, each a ragged core with a soft halo. The biome wash and the water's blue are glazes
  (`Paper.Filter`: the tint relative to clean paper multiplies the paper), so stains show through
  them; on clean paper the colours are as before. Water stops at the frame; beyond it is bare
  margin. The fog shares the stained parchment, so stains run on unbroken. After the user found
  the map "a bit too clean", coverage was doubled and an uneven patina (`Paper.Age`, warped fBm
  blotches, worked out every 4 paper pixels) laid over the whole sheet. The parchment is made
  once per world (`AtlasMap.Parchment`, shared by fog and wash) with its blots and rows in
  parallel: about 1.4 s offline, against 4.3 s single-threaded.
- A faint watercolour wash per biome lies under the ink (`Paper.Washed`, darker in woods, pale on
  water, blurred so it bleeds softly across edges); it replaced the earlier pure-black-ink rule
  when the user found the map hard to read. The fog keeps the bare parchment, so
  unexplored land shows no colour. The paper is built from the decoded world each session, not
  cached; it is ready about a second after the world's minimap.
- Woods are shapes, not scattered trees (forest and open land must not be "a
  weird mix"). `Woods.Build` turns vanilla's forest mask (red in Meadows, Black Forest, Plains;
  green in the Mistlands) into a field: copses and clearings under 12 and 16 map pixels dropped,
  then blurred, edge at 0.5. Trees stand only where their foot and crown centre are inside, so
  the outer crowns alone form the edge. Edge lines were tried and dropped: a scalloped edge after
  the trees looked fuzzy with a gap inside it, and a plain line under the trees read as height
  contours to the user (removed). Ground
  fills and hills keep `WoodClearance` from every wood. Vanilla's mask (`Minimap.GetMaskColor`)
  marks all Black Forest land as forest, Meadows by `WorldGenerator.InForest`, Plains where the
  forest factor is under 0.8 and the Mistlands by a smoothstep of it, so the Black Forest is one
  solid wood; its old noise clearings were invented and are gone. (Cells under water keep the
  biome colour but no forest, which once made the biome look 60% open in a histogram.)
- Ground fills on open land stay sparse, since the wash now tells biomes apart. The Deep North is
  a deliberate near-blank snowfield with rare chevrons, like the classic map's northern waste:
  the user chose it over snow stars, drifts, ice shards and cracked ice. The Mistlands' mist is
  drawn after the glyphs (over the woods), since a fill underneath was scraped away by its dense
  giant trees.
- Every pen line is at least `Pen` (1 px) and full strength: thinner or fainter lines average to
  grey in the ink mips at the opening zoom. The Ashlands hatch is the one deliberate grey tone.
- Zoomed in past 1.5 screen pixels per sheet pixel, close-ups come from fixed 256² tiles
  (`DetailTiles`) at one level only, the finest the view reaches (4x, 8x with a UI scale over
  1), mipmapped on the worker and faded in over 0.2 s, laid over the sheet (opaque, under the
  fog). A pyramid of 2x, 4x and 8x tiles, coarse ones shown while fine ones were missing, looked
  scratchy to the user as levels replaced each other. Tiles are drawn ahead of
  need, one per worker (half the cores, at most 4): the view from the pointer outward and a ring
  of one tile round it; zoomed out, up to 48 tiles round the pointer, where a zoom lands. At most
  3 textures are uploaded a frame and 320 kept (about 110 MB with mips). Tiles sample their ground `TileMargin` beyond their edge and scatter glyphs from cells
  `GlyphReach` beyond it, so neighbours draw the same glyphs and margins and seams do not show.
  The sheet's non-tree glyphs are recorded as groups with their feet (`InkRecord.BeginGroup`),
  and a tile sorts them in with its own trees and draws all from north to south; replayed over
  all the trees, spires behind trees covered them in the Mistlands.
  This replaced one big tile for the whole view, which the user found slow to catch up even with
  the sample cache. The
  ground fills, trees and coast are drawn afresh (`InkPainter.PaintTile`) from a
  `TileGrid` sampled every few tile pixels; everything else is replayed from the `InkRecord`
  (every pen call of the painting with its `InkLayer`, about 720k calls, 20 MB, indexed in 64 px
  cells). Because only
  painting yields the record, the world is painted once per session in the background even when
  the sheet came from the disk cache. In game the plain sheet looked too pixelated at
  4x zoom, and the user then found close zoom lacking detail, which is what led to this.
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
  were true to life but read as a uniform bubble texture. Scatter grids are anchored to the sheet
  and hashed per level, level 1 as the sheet, so glyphs do not move as the view pans.
- Rivers and streams come from `WorldGenerator.GetRivers()`/`GetStreams()`, copied in
  `WorldSource.Capture` and inked along vanilla's own sine curve (`RiverPath.PointAt`, from
  `WorldGenerator.RenderRivers`) over land only, on a scraped band. Streams run from their low
  mouth (`p0`, 26-31 m) to the source and taper. Deep North streams are not kept by vanilla and so
  are not drawn. The offline harness cannot run the generator; its previews use streams laid out
  with the same rule on the cached heights, so in-game courses differ from the previews.
- Tall glyphs test their whole column with `IsLandColumn` as well as the shore margin at their
  foot, or peaks and trees on south-facing coasts or thin spits reach into the sea.
- Thresholds were set from the Velen world's height percentiles (Ashlands land p90 22 m above
  water, Deep North p90 59 m, Mountain p50 90 m).
- An offline preview harness (not in the repo) fed vanilla's cached minimap files
  (`worlds_local/<world>/cacheMinimap*`, gzip of Color32 or half buffers) through `WorldSource` and
  `InkPainter` by reflection; painting the whole world took about 2.5 s.

## In game

Nothing has been confirmed in game yet: the table hooks, opening the interactive map in nomap, the
`Minimap.IsOpen` input blocking, the view's zoom and drag, pin sprites and labels, the fog edge,
reading the 4-bit forest mask texture with `GetPixels32`, and running the world reads off the
main thread.
