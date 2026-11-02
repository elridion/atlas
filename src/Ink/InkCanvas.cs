using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A sheet of ink coverage (0 bare parchment, 255 solid ink) with anti-aliased pens. Callers
    /// draw in sheet coordinates: continuous pixels of the whole 4096 sheet, y growing southward,
    /// pixel (x, y) centred on (x + 0.5, y + 0.5). A canvas may cover only part of the sheet at a
    /// magnification (a detail tile), so the same calls draw the same marks, sharper. Ink only ever
    /// darkens (the darker of old and new wins), and Erase scrapes back to bare parchment, which is
    /// how a glyph hides whatever lies behind it. A recorder, when set, keeps every call so it can
    /// be replayed into a detail tile later.
    /// </summary>
    // What a peak's or spire's body takes the wash of; each kind but None has its own body mask.
    internal enum BodyKind : byte
    {
        None,
        Mountain,
        North,
        Mist,
    }

    internal sealed class InkCanvas
    {
        public readonly int m_width;
        public readonly int m_height;
        public readonly byte[] m_ink;
        // Where a peak's body lies (0 to 255), which takes the mountains' wash whatever the
        // ground under it: peaks rise north from their feet over the land beyond a range.
        public readonly byte[] m_body;
        // The same for Deep North peaks, which take the Deep North's wash instead.
        public readonly byte[] m_bodyNorth;
        // The same for the Mistlands' spires, which take their rock's colour (Paper.BodyColor).
        // A plane of its own rather than a kind byte beside one shared mask: where two kinds meet
        // (a spire in front of a peak, a skirt over another body) a pixel holds a share of each,
        // which one mask and one kind could not keep.
        public readonly byte[] m_bodyMist;
        // While not None, Erase marks the scraped polygon as that kind of body (and clears the
        // others); any other erase clears all, so the glyph in front decides.
        public BodyKind m_markBody;
        // When set, every erase also keeps its coverage here (the most of any erase), so a glyph's
        // scraped silhouette can be read back: a sea illustration's wash follows it (SeaWash).
        public byte[] m_scraped;
        // While Tint runs: the mask it raises, and its fade (sheet y where it starts, its length).
        private BodyKind m_tintKind;
        private float m_tintTop, m_tintFade, m_tintStrength;

        private readonly float m_originX;
        private readonly float m_originY;
        private readonly float m_scale;
        private readonly List<Vector2> m_transformed = new List<Vector2>(16);
        private InkRecord m_record;

        // What the calls drawn from now on belong to, kept with them in the record.
        public InkLayer m_layer;
        // Pen and band widths are multiplied by this: a detail tile drawn at a finer level of detail
        // uses finer pens, so lines do not swell with the zoom.
        public float m_penScale = 1f;

        public InkCanvas(int size) : this(size, size, 0f, 0f, 1f)
        {
        }

        // A canvas of width x height pixels showing the sheet from (originX, originY) at scale
        // pixels per sheet pixel.
        public InkCanvas(int width, int height, float originX, float originY, float scale)
        {
            m_width = width;
            m_height = height;
            m_ink = new byte[width * height];
            m_body = new byte[width * height];
            m_bodyNorth = new byte[width * height];
            m_bodyMist = new byte[width * height];
            m_originX = originX;
            m_originY = originY;
            m_scale = scale;
        }

        // The body mask a kind marks (None has none).
        public byte[] BodyMask(BodyKind kind)
        {
            switch (kind)
            {
                case BodyKind.Mountain: return m_body;
                case BodyKind.North: return m_bodyNorth;
                case BodyKind.Mist: return m_bodyMist;
                default: return null;
            }
        }

        public void Record(InkRecord record) => m_record = record;

        // Marks the calls from here to EndGroup as one glyph standing at footY, in the record.
        public void BeginGroup(float footY) => m_record?.BeginGroup(footY);

        public void EndGroup() => m_record?.EndGroup();

        private float X(float x) => (x - m_originX) * m_scale;
        private float Y(float y) => (y - m_originY) * m_scale;

        // A straight pen stroke with round ends.
        public void Stroke(float x0, float y0, float x1, float y1, float width, float strength = 1f)
        {
            m_record?.Stroke(m_layer, x0, y0, x1, y1, width, strength, erase: false);
            Capsule(X(x0), Y(y0), X(x1), Y(y1), width * m_penScale * m_scale, strength, erase: false);
        }

        // Scrapes a band with round ends back to bare parchment.
        public void Scrape(float x0, float y0, float x1, float y1, float width)
        {
            m_record?.Stroke(m_layer, x0, y0, x1, y1, width, 1f, erase: true);
            Capsule(X(x0), Y(y0), X(x1), Y(y1), width * m_penScale * m_scale, 1f, erase: true);
        }

        public void Polyline(IList<Vector2> points, float width, float strength = 1f)
        {
            for (int i = 1; i < points.Count; i++)
                Stroke(points[i - 1].x, points[i - 1].y, points[i].x, points[i].y, width, strength);
        }

        public void Dot(float cx, float cy, float radius, float strength = 1f)
        {
            m_record?.Ellipse(m_layer, cx, cy, radius, radius, strength, erase: false);
            Ellipse(X(cx), Y(cy), radius * m_scale, radius * m_scale, strength, erase: false);
        }

        public void EraseEllipse(float cx, float cy, float rx, float ry)
        {
            m_record?.Ellipse(m_layer, cx, cy, rx, ry, 1f, erase: true);
            Ellipse(X(cx), Y(cy), rx * m_scale, ry * m_scale, 1f, erase: true);
        }

        public void Ring(float cx, float cy, float rx, float ry, float width, float strength = 1f)
        {
            m_record?.Ring(m_layer, cx, cy, rx, ry, width, strength);
            RingPixels(X(cx), Y(cy), rx * m_scale, ry * m_scale, width * m_penScale * m_scale, strength);
        }

        // Convex polygons only, wound either way.
        public void Fill(IList<Vector2> polygon, float strength = 1f)
        {
            m_record?.Polygon(m_layer, polygon, strength, erase: false);
            Polygon(polygon, strength, erase: false);
        }

        public void Erase(IList<Vector2> polygon)
        {
            m_record?.Polygon(m_layer, polygon, 1f, erase: true, body: m_markBody);
            Polygon(polygon, 1f, erase: true);
        }

        // Scrapes a simple polygon, concave or not, back to bare parchment in one pass, its edge
        // pushed out by grow sheet pixels; marks body as Erase does. A concave outline scraped as
        // a fan of convex triangles leaves a faint line along every shared edge: each triangle
        // covers a pixel on it only partly, and the masks keep the larger share while the ink
        // keeps the product of what is left, so neither comes out whole.
        public void EraseOutline(IList<Vector2> outline, float grow)
        {
            m_record?.Outline(m_layer, outline, grow, m_markBody);
            m_erasingPolygon = true;
            try
            {
                OutlinePixels(outline, grow * m_scale);
            }
            finally
            {
                m_erasingPolygon = false;
            }
        }

        // Coverage from the signed distance to the nearest edge segment, inside by the even-odd
        // rule, so it is exact for concave outlines too.
        private void OutlinePixels(IList<Vector2> sheetPolygon, float grow)
        {
            int count = sheetPolygon.Count;
            if (count < 3) return;
            m_transformed.Clear();
            foreach (Vector2 p in sheetPolygon) m_transformed.Add(new Vector2(X(p.x), Y(p.y)));
            List<Vector2> polygon = m_transformed;
            float minXf = float.MaxValue, maxXf = float.MinValue, minYf = float.MaxValue, maxYf = float.MinValue;
            foreach (Vector2 p in polygon)
            {
                minXf = Math.Min(minXf, p.x);
                maxXf = Math.Max(maxXf, p.x);
                minYf = Math.Min(minYf, p.y);
                maxYf = Math.Max(maxYf, p.y);
            }
            float reach = Math.Max(grow, 0f) + 1f;
            int minX = Math.Max(0, (int)(minXf - reach)), maxX = Math.Min(m_width - 1, (int)(maxXf + reach));
            int minY = Math.Max(0, (int)(minYf - reach)), maxY = Math.Min(m_height - 1, (int)(maxYf + reach));
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    bool inside = false;
                    float nearest = float.MaxValue;
                    for (int i = 0; i < count; i++)
                    {
                        Vector2 a = polygon[i], b = polygon[(i + 1) % count];
                        if ((a.y > py) != (b.y > py) && px < a.x + (py - a.y) * (b.x - a.x) / (b.y - a.y)) inside = !inside;
                        float ex = b.x - a.x, ey = b.y - a.y;
                        float lengthSqr = ex * ex + ey * ey;
                        float t = lengthSqr > 0f ? Mathf.Clamp01(((px - a.x) * ex + (py - a.y) * ey) / lengthSqr) : 0f;
                        float dx = px - (a.x + t * ex), dy = py - (a.y + t * ey);
                        nearest = Math.Min(nearest, dx * dx + dy * dy);
                    }
                    float distance = Mathf.Sqrt(nearest);
                    Lighten(y * m_width + x, (inside ? distance : -distance) + grow + 0.5f);
                }
            }
        }

        // Raises a body mask over a convex polygon without touching the ink, at strength by sheet
        // y = top and fading to nothing at top + fade: the skirt by which a peak's wash bleeds a
        // little into the ground below its foot.
        public void Tint(IList<Vector2> polygon, BodyKind kind, float top, float fade, float strength)
        {
            m_record?.Tint(m_layer, polygon, kind, top, fade, strength);
            m_tintKind = kind;
            m_tintTop = top;
            m_tintFade = Math.Max(fade, 1e-3f);
            m_tintStrength = strength;
            try
            {
                PolygonPixels(polygon, 1f, erase: false);
            }
            finally
            {
                m_tintKind = BodyKind.None;
            }
        }

        private void TintPixel(int index, float coverage)
        {
            if (coverage <= 0f) return;
            float sheetY = (index / m_width + 0.5f) / m_scale + m_originY;
            // Smoothstep, so the wash leaves the foot level with the body and runs out without a
            // visible edge at either end.
            float fade = Mathf.Clamp01(1f - (sheetY - m_tintTop) / m_tintFade);
            fade = fade * fade * (3f - 2f * fade);
            int value = (int)(Math.Min(coverage, 1f) * fade * m_tintStrength * 255f + 0.5f);
            byte[] mark = BodyMask(m_tintKind);
            if (value > mark[index]) mark[index] = (byte)value;
        }

        private void Capsule(float x0, float y0, float x1, float y1, float width, float strength, bool erase)
        {
            float r = width * 0.5f;
            float dx = x1 - x0, dy = y1 - y0;
            float lengthSqr = dx * dx + dy * dy;
            int minX = Math.Max(0, (int)(Math.Min(x0, x1) - r - 1f));
            int maxX = Math.Min(m_width - 1, (int)(Math.Max(x0, x1) + r + 1f));
            int minY = Math.Max(0, (int)(Math.Min(y0, y1) - r - 1f));
            int maxY = Math.Min(m_height - 1, (int)(Math.Max(y0, y1) + r + 1f));
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float t = lengthSqr > 0f ? Mathf.Clamp01(((px - x0) * dx + (py - y0) * dy) / lengthSqr) : 0f;
                    float ex = px - (x0 + t * dx), ey = py - (y0 + t * dy);
                    float coverage = r + 0.5f - Mathf.Sqrt(ex * ex + ey * ey);
                    if (erase) Lighten(y * m_width + x, coverage);
                    else Darken(y * m_width + x, coverage * strength);
                }
            }
        }

        private void RingPixels(float cx, float cy, float rx, float ry, float width, float strength)
        {
            float r = width * 0.5f;
            int minX = Math.Max(0, (int)(cx - rx - r - 1f));
            int maxX = Math.Min(m_width - 1, (int)(cx + rx + r + 1f));
            int minY = Math.Max(0, (int)(cy - ry - r - 1f));
            int maxY = Math.Min(m_height - 1, (int)(cy + ry + r + 1f));
            float scale = Math.Min(rx, ry);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float ex = (x + 0.5f - cx) / rx, ey = (y + 0.5f - cy) / ry;
                    // Distance to an ellipse, approximated by the normalised radius; exact for circles.
                    float distance = Math.Abs(Mathf.Sqrt(ex * ex + ey * ey) - 1f) * scale;
                    Darken(y * m_width + x, (r + 0.5f - distance) * strength);
                }
            }
        }

        private void Ellipse(float cx, float cy, float rx, float ry, float strength, bool erase)
        {
            int minX = Math.Max(0, (int)(cx - rx - 1f));
            int maxX = Math.Min(m_width - 1, (int)(cx + rx + 1f));
            int minY = Math.Max(0, (int)(cy - ry - 1f));
            int maxY = Math.Min(m_height - 1, (int)(cy + ry + 1f));
            float scale = Math.Min(rx, ry);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float ex = (x + 0.5f - cx) / rx, ey = (y + 0.5f - cy) / ry;
                    float coverage = (1f - Mathf.Sqrt(ex * ex + ey * ey)) * scale + 0.5f;
                    if (erase) Lighten(y * m_width + x, coverage);
                    else Darken(y * m_width + x, coverage * strength);
                }
            }
        }

        private void Polygon(IList<Vector2> sheetPolygon, float strength, bool erase)
        {
            m_erasingPolygon = erase;
            try
            {
                PolygonPixels(sheetPolygon, strength, erase);
            }
            finally
            {
                m_erasingPolygon = false;
            }
        }

        private void PolygonPixels(IList<Vector2> sheetPolygon, float strength, bool erase)
        {
            int count = sheetPolygon.Count;
            if (count < 3) return;
            m_transformed.Clear();
            foreach (Vector2 p in sheetPolygon) m_transformed.Add(new Vector2(X(p.x), Y(p.y)));
            List<Vector2> polygon = m_transformed;
            float minXf = float.MaxValue, maxXf = float.MinValue, minYf = float.MaxValue, maxYf = float.MinValue;
            float area = 0f;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = polygon[i], b = polygon[(i + 1) % count];
                area += a.x * b.y - b.x * a.y;
                minXf = Math.Min(minXf, a.x);
                maxXf = Math.Max(maxXf, a.x);
                minYf = Math.Min(minYf, a.y);
                maxYf = Math.Max(maxYf, a.y);
            }
            if (Math.Abs(area) < 1e-4f) return;
            float winding = Math.Sign(area);
            int minX = Math.Max(0, (int)(minXf - 1f)), maxX = Math.Min(m_width - 1, (int)(maxXf + 1f));
            int minY = Math.Max(0, (int)(minYf - 1f)), maxY = Math.Min(m_height - 1, (int)(maxYf + 1f));
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    // Signed distance to the nearest edge line, positive inside.
                    float inside = float.MaxValue;
                    for (int i = 0; i < count; i++)
                    {
                        Vector2 a = polygon[i], b = polygon[(i + 1) % count];
                        float ex = b.x - a.x, ey = b.y - a.y;
                        float length = Mathf.Sqrt(ex * ex + ey * ey);
                        if (length < 1e-5f) continue;
                        float d = winding * (ex * (py - a.y) - ey * (px - a.x)) / length;
                        if (d < inside) inside = d;
                    }
                    float coverage = inside + 0.5f;
                    if (m_tintKind != BodyKind.None) TintPixel(y * m_width + x, coverage);
                    else if (erase) Lighten(y * m_width + x, coverage);
                    else Darken(y * m_width + x, coverage * strength);
                }
            }
        }

        private void Darken(int index, float coverage)
        {
            if (coverage <= 0f) return;
            int value = (int)(Math.Min(coverage, 1f) * 255f + 0.5f);
            if (value > m_ink[index]) m_ink[index] = (byte)value;
        }

        private void Lighten(int index, float coverage)
        {
            if (coverage <= 0f) return;
            float keep = 1f - Math.Min(coverage, 1f);
            if (m_scraped != null)
            {
                int scraped = (int)(Math.Min(coverage, 1f) * 255f + 0.5f);
                if (scraped > m_scraped[index]) m_scraped[index] = (byte)scraped;
            }
            BodyKind marking = m_erasingPolygon ? m_markBody : BodyKind.None;
            Fade(m_body, index, keep, marking == BodyKind.Mountain);
            Fade(m_bodyNorth, index, keep, marking == BodyKind.North);
            Fade(m_bodyMist, index, keep, marking == BodyKind.Mist);
            if (marking != BodyKind.None)
            {
                int value = (int)(Math.Min(coverage, 1f) * 255f + 0.5f);
                byte[] mark = BodyMask(marking);
                if (value > mark[index]) mark[index] = (byte)value;
            }
            if (coverage >= 1f)
            {
                m_ink[index] = 0;
                return;
            }
            m_ink[index] = (byte)(m_ink[index] * (1f - coverage));
        }

        // Scrapes a body mask by keep, except the one the erase is marking, which it only raises.
        private static void Fade(byte[] mask, int index, float keep, bool marking)
        {
            if (!marking) mask[index] = (byte)(mask[index] * keep);
        }

        // Set while a polygon is erased, so only polygon erases mark body.
        private bool m_erasingPolygon;
    }
}
