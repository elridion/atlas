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

        // Coast and ripples, in canvas pixels. Ripples only run along water at least OpenWidth wide
        // on both sides of a point, so rivers and narrow straits keep just their two banks.
        private const float CoastWidth = 2.1f;
        // Ripple lines out from the coast, and for each the share of its length drawn (noise
        // threshold; 1 is unbroken).
        private static readonly float[] s_ripples = { 4.5f * G, 8.5f * G, 13f * G, 18f * G };
        private static readonly float[] s_rippleGaps = { 1f, 0.68f, 0.55f, 0.45f };
        // A ring needs water RippleWidth times its distance from land within reach, and (beyond the
        // first) land in a box RippleLandBox times its distance across of at least RippleLandShare
        // times its distance squared: a straight coast gives three times that, an islet of about 0.4
        // times the ring's distance in radius just passes.
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
        // Coast clean-up: swamp water shallower than SwampDeep metres counts as land, and islands
        // or enclosed lakes smaller than these many map pixels (12 m each) are not drawn.
        private const float SwampDeep = 3f;
        private const int MinIsland = 12;
        private const int MinLake = 16;

        // Terrain thresholds: metres above the water and height change per metre of slope.
        private const float PeakHeight = 50f;
        private const float DeepNorthPeakHeight = 45f;
        private const float HillSlope = 0.35f;
        // Around a range, gentler ground already counts as foothills once this high above the water.
        private const float FoothillSlope = 0.25f;
        private const float FoothillHeight = 25f;
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
        private const float SpireSlope = 0.45f;
        // Mistlands land this high above the water gets spires even where it is not steep
        // (the Velen world's Mistlands p90 is 47 m).
        private const float SpireHeight = 42f;
        private const float VolcanoHeight = 22f;
        // Volcanoes are drawn larger than other land glyphs, closer to the size of a peak.
        private const float VolcanoScale = 2.2f;

        private readonly WorldSource m_world;
        private readonly CancellationToken m_cancel;
        private readonly InkCanvas m_canvas = new InkCanvas(CanvasSize);
        private readonly List<Vector2> m_points = new List<Vector2>(16);
        private readonly List<bool> m_pathInside = new List<bool>(32);
        // Canvas pixels covered by peaks and spires, which the mist banks drawn later must not cut.
        private byte[] m_solid;
        private readonly List<Glyph> m_glyphs = new List<Glyph>(200000);
        // Ice floes sit on the water, so they are drawn after the coast's ripple lines, over them.
        private readonly List<Glyph> m_floes = new List<Glyph>(4096);
        private float[] m_coast;
        private byte[] m_land;
        private byte[] m_waterDistance;
        private byte[] m_landDistance;
        private byte[] m_openDistance;

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
            public bool m_isLand, m_forest, m_mist, m_lava;
            public float m_shore, m_water;
        }

        private InkPainter(WorldSource world, CancellationToken cancel)
        {
            m_world = world;
            m_cancel = cancel;
        }

        // Throws OperationCanceledException between stages once cancel is set, so a drawing for a
        // world the player already left stops instead of holding its memory to the end.
        public static byte[] Paint(WorldSource world, CancellationToken cancel)
        {
            var painter = new InkPainter(world, cancel);
            painter.Run();
            return painter.m_canvas.m_ink;
        }

        private void Run()
        {
            BuildCoastHeights();
            m_cancel.ThrowIfCancellationRequested();
            BuildFields();
            m_cancel.ThrowIfCancellationRequested();
            DrawBackground();
            PlaceGlyphs();
            m_cancel.ThrowIfCancellationRequested();
            m_glyphs.Sort((a, b) => a.m_y.CompareTo(b.m_y));
            foreach (Glyph glyph in m_glyphs) DrawGlyph(glyph);
            DrawMist();
            DrawRivers();
            m_cancel.ThrowIfCancellationRequested();
            DrawCoast();
            DrawRipples();
            foreach (Glyph floe in m_floes) DrawGlyph(floe);
            DrawFrame();
        }

        // The height field the coastline is traced on: vanilla's heights, lightly smoothed, with the
        // swamp's standing water counted as land and specks of land or water too small to draw
        // removed. Raw heights hover at the water line in swamps and along rocky shores, which
        // otherwise traces a thicket of tiny islands and ponds.
        private void BuildCoastHeights()
        {
            int size = m_world.m_size;
            float water = m_world.m_waterLevel;
            float[] raw = m_world.m_height;
            var heights = new float[size * size];
            for (int i = 0; i < raw.Length; i++)
            {
                float h = raw[i];
                if (m_world.m_biome[i] == Land.Swamp && h > water - SwampDeep) h = Math.Max(h, water + 0.3f);
                heights[i] = h;
            }

            // Two blurs before the specks go and one after: one pass left small islands as angular
            // lozenges and the swamp's flat water level as straight chords along the coast.
            m_coast = new float[size * size];
            Blur(heights, m_coast, size);
            Blur(m_coast, heights, size);
            Array.Copy(heights, m_coast, heights.Length);
            RemoveSpecks(water, land: true, MinIsland);
            RemoveSpecks(water, land: false, MinLake);
            Blur(m_coast, heights, size);
            m_coast = heights;
        }

        // 3x3 box blur of a square grid, edges averaged over the cells that exist.
        private static void Blur(float[] source, float[] target, int size)
        {
            for (int z = 0; z < size; z++)
            {
                for (int x = 0; x < size; x++)
                {
                    float sum = 0f;
                    int count = 0;
                    for (int dz = -1; dz <= 1; dz++)
                    {
                        int zz = z + dz;
                        if (zz < 0 || zz >= size) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int xx = x + dx;
                            if (xx < 0 || xx >= size) continue;
                            sum += source[zz * size + xx];
                            count++;
                        }
                    }
                    target[z * size + x] = sum / count;
                }
            }
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

        private void BuildFields()
        {
            int n = CanvasSize;
            m_land = new byte[n * n];
            float edgeSqr = EdgeRadius * EdgeRadius;
            for (int y = 0; y < n; y++)
            {
                float wz = HalfExtent - (y + 0.5f) * MetresPerPixel;
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * MetresPerPixel - HalfExtent;
                    if (wx * wx + wz * wz >= edgeSqr) continue;
                    if (m_world.Sample(m_coast, wx, wz) > m_world.m_waterLevel) m_land[y * n + x] = 1;
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
        }

        private bool IsOpenWater(int pixel) => m_land[pixel] == 0 && m_openDistance[pixel] <= OpenWidth + 1.5f;

        private Spot Look(float x, float y)
        {
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
            spot.m_forest = m_world.m_forest[source] > 127;
            spot.m_mist = m_world.m_mist[source] > 127;
            spot.m_lava = m_world.m_lava[source] > 127;
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
            int columns = (int)(CanvasSize / spacingX);
            int rows = (int)(CanvasSize / spacingY);
            float edge = EdgeRadius / MetresPerPixel;
            float centre = CanvasSize * 0.5f;
            int worldSeed = unchecked(m_world.m_seed + salt * 7919);
            for (int gy = 0; gy < rows; gy++)
            {
                for (int gx = 0; gx < columns; gx++)
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

        private void Add(Spot spot, Kind kind, float size)
        {
            m_glyphs.Add(new Glyph { m_x = spot.m_x, m_y = spot.m_y, m_size = size, m_kind = kind, m_seed = spot.m_seed });
        }

        private static float Unit(int seed, int k) => Atlas.Noise.Unit(seed, k);

        private static int HashInt(int x, int y, int seed) => Atlas.Noise.HashInt(x, y, seed);

        // Every biome but the mountains and the near-blank Deep North has a ground fill of its own,
        // laid over all of its land, woods included, so biomes can be told apart by the fill alone;
        // trees and other glyphs are drawn on top and scrape the fill away behind their bodies.
        // Every pen line is at least Pen wide and full strength: thinner or fainter lines average
        // away to grey at the opening zoom.
        private void DrawBackground()
        {
            // Meadows: short tufts of grass.
            Scatter(12f * G, 14, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Meadows || s.m_shore < 3f || !(s.m_roll < 0.45f)) return;
                Grass(s.m_x, s.m_y, G);
            });
            // Black Forest: scattered stones on the forest floor.
            Scatter(9f * G, 17, s =>
            {
                if (!s.m_isLand || s.m_land != Land.BlackForest || s.m_shore < 3f || !(s.m_roll < 0.5f)) return;
                Stone(s.m_x, s.m_y, G, s.m_seed);
            });
            // Swamp: small wavelets of standing water, with flat strokes of still water between.
            Scatter(8f * G, 11, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 3f) return;
                if (s.m_roll < 0.45f) Wavelet(s.m_x, s.m_y, G);
                else if (s.m_roll < 0.7f) m_canvas.Stroke(s.m_x - 1.5f * G, s.m_y, s.m_x + 1.5f * G, s.m_y, Pen);
            });
            // Plains: a stipple of fine dots, the dry heath's own tone.
            Scatter(4f * G, 16, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Plains || s.m_shore < 2f || !(s.m_roll < 0.6f)) return;
                m_canvas.Dot(s.m_x, s.m_y, StippleRadius);
            });
            // Ashlands: dense diagonal hatching darkens the burnt ground. Deliberately a grey tone.
            Scatter(5f * G, 12, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_shore < 2f || !(s.m_roll < 0.75f)) return;
                m_canvas.Stroke(s.m_x - 1.4f * G, s.m_y + 1.4f * G, s.m_x + 1.4f * G, s.m_y - 1.4f * G, 0.7f, 0.75f);
            });
            // Deep North: a near-blank snowfield, as the classic map leaves its northern waste. Its
            // only marks are chevrons of pressure ridges, gathered into thin winding bands (the
            // crest lines of a ridged noise) with open snow between, rather than sprinkled evenly.
            Scatter(8f * G, 15, s =>
            {
                if (!s.m_isLand || s.m_land != Land.DeepNorth || s.m_above >= DeepNorthPeakHeight || s.m_shore < 3f) return;
                float ridge = 1f - Math.Abs(2f * Noise(s.m_x / RidgeScale, s.m_y / RidgeScale, m_world.m_seed + 9) - 1f);
                if (ridge < RidgeBand || !(s.m_roll < 0.7f)) return;
                m_canvas.Stroke(s.m_x - 2.4f * G, s.m_y, s.m_x, s.m_y - 2f * G, Pen);
                m_canvas.Stroke(s.m_x, s.m_y - 2f * G, s.m_x + 2.4f * G, s.m_y, Pen);
            });
        }

        private void PlaceGlyphs()
        {
            // Mountains: staggered rows of overlapping peaks, half a peak's height apart and three
            // quarters of a width across, so a range reads as one dark mass, as on the classic maps.
            // Sized by height, in the Mountain biome and the high Deep North.
            Scatter(16f * G, 9f * G, 0.3f, 0.5f, 21, s =>
            {
                if (!s.m_isLand || !(s.m_roll < 0.97f)) return;
                bool mountain = s.m_land == Land.Mountain && s.m_above >= PeakHeight;
                bool north = s.m_land == Land.DeepNorth && s.m_above >= DeepNorthPeakHeight;
                if (!mountain && !north) return;
                float width = Mathf.Lerp(18f, 30f, Mathf.Clamp01((s.m_above - PeakHeight) / 160f)) * G;
                if (s.m_shore < width * 0.45f || !IsLandColumn(s.m_x, s.m_y, width * 0.75f)) return;
                Add(s, Kind.Peak, width);
            });
            // Hills: groups of open humps on steep open ground, and a ring of foothills around each
            // range. None under forest: the trees cover the hills there.
            Scatter(10f * G, 22, s =>
            {
                if (!s.m_isLand || s.m_above < 3f || !(s.m_roll < 0.75f)) return;
                bool open = (s.m_land == Land.Meadows && !s.m_forest) || s.m_land == Land.Plains;
                bool foothill = s.m_land == Land.Mountain && s.m_above < PeakHeight;
                bool steep = s.m_slope >= HillSlope || (foothill && s.m_slope >= FoothillSlope && s.m_above >= FoothillHeight);
                float width = Mathf.Lerp(9f, 13f, Unit(s.m_seed, 4)) * G;
                if (!(open || foothill) || !steep || s.m_shore < width * 0.5f) return;
                Add(s, Kind.Hill, width);
            });
            // Meadows woods: egg-shaped crowns packed so tightly that only the southern edge shows
            // trunks, the rest hidden behind the row in front.
            Scatter(5.2f * G, 23, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Meadows || !s.m_forest || s.m_shore < 4f || !(s.m_roll < 0.95f)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 8f * G)) return;
                Add(s, Kind.Broadleaf, G);
            });
            // Black Forest: dense pines with a few clearings.
            Scatter(5.5f * G, 24, s =>
            {
                if (!s.m_isLand || s.m_land != Land.BlackForest || s.m_shore < 4f || !(s.m_roll < 0.9f)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 11f * G)) return;
                if (Noise(s.m_x / (40f * G), s.m_y / (40f * G), m_world.m_seed) > 0.72f) return;
                Add(s, Kind.Conifer, G);
            });
            // Plains: a few lone round trees where vanilla puts its sparse woods.
            Scatter(12f * G, 25, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Plains || !s.m_forest || s.m_shore < 3f || !(s.m_roll < 0.5f)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 6f * G)) return;
                Add(s, Kind.PlainsTree, G);
            });
            // Swamp: a few marsh tufts among the water strokes, and some dead trees.
            Scatter(11f * G, 26, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 3f || !(s.m_roll < 0.35f)) return;
                Add(s, Kind.MarshTuft, G);
            });
            Scatter(24f * G, 27, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 4f || !(s.m_roll < 0.35f)) return;
                Add(s, Kind.DeadTree, G);
            });
            // Mistlands: rock spires on the steep or high ground, and woods of giant trees packed
            // closely enough to read as the biome's dark forest.
            Scatter(12f * G, 28, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 5f || !(s.m_roll < 0.8f)) return;
                if (s.m_slope < SpireSlope && s.m_above < SpireHeight) return;
                if (!IsLandColumn(s.m_x, s.m_y, 22f * G)) return;
                Add(s, Kind.Spire, G);
            });
            Scatter(8f * G, 29, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || !s.m_mist || s.m_slope >= SpireSlope || s.m_shore < 5f || !(s.m_roll < 0.75f)) return;
                if (!IsLandColumn(s.m_x, s.m_y, 14f * G)) return;
                Add(s, Kind.GiantTree, G);
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
                if (s.m_isLand || s.m_water < 4f * G || !(s.m_roll < 0.5f)) return;
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
                (a, b) =>
                {
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
                    m_pathInside.Add(world.sqrMagnitude < edge * edge && IsLandAt(point.x, point.y));
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    for (int i = 1; i < m_points.Count; i++)
                    {
                        if (!m_pathInside[i] || !m_pathInside[i - 1]) continue;
                        float t = (float)i / (m_points.Count - 1);
                        float width = river.m_stream ? Mathf.Lerp(StreamMouth, Pen, t) : RiverWidth;
                        Vector2 a = m_points[i - 1], b = m_points[i];
                        if (pass == 0) m_canvas.Scrape(a.x, a.y, b.x, b.y, width + RiverBand);
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
                field.Compute(j => m_land[j] == 0 && m_waterDistance[j] >= deep, deepReach, 1f);
                float reach = ring * (RippleWidth - 1f) + 2.5f;
                int box = (int)(ring * RippleLandBox);
                float landNeeded = i == 0 ? 0f : RippleLandShare * ring * ring;
                Func<int, int, bool> skip = (x, y) =>
                {
                    float dx = x + 1f - centre, dy = y + 1f - centre;
                    if (dx * dx + dy * dy > edge * edge || m_land[y * n + x] != 0) return true;
                    if (deepReach[y * n + x] > reach) return true;
                    return landNeeded > 0f && LandInBox(landSum, x, y, box) < landNeeded;
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

        // Summed-area table of the land mask, (n + 1)² entries.
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
