using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Illustrations for the open sea (the blank open sea may get some artistic
    /// filling), drawn with the atlas's own pen so they sit with its peaks and woods, in the hand
    /// of the engraved sea monsters and ships on Olaus Magnus' Carta Marina and Ortelius'
    /// Islandia: sober, small, no faces. InkPainter.DrawIllustrations places them; Catalogue lays
    /// every one out for the offline preview. Each glyph scrapes its silhouette back to parchment
    /// before inking, as the land glyphs do, so whatever lies behind it is hidden. Sizes are in
    /// sheet pixels at the opening zoom; every line is at least Pen wide and full black, since
    /// thinner lines average to grey there, so a copy drawn smaller or larger scales its shape and
    /// scrapes but keeps its pens.
    /// </summary>
    internal static class SeaGlyphs
    {
        public const int SeaSerpent = 0;
        public const int SerpentCoil = 1;
        public const int Leviathan = 2;
        public const int BonemawSerpent = 3;

        // The finest line, and the outline weight, a shade heavier as the peaks' flanks are.
        private const float Pen = 1f;
        private const float Edge = 1.4f;
        // The serpents' plain outline, heavier still, since it is all the marks they have.
        private const float Plain = 1.6f;
        // Hatch strokes across a body are laid this far apart along it, unless a glyph asks for
        // a sparser hand.
        private const float HatchPitch = 2.1f;
        // A surface whose outward normal points east at least this much is in shade and hatched.
        private const float ShadeFacing = 0.55f;

        // The sea serpent is shown twice, without and with one fin swept back off its head, for
        // the user to choose from.
        public static readonly string[] Names =
        {
            "Sea serpent", "Serpent coil", "Leviathan", "Bonemaw serpent",
        };

        // Draws glyph kind centred on (x, y), its waterline at y, scale times its size, turned east
        // for west when mirror is set. The leviathan is not turned: its east flank is hatched as
        // in shade, as every glyph on the map is lit from the west, so it only lays its shell and
        // barnacles the other way round.
        public static void Draw(InkCanvas canvas, int kind, float x, float y, bool mirror = false, float scale = 1f)
        {
            var c = new Hand(canvas, x, y, mirror && kind != Leviathan, scale);
            switch (kind)
            {
                case SeaSerpent: Serpent(c, x, y, false); break;
                case SerpentCoil: Coil(c, x, y); break;
                case Leviathan: LeviathanShell(c, x, y, mirror); break;
                case BonemawSerpent: Bonemaw(c, x, y); break;
            }
        }

        // The canvas as a glyph draws on it: every point turned east for west about the glyph's
        // centre when mirrored and scaled about it, scrapes scaled with the shape, pens kept, so
        // a smaller copy still draws no line under Pen. The transform lives here, not in a
        // flipped bitmap, so the record keeps sheet points and close-ups replay them sharp.
        private sealed class Hand
        {
            private readonly InkCanvas m_canvas;
            private readonly float m_x, m_y, m_sx, m_scale;
            private readonly List<Vector2> m_points = new List<Vector2>(64);

            public Hand(InkCanvas canvas, float x, float y, bool mirror, float scale)
            {
                m_canvas = canvas;
                m_x = x;
                m_y = y;
                m_scale = scale;
                m_sx = mirror ? -scale : scale;
            }

            private Vector2 At(float x, float y) => new Vector2(m_x + (x - m_x) * m_sx, m_y + (y - m_y) * m_scale);

            private List<Vector2> At(IList<Vector2> points)
            {
                m_points.Clear();
                foreach (Vector2 p in points) m_points.Add(At(p.x, p.y));
                return m_points;
            }

            public void Stroke(float x0, float y0, float x1, float y1, float width)
            {
                Vector2 a = At(x0, y0), b = At(x1, y1);
                m_canvas.Stroke(a.x, a.y, b.x, b.y, width);
            }

            public void Scrape(float x0, float y0, float x1, float y1, float width)
            {
                Vector2 a = At(x0, y0), b = At(x1, y1);
                m_canvas.Scrape(a.x, a.y, b.x, b.y, width * m_scale);
            }

            public void Polyline(IList<Vector2> points, float width) => m_canvas.Polyline(At(points), width);

            public void Fill(IList<Vector2> polygon) => m_canvas.Fill(At(polygon));

            public void Erase(IList<Vector2> polygon) => m_canvas.Erase(At(polygon));
        }

        // Catalogue: one cell per candidate, cell sheet pixels square, in columns.
        public static InkCanvas Catalogue(int columns, float cell, float scale)
        {
            int rows = (Names.Length + columns - 1) / columns;
            // Magnified cells use a close-up's thinner pen (InkCanvas.m_penScale), as detail tiles do;
            // the sheet's pen magnified 4x reads far heavier than any close-up draws it.
            var canvas = new InkCanvas(Mathf.CeilToInt(columns * cell * scale), Mathf.CeilToInt(rows * cell * scale), 0f, 0f, scale)
            {
                m_penScale = scale > 1f ? 1f / Mathf.Sqrt(scale) : 1f,
            };
            for (int k = 0; k < Names.Length; k++)
            {
                float cx = (k % columns + 0.5f) * cell, cy = (k / columns + 0.6f) * cell;
                Draw(canvas, k, cx, cy);
            }
            return canvas;
        }

        // ---- The sea serpent -------------------------------------------------------------------

        // Valheim's ocean serpent as the old maps drew theirs: three coils of its back breaking
        // the water, each a round body cut by the waterline, growing toward the raised neck, and
        // the long snouted head at the top, all in plain outline a shade heavier than the land
        // glyphs' (the plain hand, fewer and bolder marks; a sawtooth crest,
        // throat plates, hatching and a frill of fins were all tried and dropped as too busy).
        private static void Serpent(Hand c, float x, float y, bool fin)
        {
            // Laid out 56 wide and drawn at this scale, so three coils, the neck and the head
            // fit the 48 or so pixels a sea glyph may take.
            const float s = 0.86f;
            float[] radius = { 3.6f, 5.2f, 6.8f };
            float[] centre = { -20.5f, -8.5f, 6.5f };
            for (int i = 0; i < 3; i++)
            {
                float r = radius[i] * s, cx = x + centre[i] * s;
                // Thick enough that little water shows under each coil: thinner, they read as
                // hoops standing in the sea rather than a body passing through it.
                Tube(c, Hump(cx, y, r), t => r * 0.5f * (0.92f + 0.08f * Mathf.Sin(t * 3.1f + i)), y, Plain, Shade.None, Shade.None, HatchPitch, i == 2, null, null);
            }
            // The neck rises in a swan's curve from the water, leaning back then forward.
            float nx = x + 18.5f * s;
            var neck = Curve(t => new Vector2(nx + 4.5f * s * t - 2.2f * s * Mathf.Sin(t * Mathf.PI), y + 2f - 15.5f * s * t), 28);
            float neckTop = 1.8f * s;
            Tube(c, neck, t => Mathf.Lerp(2.6f, 1.8f, t) * s, y, Plain, Shade.None, Shade.None, HatchPitch, true, null, null);
            Head(c, neck[neck.Count - 1], neck[neck.Count - 1] - neck[neck.Count - 2], neckTop, -0.4f, s, false, fin);
            Ripple(c, x - 29f * s, y + 2.6f, 6f);
            Ripple(c, nx + 4f, y + 3f, 7f);
        }

        // The spine of one coil of a serpent's back: an arc of radius r whose centre lies 0.3 r
        // under the waterline, so the body enters the water at a slant and not straight down.
        private static List<Vector2> Hump(float cx, float water, float r)
        {
            // Taller than wide, as a body rising from the water looks from the side.
            const float tall = 1.35f;
            var centre = new Vector2(cx, water + 0.3f * r * tall);
            return Curve(t =>
            {
                float a = Mathf.Lerp(Mathf.PI + 0.5f, -0.5f, t);
                return centre + new Vector2(r * Mathf.Cos(a), -r * tall * Mathf.Sin(a));
            }, Mathf.CeilToInt(r * 6f));
        }

        // A serpent's head on the top of its neck at e, where the neck travels in direction
        // travel and is w wide to each side: a tapered skull flowing out of the neck with no
        // step, a long snout narrowing to the tip, the jaw a single curve under it with the
        // mouth a closed line, a small eye set under the brow. Drawn as a few curved strokes,
        // as the serpents of the Carta Marina and the Islandia map are, not as a polygon: a
        // flat-topped skull with a square, evenly toothed jaw read as a toy figure (user,
        //). The head faces the way the neck's back is not; tilt turns the snout
        // from the neck's own lean (negative lifts it). fin adds one fin swept back off the
        // crown. As a skeleton (bone) the same silhouette is drawn in straight strokes, with a
        // hooked snout, a dark hollow for the eye, the jaw hanging open and a few teeth.
        private static void Head(Hand c, Vector2 e, Vector2 travel, float w, float tilt, float size, bool bone, bool fin)
        {
            travel.Normalize();
            // The head's frame: forward is across the neck toward its throat side, down runs
            // back along the neck; both turned by tilt.
            float theta = Mathf.Atan2(travel.x, -travel.y) + tilt;
            var fwd = new Vector2(Mathf.Cos(theta), Mathf.Sin(theta));
            var dn = new Vector2(-fwd.y, fwd.x);
            Vector2 P(float px, float py) => e + (fwd * px + dn * py) * size;
            var nrm = new Vector2(travel.y, -travel.x);
            Vector2 nape = e + nrm * w, throat = e - nrm * w;
            float wl = w / size;
            List<Vector2> outline;
            if (!bone)
            {
                // Crown to snout tip over the top, back under the jaw to the throat, smoothed
                // through a few points; the first and last are the neck's own edges.
                var top = new List<Vector2>
                {
                    nape, P(0.9f, -wl - 0.9f), P(2.4f, -wl - 1.3f), P(4.3f, -wl - 0.9f), P(6.3f, -wl - 0.1f), P(8f, -wl + 0.8f), P(8.8f, -wl + 1.7f),
                    P(7.6f, -wl + 2.6f), P(5.6f, -wl + 3.3f), P(3.4f, -wl + 3.7f), P(1.4f, -wl + 3.65f), P(0.1f, wl + 0.3f), throat,
                };
                outline = Smooth(top, 4);
            }
            else
            {
                outline = new List<Vector2>
                {
                    nape, P(0.6f, -wl - 1.5f), P(2f, -wl - 2.2f), P(2.8f, -wl - 1.95f), P(4.6f, -wl - 1.7f), P(6.6f, -wl - 0.9f), P(8.3f, -wl - 0.2f), P(9f, -wl + 0.6f),
                    P(8.4f, -wl + 1.2f), P(6f, -wl + 1.4f), P(3.4f, -wl + 1.5f), P(1.6f, -wl + 1.7f), throat,
                };
            }
            if (fin && !bone)
            {
                Vector2 a = P(1.1f, -wl - 1.1f), b = P(-2.4f, -wl - 3.6f), a2 = P(0.2f, -wl - 0.85f);
                c.Scrape(a.x, a.y, b.x, b.y, 2.2f);
                c.Scrape(a2.x, a2.y, b.x, b.y, 2.2f);
                c.Stroke(a.x, a.y, b.x, b.y, Pen);
                c.Stroke(a2.x, a2.y, b.x, b.y, Pen);
            }
            FanErase(c, outline, P(3.5f, -wl + 1.2f));
            c.Polyline(outline, Plain);
            c.Stroke(outline[outline.Count - 1].x, outline[outline.Count - 1].y, outline[0].x, outline[0].y, Plain);
            if (!bone)
            {
                // The mouth, one line back from the tip; the eye a small almond under the brow.
                c.Polyline(Smooth(new List<Vector2> { P(8.5f, -wl + 1.65f), P(7f, -wl + 2.2f), P(5.2f, -wl + 2.45f), P(3.6f, -wl + 2.5f) }, 4), Pen);
                c.Fill(new List<Vector2> { P(2.2f, -wl + 0.05f), P(2.9f, -wl - 0.4f), P(3.8f, -wl + 0.05f), P(2.9f, -wl + 0.4f) });
                return;
            }
            // The skeleton: the lower jaw a wedge hinged in front of the neck, hanging open; the
            // eye a hollow; a tooth or two on each jaw.
            var jaw = new List<Vector2> { P(1.4f, -wl + 1.9f), P(8.2f, -wl + 4.1f), P(2.6f, -wl + 3.9f), P(1.2f, -wl + 2.9f) };
            c.Erase(Grown(jaw, 0.5f));
            Closed(c, jaw, Plain);
            c.Fill(new List<Vector2> { P(2.4f, -wl - 1.4f), P(4.1f, -wl - 1.55f), P(3.5f, -wl - 0.35f) });
            c.Stroke(P(5f, -wl + 1.45f).x, P(5f, -wl + 1.45f).y, P(5.2f, -wl + 2.45f).x, P(5.2f, -wl + 2.45f).y, Pen);
            c.Stroke(P(6.8f, -wl + 1.35f).x, P(6.8f, -wl + 1.35f).y, P(7f, -wl + 2.3f).x, P(7f, -wl + 2.3f).y, Pen);
            c.Stroke(P(4.4f, -wl + 2.95f).x, P(4.4f, -wl + 2.95f).y, P(4.5f, -wl + 2.05f).x, P(4.5f, -wl + 2.05f).y, Pen);
        }

        // A curve through points, a Catmull-Rom spline with steps segments between neighbours.
        private static List<Vector2> Smooth(List<Vector2> points, int steps)
        {
            var curve = new List<Vector2>((points.Count - 1) * steps + 1);
            for (int i = 0; i < points.Count - 1; i++)
            {
                Vector2 p0 = points[Math.Max(i - 1, 0)], p1 = points[i], p2 = points[i + 1], p3 = points[Math.Min(i + 2, points.Count - 1)];
                for (int k = 0; k < steps; k++)
                {
                    float t = k / (float)steps, t2 = t * t, t3 = t2 * t;
                    curve.Add(0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3));
                }
            }
            curve.Add(points[points.Count - 1]);
            return curve;
        }

        // ---- The serpent coil ------------------------------------------------------------------

        // A serpent thrown into a loop above the water, its body passing in front of itself where
        // the loop closes, the tail's tip rising out of the sea to the west and the head rising
        // out of the loop to the east. The first coil read as a snail: a flat
        // spiral has no crossing, and a crossing is what makes a coil; with a crest all round the
        // ring it then read as a cog, so it is plain outline alone.
        private static void Coil(Hand c, float x, float y)
        {
            const float s = 1.12f;
            // The tail's tip, tapering to a point as it rises.
            var tail = Curve(t => new Vector2(x + (-22f + 3.5f * t + 1.5f * Mathf.Sin(t * Mathf.PI)) * s, y + 2f - 7f * s * t), 14);
            Tube(c, tail, t => Mathf.Lerp(1.5f, 0.45f, t * t) * s, y, Plain, Shade.None, Shade.None, HatchPitch, false, null, null);
            // The body is one run: up out of the water on a tangent into a ring, round it, out on
            // the other tangent so the two legs cross under the ring (the shape of an alpha), on
            // down to the water, where it turns up again into the neck. Drawn in that order, so
            // the later run lies in front at the crossing. Angles run anticlockwise from east;
            // the tangents touch the ring spread either side of its bottom, and the legs' lines
            // meet ring / cos(spread) under its centre.
            float ring = 5.4f * s, spread = 0.95f, below = 4.5f * s, turn = 3f * s, lean = 0.3f;
            float angIn = -Mathf.PI * 0.5f + spread, angOut = Mathf.PI * 1.5f - spread;
            var o = new Vector2(x - 8f * s, y - below - ring / Mathf.Cos(spread));
            Vector2 OnRing(Vector2 centre, float ang, float r) => centre + new Vector2(Mathf.Cos(ang), -Mathf.Sin(ang)) * r;
            Vector2 Travel(float ang) => new Vector2(-Mathf.Sin(ang), -Mathf.Cos(ang));
            Vector2 touchIn = OnRing(o, angIn, ring), touchOut = OnRing(o, angOut, ring);
            float legIn = (y + 2.5f - touchIn.y) / Mathf.Sin(spread);
            float arcLen = ring * (angOut - angIn);
            float legOut = (y - 1f - touchOut.y) / Mathf.Sin(spread);
            // The turn: an arc of the leg's own tangent circle, from heading down-east to heading
            // up and a little east, its centre east and above the leg's end.
            Vector2 legEnd = touchOut + Travel(angOut) * legOut;
            Vector2 turnCentre = legEnd - new Vector2(Mathf.Cos(angOut), -Mathf.Sin(angOut)) * turn;
            float angTurnEnd = Mathf.PI * 2f - lean;
            float turnLen = turn * (angTurnEnd - angOut);
            Vector2 neckFoot = OnRing(turnCentre, angTurnEnd, turn);
            float neckLen = 12.5f * s;
            float total = legIn + arcLen + legOut + turnLen + neckLen;
            var body = Curve(t =>
            {
                float d = t * total;
                if (d < legIn) return touchIn - Travel(angIn) * (legIn - d);
                d -= legIn;
                if (d < arcLen)
                {
                    float ang = angIn + d / ring;
                    // The ring's radius wavers a little, as a hand's would.
                    return OnRing(o, ang, ring * (1f + 0.05f * Mathf.Sin(ang * 2f + 0.7f)));
                }
                d -= arcLen;
                if (d < legOut) return touchOut + Travel(angOut) * d;
                d -= legOut;
                if (d < turnLen) return OnRing(turnCentre, angOut + d / turn, turn);
                d -= turnLen;
                // The neck bows back a little then forward, as the serpent's does.
                float u = d / neckLen;
                return neckFoot + Travel(angTurnEnd) * d + new Vector2(-Mathf.Cos(lean), -Mathf.Sin(lean)) * 1.4f * s * Mathf.Sin(u * Mathf.PI);
            }, 140);
            // Thin toward the tail, full where the neck rises.
            Func<float, float> width = t => Mathf.Lerp(1.7f, 2.3f, Mathf.Clamp01(t * 1.6f)) * s * (0.94f + 0.06f * Mathf.Sin(t * 9f));
            Tube(c, body, width, y, Plain, Shade.None, Shade.None, HatchPitch, true, null, null);
            Head(c, body[body.Count - 1], body[body.Count - 1] - body[body.Count - 2], width(1f), -0.45f, 0.9f * s, false, false);
            Ripple(c, x - 17f * s, y + 3.2f, 7f);
            Ripple(c, x + 8f * s, y + 3f, 6f);
        }

        // ---- The leviathan ---------------------------------------------------------------------

        // Valheim's leviathan lying in the sea like a reef: a long low shell with a rough, rocky
        // top, the eastern flank hatched down its curve, a few abyssal barnacles standing as cones
        // on its back among tufts of growth and weed hanging down its flanks to the water. The
        // previous one was a bare dome with rings and ticks (messy); a beaked
        // head at the west end read as a face and was removed. Mirrored, the
        // shell's profile and the barnacles, tufts and ripples run the other way round, while the
        // hatching, ledges and weed stay on the flanks the light gives them.
        private static void LeviathanShell(Hand c, float x, float y, bool mirror)
        {
            float w = 40f, h = 10f;
            float west = x - w * 0.5f, east = x + w * 0.5f;
            // The shell's outline, foot to foot over the top: a flattish dome, highest a little
            // east of the middle, its curve roughened by two slow bumps so it reads as rock.
            var shell = Curve(t =>
            {
                float m = mirror ? 1f - t : t;
                float u = Mathf.Pow(m, 1.1f);
                float lift = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(u * Mathf.PI)), 0.5f);
                float rough = 1f + 0.08f * Mathf.Sin(m * 15.7f + 0.8f) + 0.05f * Mathf.Sin(m * 27.3f + 2.1f);
                return new Vector2(west + w * t, y - h * lift * rough);
            }, 40);
            // A place along the shell from the west foot, or from the east one when mirrored.
            Vector2 On(float t) => Along(shell, mirror ? 1f - t : t);
            FanErase(c, shell, new Vector2(x, y - 1f));
            // Contour hatching down the east flank: strokes from the outline inward and down,
            // bowed with the shell's curve, as the peaks' east faces are hatched.
            for (int k = 0; k < 11; k++)
            {
                float t = Mathf.Lerp(0.58f, 0.975f, (k + 0.5f) / 11f);
                Vector2 from = Along(shell, t);
                float reach = Mathf.Lerp(0.35f, 0.6f, 0.5f + 0.5f * Mathf.Sin(k * 2.1f));
                var to = new Vector2(Mathf.Lerp(from.x, x + 4f, reach), Mathf.Min(y - 0.8f, from.y + h * 0.45f));
                Vector2 mid = Vector2.Lerp(from, to, 0.5f) + new Vector2(0.6f, -0.5f);
                c.Polyline(new List<Vector2> { from, mid, to }, Pen);
            }
            // A few short ledges on the lit flank: the shell is rock, in plates.
            c.Stroke(x - 13f, y - 5.2f, x - 8f, y - 6f, Pen);
            c.Stroke(x - 11f, y - 2.4f, x - 5f, y - 2.9f, Pen);
            c.Stroke(x - 2f, y - 7.4f, x + 3f, y - 7.9f, Pen);
            // Abyssal barnacles: squat cones on the back with a notched crater and a shaded
            // east side; tufts of growth between them, and weed down the lit flank to the water.
            Barnacle(c, On(0.27f), 4.8f, 2.9f);
            Barnacle(c, On(0.5f), 6f, 3.9f);
            Barnacle(c, On(0.7f), 4.4f, 2.7f);
            Tuft(c, On(0.39f), 2.6f, 0);
            Tuft(c, On(0.6f), 2.3f, 1);
            Tuft(c, On(0.17f), 2f, 2);
            Weed(c, Along(shell, 0.12f) + new Vector2(1.2f, 0.4f), y, 0);
            c.Polyline(shell, Edge);
            // The water's edge along the shell, with foam at both feet.
            c.Stroke(west - 1f, y + 0.2f, east + 1f, y + 0.2f, 1.2f);
            Foam(c, new Vector2(west - 1f, y), -1);
            Foam(c, new Vector2(east + 1f, y), 1);
            Ripple(c, mirror ? x - 1f : x - 8f, y + 3.4f, 9f);
            Ripple(c, mirror ? x - 17f : x + 10f, y + 3f, 7f);
        }

        // A barnacle cone standing on the shell at foot: two flanks up to a crater notched
        // between two tips, and a solid wedge down its east side.
        private static void Barnacle(Hand c, Vector2 foot, float width, float height)
        {
            var tipL = new Vector2(foot.x - width * 0.2f, foot.y - height);
            var notch = new Vector2(foot.x + width * 0.02f, foot.y - height * 0.78f);
            var tipR = new Vector2(foot.x + width * 0.22f, foot.y - height * 0.92f);
            var left = new Vector2(foot.x - width * 0.5f, foot.y + 0.5f);
            var right = new Vector2(foot.x + width * 0.5f, foot.y + 0.3f);
            c.Erase(Grown(new List<Vector2> { left, tipL, tipR, right }, 0.5f));
            c.Fill(new List<Vector2> { tipR, right, new Vector2(foot.x + width * 0.1f, foot.y + 0.3f), new Vector2(foot.x + width * 0.08f, foot.y - height * 0.5f) });
            c.Polyline(new List<Vector2> { left, tipL, notch, tipR, right }, 1.2f);
        }

        // A tuft of growth: three or four short blades fanning up from a point.
        private static void Tuft(Hand c, Vector2 at, float height, int k)
        {
            int blades = 3 + (k % 2);
            for (int i = 0; i < blades; i++)
            {
                float lean = Mathf.Lerp(-0.8f, 0.8f, (i + 0.5f) / blades) + 0.15f * Mathf.Sin(k * 2.3f + i);
                float len = height * (0.75f + 0.25f * Mathf.Sin(k * 1.9f + i * 2.7f));
                c.Stroke(at.x + lean * 0.3f, at.y + 0.3f, at.x + lean * len * 0.6f, at.y - len, Pen);
            }
        }

        // Weed hanging from the shell's flank to the water: a wavering strand and a shorter one.
        private static void Weed(Hand c, Vector2 from, float water, int k)
        {
            float drop = water - from.y - 0.4f;
            if (drop < 1.5f) return;
            for (int s = 0; s < 2; s++)
            {
                float len = drop * (s == 0 ? 1f : 0.6f), sway = 0.8f + 0.3f * s;
                float ox = s * 1.6f - 0.8f;
                var strand = Curve(t => new Vector2(from.x + ox + sway * Mathf.Sin(t * 4.5f + k + s), from.y + 0.5f + len * t), 8);
                c.Polyline(strand, Pen);
            }
        }

        // The Ashlands' bonemaw: the same three coils and raised neck as the sea serpent, but as a
        // bare backbone, the vertebrae ticked across it, ribs curving down from it on each coil,
        // and the angular skull with its hollow eye and open, toothed jaws.
        private static void Bonemaw(Hand c, float x, float y)
        {
            float[] radius = { 4f, 5.4f, 6.8f };
            float[] centre = { -19f, -8.5f, 4.5f };
            // The tail's last vertebrae lifting out of the water west of the coils.
            var tail = Curve(t => new Vector2(x - 28f + 4f * t, y + 1.5f - 4f * t + 1.2f * Mathf.Sin(t * Mathf.PI)), 8);
            Backbone(c, tail, y, false, 4);
            for (int i = 0; i < 3; i++)
            {
                var spine = Hump(x + centre[i], y, radius[i]);
                Backbone(c, spine, y, true, i);
            }
            float nx = x + 17.5f;
            var neck = Curve(t => new Vector2(nx + 3.5f * t - 2f * Mathf.Sin(t * Mathf.PI), y + 2f - 13.5f * t), 24);
            Backbone(c, neck, y, false, 3);
            Head(c, neck[neck.Count - 1], neck[neck.Count - 1] - neck[neck.Count - 2], 0.85f, -0.3f, 0.85f, true, false);
            Ripple(c, x - 24f, y + 3f, 6f);
            Ripple(c, nx + 4f, y + 2.8f, 6f);
        }

        // A length of backbone above the water: scraped clear under it, inked, ticked across every
        // vertebra, and with ribs hanging from it (curving down and in toward the coil's hollow)
        // where the bone arches over the water.
        private static void Backbone(Hand c, List<Vector2> spine, float water, bool ribs, int k)
        {
            var above = Clip(spine, water);
            if (above.Count < 2) return;
            for (int i = 1; i < above.Count; i++) c.Scrape(above[i - 1].x, above[i - 1].y, above[i].x, above[i].y, 3.4f);
            // Ribs first, so the spine's line lies over their roots.
            float since = 2f;
            for (int i = 1; i < above.Count - 1 && ribs; i++)
            {
                since += Vector2.Distance(above[i - 1], above[i]);
                if (since < 4f) continue;
                since = 0f;
                Vector2 d = (above[i + 1] - above[i - 1]).normalized;
                var inward = new Vector2(-d.y, d.x);
                if (inward.y < 0.25f) continue;
                // Each rib curves down and back under the arch, stopping short of the water.
                float len = Mathf.Min(7.5f, (water - 1f - above[i].y) / Math.Max(inward.y, 0.3f));
                if (len < 2.5f) continue;
                Vector2 root = above[i], mid = root + inward * len * 0.45f + d * 1.6f, end = root + inward * len + d * 3.2f;
                c.Scrape(root.x, root.y, mid.x, mid.y, 2.2f);
                c.Scrape(mid.x, mid.y, end.x, end.y, 2.2f);
                c.Polyline(new List<Vector2> { root, mid, end }, 1.2f);
            }
            c.Polyline(above, Edge);
            // The vertebrae: short ticks across the bone, every few pixels.
            since = 1.5f;
            for (int i = 1; i < above.Count; i++)
            {
                since += Vector2.Distance(above[i - 1], above[i]);
                if (since < 3f) continue;
                since = 0f;
                Vector2 d = (above[i] - above[i - 1]).normalized;
                var n = new Vector2(d.y, -d.x) * (0.85f + 0.15f * Mathf.Sin(i * 1.3f + k));
                c.Stroke(above[i].x - n.x, above[i].y - n.y, above[i].x + n.x, above[i].y + n.y, Pen);
            }
        }

        // ---- Shared drawing --------------------------------------------------------------------

        // Which edge of a body gets a mark: its back (left of travel), its belly, or both.
        private enum Shade
        {
            None,
            Both,
            Back,
            Belly,
        }

        // Inks a round body along a spine as the engravers drew a serpent: its band scraped clear
        // and edged segment by segment, so where the body crosses itself the part drawn later lies
        // in front; hatch strokes laid across it from the edge turned from the light (facing
        // east), reaching part way over, which rounds it; and short plate marks from the other
        // edge where plates asks for them. Below water (y over water) nothing is drawn; where the
        // body meets the water its cut is inked level, with foam if asked. back and belly, when
        // given, receive the two edges (back to the left of travel) for crests and the like.
        private static void Tube(Hand c, List<Vector2> spine, Func<float, float> half, float water, float edge, Shade shade, Shade plates, float pitch, bool foam, List<Vector2> back, List<Vector2> belly)
        {
            int n = spine.Count;
            var a = new List<Vector2>(n);
            var b = new List<Vector2>(n);
            var grow = new List<Vector2>(n);
            var along = new List<Vector2>(n);
            var facing = new List<float>(n);
            bool cut = !float.IsNaN(water);
            for (int i = 0; i < n; i++)
            {
                Vector2 d = (spine[Math.Min(i + 1, n - 1)] - spine[Math.Max(i - 1, 0)]).normalized;
                var nrm = new Vector2(d.y, -d.x);
                float h = half(i / (float)(n - 1));
                a.Add(spine[i] + nrm * h);
                b.Add(spine[i] - nrm * h);
                // The scrape reaches past the edge lines sideways, and a little along the spine
                // so neighbouring quads overlap: meeting edge to edge they each left the shared
                // pixels half inked, a faint seam wherever ink lay under them (the crossing of a
                // coil). A segment's lines are inked only after the next quad is scraped, or the
                // overlap eats their ends and the edges bead.
                grow.Add(nrm * 0.6f * edge);
                along.Add(d * 0.35f);
                // Which edge is in shade: the one whose outward normal faces east (and a little up,
                // so an underside is not hatched as much as a flank).
                facing.Add(Vector2.Dot(nrm, new Vector2(0.96f, -0.28f)));
            }
            if (back != null) back.AddRange(a);
            if (belly != null) belly.AddRange(b);
            float since = pitch * 0.5f;
            int pending = -1;
            for (int i = 0; i < n - 1; i++)
            {
                if (cut && spine[i].y > water + 0.3f && spine[i + 1].y > water + 0.3f) continue;
                Vector2 a0 = Lid(a[i], water, cut), a1 = Lid(a[i + 1], water, cut), b0 = Lid(b[i], water, cut), b1 = Lid(b[i + 1], water, cut);
                c.Erase(new List<Vector2>
                {
                    Lid(a0 + grow[i] - along[i], water, cut), Lid(a1 + grow[i + 1] + along[i + 1], water, cut),
                    Lid(b1 - grow[i + 1] + along[i + 1], water, cut), Lid(b0 - grow[i] - along[i], water, cut),
                });
                if (pending >= 0) EdgeLines(c, a, b, pending, water, cut, edge);
                pending = i;
                since += Vector2.Distance(spine[i], spine[i + 1]);
                if (since < pitch) continue;
                since = 0f;
                float f = facing[i];
                bool hatch = Math.Abs(f) >= ShadeFacing && shade != Shade.None && !(f > 0 && shade == Shade.Belly || f < 0 && shade == Shade.Back);
                Vector2 from, to;
                float reach;
                if (hatch)
                {
                    from = f > 0 ? a0 : b0;
                    to = f > 0 ? b0 : a0;
                    reach = Mathf.Lerp(0.5f, 0.8f, 0.5f + 0.5f * Mathf.Sin(i * 2.9f + spine[0].x)) * Mathf.Clamp01((Math.Abs(f) - ShadeFacing) / 0.3f + 0.6f);
                }
                else if (plates == Shade.Back || plates == Shade.Belly)
                {
                    from = plates == Shade.Back ? a0 : b0;
                    to = plates == Shade.Back ? b0 : a0;
                    reach = Mathf.Lerp(0.28f, 0.4f, 0.5f + 0.5f * Mathf.Sin(i * 1.7f));
                }
                else continue;
                if (from.y >= water - 0.6f && cut) continue;
                Vector2 end = Vector2.Lerp(from, to, reach);
                c.Stroke(from.x, from.y, end.x, end.y, Pen);
            }
            if (pending >= 0) EdgeLines(c, a, b, pending, water, cut, edge);
            if (!cut) return;
            // The cut where the body enters the water at either end, with a little foam.
            if (spine[0].y > water) WaterCut(c, a, b, water, 1, foam);
            if (spine[n - 1].y > water) WaterCut(c, a, b, water, -1, foam);
        }

        // Inks both edges of a body's segment i, cut at the water.
        private static void EdgeLines(Hand c, List<Vector2> a, List<Vector2> b, int i, float water, bool cut, float edge)
        {
            Vector2 a0 = Lid(a[i], water, cut), a1 = Lid(a[i + 1], water, cut), b0 = Lid(b[i], water, cut), b1 = Lid(b[i + 1], water, cut);
            c.Stroke(a0.x, a0.y, a1.x, a1.y, edge);
            c.Stroke(b0.x, b0.y, b1.x, b1.y, edge);
        }

        // Clamps a point to the waterline.
        private static Vector2 Lid(Vector2 p, float water, bool cut) => cut && p.y > water ? new Vector2(p.x, water) : p;

        // The level line across a body where it meets the water, between the points where its two
        // edges cross the waterline, searched from the start (dir 1) or the end (dir -1).
        private static void WaterCut(Hand c, List<Vector2> a, List<Vector2> b, float water, int dir, bool foam)
        {
            float ax, bx;
            if (!CrossX(a, water, dir, out ax) || !CrossX(b, water, dir, out bx)) return;
            c.Stroke(ax, water, bx, water, Pen);
            if (!foam) return;
            Foam(c, new Vector2(Math.Min(ax, bx), water), -1);
            Foam(c, new Vector2(Math.Max(ax, bx), water), 1);
        }

        private static bool CrossX(List<Vector2> edge, float water, int dir, out float x)
        {
            int start = dir > 0 ? 0 : edge.Count - 1;
            for (int i = start; i >= 0 && i < edge.Count; i += dir)
            {
                int j = i + dir;
                if (j < 0 || j >= edge.Count) break;
                Vector2 p = edge[i], q = edge[j];
                if (p.y >= water && q.y < water)
                {
                    x = Mathf.Lerp(p.x, q.x, (p.y - water) / (p.y - q.y));
                    return true;
                }
            }
            x = 0f;
            return false;
        }

        // A little foam where something breaks the water: two short ticks splayed up and outward
        // from the point, to dir (1 east, -1 west).
        private static void Foam(Hand c, Vector2 at, int dir)
        {
            c.Stroke(at.x + dir * 0.4f, at.y - 0.2f, at.x + dir * 2.2f, at.y - 1.6f, Pen);
            c.Stroke(at.x + dir * 0.8f, at.y + 0.3f, at.x + dir * 3f, at.y - 0.3f, Pen);
        }

        // A short level ripple of a few crests, the sea's own mark, length long from x.
        private static void Ripple(Hand c, float x, float y, float length)
        {
            int crests = Math.Max(2, Mathf.RoundToInt(length / 2.8f));
            var points = Curve(t => new Vector2(x + length * t, y - 0.6f * Mathf.Sin(t * crests * Mathf.PI * 2f) * Mathf.Clamp01(Mathf.Sin(Mathf.PI * t) * 1.6f)), crests * 8);
            c.Polyline(points, Pen);
        }

        // The part of a polyline above the water, cut at the waterline.
        private static List<Vector2> Clip(List<Vector2> line, float water)
        {
            var kept = new List<Vector2>(line.Count);
            for (int i = 0; i < line.Count; i++)
            {
                Vector2 p = line[i];
                bool above = p.y <= water;
                if (i > 0)
                {
                    Vector2 q = line[i - 1];
                    bool prevAbove = q.y <= water;
                    if (above != prevAbove) kept.Add(new Vector2(Mathf.Lerp(q.x, p.x, (q.y - water) / (q.y - p.y)), water));
                }
                if (above) kept.Add(p);
            }
            return kept;
        }

        // Erases a star-shaped outline as a fan of triangles from a hub inside it, each grown a
        // little so no seam is left between them (as the peaks' bodies are scraped).
        private static void FanErase(Hand c, List<Vector2> outline, Vector2 hub)
        {
            for (int i = 1; i < outline.Count; i++)
                c.Erase(Grown(new List<Vector2> { hub, outline[i - 1], outline[i] }, 0.6f));
        }

        // A convex polygon pushed out from its centre by margin, so the scrape reaches past the
        // line inked on its edge.
        private static List<Vector2> Grown(List<Vector2> polygon, float margin)
        {
            Vector2 centre = Vector2.zero;
            foreach (Vector2 p in polygon) centre += p;
            centre /= polygon.Count;
            var grown = new List<Vector2>(polygon.Count);
            foreach (Vector2 p in polygon)
            {
                Vector2 out_ = p - centre;
                float length = out_.magnitude;
                grown.Add(length > 1e-4f ? p + out_ / length * margin : p);
            }
            return grown;
        }

        // Erases a possibly concave band polygon (an outline there and back) as strips of quads.
        private static void ErasePieces(Hand c, List<Vector2> band)
        {
            int n = band.Count / 2;
            for (int i = 0; i < n - 1; i++)
                c.Erase(new List<Vector2> { band[i], band[i + 1], band[band.Count - 2 - i], band[band.Count - 1 - i] });
        }

        private static void Closed(Hand c, List<Vector2> points, float width)
        {
            c.Polyline(points, width);
            c.Stroke(points[points.Count - 1].x, points[points.Count - 1].y, points[0].x, points[0].y, width);
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

        private static List<Vector2> Curve(Func<float, Vector2> at, int segments)
        {
            var points = new List<Vector2>(segments + 1);
            for (int i = 0; i <= segments; i++) points.Add(at(i / (float)segments));
            return points;
        }
    }
}
