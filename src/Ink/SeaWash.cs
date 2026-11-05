using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A sea mark's washes, as a colourist laid them in a map's monsters (the user's "take 3"):
    /// the water under the mark's waterline gets a wedge of a deeper blue, the shadow it casts
    /// trailing east (the map's shade side), and a figure's body above the water an iron grey
    /// (Paper.SeaMarkGlaze). The body follows what the glyph scrapes, not its box, ripples or
    /// foam: the glyph is drawn again from its SeaMark on a scratch canvas that keeps every
    /// erase's coverage (InkCanvas.m_scraped). The wedge's top lies on the waterline across the
    /// columns where the glyph breaks the surface. The bonemaw's bare bones and the leaping fish
    /// get the wedge only; boiling water gets its wedge and the grey inside its bubbles
    /// (BuildStandIn). Being made from the mark alone, the washes are
    /// the same from a cached sheet, on the sheet (Paper.InkWithMips) and in every close-up
    /// (DetailTile), which only sample them. They never reach more than Reach past the mark's
    /// box, inside the explored margin a mark needs to be drawn (Paper.SeaMarkSeen).
    /// </summary>
    internal sealed class SeaWash
    {
        // The washes are built at Detail pixels a sheet pixel, so their edges stay smooth in
        // close-ups.
        private const int Detail = 2;
        // Sheet pixels the washes' area reaches past the box: the wedge runs WedgeDepthMax under
        // the waterline, which lies inside the box, and the blur a little past that. A mark is
        // drawn only with Paper.SeaMarkMargin (70 m, about 13 px) round its box explored, of
        // which the glimpse softens about 21 m.
        private const int Reach = 10;
        // The washes are blurred BlurPasses times by a box BlurRadius wide either side (detail
        // pixels): a soft edge of about 0.7 sheet px, as a wet wash spreads a little past the
        // line. Gain keeps a thin coil's middle at full strength in the body. EdgeFade (detail
        // pixels) fades the blur's tail out before the area's edge.
        private const int BlurRadius = 1;
        private const int BlurPasses = 3;
        private const float Gain = 1.5f;
        private const float EdgeFade = 3f;
        // Watercolour dries unevenly: the strength wavers by up to Mottle in blotches of about
        // MottleScale sheet pixels, set by the glyph's place.
        private const float Mottle = 0.3f;
        private const float MottleScale = 7f;
        // Where the glyph breaks the surface: columns it scrapes within BreakBand sheet px above
        // its waterline.
        private const float BreakBand = 2f;
        // The wedge: one triangle across every break, padded WedgePad either side, WedgeDepth of
        // its top's width deep (between WedgeDepthMin and WedgeDepthMax sheet px), its tip moved
        // WedgeEast of the width east and fading to WedgeTip of its strength. Shallower or
        // centred, it read as a stain under the figure rather than its shadow on the water.
        private const float WedgePad = 1f;
        private const float WedgeDepth = 0.25f;
        private const float WedgeDepthMin = 5f;
        private const float WedgeDepthMax = 8f;
        private const float WedgeEast = 0.3f;
        private const float WedgeTip = 0.15f;
        // A stand-in's wedge is StandInGrow times as wide and deep, its top raised StandInLift
        // px over the front water line, so it still shows under the small marks (the user's call).
        private const float StandInGrow = 1.3f;
        private const float StandInLift = 1.2f;
        // A stand-in's wash area: StandInSide sheet px past its wedge's top on either side,
        // StandInAbove over its waterline and StandInBelow past its deepest wedge.
        private const float StandInSide = 4f;
        private const float StandInAbove = 3f;
        private const float StandInBelow = 3f;

        // The wash's area in sheet pixels (left, top) and in detail pixels (width, height).
        private readonly float m_left, m_top;
        private readonly int m_width, m_height;
        // The body's grey (null for none) and the blue wedge.
        private readonly float[] m_mask;
        private readonly float[] m_wedge;

        private SeaWash(float left, float top, int width, int height, float[] mask, float[] wedge)
        {
            m_left = left;
            m_top = top;
            m_width = width;
            m_height = height;
            m_mask = mask;
            m_wedge = wedge;
        }

        public float Left => m_left;
        public float Top => m_top;
        public float Right => m_left + m_width / (float)Detail;
        public float Bottom => m_top + m_height / (float)Detail;

        public bool Overlaps(float left, float top, float right, float bottom) =>
            Right >= left && m_left <= right && Bottom >= top && m_top <= bottom;

        // Worker thread. The washes of one sea mark.
        public static SeaWash Build(SeaMark mark)
        {
            if (SeaGlyphs.IsStandIn(mark.m_kind))
            {
                int shape = SeaGlyphs.StandInSeed(mark.m_x, mark.m_y);
                return BuildStandIn(mark.m_kind, mark.m_x, mark.m_y, shape, SeaGlyphs.StandInVariant(mark.m_kind, shape));
            }
            float boxLeft = (mark.m_west + InkPainter.HalfExtent) / InkPainter.MetresPerPixel;
            float boxRight = (mark.m_east + InkPainter.HalfExtent) / InkPainter.MetresPerPixel;
            float boxTop = (InkPainter.HalfExtent - mark.m_north) / InkPainter.MetresPerPixel;
            float boxBottom = (InkPainter.HalfExtent - mark.m_south) / InkPainter.MetresPerPixel;
            float left = Mathf.Floor(boxLeft) - Reach, top = Mathf.Floor(boxTop) - Reach;
            int width = ((int)Mathf.Ceil(boxRight) + Reach - (int)left) * Detail;
            int height = ((int)Mathf.Ceil(boxBottom) + Reach - (int)top) * Detail;
            var scratch = new InkCanvas(width, height, left, top, Detail) { m_scraped = new byte[width * height] };
            SeaGlyphs.Draw(scratch, mark.m_kind, mark.m_x, mark.m_y, mark.m_mirror, mark.m_scale);

            var mask = new float[width * height];
            for (int i = 0; i < mask.Length; i++) mask[i] = scratch.m_scraped[i] / 255f;
            float water = (mark.m_y - top) * Detail;
            // The breaks: runs of columns scraped within BreakBand above the waterline.
            int from = Mathf.Max(0, Mathf.FloorToInt(water - BreakBand * Detail)), to = Mathf.Min(height - 1, Mathf.FloorToInt(water) - 1);
            float first = float.MaxValue, last = float.MinValue;
            for (int x = 0; x < width; x++)
            {
                bool hit = false;
                for (int y = from; y <= to && !hit; y++)
                    if (mask[y * width + x] >= 0.5f) hit = true;
                if (!hit) continue;
                first = Math.Min(first, left + x / (float)Detail);
                last = Math.Max(last, left + (x + 1) / (float)Detail);
            }
            var wedge = new float[width * height];
            if (first < last) LayWedge(wedge, width, height, left, top, mark.m_y, first, last, 1f);
            // The bonemaw is bare bone: no body wash, only the water's wedge. The others' body is
            // washed above the water only, so the wedge alone lies under the waterline.
            if (mark.m_kind == SeaGlyphs.BonemawSerpent) Array.Clear(mask, 0, mask.Length);
            else
                for (int y = Mathf.Max(0, Mathf.CeilToInt(water)); y < height; y++)
                    for (int x = 0; x < width; x++) mask[y * width + x] = 0f;
            int seed = Noise.HashInt(Mathf.RoundToInt(mark.m_x * 8f), Mathf.RoundToInt(mark.m_y * 8f), 73 + mark.m_kind);
            Finish(mask, width, height, left, top, seed, true);
            Finish(wedge, width, height, left, top, seed + 1, false);
            return new SeaWash(left, top, width, height, mask, wedge);
        }

        // Worker thread. A stand-in's washes (SeaGlyphs.Boil or Fish drawn at x, y with seed and
        // variant): the wedge under its front water line and, for boiling water, the body's grey
        // filling its bubbles (the user's call), as a figure's body takes it. The fish keeps the
        // wedge alone: its solid body leaves no water inside to wash.
        public static SeaWash BuildStandIn(int kind, float x, float y, int seed, int variant)
        {
            SeaGlyphs.StandInWater(kind, x, y, seed, variant, out float x0, out float x1, out float water);
            float mid = (x0 + x1) * 0.5f, half = (x1 - x0) * 0.5f * StandInGrow;
            x0 = mid - half;
            x1 = mid + half;
            water -= StandInLift;
            float left = Mathf.Floor(x0) - StandInSide, top = Mathf.Floor(water) - StandInAbove;
            float right = Mathf.Ceil(x1) + StandInSide, bottom = top + WedgeDepthMax * StandInGrow + StandInAbove + StandInBelow;
            List<Vector4> bubbles = null;
            if (kind == SeaGlyphs.Boil)
            {
                bubbles = new List<Vector4>();
                SeaGlyphs.BoilBubbleEllipses(x, y, seed, variant, bubbles);
                // The area takes in every bubble and room for the blur round it.
                foreach (Vector4 b in bubbles)
                {
                    left = Mathf.Min(left, Mathf.Floor(b.x - b.z) - StandInAbove);
                    right = Mathf.Max(right, Mathf.Ceil(b.x + b.z) + StandInAbove);
                    top = Mathf.Min(top, Mathf.Floor(b.y - b.w) - StandInAbove);
                }
            }
            int width = (int)(right - left) * Detail;
            int height = (int)((bottom - top) * Detail);
            var wedge = new float[width * height];
            LayWedge(wedge, width, height, left, top, water, x0, x1, StandInGrow);
            Finish(wedge, width, height, left, top, seed, false);
            float[] body = null;
            if (bubbles != null)
            {
                body = new float[width * height];
                foreach (Vector4 b in bubbles) FillEllipse(body, width, height, left, top, b);
                Finish(body, width, height, left, top, seed + 1, true);
            }
            return new SeaWash(left, top, width, height, body, wedge);
        }

        // Fills an ellipse (centre x, y, radii z across and w up, sheet px) into a mask at Detail,
        // its edge anti-aliased over a detail pixel. It runs to the ring's middle, so the ring's
        // ink covers its edge.
        private static void FillEllipse(float[] mask, int width, int height, float left, float top, Vector4 e)
        {
            for (int py = 0; py < height; py++)
            {
                float dy = (top + (py + 0.5f) / Detail - e.y) / e.w;
                if (dy * dy >= 1.2f) continue;
                for (int px = 0; px < width; px++)
                {
                    float dx = (left + (px + 0.5f) / Detail - e.x) / e.z;
                    // Distance past the edge in detail pixels, roughly, by the smaller radius.
                    float past = (Mathf.Sqrt(dx * dx + dy * dy) - 1f) * Mathf.Min(e.z, e.w) * Detail;
                    float cover = Mathf.Clamp01(0.5f - past);
                    int i = py * width + px;
                    if (cover > mask[i]) mask[i] = cover;
                }
            }
        }

        // Lays the wedge (coverage times its strength, fading to the tip) under the waterline
        // (sheet y water) across x0 to x1, depths grown by grow.
        private static void LayWedge(float[] wedge, int width, int height, float left, float top, float water, float x0, float x1, float grow)
        {
            x0 -= WedgePad;
            x1 += WedgePad;
            float w = x1 - x0;
            float depth = Mathf.Clamp(w * WedgeDepth, WedgeDepthMin * grow, WedgeDepthMax * grow);
            float apex = (x0 + x1) * 0.5f + WedgeEast * w;
            for (int py = 0; py < height; py++)
            {
                float sy = top + (py + 0.5f) / Detail;
                float t = (sy - water) / depth;
                if (t < 0f || t > 1f) continue;
                float l = Mathf.Lerp(x0, apex, t), r = Mathf.Lerp(x1, apex, t);
                float strength = Mathf.Lerp(1f, WedgeTip, t);
                for (int px = 0; px < width; px++)
                {
                    float sx = left + (px + 0.5f) / Detail;
                    float cover = Mathf.Clamp01(Math.Min(sx - l, r - sx) * Detail + 0.5f);
                    if (cover <= 0f) continue;
                    int i = py * width + px;
                    wedge[i] = Math.Max(wedge[i], cover * strength);
                }
            }
        }

        // Blur, Gain (the body only), mottle and the edge fade.
        private static void Finish(float[] mask, int width, int height, float left, float top, int seed, bool gain)
        {
            var row = new float[Math.Max(width, height)];
            for (int pass = 0; pass < BlurPasses; pass++) Blur(mask, width, height, row);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (mask[i] <= 0f) continue;
                    float sx = left + (x + 0.5f) / Detail, sy = top + (y + 0.5f) / Detail;
                    float mottle = 1f - Mottle * Noise.Value(sx / MottleScale, sy / MottleScale, seed);
                    // The blur's last faint tail is faded out over the area's outer EdgeFade
                    // pixels, so it ends softly short of the fog's margin instead of at a cut.
                    float edge = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y)) + 0.5f;
                    mask[i] = Mathf.Clamp01(mask[i] * (gain ? Gain : 1f)) * mottle * Mathf.Clamp01(edge / EdgeFade);
                }
            }
        }

        // How much of the body's grey lies at a sheet point (0 to 1), bilinear between pixels.
        public float At(float sx, float sy) => m_mask == null ? 0f : Sample(m_mask, sx, sy);

        // How much of the blue wedge lies at a sheet point (0 to 1).
        public float WedgeAt(float sx, float sy) => Sample(m_wedge, sx, sy);

        private float Sample(float[] plane, float sx, float sy)
        {
            float u = (sx - m_left) * Detail - 0.5f, v = (sy - m_top) * Detail - 0.5f;
            if (u <= -1f || v <= -1f || u >= m_width || v >= m_height) return 0f;
            int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
            float fx = u - x0, fy = v - y0;
            float a = Pixel(plane, x0, y0), b = Pixel(plane, x0 + 1, y0), c = Pixel(plane, x0, y0 + 1), d = Pixel(plane, x0 + 1, y0 + 1);
            float upper = a + (b - a) * fx, lower = c + (d - c) * fx;
            return upper + (lower - upper) * fy;
        }

        private float Pixel(float[] plane, int x, int y) =>
            x < 0 || y < 0 || x >= m_width || y >= m_height ? 0f : plane[y * m_width + x];

        // A box blur BlurRadius either side, rows then columns, nothing beyond the edge.
        private static void Blur(float[] values, int width, int height, float[] row)
        {
            float span = 2 * BlurRadius + 1;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float sum = 0f;
                    for (int k = -BlurRadius; k <= BlurRadius; k++)
                    {
                        int at = x + k;
                        if (at >= 0 && at < width) sum += values[y * width + at];
                    }
                    row[x] = sum / span;
                }
                Array.Copy(row, 0, values, y * width, width);
            }
            for (int x = 0; x < width; x++)
            {
                for (int y = 0; y < height; y++)
                {
                    float sum = 0f;
                    for (int k = -BlurRadius; k <= BlurRadius; k++)
                    {
                        int at = y + k;
                        if (at >= 0 && at < height) sum += values[at * width + x];
                    }
                    row[y] = sum / span;
                }
                for (int y = 0; y < height; y++) values[y * width + x] = row[y];
            }
        }
    }
}
