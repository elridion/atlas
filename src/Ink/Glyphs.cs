using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The pen glyphs, all drawn upward from their base point (x, y), the foot that sorts them.
    /// A glyph with a body first scrapes its outline back to parchment, then inks it, so whatever
    /// lies behind it (to the north) disappears behind its shape.
    /// </summary>
    internal sealed partial class InkPainter
    {
        private void DrawGlyph(Glyph g)
        {
            switch (g.m_kind)
            {
                case Kind.Peak: Peak(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Hill: Hill(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Broadleaf: Broadleaf(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Conifer: Conifer(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.PlainsTree: PlainsTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.MarshTuft: MarshTuft(g.m_x, g.m_y, g.m_size); break;
                case Kind.DeadTree: DeadTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Spire: Spire(g.m_x, g.m_y, g.m_size, g.m_seed); break;
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

        // A peak: two slightly hollow flanks meeting at the summit, the eastern face shaded solid.
        private void Peak(float x, float y, float width, int seed)
        {
            float height = width * 0.95f * Vary(seed, 10, 0.15f);
            var apex = new Vector2(x + (Unit(seed, 11) - 0.5f) * 0.24f * width, y - height);
            var left = new Vector2(x - width * 0.5f, y);
            var right = new Vector2(x + width * 0.5f, y);
            var split = new Vector2(x + width * 0.08f, y);
            // Hollow flanks: each side bends inward a little halfway down.
            Vector2 leftMid = Vector2.Lerp(apex, left, 0.5f) + new Vector2(width * 0.05f, -width * 0.02f);
            Vector2 rightMid = Vector2.Lerp(apex, right, 0.5f) + new Vector2(-width * 0.05f, -width * 0.02f);

            Poly(apex, right, left);
            m_canvas.Erase(m_points);
            Poly(apex, right, split);
            m_canvas.Fill(m_points);
            m_canvas.Stroke(apex.x, apex.y, leftMid.x, leftMid.y, 1.3f);
            m_canvas.Stroke(leftMid.x, leftMid.y, left.x, left.y, 1.3f);
            m_canvas.Stroke(apex.x, apex.y, rightMid.x, rightMid.y, 1.3f);
            m_canvas.Stroke(rightMid.x, rightMid.y, right.x, right.y, 1.3f);
            // Snow line on the tallest peaks: a short notched stroke under the summit.
            if (width > 20f * G)
            {
                float sy = apex.y + height * 0.3f;
                float sx = apex.x - height * 0.14f;
                m_canvas.Stroke(sx, sy, apex.x - height * 0.04f, sy - height * 0.06f, 0.8f);
                m_canvas.Stroke(apex.x - height * 0.04f, sy - height * 0.06f, apex.x + height * 0.02f, sy + height * 0.02f, 0.8f);
            }
        }

        // A hill: an open hump with two short shading strokes on its eastern side.
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
            m_canvas.Polyline(m_points, 1.1f);
            m_canvas.Stroke(x + width * 0.16f, y - height * 0.5f, x + width * 0.26f, y - height * 0.08f, 0.7f, 0.8f);
            m_canvas.Stroke(x + width * 0.3f, y - height * 0.38f, x + width * 0.36f, y - height * 0.06f, 0.7f, 0.8f);
        }

        // A broadleaf tree: a round crown on a short trunk, shaded low on the east.
        private void Broadleaf(float x, float y, float s, int seed)
        {
            float r = 2.7f * s * Vary(seed, 10, 0.15f);
            float cx = x + (Unit(seed, 11) - 0.5f) * 0.6f * s;
            float cy = y - 1.6f * s - r;
            m_canvas.EraseEllipse(cx, cy, r + 0.7f, r + 0.7f);
            m_canvas.Ring(cx, cy, r, r, 0.95f);
            m_canvas.Stroke(cx + r * 0.3f, cy + r * 0.35f, cx + r * 0.62f, cy + r * 0.05f, 0.7f, 0.8f);
            m_canvas.Stroke(x, cy + r, x, y, 0.9f);
        }

        // A pine: a narrow pointed triangle on a trunk tick.
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
            m_canvas.Stroke(apex.x, apex.y, left.x, left.y, 0.9f);
            m_canvas.Stroke(apex.x, apex.y, right.x, right.y, 0.9f);
            m_canvas.Stroke(left.x, left.y, right.x, right.y, 0.8f);
            m_canvas.Stroke(apex.x + 0.3f, apex.y + height * 0.35f, x + half * 0.45f, baseY - 0.4f, 0.7f, 0.8f);
            m_canvas.Stroke(x, baseY, x, y, 0.9f);
        }

        // A plains tree: a lone flat crown on a tall thin trunk.
        private void PlainsTree(float x, float y, float s, int seed)
        {
            float rx = 3.6f * s * Vary(seed, 10, 0.15f);
            float ry = 1.3f * s;
            float cy = y - 5.6f * s;
            m_canvas.EraseEllipse(x, cy, rx + 0.6f, ry + 0.6f);
            m_canvas.Ring(x, cy, rx, ry, 0.9f);
            m_canvas.Stroke(x, cy + ry, x, y, 0.9f);
        }

        // A marsh tuft: three blades fanning from a line of water.
        private void MarshTuft(float x, float y, float s)
        {
            m_canvas.Stroke(x - 3f * s, y, x + 3f * s, y, 0.8f);
            m_canvas.Stroke(x, y - 0.4f, x, y - 4.5f * s, 0.75f);
            m_canvas.Stroke(x - 0.5f * s, y - 0.4f, x - 2.6f * s, y - 3.6f * s, 0.75f);
            m_canvas.Stroke(x + 0.5f * s, y - 0.4f, x + 2.6f * s, y - 3.6f * s, 0.75f);
        }

        // A dead tree: a crooked bare trunk with forked branches.
        private void DeadTree(float x, float y, float s, int seed)
        {
            float lean = (Unit(seed, 10) - 0.5f) * 1.2f * s;
            float top = y - 9f * s;
            m_canvas.Stroke(x, y, x + lean, top, 1.1f);
            m_canvas.Stroke(x + lean * 0.55f, y - 5f * s, x - 3f * s, y - 8.5f * s, 0.8f);
            m_canvas.Stroke(x + lean * 0.7f, y - 6.5f * s, x + 3.2f * s, y - 9.6f * s, 0.8f);
            m_canvas.Stroke(x - 1.6f * s, y - 7f * s, x - 2.2f * s, y - 10f * s, 0.7f);
        }

        // A Mistlands spire: a tall thin crag, its eastern face shaded.
        private void Spire(float x, float y, float s, int seed)
        {
            float height = Mathf.Lerp(15f, 24f, Unit(seed, 10)) * s;
            float half = 3.2f * s;
            var apex = new Vector2(x + (Unit(seed, 11) - 0.5f) * 1.6f * s, y - height);
            var left = new Vector2(x - half, y);
            var right = new Vector2(x + half, y);
            var split = new Vector2(x + 0.6f * s, y);
            Poly(apex, right, left);
            m_canvas.Erase(m_points);
            Poly(apex, right, split);
            m_canvas.Fill(m_points);
            m_canvas.Stroke(apex.x, apex.y, left.x, left.y, 1.1f);
            m_canvas.Stroke(apex.x, apex.y, right.x, right.y, 1.1f);
        }

        // A giant Mistlands tree: a tall trunk under a wide umbrella crown.
        private void GiantTree(float x, float y, float s, int seed)
        {
            float height = 12f * s * Vary(seed, 10, 0.12f);
            float half = 5.5f * s * Vary(seed, 11, 0.12f);
            float crown = y - height + 1f * s;
            var a = new Vector2(x - half, crown);
            var b = new Vector2(x - half * 0.45f, crown - 2.8f * s);
            var c = new Vector2(x + half * 0.45f, crown - 2.8f * s);
            var d = new Vector2(x + half, crown);
            Poly(a, b, c, d);
            m_canvas.Erase(m_points);
            m_canvas.Polyline(m_points, 1.1f);
            m_canvas.Stroke(a.x, a.y, d.x, d.y, 0.8f);
            m_canvas.Stroke(x, crown, x, y, 1.3f);
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
            m_canvas.Stroke(baseLeft.x, baseLeft.y, rimLeft.x, rimLeft.y, 1.3f);
            m_canvas.Stroke(baseRight.x, baseRight.y, rimRight.x, rimRight.y, 1.3f);
            m_canvas.Ring(x, top, 2.6f * s, 0.9f * s, 0.9f);
            Poly(new Vector2(x, top - 1f * s), new Vector2(x + 1.4f * s, top - 4f * s), new Vector2(x - 0.6f * s, top - 7f * s),
                new Vector2(x + 1.2f * s, top - 10f * s), new Vector2(x + 0.2f * s, top - 13f * s));
            m_canvas.Polyline(m_points, 0.8f, 0.8f);
        }

        // A lava crack: a jagged line across the flow.
        private void Fissure(float x, float y, float s, int seed)
        {
            m_points.Clear();
            float px = x - 5f * s, py = y;
            m_points.Add(new Vector2(px, py));
            for (int i = 0; i < 4; i++)
            {
                px += 2.5f * s;
                py += (Unit(seed, 10 + i) - 0.5f) * 3.2f * s;
                m_points.Add(new Vector2(px, py));
            }
            m_canvas.Polyline(m_points, 1.0f);
            Vector2 fork = m_points[2];
            m_canvas.Stroke(fork.x, fork.y, fork.x + 1.2f * s, fork.y - 2.4f * s, 0.7f);
        }

        // A wave mark far out at sea.
        private void Wave(float x, float y, float s)
        {
            Poly(new Vector2(x - 4f * s, y), new Vector2(x - 2f * s, y - 1.6f * s), new Vector2(x, y),
                new Vector2(x + 2f * s, y - 1.6f * s), new Vector2(x + 4f * s, y));
            m_canvas.Polyline(m_points, 0.85f, 0.85f);
        }

        // An ice floe: a small irregular outline.
        private void IceFloe(float x, float y, float s, int seed)
        {
            m_points.Clear();
            const int corners = 6;
            float size = Mathf.Lerp(2.5f, 5.5f, Unit(seed, 10)) * s;
            for (int i = 0; i <= corners; i++)
            {
                int k = i % corners;
                float a = (k + Unit(seed, 11 + k) * 0.6f) / corners * Mathf.PI * 2f;
                float r = size * Mathf.Lerp(0.7f, 1f, Unit(seed, 20 + k));
                m_points.Add(new Vector2(x + Mathf.Cos(a) * r, y + Mathf.Sin(a) * r * 0.7f));
            }
            m_canvas.Polyline(m_points, 0.8f);
        }

        // A tuft of grass: two short strokes.
        private void Grass(float x, float y, float s)
        {
            m_canvas.Stroke(x - 1f * s, y, x - 1.6f * s, y - 2.6f * s, 0.7f, 0.8f);
            m_canvas.Stroke(x + 0.8f * s, y, x + 1.2f * s, y - 2.8f * s, 0.7f, 0.8f);
        }
    }
}
