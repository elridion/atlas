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
        private const float RippleNear = 4.5f * G;
        private const float RippleFar = 8.5f * G;
        private const float OpenWidth = 9f * G;
        private const float DistanceScale = 4f;
        private const float FrameGap = 7f;
        // Coast clean-up: swamp water shallower than SwampDeep metres counts as land, and islands
        // or enclosed lakes smaller than these many map pixels (12 m each) are not drawn.
        private const float SwampDeep = 3f;
        private const int MinIsland = 12;
        private const int MinLake = 16;

        // Terrain thresholds: metres above the water and height change per metre of slope.
        private const float PeakHeight = 50f;
        private const float DeepNorthPeakHeight = 45f;
        private const float HillSlope = 0.35f;
        private const float SpireSlope = 0.55f;
        private const float VolcanoHeight = 22f;
        private const float WetHeight = 0.6f;
        // Volcanoes are drawn larger than other land glyphs, closer to the size of a peak.
        private const float VolcanoScale = 1.6f;

        private readonly WorldSource m_world;
        private readonly CancellationToken m_cancel;
        private readonly InkCanvas m_canvas = new InkCanvas(CanvasSize);
        private readonly List<Vector2> m_points = new List<Vector2>(16);
        private readonly List<Glyph> m_glyphs = new List<Glyph>(200000);
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
            m_cancel.ThrowIfCancellationRequested();
            DrawCoast();
            DrawRipples();
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

            m_coast = new float[size * size];
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
                            sum += heights[zz * size + xx];
                            count++;
                        }
                    }
                    m_coast[z * size + x] = sum / count;
                }
            }

            RemoveSpecks(water, land: true, MinIsland);
            RemoveSpecks(water, land: false, MinLake);
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
        private void Scatter(float spacing, int salt, Action<Spot> visit)
        {
            int cells = (int)(CanvasSize / spacing);
            float edge = EdgeRadius / MetresPerPixel;
            float centre = CanvasSize * 0.5f;
            int worldSeed = unchecked(m_world.m_seed + salt * 7919);
            for (int gy = 0; gy < cells; gy++)
            {
                for (int gx = 0; gx < cells; gx++)
                {
                    int seed = HashInt(gx, gy, worldSeed);
                    float x = (gx + 0.5f + (Unit(seed, 1) - 0.5f) * 0.9f) * spacing;
                    float y = (gy + 0.5f + (Unit(seed, 2) - 0.5f) * 0.9f) * spacing;
                    float dx = x - centre, dy = y - centre;
                    if (dx * dx + dy * dy > edge * edge * 0.985f) continue;
                    Spot spot = Look(x, y);
                    spot.m_roll = Unit(seed, 3);
                    spot.m_seed = seed;
                    visit(spot);
                }
            }
        }

        private void Add(Spot spot, Kind kind, float size)
        {
            m_glyphs.Add(new Glyph { m_x = spot.m_x, m_y = spot.m_y, m_size = size, m_kind = kind, m_seed = spot.m_seed });
        }

        private static float Unit(int seed, int k) => Atlas.Noise.Unit(seed, k);

        private static int HashInt(int x, int y, int seed) => Atlas.Noise.HashInt(x, y, seed);

        // Textures that lie flat on the land; glyphs placed later scrape them away behind their bodies.
        private void DrawBackground()
        {
            // Swamp: short horizontal strokes of standing water in the low, wet ground.
            Scatter(9f * G, 11, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_above > WetHeight || s.m_shore < 3f || !(s.m_roll <0.4f)) return;
                m_canvas.Stroke(s.m_x - 2.2f * G, s.m_y, s.m_x + 2.2f * G, s.m_y, 0.7f, 0.8f);
            });
            // Ashlands: dense diagonal hatching darkens the burnt ground.
            Scatter(5f * G, 12, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_shore < 2f || !(s.m_roll <0.75f)) return;
                m_canvas.Stroke(s.m_x - 1.4f * G, s.m_y + 1.4f * G, s.m_x + 1.4f * G, s.m_y - 1.4f * G, 0.7f, 0.75f);
            });
            // Mistlands: drifting lines of dots.
            Scatter(30f * G, 13, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_shore < 6f || !(s.m_roll <0.6f)) return;
                float phase = Unit(s.m_seed, 4) * 6.28f;
                float length = 26f * G;
                for (float t = 0f; t <= length; t += 2.4f * G)
                {
                    float wave = Mathf.Sin(t / length * 9.42f + phase) * 1.6f * G;
                    m_canvas.Dot(s.m_x - length * 0.5f + t, s.m_y + wave, 0.6f, 0.85f);
                }
            });
            // Meadows and Plains: sparse tufts of grass in the open.
            Scatter(22f * G, 14, s =>
            {
                if (!s.m_isLand || s.m_shore < 3f) return;
                bool meadow = s.m_land == Land.Meadows && !s.m_forest && (s.m_roll <0.25f);
                bool plain = s.m_land == Land.Plains && !s.m_forest && (s.m_roll <0.4f);
                if (meadow || plain) Grass(s.m_x, s.m_y, G);
            });
            // Deep North: low chevrons of glacier ice.
            Scatter(15f * G, 15, s =>
            {
                if (!s.m_isLand || s.m_land != Land.DeepNorth || s.m_above >= DeepNorthPeakHeight || s.m_shore < 3f || !(s.m_roll <0.55f)) return;
                m_canvas.Stroke(s.m_x - 3f * G, s.m_y, s.m_x, s.m_y - 2.4f * G, 0.8f, 0.9f);
                m_canvas.Stroke(s.m_x, s.m_y - 2.4f * G, s.m_x + 3f * G, s.m_y, 0.8f, 0.9f);
            });
        }

        private void PlaceGlyphs()
        {
            // Mountains: shaded peaks sized by height, in the Mountain biome and the high Deep North.
            Scatter(18f * G, 21, s =>
            {
                if (!s.m_isLand || !(s.m_roll <0.85f)) return;
                bool mountain = s.m_land == Land.Mountain && s.m_above >= PeakHeight;
                bool north = s.m_land == Land.DeepNorth && s.m_above >= DeepNorthPeakHeight;
                if (!mountain && !north) return;
                float width = Mathf.Lerp(12f, 26f, Mathf.Clamp01((s.m_above - PeakHeight) / 180f)) * G;
                if (s.m_shore < width * 0.6f) return;
                Add(s, Kind.Peak, width);
            });
            // Hills: open humps on steep ground below the peaks.
            Scatter(15f * G, 22, s =>
            {
                if (!s.m_isLand || s.m_slope < HillSlope || s.m_above < 3f || !(s.m_roll <0.6f)) return;
                bool hillCountry = s.m_land == Land.Meadows || s.m_land == Land.BlackForest || s.m_land == Land.Plains
                    || (s.m_land == Land.Mountain && s.m_above < PeakHeight);
                float width = Mathf.Lerp(9f, 13f, Unit(s.m_seed, 4)) * G;
                if (!hillCountry || s.m_shore < width * 0.5f) return;
                Add(s, Kind.Hill, width);
            });
            // Meadows woods: round broadleaf trees where vanilla grows forest.
            Scatter(8f * G, 23, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Meadows || !s.m_forest || s.m_slope >= HillSlope || s.m_shore < 3f || !(s.m_roll <0.85f)) return;
                Add(s, Kind.Broadleaf, G);
            });
            // Black Forest: dense pointed pines with a few clearings.
            Scatter(7f * G, 24, s =>
            {
                if (!s.m_isLand || s.m_land != Land.BlackForest || s.m_slope >= HillSlope || s.m_shore < 3f || !(s.m_roll <0.8f)) return;
                if (Noise(s.m_x / (40f * G), s.m_y / (40f * G), m_world.m_seed) > 0.72f) return;
                Add(s, Kind.Conifer, G);
            });
            // Plains: lone flat-topped trees where vanilla puts its sparse woods.
            Scatter(12f * G, 25, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Plains || !s.m_forest || s.m_shore < 3f || !(s.m_roll <0.5f)) return;
                Add(s, Kind.PlainsTree, G);
            });
            // Swamp: marsh tufts and a scattering of dead trees.
            Scatter(11f * G, 26, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 3f || !(s.m_roll <0.55f)) return;
                Add(s, Kind.MarshTuft, G);
            });
            Scatter(24f * G, 27, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Swamp || s.m_shore < 4f || !(s.m_roll <0.35f)) return;
                Add(s, Kind.DeadTree, G);
            });
            // Mistlands: rock spires on the steep ground, giant trees in the misty woods.
            Scatter(14f * G, 28, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || s.m_slope < SpireSlope || s.m_shore < 5f || !(s.m_roll <0.8f)) return;
                Add(s, Kind.Spire, G);
            });
            Scatter(13f * G, 29, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Mistlands || !s.m_mist || s.m_slope >= SpireSlope || s.m_shore < 6f || !(s.m_roll <0.5f)) return;
                Add(s, Kind.GiantTree, G);
            });
            // Ashlands: smoking volcanoes on the heights, lava cracks in the flows.
            Scatter(34f * G, 30, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || s.m_above < VolcanoHeight || s.m_shore < 12f * G || !(s.m_roll <0.45f)) return;
                Add(s, Kind.Volcano, VolcanoScale * G);
            });
            Scatter(16f * G, 31, s =>
            {
                if (!s.m_isLand || s.m_land != Land.Ashlands || !s.m_lava || s.m_shore < 6f * G || !(s.m_roll <0.55f)) return;
                Add(s, Kind.Fissure, G);
            });
            // Sea: a few wave marks far out, ice floes in the northern sea.
            Scatter(60f * G, 32, s =>
            {
                if (s.m_isLand || s.m_water < 28f * G || !IsOpenWater(s.m_pixel) || !(s.m_roll <0.45f)) return;
                if (WorldGenerator.IsDeepnorth(s.m_worldX, s.m_worldZ)) return;
                Add(s, Kind.Wave, G);
            });
            Scatter(15f * G, 33, s =>
            {
                if (s.m_isLand || s.m_water < 4f * G || !(s.m_roll <0.5f)) return;
                if (!WorldGenerator.IsDeepnorth(s.m_worldX, s.m_worldZ)) return;
                Add(s, Kind.IceFloe, G);
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

        private void DrawRipples()
        {
            int n = CanvasSize;
            float edge = EdgeRadius / MetresPerPixel - 12f;
            float centre = n * 0.5f;
            Func<int, int, float> distance = (x, y) => m_waterDistance[y * n + x] / DistanceScale;
            Func<int, int, bool> skip = (x, y) =>
            {
                float dx = x + 1f - centre, dy = y + 1f - centre;
                return dx * dx + dy * dy > edge * edge || !IsOpenWater(y * n + x) || !IsOpenWater((y + 1) * n + x + 1);
            };
            // Distances run to the centre of the nearest land pixel; the coast lies half a pixel out.
            DistanceField.Contour(n, distance, RippleNear + 0.5f, skip, (a, b) =>
                m_canvas.Stroke(a.x + 0.5f, a.y + 0.5f, b.x + 0.5f, b.y + 0.5f, 1.0f, 0.9f));
            // The outer line is fainter and breaks off here and there, as if the pen ran dry.
            int seed = m_world.m_seed;
            DistanceField.Contour(n, distance, RippleFar + 0.5f, skip, (a, b) =>
            {
                if (Noise(a.x / 18f, a.y / 18f, seed + 5) > 0.68f) return;
                m_canvas.Stroke(a.x + 0.5f, a.y + 0.5f, b.x + 0.5f, b.y + 0.5f, 0.8f, 0.65f);
            });
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
