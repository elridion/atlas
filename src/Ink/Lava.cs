using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The Ashlands' lava fields, drawn as shapes the way the woods are: an outline traced on the
    /// lava field (Woods.BuildLava), filled with an even engraved hatch of fine diagonal lines, as
    /// an engraver tones a plate. Drawn on the sheet only, under the glyphs; close-ups replay it.
    /// Streamlines of dashes along each field read as doodles. The vermilion
    /// wash under it comes from Paper.Washed.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // A hatch run is inked in pieces at most this long (canvas pixels), so each stroke's
        // bounding box, which the rasterizer scans whole, stays small on a diagonal.
        private const float LavaPiece = 12f;

        private void DrawLava()
        {
            int size = m_world.m_size;
            float half = size * 0.5f;
            float edgeSqr = EdgeRadius * EdgeRadius;
            float[] field = m_world.m_lavaField;
            DistanceField.Contour(size,
                (x, y) => field[y * size + x],
                0.5f,
                (x, y) =>
                {
                    float wx = (x + 1f - half) * m_world.m_pixelSize;
                    float wz = (y + 1f - half) * m_world.m_pixelSize;
                    return wx * wx + wz * wz >= edgeSqr;
                },
                (a, b) =>
                {
                    Vector2 p = SourceToCanvas(a), q = SourceToCanvas(b);
                    if (!IsLandAt(p.x, p.y) || !IsLandAt(q.x, q.y)) return;
                    m_canvas.Stroke(p.x, p.y, q.x, q.y, 1.2f);
                });

            // The bounds of all lava on the canvas, so the hatch only walks where lava can be.
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < field.Length; i++)
            {
                if (field[i] <= 0.5f) continue;
                Vector2 p = SourceToCanvas(new Vector2(i % size, i / size));
                minX = Mathf.Min(minX, p.x);
                maxX = Mathf.Max(maxX, p.x);
                minY = Mathf.Min(minY, p.y);
                maxY = Mathf.Max(maxY, p.y);
            }
            if (minX > maxX) return;
            const float pad = 4f;
            minX -= pad;
            minY -= pad;
            maxX += pad;
            maxY += pad;

            // Lines x + y = c, rising to the north-east (y grows south), walked one pixel at a
            // time and inked where they cross lava on land.
            float step = LavaHatch * Mathf.Sqrt(2f);
            for (float c = minX + minY; c <= maxX + maxY; c += step)
            {
                float x0 = Mathf.Max(minX, c - maxY), x1 = Mathf.Min(maxX, c - minY);
                bool inside = false;
                float startX = 0f;
                for (float x = x0; x <= x1 + 1f; x += 1f)
                {
                    bool lava = x <= x1 && LavaAt(x, c - x) && IsLandAt(x, c - x);
                    if (lava && !inside)
                    {
                        inside = true;
                        startX = x;
                    }
                    else if (!lava && inside)
                    {
                        inside = false;
                        Run(startX, x - 1f, c);
                    }
                }
            }

            void Run(float from, float to, float c)
            {
                if (to - from < 1f) return;
                for (float a = from; a < to; a += LavaPiece)
                {
                    float b = Mathf.Min(to, a + LavaPiece);
                    m_canvas.Stroke(a, c - a, b, c - b, Pen);
                }
            }
        }
    }
}
