using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The sea's illustrations (SeaGlyphs), placed on the sheet after the waves and floes (user,
    ///): few, each in a wide stretch of open water of its own sea, ranked by how far
    /// the water reaches round it and spread over the map. The serpents and the leviathan keep to
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
        // (about 470 m), and its box IllustrationClear past its own half diagonal, so it stands
        // far out at sea, well clear of coasts, islets and the waves round them.
        private const int OpenCell = 4;
        private const float OpenWaterMin = 90f;
        private const float IllustrationClear = 40f;
        // No ink at all (waves, floes, anything) within IllustrationInkGap of a glyph's box, and
        // glyphs IllustrationSpacing apart (about 3.2 km), so they spread over the oceans instead
        // of gathering in the widest one.
        private const float IllustrationInkGap = 8f;
        private const float IllustrationSpacing = 600f;
        // Hash salt of the placement rule (see Scatter's salts).
        private const int IllustrationSalt = 61;

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
            int firstKind = (int)(Unit(salt, 1) * 3f) % 3;
            InkLayer layer = m_canvas.m_layer;
            m_canvas.m_layer = InkLayer.Illustration;
            foreach (OpenSpot spot in spots)
            {
                var at = new Vector2((spot.m_cx + 0.5f) * OpenCell, (spot.m_cy + 0.5f) * OpenCell);
                if (Crowded(placed, at, IllustrationSpacing)) continue;
                Sea sea = SeaAt(at.x, at.y);
                int kind = PickIllustration(sea, counts, firstKind);
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
        // fewest times first (from a seed-set one), so each world gets a mix; -1 when the sea has
        // none left.
        private static int PickIllustration(Sea sea, int[] counts, int first)
        {
            if (sea == Sea.Ashlands) return counts[SeaGlyphs.BonemawSerpent] < IllustrationsEach ? SeaGlyphs.BonemawSerpent : -1;
            if (sea != Sea.Open) return -1;
            int[] open = { SeaGlyphs.SeaSerpent, SeaGlyphs.SerpentCoil, SeaGlyphs.Leviathan };
            int best = -1;
            for (int k = 0; k < open.Length; k++)
            {
                int kind = open[(first + k) % open.Length];
                if (counts[kind] >= IllustrationsEach) continue;
                if (best < 0 || counts[kind] < counts[best]) best = kind;
            }
            return best;
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
