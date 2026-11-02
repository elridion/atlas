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
- `src/AtlasView.cs` the window (parchment, ink and fog `RawImage`s plus pin markers) with zoom and drag.
- `src/TablePatches.cs` the `MapTable.OnRead`/`OnWrite` postfixes and the `Minimap.IsOpen` postfix.
- `src/InteractiveMap.cs` opens vanilla's large map in a nomap world.
- `src/Snapshot.cs` the recorded exploration and pins, in `Player.m_customData`.
- `src/Paper.cs` parchment, fog and the ink texture's pixel data (mip chain).
- `src/Ink/`: `WorldSource.cs` vanilla minimap data, `InkPainter.cs` the drawing pipeline and
  scatter rules, `Glyphs.cs` the pen glyphs, `InkCanvas.cs` the anti-aliased rasterizer,
  `DistanceField.cs` distance transform and marching squares, `Noise.cs` hashes and value noise,
  `InkCache.cs` the per-world disk cache.
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
  (`LoadRawTextureData`, made unreadable after), the 1024² fog once per recording. Painting, the
  fog, parchment, mips and the cache run on `Task.Run`. Worker code must not call Unity APIs except
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
- The coastline is traced (marching squares) on a cleaned copy of the heights: swamp water
  shallower than 3 m counts as land, a 3x3 blur, and islands under 12 or enclosed lakes under 16
  map pixels removed. Without that, swamps and rocky shores became black thickets (seen in the
  offline preview). Ripples are contours of the distance to land, only where water is
  open (at least `OpenWidth` wide), so rivers keep just their banks.
- Glyphs are scattered on jittered grids per rule, sorted by their foot (north first) and drawn so
  that each scrapes its outline back to parchment before inking: southern glyphs hide northern ones.
- Peaks are packed in staggered rows (16 G across, 9 G down, half-cell stagger) so ranges read as
  one dark mass, as on the classic map; with that overlap the front peak hides the lower half of
  the one behind, so the shaded wedge has to stay wide near the summit. Forests are packed so
  densely that only their southern edge shows trunks. Both came from an art review against the
  classic map style that the user liked.
- Every biome except the mountains has its own ground fill so it can be told apart without trees
 . The Deep North is a deliberate near-blank snowfield with rare
  chevrons, like the classic map's northern waste: the user chose it over snow stars, drifts,
  ice shards and cracked ice. The Mistlands' mist is drawn after the glyphs (over the woods),
  since a fill underneath was scraped away by its dense giant trees.
- Every pen line is at least `Pen` (1 px) and full strength: thinner or fainter lines average to
  grey in the ink mips at the opening zoom. The Ashlands hatch is the one deliberate grey tone.
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
