using System;
using System.Collections.Generic;
using System.Threading;
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
        public const float HalfExtent = 10800f;
        public const float MetresPerPixel = 2f * HalfExtent / CanvasSize;
        // Vanilla's world edge (WorldGenerator.waterEdge): the frame is drawn there.
        public const float EdgeRadius = 10500f;

        // Every glyph size and spacing below is in these units; 1.5 canvas pixels (about 8 m).
        private const float G = 1.5f;
        // Trees use a smaller unit than other glyphs, packed as densely in their own unit, so woods
        // keep their texture while each tree reads as a tree at the closer zoom.
        private const float T = 0.75f * G;

        // The coast's pen, in canvas pixels: barely heavier than the waves beside it, as on the
        // classic map (it was 2.1, the map's heaviest line).
        private const float CoastWidth = 1.4f;
        // Wave accents (canvas pixels): groups start WaveFirstRow from land, rows WaveRowGap
        // apart; candidate points every WaveSpotStep along the shore, scored on a disc of
        // WaveShapeRadius; a score of WaveAccent or more gets a group, others a lone stroke at
        // chance WaveLone; groups keep WaveGroupGap apart, give or take WaveGapJitter so they
        // fall in no grid. Each wave a level tilde of two or three crests WaveCrest long and
        // WaveLift high, its ends WaveShoreClear from land: about 30 to 40 m, a tree's width or
        // two, so it sits among the glyphs at their scale (smaller, more
        // delicate). Islets under MajorLandCells map pixels get two strokes at most.
        private const float WaveFirstRow = 3f;
        private const float WaveRowGap = 2.8f;
        private const float WaveSpotStep = 4f;
        private const float WaveShapeRadius = 22f;
        private const float WaveAccent = 0.3f;
        private const float WaveLone = 0.1f;
        private const float WaveGroupGap = 26f;
        private const float WaveGapJitter = 0.3f;
        private const float WaveCrest = 2.6f;
        private const float WaveLift = 0.45f;
        private const float WaveShoreClear = 1.6f;
        // Open water: one candidate per WaveOpenStep square, taken at chance WaveOpenChance, at
        // least WaveOpenShore from land and WaveOpenGap from any other group, so the coasts keep
        // most of the waves and the open sea gets a sparse scatter of small groups; the chance
        // drifts over WaveOpenDrift squares, so the scatter comes in loose patches.
        private const float WaveOpenDrift = 4f;
        private const float WaveOpenStep = 40f;
        private const float WaveOpenChance = 0.65f;
        private const float WaveOpenShore = 10f;
        private const float WaveOpenGap = 36f;
        // A stroke marks WaveInkHalf either side of its line and keeps WaveKeepOff from any mark,
        // so a group's rows, under three pixels apart, still pass while no two strokes touch.
        private const float WaveInkHalf = 0.5f;
        private const float WaveKeepOff = 0.9f;
        // A stroke starts WaveShoreGap off a shore level with it within WaveAnchorReach, and such
        // points score WaveBesideBonus more.
        private const float WaveShoreGap = 1.4f;
        private const int WaveAnchorReach = 8;
        private const float WaveBesideBonus = 0.25f;
        private const int MajorLandCells = 400;
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
        // Patches of land or water smaller than this many map pixels (12 m each) are dropped as noise.
        private const int SpeckCells = 2;

        // Woods: the wood field's edge level (Woods.Build), and how far ground fills and
        // open-country glyphs keep from a wood (canvas pixels), so the outer crowns alone bound it.
        private const float WoodEdge = 0.5f;
        private const float WoodClearance = 5f;

        // Terrain thresholds: metres above the water and height change per metre of slope.
        private const float PeakHeight = 50f;
        private const float DeepNorthPeakHeight = 45f;
        // Summits and hilltops: a map pixel (12 m) is a summit when it is the highest within
        // SummitCells and rises SummitProminence metres above the lowest point within
        // ProminenceCells; hilltops likewise with smaller numbers. Filler peaks keep SummitClearance
        // times the two glyphs' widths away from a summit's glyph.
        private const int SummitCells = 6;
        private const float SummitProminence = 20f;
        private const int HilltopCells = 4;
        private const float HillProminence = 6f;
        private const int ProminenceCells = 10;
        private const float SummitClearance = 0.55f;
        // Mist banks: the gap between a bank's three lines (canvas pixels), the step between points
        // along a line, and the height above the water (metres) by which banks have thinned to
        // their fewest, as the game's mist clears on the heights.
        private const float MistGap = 2f * G;
        private const float MistStep = 1.5f * G;
        private const float MistCeiling = 75f;
        // Deep North pressure ridges: the scale of the noise their bands follow (canvas pixels) and
        // how close to a crest line a chevron must lie (1 is on the crest).
        private const float RidgeScale = 70f * G;
        private const float RidgeBand = 0.9f;
        // How far a chevron keeps from the shore (in the rules' shore units) and from the next
        // chevron (in G).
        private const float RidgeShore = 4f * G;
        private const float RidgeGap = 1.5f;
        // Rivers: the pen for a river, a stream at its mouth (thinning to Pen at its source), the extra
        // width scraped clear on either side, and the step between points along a course (canvas px).
        private const float RiverWidth = 1.2f;
        private const float StreamMouth = 1.1f;
        private const float RiverBand = 2.4f;
        private const float RiverStep = 1.5f;
        // River ink keeps this far (canvas pixels) from any water the coast already draws, or it
        // runs along a bank or a lake shore and the two lines merge into a smear.
        private const float RiverShoreClear = 2f;
        // Mistlands ground this steep (height per metre) is broken crag: filler spires stand there,
        // and woods and mist keep off it. Summit spires stand on the real summits: the highest
        // map pixel within SpireCells that rises SpireProminence metres over its surroundings.
        private const float SpireSlope = 0.45f;
        private const int SpireCells = 5;
        private const float SpireProminence = 25f;
        private const float VolcanoHeight = 22f;
        // Volcanoes are drawn larger than other land glyphs, closer to the size of a peak.
        private const float VolcanoScale = 2.2f;
        // Ashlands ground this steep (height per metre) is broken into crags.
        private const float CragSlope = 0.25f;
        // Crags are small peaks, about half a filler peak's width.
        private const float CragScale = 1.8f;
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
        private readonly List<Vector2> m_points = new List<Vector2>(16);
        private readonly List<bool> m_pathInside = new List<bool>(32);
        // Canvas pixels covered by peaks and spires, which the mist banks drawn later must not cut.
        private readonly List<Glyph> m_glyphs = new List<Glyph>(200000);
        // Real summits placed so far: apex position (x, y) and glyph width (z).
        private readonly List<Vector3> m_summits = new List<Vector3>(1024);
        // Volcano feet placed so far, which keep later ones away.
        private readonly List<Vector2> m_volcanoes = new List<Vector2>(64);
        // Ice floes sit on the water, so they are drawn after the coast's wave accents, over them.
        private readonly List<Glyph> m_floes = new List<Glyph>(4096);
        private float[] m_coast;
        // Map pixels of water joined to the sea, and canvas pixels of sea.
        private bool[] m_seaCell;
        private byte[] m_sea;
        private byte[] m_land;
        private byte[] m_waterDistance;
        // Distance from land masses of at least MajorLandCells, scaled like m_waterDistance.
        private byte[] m_majorDistance;
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
            Volcano,
            Crag,
            Mist,
            IceFloe,
        }

        private struct Glyph
        {
            public float m_x, m_y, m_size;
            public Kind m_kind;
            public int m_seed;
        }

        // Everything a scatter rule needs to know about one canvas point.
        private struct Spot
        {
            public float m_x, m_y, m_worldX, m_worldZ, m_above, m_slope, m_roll;
            public int m_pixel, m_seed;
            public Land m_land;
            public bool m_isLand, m_wood, m_mist, m_lava;
            // The lava field's value here, 0.5 at a lava field's edge.
            public float m_lavaLevel;
            // The scatter cell and the grid's hash seed, so a rule can work out its neighbours'
            // candidates from hashes alone.
            public int m_gx, m_gy, m_gridSeed;
            // Distances to water, to land and to the nearest wood, in canvas pixels.
            public float m_shore, m_water, m_woodDistance;
        }

        private InkPainter(WorldSource world, CancellationToken cancel)
        {
            m_world = world;
            m_cancel = cancel;
            m_canvas = new InkCanvas(CanvasSize);
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
        // world the player already left stops instead of holding its memory to the end. When record
        // is given, every pen call is kept in it (and indexed) for sharp detail tiles.
        public static byte[] Paint(WorldSource world, CancellationToken cancel, InkRecord record)
        {
            var painter = new InkPainter(world, cancel);
            if (record != null) painter.m_canvas.Record(record);
            painter.Run();
            record?.Seal(CanvasSize);
            // The sheet: ink coverage, then the peaks' body masks (mountain, Deep North), one
            // byte a pixel each.
            int n = CanvasSize * CanvasSize;
            var sheet = new byte[n * 3];
            Buffer.BlockCopy(painter.m_canvas.m_ink, 0, sheet, 0, n);
            Buffer.BlockCopy(painter.m_canvas.m_body, 0, sheet, n, n);
            Buffer.BlockCopy(painter.m_canvas.m_bodyNorth, 0, sheet, 2 * n, n);
            return sheet;
        }

        private void Run()
        {
            BuildCoastHeights();
            m_cancel.ThrowIfCancellationRequested();
            BuildFields();
            m_cancel.ThrowIfCancellationRequested();
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
            PlaceGlyphs();
            m_cancel.ThrowIfCancellationRequested();
            m_glyphs.Sort((a, b) => a.m_y.CompareTo(b.m_y));
            foreach (Glyph glyph in m_glyphs)
            {
                bool tree = IsTree(glyph.m_kind);
                m_canvas.m_layer = tree ? InkLayer.Tree : InkLayer.Glyph;
                if (!tree) m_canvas.BeginGroup(glyph.m_y);
                DrawGlyph(glyph);
                if (!tree) m_canvas.EndGroup();
            }
            m_canvas.m_layer = InkLayer.Ripple;
            DrawWaveAccents();
            m_canvas.m_layer = InkLayer.Floe;
            foreach (Glyph floe in m_floes) DrawGlyph(floe);
            m_canvas.m_layer = InkLayer.Frame;
            DrawFrame();
        }

        // The glyphs a detail tile draws afresh at its own level of detail.
        private static bool IsTree(Kind kind) =>
            kind == Kind.Broadleaf || kind == Kind.Conifer || kind == Kind.PlainsTree || kind == Kind.GiantTree;

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
            for (int y = 0; y < n; y++)
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
                        if (m_world.Sample(m_world.m_wood, wx, wz) > WoodEdge) m_wood[pixel] = 1;
                        continue;
                    }
                    // Sea if any of the four map pixels around it is sea water.
                    int sx = Mathf.Clamp((int)m_world.ToSourceX(wx), 0, size - 2);
                    int sz = Mathf.Clamp((int)m_world.ToSourceZ(wz), 0, size - 2);
                    int i = sz * size + sx;
                    if (m_seaCell[i] || m_seaCell[i + 1] || m_seaCell[i + size] || m_seaCell[i + size + 1]) m_sea[pixel] = 1;
                }
            }

            var field = new DistanceField(n);
            m_waterDistance = new byte[n * n];
            m_landDistance = new byte[n * n];
            field.Compute(i => m_land[i] != 0, m_waterDistance, DistanceScale);
            m_cancel.ThrowIfCancellationRequested();
            field.Compute(i => m_land[i] == 0, m_landDistance, 1f);
            m_cancel.ThrowIfCancellationRequested();
            m_woodDistance = new byte[n * n];
            field.Compute(i => m_wood[i] != 0, m_woodDistance, 1f);
            m_cancel.ThrowIfCancellationRequested();
            bool[] major = MajorLand(m_world.m_waterLevel, size);
            m_majorDistance = new byte[n * n];
            field.Compute(i =>
            {
                if (m_land[i] == 0) return false;
                float wx = (i % n + 0.5f) * MetresPerPixel - HalfExtent, wz = HalfExtent - (i / n + 0.5f) * MetresPerPixel;
                return major[m_world.Index(wx, wz)];
            }, m_majorDistance, DistanceScale);
        }

        // The map pixels of land masses of at least MajorLandCells (four-way connected), which get
        // the wave accents' full band; smaller islets only a ring, or a chain of them turns grey.
        private bool[] MajorLand(float water, int size)
        {
            var major = new bool[size * size];
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
                if (tail < MajorLandCells) continue;
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
            spot.m_above = m_world.HeightAt(spot.m_worldX, spot.m_worldZ) - m_world.m_waterLevel;
            spot.m_slope = m_world.SlopeAt(spot.m_worldX, spot.m_worldZ);
            spot.m_wood = m_wood[spot.m_pixel] != 0;
            spot.m_woodDistance = m_woodDistance[spot.m_pixel];
            spot.m_mist = m_world.m_mist[source] > 127;
            spot.m_lavaLevel = m_world.Sample(m_world.m_lavaField, spot.m_worldX, spot.m_worldZ);
            spot.m_lava = spot.m_lavaLevel > 0.5f;
            return spot;
        }

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
            spot.m_slope = m_grid.Slope(x, y);
            spot.m_wood = m_grid.IsWood(x, y);
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
        // row shifted by stagger cells, so glyphs pack like bricks.
        private void Scatter(float spacingX, float spacingY, float jitter, float stagger, int salt, Action<Spot> visit)
        {
            // The grid is anchored to the sheet, so a tile's glyphs are the same whichever part of
            // the sheet it covers; each level of detail has grids of its own.
            int columns = (int)(CanvasSize / spacingX);
            int rows = (int)(CanvasSize / spacingY);
            // A tile also visits cells around it, far enough for any glyph standing outside it to
            // reach in, so glyphs carry on unbroken across tile edges.
            float reach = m_grid != null ? GlyphReach / m_lod : 0f;
            int gx0 = Math.Max(0, (int)((m_left - reach) / spacingX) - 1), gx1 = Math.Min(columns, (int)((m_right + reach) / spacingX) + 2);
            int gy0 = Math.Max(0, (int)((m_top - reach) / spacingY) - 1), gy1 = Math.Min(rows, (int)((m_bottom + reach) / spacingY) + 2);
            float edge = EdgeRadius / MetresPerPixel;
            float centre = CanvasSize * 0.5f;
            // Level 1 hashes as the sheet does, so a tile at that level places the sheet's glyphs.
            int worldSeed = unchecked(m_world.m_seed + salt * 7919 + (Mathf.RoundToInt(m_lod * 4f) - 4) * 104729);
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
        // testing only the top let a pine on a thin spit of land reach across the water between.
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

        // Whether a peak of the given width and height standing at (x, y) lies wholly over land:
        // its centre column, both ends of its base and both flanks. Testing the column alone let
        // the wide base of a coastal peak hang out over the sea.
        private bool PeakOnLand(float x, float y, float width, float height)
        {
            if (!IsLandColumn(x, y, height)) return false;
            float half = width * 0.55f;
            return IsLandAt(x - half, y) && IsLandAt(x + half, y)
                && IsLandAt(x - half * 0.5f, y - height * 0.5f) && IsLandAt(x + half * 0.5f, y - height * 0.5f);
        }

        // Whether a peak-like glyph of the given width standing at (x, y) keeps its base off every
        // lava field, along its whole width with a margin. Testing the foot alone let crags and
        // volcanoes stand with their bases in lava lakes; the peak above may still rise in front
        // of a lake behind it ("peaks yes, bases not").
        private bool ClearOfLava(float x, float y, float width)
        {
            float half = width * 0.6f;
            for (int k = 0; k <= 6; k++)
            {
                float px = x - half + 2f * half * k / 6f;
                if (LavaAt(px, y + 1.5f) || LavaAt(px, y - 1.5f)) return false;
            }
            return true;
        }

        private bool LavaAt(float x, float y) =>
            m_world.Sample(m_world.m_lavaField, x * MetresPerPixel - HalfExtent, HalfExtent - y * MetresPerPixel) > 0.5f;

        private void Add(Spot spot, Kind kind, float size)
        {
            m_glyphs.Add(new Glyph { m_x = spot.m_x, m_y = spot.m_y, m_size = size, m_kind = kind, m_seed = spot.m_seed });
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
            // Meadows: short tufts of grass.
            Scatter(12f * m_g, 14, s =>
            {
                if (!IsOpenLand(s, Land.Meadows, 3f) || !(s.m_roll < 0.3f)) return;
                Grass(s.m_x, s.m_y, m_g);
            });
            // Black Forest: scattered stones on the open ground between the woods.
            Scatter(10f * m_g, 17, s =>
            {
                if (!IsOpenLand(s, Land.BlackForest, 3f) || !(s.m_roll < 0.4f)) return;
                Stone(s.m_x, s.m_y, m_g, s.m_seed);
            });
            // Swamp: the classic map's marsh sign, short flat water
            // lines with tufts of reeds on many, gathered into patches by a slow noise: crowded
            // fen in places, thin open bog between. Even rows over the whole swamp read as messy
            // and ruled, so the grid is square and fully jittered.
            // The bog between patches keeps a fair share of marks: a near-empty one read as too
            // sparse. Marks are sized to their cell and one that would touch a
            // neighbour of higher priority is left out, decided from hashes alone (MarshMark), so
            // the sheet and every tile agree; marks bigger than their cell with a jitter of nearly
            // a cell piled up into knots.
            float marsh = MarshStep * 1.5f * m_g;
            // Close-ups draw the marks a little finer: at the tile's pen the small tufts closed
            // up into lumps. The sheet keeps the full Pen.
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
            // read as a flat grey blanket.
            // Denser by the lava's edge, thinning out on the open plain, as the classic map
            // stipples its blasted plains. One even dot: cinder ticks and fissure pairs made the
            // ground look doodled.
            Scatter(4.5f * m_g, 12, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_shore < 2f || s.m_lava) return;
                float near = Mathf.Clamp01(s.m_lavaLevel / 0.5f);
                if (!(s.m_roll < Mathf.Lerp(0.15f, 0.65f, near))) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
            });
            // Deep North: a near-blank snowfield, as the classic map leaves its northern waste. Its
            // only marks are chevrons of pressure ridges, gathered into thin winding bands (the
            // crest lines of a ridged noise) with open snow between, rather than sprinkled evenly.
            // Each chevron keeps clear of the water, of the peaks and of the other chevrons: ones
            // touching a shore, a peak's foot or each other read as clutter.
            float ridgeStep = 8f * m_g;
            Scatter(ridgeStep, 15, s =>
            {
                if (!s.m_isLand || s.m_land != Land.DeepNorth || s.m_above >= DeepNorthPeakHeight || s.m_shore < RidgeShore) return;
                if (!IsRidge(s.m_x, s.m_y) || !(s.m_roll < 0.8f)) return;
                if (RidgeCrowded(s, ridgeStep) || NearHighGround(s.m_x, s.m_y)) return;
                m_canvas.Stroke(s.m_x - 2.4f * m_g, s.m_y, s.m_x, s.m_y - 2f * m_g, Pen);
                m_canvas.Stroke(s.m_x, s.m_y - 2f * m_g, s.m_x + 2.4f * m_g, s.m_y, Pen);
            });
        }

        // Whether a point lies on a crest line of the pressure ridges' noise.
        private bool IsRidge(float x, float y) =>
            1f - Math.Abs(2f * Noise(x / RidgeScale, y / RidgeScale, m_world.m_seed + 9) - 1f) >= RidgeBand;

        // Whether a neighbouring cell of the chevron grid could put a chevron of higher priority
        // within RidgeGap of this one. Decided from the grid's hashes and the ridge noise alone,
        // without the ground, so the sheet and every tile agree; a neighbour off the snow can still
        // make a chevron give way, which only thins the bands' ends.
        private bool RidgeCrowded(Spot s, float step)
        {
            float halfWidth = 2.4f * m_g, height = 2f * m_g, gap = RidgeGap * m_g;
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
                    if (Math.Abs(x - s.m_x) < 2f * halfWidth + gap && Math.Abs(y - s.m_y) < height + gap && IsRidge(x, y)) return true;
                }
            }
            return false;
        }

        // Whether high ground that carries peaks lies where one of them could stand over or beside
        // a mark at (x, y): a peak rises north of its foot, so the search reaches far south and only
        // a little north. Read from the world's own heights, the same on the sheet and every tile.
        private bool NearHighGround(float x, float y)
        {
            for (float dy = -3f * G; dy <= 22f * G; dy += 2.5f * G)
            {
                for (float dx = -13f * G; dx <= 13f * G; dx += 2.5f * G)
                {
                    float wx = (x + dx) * MetresPerPixel - HalfExtent, wz = HalfExtent - (y + dy) * MetresPerPixel;
                    float above = m_world.HeightAt(wx, wz) - m_world.m_waterLevel;
                    if (above < DeepNorthPeakHeight) continue;
                    Land land = m_world.m_biome[m_world.Index(wx, wz)];
                    if ((land == Land.Mountain && above >= PeakHeight) || land == Land.DeepNorth) return true;
                }
            }
            return false;
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
            // raised, with a little noise so they do not trace the pools exactly; noise alone made
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
            for (int z = ProminenceCells; z < size - ProminenceCells; z++)
            {
                for (int x = ProminenceCells; x < size - ProminenceCells; x++)
                {
                    int i = z * size + x;
                    float above = h[i] - water;
                    if (above < 3f) continue;
                    Land land = m_world.m_biome[i];
                    bool high = (land == Land.Mountain && above >= PeakHeight) || (land == Land.DeepNorth && above >= DeepNorthPeakHeight);
                    bool hillCountry = ((land == Land.Meadows || land == Land.Plains) && m_world.m_wood[i] < WoodEdge)
                        || (land == Land.Mountain && above < PeakHeight);
                    if (land == Land.Mistlands)
                    {
                        PlaceSummitSpire(h, size, x, z);
                        continue;
                    }
                    if (!high && !hillCountry) continue;
                    int radius = high ? SummitCells : HilltopCells;
                    if (!IsHighest(h, size, x, z, radius)) continue;
                    float prominence = h[i] - Lowest(h, size, x, z, ProminenceCells);

                    float wx = (x - size / 2 + 0.5f) * m_world.m_pixelSize;
                    float wz = (z - size / 2 + 0.5f) * m_world.m_pixelSize;
                    float cx = (wx + HalfExtent) / MetresPerPixel, cy = (HalfExtent - wz) / MetresPerPixel;
                    float dx = cx - CanvasSize * 0.5f, dy = cy - CanvasSize * 0.5f;
                    float edge = EdgeRadius / MetresPerPixel * EdgeFraction;
                    if (dx * dx + dy * dy > edge * edge || !IsLandAt(cx, cy)) continue;
                    int seed = HashInt(x, z, m_world.m_seed + 77);

                    if (high)
                    {
                        if (prominence < SummitProminence) continue;
                        float width = Mathf.Lerp(22f, 34f, Mathf.Clamp01((prominence - SummitProminence) / 80f)) * G;
                        // The peak glyph rises from its foot to an apex offset a little east of the
                        // foot; place the foot so the apex lands on the summit.
                        float height = PeakHeightOf(width, seed);
                        float footX = cx - width * Unit(seed, 11) * 0.12f, footY = cy + height;
                        if (!PeakOnLand(footX, footY, width, height)) continue;
                        m_glyphs.Add(new Glyph { m_x = footX, m_y = footY, m_size = width, m_kind = Kind.Peak, m_seed = seed });
                        m_summits.Add(new Vector3(cx, cy, width));
                    }
                    else
                    {
                        if (prominence < HillProminence) continue;
                        float width = Mathf.Lerp(9f, 15f, Mathf.Clamp01((prominence - HillProminence) / 25f)) * G;
                        float height = width * 0.42f * Vary(seed, 10, 0.15f);
                        int foot = PixelAt(cx, cy + height);
                        if (m_landDistance[foot] < width * 0.5f || m_woodDistance[foot] < width * 0.5f) continue;
                        m_glyphs.Add(new Glyph { m_x = cx, m_y = cy + height, m_size = width, m_kind = Kind.Hill, m_seed = seed });
                    }
                }
            }
        }

        // A Mistlands crag on a real summit, sized by its prominence, the foot set so the apex of
        // its pinnacle lands on the summit, as peaks are placed.
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
            float s = Mathf.Lerp(1.1f, 1.6f, Mathf.Clamp01((prominence - SpireProminence) / 60f)) * G;
            float height = SpireHeightOf(s, seed);
            float footY = cy + height;
            if (!IsLandColumn(cx, footY, height) || m_landDistance[PixelAt(cx, footY)] < 4f) return;
            m_glyphs.Add(new Glyph { m_x = cx, m_y = footY, m_size = s, m_kind = Kind.Spire, m_seed = seed });
            m_spires.Add(new Vector3(cx, footY, height * 0.35f));
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

        // Whether a filler peak at this foot would crowd a real summit's glyph.
        private bool NearSummit(float x, float y, float width)
        {
            foreach (Vector3 summit in m_summits)
            {
                float reach = (summit.z + width) * SummitClearance;
                float dx = x - summit.x, dy = y - (summit.y + summit.z * 0.6f);
                if (dx * dx + dy * dy < reach * reach) return true;
            }
            return false;
        }

        // Woods, one tree family per biome, only inside the woods, so every wood is one clearly
        // bounded mass. Crowns are packed so tightly that only the southern edge shows trunks, the
        // rest hidden behind the row in front. Sizes are in the level of detail's units, so a
        // detail tile packs more, smaller trees.
        private void PlaceTrees()
        {
            // Meadows: egg-shaped broadleaf crowns.
            Scatter(5.2f * m_t, 23, s =>
            {
                if (!InWood(s, Land.Meadows, 8f * m_t) || !(s.m_roll < 0.95f)) return;
                Add(s, Kind.Broadleaf, m_t);
            });
            // Black Forest: dense pines.
            Scatter(5.5f * m_t, 24, s =>
            {
                if (!InWood(s, Land.BlackForest, 11f * m_t) || !(s.m_roll < 0.95f)) return;
                Add(s, Kind.Conifer, m_t);
            });
            // Plains: copses of small round trees where vanilla grows its sparse woods.
            Scatter(5f * m_t, 25, s =>
            {
                if (!InWood(s, Land.Plains, 6f * m_t) || !(s.m_roll < 0.9f)) return;
                Add(s, Kind.PlainsTree, m_t);
            });
            // Mistlands: gnarled trees packed closely enough that their canopies mass into one dark
            // wood, as the Black Forest's pines do, off the steep ground where the spires stand.
            Scatter(7f * m_t, 29, s =>
            {
                if (s.m_slope >= SpireSlope || !InWood(s, Land.Mistlands, 13f * m_t) || !(s.m_roll < 0.9f)) return;
                Add(s, Kind.GiantTree, m_t);
            });
        }

        private void PlaceGlyphs()
        {
            PlaceSummitsAndHills();
            // Mountains between the summits: staggered rows of smaller overlapping peaks, so a range
            // still reads as one mass while its real summits stand out above it. In the Mountain
            // biome and the high Deep North.
            Scatter(16f * G, 9f * G, 0.3f, 0.5f, 21, s =>
            {
                if (!s.m_isLand || !(s.m_roll < 0.97f)) return;
                bool mountain = s.m_land == Land.Mountain && s.m_above >= PeakHeight;
                bool north = s.m_land == Land.DeepNorth && s.m_above >= DeepNorthPeakHeight;
                if (!mountain && !north) return;
                float width = Mathf.Lerp(14f, 22f, Mathf.Clamp01((s.m_above - PeakHeight) / 160f)) * G;
                if (s.m_shore < width * 0.45f || !PeakOnLand(s.m_x, s.m_y, width, PeakHeightOf(width, s.m_seed))) return;
                if (NearSummit(s.m_x, s.m_y, width)) return;
                Add(s, Kind.Peak, width);
            });
            PlaceTrees();
            PlaceMist();
            // Swamp: a few tall ancient dead trees among the marsh lines, standing clear of water.
            Scatter(30f * T, 27, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 4f || !(s.m_roll < 0.45f)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 14f * T)) return;
                Add(s, Kind.DeadTree, T);
            });
            // Mistlands: crags on the real summits (PlaceSummitSpire) and filler crags on the steep,
            // broken ground only, clear of the summits; high flat plateaus are not crags.
            Scatter(18f * G, 28, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 5f || !(s.m_roll < 0.35f)) return;
                if (s.m_slope < SpireSlope * 1.3f || NearSummitSpire(s.m_x, s.m_y, 7f * G)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 22f * G)) return;
                Add(s, Kind.Spire, G);
            });
            // Ashlands: smoking volcanoes on the heights, and clusters of jagged crags on the
            // broken ground between the lava fields.
            // Volcanoes keep a cone's width apart and clear of the coast, so they never overlap
            // each other or reach into the sea.
            Scatter(24f * G, 30, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_lava || s.m_above < VolcanoHeight || !(s.m_roll < 0.7f)) return;
                float width = 14f * VolcanoScale * G;
                if (s.m_shore < width * 0.6f || !PeakOnLand(s.m_x, s.m_y, width, width * 0.55f)) return;
                if (!ClearOfLava(s.m_x, s.m_y, width)) return;
                foreach (Vector2 other in m_volcanoes)
                    if ((other - new Vector2(s.m_x, s.m_y)).sqrMagnitude < width * width) return;
                m_volcanoes.Add(new Vector2(s.m_x, s.m_y));
                Add(s, Kind.Volcano, VolcanoScale * G);
            });
            // Crag ridges, never standing on lava: both ends of the ridge are tested as well.
            Scatter(18f * G, 31, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_lava || s.m_shore < 6f * G) return;
                if (!(s.m_roll < (s.m_slope >= CragSlope ? 0.7f : 0.1f))) return;
                // The crag group (Glyphs.Crag) is up to about 1.7 of its front peak's width across,
                // reaching west of the foot; tested as one wide peak centred a little west.
                float width = 6.9f * CragScale * G;
                float reach = 9f * G;
                if (!ClearOfLava(s.m_x - width * 0.3f, s.m_y, width * 1.7f)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 10f * G) || !IsLandAt(s.m_x - reach, s.m_y) || !IsLandAt(s.m_x + reach, s.m_y)) return;
                Add(s, Kind.Crag, CragScale * G);
            });
            // Sea: ice floes in the northern sea. The open sea is otherwise left blank, as on the
            // classic map; wave marks far out read as birds.
            Scatter(15f * G, 33, s =>
            {
                if (m_sea[s.m_pixel] == 0 || s.m_water < 4f * G || !(s.m_roll < 0.5f)) return;
                if (!WorldGenerator.IsDeepnorth(s.m_worldX, s.m_worldZ)) return;
                m_floes.Add(new Glyph { m_x = s.m_x, m_y = s.m_y, m_size = G, m_kind = Kind.IceFloe, m_seed = s.m_seed });
            });
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
                    // One pen for every shore, sea or inland: a lighter one for lakes left their
                    // outlines reading as unexplained lines, and the water's blue now tells them.
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
        // read as heavy padding. Banks lie in the valleys and thin out with height, as the game's
        // mist clears on the heights, and keep off steep crag ground (Mistlands review).
        private void PlaceMist()
        {
            Scatter(30f * G, 12f * G, 0.5f, 0.5f, 13, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 6f || s.m_slope >= SpireSlope * 1.3f) return;
                if (!(s.m_roll < Mathf.Lerp(0.9f, 0.35f, Mathf.Clamp01(s.m_above / MistCeiling)))) return;
                m_glyphs.Add(new Glyph { m_x = s.m_x, m_y = s.m_y + MistGap, m_size = G, m_kind = Kind.Mist, m_seed = s.m_seed });
            });
        }

        // A bank centred on (x, y).
        private void MistBank(float x, float y, int seed)
        {
            float length = Mathf.Lerp(26f, 40f, Unit(seed, 5)) * G;
            float tilt = (Unit(seed, 6) - 0.5f) * 0.06f;
            float phase = Unit(seed, 4) * 6.28f;
            float left = x - length * 0.5f;
            MistLine(left, y - MistGap, length * 0.2f, length * 0.8f, length, tilt, phase, seed, 0);
            MistLine(left, y, 0f, length, length, tilt, phase + 0.6f, seed, 1);
            MistLine(left, y + MistGap, length * 0.25f, length * 0.85f, length, tilt, phase + 1.2f, seed, 2);
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
                var point = new Vector2(left + t, y + t * tilt + Mathf.Sin(t / length * 5f + phase) * 0.6f * G);
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
                        && m_landDistance[PixelAt(point.x, point.y)] >= RiverShoreClear);
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

        // Wave accents, as the classic hand-inked maps shade their coasts: small groups of level
        // tildes set at chosen points of the shore rather than an even band (
        // after rows that followed the coast "looked even dumber"; the reference's strokes are
        // strictly horizontal). Points along the coast are scored by how the shore shapes the water
        // there: an enclosed bay, inlet or strait (much land round it), the water off a cape (little
        // land round it) and river mouths score high, a straight open coast low. The best points
        // get a group of two to four strokes stacked away from the shore, each row shorter and
        // shifted, and keep about WaveGroupGap from other groups; straight coasts get a lone
        // stroke now and then, and the open sea a sparse scatter of two- and three-stroke groups
        // (mostly by the coasts, some further out). No stroke
        // touches land, a floe or another. Drawn on the sheet only, in a fixed order, so the
        // result is deterministic; close-ups replay it.
        private void DrawWaveAccents()
        {
            int n = CanvasSize;
            // One-pixel cells, kept sparse: rows of a group stand under three pixels apart, which
            // a coarser grid could not tell from touching.
            var taken = new HashSet<int>();
            float centre = n * 0.5f, limit = EdgeRadius / MetresPerPixel * EdgeFraction;
            int salt = unchecked(m_world.m_seed + 34 * 7919);
            // Floes are inked over the waves and erase under them; keeping the waves off them
            // spares half-erased strokes.
            foreach (Glyph floe in m_floes)
            {
                float size = 5.5f * floe.m_size + 1.5f;
                Footprint(taken, n, new Vector2(floe.m_x - size, floe.m_y), new Vector2(floe.m_x + size, floe.m_y), size * 0.7f, true);
            }

            // Candidate points just off the shore, scored.
            var spots = new List<WaveSpot>();
            int grid = (int)(n / WaveSpotStep);
            for (int gy = 0; gy < grid; gy++)
            {
                if ((gy & 63) == 0) m_cancel.ThrowIfCancellationRequested();
                for (int gx = 0; gx < grid; gx++)
                {
                    int seed = HashInt(gx, gy, salt);
                    float x = (gx + 0.5f + (Unit(seed, 1) - 0.5f) * 0.6f) * WaveSpotStep;
                    float y = (gy + 0.5f + (Unit(seed, 2) - 0.5f) * 0.6f) * WaveSpotStep;
                    int pixel = PixelAt(x, y);
                    if (m_land[pixel] != 0 || m_sea[pixel] == 0) continue;
                    float d = m_waterDistance[pixel] / DistanceScale;
                    if (d < WaveFirstRow - 0.8f || d > WaveFirstRow + 2.5f) continue;
                    float dx = x - centre, dy = y - centre;
                    if (dx * dx + dy * dy > limit * limit) continue;
                    float score = ShoreScore(x, y);
                    if (NearRiverMouth(x, y)) score = Mathf.Max(score, 0.8f);
                    // Shores facing east or west carry the strokes that start at the coast.
                    if (ShoreBeside(x, y, out _, out _)) score += WaveBesideBonus;
                    spots.Add(new WaveSpot { m_x = x, m_y = y, m_score = score + Unit(seed, 3) * 0.05f, m_seed = seed, m_order = spots.Count });
                }
            }
            spots.Sort((a, b) => a.m_score != b.m_score ? b.m_score.CompareTo(a.m_score) : a.m_order.CompareTo(b.m_order));

            var groups = new List<Vector2>();
            foreach (WaveSpot spot in spots)
            {
                bool accent = spot.m_score >= WaveAccent;
                // Straight coasts: a lone stroke now and then, kept further apart.
                if (!accent && !(Unit(spot.m_seed, 4) < WaveLone)) continue;
                float gap = (accent ? WaveGroupGap : WaveGroupGap * 1.6f) * Vary(spot.m_seed, 5, WaveGapJitter);
                if (Crowded(groups, spot.m_x, spot.m_y, gap)) continue;
                bool islet = m_majorDistance[PixelAt(spot.m_x, spot.m_y)] / DistanceScale > m_waterDistance[PixelAt(spot.m_x, spot.m_y)] / DistanceScale + 2f;
                int strokes = accent ? 2 + Mathf.Min(2, (int)((spot.m_score - WaveAccent) / (1f - WaveAccent) * 3f)) : 1;
                if (islet) strokes = Mathf.Min(strokes, 2);
                if (DrawWaveGroup(spot, strokes, taken, n) > 0) groups.Add(new Vector2(spot.m_x, spot.m_y));
            }

            // Open water, after the coasts so their groups keep first claim.
            int open = (int)(n / WaveOpenStep);
            for (int gy = 0; gy < open; gy++)
            {
                for (int gx = 0; gx < open; gx++)
                {
                    int seed = HashInt(gx, gy, salt + 7);
                    // Broad noise thins the scatter to a fifth over some stretches of sea and
                    // lets it gather in others, so it never reads as a sprinkled grid.
                    float drift = Mathf.Lerp(0.2f, 1f, Mathf.Clamp01((Noise(gx / WaveOpenDrift, gy / WaveOpenDrift, salt + 8) - 0.25f) / 0.5f));
                    if (!(Unit(seed, 0) < WaveOpenChance * drift)) continue;
                    float x = (gx + 0.15f + Unit(seed, 1) * 0.7f) * WaveOpenStep;
                    float y = (gy + 0.15f + Unit(seed, 2) * 0.7f) * WaveOpenStep;
                    float dx = x - centre, dy = y - centre;
                    if (dx * dx + dy * dy > limit * limit) continue;
                    var p = new Vector2(x, y);
                    if (!OnSea(p) || WaterAt(p) < WaveOpenShore) continue;
                    if (Crowded(groups, x, y, WaveOpenGap * Vary(seed, 5, WaveGapJitter))) continue;
                    var spot = new WaveSpot { m_x = x, m_y = y, m_seed = seed };
                    if (DrawWaveGroup(spot, Unit(seed, 6) < 0.3f ? 3 : 2, taken, n) > 0) groups.Add(p);
                }
            }
        }

        private static bool Crowded(List<Vector2> groups, float x, float y, float gap)
        {
            var p = new Vector2(x, y);
            foreach (Vector2 other in groups)
                if ((other - p).sqrMagnitude < gap * gap) return true;
            return false;
        }

        private struct WaveSpot
        {
            public float m_x, m_y, m_score;
            public int m_seed, m_order;
        }

        // How strongly the shore shapes the water at (x, y), 0 to 1: the share of land on a disc
        // of WaveShapeRadius round it, which is about half by a straight coast, more in a bay,
        // inlet or strait and less off a cape; both ends score high.
        private float ShoreScore(float x, float y)
        {
            int land = 0, total = 0;
            for (int k = 0; k < 16; k++)
            {
                float a = k * Mathf.PI / 8f;
                float c = Mathf.Cos(a), s = Mathf.Sin(a);
                for (int r = 1; r <= 3; r++)
                {
                    float radius = WaveShapeRadius * r / 3f;
                    total++;
                    if (m_land[PixelAt(x + c * radius, y + s * radius)] != 0) land++;
                }
            }
            float share = land / (float)total;
            return Mathf.Clamp01(Mathf.Max((share - 0.45f) / 0.3f, (0.38f - share) / 0.25f));
        }

        private bool NearRiverMouth(float x, float y)
        {
            if (m_riverMask == null) return false;
            for (int k = 0; k < 8; k++)
            {
                float a = k * Mathf.PI / 4f;
                if (m_riverMask[PixelAt(x + Mathf.Cos(a) * 6f, y + Mathf.Sin(a) * 6f)] != 0) return true;
            }
            return false;
        }

        // A stack of level tildes from the spot away from the shore, each row WaveRowGap further
        // out, shorter and shifted a little; a row that would touch land or another wave is
        // nudged sideways, or skipped. Returns how many were inked.
        private int DrawWaveGroup(WaveSpot spot, int strokes, HashSet<int> taken, int n)
        {
            // Away from the shore, up or down the sheet as the water opens.
            float up = WaterAt(new Vector2(spot.m_x, spot.m_y - 4f)), down = WaterAt(new Vector2(spot.m_x, spot.m_y + 4f));
            float step = down >= up ? WaveRowGap : -WaveRowGap;
            // Sideways, toward the open water, for nudges.
            float east = WaterAt(new Vector2(spot.m_x + 4f, spot.m_y)), west = WaterAt(new Vector2(spot.m_x - 4f, spot.m_y));
            float side = east >= west ? 1f : -1f;
            var dir = new Vector2(1f, 0f);
            int inked = 0;
            float y = spot.m_y;
            for (int k = 0; k < strokes; k++)
            {
                // Two or three crests, the first row the longest; one-crest strokes read as
                // commas, not water.
                int crests = k == 0 && Unit(spot.m_seed, 10) < 0.6f ? 3 : 2;
                float length = crests * WaveCrest * Vary(spot.m_seed, 20 + k, 0.08f);
                float shift = (Unit(spot.m_seed, 30 + k) - 0.5f) * WaveCrest * 1.4f;
                // Rows a little uneven, as a hand spaces them.
                if (k > 0) y += step * Vary(spot.m_seed, 40 + k, 0.12f);
                bool done = false;
                // Where the shore lies level with the row, the stroke starts right at it and runs
                // out to sea, the classic map's whole style.
                if (ShoreBeside(spot.m_x, y, out float shoreX, out float away))
                {
                    float start = shoreX + away * WaveShoreGap;
                    float end = start + away * length;
                    var a = new Vector2(Mathf.Min(start, end), y);
                    var b = new Vector2(Mathf.Max(start, end), y);
                    var far = new Vector2(end, y);
                    var mid = new Vector2((start + end) * 0.5f, y);
                    if (OnSea(a) && OnSea(b) && OnSea(mid) && WaterAt(far) >= WaveShoreClear && WaterAt(mid) >= WaveShoreClear
                        && !Footprint(taken, n, a - dir * 2f, b + dir * 2f, WaveKeepOff, false))
                    {
                        Footprint(taken, n, a, b, WaveInkHalf, true);
                        WaveStroke(a, dir, length, crests);
                        inked++;
                        done = true;
                    }
                }
                for (int nudge = 0; nudge < 4 && !done; nudge++)
                {
                    float cx = spot.m_x + shift + side * nudge * 2.5f;
                    var a = new Vector2(cx - length * 0.5f, y);
                    var b = new Vector2(cx + length * 0.5f, y);
                    if (!OnSea(a) || !OnSea(b) || !OnSea(new Vector2(cx, y))) continue;
                    if (Mathf.Min(WaterAt(a), Mathf.Min(WaterAt(b), WaterAt(new Vector2(cx, y)))) < WaveShoreClear) continue;
                    if (Footprint(taken, n, a - dir * 2.5f, b + dir * 2.5f, WaveKeepOff, false)) continue;
                    Footprint(taken, n, a, b, WaveInkHalf, true);
                    WaveStroke(a, dir, length, crests);
                    inked++;
                    done = true;
                }
                if (!done && k == 0) return 0;
            }
            return inked;
        }

        // The nearest land level with (x, y) within WaveAnchorReach to the west or east: the x of
        // the shore's water edge there, and the way out to sea (+1 east, -1 west).
        private bool ShoreBeside(float x, float y, out float shoreX, out float away)
        {
            for (int step = 1; step <= WaveAnchorReach; step++)
            {
                if (m_land[PixelAt(x - step, y)] != 0)
                {
                    shoreX = Mathf.Floor(x - step) + 1f;
                    away = 1f;
                    return true;
                }
                if (m_land[PixelAt(x + step, y)] != 0)
                {
                    shoreX = Mathf.Floor(x + step);
                    away = -1f;
                    return true;
                }
            }
            shoreX = x;
            away = 0f;
            return false;
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

        // A tilde from a along dir: crests that rise and dip by WaveLift, running out flat at both
        // ends, eight points a crest so close-ups show a curve, not a zigzag.
        private void WaveStroke(Vector2 a, Vector2 dir, float length, int crests)
        {
            var up = new Vector2(dir.y, -dir.x);
            m_points.Clear();
            int count = crests * 8;
            for (int i = 0; i <= count; i++)
            {
                float u = i / (float)count;
                float envelope = Mathf.Clamp01(Mathf.Sin(Mathf.PI * u) * 1.6f);
                float lift = Mathf.Sin(u * crests * Mathf.PI * 2f) * WaveLift * envelope;
                m_points.Add(a + dir * (u * length) + up * lift);
            }
            m_canvas.Polyline(m_points, Pen);
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
