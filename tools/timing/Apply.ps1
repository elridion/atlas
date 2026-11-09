# Temporary timing instrumentation for the atlas (never commit its result). From the repo root:
#   powershell -File tools\timing\Apply.ps1
#   dotnet build -c Release -t:Package "-p:DefineConstants=TRACE%3BATLAS_TIMING"
#   rename dist\Atlas-<version>.zip to Atlas-<version>-timing.zip
#   git checkout -- .   and delete src\Timing.cs
# It copies Timing.cs into src and inserts Timing calls by exact text; a "MISSING" line means
# the code moved and that call needs a new anchor. Lines land in BepInEx\LogOutput.log as
# "[atlas timing] <seconds since joining> gc <collections> <stage>".
Set-Location (Resolve-Path "$PSScriptRoot\..\..")
$enc = New-Object Text.UTF8Encoding $false
Copy-Item "$PSScriptRoot\Timing.cs" 'src\Timing.cs' -Force
# AtlasMap stages.
$m = (Resolve-Path 'src\AtlasMap.cs').Path
$t = [IO.File]::ReadAllText($m)
$nl = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
function Put([string]$a, [string]$b)
{
    $a = $a.Replace('|', $nl); $b = $b.Replace('|', $nl)
    if (-not $script:t.Contains($a)) { "MISSING: $a"; return }
    $script:t = $script:t.Replace($a, $b)
}
Put '                s_worldUid = worldUid;' '                s_worldUid = worldUid;|                Timing.Start();'
Put '                    CachedSheet cached = s_cacheTask.Result;' '                    CachedSheet cached = s_cacheTask.Result;|                    Timing.Once("cached sheet loaded");'
Put '                s_cacheTask = null;|                // Painting failed' '                if (s_sheet == null) Timing.Once("no usable cached sheet");|                s_cacheTask = null;|                // Painting failed'
Put '                        WorldSource captured = WorldSource.Capture(Minimap.instance);' '                        Timing.Once("vanilla minimap ready");|                        Timing.BlockBegin();|                        WorldSource captured = WorldSource.Capture(Minimap.instance);|                        Timing.BlockEnd("minimap captured");'
Put '                    WorldSource decodedSource = decoded.Result;' '                    WorldSource decodedSource = decoded.Result;|                    Timing.Once("world decoded");'
Put '            PaintResult result = painted.Result;' '            PaintResult result = painted.Result;|            Timing.Once("world painted (background)");'
Put '                UploadInk(done.Result, s_mipsDensity, s_mipsKey);' '                Timing.BlockBegin();|                UploadInk(done.Result, s_mipsDensity, s_mipsKey);|                Timing.BlockEnd("ink laid and uploaded");'
Put '                s_parchment = done.Result.m_parchment;' '                s_parchment = done.Result.m_parchment;|                Timing.Once("fog laid (first)");'
Put '            s_bareParchment.LoadRawTextureData(s_parchment);' '            Timing.BlockBegin();|            s_bareParchment.LoadRawTextureData(s_parchment);'
Put '            s_view.SetParchment(s_bareParchment);' '            Timing.BlockEnd("bare parchment uploaded");|            s_view.SetParchment(s_bareParchment);'
Put '            s_dirt.LoadRawTextureData(s_dirtTask.Result);' '            Timing.BlockBegin();|            s_dirt.LoadRawTextureData(s_dirtTask.Result);'
Put '            s_view.SetDirt(s_dirt);' '            Timing.BlockEnd("dirt uploaded");|            s_view.SetDirt(s_dirt);'
Put '            s_edgeStrips = textures;' '            Timing.Once("edge uploaded");|            s_edgeStrips = textures;'
Put '            s_view.Show(s_paper, s_ink, s_fog, s_dirt, s_snapshot, s_density, s_source);' '            Timing.Opened();|            Timing.BlockBegin();|            s_view.Show(s_paper, s_ink, s_fog, s_dirt, s_snapshot, s_density, s_source);|            Timing.BlockEnd("opening (Show)");'
Put '            s_tiles.Update(s_view);' '            s_tiles.Update(s_view);|            if (s_ink && s_paper && s_fog && s_fogMarks == s_seaMarks) Timing.Once("atlas can open");|            if (s_tiles.Ready) Timing.Once("close-ups ready");'
[IO.File]::WriteAllText($m, $t, $enc)
"AtlasMap timing calls: " + ([regex]::Matches($t, 'Timing\.')).Count

