using System.Collections.Generic;
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
        // A round crown's shade spans this half-angle either side of due east (radians): 1.2 fills
        // about the eastern quarter, as the pines fill their eastern half.
        private const float CrownShade = 1.05f;
        // How far down the eastern face (share of the flank) a peak's solid wedge reaches before
        // the hatching takes over: enough for ranges to read dark at the opening zoom.
        private const float PeakWedge = 0.45f;
        // A spire's wedge reaches further down its east face: the Mistlands' rock is black.
        private const float SpireWedge = 0.65f;
        // How far (sheet pixels) a peak's wash runs out below its foot, and how strongly at the
        // foot (0 to 1), fading to nothing at the skirt's end.
        private const float PeakSkirt = 2f;
        private const float PeakBleed = 0.6f;
        private readonly List<Vector2> m_west = new List<Vector2>(8);
        private readonly List<Vector2> m_east = new List<Vector2>(8);

        private void DrawGlyph(Glyph g)
        {
            switch (g.m_kind)
            {
                case Kind.Peak:
                    Peak(g.m_x, g.m_y, g.m_size, g.m_seed);
                    break;
                case Kind.Hill: Hill(g.m_x, g.m_y, g.m_size, g.m_seed, g.m_lone); break;
                case Kind.Broadleaf: Broadleaf(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Conifer: Conifer(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.PlainsTree: PlainsTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.DeadTree: DeadTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Spire:
                    Spire(g.m_x, g.m_y, g.m_size, g.m_seed);
                    break;
                case Kind.GiantTree: GiantTree(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Volcano: Volcano(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Crag: Crag(g.m_x, g.m_y, g.m_size, g.m_seed); break;
                case Kind.Mist: MistBank(g.m_x, g.m_y - MistGap, g.m_seed); break;
                case Kind.IceFloe: IceFloe(g.m_x, g.m_y, g.m_size, g.m_seed); break;
            }
        }

        private static float Vary(int seed, int k, float amount) => 1f + (Unit(seed, k) - 0.5f) * 2f * amount;

        private void Poly(params Vector2[] points)
        {
            m_points.Clear();
            m_points.AddRange(points);
        }

        // A peak, drawn as on the classic hand-inked maps rather than as one stamped triangle: a
        // jagged ridge line whose shape varies by seed (a plain crag, a shoulder on the east, or a
        // twin summit on the west), the summit right of centre so the lit western flank is the long
        // one, and the eastern face hatched with slanting strokes under a small solid wedge at the
        // top, which keeps ranges dark at the opening zoom. Proportions, a blunt top, a snow line
        // and the hatching hand vary by seed too. The apex stays where the summit placement puts
        // it (x + 0.12 width at most, PeakHeightOf above the foot).
        // Without body, the peak's fill keeps the ground's own wash (the Ashlands' crags).
        private void Peak(float x, float y, float width, int seed, bool body = true)
        {
            float height = PeakHeightOf(width, seed);
            var apex = new Vector2(x + width * Unit(seed, 11) * 0.12f, y - height);
            var left = new Vector2(x - width * 0.5f * Vary(seed, 13, 0.12f), y);
            var right = new Vector2(x + width * 0.5f * Vary(seed, 14, 0.12f), y);
            float style = Unit(seed, 12);

            // The west flank, foot to apex: sagging into the body, with a jag or a second summit.
            m_west.Clear();
            m_west.Add(left);
            if (style < 0.25f)
            {
                m_west.Add(Flank(left, apex, 0.35f, -0.05f * width));
                m_west.Add(Flank(left, apex, 0.55f, 0.1f * height));
                m_west.Add(Flank(left, apex, 0.68f, -0.06f * width));
            }
            else
            {
                m_west.Add(Flank(left, apex, 0.45f, -(0.04f + 0.05f * Unit(seed, 15)) * width));
                m_west.Add(Flank(left, apex, 0.78f, (Unit(seed, 16) - 0.5f) * 0.05f * width));
            }
            // A blunt, flat-topped summit on some peaks: the west flank ends short of the apex.
            float top = Unit(seed, 19);
            if (top < 0.2f) m_west.Add(new Vector2(apex.x - width * 0.08f, apex.y + height * 0.03f));
            m_west.Add(apex);
            // The east flank, apex to foot: a notch and a shoulder, or a couple of jags.
            m_east.Clear();
            m_east.Add(apex);
            if (style > 0.55f)
            {
                m_east.Add(Flank(apex, right, 0.25f, -0.05f * width));
                m_east.Add(Flank(apex, right, 0.45f, 0.08f * height));
                m_east.Add(Flank(apex, right, 0.72f, (Unit(seed, 17) - 0.5f) * 0.05f * width));
            }
            else
            {
                m_east.Add(Flank(apex, right, 0.3f, (Unit(seed, 17) - 0.5f) * 0.06f * width));
                m_east.Add(Flank(apex, right, 0.65f, (Unit(seed, 18) - 0.5f) * 0.06f * width));
            }
            m_east.Add(right);

            // The outline, foot to foot.
            m_points.Clear();
            m_points.AddRange(m_west);
            for (int i = 1; i < m_east.Count; i++) m_points.Add(m_east[i]);
            // The body is scraped to the outline itself, not its convex hull, so the fill matches
            // the drawn contour (a hull spilled past every sag and notch). The
            // outline is a mountain silhouette above its base, seen whole from a point low in its
            // middle, so it is filled as a fan of convex triangles from there, each grown a
            // little so no seam is left between them. Deep North peaks take its wash.
            BodyKind kind = !body ? BodyKind.None : IsNorthAt(x, y) ? BodyKind.North : BodyKind.Mountain;
            m_canvas.m_markBody = kind;
            var hub = new Vector2(apex.x, y - height * 0.12f);
            m_fan.Clear();
            m_fan.AddRange(m_points);
            for (int i = 0; i < m_fan.Count; i++)
            {
                Vector2 a = m_fan[i], b = m_fan[(i + 1) % m_fan.Count];
                Poly(Grow(hub, hub, a, b), Grow(a, hub, a, b), Grow(b, hub, a, b));
                m_canvas.Erase(m_points);
            }
            m_canvas.m_markBody = BodyKind.None;
            // A soft edge of PeakSkirt sheet pixels below the foot, where the peak's wash runs out
            // into the ground, so the base is not cut hard; the body stays fully the peak's (user,
            //: the base leads into the other biome, not the other biome into the
            // mountain). A deep skirt and a fading body both read wrong, so this stays a hairline.
            if (kind != BodyKind.None)
            {
                Poly(new Vector2(left.x + width * 0.05f, y - 0.5f), new Vector2(right.x - width * 0.05f, y - 0.5f),
                    new Vector2(right.x - width * 0.02f, y + PeakSkirt), new Vector2(left.x + width * 0.02f, y + PeakSkirt));
                m_canvas.Tint(m_points, kind, y - 0.5f, PeakSkirt + 0.5f, PeakBleed);
            }

            // The shadow: a solid wedge from the apex, then hatching down the eastern face.
            var foot = new Vector2(apex.x + width * 0.06f, y);
            Vector2 wedgeEast = Along(m_east, PeakWedge);
            Poly(apex, wedgeEast, Vector2.Lerp(apex, foot, PeakWedge));
            m_canvas.Fill(m_points);
            // Three hatching hands, by seed: gently slanting, level, or steep; dense or open.
            float hatch = Unit(seed, 21);
            float drop = hatch < 0.4f ? 0.07f : hatch < 0.7f ? 0.01f : 0.16f;
            float spacing = Unit(seed, 22) < 0.5f ? 2.2f : 3f;
            int strokes = Mathf.Clamp(Mathf.RoundToInt(height / (spacing * m_pen)), 4, 12);
            for (int k = 0; k < strokes; k++)
            {
                float t = Mathf.Lerp(PeakWedge, 0.95f, (k + 0.5f) / strokes);
                Vector2 from = Along(m_east, t);
                float shadowX = Mathf.Lerp(apex.x, foot.x, (from.y - apex.y) / height);
                float reach = Mathf.Lerp(0.6f, 0.95f, Unit(seed, 30 + k));
                var to = new Vector2(Mathf.Lerp(from.x, shadowX, reach), from.y + height * drop);
                m_canvas.Stroke(from.x, from.y, to.x, to.y, Pen);
            }
            // A snow line across the lit face on some tall peaks: a short jagged stroke below the top.
            if (top > 0.2f && top < 0.45f && height > width * 0.72f)
            {
                Vector2 a = Along(m_west, 0.68f), b = Vector2.Lerp(apex, foot, 0.3f);
                Vector2 mid = Vector2.Lerp(a, b, 0.5f) + new Vector2(0f, height * 0.05f);
                Poly(a + new Vector2(width * 0.03f, 0f), mid, b);
                m_canvas.Polyline(m_points, Pen);
            }
            // A spur down the lit face from the west flank's jag, on some peaks.
            if (style > 0.35f && style < 0.8f)
            {
                Vector2 spur = m_west[1];
                m_canvas.Stroke(spur.x, spur.y, spur.x + width * 0.1f, spur.y + height * 0.25f, Pen);
            }
            m_canvas.Polyline(m_west, 1.5f);
            m_canvas.Polyline(m_east, 1.5f);
        }

        // A point on the line from a to b at t, pushed off it by offset to the line's left (up and
        // out of a peak's body on both flanks, as they run west to east).
        private static Vector2 Flank(Vector2 a, Vector2 b, float t, float offset)
        {
            Vector2 along = b - a;
            var normal = new Vector2(along.y, -along.x).normalized;
            return Vector2.Lerp(a, b, t) + normal * offset;
        }

        // The point at share t of a polyline's length.
        private static Vector2 Along(List<Vector2> line, float t)
        {
            float total = 0f;
            for (int i = 1; i < line.Count; i++) total += Vector2.Distance(line[i - 1], line[i]);
            float goal = total * Mathf.Clamp01(t);
            for (int i = 1; i < line.Count; i++)
            {
                float span = Vector2.Distance(line[i - 1], line[i]);
                if (goal <= span) return span > 0f ? Vector2.Lerp(line[i - 1], line[i], goal / span) : line[i];
                goal -= span;
            }
            return line[line.Count - 1];
        }

        // A peak's height for its width: from broad and low to tall and narrow, by seed. Summit
        // placement uses it too, to set the foot so the apex lands on the summit.
        private static float PeakHeightOf(float width, int seed) => width * Mathf.Lerp(0.58f, 1.02f, Unit(seed, 10));

        private readonly List<Vector2> m_fan = new List<Vector2>(16);

        // A fan triangle's corner pushed 0.6 pen out from the triangle's centre, so neighbouring
        // triangles overlap and their anti-aliased edges leave no faint seam inside the body.
        private Vector2 Grow(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            Vector2 centre = (a + b + c) / 3f;
            Vector2 out_ = p - centre;
            float length = out_.magnitude;
            return length > 1e-4f ? p + out_ / length * 0.6f * m_pen : p;
        }

        private bool IsNorthAt(float x, float y)
        {
            float wx = x * MetresPerPixel - HalfExtent, wz = HalfExtent - y * MetresPerPixel;
            if (m_grid != null) return m_grid.m_land[m_grid.Nearest(x, y)] == Land.DeepNorth;
            return m_world.m_biome[m_world.Index(wx, wz)] == Land.DeepNorth;
        }


        // A hill as the classic map draws its downs: a low,
        // lopsided dome stroke with a few hatch dashes under its eastern side, often with a
        // smaller dome behind, so groups of hills read as rolling country. Each hill varies by
        // seed as the peaks do (a stamp repeated over a plain): its proportions,
        // which side is steeper, the crest (one dome, a long ridge, a double crest, a shoulder),
        // none, one or two domes behind, the hatching hand and a slight wobble of the pen.
        // (x, y) is the middle of the front dome's foot; FitHill sets it so the crown, wherever
        // the crest puts it, lands on the hilltop. lone drops the domes behind, which keeps a hill
        // by the water off it (FitHill).
        private void Hill(float x, float y, float width, int seed, bool lone = false)
        {
            int behind = lone ? 0 : DomesBehind(seed);
            for (int k = behind - 1; k >= 0; k--)
            {
                DomeBehind(x, y, width, seed, k, out float bx, out float by, out float small);
                Dome(bx, by, small, seed + 1 + k, true);
            }
            Dome(x, y, width, seed, false);
        }

        // How many smaller domes stand behind a hill: none, one, or now and then two.
        private static int DomesBehind(int seed)
        {
            float roll = Unit(seed, 12);
            return roll < 0.4f ? 0 : roll < 0.9f ? 1 : 2;
        }

        // The k-th dome behind a hill (0 nearest): mostly to the west, sometimes to the east, at
        // seed-set offsets and sizes; a second one stands on the other side, smaller and further
        // back. Hill draws them furthest first, so the nearer ones and the front dome hide them.
        private static void DomeBehind(float x, float y, float width, int seed, int k, out float bx, out float by, out float small)
        {
            float side = Unit(seed, 55) < 0.78f ? -1f : 1f;
            if (k == 1) side = -side;
            small = width * (k == 0 ? Mathf.Lerp(0.48f, 0.7f, Unit(seed, 13)) : Mathf.Lerp(0.4f, 0.52f, Unit(seed, 56)));
            bx = x + side * width * Mathf.Lerp(0.38f, 0.52f, Unit(seed, 57 + k));
            by = y - width * (Mathf.Lerp(0.08f, 0.15f, Unit(seed, 59 + k)) + k * 0.04f);
        }

        // A dome inked as a pen would trace it (hills should feel hand-drawn,
        // as the peaks do): the line swells over the crest and thins toward the feet, lifts once
        // somewhere along the back and is set down again a hair off with a small overshoot, and
        // runs out at each foot in a short tail.
        private void Dome(float x, float y, float width, int seed, bool behind)
        {
            DomeShape shape = DomeOutline(m_points, x, y, width, seed, behind);
            m_canvas.Erase(m_points);
            float pen = Mathf.Lerp(1.1f, 1.3f, Unit(seed, 54));
            int n = m_points.Count - 1;
            int lift = Mathf.RoundToInt(Mathf.Lerp(0.35f, 0.7f, Unit(seed, 91)) * n);
            var setDown = new Vector2(0f, Mathf.Lerp(-0.3f, 0.3f, Unit(seed, 92)));
            for (int i = 1; i <= n; i++)
            {
                Vector2 a = m_points[i - 1], b = m_points[i];
                if (i == lift + 1) a += setDown;
                if (i == lift) b += (b - a).normalized * 0.5f;
                if (i == 1) a += new Vector2(-0.45f, 0.15f);
                if (i == n) b += new Vector2(0.5f, 0.1f);
                float swell = 0.8f + 0.32f * Mathf.Sin(Mathf.PI * (i - 0.5f) / n);
                m_canvas.Stroke(a.x, a.y, b.x, b.y, Mathf.Max(Pen, pen * swell * Vary(seed, 130 + i, 0.07f)));
            }
            for (int k = 0; k < shape.m_dashes; k++)
            {
                if (!DomeDash(shape, x, y, width, seed, k, out Vector2 a, out Vector2 b)) continue;
                m_canvas.Stroke(a.x, a.y, b.x, b.y, Pen);
            }
        }

        // A dome's drawn shape: its height, the crest whose east flank carries the hatching (as a
        // share u of the width, 0 at the west foot), the crown (the outline's highest point,
        // relative to the foot's middle) and the hatching hand.
        private struct DomeShape
        {
            public float m_height, m_shadeFrom, m_crownX, m_crownRise;
            public int m_seed, m_style, m_dashes;
            public float m_west, m_east, m_crest, m_wobble;
            public float m_dashLength, m_dashGap, m_dashSlant;
        }

        // Crest styles: one dome, a long low ridge with a flattish back, a double crest with a
        // saddle, a dome with a shoulder on one flank.
        private const int DomeSingle = 0;
        private const int DomeRidge = 1;
        private const int DomeTwin = 2;
        private const int DomeShoulder = 3;
        private const int DomeSteps = 18;
        // How far a ridge's back bows up over its middle, as a share of the dome's height.
        private const float RidgeBow = 0.12f;

        // A dome's outline from its west foot to its east foot, into points. Shared by the
        // drawing, the hill's water test and its placement (the crown), so all three see the
        // drawn shape. Domes behind keep to a single crest or a shoulder: doubled crests on the
        // domes behind as well made a hill busy.
        private static DomeShape DomeOutline(List<Vector2> points, float x, float y, float width, int seed, bool behind)
        {
            var shape = new DomeShape { m_seed = seed };
            float roll = Unit(seed, 50);
            shape.m_style = behind
                ? (roll < 0.75f ? DomeSingle : DomeShoulder)
                : roll < 0.5f ? DomeSingle : roll < 0.65f ? DomeRidge : roll < 0.82f ? DomeTwin : DomeShoulder;
            // From broad and low to rounder and taller: the taller domes also have the fuller sides.
            float tall = Unit(seed, 10);
            shape.m_height = width * Mathf.Lerp(0.22f, 0.37f, tall) * (shape.m_style == DomeRidge ? 0.8f : 1f);
            float fullness = Mathf.Lerp(1f, 0.68f, tall);
            // Lopsided: the crest off centre, each flank its own steepness.
            shape.m_crest = Mathf.Lerp(0.38f, 0.62f, Unit(seed, 11));
            shape.m_west = fullness * Vary(seed, 51, 0.22f);
            shape.m_east = fullness * Vary(seed, 52, 0.22f);
            shape.m_wobble = Mathf.Lerp(0.015f, 0.045f, Unit(seed, 53));
            shape.m_shadeFrom = shape.m_style == DomeTwin ? TwinEastCrest(seed) : shape.m_crest;

            points.Clear();
            int crown = 0;
            for (int i = 0; i <= DomeSteps; i++)
            {
                // Points close up toward the feet, where the flanks are steepest, unevenly spaced
                // and nudged a little off the curve, so the line is drawn in short facets as the
                // peaks' ridges are rather than as one smooth arc.
                float step = i == 0 || i == DomeSteps ? 0f : (Unit(seed, 90 + i) - 0.5f) * 0.4f;
                float u = 0.5f - 0.5f * Mathf.Cos(Mathf.PI * (i + step) / DomeSteps);
                float nudge = i == 0 || i == DomeSteps ? 0f : (Unit(seed, 110 + i) - 0.5f) * 0.06f * Mathf.Sin(Mathf.PI * u);
                var p = new Vector2(x + (u - 0.5f) * width, y - (DomeRise(shape, u) + nudge) * shape.m_height);
                points.Add(p);
                if (p.y < points[crown].y) crown = i;
            }
            shape.m_crownX = points[crown].x - x;
            shape.m_crownRise = y - points[crown].y;

            // The hatching hands: a sparse pair of long dashes, the usual three tapering, a
            // dense row of short ones, or three long even ones.
            float hand = Unit(seed, 62);
            shape.m_dashes = hand < 0.25f ? 2 : hand < 0.65f ? 3 : hand < 0.88f ? 4 + (Unit(seed, 63) < 0.5f ? 1 : 0) : 3;
            shape.m_dashLength = hand < 0.25f ? 0.6f : hand < 0.65f ? 0.55f : hand < 0.88f ? 0.42f : 0.5f;
            shape.m_dashGap = Mathf.Lerp(0.15f, 0.22f, Unit(seed, 64));
            shape.m_dashSlant = Mathf.Lerp(0.03f, 0.085f, Unit(seed, 65));
            if (behind) shape.m_dashes = Mathf.Min(shape.m_dashes, 3);
            return shape;
        }

        // The double crest's eastern top (share of the width), which the hatching hangs from.
        private static float TwinEastCrest(int seed) => Mathf.Lerp(0.64f, 0.72f, Unit(seed, 66));

        // The dome's height at u (0 at the west foot, 1 at the east), as a share of its height.
        private static float DomeRise(DomeShape s, float u)
        {
            float f;
            switch (s.m_style)
            {
                case DomeRidge:
                {
                    // A long back: steeper ends and a crest line that sinks a little to one end.
                    float half = Mathf.Lerp(0.12f, 0.2f, Unit(s.m_seed, 67));
                    float a = Mathf.Max(0.12f, s.m_crest - half), b = Mathf.Min(0.88f, s.m_crest + half);
                    float sink = (Unit(s.m_seed, 68) < 0.5f ? -1f : 1f) * 0.07f;
                    if (u < a) f = DomeFlank(u / a, s.m_west) * (1f + Mathf.Min(sink, 0f));
                    else if (u > b) f = DomeFlank((1f - u) / (1f - b), s.m_east) * (1f - Mathf.Max(sink, 0f));
                    else
                    {
                        // The back bows up in a gentle dome rather than running level: a flat
                        // back read as a lid at close zoom.
                        float t = (u - a) / (b - a);
                        f = 1f - Mathf.Max(sink, 0f) * t + Mathf.Min(sink, 0f) * (1f - t) + RidgeBow * Mathf.Sin(Mathf.PI * t);
                    }
                    break;
                }
                case DomeTwin:
                {
                    // Two tops on one hill, the saddle between them softened.
                    float west = Mathf.Lerp(0.28f, 0.36f, Unit(s.m_seed, 69)), east = TwinEastCrest(s.m_seed);
                    float second = Mathf.Lerp(0.7f, 0.88f, Unit(s.m_seed, 70));
                    bool westTaller = Unit(s.m_seed, 71) < 0.5f;
                    float hw = Hump(u, 0f, west, 0.62f, s.m_west) * (westTaller ? 1f : second);
                    float he = Hump(u, 0.38f, east, 1f, s.m_east) * (westTaller ? second : 1f);
                    f = SoftMax(hw, he, 4f);
                    break;
                }
                case DomeShoulder:
                {
                    // A lower knoll on one flank, run into the main dome.
                    bool east = Unit(s.m_seed, 72) < 0.5f;
                    float main = Hump(u, 0f, s.m_crest, 1f, s.m_west, s.m_east);
                    float at = east ? Mathf.Lerp(0.78f, 0.84f, Unit(s.m_seed, 73)) : Mathf.Lerp(0.16f, 0.22f, Unit(s.m_seed, 73));
                    float knoll = Hump(u, at - 0.16f, at, at + 0.16f, 0.9f) * Mathf.Lerp(0.5f, 0.64f, Unit(s.m_seed, 74));
                    f = SoftMax(main, knoll, 6f);
                    break;
                }
                default:
                    f = Hump(u, 0f, s.m_crest, 1f, s.m_west, s.m_east);
                    break;
            }
            // A slight wobble of the pen, nothing at the feet, so no two domes are perfect arcs.
            float phase = Unit(s.m_seed, 75) * 6.28f, phase2 = Unit(s.m_seed, 76) * 6.28f;
            float wobble = s.m_wobble * (Mathf.Sin(u * 9.4f + phase) * 0.6f + Mathf.Sin(u * 17f + phase2) * 0.4f);
            return Mathf.Max(0f, f + wobble * Mathf.Sin(Mathf.PI * u));
        }

        // A hump over [a, b] topped at c, 1 there and 0 at its ends, each flank its own fullness.
        private static float Hump(float u, float a, float c, float b, float west, float east = -1f)
        {
            if (east < 0f) east = west;
            if (u <= a || u >= b) return 0f;
            return u < c ? DomeFlank((u - a) / (c - a), west) : DomeFlank((b - u) / (b - c), east);
        }

        // A flank rising from its foot (s = 0) to its crest (s = 1); fuller below 1.
        private static float DomeFlank(float s, float fullness) => Mathf.Pow(Mathf.Sin(Mathf.PI * 0.5f * Mathf.Clamp01(s)), fullness);

        private static float SoftMax(float a, float b, float k) => Mathf.Pow(Mathf.Pow(a, k) + Mathf.Pow(b, k), 1f / k);

        // The k-th shade dash under a dome's eastern flank, from a to b, hung a gap below the
        // outline and slanting back toward the crest; false when the flank there is too shallow
        // for one. Shared by the drawing and the hill's water test.
        private static bool DomeDash(DomeShape shape, float x, float y, float width, int seed, int k, out Vector2 a, out Vector2 b)
        {
            float from = shape.m_shadeFrom + (1f - shape.m_shadeFrom) * 0.14f;
            float to = shape.m_shadeFrom + (1f - shape.m_shadeFrom) * 0.78f;
            float u = shape.m_dashes == 1 ? from : Mathf.Lerp(from, to, (float)k / (shape.m_dashes - 1));
            float depth = DomeRise(shape, u) * shape.m_height;
            var top = new Vector2(x + (u - 0.5f) * width, y - depth);
            float gap = shape.m_height * shape.m_dashGap;
            // Tapering toward the foot, a little uneven, and never reaching below it.
            float taper = shape.m_dashes == 1 ? 1f : Mathf.Lerp(1f, 0.5f, (float)k / (shape.m_dashes - 1));
            float length = shape.m_height * shape.m_dashLength * taper * Vary(seed, 80 + k, 0.15f);
            length = Mathf.Min(length, depth - gap - shape.m_height * 0.06f);
            a = new Vector2(top.x - width * 0.015f, top.y + gap);
            // Each dash at its own angle, as a hand never repeats one exactly.
            b = new Vector2(top.x - width * shape.m_dashSlant * Vary(seed, 95 + k, 0.35f), top.y + gap + length);
            return length >= shape.m_height * 0.1f;
        }

        // A broadleaf tree: an egg-shaped crown on a short trunk. Packed densely, the crowns hide
        // the trunks behind them, so only the southern edge of a wood shows trunks.
        private void Broadleaf(float x, float y, float s, int seed)
        {
            float r = 2.6f * s * Vary(seed, 10, 0.25f);
            float ry = r * Mathf.Lerp(1.05f, 1.45f, Unit(seed, 12));
            float cx = x + (Unit(seed, 11) - 0.5f) * 0.9f * s;
            float trunk = Mathf.Lerp(1f, 2.2f, Unit(seed, 13)) * s;
            float cy = y - trunk - ry;
            m_canvas.EraseEllipse(cx, cy, r * 1.12f + 0.7f * m_pen, ry * 1.12f + 0.7f * m_pen);
            Shade(cx, cy, r, ry, seed);
            HandCrown(cx, cy, r, ry, seed);
            m_canvas.Stroke(x, cy + ry * 0.9f, x + (cx - x) * 0.3f, y, Pen);
        }

        // A crown traced as a pen would: a closed loop whose radius wobbles a little all round, by
        // seed, and whose start and end overlap slightly, so no two crowns are the same shape. A
        // perfect ellipse on every tree read as stamped, not drawn.
        // wobbleScale above 1 makes a raggeder outline (the Mistlands' canopies).
        private void HandCrown(float cx, float cy, float rx, float ry, int seed, float wobbleScale = 1f)
        {
            m_points.Clear();
            const int steps = 14;
            float start = Unit(seed, 40) * Mathf.PI * 2f;
            float wobble = Mathf.Lerp(0.04f, 0.11f, Unit(seed, 41)) * wobbleScale;
            for (int i = 0; i <= steps + 1; i++)
            {
                float a = start + Mathf.PI * 2f * i / steps;
                // Two lumps of different frequency make the outline uneven but not jagged.
                float bump = 1f + wobble * (Mathf.Sin(a * 3f + Unit(seed, 42) * 6.28f) * 0.6f + Mathf.Sin(a * 5f + Unit(seed, 43) * 6.28f) * 0.4f);
                if (i > steps) bump *= 0.96f;
                m_points.Add(new Vector2(cx + Mathf.Cos(a) * rx * bump, cy + Mathf.Sin(a) * ry * bump));
            }
            m_canvas.Polyline(m_points, Mathf.Lerp(1f, 1.25f, Unit(seed, 44)));
        }

        // The shaded eastern side of a round crown, as the pines' filled eastern half: the part
        // of the ellipse east of a chord, a convex segment the rasterizer can fill.
        // How much of the crown is shaded varies by seed, as a hand would vary it.
        private void Shade(float cx, float cy, float rx, float ry, int seed)
        {
            m_points.Clear();
            const int steps = 8;
            float span = CrownShade * Mathf.Lerp(0.75f, 1.2f, Unit(seed, 45));
            float turn = (Unit(seed, 46) - 0.5f) * 0.5f;
            for (int i = 0; i <= steps; i++)
            {
                float a = turn + Mathf.Lerp(-span, span, (float)i / steps);
                m_points.Add(new Vector2(cx + Mathf.Cos(a) * rx, cy + Mathf.Sin(a) * ry));
            }
            m_canvas.Fill(m_points);
        }

        // A pine: a narrow pointed triangle with its eastern half filled, on a trunk tick.
        private void Conifer(float x, float y, float s, int seed)
        {
            // Each pine a little different, as drawn by hand: height, width, a leaning tip,
            // faintly bowed flanks, an uneven base, the trunk and the shaded share all by seed.
            float height = 9.5f * s * Vary(seed, 10, 0.28f);
            float half = 2.8f * s * Vary(seed, 11, 0.22f);
            float baseY = y - Mathf.Lerp(1f, 2.2f, Unit(seed, 13)) * s;
            var apex = new Vector2(x + (Unit(seed, 12) - 0.5f) * 0.9f * s, baseY - height);
            var left = new Vector2(x - half, baseY + (Unit(seed, 14) - 0.5f) * 0.5f * s);
            var right = new Vector2(x + half, baseY + (Unit(seed, 15) - 0.5f) * 0.5f * s);
            Poly(apex, right + new Vector2(0.6f, 0.5f) * m_pen, left + new Vector2(-0.6f, 0.5f) * m_pen);
            m_canvas.Erase(m_points);
            float split = Mathf.Lerp(0.0f, 0.35f, Unit(seed, 16));
            Poly(apex, right, new Vector2(Mathf.Lerp(x, right.x, split), baseY));
            m_canvas.Fill(m_points);
            float bow = (Unit(seed, 17) - 0.5f) * 0.5f * s;
            Poly(apex, Vector2.Lerp(apex, left, 0.5f) + new Vector2(-bow, 0f), left);
            m_canvas.Polyline(m_points, Mathf.Lerp(1f, 1.2f, Unit(seed, 18)));
            m_canvas.Stroke(apex.x, apex.y, right.x, right.y, 1.1f);
            m_canvas.Stroke(left.x, left.y, right.x, right.y, Pen);
            m_canvas.Stroke(x, baseY, x + (Unit(seed, 19) - 0.5f) * 0.4f * s, y, Pen);
        }

        // A plains tree: a small round crown on a short trunk, standing alone.
        private void PlainsTree(float x, float y, float s, int seed)
        {
            float r = 2.2f * s * Vary(seed, 10, 0.25f);
            float ry = r * Mathf.Lerp(0.85f, 1.15f, Unit(seed, 12));
            float cx = x + (Unit(seed, 11) - 0.5f) * 0.7f * s;
            float cy = y - Mathf.Lerp(1.4f, 2.8f, Unit(seed, 13)) * s - ry;
            m_canvas.EraseEllipse(cx, cy, r * 1.12f + 0.7f * m_pen, ry * 1.12f + 0.7f * m_pen);
            Shade(cx, cy, r, ry, seed);
            HandCrown(cx, cy, r, ry, seed);
            m_canvas.Stroke(x, cy + ry * 0.9f, x + (cx - x) * 0.3f, y, Pen);
        }

        // A tuft of reeds standing on a marsh line: a small fan of three blades, the marsh sign
        // of the classic map.
        // Sized to the marsh grid's cell c, so a tuft stays inside its own cell. Two to four blades,
        // the middle one tallest, their heights, spread and lean by seed, so no two tufts are
        // stamped alike; always three blades read as the Meadows' grass.
        private void Reeds(float x, float y, float c, int seed, float pen)
        {
            float tall = Mathf.Lerp(0.34f, 0.46f, Unit(seed, 20)) * c;
            float spread = Mathf.Lerp(0.14f, 0.22f, Unit(seed, 21)) * c;
            float lean = (Unit(seed, 22) - 0.5f) * 0.12f * c;
            float roll = Unit(seed, 25);
            int blades = roll < 0.35f ? 2 : roll < 0.85f ? 3 : 4;
            for (int k = 0; k < blades; k++)
            {
                // -1 to 1 across the tuft; the middle blades stand tallest and most upright.
                float u = blades == 1 ? 0f : -1f + 2f * k / (blades - 1);
                float height = tall * (1f - 0.35f * Mathf.Abs(u)) * Mathf.Lerp(0.85f, 1f, Unit(seed, 26 + k));
                m_canvas.Stroke(x + u * 0.05f * c, y, x + u * spread + lean, y - height, pen);
            }
        }

        // One of the swamp's ancient dead trees: a tall bowed trunk on splayed roots, with two to
        // four bare branches set by seed, some forked. Four fixed strokes read as a stick figure,
        // smaller than a pine while the game's ancient trees tower.
        // A slightly wider band is scraped under every limb first, as the pines scrape under
        // themselves, so the marsh lines do not run through the tree.
        private void DeadTree(float x, float y, float s, int seed)
        {
            m_limbs.Clear();
            float height = Mathf.Lerp(11f, 14f, Unit(seed, 10)) * s;
            float bow = (Unit(seed, 11) - 0.5f) * 2.4f * s;
            var foot = new Vector2(x, y);
            var mid = new Vector2(x + bow, y - height * 0.5f);
            var top = new Vector2(x + bow * 0.4f + (Unit(seed, 12) - 0.5f) * 1.2f * s, y - height);
            m_limbs.Add(new Limb(foot, mid, 1.3f));
            m_limbs.Add(new Limb(mid, top, 1.1f));
            // Roots splaying out at the foot.
            m_limbs.Add(new Limb(foot + new Vector2(0f, -0.6f * s), foot + new Vector2(-Mathf.Lerp(1.4f, 2.2f, Unit(seed, 13)) * s, 0.4f * s), Pen));
            m_limbs.Add(new Limb(foot + new Vector2(0f, -0.6f * s), foot + new Vector2(Mathf.Lerp(1.2f, 2f, Unit(seed, 14)) * s, 0.3f * s), Pen));
            int branches = 2 + (int)(Unit(seed, 15) * 2.99f);
            float side = Unit(seed, 16) < 0.5f ? -1f : 1f;
            for (int k = 0; k < branches; k++)
            {
                float t = Mathf.Lerp(0.42f, 0.88f, (k + 0.3f + 0.4f * Unit(seed, 20 + k)) / branches);
                Vector2 start = t < 0.5f ? Vector2.Lerp(foot, mid, t * 2f) : Vector2.Lerp(mid, top, t * 2f - 1f);
                float length = Mathf.Lerp(2.6f, 4.4f, Unit(seed, 30 + k)) * s * (1.15f - t * 0.4f);
                var end = start + new Vector2(side * length * Mathf.Lerp(0.7f, 0.95f, Unit(seed, 40 + k)), -length * Mathf.Lerp(0.45f, 0.8f, Unit(seed, 50 + k)));
                m_limbs.Add(new Limb(start, end, Pen));
                // Some branches fork near their end into a short upturned twig.
                if (Unit(seed, 60 + k) < 0.5f)
                {
                    Vector2 fork = Vector2.Lerp(start, end, 0.6f);
                    m_limbs.Add(new Limb(fork, fork + new Vector2(side * 0.5f * s, -1.4f * s), Pen));
                }
                side = -side;
            }
            foreach (Limb limb in m_limbs) m_canvas.Scrape(limb.m_a.x, limb.m_a.y, limb.m_b.x, limb.m_b.y, limb.m_width + 1.4f);
            foreach (Limb limb in m_limbs) m_canvas.Stroke(limb.m_a.x, limb.m_a.y, limb.m_b.x, limb.m_b.y, limb.m_width);
        }

        private struct Limb
        {
            public Vector2 m_a, m_b;
            public float m_width;

            public Limb(Vector2 a, Vector2 b, float width)
            {
                m_a = a;
                m_b = b;
                m_width = width;
            }
        }

        private readonly List<Limb> m_limbs = new List<Limb>(16);

        // A Mistlands crag: a black pinnacle in the peaks' hand, often with a smaller one or two
        // standing behind it, drawn back to front. The old pillar was one symmetric trapezoid with
        // three upright strokes on every spire and read as a fence post (Mistlands review,
        //).
        private void Spire(float x, float y, float s, int seed)
        {
            float width = SpireHeightOf(s, seed) * 0.45f;
            if (Unit(seed, 40) < 0.55f)
                Pinnacle(x - width * 0.4f, y - width * 0.1f, s * Mathf.Lerp(0.55f, 0.75f, Unit(seed, 41)), seed + 1);
            if (Unit(seed, 42) < 0.2f)
                Pinnacle(x + width * 0.45f, y - width * 0.08f, s * Mathf.Lerp(0.5f, 0.65f, Unit(seed, 43)), seed + 2);
            Pinnacle(x, y, s, seed);
        }

        // A spire's height for its size, shared with summit placement, which sets the foot so the
        // apex lands on the summit.
        private static float SpireHeightOf(float s, int seed) => Mathf.Lerp(8f, 12f, Unit(seed, 10)) * s;

        // One pinnacle: 0.45 to 0.62 of its height wide (narrower read as needles), leaning a little, jagged flanks with
        // a ledge, some with a broken twin tip; the body scraped as a fan like a peak's but keeping
        // the Mistlands' wash; a solid wedge down most of the east face, as the rock is black, and
        // hatching below it.
        private void Pinnacle(float x, float y, float s, int seed)
        {
            float height = SpireHeightOf(s, seed);
            float width = height * Mathf.Lerp(0.45f, 0.62f, Unit(seed, 11));
            var apex = new Vector2(x + (Unit(seed, 12) - 0.5f) * 0.1f * height, y - height);
            var left = new Vector2(x - width * 0.5f, y + (Unit(seed, 16) - 0.5f) * 0.6f * s);
            var right = new Vector2(x + width * 0.5f, y + (Unit(seed, 17) - 0.5f) * 0.6f * s);
            m_west.Clear();
            m_west.Add(left);
            m_west.Add(Flank(left, apex, 0.4f, -0.06f * width));
            m_west.Add(Flank(left, apex, 0.72f, (Unit(seed, 14) - 0.5f) * 0.1f * width));
            m_west.Add(apex);
            m_east.Clear();
            m_east.Add(apex);
            if (Unit(seed, 13) < 0.4f)
            {
                // A broken twin tip: a notch, then a second point a little lower to the east.
                m_east.Add(new Vector2(apex.x + width * 0.06f, apex.y + height * 0.18f));
                m_east.Add(new Vector2(apex.x + width * 0.14f, apex.y + height * 0.1f));
            }
            m_east.Add(Flank(apex, right, 0.3f, (Unit(seed, 15) - 0.5f) * 0.1f * width));
            m_east.Add(Flank(apex, right, 0.6f, Unit(seed, 18) < 0.5f ? 0.05f * height : 0f));
            m_east.Add(right);

            m_points.Clear();
            m_points.AddRange(m_west);
            for (int i = 1; i < m_east.Count; i++) m_points.Add(m_east[i]);
            var hub = new Vector2(apex.x, y - height * 0.15f);
            m_fan.Clear();
            m_fan.AddRange(m_points);
            for (int i = 0; i < m_fan.Count; i++)
            {
                Vector2 a = m_fan[i], b = m_fan[(i + 1) % m_fan.Count];
                Poly(Grow(hub, hub, a, b), Grow(a, hub, a, b), Grow(b, hub, a, b));
                m_canvas.Erase(m_points);
            }

            var foot = new Vector2(apex.x + width * 0.05f, y);
            Poly(apex, Along(m_east, SpireWedge), Vector2.Lerp(apex, foot, SpireWedge));
            m_canvas.Fill(m_points);
            int strokes = 3 + (int)(Unit(seed, 21) * 2.99f);
            for (int k = 0; k < strokes; k++)
            {
                float t = Mathf.Lerp(SpireWedge, 0.95f, (k + 0.5f) / strokes);
                Vector2 from = Along(m_east, t);
                float shadowX = Mathf.Lerp(apex.x, foot.x, (from.y - apex.y) / height);
                var to = new Vector2(Mathf.Lerp(from.x, shadowX, Mathf.Lerp(0.6f, 0.9f, Unit(seed, 30 + k))), from.y + height * 0.06f);
                m_canvas.Stroke(from.x, from.y, to.x, to.y, Pen);
            }
            m_canvas.Polyline(m_west, 1.4f);
            m_canvas.Polyline(m_east, 1.4f);
        }

        // A Mistlands tree as the game's Yggdrasil shoots: a kinked trunk on flared roots, one or
        // two branches up into one broad, slightly ragged canopy. Three
        // round crowns on a straight stick read as a lollipop.
        private void GiantTree(float x, float y, float s, int seed)
        {
            float trunk = Mathf.Lerp(4.5f, 6f, Unit(seed, 10)) * s;
            var foot = new Vector2(x, y);
            var p1 = new Vector2(x + (Unit(seed, 11) - 0.5f) * 1.2f * s, y - trunk * 0.33f);
            var p2 = new Vector2(x + (Unit(seed, 12) - 0.5f) * 1.2f * s, y - trunk * 0.66f);
            var p3 = new Vector2(x + (Unit(seed, 13) - 0.5f) * 0.6f * s, y - trunk);
            float rx = Mathf.Lerp(3.6f, 4.6f, Unit(seed, 17)) * s;
            float ry = rx * Mathf.Lerp(0.55f, 0.7f, Unit(seed, 18));
            float cx = p3.x, cy = p3.y - ry * 0.6f;

            m_limbs.Clear();
            m_limbs.Add(new Limb(foot, p1, 1.4f));
            m_limbs.Add(new Limb(p1, p2, 1.3f));
            m_limbs.Add(new Limb(p2, p3, 1.2f));
            m_limbs.Add(new Limb(new Vector2(x, y - 0.8f * s), new Vector2(x - 1.3f * s, y + 0.2f * s), Pen));
            m_limbs.Add(new Limb(new Vector2(x, y - 0.8f * s), new Vector2(x + 1.3f * s, y + 0.2f * s), Pen));
            float side = Unit(seed, 15) < 0.5f ? -1f : 1f;
            Vector2 fork = Vector2.Lerp(p2, p3, 0.4f);
            m_limbs.Add(new Limb(fork, new Vector2(cx + side * rx * 0.7f, cy + ry * 0.6f), Pen));
            if (Unit(seed, 16) < 0.6f) m_limbs.Add(new Limb(fork, new Vector2(cx - side * rx * 0.6f, cy + ry * 0.7f), Pen));
            foreach (Limb limb in m_limbs) m_canvas.Scrape(limb.m_a.x, limb.m_a.y, limb.m_b.x, limb.m_b.y, limb.m_width + 1.2f);
            foreach (Limb limb in m_limbs) m_canvas.Stroke(limb.m_a.x, limb.m_a.y, limb.m_b.x, limb.m_b.y, limb.m_width);

            // One crown only: lobes behind or beside it showed as thin black crescents along its
            // edge at the map's scale (black artifacts outside the padding).
            Canopy(cx, cy, rx, ry, seed + 2);

            void Canopy(float ox, float oy, float ex, float ey, int crownSeed)
            {
                m_canvas.EraseEllipse(ox, oy, ex * 1.12f + 0.7f * m_pen, ey * 1.12f + 0.7f * m_pen);
                Shade(ox, oy, ex, ey, crownSeed);
                HandCrown(ox, oy, ex, ey, crownSeed, 1.15f);
            }
        }

        // A volcano in the mountains' hand, at a mountain's scale: a broad cone with gently
        // concave flanks, its top cut off by a notched crater with a thin rim, the eastern face a
        // small solid wedge under the rim and slanting hatch below. The old cone was two or three
        // times a peak's height with a solid black face and read as an obelisk (art review).
        private void Volcano(float x, float y, float s, int seed)
        {
            float width = 14f * s * Vary(seed, 10, 0.12f);
            float height = width * Mathf.Lerp(0.42f, 0.55f, Unit(seed, 11));
            float rim = width * 0.25f;
            float top = y - height;
            var left = new Vector2(x - width * 0.5f, y);
            var right = new Vector2(x + width * 0.5f, y);
            var rimLeft = new Vector2(x - rim * 0.5f, top);
            var rimRight = new Vector2(x + rim * 0.5f, top + height * 0.03f);
            // Concave flanks: a midpoint pulled in towards the body.
            Vector2 westMid = Vector2.Lerp(left, rimLeft, 0.5f) + new Vector2(width * 0.06f, height * 0.04f);
            Vector2 eastMid = Vector2.Lerp(rimRight, right, 0.5f) + new Vector2(-width * 0.06f, height * 0.04f);
            Poly(left, rimLeft, rimRight, right);
            m_canvas.Erase(m_points);
            // The shadow: a wedge under the rim, then hatch strokes down the eastern face.
            var foot = new Vector2(x + rim * 0.2f, y);
            Poly(rimRight, Vector2.Lerp(rimRight, eastMid, 0.35f), Vector2.Lerp(rimRight, foot, 0.18f));
            m_canvas.Fill(m_points);
            m_east.Clear();
            m_east.Add(rimRight);
            m_east.Add(eastMid);
            m_east.Add(right);
            int strokes = Mathf.Clamp(Mathf.RoundToInt(height / (2.4f * m_pen)), 5, 9);
            for (int k = 0; k < strokes; k++)
            {
                float t = Mathf.Lerp(0.2f, 0.92f, (k + 0.5f) / strokes);
                Vector2 from = Along(m_east, t);
                float shadowX = Mathf.Lerp(rimRight.x, foot.x, (from.y - top) / height);
                var to = new Vector2(Mathf.Lerp(from.x, shadowX, Mathf.Lerp(0.55f, 0.85f, Unit(seed, 30 + k))), from.y + height * 0.08f);
                m_canvas.Stroke(from.x, from.y, to.x, to.y, Pen);
            }
            // Flanks.
            Poly(left, westMid, rimLeft);
            m_canvas.Polyline(m_points, 1.5f);
            Poly(rimRight, eastMid, right);
            m_canvas.Polyline(m_points, 1.5f);
            // The crater: a notched rim line and a thin ellipse.
            Poly(rimLeft, new Vector2(x - rim * 0.15f, top + height * 0.06f), new Vector2(x + rim * 0.1f, top + height * 0.02f), rimRight);
            m_canvas.Polyline(m_points, 1.2f);
            m_canvas.Ring(x, top + height * 0.01f, rim * 0.5f, rim * 0.16f, Pen);
            // No smoke curl or lava stream: with them the cone read as a cartoon, and the map is
            // meant to be a sober survey. The crater alone says volcano.
        }

        // The Ashlands' broken rock: one or two small peaks in the mountains' own hand, the
        // smaller behind to the west, keeping the burnt ground's wash. Ridges of thin notched
        // spires read as grass tufts and looked doodled.
        private void Crag(float x, float y, float s, int seed)
        {
            float width = 6f * s * Vary(seed, 10, 0.15f);
            if (Unit(seed, 12) < 0.6f)
                Peak(x - width * 0.45f, y - width * 0.12f, width * Mathf.Lerp(0.65f, 0.85f, Unit(seed, 13)), seed + 1, body: false);
            Peak(x, y, width, seed, body: false);
        }


        // An ice floe: a small irregular outline lying on the water. Its body (an ellipse just
        // around the outline) is scraped back to parchment first, so ripple lines stop at its edge,
        // and the outline is filled with the Deep North's wash, as ice, not the sea's (user,
        //). The outline may sag inwards, so it is filled as a fan of convex triangles
        // from its centre, as peak bodies are.
        private void IceFloe(float x, float y, float s, int seed)
        {
            const int corners = 6;
            float size = Mathf.Lerp(2.5f, 5.5f, Unit(seed, 10)) * s;
            m_fan.Clear();
            for (int k = 0; k < corners; k++)
            {
                float a = (k + Unit(seed, 11 + k) * 0.6f) / corners * Mathf.PI * 2f;
                float r = size * Mathf.Lerp(0.7f, 1f, Unit(seed, 20 + k));
                m_fan.Add(new Vector2(x + Mathf.Cos(a) * r, y + Mathf.Sin(a) * r * 0.7f));
            }
            m_canvas.EraseEllipse(x, y, size + 0.8f, size * 0.7f + 0.8f);
            var hub = new Vector2(x, y);
            m_canvas.m_markBody = BodyKind.North;
            for (int i = 0; i < corners; i++)
            {
                Vector2 a = m_fan[i], b = m_fan[(i + 1) % corners];
                Poly(Grow(hub, hub, a, b), Grow(a, hub, a, b), Grow(b, hub, a, b));
                m_canvas.Erase(m_points);
            }
            m_canvas.m_markBody = BodyKind.None;
            m_points.Clear();
            m_points.AddRange(m_fan);
            m_points.Add(m_fan[0]);
            m_canvas.Polyline(m_points, Pen);
        }

        // A stone on the forest floor: a small flattened outline.
        private void Stone(float x, float y, float s, int seed)
        {
            float rx = 1.4f * s * Vary(seed, 10, 0.25f);
            float ry = rx * 0.7f;
            m_canvas.EraseEllipse(x, y - ry, rx + 0.5f * m_pen, ry + 0.5f * m_pen);
            m_canvas.Ring(x, y - ry, rx, ry, Pen);
        }

        // A tuft of grass: two short strokes.
        private void Grass(float x, float y, float s)
        {
            m_canvas.Stroke(x - 1f * s, y, x - 1.6f * s, y - 2.6f * s, Pen);
            m_canvas.Stroke(x + 0.8f * s, y, x + 1.2f * s, y - 2.8f * s, Pen);
        }
    }
}
