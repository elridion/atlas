param([switch]$Publish)
# Renders the 39 atlas samples into img/: the world, the region and the start island; each biome's
# close and wide view followed by its biome variants that the atlas draws differently (forced
# with -Alt over the biome's own wide view (the Mountain variants over fir woods elsewhere), since the preview cannot compute biome variants: Smalltree
# and Birch Meadows; a Kalhygge clear-cut, wide and close, Rock and Pinetree Black Forest; Bog and Hut Swamp;
# Wolf, Drake and Fortress Mountain; Lox and Death Plains; Trees Mistlands); pins drawn into the
# sheet round the start island and in the mountains; the pin catalogue and the sea mark catalogue.
# -Publish replaces the samples in the repo's screenshots/.
$s = $PSScriptRoot
$shots = (Resolve-Path "$s\..\..").Path + "\screenshots"
Remove-Item "$s\img\*.png" -Force -ErrorAction SilentlyContinue
# Each biome's sample spot; its variants are drawn over the same ground.
$spot = @{
    Meadows = '-48:192'; BlackForest = '1872:4992'; Swamp = '-4128:3552'; Mountain = '-3408:-1008'; Plains = '-2688:2112'
    Mistlands = '5712:-5088'; Ashlands = '672:-8928'; DeepNorth = '5952:7152'; Ocean = '-1250:-350'
}
$biomes = 'Meadows','BlackForest','Swamp','Mountain','Plains','Mistlands','Ashlands','DeepNorth','Ocean'
# Where a biome's variants are drawn instead, when its sample spot shows little of what they
# change: the Mountain sample is nearly all peaks, the variants change only its fir woods.
$variantSpot = @{ Mountain = '-7500:3000' }
function VariantSpot($biome) { if ($variantSpot.ContainsKey($biome)) { $variantSpot[$biome] } else { $spot[$biome] } }
# Velen's start island with the Sacrificial Stones and a set of pins drawn into the sheet, one in
# the mountains to its south (the preview has no location icons or snapshot of its own).
$pins = 'House:205:-119','Fire:158:-158','Bed:-210:0','Portal:-527:316','Hammer:738:-527','Point:-1265:422:x','Death:0:-1001','Boss:120:-1180','Tower:-240:359','Trader:-60:-120','Forge:-80:-1110'
$views = @('overview','wide:0:0:1000:1','start:0:0:600:1') + ($biomes | ForEach-Object { "tilesb${_}4:$($spot[$_]):4", "tilesb${_}2:$($spot[$_]):2" }) + @('tilesPinsStart4:0:60:4','tilesPinsMountain2:60:-1090:2')
& "$s\run.ps1" -Temple '-111:263' -Pin $pins -Views $views 2>&1 | Select-Object -Last 2
# The variants: [biome, alt biome name, view name, sample slug]. An --alt only fills cells of its
# own biome within its radius, so one run takes one variant per biome; two of the same biome on
# the same spot go to separate runs.
$variants = @(
    @('Meadows', 'Smalltree Meadows', 'tilesSmalltree2', 'smalltree-meadows', 0),
    @('Meadows', 'Birch Meadows', 'tilesBirch2', 'birch-meadows', 1),
    @('BlackForest', 'Kalhygge Black Forest', 'tilesClearcut2', 'clear-cut-wide', 0),
    @('BlackForest', 'Rock Black Forest', 'tilesRock2', 'rock-black-forest', 1),
    @('BlackForest', 'Pinetree Black Forest', 'tilesPinetree2', 'pinetree-black-forest', 2),
    @('Swamp', 'Bog Swamp', 'tilesBog2', 'bog-swamp', 0),
    @('Swamp', 'Hut Swamp', 'tilesHut2', 'hut-swamp', 1),
    @('Mountain', 'Wolf Mountain', 'tilesWolf2', 'wolf-mountain', 0),
    @('Mountain', 'Drake Mountain', 'tilesDrake2', 'drake-mountain', 1),
    @('Mountain', 'Fortress Mountain', 'tilesFortress2', 'fortress-mountain', 2),
    @('Plains', 'Lox Plains', 'tilesLox2', 'lox-plains', 0),
    @('Plains', 'Death Plains', 'tilesDeath2', 'death-plains', 1),
    @('Mistlands', 'Trees Mistlands', 'tilesTreesMist2', 'trees-mistlands', 0)
)
foreach ($run in 0..2)
{
    $v = @($variants | Where-Object { $_[4] -eq $run })
    $runViews = @($v | ForEach-Object { "$($_[2]):$(VariantSpot $_[0]):2" })
    # The clear-cut is shown close as well.
    if ($run -eq 0) { $runViews += "tilesClearcut4:$($spot['BlackForest']):4" }
    $alts = @($v | ForEach-Object { "$($_[1]):$(VariantSpot $_[0]):900" })
    & "$s\run.ps1" -Views $runViews -Alt $alts 2>&1 | Select-Object -Last 2
}
& "$s\out\Preview.exe" --pincat "$s\img\pincat.png"
& "$s\out\Preview.exe" --seacat "$s\img\seacat.png"
if (-not $Publish) { return }
# Publish only a complete set: a failed render must not wipe the published samples.
$need = @('overview','wide','start','tilesPinsStart4','tilesPinsMountain2','pincat','seacat','tilesClearcut4') + ($variants | ForEach-Object { $_[2] }) + ($biomes | ForEach-Object { "tilesb${_}4", "tilesb${_}2" })
$missing = $need | Where-Object { -not (Test-Path "$s\img\$_.png") }
if ($missing) { "render incomplete, samples not published: $($missing -join ', ')"; exit 1 }
New-Item -ItemType Directory -Force $shots | Out-Null
Get-ChildItem $shots -Filter 'atlas-sample-*.png' | Remove-Item -Force
Copy-Item "$s\img\overview.png" "$shots\atlas-sample-01-world.png"
Copy-Item "$s\img\wide.png" "$shots\atlas-sample-02-region.png"
Copy-Item "$s\img\start.png" "$shots\atlas-sample-03-start-island.png"
$n = 4
foreach ($b in $biomes)
{
    $slug = ($b -creplace '([a-z])([A-Z])','$1-$2').ToLower()
    Copy-Item "$s\img\tilesb${b}4.png" ("$shots\atlas-sample-{0:D2}-{1}-close.png" -f $n, $slug)
    Copy-Item "$s\img\tilesb${b}2.png" ("$shots\atlas-sample-{0:D2}-{1}-wide.png" -f $n, $slug)
    $n++
    foreach ($v in $variants | Where-Object { $_[0] -eq $b })
    {
        Copy-Item "$s\img\$($v[2]).png" ("$shots\atlas-sample-{0:D2}-{1}.png" -f $n, $v[3])
        if ($v[2] -eq 'tilesClearcut2') { Copy-Item "$s\img\tilesClearcut4.png" ("$shots\atlas-sample-{0:D2}-clear-cut-close.png" -f $n) }
        $n++
    }
}
Copy-Item "$s\img\tilesPinsStart4.png" ("$shots\atlas-sample-{0:D2}-pins-start.png" -f $n++)
Copy-Item "$s\img\tilesPinsMountain2.png" ("$shots\atlas-sample-{0:D2}-pins-mountains.png" -f $n++)
Copy-Item "$s\img\pincat.png" ("$shots\atlas-sample-{0:D2}-pin-catalogue.png" -f $n++)
Copy-Item "$s\img\seacat.png" ("$shots\atlas-sample-{0:D2}-sea-catalogue.png" -f $n++)
(Get-ChildItem $shots -Filter 'atlas-sample-*.png').Count
