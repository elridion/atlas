using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Inks the whole world once, on a worker thread, in the manner of a classic fantasy-novel map:
    /// a heavy coastline shaded by short wave strokes on the sea, and small pen glyphs for the land,
    /// one family per biome, scattered on jittered grids and drawn from north to south so that each
    /// glyph hides the ones behind it. Everything is deterministic for a world seed, so a redraw
    /// gives the same map. Unexplored land is hidden later by the fog, not here.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // Canvas: CanvasSize pixels across HalfExtent metres either side of the world centre.
        public const int CanvasSize = 4096;
        // The painted sheet's planes of CanvasSizeÂ² bytes: ink, one body mask per BodyKind, then
        // the pins' red ink (InkCanvas.m_pinInk).
        public const int SheetPlanes = 6;
        public const float HalfExtent = 10800f;
        public const float MetresPerPixel = 2f * HalfExtent / CanvasSize;
        // Vanilla's world edge (WorldGenerator.waterEdge): the frame is drawn there.
        public const float EdgeRadius = 10500f;

        // Every glyph size and spacing below is in these units; 1.5 canvas pixels (about 8 m).
        private const float G = 1.5f;
        // Trees use a smaller unit than other glyphs, packed as densely in their own unit, so woods
        // keep their texture while each tree reads as a tree at the closer zoom.
        private const float T = 0.75f * G;
        // Crowns hidden by the crowns in front (HideCoveredCrowns): a crown stays when an unbroken
        // CrownShown share of its outline stands CrownSliver pens clear of them, tested at
        // CrownSamples points. Crowns are looked up on cells of CrownCell T, more than the widest
        // scraped crown (the giant trees', 5.2 T) and the sliver.
        private const float CrownShown = 0.2f;
        private const float CrownSliver = 1.6f;
        private const int CrownSamples = 32;
        private const float CrownCell = 8f;

        // The coast's pen, in canvas pixels: barely heavier than the waves beside it, as on the
        // classic map.
        internal const float CoastWidth = 1.4f;
        // The sea's water lines follow the 1980 redrawn Middle-earth map; sizes in canvas pixels.
        // The waterline is traced, not drawn, WaterLine out from every lined shore and is always a
        // closed loop, never a loose end: narrow channels and water that carries no line count as
        // land for it, so it bridges a strait's mouth instead of stopping there. Lakes are lined
        // only when their water lies LakeMinWidth from the shore somewhere, about 40 m.
        private const float WaterLine = 2.8f;
        private const float LakeMinWidth = 8f;
        // Water farther than ChannelReach from any water WideWater from land is a channel: a
        // strait or inlet under about 115 m across (a narrower limit leaves straits lined on both
        // banks).
        private const float WideWater = 11f;
        private const float ChannelReach = 12f;
        // Islets under SmallIsletArea square canvas pixels (about 4,200 mÂ², some 65 m across) are
        // left out of the waterline's trace, which counts them as water. This steers where the
        // waves find the coast's bends: at a lower area the trace wraps round the Mistlands' islet
        // fringes, its bends sit on islets (which get no waves) and the main coast behind stays
        // bare.
        private const float SmallIsletArea = 150f;
        // Waves come in groups at the coast's pivotal points: WaveGroupMin to
        // WaveGroupMax strokes on rows ruled WaveRowPitch apart, long (WaveLongCrests) and short
        // (WaveShortCrests) in turn, a crest more by seed. Each starts at the coast, WaveShoreGap
        // off it, and runs out to sea with WaveSlotGap kept to any other stroke or shore ahead.
        // A headland pointing north or south may have up to WaveFloating strokes floating off its
        // tip, kept WaveFloatClear from land. Each crest is WaveCrest long and WaveLift high: with
        // a lift under half a pixel the tildes read as flat dashes at the opening zoom, and
        // two-crest short rows read as stubs beside the long ones.
        private const float WaveRowPitch = 3.2f;
        private const int WaveGroupMin = 3;
        private const int WaveGroupMax = 7;
        private const int WaveFloating = 2;
        private const float WaveShoreGap = 1.2f;
        private const float WaveFloatClear = 2f;
        private const float WaveSlotGap = 2.4f;
        private const float WaveCrest = 2.8f;
        private const float WaveLift = 0.6f;
        private const int WaveLongCrests = 5;
        private const int WaveShortCrests = 3;
        // Accents: points where the waterline's trace turns by AccentTurn radians over
        // AccentWindow either side (a bay's head, a headland's tip), at least AccentSpacing apart,
        // and on a straight stretch of about AccentPlain one with AccentPlainOdds. A group finds
        // its shore within AccentReach of the accent along each row. A coast faces north or south
        // when its normal is NorthSouth times steeper than level; straight coasts facing so get no
        // group, since no row meets them.
        private const float AccentWindow = 16f;
        private const float AccentTurn = 0.9f;
        private const float AccentSpacing = 40f;
        private const float AccentPlain = 140f;
        private const float AccentPlainOdds = 0.5f;
        private const float AccentReach = 24f;
        private const float NorthSouth = 1.2f;
        // Large lakes, whose middle lies LakeWaveWidth from the shore (about 60 m), get straight
        // hairlines instead of waves: still water lies flat. They are placed as
        // the sea's waves are, in groups of LakeGroupMin to LakeGroupMax at the lake shore's
        // bends, each line starting at the shore and running into the lake, long
        // (LakeLongDashes) and short (LakeShortDashes) units of LakeDash in turn, reaching at most
        // LakeReach of the way to the far shore so a line never spans the lake. A large lake with
        // no bend gets one group off its west or east shore level with its middle.
        private const float LakeWaveWidth = 11f;
        private const int LakeGroupMin = 3;
        private const int LakeGroupMax = 5;
        private const float LakeDash = 2.2f;
        private const int LakeLongDashes = 6;
        private const int LakeShortDashes = 3;
        private const float LakeReach = 0.4f;
        // A lake's group is one stack (PlaceLakeGroup): the first line's shore within
        // LakeFirstReach of the accent (the trace runs WaterLine inside the shore), each next
        // row's within LakeDrift of the row beside it (a shore at 30 degrees from level moves
        // 5.5 px a row), and no line under LakeLeastDashes (two read as stray marks). Straight
        // shores facing east or west get a group about every LakePlain.
        private const float LakeFirstReach = 8f;
        private const float LakeDrift = 6f;
        private const int LakeLeastDashes = 3;
        private const float LakePlain = 60f;
        private const float LakeRetry = 6f;
        // A lake's line starts this far inside the shore's pixel edge, under the coast's pen.
        private const float LakeOverlap = 0.5f;
        // Lakes between LakeMinWidth and LakeWaveWidth (about 40 to 60 m from shore to middle)
        // and islets get a stepped-down hand, so narrower lakes are lined too. StepGroupMin to
        // StepGroupMax lines a group; a small lake's are SmallLakeDash units, SmallLakeLongDashes
        // and SmallLakeShortDashes in turn; an islet's are the sea's tildes with IsletCrest crests, IsletLongCrests and
        // IsletShortCrests in turn (two-crest short rows read as stubs, so three stay), one group
        // in IsletSpacing so a chain of islets is not lined from end to end. A lake's or an islet
        // cluster's group with room for fewer than GroupFloor strokes is left out. The sea's hand
        // has no floor: its groups may draw one or two strokes where room is short.
        private const int GroupFloor = 3;
        private const int StepGroupMin = 3;
        private const int StepGroupMax = 4;
        private const float SmallLakeDash = 1.8f;
        private const int SmallLakeLongDashes = 5;
        private const int SmallLakeShortDashes = 3;
        private const float IsletCrest = 2.4f;
        // A cluster's group keeps this far from the coast's groups, as they keep from each other
        // (AccentSpacing). A wider spacing leaves about half the clusters bare beside a coast
        // group.
        private const float IsletSpacing = 40f;
        // Islets are grouped into clusters by single linkage on their bounds with IsletLink sheet
        // pixels between. Each cluster gets one group round its centroid (PlaceClusterGroup), its
        // strokes level; on a cluster whose cells spread IsletElongation times as far one way as
        // across, its open rows are shifted left or right to follow the chain while the strokes
        // themselves stay level.
        private const float IsletLink = 16f;
        private const float IsletClusterSpan = 48f;
        private const float IsletLinkMin = 3f;
        private const float IsletElongation = 1.5f;
        // How far a row may shift along a slanting chain per pixel it lies above or below the
        // centre: a chain near level would otherwise send its rows far apart.
        private const float IsletLean = 2.5f;
        // The sine of 25 degrees: a chain flatter than that gets no lean.
        private const float IsletLevelChain = 0.42f;
        // A group is sized to its cluster, so a small islet gets few waves. Its size is the side
        // of a square of its cells, in sheet pixels (8 for WavedIsletCells, 26 for 134 cells, 36
        // just under MajorLandCells):
        // GroupFloor strokes up to IsletSizeBase, one more every IsletSizeStep, IsletStrokesMax
        // at most; a long row IsletShortCrests and one more every IsletSizeLong, between one more
        // than that and IsletLongCrests, a short row two crests shorter, never under
        // IsletShortCrests (two-crest strokes read as stubs).
        private const float IsletSizeBase = 12f;
        private const float IsletSizeStep = 10f;
        private const int IsletStrokesMax = 5;
        private const float IsletSizeLong = 12f;
        private const int IsletLongCrests = 6;
        private const int IsletShortCrests = 3;
        // Each row's centre sits left and right of the group's centre in turn, IsletSway crests
        // (0.5 to 1.3 of it by seed) on an open row, a crest moved from one side's stroke to the
        // other's on a row through the land, so the stack reads as hand-drawn round one centre
        // instead of ruled on one vertical line.
        private const float IsletSway = 0.9f;
        // Land masses from MajorLandCells (about 36,000 mÂ², some 190 m across) are coast: they get
        // the coast's groups at their bends. With a higher threshold, masses lying in channels
        // off the main coast get islet groups, though they read as part of the coast. An islet
        // whose bounds come within IsletCoastGap (about 63 m, the reach of a coast stroke) of such
        // land is no islet either: it lies in the coast's own waves and gets no group of its own.
        private const int MajorLandCells = 250;
        private const float IsletCoastGap = 12f;
        // A stroke off an islet stops this far (about 53 m, four crests) short of other land, so
        // channels between islets stay open water and their strokes run out to the open sea
        // (IsletRoom); a smaller gap lets strokes fill the channel between close islets.
        private const float IsletChannelGap = 10f;
        // A row crossing less land than this (sheet pixels) only grazes a tip and takes no stroke.
        private const int IsletTipWidth = 3;
        // InkRecord.Wave.m_anchor of a stroke off an islet's shore, times its direction: close-ups
        // move it to their own shore as a coast stroke, and keep its water clear of land
        // TileIsletBand above and below its row too, since an islet's shore curves close round
        // the stroke.
        private const int IsletAnchor = 2;
        // Islets of fewer map cells than this (1,700 mÂ², about 40 m across) get no waves: the
        // start's outer islets are 13 to 22 cells, the specks round them 2 to 6.
        private const int WavedIsletCells = 12;
        private const float DistanceScale = 4f;
        private const float FrameGap = 7f;
        // Glyph feet stay inside this fraction of the frame's radius, clear of the inner ring.
        private const float EdgeFraction = 0.97f;
        // The thinnest pen line, in canvas pixels; thinner lines fade to grey at the opening zoom.
        private const float Pen = 1f;
        // Plains stipple dots, one pen width across.
        private const float StippleRadius = 0.6f;
        // The swamp's marsh lines: their grid step in glyph units, and the scale of the noise that
        // gathers them into patches (canvas pixels).
        private const float MarshStep = 3.2f;
        private const float MarshPatch = 26f * G;
        // The swamp's ancient dead trees: one candidate per DeadTreeStep tree units, standing
        // with DeadTreeOdds (times the grove's KeepOf) on the ground the game's ancient trees grow
        // on (WorldSource.m_swampGround). Two keep their feet outside an ellipse of
        // DeadTreeGapX by DeadTreeGapY tree units round each other, so neighbours stand close
        // but their limbs do not tangle into scribble.
        private const float DeadTreeStep = 9f;
        private const float DeadTreeOdds = 0.8f;
        private const float DeadTreeGapX = 6.5f;
        private const float DeadTreeGapY = 8f;
        private const int DeadTreeSalt = 27;
        // Patches of land or water smaller than this many map pixels (12 m each) are dropped as noise.
        private const int SpeckCells = 2;

        // Woods: the wood field's edge level (Woods.Build), and how far ground fills and
        // open-country glyphs keep from a wood (canvas pixels), so the outer crowns alone bound it.
        private const float WoodEdge = 0.5f;
        private const float WoodClearance = 5f;
        // Alt biomes (Grove): a thin grove keeps ThinKeep of its trees, a sparse one SparseKeep,
        // a light one LightKeep and a Lox Plains LoxKeep, by the cell's hash number SparseRoll
        // (a swamp's dead trees by their odds); a small-tree grove keeps SmalltreeBig of its
        // spots (hash number SmalltreeRoll) as full-size broadleaves and draws SmalltreeKeep of
        // the rest (hash number SmalltreeKeepRoll) as small trees: one on every spot crowded
        // their lobed crowns into each other; a dense grove's own grid is DenseSpacing of the rule's step with
        // every cell taken (about 1.4-1.5 times the trees; the rule's grids are already 90-95%
        // full), salted DenseSalt past the rule's. A clear-cut's stumps are few, as the Meadows'
        // tufts are, on one sheet grid of StumpStep glyph units, with StumpOdds inside the stands
        // a slow noise of StumpStand sheet pixels gathers them into.
        private const float ThinKeep = 0.33f;
        private const float SparseKeep = 0.5f;
        private const int SparseRoll = 90;
        private const float LightKeep = 0.75f;
        // The user chose a Lox Plains sparser than the game's (which only blocks one of the
        // Plains' two birches, about 0.75 of the trees): open grazing land for the herds, at
        // half the trees (raised from 0.35, the user's call).
        private const float LoxKeep = 0.5f;
        // The game keeps about 8 of the Meadows' ~47 big trees a zone there (0.17), but that many
        // read as too many among the small ones: halved, the user's call.
        private const float SmalltreeBig = 0.085f;
        private const int SmalltreeRoll = 96;
        private const float SmalltreeKeep = 0.75f;
        private const int SmalltreeKeepRoll = 97;
        private const float DenseSpacing = 0.85f;
        private const int DenseSalt = 50;
        private const float StumpStep = 3.8f;
        private const float StumpOdds = 0.5f;
        private const float StumpFloor = 0.5f;
        // How far (glyph units) round a stump the other glyphs' footprints must keep.
        private const float StumpClear = 0.6f;
        private const float StumpStand = 40f;
        private const float ClearCutStipple = 0.3f;
        // A boulder field's (Grove.Rocky) boulders, as the stumps are: one sheet grid of
        // BoulderStep glyph units, BoulderOdds inside the scree a slow noise of BoulderStand sheet
        // pixels gathers them into and BoulderFloor of that between, kept BoulderClear glyph units
        // past their own size off every glyph's footprint. Its few lone pines (vanilla keeps
        // firtree_blackforest2, 1-2 a zone) stand on a per-level grid of LonePineStep tree units
        // with LonePineOdds.
        private const float BoulderStep = 5f;
        private const float BoulderOdds = 0.6f;
        private const float BoulderFloor = 0.2f;
        private const float BoulderStand = 30f;
        private const float BoulderClear = 0.6f;
        // How far (sheet pixels) a boulder's whole sheet-sized footprint keeps off the water, so
        // it stands clear of the coast's line and of the lake lines drawn on the water beside it.
        private const float BoulderShore = 1.5f;
        // The share of boulders with a smaller one beside them.
        private const float BoulderPair = 0.3f;
        private const int BoulderSalt = 39;
        private const float LonePineStep = 14f;
        private const float LonePineOdds = 0.15f;
        private const int LonePineSalt = 40;
        // Its open ground takes the Plains' dry stipple (the user's call), with the Plains' odds
        // RockStipple, drawn after the boulders and kept RockStippleClear glyph units off them
        // and off every glyph's footprint.
        private const float RockStipple = 0.45f;
        private const float RockStippleClear = 0.6f;
        private const int RockStippleSalt = 41;
        // Wood ground the wash darkens but no tree stands on (DrawWoodlessGround; mostly the
        // steep slopes SteepGround keeps the Black Forest's and the Meadows' trees off): a spot
        // WoodlessGap tree units from every tree's footprint takes its biome's open fill, the
        // Plains' stipple with WoodlessStipple odds (lighter than the Plains' own) in the Black
        // Forest and the Plains, the Meadows' grass tufts there, kept RockStippleClear glyph
        // units off every glyph's footprint.
        private const float WoodlessGap = 6f;
        private const float WoodlessStep = 4f;
        private const float WoodlessStipple = 0.45f;
        private const int WoodlessSalt = 43;
        // The Meadows' woodless ground takes the open meadows' grass tufts on a finer sheet grid
        // of its own (WoodlessGrassStep glyph units, WoodlessGrassOdds), so a steep strip reads
        // as grassland, not bare wash.
        private const float WoodlessGrassStep = 4f;
        private const float WoodlessGrassJitter = 0.6f;
        private const float WoodlessGrassOdds = 0.6f;
        private const int WoodlessGrassSalt = 45;
        // A copse's tree (PlaceCopses): its foot this share of its height (8 T) south of the map
        // pixel's centre, so the pixel lies midway between the foot and the crown's centre
        // (0.6 of the height up), and moved at most half this share of a map pixel either way.
        private const float CopseLift = 0.3f;
        private const float CopseJitter = 0.6f;
        private const int CopseSalt = 42;
        // Mountain trees keep off the peaks' bases (ClearOfPeakBases): the base reaches this share
        // of the peak's width either side of its foot (its corners lie at most 0.56 out), and
        // PeakBaseGap sheet pixels below it (the body's skirt is 2); a pine's crown is about
        // PeakCrownWidth of its height either side. Peaks are filed in PeakCell squares.
        private const float PeakBaseWidth = 0.56f;
        private const float PeakBaseGap = 2.5f;
        private const float PeakCrownWidth = 0.3f;
        private const float PeakCell = 64f;
        // Mountain trees may stand on a peak's or ridge's lower slopes, their crowns within
        // SlopeTop of its height over its foot (ClearOfPeakBases), drawn just after it: SlopeOrderGap
        // after its foot, plus SlopeOrderStep per pixel further south, so trees on one slope keep
        // their own order (SlopeOrder).
        private const float SlopeTop = 0.35f;
        private const float SlopeOrderGap = 0.02f;
        private const float SlopeOrderStep = 0.001f;

        // Terrain thresholds: metres above the water and height change per metre of slope.
        private const float PeakHeight = 50f;
        // Deep North ground this high carries a peak on a real summit and hills on its other
        // hilltops. The game's Deep North rises in gentle uplands (GetDeepNorthHeight has none of
        // the Mountains' doubling, ridge noise or roughness; only 6-11% of its ground over 45 m is
        // steeper than 30 degrees, against 55% of the Mountains' over 50 m) and its snow forests
        // grow up them, so it gets no filler peaks and its woods run uphill.
        private const float DeepNorthSummitHeight = 45f;
        // Summits and hilltops: a map pixel (12 m) is a summit when it is the highest within
        // SummitCells and rises SummitProminence metres above the lowest point within
        // ProminenceCells; hilltops likewise with smaller numbers. Filler peaks mass round a
        // summit's glyph as they do round each other, overlapping it, but one standing within
        // SummitCore of the summit's width from its centre may neither hide more than the
        // summit's lower SummitShown from in front nor rise above its apex from behind
        // (NearSummit), so the summit still stands out. Kept a fixed distance away instead, they
        // leave most of a range round its summits bare ground that does not read as high.
        private const int SummitCells = 6;
        private const float SummitProminence = 20f;
        private const int HilltopCells = 4;
        // One rule for every hill country: the game builds the Meadows' and the Plains' heights with
        // the same code (GetMeadowsHeight, GetPlainsHeight), and their open land has the same relief
        // (mean 25-27 m over 10 cells on Velen and demoo), so the Meadows get no finer search.
        private const float HillProminence = 6f;
        private const int ProminenceCells = 10;
        // Every part of a hill keeps this far (canvas pixels) off water and river courses: its
        // pen's half width, the coast's, and a pixel of parchment between.
        private const float HillWaterGap = 3f;
        // A hill that would come closer first drops its dome behind, then shrinks by
        // HillShrinkStep of its width down to HillMinScale of it, crown kept on the hilltop.
        // Failing that its hilltop is moved in rings HillShiftStep of its width apart out to
        // HillShiftReach of it (one width), onto ground at most HillShiftDrop of the hill's
        // prominence below its top, and keeps HillGap (canvas pixels) off the hills and summit
        // peaks already placed.
        private const float HillShrinkStep = 0.1f;
        private const float HillMinScale = 0.6f;
        private const float HillShiftStep = 0.1f;
        private const float HillShiftReach = 1f;
        private const float HillShiftDrop = 0.5f;
        private const float HillGap = 1.5f;
        private const float SummitCore = 0.3f;
        private const float SummitShown = 0.5f;
        // Mist banks: the gap between a bank's three lines (canvas pixels) and the step between
        // points along a line. Banks lie only in the mist area, the mask's green from MistArea
        // (0.84, a forest factor under 1.15, the game's "Mist area" vegetation), and thin with
        // their height over the lowest ground within MistReach map pixels (60 m; the game's
        // ParticleMist emits above the lowest ground within 50 m, up to about 10 m over its
        // emitter on the lower slopes): all kept up to MistFull metres, none from MistClear, so
        // the mist fills the hollows and the knolls stand out of it.
        private const float MistGap = 2f * G;
        private const float MistStep = 1.5f * G;
        private const int MistArea = 215;
        private const int MistReach = 5;
        private const float MistFull = 6f;
        private const float MistClear = 18f;
        private const float MistOdds = 0.9f;
        // Deep North snow marks (chevrons): the scale of the noise their bands follow (canvas
        // pixels) and how close to a crest line of it a chevron must lie (1 is on the crest).
        // Only a mark of the snow, not a landform: the game has no ice ridges on land.
        private const float SnowMarkScale = 70f * G;
        private const float SnowMarkBand = 0.9f;
        // How far a chevron keeps from the shore (in the rules' shore units) and from the next
        // chevron (in G).
        private const float SnowMarkShore = 4f * G;
        private const float SnowMarkGap = 1.5f;
        // Rivers: the pen for a river, a stream at its mouth (thinning to Pen at its source), the extra
        // width scraped clear on either side, and the step between points along a course (canvas px).
        private const float RiverWidth = 1.2f;
        private const float StreamMouth = 1.1f;
        private const float RiverBand = 2.4f;
        private const float RiverStep = 1.5f;
        // River ink keeps this far (canvas pixels) from any water the coast already draws, or it
        // runs along a bank or a lake shore and the two lines merge into a smear.
        private const float RiverShoreClear = 2f;
        // A Meadows grass tuft keeps every end of its two blades this far (sheet pixels, thinned
        // by the level's pen) from water and river courses: drawn after the rivers since they no
        // longer scrape it, a tuft at a bank or shore otherwise stood half on the water.
        private const float TuftWaterClear = 1.5f;
        // Mistlands ground this steep (height per metre) is broken crag: scarps mark it, giant trees
        // stand sparser there, and filler spires stand and mist keeps off from 1.3 times it. Summit spires stand on the real summits: the highest
        // map pixel within SpireCells that rises SpireProminence metres over its surroundings.
        private const float SpireSlope = 0.45f;
        private const int SpireCells = 5;
        private const float SpireProminence = 25f;
        // A summit crag that would stand outside the Mistlands shrinks by SpireShrink, at most
        // SpireShrinkSteps sizes in all, before it is left out.
        private const float SpireShrink = 0.85f;
        private const int SpireShrinkSteps = 4;
        private const int SpireAnchorSteps = 3;
        // The share of a summit crag's height, from its foot up, that must stand over land, however
        // far it slides: above that its top may rise over water, but a crag on an islet with only
        // its foot on land stands in the sea.
        private const float SpireLandBase = 0.3f;
        // The game's rock spires (Mistlands_RockSpire1) stand down to 10 m under the water line,
        // so a spire may stand in the Mistlands' sea where it is at most SpireDepth metres deep,
        // its foot SpireWater (canvas pixels) off land and its waterline clear of land, on a level
        // waterline as the sea thorns do. The filler grid takes SpireWaterOdds of its cells
        // there: the game's density is not representable, and more fill the shallows round the
        // islet fields.
        private const float SpireDepth = 10f;
        private const float SpireWater = 4f;
        private const float SpireWaterOdds = 0.25f;
        // Where a real summit lies on its glyph, as a share of the glyph's height up from the
        // foot. The glyphs are drawn in elevation but placed on a plan, and whatever stands
        // south of where it belongs lies over ground that is not its own: with the apex on the
        // summit the whole body hung south of it, over the next biome or the coast. Centred a
        // little below the middle, the base stays close to the summit's own ground and the top
        // rises over the land behind, as the glyphs in front already hide the ones behind.
        private const float SummitAnchor = 0.4f;
        // A summit's peak that fits nowhere along its centre line shrinks by SummitShrink, at most
        // SummitShrinkSteps sizes in all, before it is left out.
        private const float SummitShrink = 0.85f;
        private const int SummitShrinkSteps = 4;
        // Scarps: the Mistlands' broken rock, where trees stand sparse, as small cliff edges along the
        // contour with hachures falling downhill. On ground of SpireSlope and steeper, on a grid
        // of ScarpStep G with ScarpOdds of its cells drawn, ScarpShore (canvas pixels) off the
        // water so the coast's islets and waves stay clear, clear of the mist banks' lines; a
        // mark gives way to a neighbour of higher priority within ScarpGap of its length.
        // ScarpTick is the longest hachure (G). ScarpClear (G) is the paper kept between a scarp and
        // every placed glyph's footprint (GlyphBox), trees, spires and peaks alike.
        private const float ScarpStep = 6f;
        private const float ScarpOdds = 0.85f;
        private const float ScarpShore = 4f;
        private const float ScarpGap = 0.75f;
        private const float ScarpTick = 1.2f;
        private const float ScarpJitter = 0.6f;
        private const float ScarpClear = 1f;
        // A giant tree's crown ends at least this far above its foot, and its trunk and roots stand
        // within this half width (both in the tree's size).
        private const float GiantCrownFoot = 2.9f;
        private const float GiantTrunkHalf = 1.5f;
        // Giant trees on the Mistlands' flat ground stand on a 7 T grid with GiantOdds of its cells
        // taken, GiantMistOdds inside the mist area: the game grows about 126 shoots a zone there
        // and 226 outside it, where the two that need a forest factor of 1 to 4 join in. The big
        // shoots (YggaShoot1-4, tilt up to 80 degrees, a terrain delta of 2-3 m) grow on the rough
        // ground too, which is about 63% of Mistlands land at 12 m pixels; there they stand
        // sparser, on a grid of their own (GiantSteepStep T, GiantSteepOdds, salt GiantSteepSalt),
        // so the scarps keep most of the rock. Flat or steep is the sheet's slope at the spot's
        // map pixel, so close-ups split the ground as the sheet does.
        private const float GiantOdds = 0.9f;
        private const float GiantMistOdds = 0.6f;
        private const float GiantSteepStep = 16f;
        private const float GiantSteepOdds = 0.5f;
        private const int GiantSteepSalt = 38;
        // No Meadows or Black Forest tree grows on ground steeper than 30 degrees (the game's
        // maxTilt 30 on every one, 20 on the oaks), so their woods keep off broken ground: where
        // most land map pixels within SteepWindow round the spot are tan 30 or steeper (read from
        // the world's map pixels, so close-ups agree). Tested per pixel (4-9% of their land) it cut
        // bare strips along every valley's banks, which 12 m pixels make read steep; the user
        // chose the window, as the Mountains' firs have (MarkMountainForest). Not in the
        // Mountains, whose wood field already weighs the slope, nor the Deep North or the
        // Mistlands, whose giant shoots tilt up to 80 degrees.
        private const float TreeTilt = 0.577f;
        private const int SteepWindow = 2;
        // Sea thorns stand in the Ashlands' sea down to this depth (metres), as the game's
        // cliff_ashlands8 do, on a grid of ThornStep with ThornOdds of its cells taken, their
        // foot at least ThornWater (canvas pixels) from land so the waterline stays on water.
        private const float ThornDepth = 25f;
        // The game stands about one group a 64 m zone there, so even this sparser scatter
        // keeps them the Ashlands' sea's own mark.
        private const float ThornStep = 16f;
        private const float ThornOdds = 0.5f;
        private const float ThornWater = 5f;
        // A hill moved off water (ShiftFoot) tries these angles (degrees) from straight away from it.
        private static readonly float[] ShiftAngles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f };
        // Lava fields: the spacing of their engraved hatch (canvas pixels, across the lines).
        private const float LavaHatch = 2.6f;

        private readonly WorldSource m_world;
        private readonly CancellationToken m_cancel;
        private readonly InkCanvas m_canvas;
        // Level of detail: 1 on the sheet; a detail tile draws its ground fills and trees at 1/lod
        // the size and spacing (m_g, m_t) and with pens and glyph margins 1/lod as wide (m_pen).
        private readonly float m_lod = 1f;
        private readonly float m_g = G;
        private readonly float m_t = T;
        private readonly float m_pen = 1f;
        // A detail tile's ground and the sheet rectangle it covers; null on the sheet.
        private readonly TileGrid m_grid;
        private readonly float m_left, m_top, m_right, m_bottom;
        // A close-up's scatter beyond its own rectangle (ScatterArea); empty on the sheet.
        private Rect m_scatter;
        private readonly List<Vector2> m_points = new List<Vector2>(16);
        private readonly List<bool> m_pathInside = new List<bool>(32);
        // Canvas pixels covered by peaks and spires, which the mist banks drawn later must not cut.
        private readonly List<Glyph> m_glyphs = new List<Glyph>(200000);
        // Real summits' peaks placed so far: foot (x, y), glyph width (z) and height (w).
        private readonly List<Vector4> m_summits = new List<Vector4>(1024);
        // Sea thorns placed so far, which the waves keep off (DrawWaves).
        private readonly List<Glyph> m_thorns = new List<Glyph>(1024);
        // Ice floes sit on the water, so they are drawn after the coast's wave accents, over them.
        private readonly List<Glyph> m_floes = new List<Glyph>(4096);
        // Floes: a grid of FloeStep G with FloeOdds of its cells taken (fine enough to hit the
        // narrow coastal ice, mostly within 24-36 m of land), and FloeShoreGap canvas pixels
        // kept between a floe's outline and land.
        private const float FloeStep = 10f;
        private const float FloeOdds = 0.5f;
        private const float FloeShoreGap = 1.5f;
        private float[] m_coast;
        // Map pixels of water joined to the sea, and canvas pixels of sea.
        private bool[] m_seaCell;
        private byte[] m_sea;
        private byte[] m_land;
        private byte[] m_waterDistance;
        // Distance from land masses of at least MajorLandCells, scaled like m_waterDistance.
        private byte[] m_majorDistance;
        // A land mass under MajorLandCells outside the Mistlands (MajorLand): the centroid of its
        // cells and its bounds on the sheet, and its size in map cells. Those from
        // WavedIsletCells anchor a cluster of islets (DrawIsletWaves); smaller specks only join one.
        private struct Islet
        {
            public Vector2 m_at;
            public float m_left, m_right, m_top, m_bottom;
            public int m_cells;
        }

        private readonly List<Islet> m_islets = new List<Islet>();
        // Per map cell, the index in m_islets plus one of the islet it belongs to, 0 elsewhere.
        private int[] m_isletCell;
        private byte[] m_landDistance;
        // Canvas pixels inside a wood, and each pixel's distance to the nearest of them.
        private byte[] m_wood;
        private byte[] m_woodDistance;
        // Canvas pixels on a river's inked course, which trees keep off (sheet only).
        private byte[] m_riverMask;

        private enum Kind : byte
        {
            Peak,
            Hill,
            Broadleaf,
            Conifer,
            PlainsTree,
            DeadTree,
            Spire,
            GiantTree,
            Thorn,
            Mist,
            IceFloe,
            // A pin (PinGlyphs), its glyph in the seed's low byte and PinStruck set when checked.
            Pin,
            // A small-tree grove's small beech (SmallTrees), drawn instead of most of its broadleaves.
            SmallTree,
            // A lone oak on open Meadows (Oaks).
            Oak,
            // A stretch of mountain wall with no summit of its own (Ridge), beside a massif's peaks.
            Ridge,
        }

        // Pins (the Sacrificial Stones among them) are drawn into the sheet as glyphs of their own,
        // part of the map: a box sized for what each stands for (PinSheetSize), against a tree of
        // about 13 pixels: a campfire or a bed smaller, a longhouse about a tree, a tower, a cave
        // or the Stones larger. They are sorted in with the trees and other glyphs by their feet,
        // so trees in front may cover part of one, up to MarkCover of its box (LimitMarkCover);
        // any other glyph in front whose footprint reaches its core (the middle, MarkCore of its
        // box in from each side) is left out. Ground marks under one are scraped by its body.
        private const float MarkCore = 0.3f;
        private const float MarkCover = 0.45f;

        // A pin's box on the sheet, in sheet pixels; the glyph fills about 0.8 of it.
        internal static float PinSheetSize(PinGlyph glyph)
        {
            switch (glyph)
            {
                case PinGlyph.Point: return 10f;
                case PinGlyph.Fire:
                case PinGlyph.Bed:
                case PinGlyph.Death: return 12f;
                case PinGlyph.Hammer:
                case PinGlyph.Cauldron: return 13f;
                case PinGlyph.Portal:
                case PinGlyph.House:
                case PinGlyph.Trader:
                case PinGlyph.Tent:
                case PinGlyph.Forge: return 16f;
                case PinGlyph.Boss: return 18f;
                case PinGlyph.Tomb: return 20f;
                case PinGlyph.Cavern:
                case PinGlyph.Tower: return 22f;
                case PinGlyph.StoneCircle: return 26f;
                default: return 13f;
            }
        }
        private const int PinStruck = 0x100;
        private readonly List<Glyph> m_marks = new List<Glyph>();

        // A mark's core and foot (its sort order), filed in PeakCell squares.
        private struct MarkZone
        {
            public Vector4 m_core;
            public float m_foot;
        }

        private readonly Dictionary<int, List<MarkZone>> m_markCells = new Dictionary<int, List<MarkZone>>();

        // The pins' glyphs and their cores, for the sheet and every close-up alike.
        private void FindMarks(IList<SheetPin> pins)
        {
            if (pins == null) return;
            foreach (SheetPin pin in pins)
            {
                Glyph g = MarkOf(pin, m_lod);
                m_marks.Add(g);
                Vector4 box = GlyphBox(g);
                float dx = (box.z - box.x) * MarkCore, dy = (box.w - box.y) * MarkCore;
                var zone = new MarkZone { m_core = new Vector4(box.x + dx, box.y + dy, box.z - dx, box.w - dy), m_foot = g.m_y };
                int x0 = (int)Math.Floor(zone.m_core.x / PeakCell), x1 = (int)Math.Floor(zone.m_core.z / PeakCell);
                int y0 = (int)Math.Floor(zone.m_core.y / PeakCell), y1 = (int)Math.Floor(zone.m_core.w / PeakCell);
                for (int cy = y0; cy <= y1; cy++)
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        int key = PeakCellKey(cx, cy);
                        if (!m_markCells.TryGetValue(key, out List<MarkZone> cell)) m_markCells[key] = cell = new List<MarkZone>(4);
                        cell.Add(zone);
                    }
            }
        }

        // A pin's glyph at a level of detail. A close-up's pins are smaller by its level of
        // detail, as its trees (InkCanvas.Pin). The glyph's place is its foot, below the pin's
        // place for the Stones and the raised glyphs (PinGlyphs.Sink).
        private static Glyph MarkOf(SheetPin pin, float lod)
        {
            float x = (pin.m_x + HalfExtent) / MetresPerPixel, y = (HalfExtent - pin.m_z) / MetresPerPixel;
            float size = PinSheetSize(pin.m_glyph) / lod;
            var g = new Glyph { m_x = x, m_y = y + PinGlyphs.Sink(pin.m_glyph) * size, m_size = size, m_kind = Kind.Pin, m_seed = (int)pin.m_glyph | (pin.m_checked ? PinStruck : 0) };
            g.m_order = g.m_y;
            return g;
        }

        // A pin's box (GlyphBox) as a close-up at lod places it, which its trees and grass are
        // tested against (LimitMarkCover, DropHiddenBehindMarks, OnAMark); RecordChange tests a
        // pin change by it.
        internal static Rect MarkBox(SheetPin pin, float lod)
        {
            Vector4 box = GlyphBox(MarkOf(pin, lod));
            return Rect.MinMaxRect(box.x, box.y, box.z, box.w);
        }

        private static bool IsMark(Kind kind) => kind == Kind.Pin;

        // Meadow grass tufts drawn under a pin, the ground fills being laid before any glyph.
        // Those standing in front of a pin (feet further south than its place in the order) are
        // inked again after the glyphs (DrawGrassOverMarks), so a tuft before a tower stands
        // over its foot as a tree would.
        private readonly List<Vector2> m_markGrass = new List<Vector2>();
        // Tufts in front of a lone oak's foot (TuftByOak), inked again after the glyphs.
        private readonly List<Vector2> m_frontGrass = new List<Vector2>();

        private bool OnAMark(Vector4 box)
        {
            foreach (Glyph mark in m_marks)
                if (Overlap(box, GlyphBox(mark)) > 0f) return true;
            return false;
        }

        private void DrawGrassOverMarks()
        {
            if (m_markGrass.Count == 0 && m_frontGrass.Count == 0) return;
            m_canvas.m_layer = InkLayer.Background;
            foreach (Vector2 tuft in m_frontGrass) Grass(tuft.x, tuft.y, m_g);
            foreach (Vector2 tuft in m_markGrass)
            {
                var box = new Vector4(tuft.x - 1.6f * m_g, tuft.y - 2.8f * m_g, tuft.x + 1.2f * m_g, tuft.y);
                foreach (Glyph mark in m_marks)
                {
                    if (tuft.y <= mark.m_order || Overlap(box, GlyphBox(mark)) <= 0f) continue;
                    Grass(tuft.x, tuft.y, m_g);
                    break;
                }
            }
        }

        // A close-up's marks take the sheet's order and relief cover (OrderMarks).
        private void TakeMarkOrders(InkRecord record)
        {
            if (record.m_pinOrders == null || record.m_pinOrders.Length != m_marks.Count) return;
            for (int i = 0; i < m_marks.Count; i++)
            {
                Glyph mark = m_marks[i];
                mark.m_order = record.m_pinOrders[i];
                mark.m_cover = record.m_pinCovers[i];
                m_marks[i] = mark;
            }
        }

        // A pin's cover is measured on CoverSamples x CoverSamples points over its box: a point is
        // covered when the drawn shape of a glyph in front lies over it (Covers), and the cover is
        // the share of points any glyph in front covers, so crowns over each other count once.
        internal const int CoverSamples = 12;

        private static Vector2 CoverPoint(Vector4 box, int k) =>
            new Vector2(box.x + (k % CoverSamples + 0.5f) / CoverSamples * (box.z - box.x),
                box.y + (k / CoverSamples + 0.5f) / CoverSamples * (box.w - box.y));

        // Whether a glyph's drawn shape lies over a sheet point: a round crown's scraped ellipse,
        // a hill's dome, or for pines and the other relief the triangle from the top of their
        // footprint down to their foot.
        private bool Covers(Glyph g, Vector2 p)
        {
            if (RoundCrown(g, out Crown crown))
            {
                float dx = (p.x - crown.m_cx) / CrownEraseX(crown), dy = (p.y - crown.m_cy) / CrownEraseY(crown);
                return dx * dx + dy * dy < 1f;
            }
            Vector4 box = GlyphBox(g);
            if (p.y > g.m_y || p.y < box.y) return false;
            float half = (box.z - box.x) * 0.5f;
            if (g.m_kind == Kind.Hill || g.m_kind == Kind.Ridge)
            {
                float hx = (p.x - g.m_x) / half, hy = (g.m_y - p.y) / (g.m_y - box.y);
                return hx * hx + hy * hy < 1f;
            }
            float t = (p.y - box.y) / Math.Max(1e-3f, g.m_y - box.y);
            return Math.Abs(p.x - g.m_x) < half * t;
        }

        // Adds a glyph's cover to a pin's points (covered), when the points it newly covers keep
        // the pin within limit points; returns whether it did. Only the points the pin's own body
        // takes count, so the cover is of the drawn pin, not of the paper round it in its box.
        private bool AddCover(Glyph g, Glyph mark, Vector4 pin, bool[] covered, ref int count, int limit)
        {
            bool[] body = CoverBody(mark);
            int added = 0;
            for (int k = 0; k < covered.Length; k++)
                if (body[k] && !covered[k] && Covers(g, CoverPoint(pin, k))) added++;
            if (count + added > limit) return false;
            if (added == 0) return true;
            for (int k = 0; k < covered.Length; k++)
                if (body[k] && !covered[k] && Covers(g, CoverPoint(pin, k))) covered[k] = true;
            count += added;
            return true;
        }

        // The points of a pin's box its body takes (the PinGlyphs mark at CoverSamples pixels).
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool[]> s_coverBodies =
            new System.Collections.Concurrent.ConcurrentDictionary<int, bool[]>();

        // Thin parts (the boss's horns, a bed's posts, the gap round every line) are eroded away,
        // so the share is of the pin's solid core: counted in, the horns let trees hide the whole
        // skull. A glyph with too little core left (the dot) keeps its whole body.
        private const int CoverCoreLeast = 10;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool[]> s_pinBodies =
            new System.Collections.Concurrent.ConcurrentDictionary<int, bool[]>();

        // The points of a pin's box its whole body takes, gap included.
        private static bool[] PinBody(Glyph mark) => PinBodyOf((PinGlyph)(mark.m_seed & 0xFF));

        // A glyph's body on CoverSamples x CoverSamples points of its box, rows running south;
        // also where AtlasView's hover finds a pin's drawn shape.
        internal static bool[] PinBodyOf(PinGlyph glyph) => s_pinBodies.GetOrAdd((int)glyph, key =>
        {
            PinMark drawn = PinGlyphs.Draw((PinGlyph)key, CoverSamples);
            var body = new bool[CoverSamples * CoverSamples];
            for (int k = 0; k < body.Length; k++) body[k] = drawn.m_body[k] > 127;
            return body;
        });

        private static bool[] CoverBody(Glyph mark) => s_coverBodies.GetOrAdd(mark.m_seed & 0xFF, glyph =>
        {
            int n = CoverSamples;
            bool[] body = PinBody(mark);
            var core = new bool[n * n];
            int count = 0;
            for (int y = 1; y < n - 1; y++)
                for (int x = 1; x < n - 1; x++)
                {
                    int k = y * n + x;
                    if (!body[k] || !body[k - 1] || !body[k + 1] || !body[k - n] || !body[k + n]) continue;
                    core[k] = true;
                    count++;
                }
            return count >= CoverCoreLeast ? core : body;
        });

        private static int CoverLimit(Glyph mark)
        {
            int points = 0;
            foreach (bool b in CoverBody(mark)) if (b) points++;
            return (int)(MarkCover * points);
        }

        // Leaves out the trees in front of each pin beyond MarkCover of its points after the relief
        // in front of it (OrderMarks, m_cover): the trees drawn after it that overlap it are taken
        // in order of their feet, north first, each adding the points it newly covers, and the
        // rest are left out. A close-up decides alike, as it scatters every tree that can reach a
        // pin near it (ScatterArea) and takes each pin's order and relief cover from the record.
        // With deadTrees the sheet's dead trees are limited so instead, before the trees, and the
        // cover of those kept joins the pin's m_cover: close-ups replay the dead trees the sheet
        // kept and take that cover from the record with the relief's, so their own trees get the
        // same share as the sheet's.
        private void LimitMarkCover(int first, bool deadTrees = false)
        {
            if (m_marks.Count == 0) return;
            var dropped = new HashSet<int>();
            var candidates = new List<int>();
            for (int m = 0; m < m_marks.Count; m++)
            {
                Glyph mark = m_marks[m];
                Vector4 pin = GlyphBox(mark);
                candidates.Clear();
                for (int i = first; i < m_glyphs.Count; i++)
                {
                    Glyph g = m_glyphs[i];
                    bool limited = deadTrees ? g.m_kind == Kind.DeadTree : IsTree(g.m_kind);
                    if (!limited || Order(g) <= mark.m_order || dropped.Contains(i)) continue;
                    if (Overlap(GlyphBox(g), pin) > 0f) candidates.Add(i);
                }
                candidates.Sort((a, b) =>
                {
                    int byFoot = m_glyphs[a].m_y.CompareTo(m_glyphs[b].m_y);
                    return byFoot != 0 ? byFoot : m_glyphs[a].m_x.CompareTo(m_glyphs[b].m_x);
                });
                var covered = mark.m_cover != null ? (bool[])mark.m_cover.Clone() : new bool[CoverSamples * CoverSamples];
                int count = 0;
                foreach (bool c in covered) if (c) count++;
                int limit = CoverLimit(mark);
                foreach (int i in candidates)
                    if (!AddCover(m_glyphs[i], mark, pin, covered, ref count, limit)) dropped.Add(i);
                if (!deadTrees) continue;
                mark.m_cover = covered;
                m_marks[m] = mark;
            }
            if (dropped.Count == 0) return;
            int write = first;
            for (int i = first; i < m_glyphs.Count; i++)
                if (!dropped.Contains(i)) m_glyphs[write++] = m_glyphs[i];
            m_glyphs.RemoveRange(write, m_glyphs.Count - write);
        }

        // A tree behind a pin whose outline shows less than HiddenShown outside the pin's body is
        // left out: scraped round the pin, its last slivers read as stray marks, not a tree.
        private const float HiddenShown = 0.25f;
        private const int HiddenSamples = 24;

        private void DropHiddenBehindMarks(int first)
        {
            if (m_marks.Count == 0) return;
            var dropped = new HashSet<int>();
            foreach (Glyph mark in m_marks)
            {
                Vector4 pin = GlyphBox(mark);
                bool[] body = PinBody(mark);
                for (int i = first; i < m_glyphs.Count; i++)
                {
                    Glyph g = m_glyphs[i];
                    if (!GivesWayAsTree(g.m_kind) || Order(g) > mark.m_order || dropped.Contains(i)) continue;
                    if (Overlap(GlyphBox(g), pin) <= 0f) continue;
                    int shown = 0;
                    for (int k = 0; k < HiddenSamples; k++)
                        if (!InBody(pin, body, OutlinePoint(g, k / (float)HiddenSamples))) shown++;
                    if (shown < HiddenShown * HiddenSamples) dropped.Add(i);
                }
            }
            if (dropped.Count == 0) return;
            int write = first;
            for (int i = first; i < m_glyphs.Count; i++)
                if (!dropped.Contains(i)) m_glyphs[write++] = m_glyphs[i];
            m_glyphs.RemoveRange(write, m_glyphs.Count - write);
        }

        // A point at share t round a tree's outline: its crown's loop, or the triangle of a pine.
        private static Vector2 OutlinePoint(Glyph g, float t)
        {
            if (RoundCrown(g, out Crown crown))
            {
                float angle = t * Mathf.PI * 2f;
                return new Vector2(crown.m_cx + Mathf.Cos(angle) * crown.m_rx, crown.m_cy + Mathf.Sin(angle) * crown.m_ry);
            }
            Vector4 box = GlyphBox(g);
            float half = (box.z - box.x) * 0.5f;
            var apex = new Vector2(g.m_x, box.y);
            var left = new Vector2(g.m_x - half, g.m_y);
            var right = new Vector2(g.m_x + half, g.m_y);
            float s = t * 3f;
            if (s < 1f) return Vector2.Lerp(apex, right, s);
            if (s < 2f) return Vector2.Lerp(right, left, s - 1f);
            return Vector2.Lerp(left, apex, s - 2f);
        }

        // Whether a sheet point lies on a pin's body (CoverBody, over its box).
        private static bool InBody(Vector4 pin, bool[] body, Vector2 p)
        {
            if (p.x < pin.x || p.x >= pin.z || p.y < pin.y || p.y >= pin.w) return false;
            int cx = Mathf.Clamp((int)((p.x - pin.x) / (pin.z - pin.x) * CoverSamples), 0, CoverSamples - 1);
            int cy = Mathf.Clamp((int)((p.y - pin.y) / (pin.w - pin.y) * CoverSamples), 0, CoverSamples - 1);
            return body[cy * CoverSamples + cx];
        }

        // How far round a pin's place a close-up scatters trees, in sheet pixels: the largest pin's
        // box (PinSheetSize, lifted) and the tallest tree that can stand in front of it.
        private const float PinInfluence = 44f;

        // The part of the sheet a close-up of region scatters its glyphs over beyond its own
        // reach: every pin near it, with PinInfluence round it, so the trees in front of a pin are
        // the same whichever tile draws it (LimitMarkCover).
        public static Rect ScatterArea(InkRecord record, RectInt region, float reach)
        {
            var area = new Rect(region.x, region.y, region.width, region.height);
            foreach (SheetPin pin in record.m_pins)
            {
                float x = (pin.m_x + HalfExtent) / MetresPerPixel, y = (HalfExtent - pin.m_z) / MetresPerPixel;
                var near = new Rect(x - PinInfluence, y - PinInfluence, 2f * PinInfluence, 2f * PinInfluence);
                if (near.xMax < region.xMin - reach || near.xMin > region.xMax + reach || near.yMax < region.yMin - reach || near.yMin > region.yMax + reach) continue;
                area = Rect.MinMaxRect(Math.Min(area.xMin, near.xMin), Math.Min(area.yMin, near.yMin), Math.Max(area.xMax, near.xMax), Math.Max(area.yMax, near.yMax));
            }
            return area;
        }

        private static float Overlap(Vector4 a, Vector4 b) =>
            Math.Max(0f, Math.Min(a.z, b.z) - Math.Max(a.x, b.x)) * Math.Max(0f, Math.Min(a.w, b.w) - Math.Max(a.y, b.y));

        // The land's own shape: peaks, hills, spires and sea thorns are never left out for a pin, as
        // a range must still read as one; a pin among them is drawn in front instead (OrderMarks).
        private static bool IsRelief(Kind kind) =>
            kind == Kind.Peak || kind == Kind.Ridge || kind == Kind.Hill || kind == Kind.Spire || kind == Kind.Thorn;

        // The order glyphs are drawn in, north to south: a glyph's foot, a mark's m_order.
        private static float Order(Glyph g) => IsMark(g.m_kind) || g.m_onSlope ? g.m_order : g.m_y;

        // Relief in front of a pin may cover it up to MarkCover of its box too, but is never left
        // out: the relief drawn after the pin that overlaps it is taken north first, each adding
        // its overlap (m_cover), and where the next would pass MarkCover the pin is drawn after it
        // instead (m_order), inked over the peak with its body scraping only what lies under it.
        // Trees get what MarkCover leaves (LimitMarkCover).
        private void OrderMarks()
        {
            var front = new List<Glyph>();
            for (int i = 0; i < m_marks.Count; i++)
            {
                Glyph mark = m_marks[i];
                Vector4 pin = GlyphBox(mark);
                front.Clear();
                foreach (Glyph g in m_glyphs)
                    if (IsRelief(g.m_kind) && g.m_y > mark.m_y && Overlap(GlyphBox(g), pin) > 0f) front.Add(g);
                front.Sort((a, b) => a.m_y != b.m_y ? a.m_y.CompareTo(b.m_y) : a.m_x.CompareTo(b.m_x));
                var covered = new bool[CoverSamples * CoverSamples];
                int count = 0;
                int limit = CoverLimit(mark);
                foreach (Glyph g in front)
                    if (g.m_y > mark.m_order && !AddCover(g, mark, pin, covered, ref count, limit)) mark.m_order = g.m_y + 0.01f;
                // Relief the pin now stands in front of no longer covers it.
                covered = new bool[CoverSamples * CoverSamples];
                count = 0;
                foreach (Glyph g in front)
                    if (g.m_y > mark.m_order) AddCover(g, mark, pin, covered, ref count, int.MaxValue);
                mark.m_cover = covered;
                m_marks[i] = mark;
            }
        }

        // Whether a glyph may stand where it is: one drawn after a mark (its foot further south)
        // must not reach the mark's core. Relief always may (OrderMarks); trees and dead trees are
        // limited by how much of the pin they cover together instead (LimitMarkCover).
        private bool ClearOfMarks(Glyph g)
        {
            if (m_markCells.Count == 0 || IsMark(g.m_kind) || IsRelief(g.m_kind) || GivesWayAsTree(g.m_kind)) return true;
            Vector4 box = GlyphBox(g);
            int x0 = (int)Math.Floor(box.x / PeakCell), x1 = (int)Math.Floor(box.z / PeakCell);
            int y0 = (int)Math.Floor(box.y / PeakCell), y1 = (int)Math.Floor(box.w / PeakCell);
            for (int cy = y0; cy <= y1; cy++)
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!m_markCells.TryGetValue(PeakCellKey(cx, cy), out List<MarkZone> cell)) continue;
                    foreach (MarkZone zone in cell)
                    {
                        Vector4 c = zone.m_core;
                        if (g.m_y > zone.m_foot && box.x < c.z && c.x < box.z && box.y < c.w && c.y < box.w) return false;
                    }
                }
            return true;
        }

        // A pin's box (left, top, size): its glyph stands on its place (PinGlyphs.Lift), which lies
        // Sink above the glyph's foot.
        private static Vector3 PinBox(Glyph g)
        {
            float size = g.m_size;
            var glyph = (PinGlyph)(g.m_seed & 0xFF);
            float centre = g.m_y - (PinGlyphs.Lift(glyph) + PinGlyphs.Sink(glyph)) * size;
            return new Vector3(g.m_x - size * 0.5f, centre - size * 0.5f, size);
        }

        private struct Glyph
        {
            public float m_x, m_y, m_size;
            public Kind m_kind;
            public int m_seed;
            // A hill drawn without the smaller dome behind it, dropped to keep it off water.
            public bool m_lone;
            // A Mistlands spire standing in shallow water, drawn on a level waterline.
            public bool m_wet;
            // A mark's place in the drawing order (Order), at or south of its foot, and the share of
            // its box the relief in front of it covers (OrderMarks).
            public float m_order;
            public bool[] m_cover;
            // A tree standing on a peak's or ridge's lower slope, drawn at m_order (SlopeOrder).
            public bool m_onSlope;
        }

        // Everything a scatter rule needs to know about one canvas point.
        private struct Spot
        {
            public float m_x, m_y, m_worldX, m_worldZ, m_above, m_slope, m_roll;
            public int m_pixel, m_seed;
            public Land m_land;
            // What the biome patch's alt biome does to its trees.
            public Grove m_grove;
            public bool m_isLand, m_wood, m_mist, m_lava;
            // The lava field's value here, 0.5 at a lava field's edge.
            public float m_lavaLevel;
            // The scatter cell and the grid's hash seed, so a rule can work out its neighbours'
            // candidates from hashes alone.
            public int m_gx, m_gy, m_gridSeed;
            // Distances to water, to land and to the nearest wood, in canvas pixels.
            public float m_shore, m_water, m_woodDistance;
        }

        // A painter for the whole sheet. Its canvas only records: the pixels are drawn from the
        // record afterwards (Paint's Rasterize, Repaint's changed cells).
        private InkPainter(WorldSource world, CancellationToken cancel)
        {
            m_world = world;
            m_cancel = cancel;
            m_canvas = InkCanvas.RecordOnly(CanvasSize);
            m_right = m_bottom = CanvasSize;
        }

        // A painter for one detail tile: draws into canvas from grid, at the given level of detail.
        private InkPainter(WorldSource world, InkCanvas canvas, TileGrid grid, float lod, RectInt region, CancellationToken cancel)
        {
            m_world = world;
            m_cancel = cancel;
            m_canvas = canvas;
            m_grid = grid;
            m_lod = lod;
            m_g = G / lod;
            m_t = T / lod;
            m_pen = 1f / lod;
            m_canvas.m_penScale = m_pen;
            m_left = region.xMin;
            m_top = region.yMin;
            m_right = region.xMax;
            m_bottom = region.yMax;
        }

        // Throws OperationCanceledException between stages once cancel is set, so a drawing for a
        // world the player already left stops instead of holding its memory to the end. Every pen
        // call is kept in record (and indexed) for sharp detail tiles; a record is made when none
        // is given. pins are drawn in as glyphs of their own, in clearings (FindMarks).
        // The painter only records (every decision is made from the record, never from pixels,
        // "The record is the drawing" in CLAUDE.md), then the whole record is replayed into the
        // sheet in tiles on the workers (Rasterize): byte for byte the sheet a canvas painted as
        // it went would hold, with the pixels spread over the workers instead of one thread.
        public static byte[] Paint(WorldSource world, CancellationToken cancel, InkRecord record, IList<SheetPin> pins)
        {
            if (record == null) record = new InkRecord();
            var painter = new InkPainter(world, cancel);
            painter.FindMarks(pins);
            painter.m_canvas.Record(record);
            if (pins != null) record.m_pins = new List<SheetPin>(pins).ToArray();
            record.m_marks.m_key = SheetPin.Key(record.m_pins);
            painter.m_record = record;
            painter.Run();
            record.Seal(CanvasSize);
            cancel.ThrowIfCancellationRequested();
            byte[] sheet = Rasterize(record, cancel);
            return sheet;
        }

        // The side of the tiles a whole record is replayed in: 16 x 16 of them on the sheet, enough
        // to keep every worker busy while calls crossing a tile's edge are drawn only a few times.
        private const int ReplayTile = 256;

        // The sheet a sealed record draws: ink coverage, then the body masks in BodyKind order
        // (mountain, Deep North, Mistlands, wood), then the pins' red, one byte a pixel each.
        // Each tile is a window of the sheet replayed exactly (InkRecord.ReplayClipped), so the
        // tiles join without a seam.
        private static byte[] Rasterize(InkRecord record, CancellationToken cancel)
        {
            int n = CanvasSize;
            var sheet = new byte[n * n * SheetPlanes];
            // Works out the base fades ReplayClipped restores once, before the workers share the
            // record; the hashes are kept with it for the next repaint's comparison.
            record.CellHashes();
            int across = (n + ReplayTile - 1) / ReplayTile;
            Parallel.For(0, across * across, Workers.LoopOptions(cancel), k =>
            {
                int x = k % across * ReplayTile, y = k / across * ReplayTile;
                var r = new RectInt(x, y, Math.Min(ReplayTile, n - x), Math.Min(ReplayTile, n - y));
                InkCanvas canvas = InkCanvas.Window(n, r.xMin, r.yMin, r.width, r.height);
                record.ReplayClipped(canvas, r.xMin, r.yMin, r.xMax, r.yMax);
                CopyWindow(canvas, r, sheet);
            });
            cancel.ThrowIfCancellationRequested();
            return sheet;
        }

        // Copies a window canvas's planes into their place in a whole sheet (SheetPlanes planes).
        private static void CopyWindow(InkCanvas canvas, RectInt r, byte[] sheet)
        {
            int n = CanvasSize;
            byte[][] planes = { canvas.m_ink, canvas.m_body, canvas.m_bodyNorth, canvas.m_bodyMist, canvas.m_bodyWood, canvas.m_pinInk };
            for (int p = 0; p < planes.Length; p++)
                for (int y = 0; y < r.height; y++)
                    Buffer.BlockCopy(planes[p], y * r.width, sheet, p * n * n + (r.yMin + y) * n + r.xMin, r.width);
        }

        private void Run()
        {
            // The coast and the fields do not depend on the pins: a repaint for new pins takes
            // them from the last paint of the same world (Groundwork).
            if (!TakeGroundwork())
            {
                BuildCoastHeights();
                m_cancel.ThrowIfCancellationRequested();
                BuildFields();
                m_cancel.ThrowIfCancellationRequested();
                KeepGroundwork();
            }
            // Nothing up to the end of StartPinFree reads the pins either: a repaint starts from
            // where the last paint of the world left it (Startwork).
            if (!TakeStartwork())
            {
                StartPinFree();
                KeepStartwork();
            }
            RunFromPins();
        }

        // The paint from the pins on: everything Run does once the pin-free start is drawn.
        private void RunFromPins()
        {
            m_cancel.ThrowIfCancellationRequested();
            PlaceGlyphs();
            m_cancel.ThrowIfCancellationRequested();
            foreach (Glyph glyph in m_glyphs)
            {
                if (glyph.m_kind == Kind.Mist) continue;
                AddFootprint(glyph);
                // Close-ups draw their own trees, so only the other glyphs' footprints are kept.
                if (!IsTree(glyph.m_kind)) m_record?.AddGlyphBox(GlyphBox(glyph));
            }
            m_canvas.m_layer = InkLayer.Background;
            DrawScarps();
            DrawStumps();
            DrawBoulders();
            DrawMeadowGrass();
            DrawWoodlessGround();
            m_glyphs.Sort((a, b) => Order(a).CompareTo(Order(b)));
            foreach (Glyph glyph in m_glyphs)
            {
                bool tree = IsTree(glyph.m_kind);
                m_canvas.m_layer = tree ? InkLayer.Tree : InkLayer.Glyph;
                if (!tree) m_canvas.BeginGroup(Order(glyph));
                // The fog's edge shows or leaves out whole every tree, relief and dead tree
                // (Paper.Fog), so it needs their footprints, and close-ups the relief's group.
                if (m_record != null && glyph.m_kind != Kind.Pin && glyph.m_kind != Kind.Mist)
                {
                    var mark = new FogMark
                    {
                        m_box = GlyphBox(glyph),
                        m_order = Order(glyph),
                        m_shape = FogShape(glyph.m_kind),
                        m_thinned = glyph.m_kind == Kind.DeadTree,
                        m_group = tree ? -1 : m_record.CurrentGroup,
                    };
                    if (tree) m_record.m_marks.m_trees.Add(mark);
                    else m_record.m_marks.m_glyphs.Add(mark);
                }
                DrawGlyph(glyph);
                if (!tree) m_canvas.EndGroup();
            }
            DrawGrassOverMarks();
            // Taken only here, as Lined reads the wide lakes once they are marked.
            List<ContourLines.Line> waterLines = TakeWaterwork();
            if (waterLines == null)
            {
                MarkWideLakes();
                waterLines = TraceWaterLines();
                KeepWaterwork(waterLines);
            }
            // The waterline itself is not drawn; the waves find the coast's bends on its trace.
            m_canvas.m_layer = InkLayer.Ripple;
            DrawWaves(waterLines);
            m_canvas.m_layer = InkLayer.Floe;
            foreach (Glyph floe in m_floes) DrawGlyph(floe);
            // Last before the frame, so they can keep clear of every mark already inked. The
            // figures and then their stand-ins (boils, fish), all on the Illustration layer.
            DrawIllustrations();
            m_cancel.ThrowIfCancellationRequested();
            m_canvas.m_layer = InkLayer.Frame;
            DrawFrame();
        }

        private void StartPinFree()
        {
            FindMist();
            m_canvas.m_layer = InkLayer.Background;
            DrawBackground();
            m_canvas.m_layer = InkLayer.Lava;
            DrawLava();
            // Rivers and the coast go under the glyphs, so a crown standing over a bank or a
            // shore hides the line behind it instead of being cut by it. Trees keep off the
            // rivers' courses (m_riverMask), so streams still run between them.
            m_canvas.m_layer = InkLayer.River;
            DrawRivers();
            m_canvas.m_layer = InkLayer.Coast;
            DrawCoast();
            m_cancel.ThrowIfCancellationRequested();
            PlacePinFree();
        }

        // The rough shape of a glyph's ink in its footprint (GlyphBox), over which the sheet's ink
        // is cleared when the fog's edge leaves it out: a crag's second peak and a dead tree's
        // limbs reach past a triangle or an ellipse.
        private static MarkShape FogShape(Kind kind)
        {
            switch (kind)
            {
                case Kind.Broadleaf:
                case Kind.PlainsTree:
                case Kind.GiantTree:
                case Kind.SmallTree:
                case Kind.Oak:
                    return MarkShape.Crown;
                case Kind.Conifer:
                case Kind.Peak:
                case Kind.Spire:
                case Kind.Thorn:
                    return MarkShape.Spire;
                case Kind.Hill:
                case Kind.Ridge:
                    return MarkShape.Dome;
                default:
                    return MarkShape.Box;
            }
        }

        // The glyphs a detail tile draws afresh at its own level of detail.
        private static bool IsTree(Kind kind) =>
            kind == Kind.Broadleaf || kind == Kind.Conifer || kind == Kind.PlainsTree || kind == Kind.GiantTree
            || kind == Kind.SmallTree || kind == Kind.Oak;

        // The glyphs a pin limits by how much of it they cover (LimitMarkCover,
        // DropHiddenBehindMarks), never by refusing them a place: the trees and the swamp's dead
        // trees, which close-ups replay from the sheet. Refused while placing, a dead tree let the
        // next one in its gap stand and so on along the row, so a pin moved dead trees far from
        // it and no placement could be kept for other pins (Startwork).
        private static bool GivesWayAsTree(Kind kind) => IsTree(kind) || kind == Kind.DeadTree;

        // The coastline is traced on vanilla's own heights, unsmoothed, so it runs where vanilla's
        // map puts the water's edge: every island, lake, pond and swamp pool stays. Only single map
        // pixels of land or water (12 m) are dropped, which are noise at any zoom. Water connected to
        // the world's edge is the sea; the rest (lakes, ponds, swamp pools) gets a lighter shore.
        private void BuildCoastHeights()
        {
            int size = m_world.m_size;
            float water = m_world.m_waterLevel;
            m_coast = (float[])m_world.m_height.Clone();
            RemoveSpecks(water, land: true, SpeckCells);
            RemoveSpecks(water, land: false, SpeckCells);
            MarkSea(water, size);
        }

        // Flattens connected patches of land (or of water) smaller than minCells map pixels into
        // their surroundings. Water touching the edge of the map is the sea and always stays.
        private void RemoveSpecks(float water, bool land, int minCells)
        {
            int size = m_world.m_size;
            var seen = new bool[size * size];
            var queue = new int[size * size];
            for (int start = 0; start < seen.Length; start++)
            {
                if (seen[start] || (m_coast[start] > water) != land) continue;
                int head = 0, tail = 0;
                bool edge = false;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int i = queue[head++];
                    int x = i % size, z = i / size;
                    if (x == 0 || z == 0 || x == size - 1 || z == size - 1) edge = true;
                    if (x > 0) Visit(i - 1);
                    if (x < size - 1) Visit(i + 1);
                    if (z > 0) Visit(i - size);
                    if (z < size - 1) Visit(i + size);
                }
                if (tail >= minCells || (!land && edge)) continue;
                float level = land ? water - 0.5f : water + 0.5f;
                for (int k = 0; k < tail; k++) m_coast[queue[k]] = level;

                void Visit(int j)
                {
                    if (seen[j] || (m_coast[j] > water) != land) return;
                    seen[j] = true;
                    queue[tail++] = j;
                }
            }
        }

        // Flood fills the water connected to the edge of the map (four-way, so a river mouth joined
        // only at a corner stays a lake) into m_seaCell.
        private void MarkSea(float water, int size)
        {
            m_seaCell = new bool[size * size];
            var queue = new int[size * size];
            int head = 0, tail = 0;
            for (int i = 0; i < size; i++)
            {
                Seed(i);
                Seed((size - 1) * size + i);
                Seed(i * size);
                Seed(i * size + size - 1);
            }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % size, z = i / size;
                if (x > 0) Seed(i - 1);
                if (x < size - 1) Seed(i + 1);
                if (z > 0) Seed(i - size);
                if (z < size - 1) Seed(i + size);
            }

            void Seed(int j)
            {
                if (m_seaCell[j] || m_coast[j] > water) return;
                m_seaCell[j] = true;
                queue[tail++] = j;
            }
        }

        private void BuildFields()
        {
            int n = CanvasSize;
            m_land = new byte[n * n];
            m_sea = new byte[n * n];
            m_wood = new byte[n * n];
            float edgeSqr = EdgeRadius * EdgeRadius;
            int size = m_world.m_size;
            // Row by row on the workers: every pixel is decided on its own, so the masks are the
            // serial ones byte for byte.
            System.Threading.Tasks.Parallel.For(0, n, Workers.LoopOptions(m_cancel), y =>
            {
                float wz = HalfExtent - (y + 0.5f) * MetresPerPixel;
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * MetresPerPixel - HalfExtent;
                    int pixel = y * n + x;
                    if (wx * wx + wz * wz >= edgeSqr)
                    {
                        m_sea[pixel] = 1;
                        continue;
                    }
                    if (m_world.Sample(m_coast, wx, wz) > m_world.m_waterLevel)
                    {
                        m_land[pixel] = 1;
                        // A felled Black Forest or a boulder field (WorldSource.IsOpen) keeps the
                        // wood's shape and wash but is open land: no wood, and the ground fills
                        // may enter it.
                        if (m_world.Sample(m_world.m_wood, wx, wz) > WoodEdge && !m_world.IsOpenGrove(wx, wz)) m_wood[pixel] = 1;
                        continue;
                    }
                    // Sea if any of the four map pixels around it is sea water.
                    int sx = Mathf.Clamp((int)m_world.ToSourceX(wx), 0, size - 2);
                    int sz = Mathf.Clamp((int)m_world.ToSourceZ(wz), 0, size - 2);
                    int i = sz * size + sx;
                    if (m_seaCell[i] || m_seaCell[i + 1] || m_seaCell[i + size] || m_seaCell[i + size + 1]) m_sea[pixel] = 1;
                }
            });

            var field = new DistanceField(n);
            m_waterDistance = new byte[n * n];
            m_landDistance = new byte[n * n];
            field.ComputeParallel(i => m_land[i] != 0, m_waterDistance, DistanceScale, m_cancel);
            m_cancel.ThrowIfCancellationRequested();
            field.ComputeParallel(i => m_land[i] == 0, m_landDistance, 1f, m_cancel);
            m_cancel.ThrowIfCancellationRequested();
            m_woodDistance = new byte[n * n];
            field.ComputeParallel(i => m_wood[i] != 0, m_woodDistance, 1f, m_cancel);
            m_cancel.ThrowIfCancellationRequested();
            bool[] major = MajorLand(m_world.m_waterLevel, size);
            m_majorDistance = new byte[n * n];
            field.ComputeParallel(i =>
            {
                if (m_land[i] == 0) return false;
                float wx = (i % n + 0.5f) * MetresPerPixel - HalfExtent, wz = HalfExtent - (i / n + 0.5f) * MetresPerPixel;
                return major[m_world.Index(wx, wz)];
            }, m_majorDistance, DistanceScale, m_cancel);
        }

        // The map pixels of land masses of at least MajorLandCells (four-way connected), which get
        // the full wave groups. The smaller ones go to m_islets to be clustered for the islets'
        // strokes, all but those mostly in the Mistlands, whose islet fields are dense and stay
        // bare; they still hide any stroke that crosses them.
        private bool[] MajorLand(float water, int size)
        {
            var major = new bool[size * size];
            m_isletCell = new int[size * size];
            var seen = new bool[size * size];
            var queue = new int[size * size];
            for (int start = 0; start < seen.Length; start++)
            {
                if (seen[start] || !(m_coast[start] > water)) continue;
                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int i = queue[head++];
                    int x = i % size, z = i / size;
                    if (x > 0) Visit(i - 1);
                    if (x < size - 1) Visit(i + 1);
                    if (z > 0) Visit(i - size);
                    if (z < size - 1) Visit(i + size);
                }
                if (tail < MajorLandCells)
                {
                    int mist = 0, left = size, right = 0, south = size, north = 0;
                    float sumX = 0f, sumZ = 0f;
                    for (int k = 0; k < tail; k++)
                    {
                        int j = queue[k], x = j % size, z = j / size;
                        if (m_world.m_biome[j] == Land.Mistlands) mist++;
                        left = Math.Min(left, x);
                        right = Math.Max(right, x);
                        south = Math.Min(south, z);
                        north = Math.Max(north, z);
                        sumX += x;
                        sumZ += z;
                    }
                    if (mist * 2 >= tail) continue;
                    for (int k = 0; k < tail; k++) m_isletCell[queue[k]] = m_islets.Count + 1;
                    // Cell edges in world metres, then on the sheet (y runs south).
                    float ps = m_world.m_pixelSize, half = size / 2;
                    float wx = (sumX / tail + 0.5f - half) * ps, wz = (sumZ / tail + 0.5f - half) * ps;
                    m_islets.Add(new Islet
                    {
                        m_at = new Vector2((wx + HalfExtent) / MetresPerPixel, (HalfExtent - wz) / MetresPerPixel),
                        m_left = ((left - half) * ps + HalfExtent) / MetresPerPixel,
                        m_right = ((right + 1 - half) * ps + HalfExtent) / MetresPerPixel,
                        m_top = (HalfExtent - (north + 1 - half) * ps) / MetresPerPixel,
                        m_bottom = (HalfExtent - (south - half) * ps) / MetresPerPixel,
                        m_cells = tail,
                    });
                    continue;
                }
                for (int k = 0; k < tail; k++) major[queue[k]] = true;

                void Visit(int j)
                {
                    if (seen[j] || !(m_coast[j] > water)) return;
                    seen[j] = true;
                    queue[tail++] = j;
                }
            }
            return major;
        }

        private Spot Look(float x, float y)
        {
            if (m_grid != null) return LookTile(x, y);
            int px = Mathf.Clamp((int)x, 0, CanvasSize - 1);
            int py = Mathf.Clamp((int)y, 0, CanvasSize - 1);
            var spot = new Spot
            {
                m_x = x,
                m_y = y,
                m_pixel = py * CanvasSize + px,
                m_worldX = x * MetresPerPixel - HalfExtent,
                m_worldZ = HalfExtent - y * MetresPerPixel,
            };
            spot.m_isLand = m_land[spot.m_pixel] != 0;
            spot.m_shore = m_landDistance[spot.m_pixel];
            spot.m_water = m_waterDistance[spot.m_pixel] / DistanceScale;
            int source = m_world.Index(spot.m_worldX, spot.m_worldZ);
            spot.m_land = m_world.m_biome[source];
            spot.m_grove = m_world.GroveAt(spot.m_worldX, spot.m_worldZ, spot.m_land);
            spot.m_above = m_world.HeightAt(spot.m_worldX, spot.m_worldZ) - m_world.m_waterLevel;
            spot.m_slope = m_world.SlopeAt(spot.m_worldX, spot.m_worldZ);
            spot.m_wood = m_wood[spot.m_pixel] != 0;
            spot.m_woodDistance = m_woodDistance[spot.m_pixel];
            spot.m_mist = m_world.m_mist[source] > MistArea;
            spot.m_lavaLevel = m_world.Sample(m_world.m_lavaField, spot.m_worldX, spot.m_worldZ);
            spot.m_lava = spot.m_lavaLevel > 0.5f;
            return spot;
        }

        // The slope of the sheet's map pixel under a spot, which the sheet and every close-up read
        // alike (a close-up's own slope is measured over a shorter baseline and finds more steep
        // ground).
        private bool SteepGround(Spot s) => m_world.MostlySteep(s.m_worldX, s.m_worldZ, SteepWindow, TreeTilt);

        private float SheetSlope(Spot s) => m_grid != null ? m_world.SlopeAt(s.m_worldX, s.m_worldZ) : s.m_slope;

        // The same from a detail tile's grid. Distances are given in the tile's own units (sheet
        // pixels times the level of detail), so the rules' margins shrink with their glyphs.
        private Spot LookTile(float x, float y)
        {
            var spot = new Spot
            {
                m_x = x,
                m_y = y,
                m_pixel = PixelAt(x, y),
                m_worldX = x * MetresPerPixel - HalfExtent,
                m_worldZ = HalfExtent - y * MetresPerPixel,
            };
            spot.m_above = m_grid.Sample(m_grid.m_height, x, y) - m_grid.m_waterLevel;
            spot.m_isLand = spot.m_above > 0f;
            spot.m_land = m_grid.m_land[m_grid.Nearest(x, y)];
            // From the sheet's own map pixels, so a close-up thins, thickens or fells the same
            // woods as the sheet.
            spot.m_grove = m_world.GroveAt(spot.m_worldX, spot.m_worldZ, spot.m_land);
            spot.m_slope = m_grid.Slope(x, y);
            spot.m_wood = m_grid.IsWood(x, y);
            // The mist area from the sheet's map pixel, so close-ups thin the giant trees in the
            // mist as the sheet does.
            spot.m_mist = m_world.m_mist[m_world.Index(spot.m_worldX, spot.m_worldZ)] > MistArea;
            spot.m_shore = m_grid.Distance(m_grid.m_shore, x, y) * m_lod;
            spot.m_woodDistance = m_grid.Distance(m_grid.m_woodDistance, x, y) * m_lod;
            spot.m_lavaLevel = m_world.Sample(m_world.m_lavaField, spot.m_worldX, spot.m_worldZ);
            spot.m_lava = spot.m_lavaLevel > 0.5f;
            return spot;
        }

        // Jittered grid: one candidate per cell. Each cell's jitter, roll and glyph seed hash from the
        // world seed, the salt and the cell alone, so tuning one rule never moves another's glyphs.
        private void Scatter(float spacing, int salt, Action<Spot> visit) =>
            Scatter(spacing, spacing, 0.9f, 0f, salt, visit);

        // Rows: separate column and row spacing, a jitter as a fraction of the cell, and every other
        // row shifted by stagger cells, so glyphs pack like bricks. A sheet grid hashes as level 1
        // at every level of detail, so its marks stand at the same places at every zoom.
        private void Scatter(float spacingX, float spacingY, float jitter, float stagger, int salt, Action<Spot> visit, bool sheetGrid = false)
        {
            // The grid is anchored to the sheet, so a tile's glyphs are the same whichever part of
            // the sheet it covers; each level of detail has grids of its own.
            int columns = (int)(CanvasSize / spacingX);
            int rows = (int)(CanvasSize / spacingY);
            // A tile also visits cells around it, far enough for any glyph standing outside it to
            // reach in, so glyphs carry on unbroken across tile edges.
            float reach = m_grid != null ? GlyphReach / m_lod : 0f;
            float left = Math.Min(m_left, m_scatter.xMin), right = Math.Max(m_right, m_scatter.xMax);
            float top = Math.Min(m_top, m_scatter.yMin), bottom = Math.Max(m_bottom, m_scatter.yMax);
            int gx0 = Math.Max(0, (int)((left - reach) / spacingX) - 1), gx1 = Math.Min(columns, (int)((right + reach) / spacingX) + 2);
            int gy0 = Math.Max(0, (int)((top - reach) / spacingY) - 1), gy1 = Math.Min(rows, (int)((bottom + reach) / spacingY) + 2);
            float edge = EdgeRadius / MetresPerPixel;
            float centre = CanvasSize * 0.5f;
            // Level 1 hashes as the sheet does, so a tile at that level places the sheet's glyphs.
            int level = sheetGrid ? 0 : Mathf.RoundToInt(m_lod * 4f) - 4;
            int worldSeed = unchecked(m_world.m_seed + salt * 7919 + level * 104729);
            for (int gy = gy0; gy < gy1; gy++)
            {
                for (int gx = gx0; gx < gx1; gx++)
                {
                    int seed = HashInt(gx, gy, worldSeed);
                    float x = (gx + 0.5f + (gy % 2) * stagger + (Unit(seed, 1) - 0.5f) * jitter) * spacingX;
                    float y = (gy + 0.5f + (Unit(seed, 2) - 0.5f) * jitter) * spacingY;
                    float dx = x - centre, dy = y - centre;
                    // Inside the frame's inner ring, with room for the glyph above its foot.
                    float limit = edge * EdgeFraction;
                    if (dx * dx + dy * dy > limit * limit) continue;
                    Spot spot = Look(x, y);
                    spot.m_roll = Unit(seed, 3);
                    spot.m_seed = seed;
                    spot.m_gx = gx;
                    spot.m_gy = gy;
                    spot.m_gridSeed = worldSeed;
                    visit(spot);
                }
            }
        }

        // Whether canvas point (x, y) is land: tall glyphs test their top too, since the shore
        // margin at their foot does not stop the summit of a peak or a tree from reaching the sea.
        private bool IsLandAt(float x, float y)
        {
            if (m_grid != null) return m_grid.IsLand(x, y);
            int px = Mathf.Clamp((int)x, 0, CanvasSize - 1);
            int py = Mathf.Clamp((int)y, 0, CanvasSize - 1);
            return m_land[py * CanvasSize + px] != 0;
        }

        // Whether the whole column a tall glyph stands in, from its foot up to height, is land:
        // testing only the top lets a pine on a thin spit of land reach across the water between.
        private bool IsLandColumn(float x, float y, float height)
        {
            for (int k = 1; k <= 4; k++)
                if (!IsLandAt(x, y - height * k / 4f)) return false;
            return true;
        }

        // Whether a tree of the given height, standing at the spot, belongs to a wood of the given
        // biome: its foot and the middle of its crown both inside, so the outer crowns form the
        // wood's edge, standing over it by at most a crown's half width.
        private bool InWood(Spot s, Land land, float height)
        {
            if (!s.m_wood || s.m_land != land || s.m_shore < 3f) return false;
            if (OnRiver(s.m_x, s.m_y) || OnRiver(s.m_x, s.m_y - height * 0.6f)) return false;
            return IsWoodAt(s.m_x, s.m_y - height * 0.6f) && IsLandColumn(s.m_x, s.m_y, height);
        }

        private bool IsWoodAt(float x, float y) => m_grid != null ? m_grid.IsWood(x, y) : m_wood[PixelAt(x, y)] != 0;

        // Whether a peak of the given width and height standing at (x, y) lies over land: its
        // centre column, both ends of its base and both flanks, up to reach of its height. Testing
        // the column alone lets the wide base of a coastal peak hang out over the sea. A summit's
        // peak needs land only up to the summit (SummitAnchor): above it the peak rises over
        // whatever lies behind, water included, as the glyphs in front hide those behind.
        private bool PeakOnLand(float x, float y, float width, float height, float reach = 1f)
        {
            if (!IsLandColumn(x, y, height * reach)) return false;
            float half = width * 0.55f, flank = Mathf.Min(0.5f, reach);
            return IsLandAt(x - half, y) && IsLandAt(x + half, y)
                && IsLandAt(x - half * (1f - flank), y - height * flank) && IsLandAt(x + half * (1f - flank), y - height * flank);
        }

        private bool LavaAt(float x, float y) => LavaLevel(x, y) > 0.5f;

        private float LavaLevel(float x, float y) =>
            m_world.Sample(m_world.m_lavaField, x * MetresPerPixel - HalfExtent, HalfExtent - y * MetresPerPixel);

        private static Vector2 Rotate(Vector2 v, float degrees)
        {
            float a = degrees * Mathf.Deg2Rad, cos = Mathf.Cos(a), sin = Mathf.Sin(a);
            return new Vector2(v.x * cos - v.y * sin, v.x * sin + v.y * cos);
        }

        private void Add(Spot spot, Kind kind, float size)
        {
            var glyph = new Glyph { m_x = spot.m_x, m_y = spot.m_y, m_size = size, m_kind = kind, m_seed = spot.m_seed };
            if (ClearOfMarks(glyph)) m_glyphs.Add(glyph);
        }

        private static float Unit(int seed, int k) => Atlas.Noise.Unit(seed, k);

        private static int HashInt(int x, int y, int seed) => Atlas.Noise.HashInt(x, y, seed);

        // Every biome but the mountains and the near-blank Deep North has a light ground fill of its
        // own on its open land, never inside a wood, so a wood and the open country around it are
        // told apart at a glance; the biome wash under the ink tells the biomes apart, so the fills
        // stay sparse. Every pen line is at least Pen wide and full strength: thinner or fainter
        // lines average away to grey at the opening zoom.
        private void DrawBackground()
        {
            // The Meadows' grass tufts are laid once the glyphs are placed (DrawMeadowGrass), as
            // they keep out from behind the lone oaks. The Black Forest has no ground fill of its
            // own: vanilla's mask marks all its land as forest, so it has no open ground but its
            // open groves. A felled Black Forest
            // (Kalhygge) gets stumps (DrawStumps) and a boulder field (Rock) boulders with the
            // Plains' stipple between (DrawBoulders, DrawRockStipple), once every glyph is placed.
            if (m_world.HasGrove(Grove.ClearCut))
            {
                // Between the stumps a light stipple of the cleared ground, sparser than the
                // Plains' heath, so the felled wood does not read as a blank.
                Scatter(5f * m_g, 36, s =>
                {
                    if (s.m_grove != Grove.ClearCut || !IsOpenLand(s, Land.BlackForest, 2f) || !(s.m_roll < ClearCutStipple)) return;
                    m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
                });
            }
            // Swamp: the classic map's marsh sign, short flat water lines with tufts of reeds on
            // many, gathered into patches by a slow noise: crowded fen in places, thin open bog
            // between. Even rows over the whole swamp read as messy and ruled, so the grid is
            // square and fully jittered. The bog between patches keeps a fair share of marks, as
            // a near-empty one reads as too sparse. Marks are sized to their cell and one that
            // would touch a neighbour of higher priority is left out, decided from hashes alone
            // (MarshMark), so the sheet and every tile agree; marks bigger than their cell with a
            // jitter of nearly a cell pile up into knots.
            float marsh = MarshStep * 1.5f * m_g;
            // Close-ups draw the marks a little finer: at the tile's pen the small tufts close up
            // into lumps. The sheet keeps the full Pen.
            float marshPen = m_grid != null ? 0.75f * Pen : Pen;
            Scatter(marsh, 11, s =>
            {
                if (!IsOpenLand(s, Land.Swamp, 3f)) return;
                MarshMark(s.m_gx, s.m_gy, s.m_gridSeed, marsh, out MarshBox mark);
                if (!mark.m_inked) return;
                for (int dy = -1; dy <= 1; dy++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        MarshMark(s.m_gx + dx, s.m_gy + dy, s.m_gridSeed, marsh, out MarshBox other);
                        if (!other.m_inked || other.m_priority < mark.m_priority) continue;
                        if (other.m_left < mark.m_right && mark.m_left < other.m_right && other.m_top < mark.m_bottom && mark.m_top < other.m_bottom) return;
                    }
                }
                if (mark.m_reeds) Reeds(mark.m_x + (Unit(s.m_seed, 6) - 0.5f) * mark.m_half * 0.6f, mark.m_y, marsh, s.m_seed, marshPen);
                m_canvas.Stroke(mark.m_x - mark.m_half, mark.m_y, mark.m_x + mark.m_half, mark.m_y, marshPen);
                if (mark.m_second)
                {
                    float x = mark.m_x + (Unit(s.m_seed, 8) - 0.3f) * mark.m_half, y = mark.m_y + 0.2f * marsh;
                    m_canvas.Stroke(x - mark.m_half * 0.55f, y, x + mark.m_half * 0.55f, y, marshPen);
                }
            });
            // Plains: a stipple of fine dots, the dry heath's own tone.
            Scatter(5f * m_g, 16, s =>
            {
                if (!IsOpenLand(s, Land.Plains, 2f) || !(s.m_roll < 0.45f)) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
            });
            // Ashlands: a sparse stipple of cinders of uneven size on the burnt ground, none on the
            // lava fields, which carry their own flow marks. A dense diagonal hatch over all of it
            // reads as a flat grey blanket.
            // Denser by the lava's edge, thinning out on the open plain, as the classic map
            // stipples its blasted plains. One even dot: cinder ticks and fissure pairs make the
            // ground look doodled.
            Scatter(4.5f * m_g, 12, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_shore < 2f || s.m_lava) return;
                float near = Mathf.Clamp01(s.m_lavaLevel / 0.5f);
                if (!(s.m_roll < Mathf.Lerp(0.15f, 0.65f, near))) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
            });
            // Deep North: a near-blank snowfield, as the classic map leaves its northern waste. Its
            // only marks are snow marks, chevrons gathered into thin winding bands (the crest
            // lines of a ridged noise) with open snow between, rather than sprinkled evenly.
            // Each chevron keeps clear of the water, of the peaks, of the woods and of the other
            // chevrons: ones touching a shore, a peak's foot or each other read as clutter.
            float snowStep = 8f * m_g;
            Scatter(snowStep, 15, s =>
            {
                if (!IsOpenLand(s, Land.DeepNorth, SnowMarkShore)) return;
                if (!OnSnowMarkBand(s.m_x, s.m_y) || !(s.m_roll < 0.8f)) return;
                if (SnowMarkCrowded(s, snowStep) || NearHighGround(s.m_x, s.m_y)) return;
                m_canvas.Stroke(s.m_x - 2.4f * m_g, s.m_y, s.m_x, s.m_y - 2f * m_g, Pen);
                m_canvas.Stroke(s.m_x, s.m_y - 2f * m_g, s.m_x + 2.4f * m_g, s.m_y, Pen);
            });
        }

        // Mistlands: the steep broken rock the spires stand on, where neither trees nor mist lie,
        // would otherwise be bare paper. Small scarps follow the ground: each edge runs along the
        // contour, its hachures point downhill, so the marks read as the terrain's own cliffs
        // rather than a texture. Only on the open rock between the glyphs, away from trees and
        // mountains so they do not overpopulate the map, so this runs once every glyph is placed,
        // on the background layer, before any glyph is inked.
        private void DrawScarps()
        {
            float scarpStep = ScarpStep * m_g;
            Scatter(scarpStep, scarpStep, ScarpJitter, 0.5f, 19, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < ScarpShore) return;
                if (s.m_slope < SpireSlope || !(s.m_roll < ScarpOdds) || ScarpCrowded(s, scarpStep)) return;
                Vector2 down = Downhill(s.m_x, s.m_y);
                // The mark's half extent: half its edge along the contour, its hachures downhill.
                float half = 0.5f * ScarpLength(s.m_seed, m_g), tick = ScarpTick * m_g;
                float ex = Math.Abs(down.y) * half + Math.Abs(down.x) * tick;
                float ey = Math.Abs(down.x) * half + Math.Abs(down.y) * tick;
                if (!ClearOfMist(s.m_x, s.m_y, ex, ey)) return;
                float clear = ScarpClear * m_g;
                if (!ClearOfGlyphs(s.m_x, s.m_y, ex + clear, ey + clear)) return;
                // Rivers are inked before the scarps, so a scarp would cross the stream.
                var along = new Vector2(-down.y, down.x) * half;
                if (OnRiver(s.m_x, s.m_y) || OnRiver(s.m_x + along.x, s.m_y + along.y) || OnRiver(s.m_x - along.x, s.m_y - along.y)) return;
                Scarp(s.m_x, s.m_y, m_g, s.m_seed, down);
            });
        }

        // A clear-cut's stumps, once every glyph is placed, so they keep off the hills and the
        // other glyphs' footprints (ClearOfGlyphs), on the background layer under every glyph.
        private void DrawStumps()
        {
            if (!m_world.HasGrove(Grove.ClearCut)) return;
            Scatter(StumpStep * G, StumpStep * G, 0.9f, 0f, 35, s =>
            {
                s.m_shore /= m_lod;
                s.m_woodDistance /= m_lod;
                if (s.m_grove != Grove.ClearCut || !IsOpenLand(s, Land.BlackForest, 3f)) return;
                // Felled in stands: a slow noise thins the stumps between loose groups, so an
                // even scatter does not read as a stipple, while thin ground keeps StumpFloor
                // of them, as stands with bare ground between read as lumps.
                float stand = Mathf.Lerp(StumpFloor, 1f, Mathf.Clamp01((Noise(s.m_x / StumpStand, s.m_y / StumpStand, m_world.m_seed + 35) - 0.35f) / 0.4f));
                if (!(s.m_roll < StumpOdds * stand)) return;
                // Off the hills and every other glyph, which would stand over the stumps.
                if (!ClearOfGlyphs(s.m_x, s.m_y - 0.8f * m_g, StumpClear * m_g, 0.5f * StumpClear * m_g)) return;
                Stump(s.m_x, s.m_y, m_g, s.m_seed);
            }, sheetGrid: true);
        }

        // A boulder field's boulders, as the stumps are: once every glyph is placed, off the hills,
        // the lone pines and every other glyph's footprint, on the background layer under every
        // glyph, on one sheet grid with margins in sheet pixels, so every zoom shows the same ones.
        private void DrawBoulders()
        {
            if (!m_world.HasGrove(Grove.Rocky)) return;
            Scatter(BoulderStep * G, BoulderStep * G, 0.9f, 0f, BoulderSalt, s =>
            {
                s.m_shore /= m_lod;
                s.m_woodDistance /= m_lod;
                if (s.m_grove != Grove.Rocky || !IsOpenLand(s, Land.BlackForest, 3f)) return;
                // Gathered into loose scree by a slow noise: an even scatter reads as a stipple.
                float stand = Mathf.Lerp(BoulderFloor, 1f, Mathf.Clamp01((Noise(s.m_x / BoulderStand, s.m_y / BoulderStand, m_world.m_seed + BoulderSalt) - 0.35f) / 0.4f));
                if (!(s.m_roll < BoulderOdds * stand)) return;
                // Off the water by the boulder's size on the sheet, not this level's, so every zoom
                // keeps the same ones; the coast and lake lines are inked before.
                if (s.m_shore < BoulderHalf(s.m_seed) * G + BoulderShore) return;
                float half = BoulderHalf(s.m_seed) * m_g;
                float clear = BoulderClear * m_g;
                if (!ClearOfGlyphs(s.m_x, s.m_y - 0.5f * half, half + clear, 0.5f * half + clear)) return;
                // Rivers are inked before, so a boulder would sit on the stream.
                if (OnRiver(s.m_x, s.m_y) || OnRiver(s.m_x - half, s.m_y) || OnRiver(s.m_x + half, s.m_y)) return;
                Boulder(s.m_x, s.m_y, m_g, s.m_seed);
                // The stone and the smaller one beside it (Glyphs.Boulder), which may sit a
                // little below the foot.
                m_boulders.Add(new Vector4(s.m_x - half, s.m_y - half, s.m_x + half, s.m_y + 0.5f * m_g));
            }, sheetGrid: true);
            // Filed only now, so the boulders do not keep off each other.
            foreach (Vector4 box in m_boulders) AddGlyphBox(box);
            DrawRockStipple();
        }

        // Every boulder's footprint this painter drew, which the boulder field's stipple keeps off.
        private readonly List<Vector4> m_boulders = new List<Vector4>();

        // A boulder field's open ground: the Plains' stipple of fine dots, once the boulders are
        // drawn, so each dot keeps off them and every other glyph's footprint (lone pines, hills)
        // at this level of detail, on the sheet and in close-ups alike. The ground under them
        // would otherwise be blank paper beside the Plains' stippled heath.
        private void DrawRockStipple()
        {
            float clear = RockStippleClear * m_g;
            Scatter(5f * m_g, RockStippleSalt, s =>
            {
                if (s.m_grove != Grove.Rocky || !IsOpenLand(s, Land.BlackForest, 2f) || !(s.m_roll < RockStipple)) return;
                if (!ClearOfGlyphs(s.m_x, s.m_y, clear, clear)) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
            });
        }

        // Wood ground that ended up with no tree on it. The wash darkens all of the wood field
        // (Paper.Washed), but the trees keep off steep ground (SteepGround), the shore, rivers and
        // the land column's water, so whole steep slopes, mostly between the Black Forest's peaks
        // and hills and the coast, lay as an empty dark green. They take their biome's open fill
        // instead, a little lighter: the Plains' stipple in the Black Forest (as its boulder
        // fields have) and the Plains, the Meadows' grass tufts on the Meadows' own sheet grid.
        // Only where no tree of these woods has its footprint within WoodlessGap, so a wood's own
        // gaps and its margins along the shore stay as they are; the Mountains' firs hidden
        // behind the peaks do not count. Drawn once every glyph is placed, at each level of detail
        // against that level's own trees, as the boulder field's stipple is. Like the open ground's
        // fills it does not test the glyphs' boxes: a peak's box takes in the open ground beside its
        // flanks, and every glyph scrapes what lies under its body when it is inked.
        private void DrawWoodlessGround()
        {
            var trees = new Dictionary<int, List<Vector4>>();
            foreach (Glyph glyph in m_glyphs)
            {
                // A lone oak stands on open ground, not in a wood; the grass keeps out from
                // behind it (TuftByOak) as on the open meadows.
                if (!IsTree(glyph.m_kind) || glyph.m_kind == Kind.Oak) continue;
                Land foot = m_world.m_biome[m_world.Index(glyph.m_x * MetresPerPixel - HalfExtent, HalfExtent - glyph.m_y * MetresPerPixel)];
                if (foot == Land.Meadows || foot == Land.BlackForest || foot == Land.Plains) FileBox(trees, GlyphBox(glyph));
            }
            float gap = WoodlessGap * m_t;
            FindOaks();
            Scatter(WoodlessStep * m_g, WoodlessSalt, s =>
            {
                if (!s.m_isLand || !s.m_wood || s.m_shore < 2f || !(s.m_roll < WoodlessStipple)) return;
                if (s.m_land != Land.BlackForest && s.m_land != Land.Plains) return;
                if (!ClearOf(trees, s.m_x, s.m_y, gap, gap) || OnRiver(s.m_x, s.m_y)) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
            });
            // The Meadows' grass tufts, on a finer sheet grid of their own (the open meadows' is so
            // sparse that a steep strip got one tuft or none), inked again over a pin in front as
            // those are.
            Scatter(WoodlessGrassStep * G, WoodlessGrassStep * G, WoodlessGrassJitter, 0f, WoodlessGrassSalt, s =>
            {
                if (!s.m_isLand || !s.m_wood || s.m_land != Land.Meadows || s.m_shore / m_lod < 3f || !(s.m_roll < WoodlessGrassOdds)) return;
                if (!ClearOf(trees, s.m_x, s.m_y, gap, gap)) return;
                Tuft(s.m_x, s.m_y);
            }, sheetGrid: true);
        }

        // The open meadows' short tufts of grass, once every glyph is placed, so they keep out
        // from behind the lone oaks (TuftByOak). They are few, so they keep one grid and its
        // margins in sheet pixels at every level of detail, and every zoom shows the same tufts: a
        // grid of each level's own brings a fresh set at each zoom, thick in close-ups and almost
        // none on the sheet. Only the tuft itself shrinks with the level, as the trees do.
        private void DrawMeadowGrass()
        {
            FindOaks();
            Scatter(8f * G, 8f * G, 0.9f, 0f, 14, s =>
            {
                s.m_shore /= m_lod;
                s.m_woodDistance /= m_lod;
                if (!IsOpenLand(s, Land.Meadows, 3f) || !(s.m_roll < 0.3f)) return;
                Tuft(s.m_x, s.m_y);
            }, sheetGrid: true);
        }

        // A Meadows grass tuft standing at (x, y), unless a lone oak in front would cut it or it
        // would reach water or a river. Laid after the rivers, which used to scrape the tufts
        // under them, so it tests both ends of both blades (Grass), each with TuftWaterClear round
        // it: against the sheet's land or a close-up's own. One over a
        // pin or in front of an oak's foot is inked again after the glyphs (DrawGrassOverMarks).
        private void Tuft(float x, float y)
        {
            if (!TuftEndDry(x - 1f * m_g, y) || !TuftEndDry(x + 0.8f * m_g, y)
                || !TuftEndDry(x - 1.6f * m_g, y - 2.6f * m_g) || !TuftEndDry(x + 1.2f * m_g, y - 2.8f * m_g)) return;
            TuftOak oak = TuftByOak(x, y);
            if (oak == TuftOak.Behind) return;
            Grass(x, y, m_g);
            if (oak == TuftOak.Front) m_frontGrass.Add(new Vector2(x, y));
            else if (OnAMark(new Vector4(x - 1.6f * m_g, y - 2.8f * m_g, x + 1.2f * m_g, y))) m_markGrass.Add(new Vector2(x, y));
        }

        // Whether a blade's end and the points TuftWaterClear round it are land off every river.
        private bool TuftEndDry(float x, float y)
        {
            float r = TuftWaterClear * m_pen;
            return Dry(x, y) && Dry(x - r, y) && Dry(x + r, y) && Dry(x, y - r) && Dry(x, y + r);

            bool Dry(float px, float py) => IsLandAt(px, py) && !OnRiver(px, py);
        }

        // Whether a tree glyph would keep clear of every non-tree glyph placed before it (the
        // hills, peaks and pins): on the sheet those placed so far, in a close-up the sheet's
        // footprints from the record.
        private bool ClearOfPlaced(Glyph g)
        {
            Vector4 box = GlyphBox(g);
            if (m_grid != null) return ClearOfGlyphs(0.5f * (box.x + box.z), 0.5f * (box.y + box.w), 0.5f * (box.z - box.x), 0.5f * (box.w - box.y));
            foreach (Glyph other in m_glyphs)
            {
                if (IsTree(other.m_kind) || other.m_kind == Kind.Mist) continue;
                Vector4 o = GlyphBox(other);
                if (o.x < box.z && box.x < o.z && o.y < box.w && box.y < o.w) return false;
            }
            return true;
        }

        // The drawn footprint of every placed glyph but the mist (left, top, right, bottom),
        // filed in PeakCell squares, which the scarps keep ScarpClear off: on the sheet all of
        // its glyphs, in a close-up its own trees and the sheet's other glyphs from the record.
        private readonly Dictionary<int, List<Vector4>> m_glyphBoxes = new Dictionary<int, List<Vector4>>();

        private void AddGlyphBox(Vector4 box) => FileBox(m_glyphBoxes, box);

        // Files a box in every PeakCell square it reaches.
        private static void FileBox(Dictionary<int, List<Vector4>> boxes, Vector4 box)
        {
            int x0 = (int)Math.Floor(box.x / PeakCell), x1 = (int)Math.Floor(box.z / PeakCell);
            int y0 = (int)Math.Floor(box.y / PeakCell), y1 = (int)Math.Floor(box.w / PeakCell);
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    int key = PeakCellKey(cx, cy);
                    if (!boxes.TryGetValue(key, out List<Vector4> cell)) boxes[key] = cell = new List<Vector4>(8);
                    cell.Add(box);
                }
            }
        }

        // Whether the rectangle of the given half width and height round (x, y) misses every
        // glyph's footprint.
        private bool ClearOfGlyphs(float x, float y, float halfWidth, float halfHeight) =>
            ClearOf(m_glyphBoxes, x, y, halfWidth, halfHeight);

        // The same against the boxes filed in the given PeakCell squares.
        private static bool ClearOf(Dictionary<int, List<Vector4>> boxes, float x, float y, float halfWidth, float halfHeight)
        {
            float left = x - halfWidth, right = x + halfWidth, top = y - halfHeight, bottom = y + halfHeight;
            int x0 = (int)Math.Floor(left / PeakCell), x1 = (int)Math.Floor(right / PeakCell);
            int y0 = (int)Math.Floor(top / PeakCell), y1 = (int)Math.Floor(bottom / PeakCell);
            for (int cy = y0; cy <= y1; cy++)
            {
                for (int cx = x0; cx <= x1; cx++)
                {
                    if (!boxes.TryGetValue(PeakCellKey(cx, cy), out List<Vector4> cell)) continue;
                    foreach (Vector4 box in cell)
                        if (box.x < right && left < box.z && box.y < bottom && top < box.w) return false;
                }
            }
            return true;
        }

        // A glyph's footprint, filed for the scarps. A giant tree's is its crown and the trunk
        // under it apart: one box round both blanks the rock beside every trunk.
        private void AddFootprint(Glyph g)
        {
            Vector4 box = GlyphBox(g);
            if (g.m_kind == Kind.GiantTree)
            {
                float crown = g.m_y - GiantCrownFoot * g.m_size, trunk = GiantTrunkHalf * g.m_size;
                AddGlyphBox(new Vector4(box.x, box.y, box.z, crown));
                box = new Vector4(g.m_x - trunk, crown, g.m_x + trunk, box.w);
            }
            AddGlyphBox(box);
        }

        // A glyph's drawn footprint, a little generous: crowns, bodies, companions behind and
        // the roots or skirt below the foot, from the same sizes and seeds the drawing uses.
        private static Vector4 GlyphBox(Glyph g)
        {
            float s = g.m_size, half, height;
            switch (g.m_kind)
            {
                case Kind.Peak:
                    half = 0.56f * s;
                    height = PeakHeightOf(s, g.m_seed);
                    break;
                case Kind.Hill:
                    half = 0.75f * s;
                    height = 0.5f * s;
                    break;
                case Kind.Ridge:
                    half = 0.56f * s;
                    height = RidgeHeightOf(s, g.m_seed);
                    break;
                case Kind.Spire:
                    height = SpireHeightOf(s, g.m_seed);
                    half = 0.42f * height;
                    // A spire in the water: its waterline reaching out either side (at most the
                    // front pinnacle's width, SpireReach) and the shorter line under its foot.
                    if (g.m_wet) return new Vector4(g.m_x - 0.62f * height, g.m_y - height, g.m_x + 0.62f * height, g.m_y + ThornLowDrop + 1f);
                    break;
                case Kind.Thorn:
                    // The tallest thorn, its companions and waterline either side, and the
                    // shorter line under its foot.
                    return new Vector4(g.m_x - 6f * s, g.m_y - 9f * s, g.m_x + 6f * s, g.m_y + 2.5f);
                case Kind.GiantTree:
                    half = 5.8f * s;
                    height = 12f * s;
                    break;
                case Kind.Conifer:
                    half = 3.6f * s;
                    height = 14.5f * s;
                    break;
                case Kind.DeadTree:
                    half = 5f * s;
                    height = 14f * s;
                    break;
                case Kind.SmallTree:
                    // The lobed crown at its widest and tallest (SmallShape) with its scraped rim.
                    half = 3.4f * s;
                    height = 7.6f * s;
                    break;
                case Kind.Oak:
                    // The billowy crown at its widest and tallest (OakShape, OakPuffs), at OakBig.
                    half = 5f * s * OakBig;
                    height = 9f * s * OakBig;
                    break;
                case Kind.Pin:
                {
                    Vector3 pin = PinBox(g);
                    return new Vector4(pin.x, pin.y, pin.x + pin.z, pin.y + pin.z);
                }
                default:
                    // Broadleaf and plains trees.
                    half = 3.6f * s;
                    height = 12f * s;
                    break;
            }
            return new Vector4(g.m_x - half, g.m_y - height, g.m_x + half, g.m_y + 0.5f * s);
        }

        // A scarp's length along its edge, from its seed.
        private static float ScarpLength(int seed, float s) => Mathf.Lerp(4f, 6f, Unit(seed, 50)) * s;

        // Whether a neighbouring cell of the scarp grid could put a scarp of higher priority too
        // close to this one. From the grid's hashes alone, as the chevrons' test, so a neighbour
        // on gentler ground can still make one give way, which only thins the rock's edges.
        private bool ScarpCrowded(Spot s, float step)
        {
            float priority = Unit(s.m_seed, 30);
            float length = ScarpLength(s.m_seed, m_g);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int gx = s.m_gx + dx, gy = s.m_gy + dy;
                    int seed = HashInt(gx, gy, s.m_gridSeed);
                    if (!(Unit(seed, 3) < ScarpOdds) || Unit(seed, 30) < priority) continue;
                    float x = (gx + 0.5f + (gy % 2) * 0.5f + (Unit(seed, 1) - 0.5f) * ScarpJitter) * step - s.m_x;
                    float y = (gy + 0.5f + (Unit(seed, 2) - 0.5f) * ScarpJitter) * step - s.m_y;
                    float reach = ScarpGap * 0.5f * (length + ScarpLength(seed, m_g));
                    if (x * x + y * y < reach * reach) return true;
                }
            }
            return false;
        }

        // The downhill direction at a canvas point (y runs south), from the heights a map pixel
        // either side: the world's on the sheet, the tile's own in a close-up, as the slope the
        // rules test. Flat ground falls south, the map's own default.
        private Vector2 Downhill(float x, float y)
        {
            float d = m_world.m_pixelSize / MetresPerPixel;
            float east = GroundHeight(x + d, y) - GroundHeight(x - d, y);
            float south = GroundHeight(x, y + d) - GroundHeight(x, y - d);
            var fall = new Vector2(-east, -south);
            return fall.sqrMagnitude > 1e-6f ? fall.normalized : new Vector2(0f, 1f);
        }

        private float GroundHeight(float x, float y) => m_grid != null
            ? m_grid.Sample(m_grid.m_height, x, y)
            : m_world.HeightAt(x * MetresPerPixel - HalfExtent, HalfExtent - y * MetresPerPixel);

        // Whether a point lies on a crest line of the snow marks' band noise.
        private bool OnSnowMarkBand(float x, float y) =>
            1f - Math.Abs(2f * Noise(x / SnowMarkScale, y / SnowMarkScale, m_world.m_seed + 9) - 1f) >= SnowMarkBand;

        // Whether a neighbouring cell of the chevron grid could put a chevron of higher priority
        // within SnowMarkGap of this one. Decided from the grid's hashes and the band noise alone,
        // without the ground, so the sheet and every tile agree; a neighbour off the snow can still
        // make a chevron give way, which only thins the bands' ends.
        private bool SnowMarkCrowded(Spot s, float step)
        {
            float halfWidth = 2.4f * m_g, height = 2f * m_g, gap = SnowMarkGap * m_g;
            float priority = Unit(s.m_seed, 30);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int gx = s.m_gx + dx, gy = s.m_gy + dy;
                    int seed = HashInt(gx, gy, s.m_gridSeed);
                    if (!(Unit(seed, 3) < 0.8f) || Unit(seed, 30) < priority) continue;
                    float x = (gx + 0.5f + (Unit(seed, 1) - 0.5f) * 0.9f) * step;
                    float y = (gy + 0.5f + (Unit(seed, 2) - 0.5f) * 0.9f) * step;
                    if (Math.Abs(x - s.m_x) < 2f * halfWidth + gap && Math.Abs(y - s.m_y) < height + gap && OnSnowMarkBand(x, y)) return true;
                }
            }
            return false;
        }

        // Whether high ground that carries peaks lies where one of them could stand over or beside
        // a mark at (x, y): a peak rises north of its foot, so the search reaches far south and only
        // a little north. Mountain ground over PeakHeight is massed with peaks; in the Deep North
        // only its summits and hilltops carry a glyph (NorthTops). Read from the world's own
        // heights, the same on the sheet and every tile.
        private bool NearHighGround(float x, float y)
        {
            for (float dy = -3f * G; dy <= 22f * G; dy += 2.5f * G)
            {
                for (float dx = -13f * G; dx <= 13f * G; dx += 2.5f * G)
                {
                    float wx = (x + dx) * MetresPerPixel - HalfExtent, wz = HalfExtent - (y + dy) * MetresPerPixel;
                    float above = m_world.HeightAt(wx, wz) - m_world.m_waterLevel;
                    if (above >= PeakHeight && m_world.m_biome[m_world.Index(wx, wz)] == Land.Mountain) return true;
                }
            }
            foreach (Vector2 top in NorthTops())
            {
                float dx = top.x - x, dy = top.y - y;
                if (dx >= -13f * G && dx <= 13f * G && dy >= -3f * G && dy <= 22f * G) return true;
            }
            return false;
        }

        // The Deep North's summits and hilltops over DeepNorthSummitHeight (sheet canvas points),
        // where PlaceSummitsAndHills may stand a peak or a hill, found once per world from its
        // heights and shared by the sheet and every close-up (WorldSource.m_northTops). Ground
        // fills are drawn before the glyphs are placed, so they cannot look at the glyphs.
        private List<Vector2> NorthTops()
        {
            lock (m_world)
            {
                if (m_world.m_northTops != null) return m_world.m_northTops;
                var tops = new List<Vector2>();
                int size = m_world.m_size;
                float[] h = m_world.m_height;
                for (int z = ProminenceCells; z < size - ProminenceCells; z++)
                    for (int x = ProminenceCells; x < size - ProminenceCells; x++)
                    {
                        int i = z * size + x;
                        if (m_world.m_biome[i] != Land.DeepNorth || h[i] - m_world.m_waterLevel < DeepNorthSummitHeight) continue;
                        if (!IsHighest(h, size, x, z, HilltopCells) || h[i] - Lowest(h, size, x, z, ProminenceCells) < HillProminence) continue;
                        float wx = (x - size / 2 + 0.5f) * m_world.m_pixelSize, wz = (z - size / 2 + 0.5f) * m_world.m_pixelSize;
                        tops.Add(new Vector2((wx + HalfExtent) / MetresPerPixel, (HalfExtent - wz) / MetresPerPixel));
                    }
                m_world.m_northTops = tops;
                return tops;
            }
        }

        // One marsh mark of the swamp rule's grid (cell gx, gy, its square grid of spacing c with
        // the default jitter), from hashes and the patch noise alone: where it stands, its size,
        // whether it would be inked and with what, its priority against neighbours and the box
        // it covers with a small gap. The ground is not looked at, so a neighbour off the swamp
        // can still make a mark give way, only ever at a swamp's edge.
        private void MarshMark(int gx, int gy, int gridSeed, float c, out MarshBox mark)
        {
            int seed = HashInt(gx, gy, gridSeed);
            mark = new MarshBox
            {
                m_x = (gx + 0.5f + (Unit(seed, 1) - 0.5f) * 0.9f) * c,
                m_y = (gy + 0.5f + (Unit(seed, 2) - 0.5f) * 0.9f) * c,
                m_priority = Unit(seed, 30),
            };
            // The clumps follow the real water: wetness from the heights before the pools were
            // raised, with a little noise so they do not trace the pools exactly; noise alone makes
            // invented patches.
            float wx = mark.m_x * MetresPerPixel - HalfExtent, wz = HalfExtent - mark.m_y * MetresPerPixel;
            float wet = m_world.Sample(m_world.m_swampWet, wx, wz);
            float noise = Mathf.Clamp01((Noise(mark.m_x / MarshPatch, mark.m_y / MarshPatch, m_world.m_seed + 13) - 0.3f) / 0.4f);
            float patch = Mathf.Clamp01(0.65f * Mathf.Clamp01(wet * 1.4f) + 0.35f * noise);
            patch = patch * patch * (3f - 2f * patch);
            // Thinner over the swamp's outermost few dozen metres, where it gives way to the
            // next biome; the wash still marks the exact edge.
            float inside = Mathf.Clamp01((m_world.Sample(m_world.m_swampField, wx, wz) - 0.5f) / 0.4f);
            mark.m_inked = Unit(seed, 3) < Mathf.Lerp(0.3f, 0.9f, patch) * Mathf.Lerp(0.35f, 1f, inside);
            mark.m_half = Mathf.Lerp(0.2f, 0.32f, Unit(seed, 4)) * c;
            // Reeds stand where reeds grow, on the rims of the pools.
            float rim = 1f - Mathf.Abs(2f * Mathf.Clamp01(wet) - 1f);
            mark.m_reeds = Unit(seed, 5) < Mathf.Lerp(0.25f, 0.85f, Mathf.Max(rim, patch * 0.5f));
            mark.m_second = Unit(seed, 7) < 0.3f;
            float gap = 0.08f * c;
            mark.m_left = mark.m_x - mark.m_half - gap;
            mark.m_right = mark.m_x + mark.m_half + gap;
            mark.m_top = mark.m_y - (mark.m_reeds ? 0.48f * c : 0.05f * c) - gap;
            mark.m_bottom = mark.m_y + (mark.m_second ? 0.22f * c : 0.05f * c) + gap;
        }

        private struct MarshBox
        {
            public float m_x, m_y, m_half, m_priority;
            public float m_left, m_right, m_top, m_bottom;
            public bool m_inked, m_reeds, m_second;
        }

        // Land of the given biome, at least shore pixels from the water and clear of every wood.
        private static bool IsOpenLand(Spot s, Land land, float shore) =>
            s.m_isLand && s.m_land == land && s.m_shore >= shore && s.m_woodDistance >= WoodClearance;

        // Summits and hilltops are the terrain's real local height maxima, found on vanilla's own
        // map pixels, so the landmarks on the atlas are the ones on the ground: a peak glyph's summit
        // sits on the actual summit, a hill's crown on the actual hilltop.
        private void PlaceSummitsAndHills()
        {
            int size = m_world.m_size;
            float water = m_world.m_waterLevel;
            float[] h = m_world.m_height;
            var hillMisses = new List<HillMiss>();
            for (int z = ProminenceCells; z < size - ProminenceCells; z++)
            {
                for (int x = ProminenceCells; x < size - ProminenceCells; x++)
                {
                    int i = z * size + x;
                    float above = h[i] - water;
                    if (above < 3f) continue;
                    Land land = m_world.m_biome[i];
                    // The Deep North's uplands: a peak on a real summit, hills on its other hilltops.
                    bool upland = land == Land.DeepNorth && above >= DeepNorthSummitHeight;
                    bool high = (land == Land.Mountain && above >= PeakHeight) || upland;
                    // A clear-cut or a boulder field is open country too: with no wood its rises show.
                    bool openGrove = land == Land.BlackForest && m_world.m_grove != null && WorldSource.IsOpen(m_world.m_grove[i]);
                    bool hillCountry = ((land == Land.Meadows || land == Land.Plains) && m_world.m_wood[i] < WoodEdge)
                        || (land == Land.Mountain && above < PeakHeight) || openGrove;
                    if (land == Land.Mistlands)
                    {
                        PlaceSummitSpire(h, size, x, z);
                        continue;
                    }
                    if (!high && !hillCountry) continue;
                    int radius = high ? SummitCells : HilltopCells;
                    bool highest = IsHighest(h, size, x, z, radius);
                    float prominence = highest || upland ? h[i] - Lowest(h, size, x, z, ProminenceCells) : 0f;
                    // An upland top that is no summit may still be a hilltop.
                    if (upland && !(highest && prominence >= SummitProminence))
                    {
                        if (!IsHighest(h, size, x, z, HilltopCells)) continue;
                        high = false;
                    }
                    else if (!highest) continue;

                    float wx = (x - size / 2 + 0.5f) * m_world.m_pixelSize;
                    float wz = (z - size / 2 + 0.5f) * m_world.m_pixelSize;
                    float cx = (wx + HalfExtent) / MetresPerPixel, cy = (HalfExtent - wz) / MetresPerPixel;
                    float dx = cx - CanvasSize * 0.5f, dy = cy - CanvasSize * 0.5f;
                    float edge = EdgeRadius / MetresPerPixel * EdgeFraction;
                    if (dx * dx + dy * dy > edge * edge || !IsLandAt(cx, cy)) continue;
                    int seed = HashInt(x, z, m_world.m_seed + 77);

                    if (high)
                    {
                        // The Mountains' massifs get their peaks and ridges from PlaceMassifs.
                        if (!upland) continue;
                        if (prominence < SummitProminence) continue;
                        float width = Mathf.Lerp(22f, 34f, Mathf.Clamp01((prominence - SummitProminence) / 80f)) * G;
                        PlaceSummitPeak(cx, cy, width, seed);
                    }
                    else
                    {
                        if (prominence < HillProminence) continue;
                        float width = Mathf.Lerp(9f, 15f, Mathf.Clamp01((prominence - HillProminence) / 25f)) * G;
                        // Water is FitHill's test, on the hill's whole drawn shape: keeping the foot
                        // half a width off water would shut out hilltops whose shrunk hill fits.
                        HillFoot(cx, cy, width, seed, out float woodX, out float woodY);
                        if (m_woodDistance[PixelAt(woodX, woodY)] < width * 0.5f) continue;
                        if (FitHill(cx, cy, width, seed, moved: false, out Glyph hill)) AddHill(hill);
                        else hillMisses.Add(new HillMiss { m_x = cx, m_y = cy, m_width = width, m_seed = seed, m_land = land, m_height = h[i], m_prominence = prominence });
                    }
                }
            }
            // A hill that does not fit on its hilltop even shrunk is moved a little off the water,
            // once every hill that fits has its place, as Ashlands glyphs are moved off lava.
            foreach (HillMiss miss in hillMisses)
                if (ShiftHill(miss, out Glyph hill)) AddHill(hill);
        }

        // The peak on a real summit. The glyph rises from its foot to an apex offset a little east
        // of the foot, and is placed so the summit lies SummitAnchor of the way up its centre
        // line. Where its base would reach water there (a summit on a narrow island or spit), it
        // tries the summit at other heights on the glyph, nearest first, then smaller sizes: a
        // summit that is the only high ground of its island keeps its peak.
        private void PlaceSummitPeak(float cx, float cy, float width, int seed)
        {
            for (int step = 0; step < SummitShrinkSteps; step++, width *= SummitShrink)
            {
                float height = PeakHeightOf(width, seed);
                foreach (float anchor in s_summitAnchors)
                {
                    float footX = cx - width * Unit(seed, 11) * 0.12f * anchor, footY = cy + height * anchor;
                    if (!PeakOnLand(footX, footY, width, height, anchor)) continue;
                    m_glyphs.Add(new Glyph { m_x = footX, m_y = footY, m_size = width, m_kind = Kind.Peak, m_seed = seed });
                    m_summits.Add(new Vector4(footX, footY, width, height));
                    m_hillBoxes.Add(new Vector4(footX - width * 0.56f, footY - height, footX + width * 0.56f, footY));
                    return;
                }
            }
        }

        // The heights on a summit's peak tried for the summit, SummitAnchor first.
        private static readonly float[] s_summitAnchors = { SummitAnchor, 0.6f, 0.2f, 0.8f, 1f, 0f };

        // A hilltop whose hill does not fit there, kept for ShiftHill: the hilltop (canvas), the
        // hill's full width and seed, the biome, and the hilltop's height and prominence (metres).
        private struct HillMiss
        {
            public float m_x, m_y, m_width, m_height, m_prominence;
            public int m_seed;
            public Land m_land;
        }

        // Where a hill's foot goes for its hilltop (x, y): the front dome's crown, wherever its
        // crest puts it, lands on the hilltop. The dome scales with its width, so the crown is
        // found on a unit dome.
        private void HillFoot(float x, float y, float width, int seed, out float footX, out float footY)
        {
            DomeShape shape = DomeOutline(m_crownProbe, 0f, 0f, 1f, seed, false);
            footX = x - shape.m_crownX * width;
            footY = y + shape.m_crownRise * width;
        }

        private readonly List<Vector2> m_crownProbe = new List<Vector2>(DomeSteps + 1);

        private void AddHill(Glyph hill)
        {
            m_glyphs.Add(hill);
            ProbeHill(hill.m_x, hill.m_y, hill.m_size, hill.m_seed, hill.m_lone);
            m_hillBoxes.Add(ProbeBox());
        }

        // The hill for the hilltop (x, y) whose every part keeps HillWaterGap off water and
        // rivers (HillDry): as drawn if it fits, else without its dome behind, else smaller by
        // HillShrinkStep of its width down to HillMinScale, the foot reset each time so the crown
        // stays on the hilltop. Shrinking keeps a hill on its real hilltop, which matters more
        // on a navigation map than its size. Every hill keeps its foot half its width off woods; a
        // moved one also keeps HillGap clear of every hill and summit peak placed.
        private bool FitHill(float x, float y, float width, int seed, bool moved, out Glyph hill)
        {
            hill = default;
            bool behind = DomesBehind(seed) > 0;
            int steps = Mathf.RoundToInt((1f - HillMinScale) / HillShrinkStep);
            for (int k = behind ? -1 : 0; k <= steps; k++)
            {
                float w = width * (1f - Math.Max(k, 0) * HillShrinkStep);
                bool lone = behind && k >= 0;
                HillFoot(x, y, w, seed, out float footX, out float footY);
                if (m_woodDistance[PixelAt(footX, footY)] < w * 0.5f) continue;
                ProbeHill(footX, footY, w, seed, lone);
                if (!m_hillProbe.TrueForAll(HillDryAt)) continue;
                if (moved && HillCrowded(ProbeBox())) continue;
                hill = new Glyph { m_x = footX, m_y = footY, m_size = w, m_kind = Kind.Hill, m_seed = seed, m_lone = lone };
                return true;
            }
            return false;
        }

        // Moves a hill that fits nowhere on its hilltop: hilltops in rings of HillShiftStep of
        // its width out to HillShiftReach of it (ShiftFoot), away from the water its full shape
        // touches first, each tried at every size FitHill tries. The moved hilltop must still be
        // high ground of the hill's biome: at least HillShiftDrop of its prominence below the
        // real top, so the hill still stands on the rise it marks.
        private bool ShiftHill(HillMiss miss, out Glyph hill)
        {
            hill = default;
            HillFoot(miss.m_x, miss.m_y, miss.m_width, miss.m_seed, out float footX, out float footY);
            ProbeHill(footX, footY, miss.m_width, miss.m_seed, false);
            Vector2 wet = Vector2.zero, all = Vector2.zero;
            int count = 0;
            foreach (Vector2 p in m_hillProbe)
            {
                all += p;
                if (HillDryAt(p)) continue;
                wet += p;
                count++;
            }
            if (count == 0) return false;
            Vector2 away = all / m_hillProbe.Count - wet / count;
            if (away.sqrMagnitude < 1e-6f) return false;
            away.Normalize();
            float floor = miss.m_height - miss.m_prominence * HillShiftDrop;
            Glyph found = default;
            bool fits = ShiftFoot(new Vector2(miss.m_x, miss.m_y), away, miss.m_width, HillShiftStep, HillShiftReach, (x, y) =>
            {
                float wx = x * MetresPerPixel - HalfExtent, wz = HalfExtent - y * MetresPerPixel;
                if (!IsLandAt(x, y) || m_world.m_biome[m_world.Index(wx, wz)] != miss.m_land) return false;
                float ground = m_world.HeightAt(wx, wz);
                if (ground < floor || ground - m_world.m_waterLevel < 3f) return false;
                return FitHill(x, y, miss.m_width, miss.m_seed, moved: true, out found);
            });
            hill = found;
            return fits;
        }

        // Feet (or hilltops) tried in turn when a glyph is moved off something: rings step of
        // its width apart out to reach of it, at ShiftAngles from straight away first and at most
        // square to it, inside the frame, so the nearest fitting spot wins and the order is fixed.
        private static bool ShiftFoot(Vector2 from, Vector2 away, float width, float step, float reach, Func<float, float, bool> fits)
        {
            float edge = EdgeRadius / MetresPerPixel * EdgeFraction;
            float centre = CanvasSize * 0.5f;
            int rings = Mathf.RoundToInt(reach / step);
            for (int ring = 1; ring <= rings; ring++)
            {
                float distance = width * step * ring;
                foreach (float angle in ShiftAngles)
                {
                    Vector2 offset = Rotate(away, angle) * distance;
                    float x = from.x + offset.x, y = from.y + offset.y;
                    float dx = x - centre, dy = y - centre;
                    if (dx * dx + dy * dy > edge * edge) continue;
                    if (fits(x, y)) return true;
                }
            }
            return false;
        }

        // The bounds of the probed hill (ProbeHill): x min, y min, x max, y max.
        private Vector4 ProbeBox()
        {
            var box = new Vector4(float.MaxValue, float.MaxValue, float.MinValue, float.MinValue);
            foreach (Vector2 p in m_hillProbe)
                box = new Vector4(Math.Min(box.x, p.x), Math.Min(box.y, p.y), Math.Max(box.z, p.x), Math.Max(box.w, p.y));
            return box;
        }

        // Whether a moved hill's bounds come within HillGap of a hill or summit peak placed.
        private bool HillCrowded(Vector4 box)
        {
            foreach (Vector4 other in m_hillBoxes)
            {
                if (box.x - HillGap < other.z && other.x < box.z + HillGap && box.y - HillGap < other.w && other.y < box.w + HillGap) return true;
            }
            return false;
        }

        // The bounds of every hill and summit peak placed, which moved hills keep clear of.
        private readonly List<Vector4> m_hillBoxes = new List<Vector4>(4096);

        private readonly List<Vector2> m_hillProbe = new List<Vector2>(256);
        private readonly List<Vector2> m_domeProbe = new List<Vector2>(DomeSteps + 1);

        // Points over everything a hill inks or scrapes (Glyphs.Hill): each dome's outline, its
        // inside down to the foot, its foot line and its shade dashes, the dome behind unless lone.
        private void ProbeHill(float x, float y, float width, int seed, bool lone)
        {
            m_hillProbe.Clear();
            int behind = lone ? 0 : DomesBehind(seed);
            for (int k = 0; k < behind; k++)
            {
                DomeBehind(x, y, width, seed, k, out float bx, out float by, out float small);
                ProbeDome(bx, by, small, seed + 1 + k, true);
            }
            ProbeDome(x, y, width, seed, false);
        }

        private void ProbeDome(float x, float y, float width, int seed, bool behind)
        {
            DomeShape shape = DomeOutline(m_domeProbe, x, y, width, seed, behind);
            for (int i = 0; i < m_domeProbe.Count; i++)
            {
                Vector2 p = m_domeProbe[i];
                m_hillProbe.Add(p);
                if (i > 0) m_hillProbe.Add((p + m_domeProbe[i - 1]) * 0.5f);
                m_hillProbe.Add(new Vector2(p.x, (p.y + y) * 0.5f));
            }
            for (int j = 0; j <= 6; j++) m_hillProbe.Add(new Vector2(x + (j / 6f - 0.5f) * width, y));
            for (int k = 0; k < shape.m_dashes; k++)
            {
                if (!DomeDash(shape, m_domeProbe, x, y, width, seed, k, out Vector2 a, out Vector2 b)) continue;
                m_hillProbe.Add(a);
                m_hillProbe.Add((a + b) * 0.5f);
                m_hillProbe.Add(b);
            }
        }

        // Whether a probed point keeps HillWaterGap off water (sea, lake or the frame's margin)
        // and off every river's inked course.
        private bool HillDryAt(Vector2 p) => m_landDistance[PixelAt(p.x, p.y)] >= HillWaterGap && !OnRiver(p.x, p.y);

        // A Mistlands crag on a real summit, sized by its prominence and placed as peaks are, the
        // summit SummitAnchor of the way up it. Its foot must stand in the Mistlands: a crag on a
        // summit near the border would otherwise plant its base, and the Mistlands' rock with it,
        // in the biome to the south. Such a crag slides north along its centre line, in
        // SpireAnchorSteps down to its foot on the summit, which is Mistlands, so a border summit
        // keeps its crag, rising over the Mistlands behind. Both ends of its base must be land
        // and SpireLandBase of it must stand over land, or it shrinks by SpireShrink a step, and
        // is left out if it never fits.
        private void PlaceSummitSpire(float[] h, int size, int x, int z)
        {
            if (!IsHighest(h, size, x, z, SpireCells)) return;
            float prominence = h[z * size + x] - Lowest(h, size, x, z, ProminenceCells);
            if (prominence < SpireProminence) return;
            float wx = (x - size / 2 + 0.5f) * m_world.m_pixelSize;
            float wz = (z - size / 2 + 0.5f) * m_world.m_pixelSize;
            float cx = (wx + HalfExtent) / MetresPerPixel, cy = (HalfExtent - wz) / MetresPerPixel;
            float dx = cx - CanvasSize * 0.5f, dy = cy - CanvasSize * 0.5f;
            float edge = EdgeRadius / MetresPerPixel * EdgeFraction;
            if (dx * dx + dy * dy > edge * edge || !IsLandAt(cx, cy)) return;
            int seed = HashInt(x, z, m_world.m_seed + 78);
            float largest = Mathf.Lerp(1.1f, 1.6f, Mathf.Clamp01((prominence - SpireProminence) / 60f)) * G;
            // On land at any size first; only a summit whose crag fits nowhere on it (an islet's)
            // stands it in the shallows at its foot, rising out of the water over the islet.
            for (int pass = 0; pass < 2; pass++)
            {
                float s = largest;
                for (int step = 0; step < SpireShrinkSteps; step++, s *= SpireShrink)
                {
                    float height = SpireHeightOf(s, seed), half = height * 0.31f;
                    for (int k = 0; k < SpireAnchorSteps; k++)
                    {
                        float anchor = SummitAnchor * (1f - k / (SpireAnchorSteps - 1f));
                        float footY = cy + height * anchor;
                        var spire = new Glyph { m_x = cx, m_y = footY, m_size = s, m_kind = Kind.Spire, m_seed = seed, m_wet = pass == 1 };
                        if (pass == 0)
                        {
                            if (LandAt(cx, footY) != Land.Mistlands) continue;
                            if (!IsLandAt(cx - half, footY) || !IsLandAt(cx + half, footY)) continue;
                            if (!IsLandColumn(cx, footY, height * Mathf.Max(anchor, SpireLandBase)) || m_landDistance[PixelAt(cx, footY)] < 4f) continue;
                        }
                        else if (!WetSpireFoot(spire)) continue;
                        m_glyphs.Add(spire);
                        m_spires.Add(new Vector3(cx, footY, height * 0.35f));
                        return;
                    }
                }
            }
        }

        // Whether a spire may stand in the water at its foot: the Mistlands' sea, at most
        // SpireDepth metres deep there, the foot SpireWater off land and its waterline and the
        // shorter line under it (the glyph's box across its foot) over water, so it reads as rock
        // rising out of the shallows. Sheet only: spires are sheet glyphs replayed by close-ups.
        private bool WetSpireFoot(Glyph g)
        {
            int pixel = PixelAt(g.m_x, g.m_y);
            if (m_land[pixel] != 0 || m_sea[pixel] == 0 || LandAt(g.m_x, g.m_y) != Land.Mistlands) return false;
            if (m_waterDistance[pixel] / DistanceScale < SpireWater) return false;
            float wx = g.m_x * MetresPerPixel - HalfExtent, wz = HalfExtent - g.m_y * MetresPerPixel;
            if (m_world.HeightAt(wx, wz) - m_world.m_waterLevel < -SpireDepth) return false;
            Vector4 box = GlyphBox(g);
            return !IsLandAt(box.x, g.m_y) && !IsLandAt(box.z, g.m_y) && !IsLandAt(g.m_x, box.w)
                && !IsLandAt(box.x, box.w) && !IsLandAt(box.z, box.w);
        }

        // Leaves out every Mistlands crag whose drawn shape overlaps a mountain peak's: where the
        // Mountains meet the Mistlands both stand on the same steep ground, and a crag among the
        // peaks lays its mauve rock, wedge and hatching across their bodies.
        private void DropSpiresOnPeaks()
        {
            var peaks = new List<Vector4>();
            foreach (Glyph g in m_glyphs)
                if (g.m_kind == Kind.Peak || g.m_kind == Kind.Ridge) peaks.Add(GlyphBox(g));
            if (peaks.Count == 0) return;
            m_glyphs.RemoveAll(g =>
            {
                if (g.m_kind != Kind.Spire) return false;
                Vector4 box = GlyphBox(g);
                foreach (Vector4 p in peaks)
                    if (box.x < p.z && p.x < box.z && box.y < p.w && p.y < box.w) return true;
                return false;
            });
        }

        // Whether a filler spire at this foot would crowd a summit's.
        private bool NearSummitSpire(float x, float y, float width)
        {
            foreach (Vector3 spire in m_spires)
            {
                float reach = 0.6f * (spire.z + width) + width;
                float dx = x - spire.x, dy = y - spire.y;
                if (dx * dx + dy * dy < reach * reach) return true;
            }
            return false;
        }

        private readonly List<Vector3> m_spires = new List<Vector3>();

        private static bool IsHighest(float[] h, int size, int x, int z, int radius)
        {
            float centre = h[z * size + x];
            for (int dz = -radius; dz <= radius; dz++)
            {
                int row = (z + dz) * size;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx == 0 && dz == 0) continue;
                    float other = h[row + x + dx];
                    // Ties go to the first pixel in scan order, so a flat top gets one glyph.
                    if (other > centre || (other == centre && (dz < 0 || (dz == 0 && dx < 0)))) return false;
                }
            }
            return true;
        }

        private static float Lowest(float[] h, int size, int x, int z, int radius)
        {
            float lowest = float.MaxValue;
            for (int dz = -radius; dz <= radius; dz++)
            {
                int row = (z + dz) * size;
                for (int dx = -radius; dx <= radius; dx++) lowest = Math.Min(lowest, h[row + x + dx]);
            }
            return lowest;
        }

        private static int PixelAt(float x, float y) =>
            Mathf.Clamp((int)y, 0, CanvasSize - 1) * CanvasSize + Mathf.Clamp((int)x, 0, CanvasSize - 1);

        // Whether a filler peak with this foot, width and height would hide a real summit's peak:
        // standing in front, covering more than its lower SummitShown, or behind, rising above
        // its apex.
        private bool NearSummit(float x, float y, float width, float height)
        {
            foreach (Vector4 summit in m_summits)
            {
                if (Mathf.Abs(x - summit.x) >= summit.z * SummitCore) continue;
                float apex = y - height;
                // Only glyphs whose heights overlap can hide one another.
                if (apex >= summit.y || y <= summit.y - summit.w) continue;
                if (y >= summit.y ? apex < summit.y - summit.w * SummitShown : apex < summit.y - summit.w) return true;
            }
            return false;
        }

        // The peaks' and ridges' feet (x, y), widths (z) and heights (w), filed in PeakCell squares
        // of the sheet, which the Mountains' trees keep clear of or stand on: on the sheet the
        // ones placed before the trees, in a close-up the sheet's from the record.
        private readonly Dictionary<int, List<Vector4>> m_peakBases = new Dictionary<int, List<Vector4>>();

        private void AddPeakBase(Vector4 peak)
        {
            int key = PeakCellKey((int)Math.Floor(peak.x / PeakCell), (int)Math.Floor(peak.y / PeakCell));
            if (!m_peakBases.TryGetValue(key, out List<Vector4> cell)) m_peakBases[key] = cell = new List<Vector4>(8);
            cell.Add(peak);
        }

        private static int PeakCellKey(int cx, int cy) => (cy + 64) * 4096 + cx + 64;

        // Whether a Mountain tree of the given height standing at (x, y) may stand there among the
        // peaks and ridges: woods climb a mountain's lower slopes (the user's call), so a tree
        // standing in front of a peak's or ridge's lower half, or below its foot, is kept as long
        // as its whole crown stays in that lower half (SlopeTop of the glyph's height over its
        // foot), across the base PeakBaseWidth of its width either side plus the crown's half
        // width; drawn after the glyph (SlopeOrder), it stands on the slope. A tree whose foot is
        // in the upper half is hidden behind the glyph. The upper half stays clear, so the
        // mountain still reads. The test reads only the glyphs' positions, the same on the sheet
        // and in every close-up, though trees there are smaller and so climb higher.
        private bool ClearOfPeakBases(float x, float y, float height)
        {
            float crown = PeakCrownWidth * height;
            int cx = (int)Math.Floor(x / PeakCell), cy = (int)Math.Floor(y / PeakCell);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!m_peakBases.TryGetValue(PeakCellKey(cx + dx, cy + dy), out List<Vector4> cell)) continue;
                    foreach (Vector4 peak in cell)
                    {
                        float line = peak.y - peak.w * SlopeTop;
                        if (y < line || y - height >= peak.y + PeakBaseGap) continue;
                        if (y - height >= line) continue;
                        if (Math.Abs(x - peak.x) < PeakBaseWidth * peak.z + crown) return false;
                    }
                }
            }
            return true;
        }

        // A tree standing in front of a peak's or ridge's lower half, inside its body, is drawn
        // just after the glyph rather than by its own foot, so it stands on the slope instead of
        // being scraped away behind it; among such trees the one further south still comes later.
        private void SlopeOrder(ref Glyph tree)
        {
            int cx = (int)Math.Floor(tree.m_x / PeakCell), cy = (int)Math.Floor(tree.m_y / PeakCell);
            float order = float.MinValue;
            for (int dy = 0; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!m_peakBases.TryGetValue(PeakCellKey(cx + dx, cy + dy), out List<Vector4> cell)) continue;
                    foreach (Vector4 peak in cell)
                    {
                        float rise = peak.y - tree.m_y;
                        if (rise <= 0f || rise > peak.w * SlopeTop) continue;
                        if (Math.Abs(tree.m_x - peak.x) >= 0.5f * peak.z * (1f - rise / peak.w)) continue;
                        order = Math.Max(order, peak.y + SlopeOrderGap + (peak.w - rise) * SlopeOrderStep);
                    }
                }
            }
            if (order == float.MinValue) return;
            tree.m_onSlope = true;
            tree.m_order = order;
        }

        // Woods, one tree family per biome, only inside the woods, so every wood is one clearly
        // bounded mass. Crowns are packed so tightly that only the southern edge shows trunks, the
        // rest hidden behind the row in front. Sizes are in the level of detail's units, so a
        // detail tile packs more, smaller trees.
        private void PlaceTrees()
        {
            int first = m_glyphs.Count;
            // Meadows: egg-shaped broadleaf crowns (and a small-tree grove's), off steep ground.
            ScatterTrees(5.2f * m_t, 23, 0.95f, Kind.Broadleaf, s => InWood(s, Land.Meadows, 8f * m_t) && !SteepGround(s));
            int copses = m_glyphs.Count;
            PlaceCopses();
            // The open Meadows' lone oaks, clear of the copses' trees.
            PlaceOaks(copses, m_glyphs.Count);
            // Black Forest, the Deep North and the Mountains: dense pines. Vanilla's Deep North
            // woods are snowy firs and pines and its Mountain woods firs, the same dark conifer
            // mass on the map. Mountain firs keep off the peaks' bases (ClearOfPeakBases), the
            // Black Forest's off steep ground.
            float pine = 11f * m_t;
            int conifers = m_glyphs.Count;
            ScatterTrees(5.5f * m_t, 24, 0.95f, Kind.Conifer, s => (InWood(s, Land.BlackForest, pine) && !SteepGround(s))
                || InWood(s, Land.DeepNorth, pine)
                || (InWood(s, Land.Mountain, pine) && ClearOfPeakBases(s.m_x, s.m_y, pine)));
            for (int i = conifers; i < m_glyphs.Count; i++)
            {
                Glyph tree = m_glyphs[i];
                if (tree.m_kind != Kind.Conifer) continue;
                SlopeOrder(ref tree);
                m_glyphs[i] = tree;
            }
            // A boulder field keeps only a few lone big pines (vanilla's firtree_blackforest2,
            // 1-2 a zone), standing on its open ground clear of the hills on its hilltops.
            if (m_world.HasGrove(Grove.Rocky))
            {
                Scatter(LonePineStep * m_t, LonePineSalt, s =>
                {
                    if (s.m_grove != Grove.Rocky || !(s.m_roll < LonePineOdds) || !IsOpenLand(s, Land.BlackForest, 3f) || SteepGround(s)) return;
                    if (OnRiver(s.m_x, s.m_y) || OnRiver(s.m_x, s.m_y - pine * 0.6f) || !IsLandColumn(s.m_x, s.m_y, pine)) return;
                    if (!ClearOfPlaced(new Glyph { m_x = s.m_x, m_y = s.m_y, m_size = m_t, m_kind = Kind.Conifer, m_seed = s.m_seed })) return;
                    Add(s, Kind.Conifer, m_t);
                });
            }
            // Plains: copses of small round trees where vanilla grows its sparse woods.
            ScatterTrees(5f * m_t, 25, 0.9f, Kind.PlainsTree, s => InWood(s, Land.Plains, 6f * m_t));
            // Mistlands: gnarled trees packed closely enough that their canopies mass into one dark
            // wood, as the Black Forest's pines do, thinner in the mist area, and a sparse scatter
            // over the steep ground among the scarps and spires.
            float giant = 13f * m_t;
            ScatterTrees(7f * m_t, 29, GiantOdds, Kind.GiantTree, s => (!s.m_mist || s.m_roll < GiantMistOdds)
                && SheetSlope(s) < SpireSlope && InWood(s, Land.Mistlands, giant));
            ScatterTrees(GiantSteepStep * m_t, GiantSteepSalt, GiantSteepOdds, Kind.GiantTree,
                s => SheetSlope(s) >= SpireSlope && InWood(s, Land.Mistlands, giant));
            HideCoveredCrowns(first);
            // A close-up's trees; the sheet limits its trees once the relief is placed (PlaceGlyphs).
            if (m_grid != null)
            {
                LimitMarkCover(first);
                DropHiddenBehindMarks(first);
            }
        }

        // The Meadows' copses too small for the wood field (Woods.Build, WorldSource.m_copses): a
        // broadleaf on each of their map pixels kept, so the clumps of trees the game grows there
        // are not lost. Placed from the world's map pixels, not the tile's ground, so the sheet and
        // every close-up draw the same trees, each at its level's size, the middle between its
        // foot and its crown's centre on the pixel (CopseLift) with a hashed jitter inside it
        // (CopseJitter), so it is true to the 12 m pixel. Kept off the shore, rivers, steep ground
        // (as the Meadows' woods) and the hills and peaks (ClearOfPlaced); the pins limit them as
        // every tree afterwards (LimitMarkCover).
        private void PlaceCopses()
        {
            int[] copses = m_world.m_copses;
            if (copses == null || copses.Length == 0) return;
            float reach = m_grid != null ? GlyphReach / m_lod : 0f;
            float left = Math.Min(m_left, m_scatter.xMin) - reach, right = Math.Max(m_right, m_scatter.xMax) + reach;
            float top = Math.Min(m_top, m_scatter.yMin) - reach, bottom = Math.Max(m_bottom, m_scatter.yMax) + reach;
            int size = m_world.m_size, half = size / 2;
            float ps = m_world.m_pixelSize, height = 8f * m_t;
            int salt = unchecked(m_world.m_seed + CopseSalt * 7919);
            foreach (int i in copses)
            {
                int px = i % size, pz = i / size;
                int seed = HashInt(px, pz, salt);
                float wx = (px - half + 0.5f + (Unit(seed, 1) - 0.5f) * CopseJitter) * ps;
                float wz = (pz - half + 0.5f + (Unit(seed, 2) - 0.5f) * CopseJitter) * ps;
                float x = (wx + HalfExtent) / MetresPerPixel, y = (HalfExtent - wz) / MetresPerPixel + CopseLift * height;
                if (x < left || x > right || y < top || y > bottom) continue;
                Spot s = Look(x, y);
                s.m_seed = seed;
                if (!s.m_isLand || s.m_shore < 3f || SteepGround(s)) continue;
                if (OnRiver(x, y) || OnRiver(x, y - height * 0.6f) || !IsLandColumn(x, y, height)) continue;
                // A small-tree grove's copse is its small trees, as its woods are mostly.
                Kind kind = s.m_grove == Grove.Smalltree ? Kind.SmallTree : Kind.Broadleaf;
                if (!ClearOfPlaced(new Glyph { m_x = x, m_y = y, m_size = m_t, m_kind = kind, m_seed = seed })) continue;
                Add(s, kind, m_t);
            }
        }

        // One biome's trees: a candidate on each cell of a spacing grid, kept with odds, where
        // inWood holds. The alt biomes change only how many stand in the same wood shapes: a
        // clear-cut or a boulder field has none, a thin, grazed, sparse or light grove keeps its share of them
        // (KeepOf) by a hash of their own, a small-tree grove (only ever on Meadows ground) keeps
        // SmalltreeBig of its broadleaves and draws SmalltreeKeep of the other spots as small
        // trees (Kind.SmallTree), and a
        // dense grove, which the rule's own grid cannot pack fuller (it is already nearly full),
        // takes every cell of a finer grid of its own (DenseSpacing, salt DenseSalt on), its own
        // grid leaving those spots out so the two do not overlap. Decided from hashes and the
        // map pixel's grove alone, so the sheet and every close-up agree.
        private void ScatterTrees(float spacing, int salt, float odds, Kind kind, Func<Spot, bool> inWood)
        {
            Scatter(spacing, salt, s =>
            {
                if (!(s.m_roll < odds) || WorldSource.IsOpen(s.m_grove) || s.m_grove == Grove.Dense) return;
                if (!(Unit(s.m_seed, SparseRoll) < KeepOf(s.m_grove))) return;
                if (!inWood(s)) return;
                if (s.m_grove == Grove.Smalltree && kind == Kind.Broadleaf)
                {
                    if (Unit(s.m_seed, SmalltreeRoll) < SmalltreeBig) Add(s, kind, m_t);
                    else if (Unit(s.m_seed, SmalltreeKeepRoll) < SmalltreeKeep) Add(s, Kind.SmallTree, m_t);
                    return;
                }
                Add(s, kind, m_t);
            });
            if (!m_world.HasGrove(Grove.Dense)) return;
            Scatter(spacing * DenseSpacing, salt + DenseSalt, s =>
            {
                if (s.m_grove != Grove.Dense || !inWood(s)) return;
                Add(s, kind, m_t);
            });
        }

        // The share of a wood's trees a grove keeps.
        private static float KeepOf(Grove grove)
        {
            switch (grove)
            {
                case Grove.Thin: return ThinKeep;
                case Grove.Grazed: return LoxKeep;
                case Grove.Sparse: return SparseKeep;
                case Grove.Light: return LightKeep;
                default: return 1f;
            }
        }

        // Whether a glyph has a round crown (Glyphs: BroadleafCrown, PlainsCrown, GiantCrown).
        private static bool RoundCrown(Glyph g, out Crown crown)
        {
            switch (g.m_kind)
            {
                case Kind.Broadleaf: crown = BroadleafCrown(g.m_x, g.m_y, g.m_size, g.m_seed); return true;
                case Kind.PlainsTree: crown = PlainsCrown(g.m_x, g.m_y, g.m_size, g.m_seed); return true;
                case Kind.GiantTree: crown = GiantCrown(g.m_x, g.m_y, g.m_size, g.m_seed); return true;
                case Kind.SmallTree: crown = SmallCrown(g.m_x, g.m_y, g.m_size, g.m_seed); return true;
                case Kind.Oak: crown = OakCrown(g.m_x, g.m_y, g.m_size, g.m_seed); return true;
                default: crown = default(Crown); return false;
            }
        }

        // Leaves out a round crown that the crowns in front of it would all but hide. Trees are
        // drawn north to south, each scraping its crown's ellipse back to paper, so a crown standing
        // just behind a bigger one kept only a thin crescent hugging the front outline, or a few
        // stray scraps of its loop: in the Meadows' dense woods that read as doubled lines and
        // broken outlines rather than trees stacked behind each other. A crown is kept when an
        // unbroken run of at least CrownShown of its outline stands clear of every crown in front
        // by CrownSliver pens; one that shows less was almost entirely hidden anyway, so the wood
        // keeps its density. Every candidate in front counts, dropped or not, so the decision
        // rests on hashed neighbours alone and a tile decides as the sheet does (a tile scatters
        // GlyphReach round itself, more than a visible crown and its covers reach).
        private void HideCoveredCrowns(int first)
        {
            int count = m_glyphs.Count - first;
            if (count <= 1) return;
            var crowns = new Crown[count];
            var round = new bool[count];
            float cell = CrownCell * m_t;
            var heads = new Dictionary<long, int>();
            var next = new int[count];
            for (int i = 0; i < count; i++)
            {
                next[i] = -1;
                round[i] = RoundCrown(m_glyphs[first + i], out crowns[i]);
                if (!round[i]) continue;
                long key = CellKey(crowns[i].m_cx, crowns[i].m_cy, cell);
                if (heads.TryGetValue(key, out int head)) next[i] = head;
                heads[key] = i;
            }

            float sliver = CrownSliver * m_pen;
            var clear = new bool[CrownSamples];
            var keep = new bool[count];
            for (int i = 0; i < count; i++)
            {
                keep[i] = true;
                if (!round[i]) continue;
                Crown a = crowns[i];
                float footA = m_glyphs[first + i].m_y;
                // Cells whose crowns' scraped ellipses can reach this one's outline.
                float reachX = a.m_rx * 1.15f + CrownCell * m_t, reachY = a.m_ry * 1.15f + CrownCell * m_t;
                int cx0 = Mathf.FloorToInt((a.m_cx - reachX) / cell), cx1 = Mathf.FloorToInt((a.m_cx + reachX) / cell);
                int cy0 = Mathf.FloorToInt((a.m_cy - reachY) / cell), cy1 = Mathf.FloorToInt((a.m_cy + reachY) / cell);
                float start = Unit(a.m_seed, 40) * Mathf.PI * 2f;
                for (int k = 0; k < CrownSamples; k++) clear[k] = true;
                for (int gy = cy0; gy <= cy1; gy++)
                {
                    for (int gx = cx0; gx <= cx1; gx++)
                    {
                        if (!heads.TryGetValue(((long)gy << 32) ^ (uint)gx, out int j)) continue;
                        for (; j >= 0; j = next[j])
                        {
                            // Only crowns drawn later (further south) cover this one.
                            if (j == i || m_glyphs[first + j].m_y <= footA) continue;
                            Crown b = crowns[j];
                            float ex = CrownEraseX(b) + sliver, ey = CrownEraseY(b) + sliver;
                            for (int k = 0; k < CrownSamples; k++)
                            {
                                if (!clear[k]) continue;
                                float angle = start + Mathf.PI * 2f * k / CrownSamples;
                                float bump = CrownBump(a.m_seed, angle, a.m_wobble);
                                float dx = (a.m_cx + Mathf.Cos(angle) * a.m_rx * bump - b.m_cx) / ex;
                                float dy = (a.m_cy + Mathf.Sin(angle) * a.m_ry * bump - b.m_cy) / ey;
                                if (dx * dx + dy * dy < 1f) clear[k] = false;
                            }
                        }
                    }
                }
                keep[i] = LongestRun(clear) >= CrownShown * CrownSamples;
            }

            int write = first;
            for (int i = 0; i < count; i++)
                if (keep[i]) m_glyphs[write++] = m_glyphs[first + i];
            m_glyphs.RemoveRange(write, m_glyphs.Count - write);
        }

        private static long CellKey(float x, float y, float cell) =>
            ((long)Mathf.FloorToInt(y / cell) << 32) ^ (uint)Mathf.FloorToInt(x / cell);

        // The longest unbroken run of true round a closed loop of flags.
        private static int LongestRun(bool[] flags)
        {
            int n = flags.Length, best = 0, run = 0;
            for (int k = 0; k < 2 * n; k++)
            {
                if (flags[k % n]) best = Math.Max(best, Math.Min(n, ++run));
                else run = 0;
            }
            return best;
        }

        // Swamp: its ancient dead trees among the marsh lines, standing clear of water on the
        // ground the game's ancient trees grow on, from its heights before the pools are raised
        // (WorldSource.m_swampGround). The sheet alone places them; close-ups replay its groups.
        // The grid visits row by row, so each tree is tested against those north and west of it
        // already placed, the same ones on every run.
        private void PlaceDeadTrees()
        {
            var feet = new List<Vector2>();
            float gapX = DeadTreeGapX * T, gapY = DeadTreeGapY * T;
            Scatter(DeadTreeStep * T, DeadTreeSalt, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 4f) return;
                if (!(s.m_roll < DeadTreeOdds * KeepOf(s.m_grove))) return;
                if (!m_world.m_swampGround[m_world.Index(s.m_worldX, s.m_worldZ)]) return;
                if (!IsLandColumn(s.m_x, s.m_y, 14f * T)) return;
                foreach (Vector2 foot in feet)
                {
                    float dx = (foot.x - s.m_x) / gapX, dy = (foot.y - s.m_y) / gapY;
                    if (dx * dx + dy * dy < 1f) return;
                }
                int count = m_glyphs.Count;
                Add(s, Kind.DeadTree, T);
                if (m_glyphs.Count > count) feet.Add(new Vector2(s.m_x, s.m_y));
            });
        }

        // Every glyph but the pins, placed without reading them (Startwork): the pins take out or
        // limit what they must afterwards (PlaceGlyphs).
        private void PlacePinFree()
        {
            PlaceSummitsAndHills();
            PlaceMassifs();
            foreach (Glyph glyph in m_glyphs)
            {
                if (glyph.m_kind != Kind.Peak && glyph.m_kind != Kind.Ridge) continue;
                float height = glyph.m_kind == Kind.Peak ? PeakHeightOf(glyph.m_size, glyph.m_seed) : RidgeHeightOf(glyph.m_size, glyph.m_seed);
                AddPeakBase(new Vector4(glyph.m_x, glyph.m_y, glyph.m_size, height));
                m_record?.AddPeak(glyph.m_x, glyph.m_y, glyph.m_size, height);
            }
            PlaceTrees();
            PlaceMist();
            PlaceDeadTrees();
            // Mistlands: crags on the real summits (PlaceSummitSpire) and filler crags on the steep,
            // broken ground only, clear of the summits; high flat plateaus are not crags. Some
            // stand in the shallow sea off its coasts, as the game's rock spires do (WetSpireFoot).
            Scatter(18f * G, 28, s =>
            {
                if (!s.m_isLand)
                {
                    if (!(s.m_roll < SpireWaterOdds) || NearSummitSpire(s.m_x, s.m_y, 7f * G)) return;
                    var wet = new Glyph { m_x = s.m_x, m_y = s.m_y, m_size = G, m_kind = Kind.Spire, m_seed = s.m_seed, m_wet = true };
                    if (WetSpireFoot(wet) && ClearOfMarks(wet)) m_glyphs.Add(wet);
                    return;
                }
                if (s.m_land != Land.Mistlands || s.m_shore < 5f || !(s.m_roll < 0.35f)) return;
                if (s.m_slope < SpireSlope * 1.3f || NearSummitSpire(s.m_x, s.m_y, 7f * G)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 22f * G)) return;
                Add(s, Kind.Spire, G);
            });
            DropSpiresOnPeaks();
            // The Ashlands' land carries no rock glyphs (the user's call): the game's rock there is
            // vegetation placed at random per zone, mostly on the shoreline, so crags on land could
            // only be a representative scatter, and players would take them for landmarks to steer
            // by. Its rock stands only in the sea, as the thorns below, where it reads as decoration.
            // Sea thorns in the Ashlands' shallows: the game stands cliff_ashlands8 in groups in
            // water down to 25 m deep and cliff_ashlands5 and 6 along the shore, the rock the
            // Ashlands' coast bristles with. Representative, as the trees are: the game places
            // them per zone at random as it loads, so their real positions are unknown. Kept
            // ThornWater from land so the waterline stays on water, and inside the frame.
            float thornRing = EdgeRadius - 80f;
            Scatter(ThornStep * G, 37, s =>
            {
                if (s.m_isLand || m_sea[s.m_pixel] == 0 || s.m_land != Land.Ashlands || !(s.m_roll < ThornOdds)) return;
                if (s.m_above > -0.5f || s.m_above < -ThornDepth || s.m_water < ThornWater) return;
                if (s.m_worldX * s.m_worldX + s.m_worldZ * s.m_worldZ > thornRing * thornRing) return;
                var thorn = new Glyph { m_x = s.m_x, m_y = s.m_y, m_size = G, m_kind = Kind.Thorn, m_seed = s.m_seed };
                m_glyphs.Add(thorn);
                m_thorns.Add(thorn);
            });
            // Sea: ice floes on the Deep North's own water (WorldSource.m_iceWater: its shallow
            // coast and the north side of the border moat), where the game sets its ice; the open
            // northern sea is Ocean and has none. A floe keeps FloeShoreGap clear of land beyond
            // its own size, so it never hides the coast line. The open sea is otherwise left blank
            // but for a few illustrations (DrawIllustrations); wave marks far out read as birds.
            Scatter(FloeStep * G, 33, s =>
            {
                if (m_sea[s.m_pixel] == 0 || !(s.m_roll < FloeOdds)) return;
                if (s.m_water < FloeRadius(G, s.m_seed) + FloeShoreGap || !m_world.IsIceWater(s.m_worldX, s.m_worldZ)) return;
                m_floes.Add(new Glyph { m_x = s.m_x, m_y = s.m_y, m_size = G, m_kind = Kind.IceFloe, m_seed = s.m_seed });
            });
        }

        // The pins among the rest: glyphs that would hide one give way (ClearOfMarks); the relief
        // in front of each takes its share of the cover first (OrderMarks), the dead trees and
        // then the trees the rest (LimitMarkCover). Close-ups take the order and the cover of the
        // relief and dead trees from the record.
        private void PlaceGlyphs()
        {
            if (m_marks.Count > 0)
            {
                m_glyphs.RemoveAll(g => !ClearOfMarks(g));
                OrderMarks();
                LimitMarkCover(0, deadTrees: true);
                LimitMarkCover(0);
                DropHiddenBehindMarks(0);
                if (m_record != null)
                {
                    m_record.m_pinOrders = new float[m_marks.Count];
                    m_record.m_pinCovers = new bool[m_marks.Count][];
                    for (int i = 0; i < m_marks.Count; i++)
                    {
                        m_record.m_pinOrders[i] = m_marks[i].m_order;
                        m_record.m_pinCovers[i] = m_marks[i].m_cover;
                    }
                }
                m_glyphs.AddRange(m_marks);
            }
        }

        private void DrawCoast()
        {
            int size = m_world.m_size;
            float half = size * 0.5f;
            float water = m_world.m_waterLevel;
            float edgeSqr = EdgeRadius * EdgeRadius;
            DistanceField.Contour(size,
                (x, y) => m_coast[y * size + x] - water,
                0f,
                (x, y) =>
                {
                    float wx = (x + 1f - half) * m_world.m_pixelSize;
                    float wz = (y + 1f - half) * m_world.m_pixelSize;
                    return wx * wx + wz * wz >= edgeSqr;
                },
                (x, y, a, b) =>
                {
                    // One pen for every shore, sea or inland: a lighter one for lakes leaves their
                    // outlines reading as unexplained lines, and the water's blue tells them apart.
                    Vector2 p = SourceToCanvas(a), q = SourceToCanvas(b);
                    m_canvas.Stroke(p.x, p.y, q.x, q.y, CoastWidth);
                });
        }

        private Vector2 SourceToCanvas(Vector2 source)
        {
            float half = m_world.m_size * 0.5f;
            float wx = (source.x - half + 0.5f) * m_world.m_pixelSize;
            float wz = (source.y - half + 0.5f) * m_world.m_pixelSize;
            return new Vector2((wx + HalfExtent) / MetresPerPixel, (HalfExtent - wz) / MetresPerPixel);
        }

        // The Mistlands' mist banks, as glyphs sorted in with the trees and crags by their foot (the
        // bank's lowest line): a tree or crag in front hides the bank behind it, and a bank lies
        // over the crowns behind it. Each bank is a veil of three long wavy
        // lines, the middle one longest, with no parchment band scraped under them: the bands
        // read as heavy padding. Banks lie in the mist area's hollows and thin out with height over
        // the ground round them, as the game's mist fills the hollows and leaves the knolls clear,
        // and keep off steep crag ground. Found on the sheet only; close-ups replay them.
        // The banks are found first (FindMist) and join the glyphs here; the scarps, laid once every
        // glyph is placed, keep off their lines (ClearOfMist).
        private void PlaceMist() => m_glyphs.AddRange(m_mist);

        private void FindMist()
        {
            Scatter(30f * G, 12f * G, 0.5f, 0.5f, 13, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 6f || s.m_slope >= SpireSlope * 1.3f) return;
                if (!s.m_mist) return;
                float rise = m_world.HeightAt(s.m_worldX, s.m_worldZ) - m_world.FloorAt(s.m_worldX, s.m_worldZ, MistReach);
                if (!(s.m_roll < MistOdds * (1f - Mathf.InverseLerp(MistFull, MistClear, rise)))) return;
                // The foot, by which banks sort in with the trees and crags, is the bottom line's
                // level; the bank is drawn as far north of it as its lowest line dips (MistFoot), so
                // no line runs on south of the foot.
                float foot = s.m_y + MistGap;
                m_mist.Add(new Glyph { m_x = s.m_x, m_y = foot, m_size = G, m_kind = Kind.Mist, m_seed = s.m_seed });
                var bank = new Vector3(s.m_x, foot - MistFoot(s.m_seed), MistLength(s.m_seed) * 0.5f);
                AddMistBank(bank);
                m_record?.AddMist(bank.x, bank.y, bank.z);
            });
        }

        private readonly List<Glyph> m_mist = new List<Glyph>();
        // The mist banks' middle lines (centre x, y and half length z), filed in PeakCell squares:
        // on the sheet the banks just found, in a close-up the sheet's from the record.
        private readonly Dictionary<int, List<Vector3>> m_mistBanks = new Dictionary<int, List<Vector3>>();

        private void AddMistBank(Vector3 bank)
        {
            int key = PeakCellKey((int)Math.Floor(bank.x / PeakCell), (int)Math.Floor(bank.y / PeakCell));
            if (!m_mistBanks.TryGetValue(key, out List<Vector3> cell)) m_mistBanks[key] = cell = new List<Vector3>(8);
            cell.Add(bank);
        }

        // Whether a mark of the given half width and height round (x, y) keeps clear of every mist
        // bank's lines (the three lines, their wave and pen): ticks under a veil's lines read as
        // scribble.
        private bool ClearOfMist(float x, float y, float halfWidth, float halfHeight)
        {
            float across = MistGap + 0.6f * G + Pen + halfHeight, radius = halfWidth + Pen;
            int cx = (int)Math.Floor(x / PeakCell), cy = (int)Math.Floor(y / PeakCell);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (!m_mistBanks.TryGetValue(PeakCellKey(cx + dx, cy + dy), out List<Vector3> cell)) continue;
                    foreach (Vector3 bank in cell)
                        if (Math.Abs(x - bank.x) < bank.z + radius && Math.Abs(y - bank.y) < across) return false;
                }
            }
            return true;
        }

        private static float MistLength(int seed) => Mathf.Lerp(26f, 40f, Unit(seed, 5)) * G;

        private static float MistTilt(int seed) => (Unit(seed, 6) - 0.5f) * 0.06f;

        private static float MistPhase(int seed) => Unit(seed, 4) * 6.28f;

        // Where each of a bank's three lines starts and ends along its length: the middle one
        // longest.
        private static void MistSpan(int line, float length, out float start, out float end)
        {
            start = length * (line == 0 ? 0.2f : line == 1 ? 0f : 0.25f);
            end = length * (line == 0 ? 0.8f : line == 1 ? 1f : 0.85f);
        }

        // A line's offset below its own level at t along the bank: the tilt and the wave.
        private static float MistDrop(float t, float length, float tilt, float phase) =>
            t * tilt + Mathf.Sin(t / length * 5f + phase) * 0.6f * G;

        // How far south of a bank's centre its lowest line reaches, pen included; the bank is drawn
        // that far north of its foot. Centred MistGap above the foot, the tilt and wave carry the
        // bottom line up to about 2.7 sheet px south of it, past the feet of glyphs drawn after
        // the bank as standing in front, so it runs on under them and shows below a peak's or
        // crag's base line as a hairline along it. Sorting the bank by that lowest point instead
        // lays it over many more crowns and over the peaks' lower bodies.
        private static float MistFoot(int seed)
        {
            float length = MistLength(seed), tilt = MistTilt(seed), phase = MistPhase(seed);
            float lowest = float.MinValue;
            for (int line = 0; line < 3; line++)
            {
                MistSpan(line, length, out float start, out float end);
                for (float t = start; t <= end; t += MistStep)
                    lowest = Math.Max(lowest, (line - 1) * MistGap + MistDrop(t, length, tilt, phase + 0.6f * line));
            }
            return lowest + Pen * 0.5f;
        }

        // A bank centred on (x, y).
        private void MistBank(float x, float y, int seed)
        {
            float length = MistLength(seed), tilt = MistTilt(seed), phase = MistPhase(seed);
            float left = x - length * 0.5f;
            for (int line = 0; line < 3; line++)
            {
                MistSpan(line, length, out float start, out float end);
                MistLine(left, y + (line - 1) * MistGap, start, end, length, tilt, phase + 0.6f * line, seed, line);
            }
        }

        // One wavy mist line of a bank, from t = start to end of its length, broken wherever it
        // leaves Mistlands land, so it never drifts over the sea or into another biome, and with
        // a break or two, so the line looks penned in strokes, not plotted.
        private void MistLine(float left, float y, float start, float end, float length, float tilt, float phase, int seed, int line)
        {
            m_points.Clear();
            m_pathInside.Clear();
            for (float t = start; t <= end; t += MistStep)
            {
                var point = new Vector2(left + t, y + MistDrop(t, length, tilt, phase));
                m_points.Add(point);
                m_pathInside.Add(IsMistlandsAt(point.x, point.y));
            }
            int count = m_points.Count;
            float breakAt = Unit(seed, 7 + line);
            bool broken = Unit(seed, 10 + line) < 0.6f;
            for (int i = 1; i < count; i++)
            {
                if (!m_pathInside[i] || !m_pathInside[i - 1]) continue;
                float u = (i - 0.5f) / (count - 1);
                if (broken && Mathf.Abs(u - breakAt) < 1.5f / count) continue;
                Vector2 a = m_points[i - 1], b = m_points[i];
                m_canvas.Stroke(a.x, a.y, b.x, b.y, Pen);
            }
        }

        private bool IsMistlandsAt(float x, float y)
        {
            if (!IsLandAt(x, y)) return false;
            float wx = x * MetresPerPixel - HalfExtent, wz = HalfExtent - y * MetresPerPixel;
            return m_world.m_biome[m_world.Index(wx, wz)] == Land.Mistlands;
        }

        // Each ring runs only where the water is wide enough for it (some water at least half as
        // far again from land lies within reach), so a narrow strait keeps one or two rings rather
        // than a wall of lines from both banks; and the rings beyond the first only around land
        // big enough to carry them, so a tiny islet is not ringed like a target.
        // Rivers and streams from the world generator, drawn over the glyphs on a band scraped back
        // to parchment, so they read through woods as on the classic map. Only the stretches over
        // land are drawn: where a channel is wide enough to show as water on the map, the coast
        // already draws both banks. Streams start at their low mouth and taper toward the source.
        // Marks the canvas pixels within radius of a point on a river's course, which trees keep off.
        private void MarkRiver(Vector2 at, float radius)
        {
            if (m_riverMask == null) m_riverMask = new byte[CanvasSize * CanvasSize];
            int x0 = Mathf.Clamp((int)(at.x - radius), 0, CanvasSize - 1), x1 = Mathf.Clamp((int)(at.x + radius), 0, CanvasSize - 1);
            int y0 = Mathf.Clamp((int)(at.y - radius), 0, CanvasSize - 1), y1 = Mathf.Clamp((int)(at.y + radius), 0, CanvasSize - 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x + 0.5f - at.x, dy = y + 0.5f - at.y;
                    if (dx * dx + dy * dy <= radius * radius) m_riverMask[y * CanvasSize + x] = 1;
                }
            }
        }

        private bool OnRiver(float x, float y) =>
            m_grid != null ? OnTileRiver(x, y) : m_riverMask != null && m_riverMask[PixelAt(x, y)] != 0;

        private void DrawRivers()
        {
            float edge = EdgeRadius - 50f;
            float step = RiverStep * MetresPerPixel;
            foreach (RiverPath river in m_world.m_rivers)
            {
                float length = Vector2.Distance(river.m_start, river.m_end);
                if (length <= 0f) continue;
                m_points.Clear();
                m_pathInside.Clear();
                for (float t = 0f; t <= length + step * 0.5f; t += step)
                {
                    Vector2 world = river.PointAt(Math.Min(t, length));
                    var point = new Vector2((world.x + HalfExtent) / MetresPerPixel, (HalfExtent - world.y) / MetresPerPixel);
                    m_points.Add(point);
                    m_pathInside.Add(world.sqrMagnitude < edge * edge && IsLandAt(point.x, point.y)
                        && m_landDistance[PixelAt(point.x, point.y)] >= RiverShoreClear && m_world.HasRiverBed(world));
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 1; i < m_points.Count; i++)
                    {
                        if (!m_pathInside[i] || !m_pathInside[i - 1]) continue;
                        float t = (float)i / (m_points.Count - 1);
                        float width = river.m_stream ? Mathf.Lerp(StreamMouth, Pen, t) : RiverWidth;
                        Vector2 a = m_points[i - 1], b = m_points[i];
                        if (pass == 0)
                        {
                            m_canvas.Scrape(a.x, a.y, b.x, b.y, width + RiverBand);
                            MarkRiver(b, (width + RiverBand) * 0.5f + 1f);
                        }
                        else m_canvas.Stroke(a.x, a.y, b.x, b.y, width);
                    }
                }
            }
        }

        // The waves, after the classic map: small groups of level
        // strokes at the coast's pivotal points, bay heads and headland tips, and now and then on a
        // long straight coast, then one group behind each cluster of islets. The rows are ruled
        // across the sheet, not along the coast, which gives the uneven hand-ruled look. Drawn on
        // the sheet only, in a fixed order, so the result is deterministic; close-ups redraw them.
        // The layout reads no pin and no ink, so the waves of the first paint from a kept start are
        // kept with it and a later paint inks those, the same strokes in the same order.
        private void DrawWaves(List<ContourLines.Line> lines)
        {
            InkRecord.Wave[] kept = m_start?.m_waves;
            if (kept != null)
            {
                foreach (InkRecord.Wave wave in kept) DrawWave(wave);
                return;
            }
            m_laidWaves = m_start != null ? new List<InkRecord.Wave>() : null;
            LayOutWaves(lines);
            if (m_start != null) m_start.m_waves = m_laidWaves.ToArray();
            m_laidWaves = null;
        }

        // The waves this paint laid out, to keep with its start (DrawWaves); null when none is kept.
        private List<InkRecord.Wave> m_laidWaves;

        private void LayOutWaves(List<ContourLines.Line> lines)
        {
            int n = CanvasSize;
            int salt = unchecked(m_world.m_seed + 34 * 7919);
            // Floes are inked over the waves and scrape what lies under them: the waves are laid
            // as if the ice were not there and the floes win (the user's call), as the Deep
            // North's coasts all carry an ice fringe and keeping the waves off it left them bare.
            m_floeSet = new HashSet<int>();
            // The Ashlands' sea thorns and the Mistlands' spires in the water are drawn before the
            // waves; the waves keep off them and their waterlines.
            var rocks = new List<Glyph>(m_thorns);
            foreach (Glyph g in m_glyphs)
                if (g.m_kind == Kind.Spire && g.m_wet) rocks.Add(g);
            foreach (Glyph thorn in rocks)
            {
                Vector4 box = GlyphBox(thorn);
                float half = (box.w - box.y) * 0.5f;
                Footprint(m_floeSet, n, new Vector2(box.x + half * 0.5f, (box.y + box.w) * 0.5f), new Vector2(box.z - half * 0.5f, (box.y + box.w) * 0.5f), half + 1.5f, true);
            }
            m_waveSpans = new Dictionary<int, List<Vector2>>();
            List<Accent> accents = FindAccents(lines, salt);
            m_cancel.ThrowIfCancellationRequested();
            // Only accents that drew a group keep the islets' groups away: one with no room left
            // a bare islet beside a bare coast.
            var drawn = new List<Accent>();
            foreach (Accent accent in accents)
                if (DrawGroup(accent, salt)) drawn.Add(accent);
            DrawLakeLines(lines, salt);
            // Islets last, kept off the coast's groups, so those stay as they were.
            DrawIsletWaves(salt, drawn);
        }

        // The islets' waves. Islets outside the Mistlands are clustered (ClusterIslets) and each cluster gets one
        // group round its centroid. Larger clusters first, each IsletSpacing from the coast's
        // groups; strokes claim their rows, so later groups keep off earlier ones.
        private void DrawIsletWaves(int salt, List<Accent> coast)
        {
            m_hand = s_isletHand;
            var grid = new Dictionary<long, List<Vector2>>();
            foreach (Accent accent in coast) AddToGrid(grid, accent.m_at);
            float centre = CanvasSize * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction;
            foreach (IsletCluster cluster in ClusterIslets())
            {
                if (Near(grid, cluster.m_at, IsletSpacing)) continue;
                float dx = cluster.m_at.x - centre, dy = cluster.m_at.y - centre;
                if (dx * dx + dy * dy > limit * limit) continue;
                BeginGroup();
                PlaceClusterGroup(cluster, salt);
                EndGroup(salt);
            }
            m_hand = s_seaHand;
        }

        // A cluster of islets: their cells' centroid, the direction its strokes run (its long
        // axis, or level) and its extent along that direction (u) and across it (v) from the
        // centroid, and its size in map cells.
        private struct IsletCluster
        {
            public Vector2 m_at, m_axis;
            public float m_alongMin, m_alongMax, m_acrossMin, m_acrossMax;
            public int m_cells, m_order;
        }

        // The islets joined into clusters, single linkage: two islets whose bounds come within
        // IsletLink of each other are in one cluster. Clusters with no islet of WavedIsletCells
        // are dropped, so specks alone get no waves. Largest first, then in the map's order. The
        // centroid weighs each islet by its cells, so a speck at a cluster's edge does not pull
        // the group off the islets that carry it; the axis is the cells' principal axis.
        private List<IsletCluster> ClusterIslets()
        {
            int count = m_islets.Count;
            // Islets within IsletCoastGap of the coast lie in its waves: they are left out, so
            // they neither get a group nor join one.
            var near = new bool[count];
            for (int i = 0; i < count; i++) near[i] = NearCoast(i);
            var parent = new int[count];
            for (int i = 0; i < count; i++) parent[i] = i;
            var cells = new Dictionary<long, List<int>>();
            for (int i = 0; i < count; i++)
            {
                if (near[i]) continue;
                Islet a = m_islets[i];
                int x0 = Mathf.FloorToInt((a.m_left - IsletLink) / AccentCell), x1 = Mathf.FloorToInt((a.m_right + IsletLink) / AccentCell);
                int y0 = Mathf.FloorToInt((a.m_top - IsletLink) / AccentCell), y1 = Mathf.FloorToInt((a.m_bottom + IsletLink) / AccentCell);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        long key = AccentKey(x, y);
                        if (!cells.TryGetValue(key, out List<int> cell)) cells[key] = cell = new List<int>();
                        foreach (int j in cell)
                            if (BoundsGap(a, m_islets[j]) <= IsletLink) Union(i, j);
                        cell.Add(i);
                    }
            }
            var byRoot = new Dictionary<int, int>();
            var linked = new List<List<int>>();
            for (int i = 0; i < count; i++)
            {
                if (near[i]) continue;
                int root = Find(i);
                if (!byRoot.TryGetValue(root, out int c))
                {
                    byRoot[root] = c = linked.Count;
                    linked.Add(new List<int>());
                }
                linked[c].Add(i);
            }
            var members = new List<List<int>>();
            foreach (List<int> group in linked) SplitCluster(group, IsletLink, members);
            // Per cluster: cells, the weighted sums of x, y, xÂ², yÂ², xy (each islet's own spread
            // as a uniform box) and whether an islet of WavedIsletCells is in it.
            var result = new List<IsletCluster>();
            for (int c = 0; c < members.Count; c++)
            {
                double w = 0, sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0;
                bool anchored = false;
                foreach (int i in members[c])
                {
                    Islet a = m_islets[i];
                    float bw = a.m_right - a.m_left, bh = a.m_bottom - a.m_top;
                    w += a.m_cells;
                    sx += a.m_cells * a.m_at.x;
                    sy += a.m_cells * a.m_at.y;
                    sxx += a.m_cells * (a.m_at.x * (double)a.m_at.x + bw * bw / 12.0);
                    syy += a.m_cells * (a.m_at.y * (double)a.m_at.y + bh * bh / 12.0);
                    sxy += a.m_cells * (a.m_at.x * (double)a.m_at.y);
                    if (a.m_cells >= WavedIsletCells) anchored = true;
                }
                if (!anchored) continue;
                double mx = sx / w, my = sy / w;
                double cxx = sxx / w - mx * mx, cyy = syy / w - my * my, cxy = sxy / w - mx * my;
                Vector2 axis = Vector2.right;
                double spread = Math.Sqrt((cxx - cyy) * (cxx - cyy) + 4 * cxy * cxy);
                double major = (cxx + cyy + spread) * 0.5, minor = (cxx + cyy - spread) * 0.5;
                if (major > IsletElongation * IsletElongation * Math.Max(minor, 1e-3))
                {
                    // The chain's direction; strokes stay level and only their rows are staggered
                    // along it (ClusterRow).
                    float angle = 0.5f * (float)Math.Atan2(2 * cxy, cxx - cyy);
                    axis = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                }
                var at = new Vector2((float)mx, (float)my);
                // Bounds in the level frame the strokes are drawn in: along x, across up.
                Vector2 level = Vector2.right, across = new Vector2(0f, -1f);
                var cluster = new IsletCluster
                {
                    m_at = at, m_axis = axis, m_cells = (int)w, m_order = result.Count,
                    m_alongMin = float.MaxValue, m_alongMax = float.MinValue, m_acrossMin = float.MaxValue, m_acrossMax = float.MinValue,
                };
                foreach (int i in members[c])
                {
                    Islet a = m_islets[i];
                    for (int corner = 0; corner < 4; corner++)
                    {
                        Vector2 d = new Vector2((corner & 1) == 0 ? a.m_left : a.m_right, (corner & 2) == 0 ? a.m_top : a.m_bottom) - at;
                        float u = Vector2.Dot(d, level), v = Vector2.Dot(d, across);
                        cluster.m_alongMin = Math.Min(cluster.m_alongMin, u);
                        cluster.m_alongMax = Math.Max(cluster.m_alongMax, u);
                        cluster.m_acrossMin = Math.Min(cluster.m_acrossMin, v);
                        cluster.m_acrossMax = Math.Max(cluster.m_acrossMax, v);
                    }
                }
                result.Add(cluster);
            }
            result.Sort((a, b) => a.m_cells != b.m_cells ? b.m_cells.CompareTo(a.m_cells) : a.m_order.CompareTo(b.m_order));
            return result;

            int Find(int i)
            {
                while (parent[i] != i) i = parent[i] = parent[parent[i]];
                return i;
            }

            void Union(int i, int j)
            {
                int a = Find(i), b = Find(j);
                if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
            }
        }

        // A cluster wider or taller than IsletClusterSpan is split by linking its islets again
        // at half the distance, down to IsletLinkMin: in a field of islets single linkage chains
        // everything into one cluster hundreds of pixels across, and a
        // group sized to that would be a hatched sea, too large to stay behind the islets.
        private void SplitCluster(List<int> group, float link, List<List<int>> into)
        {
            float left = float.MaxValue, right = float.MinValue, top = float.MaxValue, bottom = float.MinValue;
            foreach (int i in group)
            {
                Islet a = m_islets[i];
                left = Math.Min(left, a.m_left);
                right = Math.Max(right, a.m_right);
                top = Math.Min(top, a.m_top);
                bottom = Math.Max(bottom, a.m_bottom);
            }
            float half = link * 0.5f;
            if (group.Count < 2 || Math.Max(right - left, bottom - top) <= IsletClusterSpan || half < IsletLinkMin)
            {
                into.Add(group);
                return;
            }
            var parent = new int[group.Count];
            for (int k = 0; k < parent.Length; k++) parent[k] = k;
            for (int k = 0; k < group.Count; k++)
                for (int j = 0; j < k; j++)
                    if (BoundsGap(m_islets[group[k]], m_islets[group[j]]) <= half)
                    {
                        int a = Find(k), b = Find(j);
                        if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b);
                    }
            var byRoot = new Dictionary<int, List<int>>();
            var order = new List<List<int>>();
            for (int k = 0; k < group.Count; k++)
            {
                int root = Find(k);
                if (!byRoot.TryGetValue(root, out List<int> part))
                {
                    byRoot[root] = part = new List<int>();
                    order.Add(part);
                }
                part.Add(group[k]);
            }
            foreach (List<int> part in order) SplitCluster(part, half, into);

            int Find(int k)
            {
                while (parent[k] != k) k = parent[k] = parent[parent[k]];
                return k;
            }
        }

        // Whether land of MajorLandCells comes within IsletCoastGap of islet i: of the sheet
        // pixels on its own map cells (m_isletCell), since its bounds may take in the edge of a
        // headland beside it.
        private bool NearCoast(int i)
        {
            Islet a = m_islets[i];
            int n = CanvasSize;
            int x0 = Mathf.Max(0, Mathf.FloorToInt(a.m_left)), x1 = Mathf.Min(n - 1, Mathf.CeilToInt(a.m_right));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(a.m_top)), y1 = Mathf.Min(n - 1, Mathf.CeilToInt(a.m_bottom));
            for (int y = y0; y <= y1; y++)
            {
                float wz = HalfExtent - (y + 0.5f) * MetresPerPixel;
                for (int x = x0; x <= x1; x++)
                {
                    float wx = (x + 0.5f) * MetresPerPixel - HalfExtent;
                    if (m_isletCell[m_world.Index(wx, wz)] != i + 1) continue;
                    if (m_majorDistance[y * n + x] / DistanceScale < IsletCoastGap) return true;
                }
            }
            return false;
        }

        // The gap between two islets' bounds, 0 where they overlap.
        private static float BoundsGap(Islet a, Islet b)
        {
            float dx = Math.Max(0f, Math.Max(a.m_left - b.m_right, b.m_left - a.m_right));
            float dy = Math.Max(0f, Math.Max(a.m_top - b.m_bottom, b.m_top - a.m_bottom));
            return Mathf.Sqrt(dx * dx + dy * dy);
        }

        // One group for a cluster, sized to it, so a small islet gets a small group. Its strokes
        // never cross or touch land: a row through the cluster gets one
        // stroke, off its east and west shore in turn, starting WaveShoreGap off it and running
        // out to sea as the coast's do; a row clear of it gets one stroke in open water, centred
        // on the cluster (shifted along a slanting chain, IsletLean) and swayed a crest or so east
        // and west in turn (IsletSway), so the stack reads as drawn by hand round one centre.
        // Rows are long and short in turn. They go out from the centroid's row, below and above
        // in turn, until the cluster's stroke budget is spent; a row that takes no stroke is
        // skipped. Strokes drawn through the islets, with the islets on top, show through the
        // land.
        private void PlaceClusterGroup(IsletCluster cluster, int salt)
        {
            float size = Mathf.Sqrt(cluster.m_cells) * m_world.m_pixelSize / MetresPerPixel;
            int budget = Mathf.Clamp(GroupFloor + Mathf.FloorToInt((size - IsletSizeBase) / IsletSizeStep), GroupFloor, IsletStrokesMax);
            int longCrests = Mathf.Clamp(IsletShortCrests + Mathf.RoundToInt(size / IsletSizeLong), IsletShortCrests + 1, IsletLongCrests);
            int shortCrests = Math.Max(IsletShortCrests, longCrests - 2);
            int groupSeed = HashInt(Mathf.RoundToInt(cluster.m_at.x), Mathf.RoundToInt(cluster.m_at.y), salt + 12);
            int r0 = Mathf.FloorToInt(cluster.m_at.y / WaveRowPitch);
            // Rows from the cluster's top to its bottom, and one more either way.
            int first = Mathf.FloorToInt((cluster.m_at.y - cluster.m_acrossMax) / WaveRowPitch) - 1;
            int last = Mathf.FloorToInt((cluster.m_at.y - cluster.m_acrossMin) / WaveRowPitch) + 1;
            int spent = 0;
            for (int k = 0; spent < budget && (r0 + k <= last || r0 - k >= first); k++)
            {
                for (int side = 0; side < 2 && spent < budget; side++)
                {
                    if (k == 0 && side == 1) continue;
                    int r = side == 0 ? r0 + k : r0 - k;
                    if (r < first || r > last) continue;
                    // Long and short in turn, and the sway's side in turn, by the row's place in
                    // the group; the group's first side by seed.
                    bool longRow = (k & 1) == 0;
                    int sway = ((r - r0) & 1) == (groupSeed & 1) ? 1 : -1;
                    spent += IsletRow(r, cluster, longRow ? longCrests : shortCrests, sway, salt);
                }
            }
        }

        // The stroke on row r of a cluster's group: off the shore where the row crosses the
        // cluster's land, otherwise in open water. sway (1 or -1) moves the row's centre east or
        // west: a crest or so on a clear row, the side the stroke runs out from on a row through
        // the land. Returns how many were laid (0 or 1).
        private int IsletRow(int r, IsletCluster cluster, int crests, int sway, int salt)
        {
            int n = CanvasSize;
            float y = RowY(r, salt);
            int py = (int)y;
            if (py < 2 || py >= n - 2) return 0;
            int seed = HashInt(Mathf.RoundToInt(cluster.m_at.x) * 31 + r, Mathf.RoundToInt(cluster.m_at.y), salt + 11);
            float crest = m_hand.m_unit * Vary(seed, 3, 0.1f);
            int left = Mathf.Max(1, Mathf.FloorToInt(cluster.m_at.x + cluster.m_alongMin) - 1);
            int right = Mathf.Min(n - 2, Mathf.CeilToInt(cluster.m_at.x + cluster.m_alongMax) + 1);
            int west = -1, east = -1;
            for (int x = left; x <= right; x++)
            {
                if (m_land[py * n + x] == 0) continue;
                if (west < 0) west = x;
                east = x;
            }
            if (west < 0)
            {
                // Clear of the land: one stroke, a crest longer than a side's as it spans the
                // cluster, centred on it and shifted along a slanting chain and by the sway.
                Vector2 chain = cluster.m_axis;
                // A chain within IsletLevelChain of level gives no lean: its rows above and below
                // would be sent the full IsletLean along it by a slight tilt either way.
                float lean = Math.Abs(chain.y) >= IsletLevelChain ? Mathf.Clamp(chain.x / chain.y, -IsletLean, IsletLean) : 0f;
                float half = (cluster.m_alongMax - cluster.m_alongMin) * 0.5f;
                float stagger = Mathf.Clamp((y - cluster.m_at.y) * lean, -half, half);
                float centre = cluster.m_at.x + stagger + sway * IsletSway * crest * (0.5f + 0.8f * Unit(seed, 8));
                for (int c = crests + 1; c >= m_hand.m_least; c--)
                {
                    float length = c * crest, x0 = centre - length * 0.5f;
                    // Clear of land by WaveFloatClear, and with IsletChannelGap of open water
                    // beyond either end, as the strokes off a shore.
                    if (!OpenRow(py, x0, length, WaveFloatClear) || !OpenRow(py, x0 - IsletChannelGap, length + 2f * IsletChannelGap, WaveShoreGap)) continue;
                    if (!Claim(r, x0, x0 + length)) return 0;
                    AddStroke(r, x0, x0 + length, y, c, seed, salt, 0);
                    return 1;
                }
                return 0;
            }
            // A row that only grazes a tip of land takes none: a stroke from a point reads as
            // drawn out of it.
            if (east - west + 1 < IsletTipWidth) return 0;
            // Through the land: one stroke, off the sway's side, or the other where that has no
            // room. Strokes off both sides of a row read as one line drawn under the islet.
            for (int s = 0; s < 2; s++)
            {
                int dir = s == 0 ? sway : -sway;
                int c = crests;
                // A spike of land the row runs along (no land a pixel above or below just inside
                // the shore) takes no stroke: it would run straight out of its point.
                int edge = dir > 0 ? east : west;
                int inside = Mathf.Clamp(edge - 2 * dir, 0, n - 1);
                if (m_land[(py - 1) * n + inside] == 0 && m_land[(py + 1) * n + inside] == 0) continue;
                int w = edge + dir;
                while (w > 0 && w < n - 1 && m_land[py * n + w] == 0 && m_waterDistance[py * n + w] / DistanceScale < WaveShoreGap) w += dir;
                if (w <= 0 || w >= n - 1 || m_land[py * n + w] != 0) continue;
                float start = dir > 0 ? w : w + 1;
                float length = Fit(crest, IsletRoom(py, start, dir, (c + 1) * crest), m_hand.m_least, ref c);
                if (length <= 0f) continue;
                float x0 = dir > 0 ? start : start - length;
                if (!Claim(r, x0, x0 + length)) continue;
                AddStroke(r, x0, x0 + length, y, c, seed, salt, dir * IsletAnchor);
                return 1;
            }
            return 0;
        }

        // Whether the stroke from x0, length long, on row py keeps clear from land (all of it,
        // islets and specks too), floes and the frame.
        private bool OpenRow(int py, float x0, float length, float clear)
        {
            int n = CanvasSize;
            float centre = n * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction, dy = py + 0.5f - centre;
            int a = Mathf.FloorToInt(x0), b = Mathf.CeilToInt(x0 + length);
            if (a < 0 || b >= n) return false;
            for (int x = a; x <= b; x++)
            {
                int pixel = py * n + x;
                float dx = x + 0.5f - centre;
                if (m_land[pixel] != 0 || m_waterDistance[pixel] / DistanceScale < clear || m_floeSet.Contains(pixel) || dx * dx + dy * dy > limit * limit) return false;
            }
            return true;
        }

        // Open water ahead of a stroke off an islet's shore, up to most: it ends IsletChannelGap
        // before it comes within WaveShoreGap of any land, on its row or the rows beside it (the
        // distance field is round), so a stroke into a channel between islets stays short or is
        // dropped instead of filling it, and WaveSlotGap short of a floe or the frame.
        private float IsletRoom(int py, float start, int dir, float most)
        {
            int n = CanvasSize, row = py * n;
            float centre = n * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction, dy = py + 0.5f - centre;
            for (float s = 0f; s < most + IsletChannelGap; s += 0.5f)
            {
                int x = Mathf.FloorToInt(start + dir * (s + 0.25f));
                if (x < 0 || x >= n) return Math.Min(most, Math.Max(0f, s - WaveSlotGap));
                int pixel = row + x;
                float dx = x + 0.5f - centre;
                if (m_land[pixel] != 0 || m_waterDistance[pixel] / DistanceScale < WaveShoreGap) return Math.Min(most, Math.Max(0f, s - IsletChannelGap));
                if (m_floeSet.Contains(pixel) || dx * dx + dy * dy > limit * limit) return Math.Min(most, Math.Max(0f, s - WaveSlotGap));
            }
            return most;
        }

        private enum CoastBend : byte
        {
            Straight,
            Headland,
            Bay,
        }

        private struct Accent
        {
            public Vector2 m_at;
            // Out to sea: from a headland's tip, into a bay from its head, off a straight coast.
            public Vector2 m_normal;
            public CoastBend m_kind;
            public int m_seed;
        }

        // One group of waves. On a coast facing east or west every row meets it, so the rows stand
        // round the accent, each stroke starting at the coast and running out to sea. A headland
        // pointing north or south has a stroke or two floating off its tip and the rest starting
        // at its flanks, both sides of a row alike. A bay opening north or south has its strokes
        // across it, from either shore in turn.
        private bool DrawGroup(Accent accent, int salt)
        {
            BeginGroup();
            if (m_hand.m_lake) PlaceLakeGroup(accent, salt);
            else PlaceGroup(accent, salt);
            return EndGroup(salt);
        }

        // A lake's group: lines on consecutive rows from one shore, the row nearest the accent
        // first and then out either way, each line starting within LakeDrift of the one beside
        // it, so the group stands as one stack (lines from either shore in turn and rows with
        // gaps read as stray marks). The stack is tried off the shore the accent
        // faces from and off the other, from the accent's row and the rows either side, and the
        // one with the most line is drawn.
        private void PlaceLakeGroup(Accent accent, int salt)
        {
            int seed = accent.m_seed;
            int count = Math.Min(m_hand.m_max, m_hand.m_min + (int)(Unit(seed, 1) * (m_hand.m_max - m_hand.m_min + 1)));
            int row = Mathf.FloorToInt(accent.m_at.y / WaveRowPitch);
            int prefer = accent.m_normal.x > 0f || (accent.m_normal.x == 0f && Unit(seed, 2) < 0.5f) ? 1 : -1;
            float best = 0f;
            int bestDir = 0, bestMid = 0;
            for (int side = 0; side < 2; side++)
            {
                int dir = side == 0 ? prefer : -prefer;
                for (int m = 0; m < 3; m++)
                {
                    int mid = row + (m == 0 ? 0 : m == 1 ? 1 : -1);
                    float total = LakeStack(mid, accent.m_at.x, dir, count, salt);
                    bool enough = m_group.Count >= m_hand.m_floor;
                    ReleaseGroup();
                    if (!enough || total <= best) continue;
                    best = total;
                    bestDir = dir;
                    bestMid = mid;
                }
            }
            if (bestDir != 0) LakeStack(bestMid, accent.m_at.x, bestDir, count, salt);
        }

        // Lines on rows from mid out, below and above in turn, until count or both sides meet a
        // row with no line; their total length.
        private float LakeStack(int mid, float ax, int dir, int count, int salt)
        {
            if (!Anchored(mid, ax, LakeFirstReach, dir, true, salt, out float start)) return 0f;
            float total = m_group[m_group.Count - 1].m_x1 - m_group[m_group.Count - 1].m_x0;
            float below = start, above = start;
            bool down = true, up = true;
            for (int k = 1; m_group.Count < count && (down || up); k++)
            {
                bool longRow = (k & 1) == 0;
                if (down && m_group.Count < count)
                {
                    if (Anchored(mid + k, below, LakeDrift, dir, longRow, salt, out start))
                    {
                        below = start;
                        total += m_group[m_group.Count - 1].m_x1 - m_group[m_group.Count - 1].m_x0;
                    }
                    else down = false;
                }
                if (up && m_group.Count < count)
                {
                    if (Anchored(mid - k, above, LakeDrift, dir, longRow, salt, out start))
                    {
                        above = start;
                        total += m_group[m_group.Count - 1].m_x1 - m_group[m_group.Count - 1].m_x0;
                    }
                    else up = false;
                }
            }
            return total;
        }

        // A group's strokes wait in m_group until it is placed whole, so one that cannot have the
        // hand's m_floor strokes is left out instead of drawn as a stub or two (never fewer than
        // 3 a group on lakes and round islets; the sea's floor is 0).
        private void BeginGroup() => m_group.Clear();

        // Draws the waiting strokes, or gives their rows back if there are too few; true if drawn.
        private bool EndGroup(int salt)
        {
            bool drawn = m_group.Count > 0 && m_group.Count >= m_hand.m_floor;
            if (!drawn)
            {
                ReleaseGroup();
                return false;
            }
            foreach (GroupStroke s in m_group) DrawWave(s.m_x0, s.m_y, s.m_x1 - s.m_x0, s.m_crests, m_hand.m_lift, s.m_seed, salt, s.m_anchor);
            m_group.Clear();
            return true;
        }

        // Gives the waiting strokes' rows back without drawing them.
        private void ReleaseGroup()
        {
            foreach (GroupStroke s in m_group)
                if (s.m_row != int.MinValue) m_waveSpans[s.m_row].Remove(new Vector2(s.m_x0, s.m_x1));
            m_group.Clear();
        }

        private void AddStroke(int r, float x0, float x1, float y, int crests, int seed, int salt, int anchor) =>
            m_group.Add(new GroupStroke { m_row = r, m_x0 = x0, m_x1 = x1, m_y = y, m_crests = crests, m_seed = seed, m_anchor = anchor });

        private struct GroupStroke
        {
            public int m_row, m_crests, m_seed, m_anchor;
            public float m_x0, m_x1, m_y;
        }

        private readonly List<GroupStroke> m_group = new List<GroupStroke>();

        private void PlaceGroup(Accent accent, int salt)
        {
            int seed = accent.m_seed;
            int count = Math.Min(m_hand.m_max, m_hand.m_min + (int)(Unit(seed, 1) * (m_hand.m_max - m_hand.m_min + 1)));
            int row = Mathf.FloorToInt(accent.m_at.y / WaveRowPitch);
            float ax = accent.m_at.x;
            Vector2 normal = accent.m_normal;
            bool northSouth = Math.Abs(normal.y) > Math.Abs(normal.x) * NorthSouth;
            if (!northSouth)
            {
                int dir = normal.x > 0f ? 1 : -1;
                for (int k = 0; k < count; k++) Anchored(row - count / 2 + k, ax, dir, (k & 1) == 0, salt);
                return;
            }
            int toSea = normal.y < 0f ? -1 : 1;
            if (accent.m_kind == CoastBend.Headland)
            {
                int drawn = 0, k = 0;
                int floating = Unit(seed, 2) < 0.5f ? 1 : WaveFloating;
                for (; k < floating; k++)
                    if (Floating(row + toSea * k, ax, (k & 1) == 0, salt)) drawn++;
                // Rows back from the tip cross the headland: a stroke off each flank.
                for (int r = row - toSea; drawn < count && Math.Abs(r - row) <= count; r -= toSea, k++)
                {
                    bool longRow = (k & 1) == 0;
                    if (Anchored(r, ax, -1, longRow, salt)) drawn++;
                    if (drawn < count && Anchored(r, ax, 1, longRow, salt)) drawn++;
                }
                return;
            }
            for (int k = 0; k < count; k++)
            {
                int dir = ((k + seed) & 1) == 0 ? 1 : -1;
                int r = row + toSea * k;
                if (!Anchored(r, ax, dir, (k & 1) == 0, salt)) Anchored(r, ax, -dir, (k & 1) == 0, salt);
            }
        }

        // A stroke on row r from the shore nearest ax with water on its dir side, running out
        // along dir; false when there is no such shore within AccentReach or no room.
        private bool Anchored(int r, float ax, int dir, bool longRow, int salt) => Anchored(r, ax, AccentReach, dir, longRow, salt, out _);

        // The same with the shore looked for within reach of ax; start is where the stroke begins.
        private bool Anchored(int r, float ax, float reach, int dir, bool longRow, int salt, out float start)
        {
            start = 0f;
            float y = RowY(r, salt);
            int py = (int)y;
            if (py < 1 || py >= CanvasSize - 1) return false;
            if (!FindShore(py, ax, reach, dir, out start)) return false;
            int seed = HashInt(r, Mathf.RoundToInt(start) * dir, salt + 7);
            int crests = Crests(seed, longRow);
            float length = Fit(m_hand.m_unit * Vary(seed, 3, 0.1f), Room(py, start, dir), m_hand.m_least, ref crests);
            if (length <= 0f) return false;
            float x0 = dir > 0 ? start : start - length;
            // Into the coast's pen, which the land mask's pixel edge can miss by half a pixel.
            if (m_hand.m_lake)
            {
                length += LakeOverlap;
                if (dir > 0) x0 -= LakeOverlap;
            }
            if (!Claim(r, x0, x0 + length)) return false;
            AddStroke(r, x0, x0 + length, y, crests, seed, salt, dir);
            return true;
        }

        // A stroke on row r centred on ax in open water, kept WaveFloatClear from land on its row
        // and the rows beside it.
        private bool Floating(int r, float ax, bool longRow, int salt)
        {
            int n = CanvasSize;
            float y = RowY(r, salt);
            int py = (int)y;
            if (py < 2 || py >= n - 2) return false;
            int seed = HashInt(r, Mathf.RoundToInt(ax), salt + 8);
            int crests = Crests(seed, longRow);
            float length = crests * m_hand.m_unit * Vary(seed, 3, 0.1f);
            float x0 = ax - length * 0.5f;
            int left = Mathf.FloorToInt(x0), right = Mathf.CeilToInt(x0 + length);
            if (left < 0 || right >= n) return false;
            for (int dy = -1; dy <= 1; dy++)
                for (int x = left; x <= right; x++)
                {
                    int pixel = (py + dy) * n + x;
                    if (m_land[pixel] != 0 || m_waterDistance[pixel] / DistanceScale < WaveFloatClear || m_floeSet.Contains(pixel)) return false;
                }
            if (!Claim(r, x0, x0 + length)) return false;
            AddStroke(r, x0, x0 + length, y, crests, seed, salt, 0);
            return true;
        }

        // The shore on row py nearest ax within AccentReach whose water lies on the dir side; start
        // is where a stroke off it begins, WaveShoreGap clear of the coast's pen. Only major land
        // is a shore: islets get no coast waves, yet a row reaching past the accent's coast to an
        // islet beside it would anchor strokes there. The slack covers the
        // major-land mask's coarser 12 m cells.
        private bool FindShore(int py, float ax, float reach, int dir, out float start)
        {
            int n = CanvasSize, row = py * n;
            start = 0f;
            int best = -1;
            float bestDistance = float.MaxValue;
            int x0 = Mathf.Max(1, Mathf.FloorToInt(ax - reach)), x1 = Mathf.Min(n - 2, Mathf.CeilToInt(ax + reach));
            for (int x = x0; x <= x1; x++)
            {
                if (m_lineLand[row + x] == 0 || m_land[row + x + dir] != 0) continue;
                if (m_majorDistance[row + x] / DistanceScale > 1.5f) continue;
                // A lake's lines start only at the shore of a lake of their size, the sea's never
                // at a lined lake's.
                if (m_lakeTier[row + x + dir] != m_hand.m_tier) continue;
                float distance = Math.Abs(x + 0.5f - ax);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = x;
                }
            }
            if (best < 0) return false;
            if (m_hand.m_lake)
            {
                // A lake's line starts at the shore itself, touching it.
                start = dir > 0 ? best + 1 : best;
                return true;
            }
            int w = best + dir;
            while (w > 0 && w < n - 1 && m_land[row + w] == 0 && m_waterDistance[row + w] / DistanceScale < WaveShoreGap) w += dir;
            if (w <= 0 || w >= n - 1 || m_land[row + w] != 0) return false;
            start = dir > 0 ? w : w + 1;
            return true;
        }

        // Open water ahead of a stroke starting at start along dir, up to the longest stroke: it
        // stops WaveSlotGap short of the next land, floe or the frame.
        private float Room(int py, float start, int dir)
        {
            int n = CanvasSize, row = py * n;
            float centre = n * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction;
            float most = (m_hand.m_long + 1) * m_hand.m_unit * 1.1f + WaveSlotGap;
            // A lake's line reaches only part of the way to the far shore.
            float scan = m_hand.m_lake ? most / LakeReach : most;
            float dy = py + 0.5f - centre;
            for (float s = 0f; s < scan; s += 1f)
            {
                int x = Mathf.FloorToInt(start + dir * (s + 0.5f));
                if (x < 0 || x >= n) return Math.Max(0f, s - WaveSlotGap);
                int pixel = row + x;
                float dx = x + 0.5f - centre;
                if (m_land[pixel] != 0 || m_floeSet.Contains(pixel) || dx * dx + dy * dy > limit * limit)
                    return m_hand.m_lake ? Math.Min(most, s * LakeReach) : Math.Max(0f, s - WaveSlotGap);
            }
            return most;
        }

        // Takes the stretch x0 to x1 of row r for a stroke, unless it comes within WaveSlotGap of
        // one already there.
        private bool Claim(int r, float x0, float x1)
        {
            if (!m_waveSpans.TryGetValue(r, out List<Vector2> spans)) m_waveSpans[r] = spans = new List<Vector2>();
            foreach (Vector2 span in spans)
                if (x0 < span.y + WaveSlotGap && x1 > span.x - WaveSlotGap) return false;
            spans.Add(new Vector2(x0, x1));
            return true;
        }

        // Rows a little unevenly ruled, as by hand.
        private float RowY(int r, int salt) => (r + 0.5f + (Unit(HashInt(r, -1, salt), 1) - 0.5f) * 0.12f) * WaveRowPitch;

        // A stroke's crests: long and short in turn, a long row a crest longer by seed. With the
        // extra crest on short rows too, a short row could stand as long as the long row beside
        // it and the alternation would be lost.
        private int Crests(int seed, bool longRow) =>
            longRow ? m_hand.m_long + (Unit(seed, 1) < 0.5f ? 0 : 1) : m_hand.m_short;

        // The stroke's length, crests of crest long, with crests dropped until it fits room, or 0
        // if least do not (one crest reads as a comma, not water; a lake line under three dashes
        // as a stray mark).
        private static float Fit(float crest, float room, int least, ref int crests)
        {
            while (crests > least && crests * crest > room) crests--;
            return crests * crest <= room ? crests * crest : 0f;
        }

        // A level wave from x along the row, with a slow waver between strokes. anchor tells
        // close-ups which end starts at the coast (InkRecord.Wave), lift how high its crests
        // rise, since a lake's lie lower than the sea's.
        private void DrawWave(float x, float rowY, float length, int crests, float lift, int seed, int salt, int anchor)
        {
            float y = rowY + (Noise(x / 48f, rowY / 48f, salt + 3) - 0.5f) * 0.4f;
            float phase = (Unit(seed, 5) - 0.5f) * 0.6f;
            var wave = new InkRecord.Wave { m_x = x, m_y = y, m_length = length, m_crests = crests, m_phase = phase, m_lift = lift, m_anchor = anchor };
            m_laidWaves?.Add(wave);
            DrawWave(wave);
        }

        // Inks a laid-out wave and records it.
        private void DrawWave(InkRecord.Wave wave)
        {
            WaveStroke(new Vector2(wave.m_x, wave.m_y), Vector2.right, wave.m_length, wave.m_crests, wave.m_phase, wave.m_lift);
            m_record?.AddWave(wave);
        }

        // The record the sheet is painted into, for the waves close-ups draw again.
        private InkRecord m_record;

        private struct Bend
        {
            public int m_line;
            public int m_index;
            public float m_turn;
        }

        private const int AccentCell = 32;

        // The coast's accents, from the waterline's trace: its sharpest bends first, each at least
        // AccentSpacing from the last, then on the sea one now and then on a long straight stretch
        // facing east or west. Islets get none (DrawIsletWaves); lakes find theirs as they draw
        // (DrawLakeLines).
        private List<Accent> FindAccents(List<ContourLines.Line> lines, int salt)
        {
            int n = CanvasSize;
            float centre = n * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction;
            List<Vector2[]> paths = TracePaths(lines);
            List<Bend> bends = FindBends(lines, paths);
            int window = (int)AccentWindow;
            var accents = new List<Accent>();
            var grid = new Dictionary<long, List<Vector2>>();
            foreach (Bend bend in bends)
            {
                Vector2 at = paths[bend.m_line][bend.m_index];
                if (Near(grid, at, AccentSpacing) || !Accentable(at, centre, limit)) continue;
                accents.Add(BendAccent(paths[bend.m_line], bend.m_index, lines[bend.m_line].m_closed, window, salt));
                AddToGrid(grid, at);
            }
            // Straight stretches count from where the nearest accent's group ends.
            foreach (Vector2[] p in paths)
            {
                float since = 0f, due = AccentPlain;
                foreach (Vector2 at in p)
                {
                    if (Near(grid, at, AccentSpacing))
                    {
                        since = 0f;
                        continue;
                    }
                    since += 1f;
                    if (since < due) continue;
                    int seed = HashInt(Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), salt + 5);
                    since = 0f;
                    due = AccentPlain * Vary(seed, 1, 0.3f);
                    if (Unit(seed, 2) >= AccentPlainOdds || !Accentable(at, centre, limit)) continue;
                    Vector2 normal = SeaNormal(at);
                    if (Math.Abs(normal.y) > Math.Abs(normal.x) * NorthSouth) continue;
                    accents.Add(new Accent { m_at = at, m_normal = normal, m_kind = CoastBend.Straight, m_seed = HashInt(Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), salt + 4) });
                    AddToGrid(grid, at);
                }
            }
            return accents;
        }

        // The trace's lines as points a canvas pixel apart.
        private static List<Vector2[]> TracePaths(List<ContourLines.Line> lines)
        {
            var paths = new List<Vector2[]>(lines.Count);
            foreach (ContourLines.Line line in lines) paths.Add(Resample(line.m_points, line.m_closed));
            return paths;
        }

        // The trace's bends: points turning over AccentTurn across AccentWindow either side that
        // turn the most within half a window, sharpest first.
        private List<Bend> FindBends(List<ContourLines.Line> lines, List<Vector2[]> paths)
        {
            int window = (int)AccentWindow;
            var bends = new List<Bend>();
            var turns = new float[0];
            for (int l = 0; l < paths.Count; l++)
            {
                Vector2[] p = paths[l];
                int m = p.Length;
                bool closed = lines[l].m_closed;
                if (m < 2 * window + 1) continue;
                if (turns.Length < m) turns = new float[m * 2];
                for (int i = 0; i < m; i++) turns[i] = Turn(p, i, window, closed);
                for (int i = 0; i < m; i++)
                {
                    float turn = turns[i];
                    if (turn < AccentTurn) continue;
                    bool peak = true;
                    for (int k = 1; k <= window / 2 && peak; k++)
                    {
                        int before = closed ? (i - k + m) % m : i - k, after = closed ? (i + k) % m : i + k;
                        if (before >= 0 && turns[before] > turn) peak = false;
                        if (after < m && turns[after] >= turn) peak = false;
                    }
                    if (peak) bends.Add(new Bend { m_line = l, m_index = i, m_turn = turn });
                }
            }
            m_cancel.ThrowIfCancellationRequested();
            bends.Sort((x, y) => x.m_turn != y.m_turn ? y.m_turn.CompareTo(x.m_turn)
                : x.m_line != y.m_line ? x.m_line.CompareTo(y.m_line) : x.m_index.CompareTo(y.m_index));
            return bends;
        }
        // Inside the frame and on the hand's shore: the sea's coast of major land, not an islet's,
        // or the shore of a lake of the hand's size.
        private bool Accentable(Vector2 at, float centre, float limit)
        {
            float dx = at.x - centre, dy = at.y - centre;
            if (dx * dx + dy * dy > limit * limit) return false;
            int pixel = PixelAt(at.x, at.y);
            if (m_hand.m_lake ? m_lakeTier[pixel] != m_hand.m_tier : m_sea[pixel] == 0) return false;
            return !(m_majorDistance[pixel] / DistanceScale > m_waterDistance[pixel] / DistanceScale + 2f);
        }

        // A bend's accent: a headland when its point bulges out to sea from the line round it, a
        // bay when it lies back from it, its normal pointing from the bulge out to sea, or from
        // the bay's head into the bay.
        private Accent BendAccent(Vector2[] p, int i, bool closed, int window, int salt)
        {
            int m = p.Length;
            Vector2 at = p[i], sum = Vector2.zero;
            int count = 0;
            for (int k = -window; k <= window; k++)
            {
                int j = closed ? ((i + k) % m + m) % m : Mathf.Clamp(i + k, 0, m - 1);
                sum += p[j];
                count++;
            }
            Vector2 bulge = at - sum / count;
            Vector2 normal = SeaNormal(at);
            var kind = CoastBend.Straight;
            if (bulge.sqrMagnitude > 0.25f)
            {
                kind = Vector2.Dot(bulge, normal) > 0f ? CoastBend.Headland : CoastBend.Bay;
                normal = kind == CoastBend.Headland ? bulge.normalized : -bulge.normalized;
            }
            int seed = HashInt(Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), salt + 4);
            return new Accent { m_at = at, m_normal = normal, m_kind = kind, m_seed = seed };
        }

        // The direction away from land at a sheet point, from the distance to land two pixels
        // either side.
        private Vector2 SeaNormal(Vector2 at)
        {
            int n = CanvasSize;
            int x = Mathf.Clamp(Mathf.FloorToInt(at.x), 2, n - 3), y = Mathf.Clamp(Mathf.FloorToInt(at.y), 2, n - 3);
            float gx = m_waterDistance[y * n + x + 2] - m_waterDistance[y * n + x - 2];
            float gy = m_waterDistance[(y + 2) * n + x] - m_waterDistance[(y - 2) * n + x];
            var g = new Vector2(gx, gy);
            return g.sqrMagnitude > 0f ? g.normalized : Vector2.right;
        }

        private static void AddToGrid(Dictionary<long, List<Vector2>> grid, Vector2 at)
        {
            long key = AccentKey(Mathf.FloorToInt(at.x / AccentCell), Mathf.FloorToInt(at.y / AccentCell));
            if (!grid.TryGetValue(key, out List<Vector2> cell)) grid[key] = cell = new List<Vector2>();
            cell.Add(at);
        }

        private static bool Near(Dictionary<long, List<Vector2>> grid, Vector2 at, float radius)
        {
            int cx = Mathf.FloorToInt(at.x / AccentCell), cy = Mathf.FloorToInt(at.y / AccentCell);
            int reach = Mathf.CeilToInt(radius / AccentCell);
            float sqr = radius * radius;
            for (int y = cy - reach; y <= cy + reach; y++)
                for (int x = cx - reach; x <= cx + reach; x++)
                {
                    if (!grid.TryGetValue(AccentKey(x, y), out List<Vector2> cell)) continue;
                    foreach (Vector2 other in cell)
                        if ((other - at).sqrMagnitude < sqr) return true;
                }
            return false;
        }

        private static long AccentKey(int x, int y) => ((long)x << 32) ^ (uint)y;

        // How sharply the line turns at point i, in radians, between the chords window points
        // back and ahead; 0 where an open line has no room.
        private static float Turn(Vector2[] p, int i, int window, bool closed)
        {
            int m = p.Length;
            int back = i - window, ahead = i + window;
            if (closed)
            {
                back = (back + m) % m;
                ahead %= m;
            }
            else if (back < 0 || ahead >= m)
            {
                return 0f;
            }
            Vector2 a = p[i] - p[back], b = p[ahead] - p[i];
            return Mathf.Abs(Mathf.Atan2(a.x * b.y - a.y * b.x, a.x * b.x + a.y * b.y));
        }

        // A polyline as points one canvas pixel apart along it; a closed one (its first point
        // repeated at the end) without the repeat.
        private static Vector2[] Resample(List<Vector2> points, bool closed)
        {
            var result = new List<Vector2>();
            if (points.Count == 0) return result.ToArray();
            result.Add(points[0]);
            float carry = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                Vector2 a = points[i - 1], b = points[i];
                float length = Vector2.Distance(a, b);
                float s = 1f - carry;
                while (s <= length)
                {
                    result.Add(Vector2.Lerp(a, b, s / length));
                    s += 1f;
                }
                carry = length - (s - 1f);
            }
            if (closed && result.Count > 1 && (result[0] - result[result.Count - 1]).sqrMagnitude < 0.25f) result.RemoveAt(result.Count - 1);
            return result.ToArray();
        }

        private HashSet<int> m_floeSet;
        private Dictionary<int, List<Vector2>> m_waveSpans;

        // The waterline: one thin line WaterLine out from every lined shore, as the classic maps
        // double their coasts, and always a closed loop, never a loose end. It
        // runs on the distance from land and from m_noLine, the water that carries no line
        // (channels, ponds, small lakes), so at a strait's mouth it turns across instead of
        // stopping: two lines along both banks of a strait read as a heavy border.
        // Traced, smoothed and returned, not inked: the waves find the coast's bends on it.
        private List<ContourLines.Line> TraceWaterLines()
        {
            int n = CanvasSize;
            var field = new DistanceField(n);
            byte[] lineLand = m_lineLand = LineLand();
            var landDistance = new byte[n * n];
            field.ComputeParallel(i => lineLand[i] != 0, landDistance, DistanceScale, m_cancel);
            m_cancel.ThrowIfCancellationRequested();
            var open = new byte[n * n];
            float wide = WideWater * DistanceScale;
            field.ComputeParallel(i => landDistance[i] >= wide, open, 1f, m_cancel);
            m_noLine = new byte[n * n];
            // A small lake has no water WideWater from land, so as a channel it would have no trace
            // and its shore no bends.
            for (int i = 0; i < n * n; i++)
                if (m_land[i] == 0 && ((open[i] > ChannelReach && m_lakeTier[i] != SmallLake) || !Lined(i))) m_noLine[i] = 1;
            m_cancel.ThrowIfCancellationRequested();
            m_lineDistance = new byte[n * n];
            field.ComputeParallel(i => lineLand[i] != 0 || m_noLine[i] != 0, m_lineDistance, DistanceScale, m_cancel);
            m_cancel.ThrowIfCancellationRequested();
            var contour = new ContourLines();
            DistanceField.Contour(n,
                (x, y) => m_lineDistance[y * n + x] / DistanceScale,
                WaterLine,
                // Neighbouring pixels differ by at most a diagonal, so only cells this near the
                // level can cross it.
                (x, y) => Math.Abs(m_lineDistance[y * n + x] / DistanceScale - WaterLine) > 1.5f,
                (a, b) => contour.Add(a + new Vector2(0.5f, 0.5f), b + new Vector2(0.5f, 0.5f)));
            m_cancel.ThrowIfCancellationRequested();
            return SmoothLines(contour);
        }

        // The land the waterline keeps its distance from: all of it but islets under
        // SmallIsletArea (four-way connected patches). A patch is grown only until it is known to
        // be large: a pixel joined to a patch already found large makes this one large too, so
        // every pixel is visited once.
        private byte[] LineLand()
        {
            int n = CanvasSize;
            // 0 unseen, 1 in a small islet, 2 in large land.
            var state = new byte[n * n];
            var patch = new List<int>(64);
            for (int start = 0; start < n * n; start++)
            {
                if (m_land[start] == 0 || state[start] != 0) continue;
                if ((start & 0xFFFFF) == 0) m_cancel.ThrowIfCancellationRequested();
                patch.Clear();
                patch.Add(start);
                state[start] = 1;
                bool large = false;
                for (int k = 0; k < patch.Count && !large; k++)
                {
                    int i = patch[k], x = i % n, y = i / n;
                    if (x > 0) large |= Join(i - 1);
                    if (x < n - 1) large |= Join(i + 1);
                    if (y > 0) large |= Join(i - n);
                    if (y < n - 1) large |= Join(i + n);
                    if (patch.Count >= SmallIsletArea) large = true;
                }
                if (large) foreach (int i in patch) state[i] = 2;
            }
            var land = new byte[n * n];
            for (int i = 0; i < n * n; i++)
                if (state[i] == 2) land[i] = 1;
            return land;

            // Adds an unseen land pixel to the patch; true when it touches land already found large.
            bool Join(int j)
            {
                if (m_land[j] == 0) return false;
                if (state[j] == 2) return true;
                if (state[j] == 0)
                {
                    state[j] = 1;
                    patch.Add(j);
                }
                return false;
            }
        }

        // Joins contour segments into lines and smooths them; a closed line gets its first point
        // again at the end. Each is a closed loop or runs off the sheet's or tile's edge, so none
        // is a scrap to drop.
        private static List<ContourLines.Line> SmoothLines(ContourLines contour)
        {
            List<ContourLines.Line> lines = contour.Build();
            foreach (ContourLines.Line line in lines)
            {
                ContourLines.Smooth(line);
                if (line.m_closed && line.m_points.Count > 0) line.m_points.Add(line.m_points[0]);
            }
            return lines;
        }

        private byte[] m_noLine;
        private byte[] m_lineDistance;
        // The land the waterline keeps from, without islets under SmallIsletArea.
        private byte[] m_lineLand;

        // Water that gets the water lines: the sea, and lakes wide enough to carry them.
        private bool Lined(int pixel) => m_sea[pixel] != 0 || (m_wideLake != null && m_wideLake[pixel] != 0);

        private byte[] m_wideLake;

        // Marks the lakes whose water lies at least LakeMinWidth from their shore somewhere: a
        // pond or a swamp pool ringed with a waterline and strokes reads as a target, not as a
        // lake. Each lake is one four-way connected patch of inland water.
        private void MarkWideLakes()
        {
            int n = CanvasSize;
            m_wideLake = new byte[n * n];
            m_lakeTier = new byte[n * n];
            var seen = new bool[n * n];
            var patch = new List<int>();
            var queue = new Queue<int>();
            for (int start = 0; start < n * n; start++)
            {
                if (seen[start] || m_land[start] != 0 || m_sea[start] != 0) continue;
                if ((start & 0xFFFFF) == 0) m_cancel.ThrowIfCancellationRequested();
                patch.Clear();
                seen[start] = true;
                queue.Enqueue(start);
                float widest = 0f;
                int centre = start;
                while (queue.Count > 0)
                {
                    int i = queue.Dequeue();
                    patch.Add(i);
                    float d = m_waterDistance[i] / DistanceScale;
                    if (d > widest)
                    {
                        widest = d;
                        centre = i;
                    }
                    int x = i % n, y = i / n;
                    if (x > 0) Visit(i - 1, seen, queue);
                    if (x < n - 1) Visit(i + 1, seen, queue);
                    if (y > 0) Visit(i - n, seen, queue);
                    if (y < n - 1) Visit(i + n, seen, queue);
                }
                if (widest < LakeMinWidth) continue;
                byte tier = widest < LakeWaveWidth ? SmallLake : LargeLake;
                m_lakeCentres.Add(centre);
                foreach (int i in patch)
                {
                    m_wideLake[i] = 1;
                    m_lakeTier[i] = tier;
                }
            }
        }

        // The pixel farthest from the shore of every lined lake: one group goes beside it when
        // none was drawn at the lake's bends.
        private readonly List<int> m_lakeCentres = new List<int>();

        // The lined lakes' water by size (LargeLake, SmallLake, 0 elsewhere), for their lines'
        // shores and accents: each size has its own hand.
        private byte[] m_lakeTier;
        private const byte LargeLake = 1;
        private const byte SmallLake = 2;

        // How a group's strokes are drawn: the sea's waves, the islets' smaller ones, or a lake's
        // straight lines, m_tier naming the lakes it is for (0 at sea).
        private struct WaveHand
        {
            public bool m_lake;
            public byte m_tier;
            // A group of fewer than m_floor strokes is left out.
            public int m_min, m_max, m_floor;
            // A stroke is never cut to fewer than m_least crests or dashes.
            public int m_least;
            public float m_unit, m_lift;
            public int m_long, m_short;
        }

        private static readonly WaveHand s_seaHand = new WaveHand
        {
            m_min = WaveGroupMin, m_max = WaveGroupMax, m_floor = 0, m_least = 2, m_unit = WaveCrest, m_lift = WaveLift,
            m_long = WaveLongCrests, m_short = WaveShortCrests,
        };

        private static readonly WaveHand s_isletHand = new WaveHand
        {
            m_min = StepGroupMin, m_max = StepGroupMax, m_floor = GroupFloor, m_least = IsletShortCrests, m_unit = IsletCrest, m_lift = WaveLift,
            m_long = IsletLongCrests, m_short = IsletShortCrests,
        };

        // Lift 0: the stroke runs out as a straight line.
        private static readonly WaveHand s_lakeHand = new WaveHand
        {
            m_lake = true, m_tier = LargeLake, m_min = LakeGroupMin, m_max = LakeGroupMax, m_floor = GroupFloor, m_least = LakeLeastDashes, m_unit = LakeDash,
            m_long = LakeLongDashes, m_short = LakeShortDashes,
        };

        private static readonly WaveHand s_smallLakeHand = new WaveHand
        {
            m_lake = true, m_tier = SmallLake, m_min = StepGroupMin, m_max = StepGroupMax, m_floor = GroupFloor, m_least = LakeLeastDashes, m_unit = SmallLakeDash,
            m_long = SmallLakeLongDashes, m_short = SmallLakeShortDashes,
        };

        private WaveHand m_hand = s_seaHand;

        // The lined lakes' lines, large lakes first: groups at the bends of their shores, found as
        // the sea's are, then on their straight shores facing east or west about every LakePlain
        // along the trace, and for a lake where none was drawn one group off its west or east
        // shore level with its middle, the side with more water before it.
        private void DrawLakeLines(List<ContourLines.Line> lines, int salt)
        {
            List<Vector2[]> paths = TracePaths(lines);
            List<Bend> bends = FindBends(lines, paths);
            DrawLakeLines(lines, paths, bends, salt, s_lakeHand);
            DrawLakeLines(lines, paths, bends, salt, s_smallLakeHand);
            m_hand = s_seaHand;
        }

        // Only a group actually drawn keeps the next AccentSpacing away: a bend whose bay is too
        // small for lines of LakeLeastDashes does not leave the shore round it bare. A straight
        // spot that cannot take a group is tried again LakeRetry further on.
        private void DrawLakeLines(List<ContourLines.Line> lines, List<Vector2[]> paths, List<Bend> bends, int salt, WaveHand hand)
        {
            m_hand = hand;
            float middle = CanvasSize * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction;
            var drawn = new List<Accent>();
            var grid = new Dictionary<long, List<Vector2>>();
            int window = (int)AccentWindow;
            foreach (Bend bend in bends)
            {
                Vector2 at = paths[bend.m_line][bend.m_index];
                if (Near(grid, at, AccentSpacing) || !Accentable(at, middle, limit)) continue;
                Accent accent = BendAccent(paths[bend.m_line], bend.m_index, lines[bend.m_line].m_closed, window, salt);
                if (!DrawGroup(accent, salt)) continue;
                drawn.Add(accent);
                AddToGrid(grid, at);
            }
            m_cancel.ThrowIfCancellationRequested();
            foreach (Vector2[] p in paths)
            {
                float since = 0f, due = LakePlain;
                foreach (Vector2 at in p)
                {
                    if (Near(grid, at, AccentSpacing))
                    {
                        since = 0f;
                        continue;
                    }
                    since += 1f;
                    if (since < due) continue;
                    int seed = HashInt(Mathf.RoundToInt(at.x), Mathf.RoundToInt(at.y), salt + 4);
                    Vector2 normal = SeaNormal(at);
                    var accent = new Accent { m_at = at, m_normal = normal, m_kind = CoastBend.Straight, m_seed = seed };
                    if (Accentable(at, middle, limit) && Math.Abs(normal.y) <= Math.Abs(normal.x) * NorthSouth && DrawGroup(accent, salt))
                    {
                        drawn.Add(accent);
                        AddToGrid(grid, at);
                        since = 0f;
                        due = LakePlain * Vary(seed, 1, 0.3f);
                    }
                    else
                    {
                        since -= LakeRetry;
                    }
                }
            }
            int n = CanvasSize;
            foreach (int centre in m_lakeCentres)
            {
                if (m_lakeTier[centre] != hand.m_tier) continue;
                int cx = centre % n, cy = centre / n;
                // The lake's reach: its middle's distance from the shore, and a margin for its shape.
                float reach = m_waterDistance[centre] / DistanceScale * 3f + AccentWindow;
                bool covered = false;
                foreach (Accent accent in drawn)
                    if ((accent.m_at - new Vector2(cx, cy)).sqrMagnitude < reach * reach) covered = true;
                if (covered) continue;
                int west = cx, east = cx, row = cy * n;
                while (west > 0 && m_land[row + west - 1] == 0) west--;
                while (east < n - 1 && m_land[row + east + 1] == 0) east++;
                bool fromWest = cx - west >= east - cx;
                var at = new Vector2(fromWest ? west : east + 1f, cy + 0.5f);
                int seed = HashInt(cx, cy, salt + 10);
                DrawGroup(new Accent { m_at = at, m_normal = new Vector2(fromWest ? 1f : -1f, 0f), m_kind = CoastBend.Straight, m_seed = seed }, salt);
            }
        }

        private void Visit(int i, bool[] seen, Queue<int> queue)
        {
            if (seen[i] || m_land[i] != 0 || m_sea[i] != 0) return;
            seen[i] = true;
            queue.Enqueue(i);
        }

        // Tests (mark false) or marks the sheet pixels of the taken set along a segment.
        private static bool Footprint(HashSet<int> taken, int n, Vector2 from, Vector2 to, float radius, bool mark)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector2.Distance(from, to)));
            for (int s = 0; s <= steps; s++)
            {
                Vector2 q = Vector2.Lerp(from, to, s / (float)steps);
                int x0 = Mathf.Clamp(Mathf.FloorToInt(q.x - radius), 0, n - 1), x1 = Mathf.Clamp(Mathf.FloorToInt(q.x + radius), 0, n - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt(q.y - radius), 0, n - 1), y1 = Mathf.Clamp(Mathf.FloorToInt(q.y + radius), 0, n - 1);
                for (int cy = y0; cy <= y1; cy++)
                {
                    for (int cx = x0; cx <= x1; cx++)
                    {
                        if (mark) taken.Add(cy * n + cx);
                        else if (taken.Contains(cy * n + cx)) return true;
                    }
                }
            }
            return false;
        }

        private float WaterAt(Vector2 p) => m_waterDistance[PixelAt(p.x, p.y)] / DistanceScale;

        private bool OnSea(Vector2 p)
        {
            int pixel = PixelAt(p.x, p.y);
            return m_land[pixel] == 0 && m_sea[pixel] != 0;
        }

        // A tilde from a along dir: crests that rise and dip by lift (WaveLift at sea, none on
        // a lake), running out flat at both ends, eight points a crest so close-ups show a curve,
        // not a zigzag. The phase (radians) shifts the crests, so neighbouring strokes in a row do
        // not crest in step.
        private void WaveStroke(Vector2 a, Vector2 dir, float length, int crests, float phase, float lift)
        {
            var up = new Vector2(dir.y, -dir.x);
            m_points.Clear();
            int count = crests * 8;
            for (int i = 0; i <= count; i++) m_points.Add(WavePoint(i / (float)count));
            if (m_points.Count > 1) m_canvas.Polyline(m_points, Pen);

            Vector2 WavePoint(float u)
            {
                float envelope = Mathf.Clamp01(Mathf.Sin(Mathf.PI * u) * 1.6f);
                float rise = Mathf.Sin(u * crests * Mathf.PI * 2f + phase) * lift * envelope;
                return a + dir * (u * length) + up * rise;
            }
        }

        private void DrawFrame()
        {
            float centre = CanvasSize * 0.5f;
            float radius = EdgeRadius / MetresPerPixel;
            m_canvas.Ring(centre, centre, radius, radius, 2.6f);
            m_canvas.Ring(centre, centre, radius - FrameGap, radius - FrameGap, 1.0f);
        }

        private static float Noise(float x, float y, int seed) => Atlas.Noise.Value(x, y, seed);
    }
}
