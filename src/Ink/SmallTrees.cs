using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The small-tree grove's tree (Smalltree Meadows, Kind.SmallTree): one small crown whose top
    /// is three rounded lobes, the classic map's leafy-tree sign, on a short trunk, its east side
    /// shaded (the user's pick, F of a design round). It is about 0.7-0.9 of a broadleaf's
    /// footprint, not smaller: the map's pen is a sheet pixel at every zoom, so a crown under
    /// about 3 tree units closes up into a black dot, and its shade spans less of the crown than
    /// a broadleaf's, or the small crowns read solid.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // The lobed crown: half width, height above and below its middle (tree units), the
        // lobes' depth as a share of the radius, and its shade's span against a broadleaf's.
        private const float SmallCrownX = 2.45f;
        private const float SmallCrownTop = 2.6f;
        private const float SmallCrownBottom = 1.5f;
        private const float SmallLobeDepth = 0.36f;
        private const float SmallShade = 0.8f;
        private readonly List<Vector2> m_lobed = new List<Vector2>(64);

        // The lobed crown's middle, radii and trunk, from the seed alone, shared by the drawing
        // and by SmallCrown.
        private static void SmallShape(float x, float y, float s, int seed,
            out float cx, out float cy, out float rx, out float top, out float bottom)
        {
            rx = SmallCrownX * s * Vary(seed, 110, 0.1f);
            top = SmallCrownTop * s * Vary(seed, 111, 0.1f);
            bottom = SmallCrownBottom * s * Vary(seed, 112, 0.1f);
            float trunk = Mathf.Lerp(1.4f, 2.2f, Unit(seed, 113)) * s;
            cx = x + (Unit(seed, 114) - 0.5f) * 0.5f * s;
            cy = y - trunk - bottom;
        }

        private void SmallTree(float x, float y, float s, int seed)
        {
            SmallShape(x, y, s, seed, out float cx, out float cy, out float rx, out float top, out float bottom);
            LobedBody(cx, cy, rx, top, bottom, 3, SmallLobeDepth, Mathf.PI * 0.9f, Mathf.PI * 2.1f, seed, x, y, SmallShade);
            m_canvas.Stroke(x, cy + bottom * 0.85f, x + (cx - x) * 0.3f, y, Pen);
        }

        // The ellipse the lobed crown fills, from its underside (the troughs' curve, 1 - depth of
        // bottom) to its lobes' tops, for HideCoveredCrowns and the pins' cover.
        private static Crown SmallCrown(float x, float y, float s, int seed)
        {
            SmallShape(x, y, s, seed, out float cx, out float cy, out float rx, out float top, out float bottom);
            float under = bottom * (1f - SmallLobeDepth);
            return new Crown
            {
                m_cx = cx, m_cy = cy - (top - under) * 0.5f, m_rx = rx, m_ry = (top + under) * 0.5f,
                m_wobble = 1f, m_seed = seed,
            };
        }

        // Scrapes, marks as wood, shades (span scaled by shade) and inks a lobed outline
        // (LobedOutline).
        private void LobedBody(float cx, float cy, float rx, float top, float bottom, int lobes, float depth,
            float from, float to, int seed, float x, float y, float shade)
        {
            LobedOutline(cx, cy, rx, top, bottom, lobes, depth, from, to, seed, false);
            m_canvas.EraseOutline(m_lobed, 0.12f * Mathf.Min(rx, top) + 0.7f * m_pen);
            BeginWood(x, y);
            m_canvas.EraseOutline(m_lobed, BodyGrow * m_pen);
            m_canvas.m_markBody = BodyKind.None;
            // The east of the troughs' shape, two half ellipses on one axis, so convex.
            float k = 1f - depth;
            m_points.Clear();
            const int steps = 8;
            float span = CrownShade * shade * Mathf.Lerp(0.75f, 1.2f, Unit(seed, 45));
            float turn = (Unit(seed, 46) - 0.5f) * 0.5f;
            for (int i = 0; i <= steps; i++)
            {
                float a = turn + Mathf.Lerp(-span, span, (float)i / steps);
                float sin = Mathf.Sin(a);
                m_points.Add(new Vector2(cx + Mathf.Cos(a) * rx * k, cy + sin * (sin < 0f ? top : bottom) * k));
            }
            m_canvas.Fill(m_points);
            LobedOutline(cx, cy, rx, top, bottom, lobes, depth, from, to, seed, true);
            m_canvas.Polyline(m_lobed, Mathf.Lerp(1f, 1.2f, Unit(seed, 44)));
        }

        // A hand-traced outline whose arc from angle `from` to `to` (screen angles: pi west,
        // 1.5 pi north, 2 pi east) is cut into rounded lobes with cusps between, the rest the
        // troughs' smooth curve; the upper half of radius top, the lower bottom.
        private void LobedOutline(float cx, float cy, float rx, float top, float bottom, int lobes, float depth,
            float from, float to, int seed, bool overlap)
        {
            m_lobed.Clear();
            int steps = lobes * 10 + 12;
            float start = Mathf.PI * 0.5f;
            int last = overlap ? steps + 1 : steps - 1;
            for (int i = 0; i <= last; i++)
            {
                float a = start + Mathf.PI * 2f * i / steps;
                float wrapped = a;
                while (wrapped < from) wrapped += Mathf.PI * 2f;
                while (wrapped > from + Mathf.PI * 2f) wrapped -= Mathf.PI * 2f;
                float f = 1f - depth;
                if (wrapped <= to)
                {
                    float phase = (wrapped - from) / (to - from) * lobes;
                    int lobe = Mathf.Min(lobes - 1, (int)phase);
                    float bump = Mathf.Pow(Mathf.Sin(Mathf.PI * (phase - lobe)), 0.6f);
                    f += depth * bump * Mathf.Lerp(0.75f, 1.15f, Unit(seed, 120 + lobe));
                }
                f *= 1f + 0.4f * (CrownBump(seed, a, 1f) - 1f);
                float sin = Mathf.Sin(a);
                m_lobed.Add(new Vector2(cx + Mathf.Cos(a) * rx * f, cy + sin * (sin < 0f ? top : bottom) * f));
            }
        }
    }
}
