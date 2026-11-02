using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A square sheet of ink coverage (0 bare parchment, 255 solid ink) with anti-aliased pens.
    /// Coordinates are continuous canvas pixels, y growing southward; pixel (x, y) has its centre at
    /// (x + 0.5, y + 0.5). Ink only ever darkens (the darker of old and new wins), and Erase scrapes
    /// back to bare parchment, which is how a glyph hides whatever lies behind it.
    /// </summary>
    internal sealed class InkCanvas
    {
        public readonly int m_size;
        public readonly byte[] m_ink;

        public InkCanvas(int size)
        {
            m_size = size;
            m_ink = new byte[size * size];
        }

        // A straight pen stroke with round ends.
        public void Stroke(float x0, float y0, float x1, float y1, float width, float strength = 1f) =>
            Capsule(x0, y0, x1, y1, width, strength, erase: false);

        // Scrapes a band with round ends back to bare parchment.
        public void Scrape(float x0, float y0, float x1, float y1, float width) =>
            Capsule(x0, y0, x1, y1, width, 1f, erase: true);

        private void Capsule(float x0, float y0, float x1, float y1, float width, float strength, bool erase)
        {
            float r = width * 0.5f;
            float dx = x1 - x0, dy = y1 - y0;
            float lengthSqr = dx * dx + dy * dy;
            int minX = Math.Max(0, (int)(Math.Min(x0, x1) - r - 1f));
            int maxX = Math.Min(m_size - 1, (int)(Math.Max(x0, x1) + r + 1f));
            int minY = Math.Max(0, (int)(Math.Min(y0, y1) - r - 1f));
            int maxY = Math.Min(m_size - 1, (int)(Math.Max(y0, y1) + r + 1f));
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    float t = lengthSqr > 0f ? Mathf.Clamp01(((px - x0) * dx + (py - y0) * dy) / lengthSqr) : 0f;
                    float ex = px - (x0 + t * dx), ey = py - (y0 + t * dy);
                    float coverage = r + 0.5f - Mathf.Sqrt(ex * ex + ey * ey);
                    if (erase) Lighten(y * m_size + x, coverage);
                    else Darken(y * m_size + x, coverage * strength);
                }
            }
        }

        public void Polyline(IList<Vector2> points, float width, float strength = 1f)
        {
            for (int i = 1; i < points.Count; i++)
                Stroke(points[i - 1].x, points[i - 1].y, points[i].x, points[i].y, width, strength);
        }

        public void Dot(float cx, float cy, float radius, float strength = 1f)
        {
            Ellipse(cx, cy, radius, radius, strength, erase: false);
        }

        public void Ring(float cx, float cy, float rx, float ry, float width, float strength = 1f)
        {
            float r = width * 0.5f;
            int minX = Math.Max(0, (int)(cx - rx - r - 1f));
            int maxX = Math.Min(m_size - 1, (int)(cx + rx + r + 1f));
            int minY = Math.Max(0, (int)(cy - ry - r - 1f));
            int maxY = Math.Min(m_size - 1, (int)(cy + ry + r + 1f));
            float scale = Math.Min(rx, ry);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float ex = (x + 0.5f - cx) / rx, ey = (y + 0.5f - cy) / ry;
                    // Distance to an ellipse, approximated by the normalised radius; exact for circles.
                    float distance = Math.Abs(Mathf.Sqrt(ex * ex + ey * ey) - 1f) * scale;
                    Darken(y * m_size + x, (r + 0.5f - distance) * strength);
                }
            }
        }

        public void EraseEllipse(float cx, float cy, float rx, float ry)
        {
            Ellipse(cx, cy, rx, ry, 1f, erase: true);
        }

        private void Ellipse(float cx, float cy, float rx, float ry, float strength, bool erase)
        {
            int minX = Math.Max(0, (int)(cx - rx - 1f));
            int maxX = Math.Min(m_size - 1, (int)(cx + rx + 1f));
            int minY = Math.Max(0, (int)(cy - ry - 1f));
            int maxY = Math.Min(m_size - 1, (int)(cy + ry + 1f));
            float scale = Math.Min(rx, ry);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float ex = (x + 0.5f - cx) / rx, ey = (y + 0.5f - cy) / ry;
                    float coverage = (1f - Mathf.Sqrt(ex * ex + ey * ey)) * scale + 0.5f;
                    if (erase) Lighten(y * m_size + x, coverage);
                    else Darken(y * m_size + x, coverage * strength);
                }
            }
        }

        // Convex polygons only, wound either way.
        public void Fill(IList<Vector2> polygon, float strength = 1f) => Polygon(polygon, strength, erase: false);

        public void Erase(IList<Vector2> polygon) => Polygon(polygon, 1f, erase: true);

        private void Polygon(IList<Vector2> polygon, float strength, bool erase)
        {
            int count = polygon.Count;
            if (count < 3) return;
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
            int minX = Math.Max(0, (int)(minXf - 1f)), maxX = Math.Min(m_size - 1, (int)(maxXf + 1f));
            int minY = Math.Max(0, (int)(minYf - 1f)), maxY = Math.Min(m_size - 1, (int)(maxYf + 1f));
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
                    if (erase) Lighten(y * m_size + x, coverage);
                    else Darken(y * m_size + x, coverage * strength);
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
            if (coverage >= 1f)
            {
                m_ink[index] = 0;
                return;
            }
            m_ink[index] = (byte)(m_ink[index] * (1f - coverage));
        }
    }
}
