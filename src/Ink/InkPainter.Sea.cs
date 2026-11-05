using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The sea's illustrations (SeaGlyphs), placed on the sheet after the waves and floes: few,
    /// each in a wide stretch of open water of its own sea, ranked by how far the water reaches
    /// round it and spread over the map. The serpents and the leviathan keep to
    /// the open ocean, the bonemaw to the Ashlands' boiling sea, and the Deep North's sea stays
    /// blank. Spots ruled out for a figure may take a small stand-in instead (boiling water or a
    /// leaping fish, PlaceStandIns). Placement hashes the world seed, so a world always gets the
    /// same ones.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // Each illustration is drawn at most IllustrationsEach times on the whole map, at a
        // seed-set IllustrationScaleMin to IllustrationScaleMax of its size, about half of them
        // turned east for west.
        private const int IllustrationsEach = 3;
        private const float IllustrationScaleMin = 0.85f;
        private const float IllustrationScaleMax = 1.15f;
        // Open water is measured on cells of OpenCell sheet pixels: the distance from a cell to the
        // nearest land or to the frame's inner ring. A glyph's box centre needs OpenWaterMin of it
        // (about 315 m), and its box IllustrationClear past its own half diagonal, so it stands
        // out at sea, clear of coasts, islets and the waves round them. Lower than that it makes
        // no difference: the clearance (66-73 px) and SameKindSpacing then cap the count.
        private const int OpenCell = 4;
        private const float OpenWaterMin = 60f;
        private const float IllustrationClear = 40f;
        // No ink at all (waves, floes, anything) within IllustrationInkGap of a glyph's box, and
        // glyphs IllustrationSpacing apart (about 3.2 km), so they spread over the oceans instead
        // of gathering in the widest one.
        private const float IllustrationInkGap = 8f;
        private const float IllustrationSpacing = 600f;
        // A glyph never stands within SameKindSpacing (about 10.5 km) of another of its kind: the
        // map opens on about 3070 x 1640 sheet pixels, and two copies of one figure in a view read
        // as stamped (historical sheets draw each creature once). With up to three of a kind
        // they cannot always be a whole view apart, so the nearest that still keeps most worlds'
        // counts.
        private const float SameKindSpacing = 2000f;
        // Hash salt of the placement rule (see Scatter's salts).
        private const int IllustrationSalt = 61;
        // A stand-in (a boil mark or a leaping fish, SeaGlyphs.Boil and Fish) needs its own clear
        // water, the figure rule (IllustrationClear past the half diagonal, OpenWaterMin) scaled
        // to a mark about 20 px across: open water from land and islets alike of its box's half
        // diagonal plus StandInOpenClear, never under StandInOpenMin (about 210 m). No ink within
        // StandInClear of its box (coast, waves, islets, figures, other stand-ins), and the frame
        // kept as far. StandInFigureGap from every figure's spot and StandInSpacing (about
        // 2.1 km) from every other stand-in, so they stay a sprinkle, not a pattern. Every
        // candidate already has OpenWaterMin, so the ink clearance and the spacings bind.
        private const float StandInOpenClear = 24f;
        private const float StandInOpenMin = 40f;
        private const float StandInClear = 16f;
        private const float StandInFigureGap = 150f;
        private const float StandInSpacing = 400f;

        // Which sea a point of open water belongs to, for its illustrations.
        private enum Sea
        {
            None,
            Open,
            Ashlands,
        }

        // The figures are taken in farthest-point order (the user's choice; NextFigureSpot): after
        // the best-ranked spot, each next is the one farthest from every figure placed, the
        // frame counting as a neighbour at FrameNeighbour times its distance (without it the
        // order runs straight to the rim), weighed by the spot's seed factor and by the square
        // root of its open water over OpenWaterMin, so a region's figure still sits in its
        // roomier pocket. Ranked by open water alone, the figures crowded the outer ocean and the
        // Ashlands' rim (about 56% within 2 km of the frame on Velen) and left interior basins
        // bare; so ordered, a third stand near the rim on the local worlds.
        private const float FrameNeighbour = 2f;

        // A candidate spot: an OpenCell cell where the open water peaks, its distance from land
        // in cells, its seed factor, its rank (open water times the factor) and its distance from
        // the frame's inner ring in sheet pixels.
        private struct OpenSpot
        {
            public int m_cx, m_cy, m_open;
            public float m_factor, m_score, m_ring;
        }

        private void DrawIllustrations()
        {
            int salt = unchecked(m_world.m_seed + IllustrationSalt * 7919);
            int n = CanvasSize / OpenCell;
            float centre = CanvasSize * 0.5f, ring = EdgeRadius / MetresPerPixel - FrameGap;
            var open = new byte[n * n];
            new DistanceField(n).Compute(i =>
            {
                int cx = i % n, cy = i / n;
                float dx = (cx + 0.5f) * OpenCell - centre, dy = (cy + 0.5f) * OpenCell - centre;
                if (dx * dx + dy * dy >= ring * ring) return true;
                for (int y = cy * OpenCell; y < (cy + 1) * OpenCell; y++)
                    for (int x = cx * OpenCell; x < (cx + 1) * OpenCell; x++)
                        if (m_land[y * CanvasSize + x] != 0) return true;
                return false;
            }, open, 1f);
            m_cancel.ThrowIfCancellationRequested();

            // Where the open water peaks (no neighbour within two cells farther out), ranked by
            // how far it reaches, a little shuffled by seed so worlds alike in shape differ.
            int least = Mathf.CeilToInt(OpenWaterMin / OpenCell);
            var spots = new List<OpenSpot>();
            for (int cy = 2; cy < n - 2; cy++)
            {
                for (int cx = 2; cx < n - 2; cx++)
                {
                    int d = open[cy * n + cx];
                    if (d < least || !IsOpenPeak(open, n, cx, cy, d)) continue;
                    float factor = Mathf.Lerp(0.85f, 1.15f, Unit(HashInt(cx, cy, salt), 0));
                    float px = (cx + 0.5f) * OpenCell - centre, py = (cy + 0.5f) * OpenCell - centre;
                    spots.Add(new OpenSpot
                    {
                        m_cx = cx, m_cy = cy, m_open = d, m_factor = factor, m_score = d * factor,
                        m_ring = ring - Mathf.Sqrt(px * px + py * py),
                    });
                }
            }
            Comparison<OpenSpot> byRank = (a, b) => a.m_score != b.m_score ? b.m_score.CompareTo(a.m_score) : (a.m_cy * n + a.m_cx).CompareTo(b.m_cy * n + b.m_cx);
            spots.Sort(byRank);

            var counts = new int[SeaGlyphs.Names.Length];
            var placed = new List<Vector2>();
            var placedKinds = new List<int>();
            int firstKind = (int)(Unit(salt, 1) * 3f) % 3;
            InkLayer layer = m_canvas.m_layer;
            m_canvas.m_layer = InkLayer.Illustration;
            // Every spot considered and ruled out, whatever the reason: the stand-ins' candidates.
            var rejected = new List<OpenSpot>();
            // The candidates left, swap-removed as they are taken (NextFigureSpot breaks ties by
            // cell index, not by place in the list), each with its squared distance to the nearest
            // figure placed, updated as figures are placed: rescanning every figure for every
            // spot at every pick took about 0.6 s on Velen.
            OpenSpot[] pool = spots.ToArray();
            var nearest = new float[pool.Length];
            for (int i = 0; i < nearest.Length; i++) nearest[i] = float.MaxValue;
            int left = pool.Length, ranked = 0;
            while (placed.Count == 0 ? ranked < left : left > 0)
            {
                // Until the first figure stands, in rank order (the pool is still sorted then).
                int pick = placed.Count == 0 ? ranked++ : NextFigureSpot(pool, nearest, left, n);
                OpenSpot spot = pool[pick];
                if (placed.Count > 0)
                {
                    left--;
                    pool[pick] = pool[left];
                    nearest[pick] = nearest[left];
                }
                rejected.Add(spot);
                var at = new Vector2((spot.m_cx + 0.5f) * OpenCell, (spot.m_cy + 0.5f) * OpenCell);
                if (Crowded(placed, at, IllustrationSpacing)) continue;
                Sea sea = SeaAt(at.x, at.y);
                int kind = PickIllustration(sea, counts, firstKind, kindAt => KindNear(placed, placedKinds, kindAt, at));
                if (kind < 0) continue;
                int seed = HashInt(spot.m_cx, spot.m_cy, salt + 1);
                bool mirror = Unit(seed, 1) < 0.5f;
                float scale = Mathf.Lerp(IllustrationScaleMin, IllustrationScaleMax, Unit(seed, 2));
                InkBox box = IllustrationBox(kind, mirror, scale);
                // The glyph is placed so its box is centred on the spot.
                float x = at.x - (box.m_left + box.m_right) * 0.5f, y = at.y - (box.m_top + box.m_bottom) * 0.5f;
                var bounds = new InkBox { m_left = x + box.m_left, m_top = y + box.m_top, m_right = x + box.m_right, m_bottom = y + box.m_bottom };
                float boxWidth = box.m_right - box.m_left, boxHeight = box.m_bottom - box.m_top;
                float halfDiagonal = 0.5f * Mathf.Sqrt(boxWidth * boxWidth + boxHeight * boxHeight);
                if (spot.m_open * OpenCell < halfDiagonal + IllustrationClear) continue;
                if (SeaAt(bounds.m_left, bounds.m_top) != sea || SeaAt(bounds.m_right, bounds.m_top) != sea
                    || SeaAt(bounds.m_left, bounds.m_bottom) != sea || SeaAt(bounds.m_right, bounds.m_bottom) != sea) continue;
                if (InkNear(bounds, IllustrationInkGap)) continue;
                rejected.RemoveAt(rejected.Count - 1);
                counts[kind]++;
                placed.Add(at);
                if (placed.Count == 1)
                {
                    // The spots before this one are taken: drop them, the pool keeps the rest.
                    Array.Copy(pool, ranked, pool, 0, left - ranked);
                    left -= ranked;
                }
                for (int i = 0; i < left; i++)
                {
                    var other = new Vector2((pool[i].m_cx + 0.5f) * OpenCell, (pool[i].m_cy + 0.5f) * OpenCell);
                    nearest[i] = Math.Min(nearest[i], (other - at).sqrMagnitude);
                }
                placedKinds.Add(kind);
                m_canvas.BeginGroup(y);
                SeaGlyphs.Draw(m_canvas, kind, x, y, mirror, scale);
                m_canvas.EndGroup();
                if (m_record == null) continue;
                var mark = new SeaMark
                {
                    m_west = bounds.m_left * MetresPerPixel - HalfExtent,
                    m_east = bounds.m_right * MetresPerPixel - HalfExtent,
                    m_north = HalfExtent - bounds.m_top * MetresPerPixel,
                    m_south = HalfExtent - bounds.m_bottom * MetresPerPixel,
                    m_kind = kind,
                    m_x = x,
                    m_y = y,
                    m_mirror = mirror,
                    m_scale = scale,
                };
                m_record.m_seaMarks.Add(mark);
                m_record.m_seaWashes.Add(SeaWash.Build(mark));
            }
            // The stand-ins keep the rank order, so boils and fish keep their own spread rather
            // than the figures' farthest-point one.
            rejected.Sort(byRank);
            PlaceStandIns(rejected, placed);
            m_canvas.m_layer = layer;
        }

        // The index of the next figure spot in farthest-point order (FrameNeighbour); ties go to
        // the lower cell index, so the order is the same on every machine.
        private static int NextFigureSpot(OpenSpot[] pool, float[] nearest, int count, int n)
        {
            int best = -1;
            float bestKey = -1f;
            for (int i = 0; i < count; i++)
            {
                OpenSpot s = pool[i];
                float key = Math.Min(Mathf.Sqrt(nearest[i]), FrameNeighbour * Math.Max(s.m_ring, 0f)) * s.m_factor
                    * Mathf.Sqrt(s.m_open * OpenCell / OpenWaterMin);
                if (key > bestKey || (key == bestKey && s.m_cy * n + s.m_cx < pool[best].m_cy * n + pool[best].m_cx))
                {
                    best = i;
                    bestKey = key;
                }
            }
            return best;
        }

        // The stand-ins: every spot a figure was considered for and ruled out (spacing, the kind
        // rule, the count cap, the box's clearance, a corner in another sea, ink near) takes, in
        // rank order, boiling water in the Ashlands' sea or a leaping fish in the open ocean (the
        // Deep North's sea stays blank, and lakes are never candidates), if the mark has its own
        // clear water (StandInOpenClear and the rest). Each is one Illustration record group and a
        // SeaMark with its box, so the fog shows it only wholly explored, as a figure, and
        // close-ups replay it whole. Boils come only so: scattered over the hot sea on a grid of
        // their own, they read as a pattern rather than as the sea's life.
        private void PlaceStandIns(List<OpenSpot> rejected, List<Vector2> figures)
        {
            float centre = CanvasSize * 0.5f, ring = EdgeRadius / MetresPerPixel - FrameGap - StandInClear;
            var standIns = new List<Vector2>();
            foreach (OpenSpot spot in rejected)
            {
                var at = new Vector2((spot.m_cx + 0.5f) * OpenCell, (spot.m_cy + 0.5f) * OpenCell);
                Sea sea = SeaAt(at.x, at.y);
                if (sea == Sea.None) continue;
                int kind = sea == Sea.Ashlands ? SeaGlyphs.Boil : SeaGlyphs.Fish;
                SeaGlyphs.StandInBox(kind, out float left, out float top, out float right, out float bottom);
                var box = new InkBox { m_left = at.x + left, m_top = at.y + top, m_right = at.x + right, m_bottom = at.y + bottom };
                float w = right - left, h = bottom - top;
                float need = Mathf.Max(StandInOpenMin, 0.5f * Mathf.Sqrt(w * w + h * h) + StandInOpenClear);
                if (spot.m_open * OpenCell < need) continue;
                if (SeaAt(box.m_left, box.m_top) != sea || SeaAt(box.m_right, box.m_top) != sea
                    || SeaAt(box.m_left, box.m_bottom) != sea || SeaAt(box.m_right, box.m_bottom) != sea) continue;
                // The frame is inked after the marks, so InkNear cannot see it: the whole box,
                // not only its spot, keeps StandInClear inside the frame's inner ring.
                if (!InsideRing(box, centre, ring) || InkNear(box, StandInClear)) continue;
                if (Crowded(figures, at, StandInFigureGap) || Crowded(standIns, at, StandInSpacing)) continue;
                standIns.Add(at);
                m_canvas.BeginGroup(at.y);
                SeaGlyphs.Draw(m_canvas, kind, at.x, at.y);
                m_canvas.EndGroup();
                if (m_record == null) continue;
                var mark = new SeaMark
                {
                    m_west = box.m_left * MetresPerPixel - HalfExtent,
                    m_east = box.m_right * MetresPerPixel - HalfExtent,
                    m_north = HalfExtent - box.m_top * MetresPerPixel,
                    m_south = HalfExtent - box.m_bottom * MetresPerPixel,
                    m_kind = kind,
                    m_x = at.x,
                    m_y = at.y,
                    m_scale = 1f,
                };
                m_record.m_seaMarks.Add(mark);
                m_record.m_seaWashes.Add(SeaWash.Build(mark));
            }
        }

        // A box on the sheet, in sheet pixels, y running south.
        private struct InkBox
        {
            public float m_left, m_top, m_right, m_bottom;
        }

        private static bool IsOpenPeak(byte[] open, int n, int cx, int cy, int d)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    if (open[(cy + dy) * n + cx + dx] > d) return false;
            return true;
        }

        // Whether all four corners of the box lie within radius of the sheet's centre.
        private static bool InsideRing(InkBox box, float centre, float radius)
        {
            float l = box.m_left - centre, r = box.m_right - centre, t = box.m_top - centre, b = box.m_bottom - centre;
            float most = Math.Max(Math.Max(l * l, r * r) + t * t, Math.Max(l * l, r * r) + b * b);
            return most < radius * radius;
        }

        private static bool Crowded(List<Vector2> placed, Vector2 at, float spacing)
        {
            foreach (Vector2 other in placed)
                if ((other - at).sqrMagnitude < spacing * spacing) return true;
            return false;
        }

        // The open ocean is where Valheim's Serpent swims: the Ocean biome. Vanilla's GetBiome
        // gives the Ashlands before the ocean, so the boiling sea round the Ashlands is the
        // Ashlands biome's water; the Deep North comes after it, so the open northern sea is Ocean
        // too and only the Deep North's own water, where its ice lies (WorldSource.m_iceWater),
        // is not (the minimap's shared white decodes it as Ocean).
        // (IsAshlands would run WorldGenerator's static constructor, which the offline preview
        // cannot load; the minimap's biomes are enough.)
        private Sea SeaAt(float x, float y)
        {
            int pixel = PixelAt(x, y);
            if (m_land[pixel] != 0 || m_sea[pixel] == 0) return Sea.None;
            float wx = x * MetresPerPixel - HalfExtent, wz = HalfExtent - y * MetresPerPixel;
            Land land = m_world.m_biome[m_world.Index(wx, wz)];
            if (land == Land.Ashlands) return Sea.Ashlands;
            if (land == Land.Ocean && !m_world.IsIceWater(wx, wz)) return Sea.Open;
            return Sea.None;
        }

        // The illustration for a spot in sea: the open ocean's three in turn, the one placed
        // fewest times first (from a seed-set one), so each world gets a mix, skipping any kind
        // that already stands near the spot (near); -1 when the sea has none left there.
        private static int PickIllustration(Sea sea, int[] counts, int first, Func<int, bool> near)
        {
            if (sea == Sea.Ashlands)
                return counts[SeaGlyphs.BonemawSerpent] < IllustrationsEach && !near(SeaGlyphs.BonemawSerpent) ? SeaGlyphs.BonemawSerpent : -1;
            if (sea != Sea.Open) return -1;
            int[] open = { SeaGlyphs.SeaSerpent, SeaGlyphs.SerpentCoil, SeaGlyphs.Leviathan };
            int best = -1;
            for (int k = 0; k < open.Length; k++)
            {
                int kind = open[(first + k) % open.Length];
                if (counts[kind] >= IllustrationsEach || near(kind)) continue;
                if (best < 0 || counts[kind] < counts[best]) best = kind;
            }
            return best;
        }

        // Whether a glyph of this kind is already placed within SameKindSpacing of the spot.
        private static bool KindNear(List<Vector2> placed, List<int> kinds, int kind, Vector2 at)
        {
            for (int i = 0; i < placed.Count; i++)
                if (kinds[i] == kind && (placed[i] - at).sqrMagnitude < SameKindSpacing * SameKindSpacing) return true;
            return false;
        }

        // The box round a glyph's ink, relative to the point it is drawn at, measured by drawing
        // it on a scratch canvas.
        private static InkBox IllustrationBox(int kind, bool mirror, float scale)
        {
            const int width = 200, height = 140;
            const float x = 100f, y = 90f;
            var scratch = new InkCanvas(width, height, 0f, 0f, 1f);
            SeaGlyphs.Draw(scratch, kind, x, y, mirror, scale);
            int left = width, right = -1, top = height, bottom = -1;
            for (int py = 0; py < height; py++)
            {
                for (int px = 0; px < width; px++)
                {
                    if (scratch.m_ink[py * width + px] == 0) continue;
                    left = Math.Min(left, px);
                    right = Math.Max(right, px);
                    top = Math.Min(top, py);
                    bottom = Math.Max(bottom, py);
                }
            }
            if (right < 0) return new InkBox();
            return new InkBox { m_left = left - x, m_top = top - y, m_right = right + 1 - x, m_bottom = bottom + 1 - y };
        }

        // Whether any ink lies on the sheet within gap of the box.
        private bool InkNear(InkBox box, float gap)
        {
            int x0 = Mathf.Max(0, Mathf.FloorToInt(box.m_left - gap)), x1 = Mathf.Min(CanvasSize - 1, Mathf.CeilToInt(box.m_right + gap));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(box.m_top - gap)), y1 = Mathf.Min(CanvasSize - 1, Mathf.CeilToInt(box.m_bottom + gap));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (m_canvas.m_ink[y * CanvasSize + x] != 0) return true;
            return false;
        }
    }
}
