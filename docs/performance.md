# Atlas performance work

Record of the work to make the atlas ready sooner after joining, keep the game's frames smooth
and make pin edits show quickly: what was measured, what was changed, what was ruled out and
what is still open. Numbers marked *measured* were measured, *estimated* ones were not.
Offline numbers come from the preview tool on the world Velen on a Ryzen 7800X3D (8 cores, 16
threads, so `Workers.Count` is 4); in-game numbers from timing builds on the same machine.

## How it is measured

- **Timing builds** (`tools/timing/`): `Apply.ps1` copies `Timing.cs` into `src` and inserts
  `Timing` calls by exact text; build with `-p:DefineConstants=TRACE%3BATLAS_TIMING`, rename the
  zip to `-timing`, then `git checkout -- .` and delete `src/Timing.cs`. Never commit the result.
  Each log line reads `[atlas timing] <seconds since joining> gc <collections> <stage>`: stages
  reached, main-thread cost of each upload, when fog, ink and paint jobs start on a worker, every
  repaint and tick redraw, a per-step breakdown of every frame over 16 ms, and the atlas's
  per-frame cost until the first opening. Every paint also logs each stage of `InkPainter.Run`
  (`paint stage <name>: <ms>, raster <ms>`) and a total: raster is the time `InkCanvas` spent
  filling pixels, the rest is placement and other decisions. The raster timers wrap about a
  million calls a paint and add an estimated 0.5 s, mostly to raster. A `MISSING` line from
  `Apply.ps1` means the code moved and that call needs a new anchor.
- **Byte checks**: every change that should not alter the drawing was compared offline before it
  was committed: SHA256 of all sheet planes and a deep hash of the `InkRecord`, close-up tiles and
  banners, under .NET and under Valheim's own Mono (the preview's code run inside
  `mono-2.0-bdwgc.dll` from a small host program). A change that moves any pixel bumps
  `InkCache.Version`; none of the work below needed it.

## Results

Velen with its cache, successive timing builds:

| | First | Second | Third | Fourth | Fifth (`b0a26e1`) |
| --- | --- | --- | --- | --- | --- |
| Atlas can open | 13.4 s | 8.8 s | 6.4 s | 5.2 s | 3.4 s |
| Background repaint | 16.5 s | 16.5 s | 9.2 s | 9.2 s | 9.2 s |
| Close-ups ready | 21.6 s | 27.8 s | 18.2 s | 16.9 s | 15.2 s |
| Worst main-thread frame | 102 ms | 37 ms | 82 ms | 50 ms | 42 ms |
| Record that ticks pins, to ink laid | - | - | stalled | 1.3 s | - |
| Pin change (repaint), to ink laid | - | - | - | - | 7.5 s |

In the fifth run the atlas's work started 0.02 s after joining, before the character arrived (1.9 s); the fog started 0.1 s after the cached sheet, read from the parchment cache, and took 0.37 s; each pin change, made on the table's map and taken when it was closed (confirmed by the user), was painted in about 5.8 s and its ink laid once. On the second world (cached) the atlas could open at 4.4 s, against 8.6 s in the fourth run. The 77 ms frame on the character's arrival did not recur.

Without a usable cache the atlas waits for the first full paint (about 11 s in game before the
latest changes). The atlas's own main-thread cost while loading is about 0.2-0.5 ms a frame.

## What was changed

