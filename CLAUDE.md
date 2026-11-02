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
  drag. Pins are inked black (the user likes that) with a crisp parchment outline of even width:
  12 parchment copies of the icon shifted 3 units in a ring behind it. Names are bold with a
  hard parchment TextMeshPro underlay on a copy of the font material. Rejected by the user on
: a soft parchment disc, the icon enlarged behind itself (outlines wide parts far,
  thin parts barely), and red icons on parchment labels. Each pin is a holder whose first
  children are the outline copies, since a UI parent draws before its children.
- `src/TablePatches.cs` the `MapTable.OnRead`/`OnWrite` postfixes and the `Minimap.IsOpen` postfix.
- `src/InteractiveMap.cs` opens vanilla's large map in a nomap world.
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
  edge (`MarkSea`, four-way flood fill) is the sea: heavy coast and ripples. Inland water gets a
  1 px shore and no ripples. An earlier version blurred the coast and deleted small islands and
  lakes for looks; that was undone for accuracy, so swamps are busy by nature. The offline
  harness can overlay vanilla's own 12 m water boundary in red to check the coast. Ripples are
  contours of the distance to land, on the sea only, where it is wide enough for each ring and
  around land big enough for it.
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
- The parchment carries stains (`Paper.Stains`, user's request): large water stains,
  dried drop rings with a tide line and foxing spots, ragged by noise, placed by hash of the seed.
  They are world-anchored on the 2048 paper, and numerous enough that the explored-bounds view
  always shows some; the fog shares the stained parchment, so they run on unbroken.
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
  This replaced one big tile for the whole view, which the user found slow to catch up even with
  the sample cache. The
  ground fills, trees and coast are drawn afresh (`InkPainter.PaintTile`) from a
  `TileGrid` sampled every few tile pixels; everything else is replayed from the `InkRecord`
  (every pen call of the painting with its `InkLayer`, about 720k calls, 20 MB, indexed in 64 px
  cells, plus the sheet's sea pixels as bits, which tell sea coast from lake shore). Because only
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
