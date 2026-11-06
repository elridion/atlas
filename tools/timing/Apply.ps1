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
Edit-File 'src\AtlasMap.cs' "                UploadFog(done.Result);`n            }" "                Timing.BlockBegin();`n                UploadFog(done.Result);`n                Timing.BlockEnd(""fog uploaded"");`n            }"
Edit-File 'src\AtlasMap.cs' "                    s_wash = done.Result;`n" "                    s_wash = done.Result;`n                    Timing.Once(""wash laid"");`n"
Edit-File 'src\AtlasMap.cs' "            s_paper.LoadRawTextureData(paper);" "            Timing.BlockBegin();`n            s_paper.LoadRawTextureData(paper);"
Edit-File 'src\AtlasMap.cs' "            s_paper.Apply(updateMipmaps: false, makeNoLongerReadable: true);" "            s_paper.Apply(updateMipmaps: false, makeNoLongerReadable: true);`n            Timing.BlockEnd(""paper uploaded"");"
Edit-File 'src\AtlasMap.cs' 'Timing.Once("world painted (background)");' 'Timing.Mark(result.m_changed != null ? "ticked pins redrawn" : "world painted");'
Edit-File 'src\AtlasMap.cs' "                s_snapshot = s_snapshotTask.IsFaulted ? null : s_snapshotTask.Result;" "                s_snapshot = s_snapshotTask.IsFaulted ? null : s_snapshotTask.Result;`n                Timing.Once(""snapshot decoded"");"
# Where a slow frame's time went (Timing.Step), and when the character arrives.
Edit-File 'src\AtlasMap.cs' "            if (Game.m_noMap && ZNet.World != null) StartWorld(worldUid);`n" "            if (Game.m_noMap && ZNet.World != null) StartWorld(worldUid);`n            Timing.Step(""world started before the character"");`n"
Edit-File 'src\AtlasMap.cs' "                s_playerId = player.GetPlayerID();`n" "                s_playerId = player.GetPlayerID();`n                Timing.Once(""character arrived"");`n"
Edit-File 'src\AtlasMap.cs' "            StartWorld(worldUid);`n            KeepPins();`n            KeepInk(worldUid);`n            KeepFog();`n            KeepPaper();`n            KeepBareParchment();`n            KeepDirt();`n            KeepEdge();`n" "            Timing.Step(""character and snapshot"");`n            StartWorld(worldUid);`n            Timing.Step(""StartWorld"");`n            KeepPins();`n            Timing.Step(""KeepPins"");`n            KeepInk(worldUid);`n            Timing.Step(""KeepInk"");`n            KeepFog();`n            Timing.Step(""KeepFog"");`n            KeepPaper();`n            Timing.Step(""KeepPaper"");`n            KeepBareParchment();`n            Timing.Step(""KeepBareParchment"");`n            KeepDirt();`n            Timing.Step(""KeepDirt"");`n            KeepEdge();`n            Timing.Step(""KeepEdge"");`n"
Edit-File 'src\AtlasMap.cs' "            s_sparkles.Update(" "            Timing.Step(""tiles"");`n            s_sparkles.Update("
Edit-File 'src\AtlasMap.cs' "            s_view.Update();`n        }`n`n        // The table's map" "            Timing.Step(""sparkles and opening"");`n            s_view.Update();`n            Timing.Step(""view"");`n        }`n`n        // The table's map"
Edit-File 'src\AtlasPlugin.cs' "                AtlasMap.Update();" "                Timing.FrameBegin();`n                AtlasMap.Update();`n                Timing.FrameEnd();"
Edit-File 'src\NameBanner.cs' "                Drawn drawn = task.Result;`n                try`n                {" "                Drawn drawn = task.Result;`n                Timing.BlockBegin();`n                try`n                {"
Edit-File 'src\NameBanner.cs' "                    uploaded = true;`n" "                    uploaded = true;`n                    Timing.BlockEnd(`$""name banner {drawn.m_width}x{drawn.m_height} uploaded"");`n"
$p = (Resolve-Path 'src\Paper.cs').Path; $t = [IO.File]::ReadAllText($p)
$t = [regex]::Replace($t, "(public static byte\[\] InkWithMips\([^\n]*\n\s*\{\n)", "`$1            Timing.Mark(""ink job started (worker)"");`n")
$t = [regex]::Replace($t, "(public static FogEdge Fog\([^\n]*\n[^\n]*\n\s*\{\n)", "`$1            Timing.Mark(""fog job started (worker)"");`n")
[IO.File]::WriteAllText($p, $t, $enc)
$p = (Resolve-Path 'src\Ink\InkPainter.cs').Path; $t = [IO.File]::ReadAllText($p)
$t = [regex]::Replace($t, "(public static byte\[\] Paint\([^\n]*\n\s*\{\n)", "`$1            Timing.Mark(""paint job started (worker)"");`n")
[IO.File]::WriteAllText($p, $t, $enc)
"timing calls: " + (Select-String -Path src\*.cs,src\Ink\*.cs -Pattern 'Timing\.').Count
