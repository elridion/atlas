param([string]$World = 'Velen', [string[]]$Views, [switch]$Layers, [string[]]$Alt, [string]$Temple)
# Builds Atlas (Release) and the preview, then renders the views into img/ from the vanilla minimap
# cache of a local world (one you have explored with the map on). -Alt 'NAME:wx:wz:radius',...
# forces alt biomes there (the preview cannot compute the game's), e.g. 'Wolf Mountain:-3408:-1008:500'.
$s = $PSScriptRoot
$atlas = (Resolve-Path "$s\..\..").Path
$valheim = $env:VALHEIM_INSTALL
if (-not $valheim) { $valheim = "C:\Program Files (x86)\Steam\steamapps\common\Valheim" }
$managed = "$valheim\valheim_Data\Managed"
Push-Location $atlas
$out = dotnet build -c Release 2>&1
Pop-Location
$errors = $out | Select-String -Pattern ' error ' | Select-Object -Unique
if ($errors) { $errors; exit 1 }
if (-not (Test-Path "$s\out\Preview.exe") -or (Get-Item "$s\Program.cs").LastWriteTime -gt (Get-Item "$s\out\Preview.exe").LastWriteTime)
{
    $b = dotnet build "$s\Preview.csproj" -c Release -o "$s\out" 2>&1
    $e = $b | Select-String -Pattern ' error ' | Select-Object -Unique
    if ($e) { $e; exit 1 }
}
# Valheim's own netstandard and game assemblies, and BepInEx's core (Harmony, MonoMod), beside the exe.
Copy-Item "$managed\netstandard.dll" "$s\out\" -Force
foreach ($f in Get-ChildItem $managed -Filter 'assembly_*.dll') { if (-not (Test-Path "$s\out\$($f.Name)")) { Copy-Item $f.FullName "$s\out\" } }
foreach ($f in Get-ChildItem $managed -Filter 'Unity*.dll') { if (-not (Test-Path "$s\out\$($f.Name)")) { Copy-Item $f.FullName "$s\out\" } }
$core = Get-ChildItem "$env:APPDATA\com.kesomannen.gale" -Recurse -Filter '0Harmony.dll' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($core) { foreach ($f in Get-ChildItem $core.DirectoryName -Filter '*.dll') { if (-not (Test-Path "$s\out\$($f.Name)")) { Copy-Item $f.FullName "$s\out\" } } }
Copy-Item "$atlas\bin\Release\Atlas.dll" "$s\out\" -Force
$extra = @()
if ($Layers) { $extra += '--layers' }
foreach ($a in $Alt) { $extra += '--alt'; $extra += $a }
if ($Temple) { $extra += '--temple'; $extra += $Temple }
New-Item -ItemType Directory -Force "$s\img" | Out-Null
& "$s\out\Preview.exe" "$env:USERPROFILE\AppData\LocalLow\IronGate\Valheim\worlds_local\$World" "$s\img" @Views @extra