# Other stages.
function Edit-File([string]$path, [string]$a, [string]$b)
{
    $p = (Resolve-Path $path).Path; $t = [IO.File]::ReadAllText($p)
    if (-not $t.Contains($a)) { "MISSING in ${path}: $a"; return }
    [IO.File]::WriteAllText($p, $t.Replace($a, $b), $enc)
}
Edit-File 'src\AtlasMap.cs' "                if (done.Result.m_redecided) KeepDecisions(done.Result);`n                else UploadFog(done.Result);`n" "                Timing.BlockBegin();`n                if (done.Result.m_redecided) KeepDecisions(done.Result);`n                else UploadFog(done.Result);`n                Timing.BlockEnd(done.Result.m_redecided ? (done.Result.m_fogChanged ? ""fog: marks judged again, lifts changed, fog texture uploaded"" : ""fog: marks judged again, band and texture kept"") : ""fog uploaded"");`n"
# The fog's worker: laid whole (Paper.Fog) or only its marks judged again (Paper.Redecide).
Edit-File 'src\AtlasMap.cs' "                FogEdge edge = Paper.Fog(pixels, glimpse, paper, snapshot, marks, glimpses, sheetMarks, glimpseBand);`n" "                var fogWatch = System.Diagnostics.Stopwatch.StartNew();`n                FogEdge edge = Paper.Fog(pixels, glimpse, paper, snapshot, marks, glimpses, sheetMarks, glimpseBand);`n                Timing.Mark(`$""fog laid whole on the worker in {fogWatch.ElapsedMilliseconds} ms"");`n"
Edit-File 'src\AtlasMap.cs' "                    FogEdge edge = Paper.Redecide(last, fog, laidGlimpse, seaMarks, judged);`n" "                    var fogWatch = System.Diagnostics.Stopwatch.StartNew();`n                    FogEdge edge = Paper.Redecide(last, fog, laidGlimpse, seaMarks, judged);`n                    Timing.Mark(`$""fog: marks judged again on the last band on the worker in {fogWatch.ElapsedMilliseconds} ms (fog not laid again)"");`n"
# The ink's worker: laid whole or again over some blocks (Paper.LayInk), and the upload's path.
Edit-File 'src\AtlasMap.cs' "                Paper.LaidInk laid = Paper.LayInk(sheet, from, redrawn, marks, glimpse, edge);`n" "                var inkWatch = System.Diagnostics.Stopwatch.StartNew();`n                Paper.LaidInk laid = Paper.LayInk(sheet, from, redrawn, marks, glimpse, edge);`n                Timing.Mark(laid.m_tiles == null ? `$""ink laid whole on the worker in {inkWatch.ElapsedMilliseconds} ms"" : `$""ink laid again over {laid.m_blocksLaid} of 16384 blocks ({System.Array.FindAll(laid.m_tiles, t => t).Length} of 256 tiles) on the worker in {inkWatch.ElapsedMilliseconds} ms"");`n"
Edit-File 'src\AtlasMap.cs' "                if (copied)`n                {`n" "                if (copied)`n                {`n                    Timing.Mark(`$""ink upload: patch of {ready.m_patch.Tiles} tiles copied on the GPU"");`n"
Edit-File 'src\AtlasMap.cs' "            byte[] data = laid.m_data;`n            Texture2D old = s_ink;`n" "            byte[] data = laid.m_data;`n            Timing.Mark(laid.m_tiles == null ? ""ink upload: whole texture (laid whole)"" : ""ink upload: whole texture (patch not used)"");`n            Texture2D old = s_ink;`n"
Edit-File 'src\AtlasMap.cs' "                    s_wash = done.Result;`n" "                    s_wash = done.Result;`n                    Timing.Once(""wash laid"");`n"
Edit-File 'src\AtlasMap.cs' "            s_paper.LoadRawTextureData(paper);" "            Timing.BlockBegin();`n            s_paper.LoadRawTextureData(paper);"
Edit-File 'src\AtlasMap.cs' "            s_paper.Apply(updateMipmaps: false, makeNoLongerReadable: true);" "            s_paper.Apply(updateMipmaps: false, makeNoLongerReadable: true);`n            Timing.BlockEnd(""paper uploaded"");"
Edit-File 'src\AtlasMap.cs' 'Timing.Once("world painted (background)");' 'Timing.Mark(result.m_changed != null ? "ticked pins redrawn" : "world painted");'
Edit-File 'src\AtlasMap.cs' "                s_snapshot = s_snapshotTask.IsFaulted ? null : s_snapshotTask.Result;" "                s_snapshot = s_snapshotTask.IsFaulted ? null : s_snapshotTask.Result;`n                Timing.Once(""snapshot decoded"");"
# Where a slow frame's time went (Timing.Step), and when the character arrives.
Edit-File 'src\AtlasMap.cs' "            if (Game.m_noMap) StartWorld(worldUid);`n" "            if (Game.m_noMap) StartWorld(worldUid);`n            Timing.Step(""world started before the character"");`n"
Edit-File 'src\AtlasMap.cs' "                s_playerId = player.GetPlayerID();`n" "                s_playerId = player.GetPlayerID();`n                Timing.Once(""character arrived"");`n"
Edit-File 'src\AtlasMap.cs' "            StartWorld(worldUid);`n            KeepPins();`n            KeepInk(worldUid);`n            KeepFog();`n            KeepPaper();`n            KeepBareParchment();`n            KeepDirt();`n            KeepEdge();`n" "            Timing.Step(""character and snapshot"");`n            StartWorld(worldUid);`n            Timing.Step(""StartWorld"");`n            KeepPins();`n            Timing.Step(""KeepPins"");`n            KeepInk(worldUid);`n            Timing.Step(""KeepInk"");`n            KeepFog();`n            Timing.Step(""KeepFog"");`n            KeepPaper();`n            Timing.Step(""KeepPaper"");`n            KeepBareParchment();`n            Timing.Step(""KeepBareParchment"");`n            KeepDirt();`n            Timing.Step(""KeepDirt"");`n            KeepEdge();`n            Timing.Step(""KeepEdge"");`n"
Edit-File 'src\AtlasMap.cs' "            s_sparkles.Update(" "            Timing.Step(""tiles"");`n            s_sparkles.Update("
Edit-File 'src\AtlasMap.cs' "            s_view.Update();`n        }`n`n        // The table's map" "            Timing.Step(""sparkles and opening"");`n            s_view.Update();`n            Timing.Step(""view"");`n        }`n`n        // The table's map"
Edit-File 'src\AtlasPlugin.cs' "                AtlasMap.Update();" "                Timing.FrameBegin();`n                AtlasMap.Update();`n                Timing.FrameEnd();"
Edit-File 'src\NameBanner.cs' "                Drawn drawn = task.Result;`n                try`n                {" "                Drawn drawn = task.Result;`n                Timing.BlockBegin();`n                try`n                {"
Edit-File 'src\NameBanner.cs' "                    uploaded = true;`n" "                    uploaded = true;`n                    Timing.BlockEnd(`$""name banner {drawn.m_width}x{drawn.m_height} uploaded"");`n"
$p = (Resolve-Path 'src\Paper.cs').Path; $t = [IO.File]::ReadAllText($p)
$t = [regex]::Replace($t, "(public static LaidInk LayInk\([^\n]*\n\s*\{\n)", "`$1            Timing.Mark(""ink job started (worker)"");`n")
$t = [regex]::Replace($t, "(public static FogEdge Fog\([^\n]*\n[^\n]*\n\s*\{\n)", "`$1            Timing.Mark(""fog job started (worker)"");`n")
[IO.File]::WriteAllText($p, $t, $enc)
$p = (Resolve-Path 'src\Ink\InkPainter.cs').Path; $t = [IO.File]::ReadAllText($p)
$t = [regex]::Replace($t, "(public static byte\[\] Paint\([^\n]*\n\s*\{\n)", "`$1            Timing.Mark(""paint job started (worker)"");`n            Timing.StagesBegin();`n            var paintWatch = System.Diagnostics.Stopwatch.StartNew();`n")
[IO.File]::WriteAllText($p, $t, $enc)