| Commit | Change | Effect |
| --- | --- | --- |
| `2d2a899` | Washed paper cached beside the sheet; reading the world, its wash and the close-up repaint wait until the atlas can open; name banners drawn on the workers | Ready 13.4 -> 8.8 s (*measured in game*) |
| `05d23a6` | A paint whose pins changed meanwhile is cancelled; outdated results dropped | Records in a row cost one paint |
| `24d900b` | Ticking pins redraws only those pins (`InkPainter.Recheck`) and drops only their close-ups | About 15 ms instead of a full paint (*measured*, byte-identical) |
| `70f2c78` | Pin-independent groundwork kept for the session (about 133 MB); `BuildFields`, `TraceWaterLines` parallel | Repaint for new pins 6.1 -> 2.6 s offline (byte-identical) |
| `f339ac9` | A fog re-lay keeps the close-ups it does not affect (`FogChange`) | About 87-89 % kept after a 200 m newly explored area (*measured*, kept tiles byte-identical) |
| `6d8adc3` | Fog skips its noise deep in the band and runs in parallel; the ink leaves 32² blocks under opaque fog clear, builds mips in place and reuses its buffer (about 89 MB kept) | Fog 0.8 -> 0.1 s, ink 1.2 -> 0.13 s offline; level 0 identical wherever the fog shows anything, coarse mips near the edge differ, but side-by-side renders ([before](images/fogmips-before.png), [after](images/fogmips-after.png)) showed no visible difference |
| `eed6497` | Priority lane in `Workers` for the opening's jobs; close-ups wholly under opaque fog not drawn | About 70 % of 4x tiles skipped on a lightly explored world |
| `d46d189` | Rasterizer evaluates only the pixels a shape can touch | Banners under Mono 0.16-5 s -> 5-17 ms; repaint under Mono 4.3 -> 2.6 s; close-ups 15-25 % faster (byte-identical) |
| `072c108` | Close-ups drawn ahead: view first, then where a pan carries the view and two wheel steps out; the reopening view drawn while closed | Simulated: fully drawn frames while panning about 25 -> 60 % |
| `618863a` | `Workers` queues a loop's parts only for idle workers | Fixes stalls and a deadlock under Mono (reproduced in Valheim's Mono) |
| `ced7325` | Parchment cached; snapshot decoded on a worker; the first paint without a cache in the priority lane | No parchment wait before the fog; no 95 ms frame reading the snapshot |
| `c5e2a20` | Closing the table's map takes the current pins into the snapshot | Pin edits reach the atlas without a record |
| `b0a26e1` | Cached files start while the world loads; the ink is laid once per record; at most one upload or minimap capture per frame | Fifth timing run: ready at 3.4 s; work starts before the character arrives; one ink lay per change |
| `3df66ec` | The pin-free start of a paint (everything before the dead trees) kept with the groundwork, about 24 MB | Repaint for new pins 2.6 -> 1.2 s offline (byte-identical); about 2.7 s of the 6.3 s in game by the sixth run's stages, not yet measured in game |
| `40f8a54` | Swamp dead trees give way to pins by cover as trees do instead of being refused a place, so no glyph placement reads the pins and the kept start covers all of it | Repaint 1.2 -> 1.1 s offline (byte-identical); dead trees near pins drawn differently (`InkCache.Version` 3) |
| `0fe6c5a` | After a pin change the fog's band is kept and only the new sheet's marks are judged on it (`Paper.Redecide`); the ink is laid again only over the 32² blocks whose inputs changed, coarse levels from a kept premultiplied level 5 (`Paper.LayInk`); the changed 256² tiles go to the GPU as one small packed upload and `Graphics.CopyTexture` (`InkPatch`), the whole texture as before if that is not supported or fails | Offline (*measured*, Velen): fog 65-130 ms -> about 8 ms, ink 125-130 ms -> 10-20 ms (4-116 of 16384 blocks, 1-21 tiles), byte-identical to a fresh fog and a whole lay for pins removed, moved and ticked, more explored, Glimpse on and off; not checked under Mono; the GPU copy untested in game |
| `0aca2ee` | A repaint runs the painter from the kept start on a record-only canvas and draws again only the 64 px cells whose calls changed (`InkPainter.Repaint`), on windows of the sheet; the tick re-check uses windows too | Repaint 1.1 -> 0.73 s offline (*measured*, sheet and record byte-identical to fresh paints under .NET for three pin sets, not checked under Mono); 7-20 cell rectangles (41-135 thousand pixels) drawn for one pin; not yet measured in game |
| `94478fb` | The waves' layout and the sea marks' candidates kept with the kept start; each paint keeps its sea mark placement with a hash of the calls over every window it tested for ink (`SeaPlan`, `InkRecord.WindowHashes`), and the next paint draws it again unless a window's calls changed (about 220 KB kept per world, the marks' 1.2 MB of washes shared with the sheet's record) | Repaint 0.70-0.80 -> 0.39-0.54 s offline (*measured*, Velen, .NET): waves 195 -> 20 ms, sea marks 170 -> 22 ms when the placement holds, about 180 ms when a pin inks a tested window (a pin on a figure or a stand-in); sheet and record byte-identical to fresh paints for repaints and kept-start paints between pin sets A, A less one, and none (start island, swamp and Mistlands, a pin on a serpent coil, on a leaping fish, on open water); not checked under Mono, not yet measured in game |
| `d0bbd55` | After a pin change the close-ups the change cannot reach are kept (`RecordChange`: the replayed calls, groups and feet per cell, the changed groups whole, the lists a tile reads, the changed pins against the area a tile scatters its trees over), and the fog's new decisions are compared mark by mark across the two sheets (`FogChange`); before, every tile was dropped twice | Offline (*measured*, Velen, .NET, kept tiles byte-identical to fresh ones): of the 4x and 2x tiles round a changed pin, 87-95 % kept, but nearly all of those lay under opaque fog (no land explored), which is never drawn; of the drawn tiles near a changed pin (6x6 4x and 4x4 2x round it) only 21-51 % were kept (*measured* later with land explored round the pins), and in game 6-10 of about 30 (start island 383-386 of 432 per pin; swamp and Mistlands 440-469 of 480-494; a pin on a sea figure 392-398 of 432; two changes before one fog 464 of 534 and 813 of 878); comparing the records 9-30 ms on the paint's worker; most dropped tiles would have come out the same; not yet measured in game |
| `600a65c` | A pin change drops only the close-ups the trees of its pin's chain can reach (`RecordChange.PinAreas`: the boxes of the pins chained to a changed one through trees that can meet both, `TilePinLink`, or through what its sheet box changes before pins limit trees, `TilePinCascade`, grown by `TilePinReach` and scaled by level of detail), instead of every tile whose fog reach met the pin's 44 px influence | Offline (*measured*, Velen, .NET, kept tiles byte-identical to fresh ones, land explored round the pins): of the drawn tiles near a changed pin (6x6 4x and 4x4 2x round it), 45-65 % kept instead of 21-51 % (start island 46-57 %, swamp and Mistlands 45-64 %, a pin at sea 50-56 %, seven pins within 100 m in a wood 51-65 %; pins removed, moved 60 m and 20 m, added, two changes before one fog); of a whole 24x14 view 74-91 % drawn kept instead of 65-84 %. The pin rule now drops at most 6 tiles past the others; the rest go by the glyph footprint read (a pin's sheet box is one, read `TileGlyphReach` 82 px out), and about half to nine in ten of those would have come out the same; not yet measured in game |
| `6925b92` | The first paint (and a full paint after a fallback) decides everything on a record-only canvas, then replays the whole sealed record into the sheet in 256² tiles on the workers (`InkPainter.Rasterize`); the kept start keeps no canvas planes (no painter reads them now), about 16 MB less | Offline (*measured*, Velen, .NET, 4 workers): first paint 3.69-3.78 -> 3.41-3.55 s (record-only part 3.15-3.22 s, tiled replay 0.29 s, which replays about 0.6 s of single-thread raster), a paint from the kept start 0.86-0.91 -> 0.64-0.70 s; sea marks 99 -> about 200 ms on the first paint, as the ink tests now replay the record (`InkIn`); sheet (all 6 planes, SHA256) and record (deep hash) byte-identical to the live paint for the start island, swamp and Mistlands and sea pin sets and two subsets each; not checked under Mono, not yet measured in game |
| `538428e` | A change (pins, ticks, fog) keeps the drawn close-ups it reaches in or near the view on screen, stale, and each one's redraw swaps its texture in place; a stale tile waits for the fog's judgement of a new record's marks (at most 3 s) instead of being drawn before and again after it; uploads take in-view missing tiles, then in-view stale ones; closed, stale tiles of the view and ring the atlas opens on use every worker, up to 48 a change (`DetailTiles.CatchUpMost`) | Zoomed in, a pin change no longer shows the blurry sheet while its tiles are redrawn, and those tiles are drawn once, not twice; opening after pins edited at a table should no longer show a blurry patch for about 2 s. No pixel changes (offline harnesses unchanged); the tile pyramid cannot run offline, so nothing here is measured; the timing build logs tiles kept stale per change, when the last stale tile in view was swapped and the closed catch-up |
| `48475e7` | A record whose explored land equals that of the snapshot the last whole fog was laid for keeps the fog (`Snapshot.SameExplored`: size, pixel size and every bit, about 1 ms on the main thread offline); glimpses and the Glimpse setting already had triggers of their own | A record that explored nothing new skips about 0.5 s of fog on a worker and an 11-24 ms upload frame (*from the last timing run*); new pins only judge their marks on the kept band as before. Comparison checked offline; not yet measured in game (the timing build logs `fog kept: inputs unchanged`) |
| `686c072` | The last fog band and laid ink are kept on disk per world (`LaidCache`, `.fog` 12 MB and `.laid` 27-85 MB, raw), each under a SHA-256 key of its inputs (band: explored bits, glimpses, `Glimpse`, seed, version; ink: the band's key, the sheet's pins key, sea marks, mark counts); read while the world loads, the first whole fog and the first ink lay take them when the keys match (`Paper.FogOfBand`, `Paper.LoadedInk`), else lay as before; written on a worker after a fog or ink with a new key is laid | Offline (*measured*, Velen, start island; .NET / Valheim's Mono): fog 100-130 / 190-200 ms laid against 25-35 / about 40 ms from disk, ink 170-220 / 220-245 ms laid against 23-25 / about 40 ms from disk; byte-identical to laid ones, and every later path (pin change region lay and `Redecide`, `FogChange`, a tick, more explored) the same from either, Glimpse on and off, .NET and Mono. In game the fog and ink took about 1.15 s before the atlas could open (fog 0.50 s, ink 0.65 s, last timing run: open at 3.44 s), so it should open about 1 s sooner when the keys match (*estimated*); not yet measured in game (the timing build logs `atlas can open (fog ..., ink ...)`) |

## Where a paint's time goes

Sixth timing run (`bdf5d1d`, Velen, no usable cached sheet), *measured* in game. The first paint
after joining took 9.5 s (2.6 s of it raster); a repaint for a pin change, with the groundwork
kept, 6.3 s (2.6 s raster):

| Stages | First paint | Pin repaint | Raster | Reads pins |
| --- | --- | --- | --- | --- |
| Groundwork (coast heights, masks, distance fields) | 1754 ms | 19 ms | 0 | no |
| Mist, background fills, lava, rivers, coast | 1039 ms | 1010 ms | 440 ms | no |
| Placement: summits and hills, massifs | 339 ms | 358 ms | 0 | no |
| Placement: trees | 1223 ms | 1190 ms | 0 | no |
| Placement: dead trees, spires, thorns and floes | 136 ms | 133 ms | 0 | only dead trees (through `Add`) |
| Pin block (`ClearOfMarks`, `OrderMarks`, cover limits) | 49 ms | 40 ms | 0-5 ms | yes |
| Fills after placement (scarps, stumps, boulders, meadow grass, woodless ground) | 580 ms | 554 ms | 4 ms | locally (glyph footprints) |
| Glyphs inked (sort included) | 1957 ms | 1928 ms | 1820 ms | yes |
| Water lines traced (waterwork) | 1339 ms | 12 ms | 0 | no |
| Waves | 485 ms | 456 ms | 160 ms | layout no, ink under pins yes |
| Floes, illustrations, frame | 486 ms | 468 ms | 152 ms | illustrations read ink (`InkNear`) |
| Record sealed, planes copied | 128 ms | 99 ms | 0 | |

So on a pin change 59 % is decisions, not pixels, and about 2.7 s (everything up to and
including placement, less the dead trees) neither reads pins nor changes for a world. Tree
placement alone is 1.2 s. In the same run the ink upload took 43-55 ms of one frame, the first
fog upload 24 ms and the first opening (`Show`) 33 ms; after the repaint the fog took about
0.5 s and the ink 1.0 s on the workers.

Seventh timing run (`1b82a64`, 0.1.4 with the kept start and dead trees giving way as trees;
Velen, no usable cache after the version bump), *measured*: three repaints for pin changes took
3.69-3.72 s against 6.29 s, taking the kept start 13-24 ms; a pin change took 5.3 s from the
paint's start to the ink laid (7.5 s in the fifth run). What a repaint still spends: glyphs inked
2.0 s (1.87 s raster), fills after placement 0.41 s, waves 0.46 s, illustrations 0.43 s, record
sealed 0.07 s. The first paint was unchanged at 9.8 s; the minimap capture took 49 ms of one frame.

Offline after `0aca2ee` (Velen, start island pins, *measured* with temporary stage timers): the
record-only repaint takes about 0.73 s against 1.1 s for a full repaint from the kept start.
Fills after placement 165 ms, glyphs recorded (sort included) 80 ms (530 ms inked), waves 205 ms
(230 ms inked), illustrations 173 ms (106 ms inked: `InkNear` replays the record there), record
sealed 30 ms, cell hashes 37 ms (the old record's are kept with it), changed cells drawn 15 ms.
What is left is decisions, not pixels; the waves' layout does not read the pins and could be
kept with the groundwork (about 0.2 s offline).

Offline after `94478fb` (Velen, *measured* with temporary stage timers, .NET): with the waves'
layout and the sea mark placement kept, a repaint takes 0.39-0.54 s (0.70-0.80 s before, same
harness). Waves 195-217 ms -> 19-36 ms (inking the 4341 kept strokes into the record); sea marks
169-187 ms -> 22-24 ms when the kept placement holds (hashing the 384 tested windows), 176-206
ms when a pin inks a tested window and the marks are placed again. A pin on open water away
from every tested window, and pins on land, keep the placement. A whole paint from the kept
start went 1.09-1.13 s -> 0.82-0.87 s; the first paint's sea stage grew 99 -> 118-162 ms by
keeping its plan.

Eighth timing run (`310de73`, the cell repaint; Velen with its cache), *measured*: the atlas
could open at 3.18 s. Three pin changes took the cell repaint, none fell back: 2.02-2.12 s
against 3.7 s, redrawing 15-34 of 4096 cells (6-19 rectangles) in 36-63 ms. A pin change took
3.7 s from the repaint's start to the ink laid (5.3 s in the seventh run), of which the fog
(0.55 s) and the ink lay (1.0 s) after it are now nearly half. What the repaint still spends:
illustrations 0.69-0.76 s (up from 0.43 s: under Mono `InkRecord.InkIn`'s scratch replays cost
more than the ink they replace), fills after placement 0.56 s, waves 0.30 s, cells compared
0.10 s, or 0.21 s when the old record's hashes are not cached yet (the first repaint, and the
first after a tick re-check, whose record is a copy), record sealed
0.07 s, glyphs recorded 0.08-0.10 s. The minimap capture took 61 ms of one frame and the first
opening 41 ms.

Offline after `6925b92` (Velen, *measured* with temporary timers, .NET, 4 workers): the first
paint records 3.15-3.22 s and replays its record into the sheet in 0.29 s, 3.41-3.55 s in all
against 3.69-3.78 s painted live (same harness, nine paints a pin set). The live paint's raster
was about 0.55 s of it; replaying the whole record on one thread takes about 0.62 s, so the 256
tiles run about 2.1 times as fast on 4 workers (calls crossing a tile's edge are drawn once per
tile, and the cell hashes, which the next repaint would otherwise work out, are made first). By
that ratio the 2.6 s of raster in game would take about 1.2 s (*estimated*), less what the sea
marks' ink tests now cost by replaying the record (about +0.1 s offline, +0.3 s under Mono by
the eighth run). The next timing build logs both parts (`paint: record-only part`, `paint: whole
record replayed`). 256 px tiles beat 128 (0.32 s) and 512 (0.32-0.35 s).

Offline after `0fe6c5a` (Velen, *measured* with a temporary check in the preview, .NET): the
fog's marks judged again on the kept band took 7-8 ms against 64-131 ms for a fresh fog, and
the ink laid again over the changed blocks 9-20 ms against 121-134 ms for a whole lay (both
byte-identical). Removing one of the start island's eleven pins changed 116 blocks in 21 tiles
(20 repaint rectangles, the sort's reordering included), moving one 60 m 4 blocks in one tile,
ticking one 2-4 blocks; exploring a 600 m disc more 55-71 blocks in 4 tiles. Scaled by the
in-game ratios (fog 0.55 s, ink 1.0 s), the fog and ink after a pin change should drop from
about 1.5 s to under 0.2 s; not yet measured in game.

Offline after `686c072` (Velen, *measured* with `--fogcachecheck` and a temporary
measurement in the preview, warm file cache; Valheim's Mono through a host on
`mono-2.0-bdwgc.dll`). What each piece costs to read back against laying it:

| Piece | Laid (.NET / Mono) | Raw read | gzip Fastest read (.NET / Mono) | Size raw / gzip |
| --- | --- | --- | --- | --- |
| Fog band (opacity, thinning, glimpse opacity) | 100-165 / 190-200 ms (the whole fog) | 5-7 ms | 32 / 24 ms | 12 MB / 0.1-0.2 MB |
| Fog RGBA (not stored: made from the band and the parchment) | | 5 ms | 109 / 40 ms | 16 MB / 3-4.6 MB |
| Laid ink with mips | 170-220 / 215-249 ms | 13-22 ms | 224 / 172 ms | 85 MB / 1.8-2.2 MB |
| Cleared ink (not stored: made again, 6-11 / 19-21 ms) | | 5 ms | 38 / 34 ms | 16 MB / 0.1 MB |

The rest of a load: the band's key 0-8 ms (SHA-256 of the 512 KB of explored bits), the fog
from the band 14-30 ms (`FogOfBand`: the buffers and the marks judged), the ink's key under
1 ms, a lay's kept state from the ink read back 5-13 / 18-24 ms (cleared ink, fogged blocks,
the shown sea marks' washes, 11 / 19 ms for 50 of them). The ink decided the format: gzip read
it back under Mono in 172 ms against a 215-249 ms lay, not clearly faster, raw in 13-22 ms;
the band is raw too (gzip would save 12 MB for about 20 ms more). The ink file leaves out the
32² blocks under opaque fog: 27-32 MB with the start island explored, 57-60 MB with most of
Velen (a 7 km disc: fog 285-359 ms laid against about 35 ms from disk, ink 344-390 ms against
45-70 ms), at most about 85 MB. Writing takes 4-9 ms (band) and 30-45 ms (ink) to the OS file
cache. Cold reads were not measured; both files are read while the world loads, about 2 s
before the fog needs them in the last timing run.

Ninth timing run (`f2bdd56`, everything through the fog and ink cache; Velen, the first
session with this build, so fog and ink were laid, not read), *measured*, and confirmed by the
user in game: pin-change repaints 1.04-1.06 s; 17 drawn close-ups kept on screen stale and none
dropped per change; the ink patched on the GPU (18-23 tiles); the last stale close-up in view
swapped 0.75 s after the change with the atlas open and 0.82 s with it closed (the closed
catch-up redrew 17 tiles on every worker in 0.79 s).

## Why painting was slower in game than offline

| Cause | Share | Evidence |
| --- | --- | --- |
| Unity Mono instead of .NET | about 1.5x | Paint 6.4 s vs 4.25 s in Valheim's Mono; the glyph stage 2.3x (small calls are not inlined) |
| Mono's garbage collector | about +10 % | A paint allocated about 950 MB; with 1.1 GB of other live objects, seven full collections of 100-160 ms |
| Contention | the rest | Jobs queued strictly in order on 4 workers, and under Mono a `Parallel.For` waits for every part it queued to start, so loops stalled behind long jobs and four at once deadlocked (fixed in `618863a`) |

Not the cause: the thread count. Valheim is Unity 6000.0.75 on Mono with the incremental GC, not
IL2CPP.

## Work the fog hides

On a lightly explored world (0.17 % of the snapshot) 72 % of the fog's pixels are opaque, about
95 % of the close-ups inside the world lie wholly under it, and 27,625 of 28,203 trees stand
under it. The fog, the ink and the close-ups now skip that work (`6d8adc3`, `eed6497`).

## Name banners

Drawing a banner tested every outline edge for every pixel of its box, so a long name took up to
1.5 s offline (5 s under Mono). Ship-prerendered banners were measured (about 440 KB for one
banner every 8 units of width) and rejected: they drift from the code and a linear rasterizer
makes them unnecessary. Banners are drawn on the workers, for every shown pin's name when the
pins are built, in 5-17 ms under Mono.

## Ruled out

- Splitting parallel loops into bands: measured no better than per-row loops under load.
- Smaller fog and glimpse textures: an Alpha8 mask loses the parchment mottle without a custom
  shader.
- Painting only explored land: the sheet is cached by its pins, so exploring would repaint while
  sailing, and several stages work across the whole world.
- A partial repaint around changed pins, re-deciding only near them: pins' effects are local
  (15 x 25 to 22 x 31 px), but about 1,000-1,500 lines with drift risk, the downstream steps
  would still take 1.5-2 s, and `m_glyphs.Sort` has no tie-break (adding a pin moves a few
  far-away pixels), which would need an `InkCache.Version` bump first. `0aca2ee` instead decides
  everything as a full paint does and only rasterizes locally; the sort's reordering just adds a
  few cells, so no bump was needed.
- Raising worker priority to Normal: risks the frame rate.

## Open

1. The minimap capture (`WorldSource.Capture`) is now the worst hitch: 38 ms on Velen and 72 ms
   on the second world, each frame with a garbage collection. It reads three large textures at
   once; spreading it over three frames and reusing its buffers should shrink it.
2. Browsing close-ups churns memory: collections rose from 18 to 75 in 25 s (about two a second)
   with a 20-27 ms frame every few seconds in the tiles' update or the view. Each tile allocates
   its buffers; reusing them and measuring the upload cost is the next step.
3. The first opening costs 38-40 ms on one world and 6 ms on another (`AtlasView.Show`, likely
   building pins and their textures the first time).
4. A per-painter array pool (`docs/array-pool.patch`, applies to `b0a26e1`): cut a paint's
   allocations 21 % (first) and 46 % (repaint) and collections over nine Mono paints from 16 to
   11, byte-identical, but holds about 208 MB more between paints and changed no measured time.
   Take it only if the timing shows collections still hurt.
5. The ink upload costs 25-55 ms of one frame (Unity uploads the whole texture with its mips at
   once). Since `0fe6c5a` a lay that changed at most 24 256² tiles (a pin change: 1-21 offline)
   uploads only a packed image of them (512 x 256 per tile plus 64 rows, about 0.5 MB a tile)
   and copies the pieces with `Graphics.CopyTexture`; not yet run in game. The next timing build
   logs which path ran (`ink upload: patch ...` or `whole texture`) and the "ink laid and
   uploaded" frame cost; a warning in the log means it fell back. The first ink of a session,
   a new sheet from the cache or a full paint still go up whole.
6. The background repaint takes about 9 s in game (Mono, collections); it no longer blocks
   opening.
7. Close-ups took about 85 ms each in game before the rasterizer change; not re-measured. Since
   `d0bbd55` a pin change keeps some of them; the next timing build logs how many
   (`close-ups after a pin change`, `close-ups after a fog`). The first timing run with it kept 6-10 of about 30 drawn round the changed pin; `600a65c` narrows the pin rule
   (offline 45-65 % of the drawn tiles near the pin kept). What still drops most of the rest is
   the glyph footprint read (`TileGlyphReach`, 82 px round a pin's sheet box, unscaled by level
   of detail); narrowing it needs the same chain of trees through `ClearOfPlaced`, the oaks,
   `HideCoveredCrowns` and the pins.
8. Stale close-ups (`538428e`) are unmeasured: the timing build logs, per change, how many drawn
   tiles stayed on screen stale and how many were dropped (`close-ups after ...: N drawn kept on
   screen stale`), how long until the last stale tile in view was swapped (expected about half a
   second open), and how many tiles the closed catch-up redrew and how long it took. Open
   questions: a pin across a tile edge shows half old and half new for a few frames (swaps are
   not grouped); and whether `CatchUpMost` 48 on every worker while closed costs frames in play.
