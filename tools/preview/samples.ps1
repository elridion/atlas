param([switch]$Publish)
# Renders the 24 atlas samples (the last three: test pins and the pin catalogue) into img/; -Publish replaces the samples in the repo's screenshots/.
$s = $PSScriptRoot
$shots = (Resolve-Path "$s\..\..").Path + "\screenshots"
Remove-Item "$s\img\*.png" -Force -ErrorAction SilentlyContinue
& "$s\run.ps1" -Views overview,wide:0:0:1000:1,start:0:0:600:1,tilesbMeadows4:-48:192:4,tilesbMeadows2:-48:192:2,tilesbBlackForest4:1872:4992:4,tilesbBlackForest2:1872:4992:2,tilesbSwamp4:-4128:3552:4,tilesbSwamp2:-4128:3552:2,tilesbMountain4:-3408:-1008:4,tilesbMountain2:-3408:-1008:2,tilesbPlains4:-2688:2112:4,tilesbPlains2:-2688:2112:2,tilesbMistlands4:5712:-5088:4,tilesbMistlands2:5712:-5088:2,tilesbAshlands4:672:-8928:4,tilesbAshlands2:672:-8928:2,tilesbDeepNorth4:5952:7152:4,tilesbDeepNorth2:5952:7152:2,tilesbOcean4:-1250:-350:4,tilesbOcean2:-1250:-350:2,pinsForest2:1872:4992:2,pinsPlains1:-2688:2112:1 2>&1 | Select-Object -Last 2
& "$s\out\Preview.exe" --pincat "$s\img\pincat.png"
if (-not $Publish) { return }
# Publish only a complete set: a failed render must not wipe the published samples.
$need = @('overview','wide','start','pinsForest2','pinsPlains1','pincat') + ('Meadows','BlackForest','Swamp','Mountain','Plains','Mistlands','Ashlands','DeepNorth','Ocean' | ForEach-Object { "tilesb${_}4", "tilesb${_}2" })
$missing = $need | Where-Object { -not (Test-Path "$s\img\$_.png") }
if ($missing) { "render incomplete, samples not published: $($missing -join ', ')"; exit 1 }
New-Item -ItemType Directory -Force $shots | Out-Null
Get-ChildItem $shots -Filter 'atlas-sample-*.png' | Remove-Item -Force
Copy-Item "$s\img\overview.png" "$shots\atlas-sample-01-world.png"
Copy-Item "$s\img\wide.png" "$shots\atlas-sample-02-region.png"
Copy-Item "$s\img\start.png" "$shots\atlas-sample-03-start-island.png"
$n = 4
foreach ($b in 'Meadows','BlackForest','Swamp','Mountain','Plains','Mistlands','Ashlands','DeepNorth','Ocean')
{
    $slug = ($b -creplace '([a-z])([A-Z])','$1-$2').ToLower()
    Copy-Item "$s\img\tilesb${b}4.png" ("$shots\atlas-sample-{0:D2}-{1}-close.png" -f $n, $slug)
    Copy-Item "$s\img\tilesb${b}2.png" ("$shots\atlas-sample-{0:D2}-{1}-wide.png" -f $n, $slug)
    $n++
}
Copy-Item "$s\img\pinsForest2.png" "$shots\atlas-sample-13-pins-forest.png"
Copy-Item "$s\img\pinsPlains1.png" "$shots\atlas-sample-14-pins-region.png"
Copy-Item "$s\img\pincat.png" "$shots\atlas-sample-15-pin-catalogue.png"
(Get-ChildItem $shots -Filter 'atlas-sample-*.png').Count
