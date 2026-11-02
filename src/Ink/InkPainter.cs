using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Inks the whole world once, on a worker thread, in the manner of a classic fantasy-novel map:
    /// a heavy coastline with two ripple lines on open water, and small pen glyphs for the land,
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

        // Coast and ripples, in canvas pixels. Ripples run only on the sea, and only where it is at
        // least OpenWidth wide on both sides of a point, so rivers and straits keep just their banks.
        private const float CoastWidth = 2.1f;
        // Ripple lines out from the coast, and for each the share of its length drawn (noise
        // threshold; 1 is unbroken). The first sits well off the shore, so small islands and narrow
        // channels stay readable.
        private static readonly float[] s_ripples = { 6f * G, 11f * G, 17f * G, 24f * G };
        private static readonly float[] s_rippleGaps = { 1f, 0.68f, 0.55f, 0.45f };
        // A ring needs sea RippleWidth times its distance from land within reach, and land in a box
        // RippleLandBox times its distance across of at least RippleLandShare times its distance
        // squared: a straight coast gives three times that, an islet of about 0.4 times the ring's
        // distance in radius just passes, smaller ones get no ring.
        private const float RippleWidth = 1.5f;
        private const float RippleLandBox = 1.5f;
        private const float RippleLandShare = 0.5f;
        private const float OpenWidth = 9f * G;
        private const float DistanceScale = 4f;
        private const float FrameGap = 7f;
        // Glyph feet stay inside this fraction of the frame's radius, clear of the inner ring.
        private const float EdgeFraction = 0.97f;
        // The thinnest pen line, in canvas pixels; thinner lines fade to grey at the opening zoom.
        private const float Pen = 1f;
        // Plains stipple dots, one pen width across.
        private const float StippleRadius = 0.6f;
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
        private const float HillSlope = 0.35f;
        // Mist banks: the gap between a bank's two lines, the parchment band scraped under each line
        // (canvas pixels, narrower than the gap so one line never scrapes the other) and the step
        // between points along a line.
        private const float MistGap = 2.6f * G;
        private const float MistBand = 3.4f;
        private const float MistStep = 1.5f * G;
        // Deep North pressure ridges: the scale of the noise their bands follow (canvas pixels) and
        // how close to a crest line a chevron must lie (1 is on the crest).
        private const float RidgeScale = 70f * G;
        private const float RidgeBand = 0.9f;
        // Rivers: the pen for a river, a stream at its mouth (thinning to Pen at its source), the extra
        // width scraped clear on either side, and the step between points along a course (canvas px).
        private const float RiverWidth = 1.8f;
        private const float StreamMouth = 1.4f;
        private const float RiverBand = 2.4f;
        private const float RiverStep = 1.5f;
        // River ink keeps this far (canvas pixels) from any water the coast already draws, or it
        // runs along a bank or a lake shore and the two lines merge into a smear.
        private const float RiverShoreClear = 2f;
        private const float SpireSlope = 0.45f;
        // Mistlands land this high above the water gets spires even where it is not steep
        // (the Velen world's Mistlands p90 is 47 m).
        private const float SpireHeight = 42f;
        private const float VolcanoHeight = 22f;
        // Volcanoes are drawn larger than other land glyphs, closer to the size of a peak.
        private const float VolcanoScale = 2.2f;

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
        private byte[] m_solid;
        private readonly List<Glyph> m_glyphs = new List<Glyph>(200000);
        // Real summits placed so far: apex position (x, y) and glyph width (z).
        private readonly List<Vector3> m_summits = new List<Vector3>(1024);
        // Ice floes sit on the water, so they are drawn after the coast's ripple lines, over them.
        private readonly List<Glyph> m_floes = new List<Glyph>(4096);
        private float[] m_coast;
        // Map pixels of water joined to the sea, and canvas pixels of sea.
        private bool[] m_seaCell;
        private byte[] m_sea;
        private byte[] m_land;
        private byte[] m_waterDistance;
        private byte[] m_landDistance;
        private byte[] m_openDistance;
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
            MarshTuft,
            DeadTree,
            Spire,
            GiantTree,
            Volcano,
            Fissure,
            Wave,
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
            return painter.m_canvas.m_ink;
        }

        private void Run()
        {
            BuildCoastHeights();
            m_cancel.ThrowIfCancellationRequested();
            BuildFields();
            m_cancel.ThrowIfCancellationRequested();
            m_canvas.m_layer = InkLayer.Background;
            DrawBackground();
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
                m_canvas.m_layer = IsTree(glyph.m_kind) ? InkLayer.Tree : InkLayer.Glyph;
                DrawGlyph(glyph);
            }
            m_canvas.m_layer = InkLayer.Mist;
            DrawMist();
            m_canvas.m_layer = InkLayer.Ripple;
            DrawRipples();
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
            m_openDistance = new byte[n * n];
            field.Compute(i => m_land[i] != 0, m_waterDistance, DistanceScale);
            m_cancel.ThrowIfCancellationRequested();
            field.Compute(i => m_land[i] == 0, m_landDistance, 1f);
            m_cancel.ThrowIfCancellationRequested();
            byte open = (byte)Math.Min(255f, OpenWidth * DistanceScale);
            field.Compute(i => m_land[i] == 0 && m_waterDistance[i] >= open, m_openDistance, 1f);
            m_cancel.ThrowIfCancellationRequested();
            m_woodDistance = new byte[n * n];
            field.Compute(i => m_wood[i] != 0, m_woodDistance, 1f);
        }

        private bool IsOpenWater(int pixel) => m_sea[pixel] != 0 && m_openDistance[pixel] <= OpenWidth + 1.5f;

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
            spot.m_lava = m_world.m_lava[source] > 127;
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
            // Swamp: small wavelets of standing water, with flat strokes of still water between.
            Scatter(8f * m_g, 11, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 3f) return;
                if (s.m_roll < 0.45f) Wavelet(s.m_x, s.m_y, m_g);
                else if (s.m_roll < 0.7f) m_canvas.Stroke(s.m_x - 1.5f * m_g, s.m_y, s.m_x + 1.5f * m_g, s.m_y, Pen);
            });
            // Plains: a stipple of fine dots, the dry heath's own tone.
            Scatter(5f * m_g, 16, s =>
            {
                if (!IsOpenLand(s, Land.Plains, 2f) || !(s.m_roll < 0.45f)) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius * m_pen);
            });
            // Ashlands: dense diagonal hatching darkens the burnt ground. Deliberately a grey tone.
            Scatter(5f * m_g, 12, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_shore < 2f || !(s.m_roll < 0.75f)) return;
                m_canvas.Stroke(s.m_x - 1.4f * m_g, s.m_y + 1.4f * m_g, s.m_x + 1.4f * m_g, s.m_y - 1.4f * m_g, 0.7f, 0.75f);
            });
            // Deep North: a near-blank snowfield, as the classic map leaves its northern waste. Its
            // only marks are chevrons of pressure ridges, gathered into thin winding bands (the
            // crest lines of a ridged noise) with open snow between, rather than sprinkled evenly.
            Scatter(8f * m_g, 15, s =>
            {
                if (!s.m_isLand || s.m_land != Land.DeepNorth || s.m_above >= DeepNorthPeakHeight || s.m_shore < 3f) return;
                float ridge = 1f - Math.Abs(2f * Noise(s.m_x / RidgeScale, s.m_y / RidgeScale, m_world.m_seed + 9) - 1f);
                if (ridge < RidgeBand || !(s.m_roll < 0.7f)) return;
                m_canvas.Stroke(s.m_x - 2.4f * m_g, s.m_y, s.m_x, s.m_y - 2f * m_g, Pen);
                m_canvas.Stroke(s.m_x, s.m_y - 2f * m_g, s.m_x + 2.4f * m_g, s.m_y, Pen);
            });
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
                        float height = width * 0.78f * Vary(seed, 10, 0.2f);
                        float footX = cx - width * Unit(seed, 11) * 0.12f, footY = cy + height;
                        if (!IsLandColumn(footX, footY, height)) continue;
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
            // Mistlands: giant trees packed closely enough to read as the biome's dark forest,
            // off the steep ground where the spires stand.
            Scatter(8f * m_t, 29, s =>
            {
                if (s.m_slope >= SpireSlope || !InWood(s, Land.Mistlands, 15f * m_t) || !(s.m_roll < 0.85f)) return;
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
                if (s.m_shore < width * 0.45f || !IsLandColumn(s.m_x, s.m_y, width * 0.75f)) return;
                if (NearSummit(s.m_x, s.m_y, width)) return;
                Add(s, Kind.Peak, width);
            });
            PlaceTrees();
            // Swamp: a few marsh tufts among the water strokes, and some dead trees.
            Scatter(11f * G, 26, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 3f || !(s.m_roll < 0.35f)) return;
                Add(s, Kind.MarshTuft, G);
            });
            Scatter(24f * T, 27, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 4f || !(s.m_roll < 0.35f)) return;
                Add(s, Kind.DeadTree, T);
            });
            // Mistlands: rock spires on the steep or high ground.
            Scatter(12f * G, 28, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 5f || !(s.m_roll < 0.8f)) return;
                if (s.m_slope < SpireSlope && s.m_above < SpireHeight) return;
                if (!IsLandColumn(s.m_x, s.m_y, 22f * G)) return;
                Add(s, Kind.Spire, G);
            });
            // Ashlands: a few large smoking volcanoes on the heights, lava cracks in the flows.
            Scatter(34f * G, 30, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_above < VolcanoHeight || s.m_shore < 10f * G || !(s.m_roll < 0.45f)) return;
                Add(s, Kind.Volcano, VolcanoScale * G);
            });
            Scatter(16f * G, 31, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || !s.m_lava || s.m_shore < 6f * G || !(s.m_roll < 0.55f)) return;
                Add(s, Kind.Fissure, G);
            });
            // Sea: a few wave marks far out, ice floes in the northern sea.
            Scatter(34f * G, 32, s =>
            {
                if (s.m_isLand || s.m_water < 24f * G || !IsOpenWater(s.m_pixel) || !(s.m_roll < 0.5f)) return;
                if (WorldGenerator.IsDeepnorth(s.m_worldX, s.m_worldZ)) return;
                Add(s, Kind.Wave, G);
            });
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

        // The Mistlands' fill. Unlike the other fills it lies on top of the glyphs: dashes laid
        // under the dense giant trees were scraped away by them and left the biome looking bare.
        // Each bank is a pair of long wavy lines drifting across the woods, each on a thin band
        // scraped back to parchment, so the mist reads as veiling the trees behind it.
        private void DrawMist()
        {
            Scatter(28f * G, 16f * G, 0.5f, 0.5f, 13, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 6f || !(s.m_roll < 0.8f)) return;
                float phase = Unit(s.m_seed, 4) * 6.28f;
                float length = Mathf.Lerp(30f, 44f, Unit(s.m_seed, 5)) * G;
                MistLine(s.m_x - length * 0.5f, s.m_y, 0f, length, length, phase);
                MistLine(s.m_x - length * 0.5f, s.m_y + MistGap, length * 0.2f, length * 0.85f, length, phase + 0.8f);
            });
        }

        // One wavy mist line from t = start to end along a bank of the given length, broken wherever
        // it leaves Mistlands land, so it never drifts over the sea or into another biome.
        // The whole line is scraped before any of it is inked; scraping segment by segment would
        // erase the end of the segment drawn just before.
        private void MistLine(float left, float y, float start, float end, float length, float phase)
        {
            m_points.Clear();
            m_pathInside.Clear();
            for (float t = start; t <= end; t += MistStep)
            {
                var point = new Vector2(left + t, y + Mathf.Sin(t / length * 7.5f + phase) * 1.2f * G);
                m_points.Add(point);
                m_pathInside.Add(IsMistlandsAt(point.x, point.y) && !IsSolidAt(point.x, point.y));
            }
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 1; i < m_points.Count; i++)
                {
                    if (!m_pathInside[i] || !m_pathInside[i - 1]) continue;
                    Vector2 a = m_points[i - 1], b = m_points[i];
                    if (pass == 0) m_canvas.Scrape(a.x, a.y, b.x, b.y, MistBand);
                    else m_canvas.Stroke(a.x, a.y, b.x, b.y, Pen);
                }
            }
        }

        // Marks a glyph's bounding box, grown by the mist band's half width, as off limits to mist.
        private void MarkSolid(float left, float top, float right, float bottom)
        {
            if (m_solid == null) m_solid = new byte[CanvasSize * CanvasSize];
            float grow = MistBand * 0.5f + 1f;
            int x0 = Mathf.Clamp((int)(left - grow), 0, CanvasSize - 1), x1 = Mathf.Clamp((int)(right + grow), 0, CanvasSize - 1);
            int y0 = Mathf.Clamp((int)(top - grow), 0, CanvasSize - 1), y1 = Mathf.Clamp((int)(bottom + grow), 0, CanvasSize - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    m_solid[y * CanvasSize + x] = 1;
        }

        private bool IsSolidAt(float x, float y)
        {
            if (m_solid == null) return false;
            int px = Mathf.Clamp((int)x, 0, CanvasSize - 1), py = Mathf.Clamp((int)y, 0, CanvasSize - 1);
            return m_solid[py * CanvasSize + px] != 0;
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

        private void DrawRipples()
        {
            int n = CanvasSize;
            float edge = EdgeRadius / MetresPerPixel - 12f;
            float centre = n * 0.5f;
            int[] landSum = LandIntegral();
            var field = new DistanceField(n);
            var deepReach = new byte[n * n];
            Func<int, int, float> distance = (x, y) => m_waterDistance[y * n + x] / DistanceScale;
            // Distances run to the centre of the nearest land pixel; the coast lies half a pixel out.
            // Each line further out breaks off more often, as if the pen ran dry, so the ripples fade
            // with distance while every stroke stays full ink.
            int seed = m_world.m_seed;
            for (int i = 0; i < s_ripples.Length; i++)
            {
                m_cancel.ThrowIfCancellationRequested();
                float ring = s_ripples[i];
                byte deep = (byte)Math.Min(255f, ring * RippleWidth * DistanceScale);
                field.Compute(j => m_sea[j] != 0 && m_waterDistance[j] >= deep, deepReach, 1f);
                float reach = ring * (RippleWidth - 1f) + 2.5f;
                int box = (int)(ring * RippleLandBox);
                float landNeeded = RippleLandShare * ring * ring;
                Func<int, int, bool> skip = (x, y) =>
                {
                    float dx = x + 1f - centre, dy = y + 1f - centre;
                    if (dx * dx + dy * dy > edge * edge || m_sea[y * n + x] == 0) return true;
                    if (deepReach[y * n + x] > reach) return true;
                    return LandInBox(landSum, x, y, box) < landNeeded;
                };
                float gaps = s_rippleGaps[i];
                int salt = seed + 5 + i;
                DistanceField.Contour(n, distance, ring + 0.5f, skip, (a, b) =>
                {
                    if (gaps < 1f && Noise(a.x / 18f, a.y / 18f, salt) > gaps) return;
                    m_canvas.Stroke(a.x + 0.5f, a.y + 0.5f, b.x + 0.5f, b.y + 0.5f, Pen);
                });
            }
        }

        // Summed-area table of the land mask, (n + 1)Â² entries.
        private int[] LandIntegral()
        {
            int n = CanvasSize, stride = n + 1;
            var sum = new int[stride * stride];
            for (int y = 1; y <= n; y++)
            {
                int row = 0;
                for (int x = 1; x <= n; x++)
                {
                    row += m_land[(y - 1) * n + x - 1];
                    sum[y * stride + x] = sum[(y - 1) * stride + x] + row;
                }
            }
            return sum;
        }

        private static int LandInBox(int[] sum, int x, int y, int half)
        {
            int n = CanvasSize, stride = n + 1;
            int x0 = Math.Max(0, x - half), y0 = Math.Max(0, y - half);
            int x1 = Math.Min(n, x + half + 1), y1 = Math.Min(n, y + half + 1);
            return sum[y1 * stride + x1] - sum[y0 * stride + x1] - sum[y1 * stride + x0] + sum[y0 * stride + x0];
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
