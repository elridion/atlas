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
  per-frame cost until the first opening. A `MISSING` line from `Apply.ps1` means the code moved
  and that call needs a new anchor.
- **Byte checks**: every change that should not alter the drawing was compared offline before it
  was committed: SHA256 of all sheet planes and a deep hash of the `InkRecord`, close-up tiles and
  banners, under .NET and under Valheim's own Mono (the preview's code run inside
  `mono-2.0-bdwgc.dll` from a small host program). A change that moves any pixel bumps
  `InkCache.Version`; none of the work below needed it.

## Results

Velen with its cache, successive timing builds:

| | First | Second | Third | Fourth |
| --- | --- | --- | --- | --- |
| Atlas can open | 13.4 s | 8.8 s | 6.4 s | 5.2 s |
| Background repaint | 16.5 s | 16.5 s | 9.2 s | 9.2 s |
| Close-ups ready | 21.6 s | 27.8 s | 18.2 s | 16.9 s |
| Worst main-thread frame | 102 ms | 37 ms | 82 ms | 50 ms |
| Record that ticks pins, to ink laid | - | - | stalled | 1.3 s |

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
| `6d8adc3` | Fog skips its noise deep in the band and runs in parallel; the ink leaves 32² blocks under opaque fog clear, builds mips in place and reuses its buffer (about 89 MB kept) | Fog 0.8 -> 0.1 s, ink 1.2 -> 0.13 s offline; level 0 identical wherever the fog shows anything, coarse mips near the edge differ, but side-by-side renders showed no visible difference |
| `eed6497` | Priority lane in `Workers` for the opening's jobs; close-ups wholly under opaque fog not drawn | About 70 % of 4x tiles skipped on a lightly explored world |
| `d46d189` | Rasterizer evaluates only the pixels a shape can touch | Banners under Mono 0.16-5 s -> 5-17 ms; repaint under Mono 4.3 -> 2.6 s; close-ups 15-25 % faster (byte-identical) |
| `072c108` | Close-ups drawn ahead: view first, then where a pan carries the view and two wheel steps out; the reopening view drawn while closed | Simulated: fully drawn frames while panning about 25 -> 60 % |
| `618863a` | `Workers` queues a loop's parts only for idle workers | Fixes stalls and a deadlock under Mono (reproduced in Valheim's Mono) |
| `ced7325` | Parchment cached; snapshot decoded on a worker; the first paint without a cache in the priority lane | No parchment wait before the fog; no 95 ms frame reading the snapshot |
| `c5e2a20` | Closing the table's map takes the current pins into the snapshot | Pin edits reach the atlas without a record |
| `b0a26e1` | Cached files start while the world loads; the ink is laid once per record; at most one upload or minimap capture per frame | Untested in game |

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
- A partial repaint around changed pins: pins' effects are local (15 x 25 to 22 x 31 px), but
  about 1,000-1,500 lines with drift risk, the downstream steps would still take 1.5-2 s, and
  `m_glyphs.Sort` has no tie-break (adding a pin moves a few far-away pixels), which would need
  an `InkCache.Version` bump first.
- Raising worker priority to Normal: risks the frame rate.

## Open

1. Test `b0a26e1` in game with a timing build: work starting while the world loads (needs
   `ZNet.World` before the character arrives), the fog starting at once now that the parchment
   cache exists, one ink lay per record, the cause of a 77 ms frame on the character's arrival
   (the frame breakdown shows it), and the pin update when the table's map closes.
2. A per-painter array pool (`docs/array-pool.patch`, applies to `b0a26e1`): cut a paint's
   allocations 21 % (first) and 46 % (repaint) and collections over nine Mono paints from 16 to
   11, byte-identical, but holds about 208 MB more between paints and changed no measured time.
   Take it only if the timing shows collections still hurt.
3. The ink upload costs 23-37 ms of one frame (Unity uploads the whole texture with its mips at
   once); uploading mip levels separately would need checking.
4. The background repaint takes about 9 s in game (Mono, collections); it no longer blocks
   opening.
5. Close-ups took about 85 ms each in game before the rasterizer change; not re-measured.
