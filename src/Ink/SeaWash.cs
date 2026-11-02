using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The darker blue wash laid in a sea illustration so it stands out a little from the sea, as
    /// a colourist laid a second glaze of the sea's indigo in a map's monsters. It follows what the glyph scrapes, its body, not its box, ripples or
    /// foam: the glyph is drawn again from its SeaMark on a scratch canvas that keeps every
    /// erase's coverage (InkCanvas.m_scraped), grown a hair and softened. Being made from the mark
    /// alone, it is the same from a cached sheet, on the sheet (Paper.InkWithMips) and in every
    /// close-up (DetailTile), which only sample it. It never reaches more than Reach past the
    /// mark's box, inside what the fog shows or hides whole round it (Paper.SeaMarkFog).
    /// </summary>
    internal sealed class SeaWash
    {
        // The wash is built at Detail pixels a sheet pixel, so its edge stays smooth in close-ups.
        private const int Detail = 2;
        // Sheet pixels it may reach past the box: the fog keeps SeaMarkMargin (40 m) round the box
        // whole and blurs about 21 m of that, leaving some 3.6 px. Grow and the blur stay inside.
        private const int Reach = 3;
        // The scraped body (which already reaches under the outline's ink) can be grown by
        // GrowPixels detail pixels; then it is blurred BlurPasses times by a box BlurRadius wide
        // either side (detail pixels): a soft edge of about 0.7 sheet px, as a wet wash spreads a
        // little past the line. Grown half a sheet pixel and blurred twice as wide, it reads as a
        // blue glow round the outline in close-ups. Gain keeps a thin coil's middle at full
        // strength. EdgeFade (detail pixels) fades the blur's tail out before the area's edge.
        private const int GrowPixels = 0;
        private const int BlurRadius = 1;
        private const int BlurPasses = 3;
        private const float Gain = 1.5f;
        private const float EdgeFade = 3f;
        // Watercolour dries unevenly: the strength wavers by up to Mottle in blotches of about
        // MottleScale sheet pixels, set by the glyph's place.
        private const float Mottle = 0.3f;
        private const float MottleScale = 7f;

        // The wash's area in sheet pixels (left, top) and in detail pixels (width, height).
        private readonly float m_left, m_top;
        private readonly int m_width, m_height;
        private readonly float[] m_mask;

        private SeaWash(float left, float top, int width, int height, float[] mask)
        {
            m_left = left;
            m_top = top;
            m_width = width;
            m_height = height;
            m_mask = mask;
        }

        public float Left => m_left;
        public float Top => m_top;
        public float Right => m_left + m_width / (float)Detail;
        public float Bottom => m_top + m_height / (float)Detail;

        public bool Overlaps(float left, float top, float right, float bottom) =>
            Right >= left && m_left <= right && Bottom >= top && m_top <= bottom;

        // Worker thread. The wash of one sea illustration.
        public static SeaWash Build(SeaMark mark)
        {
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
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float most = 0f;
                    for (int dy = -GrowPixels; dy <= GrowPixels; dy++)
                    {
                        int sy = y + dy;
                        if (sy < 0 || sy >= height) continue;
                        for (int dx = -GrowPixels; dx <= GrowPixels; dx++)
                        {
                            int sx = x + dx;
                            if (sx < 0 || sx >= width || dx * dx + dy * dy > GrowPixels * GrowPixels * 2) continue;
                            most = Math.Max(most, scratch.m_scraped[sy * width + sx]);
                        }
                    }
                    mask[y * width + x] = most / 255f;
                }
            }
            if (mark.m_kind == SeaGlyphs.BonemawSerpent) FillArches(mask, width, height, (mark.m_y - top) * Detail);
            var row = new float[Math.Max(width, height)];
            for (int pass = 0; pass < BlurPasses; pass++) Blur(mask, width, height, row);

            int seed = Noise.HashInt(Mathf.RoundToInt(mark.m_x * 8f), Mathf.RoundToInt(mark.m_y * 8f), 73 + mark.m_kind);
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
                    mask[i] = Mathf.Clamp01(mask[i] * Gain) * mottle * Mathf.Clamp01(edge / EdgeFade);
                }
            }
            return new SeaWash(left, top, width, height, mask);
        }

        // How much of the wash lies at a sheet point (0 to 1), bilinear between its pixels.
        public float At(float sx, float sy)
        {
            float u = (sx - m_left) * Detail - 0.5f, v = (sy - m_top) * Detail - 0.5f;
            if (u <= -1f || v <= -1f || u >= m_width || v >= m_height) return 0f;
            int x0 = Mathf.FloorToInt(u), y0 = Mathf.FloorToInt(v);
            float fx = u - x0, fy = v - y0;
            float a = Sample(x0, y0), b = Sample(x0 + 1, y0), c = Sample(x0, y0 + 1), d = Sample(x0 + 1, y0 + 1);
            float upper = a + (b - a) * fx, lower = c + (d - c) * fx;
            return upper + (lower - upper) * fy;
        }

        private float Sample(int x, int y) =>
            x < 0 || y < 0 || x >= m_width || y >= m_height ? 0f : m_mask[y * m_width + x];

        // The bonemaw is bare bone, so its scrape alone gives a wash that hugs the bones like a
        // glow. Its body is washed instead: the water each arch of its backbone encloses down to
        // the waterline (detail row water), ribs and all, is filled as part of it. Whatever the
        // sea reaches from the area's edge without crossing bone or the waterline stays sea.
        private static void FillArches(float[] mask, int width, int height, float water)
        {
            // The line just above the waterline closes each arch between its two feet.
            int line = Mathf.Clamp(Mathf.FloorToInt(water) - 1, 0, height - 1);
            var sea = new bool[width * height];
            var open = new Stack<int>();
            for (int x = 0; x < width; x++)
            {
                open.Push(x);
                open.Push((height - 1) * width + x);
            }
            for (int y = 0; y < height; y++)
            {
                open.Push(y * width);
                open.Push(y * width + width - 1);
            }
            while (open.Count > 0)
            {
                int i = open.Pop();
                if (sea[i] || mask[i] >= 0.5f || i / width == line) continue;
                sea[i] = true;
                int x = i % width;
                if (x > 0) open.Push(i - 1);
                if (x < width - 1) open.Push(i + 1);
                if (i >= width) open.Push(i - width);
                if (i < (height - 1) * width) open.Push(i + width);
            }
            for (int i = 0; i < width * height; i++)
                if (!sea[i] && i / width < line) mask[i] = 1f;
        }

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
