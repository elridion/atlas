using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The pen glyphs, all drawn upward from their base point (x, y), the foot that sorts them.
    /// A glyph with a body first scrapes its outline back to parchment, then inks it, so whatever
    /// lies behind it (to the north) disappears behind its shape. Lines are never thinner than Pen
    /// and never fainter than full ink, so the marks keep their weight at the opening zoom.
    /// </summary>
    internal sealed partial class InkPainter
    {
        private void DrawGlyph(Glyph g)
        {
            switch (g.m_kind)
            {
                case Kind.Peak:
                    Peak(g.m_x, g.m_y, g.m_size, g.m_seed);
                    MarkSolid(g.m_x - g.m_size * 0.5f, g.m_y - g.m_size, g.m_x + g.m_size * 0.62f, g.m_y);
                    break;
                case Kind.Hill: Hill(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Broadleaf: Broadleaf(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Conifer: Conifer(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.PlainsTree: PlainsTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.MarshTuft: MarshTuft(g.m_x, g.m_y, g.m_size); break;
                case Kind.DeadTree: DeadTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Spire:
                    Spire(g.m_x, g.m_y, g.m_size, g.m_seed);
                    MarkSolid(g.m_x - 4.5f * g.m_size, g.m_y - 21f * g.m_size, g.m_x + 4.5f * g.m_size, g.m_y);
                    break;
                case Kind.GiantTree: GiantTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Volcano: Volcano(g.m_x, g.m_y, g.m_size); break;
                case Kind.Fissure: Fissure(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Wave: Wave(g.m_x, g.m_y, g.m_size); break;
                case Kind.IceFloe: IceFloe(g.m_x, g.m_y, g.m_size, g.m_seed); break;
            }
        }

        private static float Vary(int seed, int k, float amount) => 1f + (Unit(seed, k) - 0.5f) * 2f * amount;

        private void Poly(params Vector2[] points)
        {
            m_points.Clear();
            m_points.AddRange(points);
        }

        // A peak: lower than it is wide, the summit right of centre so the lit western flank is the
        // long one, and the eastern face a wide solid wedge. Packed in rows, the peak in front hides
        // the lower half of the one behind, so the wedge must stay wide near the top to survive.
        private void Peak(float x, float y, float width, int seed)
        {
            float height = width * 0.78f * Vary(seed, 10, 0.2f);
            var apex = new Vector2(x + width * Unit(seed, 11) * 0.12f, y - height);
            var left = new Vector2(x - width * 0.5f, y);
            var right = new Vector2(x + width * 0.5f, y);
            var foot = new Vector2(apex.x + width * 0.06f, y);
            // The western flank sags into the body. Polygons must be convex, so the straight triangle
            // is erased and the hollow flank drawn on it; the sliver outside reads as a pen's halo.
            Vector2 leftMid = Vector2.Lerp(apex, left, 0.55f) + new Vector2(width * 0.09f, height * 0.04f);

            Poly(apex, right, left);
            m_canvas.Erase(m_points);
            Poly(apex, right, foot);
            m_canvas.Fill(m_points);
            // Where the shadow spills to the foot of the western face.
            Vector2 from = Vector2.Lerp(apex, foot, 0.7f);
            m_canvas.Stroke(from.x, from.y, from.x - width * 0.16f, y - height * 0.03f, 1.1f);
            m_canvas.Stroke(apex.x, apex.y, leftMid.x, leftMid.y, 1.5f);
            m_canvas.Stroke(leftMid.x, leftMid.y, left.x, left.y, 1.5f);
            m_canvas.Stroke(apex.x, apex.y, right.x, right.y, 1.5f);
        }

        // A hill: an open, unshaded hump.
        private void Hill(float x, float y, float width, int seed)
        {
            float height = width * 0.42f * Vary(seed, 10, 0.15f);
            m_points.Clear();
            const int steps = 10;
            for (int i = 0; i <= steps; i++)
            {
                float a = Mathf.PI * i / steps;
                m_points.Add(new Vector2(x - Mathf.Cos(a) * width * 0.5f, y - Mathf.Sin(a) * height));
            }
            m_canvas.Erase(m_points);
            m_canvas.Polyline(m_points, 1.2f);
        }

        // A broadleaf tree: an egg-shaped crown on a short trunk. Packed densely, the crowns hide
        // the trunks behind them, so only the southern edge of a wood shows trunks.
        private void Broadleaf(float x, float y, float s, int seed)
        {
            float r = 2.6f * s * Vary(seed, 10, 0.15f);
            float ry = r * 1.25f;
            float cx = x + (Unit(seed, 11) - 0.5f) * 0.6f * s;
            float cy = y - 1.6f * s - ry;
            m_canvas.EraseEllipse(cx, cy, r + 0.7f, ry + 0.7f);
            m_canvas.Ring(cx, cy, r, ry, 1.1f);
            m_canvas.Stroke(x, cy + ry, x, y, Pen);
        }

        // A pine: a narrow pointed triangle with its eastern half filled, on a trunk tick.
        private void Conifer(float x, float y, float s, int seed)
        {
            float height = 9.5f * s * Vary(seed, 10, 0.18f);
            float half = 2.8f * s * Vary(seed, 11, 0.12f);
            float baseY = y - 1.6f * s;
            var apex = new Vector2(x, baseY - height);
            var left = new Vector2(x - half, baseY);
            var right = new Vector2(x + half, baseY);
            Poly(apex, right + new Vector2(0.6f, 0.5f), left + new Vector2(-0.6f, 0.5f));
            m_canvas.Erase(m_points);
            Poly(apex, right, new Vector2(x + half * 0.15f, baseY));
            m_canvas.Fill(m_points);
            m_canvas.Stroke(apex.x, apex.y, left.x, left.y, 1.1f);
            m_canvas.Stroke(apex.x, apex.y, right.x, right.y, 1.1f);
            m_canvas.Stroke(left.x, left.y, right.x, right.y, Pen);
            m_canvas.Stroke(x, baseY, x, y, Pen);
        }

        // A plains tree: a small round crown on a short trunk, standing alone.
        private void PlainsTree(float x, float y, float s, int seed)
        {
            float r = 2.2f * s * Vary(seed, 10, 0.15f);
            float cy = y - 2f * s - r;
            m_canvas.EraseEllipse(x, cy, r + 0.7f, r + 0.7f);
            m_canvas.Ring(x, cy, r, r, 1.1f);
            m_canvas.Stroke(x, cy + r, x, y, Pen);
        }

        // A marsh tuft: three blades fanning from a line of water.
        private void MarshTuft(float x, float y, float s)
        {
            m_canvas.Stroke(x - 3f * s, y, x + 3f * s, y, Pen);
            m_canvas.Stroke(x, y - 0.4f, x, y - 4.5f * s, Pen);
            m_canvas.Stroke(x - 0.5f * s, y - 0.4f, x - 2.6f * s, y - 3.6f * s, Pen);
            m_canvas.Stroke(x + 0.5f * s, y - 0.4f, x + 2.6f * s, y - 3.6f * s, Pen);
        }

        // A dead tree: a crooked bare trunk with forked branches.
        private void DeadTree(float x, float y, float s, int seed)
        {
            float lean = (Unit(seed, 10) - 0.5f) * 1.2f * s;
            float top = y - 9f * s;
            m_canvas.Stroke(x, y, x + lean, top, 1.1f);
            m_canvas.Stroke(x + lean * 0.55f, y - 5f * s, x - 3f * s, y - 8.5f * s, Pen);
            m_canvas.Stroke(x + lean * 0.7f, y - 6.5f * s, x + 3.2f * s, y - 9.6f * s, Pen);
            m_canvas.Stroke(x - 1.6f * s, y - 7f * s, x - 2.2f * s, y - 10f * s, Pen);
        }

        // A Mistlands rock pillar: steep sides narrowing to a broken top, its eastern face hatched
        // with upright strokes, so it reads as a crag rather than a peak or a pine.
        private void Spire(float x, float y, float s, int seed)
        {
            float height = Mathf.Lerp(13f, 20f, Unit(seed, 10)) * s;
            float baseHalf = 4.2f * s;
            float topHalf = 2f * s;
            float top = y - height;
            var baseLeft = new Vector2(x - baseHalf, y);
            var baseRight = new Vector2(x + baseHalf, y);
            var topLeft = new Vector2(x - topHalf, top + Unit(seed, 11) * 1.5f * s);
            var topRight = new Vector2(x + topHalf, top + Unit(seed, 12) * 1.5f * s);
            // The broken top: a notch between two knuckles of rock.
            var notch = new Vector2(x + (Unit(seed, 13) - 0.5f) * topHalf, top + 1.8f * s);
            Poly(baseLeft, topLeft, topRight, baseRight);
            m_canvas.Erase(m_points);
            for (int i = 0; i < 3; i++)
            {
                float t = (i + 1) / 4f;
                float hx = Mathf.Lerp(x + topHalf * 0.2f, x + baseHalf * 0.85f, t);
                float hy = Mathf.Lerp(top, y, t * 0.6f) + 1.5f * s;
                m_canvas.Stroke(hx, hy, hx, y - 0.8f * s, Pen);
            }
            m_canvas.Stroke(baseLeft.x, baseLeft.y, topLeft.x, topLeft.y, 1.4f);
            m_canvas.Stroke(baseRight.x, baseRight.y, topRight.x, topRight.y, 1.4f);
            m_canvas.Stroke(topLeft.x, topLeft.y, notch.x, notch.y, 1.2f);
            m_canvas.Stroke(notch.x, notch.y, topRight.x, topRight.y, 1.2f);
        }

        // A giant Mistlands tree: a tall trunk under a canopy of three overlapping crowns.
        private void GiantTree(float x, float y, float s, int seed)
        {
            float height = 12f * s * Vary(seed, 10, 0.12f);
            float r = 2.9f * s;
            float crown = y - height;
            m_canvas.Stroke(x, crown + r, x, y, 1.4f);
            // Outer crowns first, the middle one last and slightly higher, in front of both.
            DrawCrown(x - 3.4f * s, crown + 0.7f * s, r);
            DrawCrown(x + 3.4f * s, crown + 0.7f * s, r);
            DrawCrown(x, crown - 0.5f * s, r);

            void DrawCrown(float cx, float cy, float radius)
            {
                m_canvas.EraseEllipse(cx, cy, radius + 0.7f, radius * 0.8f + 0.7f);
                m_canvas.Ring(cx, cy, radius, radius * 0.8f, 1.1f);
            }
        }

        // A volcano: a flat-topped cone with a crater rim and a curl of smoke.
        private void Volcano(float x, float y, float s)
        {
            float top = y - 11f * s;
            var baseLeft = new Vector2(x - 10f * s, y);
            var baseRight = new Vector2(x + 10f * s, y);
            var rimLeft = new Vector2(x - 2.6f * s, top);
            var rimRight = new Vector2(x + 2.6f * s, top);
            Poly(baseLeft, rimLeft, rimRight, baseRight);
            m_canvas.Erase(m_points);
            Poly(new Vector2(x + 1f * s, top), rimRight, baseRight, new Vector2(x + 3f * s, y));
            m_canvas.Fill(m_points);
            m_canvas.Stroke(baseLeft.x, baseLeft.y, rimLeft.x, rimLeft.y, 1.5f);
            m_canvas.Stroke(baseRight.x, baseRight.y, rimRight.x, rimRight.y, 1.5f);
            m_canvas.Ring(x, top, 2.6f * s, 0.9f * s, 1.1f);
            Poly(new Vector2(x, top - 1f * s), new Vector2(x + 1.4f * s, top - 4f * s), new Vector2(x - 0.6f * s, top - 7f * s),
                new Vector2(x + 1.2f * s, top - 10f * s), new Vector2(x + 0.2f * s, top - 13f * s));
            m_canvas.Polyline(m_points, Pen);
        }

        // A lava crack: a jagged line with sharp, alternating kinks and a short side branch, laid
        // roughly along the flow, heavy enough to show through the hatching. A smooth wobble with a
        // forked tip read as a flying gull.
        private void Fissure(float x, float y, float s, int seed)
        {
            float angle = (Unit(seed, 10) - 0.5f) * 1.1f;
            var along = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var across = new Vector2(-along.y, along.x);
            m_points.Clear();
            for (int i = 0; i <= 5; i++)
            {
                float t = (i - 2.5f) * 2f * s;
                float kink = (i % 2 == 0 ? 1f : -1f) * (0.6f + Unit(seed, 11 + i) * 0.8f) * s;
                m_points.Add(new Vector2(x, y) + along * t + across * kink);
            }
            m_canvas.Polyline(m_points, 1.3f);
            Vector2 fork = m_points[2];
            float side = Unit(seed, 20) < 0.5f ? 1f : -1f;
            Vector2 tip = fork + (across * side * 2.2f + along * 1.2f) * s;
            m_canvas.Stroke(fork.x, fork.y, tip.x, tip.y, Pen);
        }

        // A wave mark far out at sea.
        private void Wave(float x, float y, float s)
        {
            Poly(new Vector2(x - 4f * s, y), new Vector2(x - 2f * s, y - 1.6f * s), new Vector2(x, y),
                new Vector2(x + 2f * s, y - 1.6f * s), new Vector2(x + 4f * s, y));
            m_canvas.Polyline(m_points, Pen);
        }

        // An ice floe: a small irregular outline lying on the water. Its body (an ellipse just
        // around the outline) is scraped back to parchment first, so ripple lines stop at its edge.
        private void IceFloe(float x, float y, float s, int seed)
        {
            m_points.Clear();
            const int corners = 6;
            float size = Mathf.Lerp(2.5f, 5.5f, Unit(seed, 10)) * s;
            m_canvas.EraseEllipse(x, y, size + 0.8f, size * 0.7f + 0.8f);
            for (int i = 0; i <= corners; i++)
            {
                int k = i % corners;
                float a = (k + Unit(seed, 11 + k) * 0.6f) / corners * Mathf.PI * 2f;
                float r = size * Mathf.Lerp(0.7f, 1f, Unit(seed, 20 + k));
                m_points.Add(new Vector2(x + Mathf.Cos(a) * r, y + Mathf.Sin(a) * r * 0.7f));
            }
            m_canvas.Polyline(m_points, Pen);
        }

        // A stone on the forest floor: a small flattened outline.
        private void Stone(float x, float y, float s, int seed)
        {
            float rx = 1.4f * s * Vary(seed, 10, 0.25f);
            float ry = rx * 0.7f;
            m_canvas.EraseEllipse(x, y - ry, rx + 0.5f, ry + 0.5f);
            m_canvas.Ring(x, y - ry, rx, ry, Pen);
        }

        // A wavelet of standing water: three small crests, a smaller cousin of the sea's wave mark.
        private void Wavelet(float x, float y, float s)
        {
            Poly(new Vector2(x - 3f * s, y), new Vector2(x - 2f * s, y - 0.9f * s), new Vector2(x - 1f * s, y),
                new Vector2(x, y - 0.9f * s), new Vector2(x + 1f * s, y), new Vector2(x + 2f * s, y - 0.9f * s),
                new Vector2(x + 3f * s, y));
            m_canvas.Polyline(m_points, Pen);
        }

        // A tuft of grass: two short strokes.
        private void Grass(float x, float y, float s)
        {
            m_canvas.Stroke(x - 1f * s, y, x - 1.6f * s, y - 2.6f * s, Pen);
            m_canvas.Stroke(x + 0.8f * s, y, x + 1.2f * s, y - 2.8f * s, Pen);
        }
    }
}