# A paint's stages, and the share of each spent filling pixels. Each anchor is one exact line that
# must occur once; the call goes before or after it.
function Around([string]$path, [string]$line, [string]$before, [string]$after)
{
    $p = (Resolve-Path $path).Path; $t = [IO.File]::ReadAllText($p)
    $nl = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
    $count = ([regex]::Matches($t, [regex]::Escape($line + $nl))).Count
    if ($count -ne 1) { "MISSING ($count) in ${path}: $line"; return }
    $indent = $line.Substring(0, $line.Length - $line.TrimStart().Length)
    $new = $line + $nl
    if ($before) { $new = "${indent}Timing.Stage(""$before"");$nl" + $new }
    if ($after) { $new = $new + "${indent}Timing.Stage(""$after"");$nl" }
    [IO.File]::WriteAllText($p, $t.Replace($line + $nl, $new), $enc)
}
$ip = 'src\Ink\InkPainter.cs'
Around $ip '            painter.FindMarks(pins);' '' 'pins found'
Around $ip '            FindMist();' 'groundwork' 'mist'
Around $ip '            DrawBackground();' '' 'background fills'
Around $ip '            DrawLava();' '' 'lava'
Around $ip '            DrawRivers();' '' 'rivers'
Around $ip '            DrawCoast();' '' 'coast'
Around $ip '            PlaceSummitsAndHills();' '' 'place: summits and hills'
Around $ip '            PlaceMassifs();' '' 'place: massifs'
Around $ip '            PlaceTrees();' 'place: peak bases' 'place: trees'
Around $ip '            PlaceMist();' '' 'place: mist'
Around $ip '            PlaceDeadTrees();' '' 'place: dead trees'
Around $ip '            DropSpiresOnPeaks();' '' 'place: spires'
Around $ip '            PlaceGlyphs();' 'start painted (thorns, floes, kept) or taken' 'place: pins'
Around $ip '            DrawScarps();' 'footprints' 'scarps'
Around $ip '            DrawStumps();' '' 'stumps'
Around $ip '            DrawBoulders();' '' 'boulders'
Around $ip '            DrawMeadowGrass();' '' 'meadow grass'
Around $ip '            DrawWoodlessGround();' '' 'woodless ground'
Around $ip '            m_glyphs.Sort((a, b) => Order(a).CompareTo(Order(b)));' '' 'sort'
Around $ip '            DrawGrassOverMarks();' 'glyphs inked' 'grass over pins'
Around $ip '            DrawWaves(waterLines);' 'waterwork' 'waves'
Around $ip '            foreach (Glyph floe in m_floes) DrawGlyph(floe);' '' 'floes'
Around $ip '            DrawIllustrations();' '' 'illustrations'
Around $ip '            DrawFrame();' '' 'frame'
# The first paint (and a full paint after a fallback) only records; the stages above are its
# decisions, so their raster is only InkIn's scratch replays for the sea marks. Then the whole
# record is replayed into the sheet in 256 px tiles on the workers (Rasterize): its stage is wall
# time, as the tiles' raster runs on other threads.
Around $ip '            record.Seal(CanvasSize);' '' 'record sealed'
Edit-File $ip "            byte[] sheet = Rasterize(record, cancel);`n" "            Timing.Mark(`$""paint: record-only part {paintWatch.ElapsedMilliseconds} ms"");`n            var replayWatch = System.Diagnostics.Stopwatch.StartNew();`n            byte[] sheet = Rasterize(record, cancel);`n            Timing.Mark(`$""paint: whole record replayed in 256 tiles on the workers in {replayWatch.ElapsedMilliseconds} ms"");`n"
Around $ip '            byte[] sheet = Rasterize(record, cancel);' '' 'tiles replayed'
$p = (Resolve-Path $ip).Path; $t = [IO.File]::ReadAllText($p)
$t = [regex]::Replace($t, "(Timing\.Stage\(""tiles replayed""\);(\r?\n))", "`$1            Timing.StagesEnd();`$2")
[IO.File]::WriteAllText($p, $t, $enc)
# Whether the waves' layout and the sea marks' placement were kept from the last paint or made again.
Edit-File $ip "                foreach (InkRecord.Wave wave in kept) DrawWave(wave);`n" "                Timing.Mark(`$""waves: kept layout reused ({kept.Length} strokes)"");`n                foreach (InkRecord.Wave wave in kept) DrawWave(wave);`n"
Edit-File $ip "            LayOutWaves(lines);`n" "            LayOutWaves(lines);`n            Timing.Mark(m_start != null ? ""waves: laid out and kept"" : ""waves: laid out (no kept start)"");`n"
$sp = 'src\Ink\InkPainter.Sea.cs'
Edit-File $sp "            bool holds = plan != null && SameHashes(" "            Timing.Stage(""illustrations: before the kept placement's check"");`n            bool holds = plan != null && SameHashes("
Edit-File $sp "            if (holds)`n" "            Timing.Stage(holds ? `$""illustrations: kept placement checked ({plan.m_hashes.Length} windows), reused"" : plan != null ? `$""illustrations: kept placement checked ({plan.m_hashes.Length} windows), a window changed, placing again"" : ""illustrations: no kept placement, placing"");`n            if (holds)`n"

