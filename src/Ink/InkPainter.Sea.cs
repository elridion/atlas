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
    /// blank. Placement hashes the world seed, so a world always gets the same ones.
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
        // The boiling sea's bubbles (Simmer): very few, one at most in a cell of BoilStep sheet
        // pixels (about 630 m) with BoilOdds, jittered within BoilJitter of the cell, and none
        // within BoilSpacing (about 1.6 km) of another, taken in grid order: pairs from
        // neighbouring cells read as a cluster, not a scatter. Only where the sea is at least
        // BoilHeat hot (vanilla's gradient, WorldSource.m_ashSea), out in the Ashlands' sea, with
        // no ink within BoilClear (coast, waves, islets, the bonemaw) nor the frame. Velen gets
        // about ten on the whole map.
        private const float BoilStep = 120f;
        private const float BoilOdds = 0.3f;
        private const float BoilJitter = 0.6f;
        private const float BoilSpacing = 300f;
        private const float BoilHeat = 0.8f;
        private const float BoilClear = 16f;
        private const int BoilSalt = 62;
        // The simmer mark's water: one wave of BoilWave px crests lifting BoilLift (lower than
        // the coast's WaveLift, a ruffle, not a group), seen at three distances: the back row
        // drawn at BoilScaleBack of the front, the lower one at BoilScaleLow, each row
        // BoilTrainShift of a wave on from the one behind. The rows' gaps are foreshortened with
        // them (in proportion to the mean scale of the two rows, 0.9 and 1.1 of 4 px).
        private const float BoilWave = 3.4f;
        private const float BoilLift = 0.45f;
        private const float BoilScaleBack = 0.8f;
        private const float BoilScaleLow = 1.2f;
        private const float BoilTrainShift = 1f / 3f;
        private const float BoilBackRise = 3.6f;
        private const float BoilLowDrop = 4.4f;

        // Which sea a point of open water belongs to, for its illustrations.
        private enum Sea
        {
            None,
            Open,
            Ashlands,
        }

        // A candidate spot: an OpenCell cell where the open water peaks, its distance from land
        // in cells and its rank.
        private struct OpenSpot
        {
            public int m_cx, m_cy, m_open;
            public float m_score;
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
                    float score = d * Mathf.Lerp(0.85f, 1.15f, Unit(HashInt(cx, cy, salt), 0));
                    spots.Add(new OpenSpot { m_cx = cx, m_cy = cy, m_open = d, m_score = score });
                }
            }
            spots.Sort((a, b) => a.m_score != b.m_score ? b.m_score.CompareTo(a.m_score) : (a.m_cy * n + a.m_cx).CompareTo(b.m_cy * n + b.m_cx));

            var counts = new int[SeaGlyphs.Names.Length];
            var placed = new List<Vector2>();
            var placedKinds = new List<int>();
            int firstKind = (int)(Unit(salt, 1) * 3f) % 3;
            InkLayer layer = m_canvas.m_layer;
            m_canvas.m_layer = InkLayer.Illustration;
            foreach (OpenSpot spot in spots)
            {
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
                counts[kind]++;
                placed.Add(at);
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
            m_canvas.m_layer = layer;
        }

        // The Ashlands' sea boils: a few simmer marks scattered thinly over its hottest water, on
        // a hashed grid of their own, so they never move with another rule. Waves read as birds
        // far out at sea, so the marks are bubbles, not strokes.
        private void DrawBoiling()
        {
            if (m_world.m_ashSea == null) return;
            int salt = unchecked(m_world.m_seed + BoilSalt * 7919);
            float centre = CanvasSize * 0.5f, ring = EdgeRadius / MetresPerPixel - FrameGap - BoilClear;
            int cells = Mathf.CeilToInt(CanvasSize / BoilStep);
            var placed = new List<Vector2>();
            for (int cy = 0; cy < cells; cy++)
            {
                for (int cx = 0; cx < cells; cx++)
                {
                    int seed = HashInt(cx, cy, salt);
                    if (Unit(seed, 0) >= BoilOdds) continue;
                    float margin = (1f - BoilJitter) * 0.5f;
                    float x = (cx + margin + BoilJitter * Unit(seed, 1)) * BoilStep;
                    float y = (cy + margin + BoilJitter * Unit(seed, 2)) * BoilStep;
                    if (SeaAt(x, y) != Sea.Ashlands) continue;
                    float wx = x * MetresPerPixel - HalfExtent, wz = HalfExtent - y * MetresPerPixel;
                    if (m_world.Sample(m_world.m_ashSea, wx, wz) < BoilHeat) continue;
                    // The mark's own reach: the bubbles and their speck rise about 11 px, the
                    // lower water line lies about 8 px under the point, and the front line runs
                    // up to about 10 px either side.
                    var box = new InkBox { m_left = x - 10.7f, m_top = y - 11.5f, m_right = x + 10.7f, m_bottom = y + 8.8f };
                    // The frame is inked after the marks, so InkNear cannot see it: the whole box,
                    // not only its point, keeps BoilClear inside the frame's inner ring.
                    if (!InsideRing(box, centre, ring)) continue;
                    if (InkNear(box, BoilClear)) continue;
                    if (Crowded(placed, new Vector2(x, y), BoilSpacing)) continue;
                    placed.Add(new Vector2(x, y));
                    Simmer(x, y, seed);
                }
            }
        }

        // A simmer mark (the user's design): a column of three bubbles rising from the water,
        // shrinking and swaying as they rise, over three level lines of one wave seen in
        // perspective: a back line broken by the column, an unbroken front line under it and a
        // shorter lower line. Each row is the front's wave scaled by its distance (BoilScaleBack,
        // 1, BoilScaleLow): wavelength, lift and length together, and the gaps between rows
        // foreshortened alike, so the water reads as a surface seen from above, not three rules.
        // Sizes and sides by seed so no two are stamped alike. At the opening zoom it reads as a
        // speck, in close-ups as bubbles on the water.
        private void Simmer(float x, float y, int seed)
        {
            float side = Unit(seed, 3) < 0.5f ? -1f : 1f;
            float r0 = Mathf.Lerp(1.6f, 2.0f, Unit(seed, 4));
            m_canvas.Ring(x, y, r0, r0 * 0.85f, Pen);
            float r1 = r0 * Mathf.Lerp(0.58f, 0.68f, Unit(seed, 5));
            float x1 = x + side * Mathf.Lerp(0.8f, 1.4f, Unit(seed, 6)), y1 = y - r0 - r1 - 1.3f;
            m_canvas.Ring(x1, y1, r1, r1 * 0.9f, Pen);
            float r2 = r1 * 0.66f;
            float x2 = x1 - side * Mathf.Lerp(0.6f, 1.2f, Unit(seed, 7)), y2 = y1 - r1 - r2 - 1.2f;
            m_canvas.Ring(x2, y2, r2, r2, Pen);
            if (Unit(seed, 8) < 0.75f) m_canvas.Dot(x2 + side * 0.9f, y2 - r2 - 1.7f, 0.55f);

            // The rows' crests a third of a wave on from the row behind, so they do not stack.
            float phase = Unit(seed, 153) * Mathf.PI * 2f, shift = BoilTrainShift * Mathf.PI * 2f;
            // Front: one unbroken line 1.7 px under the big bubble's foot, so a crest under it
            // still leaves paper between them.
            float front = y + 0.85f * r0 + 1.7f;
            float width = Mathf.Lerp(15f, 18f, Unit(seed, 151));
            float centre = x + Mathf.Lerp(-1.2f, 1.2f, Unit(seed, 152));
            BoilPiece(centre - width * 0.5f, front, width, x, phase + shift, 1f);
            // Back: behind the column and broken by it, a long piece on the seed's side and a
            // short one on the other, so it shows past the column both ways. The hole is the
            // bubble's own size, not scaled; running up to the bubble the line skewers it.
            float backSide = Unit(seed, 150) < 0.5f ? -1f : 1f;
            float back = front - BoilBackRise, hole = r0 + 1.0f;
            float longer = Mathf.Lerp(4.6f, 5.8f, Unit(seed, 155)) * BoilScaleBack;
            float shorter = Mathf.Lerp(3.0f, 3.8f, Unit(seed, 156)) * BoilScaleBack;
            float left = backSide < 0f ? longer : shorter, right = backSide < 0f ? shorter : longer;
            BoilPiece(x - hole - left, back, left, x, phase, BoilScaleBack);
            BoilPiece(x + hole, back, right, x, phase, BoilScaleBack);
            // Lower: about half the front's width, shifted away from the back's long side.
            float low = front + BoilLowDrop;
            float lowWidth = width * 0.5f * Mathf.Lerp(0.9f, 1.1f, Unit(seed, 159)) * BoilScaleLow;
            float lowCentre = x - backSide * Mathf.Lerp(0.8f, 2.2f, Unit(seed, 160));
            BoilPiece(lowCentre - lowWidth * 0.5f, low, lowWidth, x, phase + 2f * shift, BoilScaleLow);
        }

        // One level piece of the simmer mark's wave: its crests fall at fixed places on the sheet
        // (BoilWave times scale long, counted from the column at x), so the pieces of a broken
        // row keep one wave; the ends run out flat as the sea's strokes do (WaveStroke, which
        // counts crests per stroke and so cannot keep a train across a break). Strictly level:
        // the sea's lines always read horizontal.
        private void BoilPiece(float x0, float level, float length, float x, float phase, float scale)
        {
            float wave = BoilWave * scale, lift = BoilLift * scale;
            // Eight points a crest, as WaveStroke, so close-ups show a curve, not a zigzag.
            int count = Math.Max(8, Mathf.CeilToInt(length / wave * 8f));
            m_points.Clear();
            for (int i = 0; i <= count; i++)
            {
                float u = i / (float)count, px = x0 + u * length;
                float envelope = Mathf.Clamp01(Mathf.Sin(Mathf.PI * u) * 1.6f);
                m_points.Add(new Vector2(px, level - Mathf.Sin((px - x) / wave * Mathf.PI * 2f + phase) * lift * envelope));
            }
            m_canvas.Polyline(m_points, Pen);
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

        // The open ocean is where Valheim's Serpent swims: the Ocean biome outside the Deep North.
        // Vanilla's GetBiome gives the Ashlands before the ocean, so the boiling sea round the
        // Ashlands is the Ashlands biome's water; the Deep North comes after it, so its sea is
        // Ocean and is told apart by IsDeepnorth, as WorldSource does for the shared white.
        // (IsAshlands would run WorldGenerator's static constructor, which the offline preview
        // cannot load; the minimap's biomes are enough.)
        private Sea SeaAt(float x, float y)
        {
            int pixel = PixelAt(x, y);
            if (m_land[pixel] != 0 || m_sea[pixel] == 0) return Sea.None;
            float wx = x * MetresPerPixel - HalfExtent, wz = HalfExtent - y * MetresPerPixel;
            Land land = m_world.m_biome[m_world.Index(wx, wz)];
            if (land == Land.Ashlands) return Sea.Ashlands;
            if (land == Land.Ocean && !WorldGenerator.IsDeepnorth(wx, wz)) return Sea.Open;
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
