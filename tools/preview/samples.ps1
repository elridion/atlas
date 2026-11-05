param([switch]$Publish)
# Renders the 32 atlas samples (pins drawn into the sheet round the start island and in the mountains, the pin catalogue, the sea mark catalogue, and views forced to biome variants (a Kalhygge clear-cut, wide and close, then Pinetree Black Forest, Lox Plains, Trees Mistlands, Wolf Mountain, Bog Swamp), since the preview cannot compute biome variants) into img/; -Publish replaces the samples in the repo's screenshots/.
$s = $PSScriptRoot
$shots = (Resolve-Path "$s\..\..").Path + "\screenshots"
Remove-Item "$s\img\*.png" -Force -ErrorAction SilentlyContinue
# Velen's start island with the Sacrificial Stones and a set of pins drawn into the sheet, one in
# the mountains to its south (the preview has no location icons or snapshot of its own).
$pins = 'House:205:-119','Fire:158:-158','Bed:-210:0','Portal:-527:316','Hammer:738:-527','Point:-1265:422:x','Death:0:-1001','Boss:120:-1180','Tower:-240:359','Trader:-60:-120','Forge:-80:-1110'
& "$s\run.ps1" -Temple '-111:263' -Pin $pins -Views overview,wide:0:0:1000:1,start:0:0:600:1,tilesbMeadows4:-48:192:4,tilesbMeadows2:-48:192:2,tilesbBlackForest4:1872:4992:4,tilesbBlackForest2:1872:4992:2,tilesbSwamp4:-4128:3552:4,tilesbSwamp2:-4128:3552:2,tilesbMountain4:-3408:-1008:4,tilesbMountain2:-3408:-1008:2,tilesbPlains4:-2688:2112:4,tilesbPlains2:-2688:2112:2,tilesbMistlands4:5712:-5088:4,tilesbMistlands2:5712:-5088:2,tilesbAshlands4:672:-8928:4,tilesbAshlands2:672:-8928:2,tilesbDeepNorth4:5952:7152:4,tilesbDeepNorth2:5952:7152:2,tilesbOcean4:-1250:-350:4,tilesbOcean2:-1250:-350:2,tilesPinsStart4:0:60:4,tilesPinsMountain2:60:-1090:2 2>&1 | Select-Object -Last 2
& "$s\run.ps1" -Views tilesClearcut2:1872:4992:2,tilesClearcut4:1872:4992:4 -Alt 'Kalhygge Black Forest:1872:4992:900' 2>&1 | Select-Object -Last 2
# The other variants that change woods, each forced over its own biome's view in one run (an
# --alt only fills cells of its own biome within its radius, so they do not meet).
& "$s\run.ps1" -Views tilesPinetree2:1872:4992:2,tilesLox2:-2688:2112:2,tilesTreesMist2:5712:-5088:2,tilesWolf2:-7500:3000:2,tilesBog2:-4128:3552:2 -Alt 'Pinetree Black Forest:1872:4992:900','Lox Plains:-2688:2112:900','Trees Mistlands:5712:-5088:900','Wolf Mountain:-7500:3000:900','Bog Swamp:-4128:3552:900' 2>&1 | Select-Object -Last 2
& "$s\out\Preview.exe" --pincat "$s\img\pincat.png"
& "$s\out\Preview.exe" --seacat "$s\img\seacat.png"
if (-not $Publish) { return }
# Publish only a complete set: a failed render must not wipe the published samples.
$need = @('overview','wide','start','tilesPinsStart4','tilesPinsMountain2','pincat','seacat','tilesClearcut2','tilesClearcut4','tilesPinetree2','tilesLox2','tilesTreesMist2','tilesWolf2','tilesBog2') + ('Meadows','BlackForest','Swamp','Mountain','Plains','Mistlands','Ashlands','DeepNorth','Ocean' | ForEach-Object { "tilesb${_}4", "tilesb${_}2" })
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
Copy-Item "$s\img\tilesPinsStart4.png" "$shots\atlas-sample-13-pins-start.png"
Copy-Item "$s\img\tilesPinsMountain2.png" "$shots\atlas-sample-14-pins-mountains.png"
Copy-Item "$s\img\pincat.png" "$shots\atlas-sample-15-pin-catalogue.png"
Copy-Item "$s\img\tilesClearcut2.png" "$shots\atlas-sample-16-clear-cut-wide.png"
Copy-Item "$s\img\tilesClearcut4.png" "$shots\atlas-sample-16-clear-cut-close.png"
Copy-Item "$s\img\tilesPinetree2.png" "$shots\atlas-sample-17-pinetree-black-forest.png"
Copy-Item "$s\img\tilesLox2.png" "$shots\atlas-sample-18-lox-plains.png"
Copy-Item "$s\img\tilesTreesMist2.png" "$shots\atlas-sample-19-trees-mistlands.png"
Copy-Item "$s\img\tilesWolf2.png" "$shots\atlas-sample-20-wolf-mountain.png"
Copy-Item "$s\img\tilesBog2.png" "$shots\atlas-sample-21-bog-swamp.png"
Copy-Item "$s\img\seacat.png" "$shots\atlas-sample-22-sea-catalogue.png"
(Get-ChildItem $shots -Filter 'atlas-sample-*.png').Count