# Close-ups kept and dropped after a pin change (RecordChange) and after the fog's decisions that
# follow it (FogChange), and what comparing the records cost on the paint's worker.
Edit-File 'src\AtlasMap.cs' "                                tileChange = RecordChange.Between(lastRecord, record);`n" "                                var changeWatch = System.Diagnostics.Stopwatch.StartNew();`n                                tileChange = RecordChange.Between(lastRecord, record);`n                                Timing.Mark(tileChange == null ? `$""close-ups: records not comparable ({changeWatch.ElapsedMilliseconds} ms)"" : `$""close-ups: records compared in {changeWatch.ElapsedMilliseconds} ms, {tileChange.ChangedCells} cells changed"");`n"
$dt = 'src\DetailTiles.cs'
Edit-File $dt "            if (change == null || m_record == null || change.m_from != m_record)`n            {`n                DropTiles();`n" "            if (change == null || m_record == null || change.m_from != m_record)`n            {`n                Timing.Mark(`$""close-ups after a new record: all {m_tiles.Count} drawn and {m_running.Count + m_ready.Count} under way dropped ({(change == null ? ""no comparison"" : ""drawn from another record"")})"");`n                DropTiles();`n"
Edit-File $dt "            DropWhere((level, region, unjudged) => change.Affects(record, region, s_scales[level]), fogged: false);`n" "            int heldTiles = m_tiles.Count, held = m_tiles.Count + m_running.Count + m_ready.Count;`n            int gone = DropWhere((level, region, unjudged) => change.Affects(record, region, s_scales[level]), fogged: false);`n            Timing.Mark(`$""close-ups after a pin change: {held - gone} kept, {gone} dropped (drawn: {m_tiles.Count} of {heldTiles} kept; {m_fogged.Count} under fog kept)"");`n"
Edit-File $dt "            if (!partial)`n            {`n                DropTiles();`n" "            if (!partial)`n            {`n                Timing.Mark(`$""close-ups after a fog: all {m_tiles.Count} drawn and {m_running.Count + m_ready.Count} under way dropped ({(change == null ? ""no comparison"" : ""other fog or record"")})"");`n                DropTiles();`n"
Edit-File $dt "            DropWhere((level, region, unjudged) => unjudged || change.Affects(region, DetailTile.FogReach(record, region, s_scales[level])));`n" "            int heldTiles = m_tiles.Count, held = m_tiles.Count + m_fogged.Count + m_running.Count + m_ready.Count;`n            int gone = DropWhere((level, region, unjudged) => unjudged || change.Affects(region, DetailTile.FogReach(record, region, s_scales[level])));`n            Timing.Mark(`$""close-ups after a fog: {held - gone} kept, {gone} dropped (drawn: {m_tiles.Count} of {heldTiles} kept)"");`n"

# The cell repaint (InkPainter.Repaint): its record-only run logs the stages above.
$rp = 'src\Ink\InkPainter.Repaint.cs'
Around $rp '            record.Seal(CanvasSize);' '' 'repaint: record sealed'
Around $rp '            List<RectInt> rects = DirtyRects(dirty, record.CellsAcross);' '' 'repaint: cells compared'
Edit-File $rp "            if (sheet == null || sheetRecord == null || record == null || record.Count != 0) return null;" "            Timing.Mark(""cell repaint started (worker)"");`n            Timing.StagesBegin();`n            if (sheet == null || sheetRecord == null || record == null || record.Count != 0) return null;"
Edit-File $rp "            changed?.AddRange(rects);" "            Timing.Stage(""repaint: cells drawn"");`n            Timing.StagesEnd();`n            Timing.Mark(`$""repaint: {count} of {after.Length} cells, {rects.Count} rectangles"");`n            changed?.AddRange(rects);"

# InkCanvas's pixel loops: each renamed <Name>Raw behind a wrapper that times it.
$cp = (Resolve-Path 'src\Ink\InkCanvas.cs').Path; $t = [IO.File]::ReadAllText($cp)
$nl = if ($t.Contains("`r`n")) { "`r`n" } else { "`n" }
function Timed([string]$access, [string]$name, [string]$params, [string]$callArgs)
{
    $sig = "        $access void $name($params)$nl"
    $count = ([regex]::Matches($script:t, [regex]::Escape($sig))).Count
    if ($count -ne 1) { "MISSING ($count) in InkCanvas: $name"; return }
    $wrap = "        $access void $name($params)$nl        {$nl            Timing.RasterBegin();$nl            try { ${name}Raw($callArgs); } finally { Timing.RasterEnd(); }$nl        }$nl$nl        private void ${name}Raw($params)$nl"
    $script:t = $script:t.Replace($sig, $wrap)
}
Timed 'private' 'Pieces' 'float x0, float y0, float x1, float y1, float width, float strength, bool erase' 'x0, y0, x1, y1, width, strength, erase'
Timed 'private' 'OutlinePixels' 'IList<Vector2> sheetPolygon, float grow' 'sheetPolygon, grow'
Timed 'private' 'RingPixels' 'float cx, float cy, float rx, float ry, float width, float strength' 'cx, cy, rx, ry, width, strength'
Timed 'private' 'Ellipse' 'float cx, float cy, float rx, float ry, float strength, bool erase' 'cx, cy, rx, ry, strength, erase'
Timed 'private' 'Polygon' 'IList<Vector2> sheetPolygon, float strength, bool erase' 'sheetPolygon, strength, erase'
Timed 'private' 'PolygonPixels' 'IList<Vector2> sheetPolygon, float strength, bool erase' 'sheetPolygon, strength, erase'
Timed 'public' 'Pin' 'PinGlyph glyph, float left, float top, float size, bool struck' 'glyph, left, top, size, struck'
[IO.File]::WriteAllText($cp, $t, $enc)
"timing calls: " + (Select-String -Path src\*.cs,src\Ink\*.cs -Pattern 'Timing\.').Count
