using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    // The pen-drawn pins; Traced stands for any other icon, redrawn from its sprite.
    internal enum PinGlyph
    {
        Fire,
        House,
        Hammer,
        Point,
        Portal,
        Death,
        // The game's own pins, which players cannot choose.
        Bed,
        Boss,
        Tomb,
        Cavern,
        Tower,
        StoneCircle,
        Trader,
        Tent,
        Cauldron,
        Forge,
        Traced,
    }

    // A pin drawn into the sheet itself (InkPainter), at its place in world metres. The
    // Sacrificial Stones are one too (StoneCircle), from the game's location pin like any other.
    internal struct SheetPin
    {
        public float m_x, m_z;
        public PinGlyph m_glyph;
        public bool m_checked;

        // A key for a list of pins, the same for the same pins in any order: the disk cache keeps
        // it with the sheet, and a sheet drawn with other pins is painted again.
        public static long Key(IList<SheetPin> pins)
        {
            var parts = new List<long>(pins.Count);
            foreach (SheetPin pin in pins)
            {
                long x = (long)Math.Round(pin.m_x * 10f), z = (long)Math.Round(pin.m_z * 10f);
                parts.Add(((x & 0xFFFFFF) << 32) ^ ((z & 0xFFFFFF) << 8) ^ ((long)pin.m_glyph << 1) ^ (pin.m_checked ? 1L : 0L));
            }
            parts.Sort();
            unchecked
            {
                long hash = (long)14695981039346656037UL;
                foreach (long part in parts)
                    for (int b = 0; b < 8; b++)
                    {
                        hash ^= (part >> (8 * b)) & 0xFF;
                        hash *= 1099511628211L;
                    }
                return hash;
            }
        }
    }

    // One pin as drawn: ink coverage and the body it scrapes (0 to 255 each), size x size pixels,
    // rows running south as on the sheet.
    internal sealed class PinMark
    {
        public int m_size;
        public byte[] m_ink;
        public byte[] m_body;
    }

    /// <summary>
    /// The pins drawn with the map's own pens: a solid red sign with its details cut back to paper
    /// standing in a body that scrapes the
    /// terrain ink round it, instead of an icon on a parchment ring, which reads as a sticker on
    /// the hand-drawn map. The body is what keeps a pin legible on dark ground. Pins are laid out in a box of
    /// PinUnits units, standing on the pin's place (Lift); pure math, so the preview draws them too.
    /// </summary>
    internal static class PinGlyphs
    {
        public const float PinUnits = 26f;
        // The outline pen, heavier than the terrain's so a marker stands out as a symbol, and the
        // fine pen for hatching, both in pin units. A pin drawn into the sheet uses the map's own
        // pen for every line instead (Draw's pen), so it reads as drawn by the same hand.
        private const float MarkerPen = 2.3f;
        private const float MarkerFinePen = 1.1f;
        private const float MarkerPaperPen = 1.6f;
        [ThreadStatic] private static float s_pen;
        // A sheet pin's outline is half again the map's pen, as a peak's flanks are, so a symbol
        // outranks the terrain round it; its inner lines keep the map's pen.
        private const float SheetOutline = 1.5f;
        private static float Pen => s_pen > 0f ? s_pen * SheetOutline : MarkerPen;
        private static float FinePen => s_pen > 0f ? s_pen : MarkerFinePen;
        // Details cut back to paper through a solid glyph: wider than the pen, as a gap in red
        // ink reads thinner than a red line of the same width.
        private static float PaperPen => s_pen > 0f ? s_pen * 1.2f : MarkerPaperPen;
        // Every line of paper cut through a pin (the gap round a part inked in front, the portal's
        // joints, the bag's tie, the tower's slit, the skull's teeth) is PaperCut of the paper pen
        // wide, one width for every glyph (the user's call). The pen is a sheet pixel in every
        // box, so that is 0.675 sheet px on every pin and about 1.9 px in the 4x close-ups. It is
        // the tent's hem line, the user's three quarters of an earlier 0.75 line: wider cuts thin
        // the grave's back stele at its 12 px box, narrower ones barely show on the sheet.
        private const float PaperCut = 0.5625f;
        private static float Cut => PaperPen * PaperCut;
        // The engraver's gap: terrain ink stops this far short of the pin's lines, as pen lines
        // break off round a symbol on a hand-drawn map, so trees do not run into its outline. A
        // pin drawn into the sheet keeps the halo a tree's crown scrapes round its own line, half
        // a pen past the ink (a crown's erase reaches 0.7 pen past its line's middle), so pins and
        // trees stand off each other alike.
        private const float MarkerGap = 1.2f;
        private const float SheetGap = 0.5f;
        private static float Gap => s_pen > 0f ? s_pen * SheetGap : MarkerGap;
        // A pin drawn into the sheet wavers as a hand-drawn line does: every point is moved by a
        // smooth noise of its place, up to Waver pin units, over WaverGrain units.
        private const float Waver = 0.45f;
        private const float WaverGrain = 3.5f;
        // A checked pin's ink scraped away (Abrade). The whole glyph keeps AbradeRubbed of its ink,
        // so it is paler even at the opening zoom where the streaks average out; inside the swath
        // the streaks lift it to AbradeGhost and the grooves to AbradeGroove. The scrape runs
        // AbradeAngleMin to + AbradeAngleSpan degrees up to the east, as a right hand draws a
        // blade across. The swath is AbradeSwathAlong by AbradeSwathAcross pin units (half axes)
        // round the box's middle shifted by up to half of AbradeShift*, its rim ragged by
        // AbradeRimRagged over AbradeRimGrain units and fading from AbradeRimInner. Streaks are
        // value noise stretched along the scrape, sized in pens so they keep at least a pixel on
        // the sheet: a coarse octave (AbradeCoarse of the mix) and a fine one.
        private const float AbradeRubbed = 0.72f;
        private const float AbradeGhost = 0.3f;
        private const float AbradeGroove = 0.06f;
        private const float AbradeAngleMin = 20f;
        private const float AbradeAngleSpan = 20f;
        private const float AbradeSwathAlong = 12.5f;
        private const float AbradeSwathAcross = 7.5f;
        private const float AbradeShiftAlong = 4f;
        private const float AbradeShiftAcross = 3f;
        private const float AbradeRimGrain = 3.5f;
        private const float AbradeRimRagged = 0.45f;
        private const float AbradeRimInner = 0.7f;
        private const float AbradeCoarse = 0.65f;
        private const float AbradeCoarseAlong = 4.5f;
        private const float AbradeCoarseAcross = 0.9f;
        private const float AbradeFineAlong = 1.6f;
        private const float AbradeFineAcross = 0.55f;
        private const float AbradeLiftFrom = 0.3f;
        private const float AbradeLiftTo = 0.52f;
        private const float AbradeGrooveFrom = 0.68f;
        private const float AbradeGrooveTo = 0.78f;
        private const int AbradeSalt = 31;
        // How far a traced icon's dark interior must fall below its brightest part to be inked,
        // and the share of the body above which the interior is left out (an all-dark icon).
        private const float TraceDark = 0.55f;
        private const float TraceMostDark = 0.6f;
        // The hand (Hand, HandInFront, HandGrown, the user's choice for the campfire, Haldor's bag,
        // the cauldron and the bed): an exact outline's corners sharpen into crisp mitres and its
        // long edges read as ruled, so a part is traced as a pen would draw it. Every corner is
        // rounded over HandRound of the outline pen (about 0.7 sheet px), as Solid rounds the
        // other pins' corners by growing them half a pen. A tip stays a tip, only blunted: an
        // inset and regrow would put knobs on acute tips. Each edge is then cut into pieces
        // HandPieceMin to HandPieceMax units long, unevenly, every point nudged up to HandNudge
        // off the line, so long edges run in faint facets that the waver bends further. Under a
        // tenth of a sheet pixel: larger nudges or shorter pieces looked serrated in close-ups.
        private const float HandRound = 0.45f;
        private const float HandNudge = 0.09f;
        private const float HandPieceMin = 2f;
        private const float HandPieceMax = 3.6f;
        // A paper cut in the hand (HandCut): its corners rounded over HandCutRound of the paper
        // pen and its edges nudged HandCutNudge as far as a part's, since a cut is narrower
        // than a part and a full nudge shows as a wobble in it.
        private const float HandCutRound = 0.4f;
        private const float HandCutNudge = 0.6f;

        // How far a glyph is lifted off its pin's place, as a share of the pin's size: a pin drawn
        // in elevation stands on its place as a tree or a peak does, its foot (the ground line it
        // is drawn on, in pin units) on the spot; the dot and icons traced from sprites
        // stay centred on it.
        public static float Lift(PinGlyph glyph) => (FootOf(glyph) - PinUnits * 0.5f) / PinUnits;

        private static float FootOf(PinGlyph glyph)
        {
            switch (glyph)
            {
                case PinGlyph.Fire: return 22.2f;
                case PinGlyph.House: return HouseFoot;
                case PinGlyph.Hammer: return 22.4f;
                case PinGlyph.Portal: return PortalGround;
                case PinGlyph.Death: return DeathFoot;
                case PinGlyph.Bed: return 22.2f;
                case PinGlyph.Boss: return 21.4f;
                case PinGlyph.Tomb: return 21.4f;
                case PinGlyph.Cavern: return 21.6f;
                case PinGlyph.Tower: return 21.8f;
                case PinGlyph.Trader: return 22.6f;
                case PinGlyph.Tent: return TentFoot;
                case PinGlyph.Cauldron: return 22.2f;
                case PinGlyph.Forge: return 22.2f;
                case PinGlyph.StoneCircle: return StonesFoot;
                default: return PinUnits * 0.5f;
            }
        }

        // pen, when given, is every line's width in pin units (the map's pen, for a pin drawn into
        // the sheet), and seed, when not 0, makes the lines waver by hand (Waver).
        public static PinMark Draw(PinGlyph glyph, int size, float pen = 0f, int seed = 0)
        {
            s_pen = pen;
            try
            {
                return DrawWith(glyph, size, seed);
            }
            finally
            {
                s_pen = 0f;
            }
        }

        private static PinMark DrawWith(PinGlyph glyph, int size, int seed)
        {
            float scale = size / PinUnits;
            var ink = new InkCanvas(size, size, 0f, 0f, scale);
            var body = new InkCanvas(size, size, 0f, 0f, scale);
            if (seed != 0)
            {
                Func<Vector2, Vector2> waver = p => p + new Vector2(
                    (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed) - 0.5f) * 2f * Waver,
                    (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed + 1) - 0.5f) * 2f * Waver);
                ink.m_warp = waver;
                body.m_warp = waver;
            }
            // The body canvas starts all ink and the silhouette is scraped from it, as the
            // canvas scrapes concave outlines in one pass; the body is what got scraped.
            body.Fill(new[] { new Vector2(-1f, -1f), new Vector2(PinUnits + 1f, -1f), new Vector2(PinUnits + 1f, PinUnits + 1f), new Vector2(-1f, PinUnits + 1f) });
            switch (glyph)
            {
                case PinGlyph.Fire: Fire(ink, body); break;
                case PinGlyph.House: House(ink, body); break;
                case PinGlyph.Hammer: Hammer(ink, body); break;
                case PinGlyph.Point: Point(ink, body); break;
                case PinGlyph.Portal: Portal(ink, body); break;
                case PinGlyph.Death: Death(ink, body); break;
                case PinGlyph.Bed: Bed(ink, body); break;
                case PinGlyph.Boss: Boss(ink, body); break;
                case PinGlyph.Tomb: Tomb(ink, body); break;
                case PinGlyph.Cavern: Cavern(ink, body); break;
                case PinGlyph.Tower: Tower(ink, body); break;
                case PinGlyph.StoneCircle: StoneCircle(ink, body); break;
                case PinGlyph.Trader: Trader(ink, body); break;
                case PinGlyph.Tent: Tent(ink, body); break;
                case PinGlyph.Cauldron: Cauldron(ink, body); break;
                case PinGlyph.Forge: Forge(ink, body); break;
            }
            var mark = new PinMark { m_size = size, m_ink = ink.m_ink, m_body = new byte[size * size] };
            for (int i = 0; i < mark.m_body.Length; i++) mark.m_body[i] = (byte)(255 - body.m_ink[i]);
            Widen(mark, Gap * scale);
            return mark;
        }

        // Grows the body by gap pixels round the body and every line.
        private static void Widen(PinMark mark, float gap)
        {
            int n = mark.m_size;
            const float Steps = 16f;
            var distance = new byte[n * n];
            new DistanceField(n).Compute(i => mark.m_body[i] > 127 || mark.m_ink[i] > 127, distance, Steps);
            for (int i = 0; i < n * n; i++)
            {
                float d = distance[i] / Steps - 0.5f;
                mark.m_body[i] = Math.Max(mark.m_body[i], (byte)(Mathf.Clamp01(gap - d + 0.5f) * 255f + 0.5f));
            }
        }

        // A campfire (the user's design): a two-tongued flame on two long, low crossed logs, in
        // the hand. The logs are solid bars with square ends, sawn a little off square and one
        // thicker than the other, longer than the flame is wide so their ends show either side:
        // round caps read as crossed bones. The flame is inked in front, a line of paper apart.
        private static void Fire(InkCanvas ink, InkCanvas body)
        {
            Hand(ink, body, SawnLog(2.2f, 16.3f, 23.8f, 19.8f, 4.4f, -0.5f, 0.6f), 101);
            Hand(ink, body, SawnLog(2.6f, 19.9f, 23.4f, 15.7f, 4.8f, 0.5f, -0.4f), 102);
            List<Vector2> flame = Curve(Points(14.5f, 1.7f, 18.7f, 7.9f, 19.3f, 12.5f, 17.5f, 15.7f, 13.1f, 16.6f, 8.7f, 15.5f,
                6.8f, 12.5f, 6.7f, 9f, 7.4f, 5.1f, 10.1f, 8.9f, 11.7f, 6.1f), closed: true);
            HandInFront(ink, body, flame, 103);
        }

        // A log from a to b, thick across, as a quad whose ends are sawn off square but for
        // skewA and skewB units along the log at each end, so the two logs' ends are not ruled.
        private static List<Vector2> SawnLog(float ax, float ay, float bx, float by, float thick, float skewA, float skewB)
        {
            Vector2 d = new Vector2(bx - ax, by - ay).normalized;
            Vector2 n = new Vector2(-d.y, d.x) * (thick * 0.5f);
            var a = new Vector2(ax, ay);
            var b = new Vector2(bx, by);
            return new List<Vector2> { a + n + d * skewA, b + n + d * skewB, b - n - d * skewB, a - n - d * skewA };
        }

        // Points from x, y pairs.
        private static List<Vector2> Points(params float[] xy)
        {
            var points = new List<Vector2>(xy.Length / 2);
            for (int i = 0; i + 1 < xy.Length; i += 2) points.Add(new Vector2(xy[i], xy[i + 1]));
            return points;
        }

        // A part in the hand: its corners rounded to the pen (round of the outline pen) and its
        // edges traced, scraped from the body and inked solid out to the outline (the outline is
        // the inked edge).
        private static void Hand(InkCanvas ink, InkCanvas body, IList<Vector2> outline, int salt, float round = HandRound)
        {
            List<Vector2> o = HandTrace(RoundCorners(outline, Pen * round), salt);
            body.EraseOutline(o, 0f);
            FillSolid(ink, o);
        }

        // A part in the hand drawn as Solid draws: corners rounded half as much, traced, then
        // grown by half the outline pen, which rounds them the rest of the way.
        private static void HandGrown(InkCanvas ink, InkCanvas body, IList<Vector2> outline, int salt)
        {
            List<Vector2> o = HandTrace(RoundCorners(outline, Pen * HandRound * 0.5f), salt);
            body.EraseOutline(o, Pen * 0.5f);
            Solid(ink, o);
        }

        // The same, inked in front of what is drawn, a line of paper (Cut) apart.
        private static void HandInFront(InkCanvas ink, InkCanvas body, IList<Vector2> outline, int salt, float round = HandRound)
        {
            List<Vector2> o = HandTrace(RoundCorners(outline, Pen * round), salt);
            body.EraseOutline(o, 0f);
            ink.EraseOutline(o, Cut);
            FillSolid(ink, o);
        }

        // A paper cut through the ink in the hand, such as a door.
        private static void HandCut(InkCanvas ink, IList<Vector2> outline, int salt)
        {
            ink.EraseOutline(HandTrace(RoundCorners(outline, PaperPen * HandCutRound), salt, HandNudge * HandCutNudge), 0f);
        }

        // A whole outline from its east half (from the top middle down to the bottom middle),
        // mirrored about the box's middle with the west half squeeze as wide and the top leaning
        // lean units east, so the two halves are no ruled mirror image. The foot stays put.
        private static List<Vector2> MirrorHand(IList<Vector2> east, float squeeze, float lean, float top, float foot)
        {
            const float Middle = PinUnits * 0.5f;
            var outline = new List<Vector2>(east);
            for (int i = east.Count - 2; i >= 1; i--) outline.Add(new Vector2(Middle - (east[i].x - Middle) * squeeze, east[i].y));
            for (int i = 0; i < outline.Count; i++)
            {
                float t = Mathf.Clamp01((foot - outline[i].y) / Mathf.Max(foot - top, 1e-3f));
                outline[i] = new Vector2(outline[i].x + lean * t, outline[i].y);
            }
            return outline;
        }

        // Rounds every corner of a closed outline: the corner is replaced by a quadratic curve from
        // r back along one edge to r along the next (less on short edges), so a tip stays a tip,
        // only blunted as a pen's would be, and long edges keep their line.
        private static List<Vector2> RoundCorners(IList<Vector2> outline, float r)
        {
            int n = outline.Count;
            var rounded = new List<Vector2>(n * 4);
            for (int i = 0; i < n; i++)
            {
                Vector2 p = outline[(i - 1 + n) % n], c = outline[i], q = outline[(i + 1) % n];
                float c0 = Mathf.Min(r, Vector2.Distance(p, c) * 0.45f), c1 = Mathf.Min(r, Vector2.Distance(c, q) * 0.45f);
                Vector2 a = c + (p - c).normalized * c0, b = c + (q - c).normalized * c1;
                for (int k = 0; k <= 3; k++)
                {
                    float t = k / 3f;
                    rounded.Add((1 - t) * (1 - t) * a + 2 * (1 - t) * t * c + t * t * b);
                }
            }
            return rounded;
        }

        // Cuts each edge of a closed outline into pieces of uneven length and nudges every point
        // up to amp (HandNudge for a part) off the line, as a pen traces it. The salt is per part, so a pin draws
        // the same everywhere.
        private static List<Vector2> HandTrace(IList<Vector2> outline, int salt, float amp = HandNudge)
        {
            var traced = new List<Vector2>();
            int n = outline.Count, k = 0;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.magnitude;
                Vector2 normal = len > 1e-4f ? new Vector2(-d.y, d.x) / len : Vector2.zero;
                traced.Add(a + normal * (HandHash(salt, k++) - 0.5f) * amp);
                float s = HandPieceMin + (HandPieceMax - HandPieceMin) * HandHash(salt + 7, k);
                while (s < len - HandPieceMin * 0.5f)
                {
                    traced.Add(a + d * (s / len) + normal * (HandHash(salt, k++) - 0.5f) * 2f * amp);
                    s += HandPieceMin + (HandPieceMax - HandPieceMin) * HandHash(salt + 7, k);
                }
            }
            return traced;
        }

        // A hash of two integers to [0, 1), for the trace's pieces and nudges.
        private static float HandHash(int a, int b)
        {
            unchecked
            {
                uint h = (uint)(a * 374761393 + b * 668265263);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }

        // A longhouse (the user's design, the dragon hall): a long gabled hall seen side-on in
        // the hand, a low wall under a big roof whose ridge runs out at each end into a carved
        // dragon head looking outward, as on the stave churches and Valheim's own Wood Dragon
        // Adornment. The roof is inked in front of the wall a line of paper apart (the eave), the
        // door a paper cut on the long side toward the east end. A gable end facing the viewer
        // read as a modern home button, and hipped side views as a bungalow.
        private const float HouseFoot = 21.6f;

        private static void House(InkCanvas ink, InkCanvas body)
        {
            Hand(ink, body, Points(5.4f, 15.6f, 20.8f, 15.4f, 21.6f, HouseFoot, 4.6f, HouseFoot), 1211);
            List<Vector2> roof = Points(
                4.2f, 15.7f, 4.5f, 10.8f,
                // The west head: neck, chin, snout, brow, crest, back of the neck.
                3.6f, 9.8f, 2.6f, 8.9f, 1.4f, 8.7f, 1.3f, 7.6f, 2.8f, 7.0f, 3.8f, 6.5f, 4.9f, 7.8f, 5.9f, 9.0f, 7.0f, 9.6f,
                13f, 9.7f, 19.2f, 9.3f,
                // The east head, a little different, as carved by hand.
                20.2f, 8.8f, 21.1f, 7.6f, 22.2f, 6.3f, 23.2f, 6.8f, 24.7f, 7.4f, 24.8f, 8.5f, 23.5f, 8.7f, 22.5f, 9.6f, 21.8f, 10.5f,
                // The eave bowing down a little to the middle, as the long walls bow out.
                22.0f, 15.5f, 19.0f, 16.4f, 13f, 16.9f, 7.2f, 16.3f);
            HandInFront(ink, body, roof, 1212);
            // The door runs on through the ground line, so it opens at the bottom.
            HandCut(ink, Points(15.9f, 18.0f, 18.2f, 17.9f, 18.4f, 23f, 16.0f, 23f), 1213);
        }

        // The game's "hammer" pin is Mjolnir, Thor's hammer as the Norse wore it on pendants, a
        // solid silhouette with square corners after the Skane hammer (Swedish History Museum,
        // photo by Ola Myrin): a broad bar on top, arched a little in the middle (the loop, a
        // bird's head on the original), a narrow neck whose sides curve in and flare out into the
        // head's shoulders, and a head as wide as the pin, its sides near upright down to sharp
        // outer corners and its lower edge falling to a blunted point in the middle.
        private static readonly Vector2[] s_mjolnirEast =
        {
            new Vector2(13f, 3.21f), new Vector2(16.11f, 3.9f), new Vector2(21.17f, 4.41f), new Vector2(21.51f, 4.84f),
            new Vector2(21.51f, 7.68f), new Vector2(20.94f, 8.03f), new Vector2(16.68f, 8.8f), new Vector2(15.99f, 9.66f),
            new Vector2(15.88f, 12.24f), new Vector2(16.45f, 14.39f), new Vector2(18.87f, 15.42f), new Vector2(22.55f, 16.02f),
            new Vector2(22.89f, 16.8f), new Vector2(22.43f, 19.21f), new Vector2(17.72f, 21.18f), new Vector2(14.3f, 22.25f), new Vector2(13f, 22.4f),
        };

        private static void Hammer(InkCanvas ink, InkCanvas body)
        {
            // East side from the middle of the top down to the point of the lower edge, then the
            // mirrored west side back up.
            var outline = new List<Vector2>(s_mjolnirEast);
            for (int i = s_mjolnirEast.Length - 2; i >= 1; i--) outline.Add(new Vector2(PinUnits - s_mjolnirEast[i].x, s_mjolnirEast[i].y));
            body.EraseOutline(outline, 0f);
            FillSolid(ink, outline);
        }

        // Inks a simple polygon, concave or not, solid, its edge pushed out by grow pin units: a
        // scratch canvas starts all ink and has the outline scraped from it in one pass, and what
        // was scraped is the fill.
        private static void FillSolid(InkCanvas ink, IList<Vector2> outline, float grow = 0f)
        {
            var shape = new InkCanvas(ink.m_width, ink.m_height, 0f, 0f, ink.m_width / PinUnits) { m_warp = ink.m_warp };
            shape.Fill(new[] { new Vector2(-1f, -1f), new Vector2(PinUnits + 1f, -1f), new Vector2(PinUnits + 1f, PinUnits + 1f), new Vector2(-1f, PinUnits + 1f) });
            shape.EraseOutline(outline, grow);
            for (int i = 0; i < ink.m_ink.Length; i++)
                ink.m_ink[i] = Math.Max(ink.m_ink[i], (byte)(255 - shape.m_ink[i]));
        }

        // A part inked solid out to where its outline's pen reached, so a solid glyph keeps the
        // size the outlined one had.
        private static void Solid(InkCanvas ink, IList<Vector2> outline) => FillSolid(ink, outline, Pen * 0.5f);

        // A part inked solid in front of what is already drawn, a line of paper (Cut) apart from it.
        private static void SolidInFront(InkCanvas ink, IList<Vector2> outline)
        {
            ink.EraseOutline(outline, Pen * 0.5f + Cut);
            Solid(ink, outline);
        }

        // Scrapes a line of paper (Cut) through the ink along the points.
        private static void PaperLine(InkCanvas ink, IList<Vector2> points)
        {
            for (int i = 1; i < points.Count; i++) ink.Scrape(points[i - 1].x, points[i - 1].y, points[i].x, points[i].y, Cut);
        }

        // A circle as an outline of sides points.
        private static List<Vector2> Circle(float cx, float cy, float r, int sides = 24)
        {
            var circle = new List<Vector2>(sides);
            for (int k = 0; k < sides; k++)
            {
                float a = Mathf.PI * 2f * k / sides;
                circle.Add(new Vector2(cx + Mathf.Cos(a) * r, cy + Mathf.Sin(a) * r));
            }
            return circle;
        }

        // A dot; its body is the paper round it.
        private static void Point(InkCanvas ink, InkCanvas body)
        {
            body.EraseEllipse(13f, 13f, 5.8f, 5.8f);
            Solid(ink, Circle(13f, 13f, 4.8f));
        }

        // The portal, for the rune icon players set on portals (the user's design): a ring of six
        // solid stones sunk onto a thin stepped slab, as the game portal stands, every joint a line
        // of paper pointing at the ring's centre. The ring is lowered just until its opening
        // reaches the slab's line of paper, so the opening is open at the bottom with no neck;
        // the ring below the slab's top is not drawn and its two lowest stones stand on the slab,
        // cut flat. The opening stays in the body, so the ground's wash fills it and no terrain
        // ink shows through the portal.
        private const float PortalCx = 13f;
        private const float PortalCy = 13.3f;
        private const float PortalInner = 5.28f;
        private const float PortalOuter = 8.58f;
        // The slab's top: the ring is cut there.
        private const float PortalSlabTop = 20.8f;
        // The body round the ring reaches a little past the stones' bulge.
        private const float PortalBodyReach = 0.4f;
        // How far a stone's outside swells past the ring in its middle, so its end rounds off and
        // the ring's edge reads as scalloped, stone by stone.
        private const float PortalBulge = 0.4f;
        // The stones' sides start PortalInnerSink inside the opening (scraped back after) and their
        // outer corners PortalShoulder inside the ring, so the bulge rounds them.
        private const float PortalInnerSink = 0.6f;
        private const float PortalShoulder = 0.35f;
        private const int PortalStoneSteps = 5;
        // Stones by their boundaries in degrees (0 east, 90 up), drawn back to front: the two
        // lowest, which run past the slab's top and are cut there, the arch's sides, then the top.
        // The side joints sit 10 degrees below level: just below level they read as level stacking
        // joints rather than radial ones, and any lower the stones under them would keep too
        // little height above the slab. The top stone runs 36 to 108, longer than the rest, so the
        // crown has no joint in its middle (the user's choice).
        private static readonly float[][] s_portalStones =
        {
            new[] { 190f, 250f }, new[] { -70f, -10f },
            new[] { 144f, 190f }, new[] { -10f, 36f }, new[] { 108f, 144f }, new[] { 36f, 108f },
        };
        // Everything below the slab's top, where nothing of the ring shows.
        private static readonly Vector2[] s_portalBelow =
        {
            new Vector2(-1f, PortalSlabTop), new Vector2(PinUnits + 1f, PortalSlabTop),
            new Vector2(PinUnits + 1f, PinUnits + 1f), new Vector2(-1f, PinUnits + 1f),
        };
        // A low stepped slab the ring is sunk onto, its top at PortalSlabTop.
        private static readonly Vector2[] s_portalSlab =
        {
            new Vector2(7f, 20.8f), new Vector2(19f, 20.8f), new Vector2(20f, 21.22f), new Vector2(23f, 21.535f),
            new Vector2(21.6f, 22.165f), new Vector2(4.4f, 22.165f), new Vector2(3f, 21.535f), new Vector2(6f, 21.22f),
        };
        private const float PortalGround = 22.17f;

        private static void Portal(InkCanvas ink, InkCanvas body)
        {
            body.EraseOutline(Circle(PortalCx, PortalCy, PortalOuter + PortalBodyReach, 32), Pen * 0.5f);
            // The ring's body below the slab's top goes back to the ground before the slab scrapes
            // its own, or the ring's round bottom would show as a halo under the slab.
            body.Fill(s_portalBelow);
            // Each stone grows only a little past its outline: grown the whole outline pen, a 36
            // degree stone would have no room left at the opening's edge for a centred joint.
            float grow = Pen * 0.2f;
            float outer = PortalOuter + Pen * 0.3f;
            foreach (float[] stone in s_portalStones) FillSolid(ink, PortalStone(stone[0], stone[1], outer, grow), grow);
            // The stones are inked touching and each joint (a boundary two stones share) is then
            // scraped along its radius, through the ring and past its bulge. Left as a gap between
            // two outlines, each side wavered on its own and the joints came out about half as wide
            // as every other cut, some pinched shut.
            foreach (float[] stone in s_portalStones)
                foreach (float[] next in s_portalStones)
                {
                    if (next == stone || next[0] != stone[1]) continue;
                    Vector2 from = new Vector2(PortalCx, PortalCy) + PortalRay(stone[1]) * (PortalInner - 1f);
                    Vector2 to = new Vector2(PortalCx, PortalCy) + PortalRay(stone[1]) * (outer + PortalBulge + 1f);
                    ink.Scrape(from.x, from.y, to.x, to.y, Cut);
                }
            // The opening and the lowest stones' ends are cut before the slab, so it stays whole
            // in front of them.
            ink.Erase(Circle(PortalCx, PortalCy, PortalInner));
            ink.Erase(s_portalBelow);
            body.EraseOutline(s_portalSlab, Pen * 0.5f);
            SolidInFront(ink, s_portalSlab);
        }

        // One stone of the ring between angles a and b (degrees). Its sides are the boundary
        // radii moved in by d, the pen the solid grows, so neighbouring stones meet on their
        // shared radius and the joint scraped along it is centred and points at the ring's centre.
        private static List<Vector2> PortalStone(float a, float b, float rOut, float d)
        {
            var c = new Vector2(PortalCx, PortalCy);
            float inner = PortalInner - PortalInnerSink, shoulder = rOut - PortalShoulder;
            var s = new List<Vector2> { c + PortalRay(a) * inner + PortalSide(a) * d };
            for (int k = 1; k < PortalStoneSteps; k++) s.Add(c + PortalRay(a + (b - a) * k / PortalStoneSteps) * inner);
            s.Add(c + PortalRay(b) * inner - PortalSide(b) * d);
            s.Add(c + PortalRay(b) * shoulder - PortalSide(b) * d);
            // The bulge spans only the angle between the moved sides, or it would reach into the
            // joints.
            float m = Mathf.Asin(Mathf.Min(1f, d / shoulder)) * Mathf.Rad2Deg;
            const int Bulge = PortalStoneSteps + 1;
            for (int k = Bulge - 1; k >= 1; k--)
            {
                float t = k / (float)Bulge;
                float r = shoulder + (PortalShoulder + PortalBulge) * Mathf.Sin(Mathf.PI * t);
                s.Add(c + PortalRay(a + m + (b - a - 2f * m) * t) * r);
            }
            s.Add(c + PortalRay(a) * shoulder + PortalSide(a) * d);
            return s;
        }

        // The unit ray at deg (0 east, 90 up, y running south), and the unit normal toward larger
        // angles, where a stone lies from its side a.
        private static Vector2 PortalRay(float deg) => new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), -Mathf.Sin(deg * Mathf.Deg2Rad));

        private static Vector2 PortalSide(float deg) => new Vector2(-Mathf.Sin(deg * Mathf.Deg2Rad), -Mathf.Cos(deg * Mathf.Deg2Rad));

        // Where a player died, their grave (the user's design): two round-topped steles on a
        // plinth, staggered as one behind the other in the map's oblique view, never a cross,
        // which has no place in a Norse afterlife. The back stele is taller and stands west; the
        // shorter front one stands east, set forward (its foot further south) over the back one's
        // lower east part, so the back one's arch and west side show over and beside it. Side by
        // side at this width each stele read as a thin pillar, and a small overlap left the back
        // one a hooked sliver. The values are inked edges; outlines are pulled in by half the
        // outline pen, which the solid fill grows back.
        // The plinth spans the glyph's width; with the back stele's top that gives the Forge of
        // Potential's inked height : width, so the two read as one set.
        private const float DeathLeft = 5.2f;
        private const float DeathRight = 20.8f;
        private const float DeathFoot = 22f;
        private const float DeathPlinthTop = 19.4f;
        // The steles stand in from the plinth's ends, so it steps out beside them; at a 12 px box
        // a smaller step does not show.
        private const float DeathBackLeft = 7f;
        private const float DeathBackRight = 14.6f;
        private const float DeathBackTop = 2.2f;
        // The back stele runs a pen below the plinth's top, so no paper shows under its foot.
        private const float DeathBackSink = 1f;
        private const float DeathFrontLeft = 12f;
        private const float DeathFrontRight = 19f;
        private const float DeathFrontTop = 7.4f;

        private static void Death(InkCanvas ink, InkCanvas body)
        {
            float g = Pen * 0.5f;
            List<Vector2> back = Stele(DeathBackLeft + g, DeathBackRight - g, DeathBackTop + g, DeathPlinthTop + DeathBackSink);
            List<Vector2> front = Stele(DeathFrontLeft + g, DeathFrontRight - g, DeathFrontTop + g, DeathPlinthTop);
            var plinth = new List<Vector2>
            {
                new Vector2(DeathLeft + g, DeathPlinthTop), new Vector2(DeathRight - g, DeathPlinthTop),
                new Vector2(DeathRight - g, DeathFoot), new Vector2(DeathLeft + g, DeathFoot),
            };
            // The front stele and the plinth scrape the body as one outline, round the plinth from
            // its west foot, along its top, over the front stele and on.
            var piece = new List<Vector2> { plinth[3], plinth[0] };
            piece.AddRange(front);
            piece.Add(plinth[1]);
            piece.Add(plinth[2]);
            body.EraseOutline(back, g);
            Solid(ink, back);
            body.EraseOutline(piece, g);
            SolidInFront(ink, front);
            Solid(ink, plinth);
        }

        // A round-topped stele between left and right, from its top down to foot, as an open
        // outline from its west foot over the arch to its east foot.
        private static List<Vector2> Stele(float left, float right, float top, float foot)
        {
            const int Steps = 12;
            float half = (right - left) * 0.5f, cx = (left + right) * 0.5f, archY = top + half;
            var outline = new List<Vector2>(Steps + 3) { new Vector2(left, foot) };
            for (int k = 0; k <= Steps; k++)
            {
                float a = Mathf.PI * (1f - k / (float)Steps);
                outline.Add(new Vector2(cx + Mathf.Cos(a) * half, archY - Mathf.Sin(a) * half));
            }
            outline.Add(new Vector2(right, foot));
            return outline;
        }

        // The game's own pins, each a plain map sign for what the place is rather than a copy of
        // the game's icon.

        // The player's bed (their spawn point, the user's design): seen from the side, long and
        // low, a sleeper under the blanket, in the hand. One solid outline from the west foot up
        // a low headboard leaning a hair, over the pillow and the blanket's uneven hump down to
        // the east end, a thick frame on short square legs. The headboard rises little over the
        // pillow, so it never reads as a chair or as the grave's tall west stele.
        private static void Bed(InkCanvas ink, InkCanvas body)
        {
            Hand(ink, body, Points(2.4f, 22.2f, 2.5f, 8.9f, 3.2f, 8.1f, 5.8f, 8.2f, 6.6f, 9.0f, 6.6f, 11.6f, 7.2f, 10.9f, 10.6f, 10.8f,
                11.6f, 11.6f, 12.1f, 13.1f, 13.3f, 12.3f, 15.5f, 11.2f, 18.2f, 10.9f, 20.9f, 11.7f, 23.1f, 12.9f, 23.7f, 13.9f,
                23.7f, 22.2f, 20.2f, 22.2f, 20.2f, 19.8f, 5.8f, 19.9f, 5.8f, 22.2f), 1101);
        }

        // A boss's altar, as Vegvisirs reveal it: a horned skull inked solid, in the language of
        // the game's own boss icon (a solid skull with horns), its eyes and nose
        // left as paper.
        private static readonly Vector2[] s_hornWest =
        {
            new Vector2(7.6f, 11.2f), new Vector2(4.6f, 9.4f), new Vector2(2.8f, 6.4f), new Vector2(2.9f, 2.2f),
            new Vector2(4.6f, 5.6f), new Vector2(6.8f, 7.6f), new Vector2(9.6f, 8.4f),
        };

        private static void Boss(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> skull = Curve(new[]
            {
                new Vector2(13f, 6.8f), new Vector2(17.8f, 8.4f), new Vector2(19.6f, 12.4f), new Vector2(18.8f, 15.8f),
                new Vector2(16.8f, 17.2f), new Vector2(16.4f, 21.4f), new Vector2(9.6f, 21.4f), new Vector2(9.2f, 17.2f),
                new Vector2(7.2f, 15.8f), new Vector2(6.4f, 12.4f), new Vector2(8.2f, 8.4f),
            }, closed: true);
            var hornEast = new List<Vector2>();
            foreach (Vector2 p in s_hornWest) hornEast.Add(new Vector2(PinUnits - p.x, p.y));
            foreach (IList<Vector2> part in new IList<Vector2>[] { skull, s_hornWest, hornEast })
            {
                body.EraseOutline(part, 0f);
                FillSolid(ink, part);
            }
            ink.EraseEllipse(10.5f, 13.2f, 1.7f, 1.9f);
            ink.EraseEllipse(15.5f, 13.2f, 1.7f, 1.9f);
            ink.Erase(new[] { new Vector2(13f, 15.2f), new Vector2(13.9f, 17f), new Vector2(12.1f, 17f) });
            ink.Scrape(11.8f, 19f, 11.8f, 21.6f, Cut);
            ink.Scrape(14.2f, 19f, 14.2f, 21.6f, Cut);
        }

        // Hildir's first request, the Smouldering Tombs: a solid barrow with a paper doorway under
        // a lintel.
        private static void Tomb(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> mound = Curve(new[] { new Vector2(2.8f, 21.4f), new Vector2(7f, 12.6f), new Vector2(13f, 9.2f), new Vector2(19f, 12.6f), new Vector2(23.2f, 21.4f) }, closed: false);
            body.EraseOutline(mound, Pen * 0.5f);
            Solid(ink, mound);
            ink.Stroke(2.8f, 21.4f, 23.2f, 21.4f, Pen);
            ink.Scrape(10f, 13.2f, 16f, 13.2f, Cut);
            ink.EraseOutline(Doorway(10.8f, 15.2f, 16.6f, 15f, 21.4f), 0f);
        }

        // A door with a pointed top, open at the bottom through the ground line below it.
        private static List<Vector2> Doorway(float left, float right, float shoulder, float top, float ground)
        {
            float middle = (left + right) * 0.5f;
            var door = Curve(new[] { new Vector2(left, ground), new Vector2(left, shoulder), new Vector2(middle, top), new Vector2(right, shoulder), new Vector2(right, ground) }, closed: false);
            door.Add(new Vector2(right, ground + 1.5f));
            door.Add(new Vector2(left, ground + 1.5f));
            return door;
        }

        // Hildir's second, the Howling Caverns: a solid rocky height with a paper cave mouth.
        private static void Cavern(InkCanvas ink, InkCanvas body)
        {
            var outline = new List<Vector2>
            {
                new Vector2(2.6f, 21.6f), new Vector2(6f, 13.4f), new Vector2(9.2f, 11.4f), new Vector2(12.4f, 5.4f),
                new Vector2(15.2f, 9.2f), new Vector2(18.2f, 7.8f), new Vector2(21f, 13.6f), new Vector2(23.4f, 21.6f),
            };
            body.EraseOutline(outline, Pen * 0.5f);
            Solid(ink, outline);
            ink.EraseOutline(Doorway(10.4f, 15.6f, 18.4f, 16.6f, 21.6f), 0f);
        }

        // Hildir's third, the Sealed Towers: a plain flat-topped solid tower with a window slit
        // and a door in paper; battlements are too fine to read at a pin's size.
        private static void Tower(InkCanvas ink, InkCanvas body)
        {
            var outline = new List<Vector2>
            {
                new Vector2(8.2f, 21.8f), new Vector2(8.8f, 4.4f), new Vector2(17.2f, 4.4f), new Vector2(17.8f, 21.8f),
            };
            body.EraseOutline(outline, Pen * 0.5f);
            Solid(ink, outline);
            ink.Scrape(13f, 8.6f, 13f, 11.6f, Cut);
            ink.EraseOutline(Doorway(11.4f, 14.6f, 18f, 16.4f, 21.8f), 0f);
        }

        // The Sacrificial Stones, where every Viking wakes: a ring of standing stones round a low
        // altar in the map's oblique view, as its peaks and trees are drawn. Each stone is a solid
        // block, drawn from the back of the ring to the front, a line of paper apart from what
        // lies behind it. It stands on the front of its ring, as every pin stands on its foot.
        private const float StonesCentreY = 15f;
        private const float StonesFoot = 19f;
        private const float StonesRadius = 9f;
        private const float StonesTilt = 0.42f;
        private const int StonesCount = 7;

        private static void StoneCircle(InkCanvas ink, InkCanvas body)
        {
            float ry = StonesRadius * StonesTilt;
            const float Height = 5.8f, Width = 2.1f;
            var order = new List<Vector3>(StonesCount + 1);
            for (int k = 0; k < StonesCount; k++)
            {
                float angle = 0.35f + Mathf.PI * 2f * k / StonesCount;
                order.Add(new Vector3(13f + Mathf.Cos(angle) * StonesRadius, StonesCentreY + Mathf.Sin(angle) * ry, k));
            }
            order.Add(new Vector3(13f, StonesCentreY, -1f));
            order.Sort((a, b) => a.y.CompareTo(b.y));
            foreach (Vector3 stone in order)
            {
                int k = (int)stone.z;
                if (k < 0) StoneBlock(ink, body, stone.x, stone.y, Width * 2.2f, Height * 0.3f, Width, 0f, 0f);
                else StoneBlock(ink, body, stone.x, stone.y, Width * (0.9f + 0.2f * ((k * 37) % 5) / 4f), Height * (0.85f + 0.3f * ((k * 53) % 7) / 6f), Width,
                    ((k * 29) % 5 - 2) * 0.12f * Width, 0.12f * Width);
            }
        }

        // One stone as a block: its front face from the foot up, its east face drawn back and up
        // by depth, its top between them, leaning and tapering a little.
        private static void StoneBlock(InkCanvas ink, InkCanvas body, float x, float y, float width, float height, float depth, float lean, float taper)
        {
            var back = new Vector2(depth * 0.5f, -depth * 0.3f);
            var bottomLeft = new Vector2(x - width * 0.5f, y);
            var bottomRight = new Vector2(x + width * 0.5f, y);
            var topLeft = new Vector2(x - width * 0.5f + taper + lean, y - height);
            var topRight = new Vector2(x + width * 0.5f - taper + lean, y - height);
            var outline = new List<Vector2> { bottomLeft, bottomRight, bottomRight + back, topRight + back, topLeft + back, topLeft };
            body.EraseOutline(outline, Pen * 0.5f);
            SolidInFront(ink, outline);
        }

        // Haldor the trader (the user's design): a solid tied bag in the hand, the cloth gathered
        // above the tie flaring out, the tie a line of paper through the neck. Grown, the tuft's
        // foot and the sack's crown overlap by about 1.6 pens; the tie runs through the middle of
        // that overlap (the tuft's foot 8.85 grown down half a pen, the sack's crown 8.6 grown up
        // half a pen, the half pens cancelling) and on past both sides of the neck, so the tuft
        // stands apart. A pen lower, across the sack's shoulders, the sack's crown stayed on the
        // tuft and the bag read as a lidded jar.
        private const float TraderTie = (8.85f + 8.6f) * 0.5f;

        private static void Trader(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> sack = Curve(Points(13.1f, 8.6f, 17.5f, 10.5f, 20.7f, 16.1f, 18.9f, 21.3f, 13f, 22.6f, 7.3f, 21.2f, 5.5f, 15.9f, 8.7f, 10.3f), closed: true);
            HandGrown(ink, body, sack, 901);
            HandGrown(ink, body, Points(9.5f, 4.1f, 16.5f, 3.8f, 14.3f, 8.8f, 11.8f, 8.9f), 902);
            // The tie sags a little, as a hand draws it.
            List<Vector2> tie = Curve(Points(8.6f, TraderTie - 0.25f, 13f, TraderTie + 0.15f, 17.4f, TraderTie - 0.2f), closed: false);
            PaperLine(ink, tie);
        }

        // Hildir's camp (the user's design): a round bell tent in the hand, a cone bellying out a
        // little over a low wall, its hem hanging in two broad scallops a side, inked in front of
        // the wall a line of paper apart (the hem line). A swallowtail pennant flies from the
        // apex itself, no pole: its hoist runs down into the cone's tip, so flag and tent are one
        // solid. The cone is no mirror image (west half TentSqueeze as wide, top leaning
        // TentLean east), the wall's top slopes. A triangle tent read as an "A" or a peak; four
        // narrow scallops broke the hem line into dots at sheet size.
        private const float TentFoot = 21.6f;
        private const float TentApex = 7.8f;
        // The hem line under the cone's bottom edge where it meets the wall's top.
        private const float TentHem = 17.9f;
        private const float TentSqueeze = 0.96f;
        private const float TentLean = 0.35f;
        // Softer than the hand's other parts (HandRound): the tent is cloth, and its tips (the
        // cone's feet, the swallowtail's points) read as cut sheet metal at 0.45.
        private const float TentRound = 0.7f;

        private static void Tent(InkCanvas ink, InkCanvas body)
        {
            Hand(ink, body, Points(4.9f, 16.9f, 21.3f, 16.4f, 21.8f, TentFoot, 4.2f, TentFoot), 1002, TentRound);
            var east = Curve(Points(13f, TentApex, 15.5f, 10.6f, 18.9f, 13.6f, 23.1f, 16.3f), closed: false);
            east.AddRange(Points(20.0f, 17.5f, 16.7f, 16.9f, 13f, 17.4f));
            HandInFront(ink, body, MirrorHand(east, TentSqueeze, TentLean, TentApex, TentHem), 1003, TentRound);
            // The pennant's hoist on the apex (13 + TentLean), its foot inside the cone's tip.
            Hand(ink, body, Points(13.0f, 9.2f, 12.9f, 2.6f, 13.7f, 1.9f, 21.4f, 3.0f, 18.9f, 4.7f, 21.1f, 6.3f, 14.6f, 6.8f, 14.0f, 9.2f), 1004, TentRound);
        }

        // The Bog Witch (the user's design): a round-bellied cauldron in the hand, the belly its
        // widest part, the rim a solid block narrower than the belly and inked in front of it a
        // line of paper apart (the one line under the rim), stubby solid feet inside the mass,
        // and two big bubbles over the rim, each a line of paper apart. Nothing splays, so it
        // reads as a pot rather than a clawfoot tub, and wide where Haldor's bag is tall.
        private static void Cauldron(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> belly = Curve(Points(7.1f, 10.6f, 3.9f, 13f, 3.1f, 16.5f, 5.7f, 19.6f, 13.1f, 21f, 20.5f, 19.5f, 23f, 16.3f,
                22.1f, 12.9f, 18.9f, 10.5f), closed: false);
            Hand(ink, body, belly, 501);
            Hand(ink, body, Points(6.5f, 18.1f, 10.2f, 19.7f, 9.7f, 22.2f, 6.7f, 22.2f), 502);
            Hand(ink, body, Points(19.3f, 18.3f, 15.9f, 19.8f, 16.4f, 22.2f, 19.3f, 22.2f), 503);
            HandInFront(ink, body, Points(6.3f, 7.9f, 19.7f, 7.6f, 19.8f, 10.3f, 6.2f, 10.5f), 504);
            HandInFront(ink, body, Circle(10.5f, 4.6f, 2.7f, 16), 505);
            HandInFront(ink, body, Circle(16.1f, 3.3f, 2f, 14), 506);
        }

        // The Forge of Potential, after the user's sketch of the game's icon: a solid ball on a
        // tapering pillar that steps out to a broad base, a V of paper cut from the ball's
        // lower edge down into the pillar.
        // How far below the capstone's point the cut's outer edges meet, and how much wider they
        // spread than its inner edges (radians), so the cut widens toward the top.
        private const float ForgeCutPoint = 1f;
        private const float ForgeCutSpread = 0.12f;

        private static void Forge(InkCanvas ink, InkCanvas body)
        {
            var stone = new List<Vector2>
            {
                new Vector2(8.5f, 8.6f), new Vector2(17.4f, 8.6f), new Vector2(18f, 17.8f), new Vector2(18.6f, 17.8f),
                new Vector2(18.6f, 19.6f), new Vector2(19.8f, 19.6f), new Vector2(19.8f, 22.2f), new Vector2(6.2f, 22.2f),
                new Vector2(6.2f, 19.6f), new Vector2(7.1f, 19.6f), new Vector2(7.1f, 17.8f), new Vector2(7.8f, 17.8f),
            };
            List<Vector2> ball = Circle(13f, 7.2f, 4.4f);
            body.EraseOutline(stone, Pen * 0.5f);
            body.EraseOutline(ball, Pen * 0.5f);
            Solid(ink, stone);
            Solid(ink, ball);
            // The V widens upward: its inner edges run from the point tangent to the ball, so ball
            // and wedge read as one smooth capstone, its outer edges spread wider into the
            // pillar's shoulders, and both run out past the shoulders, so its top stays open.
            var centre = new Vector2(13f, 7.2f);
            var point = new Vector2(13f, 17.2f);
            var bottom = new Vector2(13f, 17.2f + ForgeCutPoint);
            float radius = 4.4f + Pen * 0.5f;
            float inner = Mathf.Asin(Mathf.Min(1f, radius / Vector2.Distance(point, centre)));
            float outer = inner + ForgeCutSpread;
            const float Top = 5.5f;
            var cut = new List<Vector2> { bottom };
            foreach (float side in new[] { -1f, 1f })
            {
                // Up and out from the point at the angle from upright, to the row Top.
                Vector2 Along(Vector2 from, float angle) => from + new Vector2(side * Mathf.Tan(angle), -1f) * (from.y - Top);
                if (side < 0f)
                {
                    cut.Add(Along(bottom, outer));
                    cut.Add(Along(point, inner));
                    cut.Add(point);
                }
                else
                {
                    cut.Add(Along(point, inner));
                    cut.Add(Along(bottom, outer));
                }
            }
            ink.EraseOutline(cut, 0f);
        }
        // A checked pin looks as if someone tried to scrape its paint away (the user's design): the
        // whole glyph rubbed back, then one ragged swath along a scraping direction lifting the ink
        // in streaks to a ghost of itself, the deepest grooves almost to the ground. Only the ink
        // is lifted, never the body, so the silhouette still says which pin it was and the
        // ground's wash shows through the grooves. Everything is in pin units and pens, through
        // the pin's own waver, so the sheet and every close-up scrape it alike. pen is the line
        // width in pin units Draw was given (0 for a marker's pens); seed is not 0.
        public static void Abrade(PinMark mark, float pen, int seed)
        {
            if (pen <= 0f) pen = MarkerFinePen;
            int n = mark.m_size;
            float angle = (AbradeAngleMin + AbradeAngleSpan * Noise.Unit(seed, AbradeSalt)) * Mathf.Deg2Rad;
            float dx = Mathf.Cos(angle), dy = -Mathf.Sin(angle);
            float offAlong = (Noise.Unit(seed, AbradeSalt + 1) - 0.5f) * AbradeShiftAlong;
            float offAcross = (Noise.Unit(seed, AbradeSalt + 2) - 0.5f) * AbradeShiftAcross;
            byte[] ink = mark.m_ink;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = y * n + x;
                    if (ink[i] == 0) continue;
                    var p = new Vector2((x + 0.5f) / n * PinUnits, (y + 0.5f) / n * PinUnits);
                    p += new Vector2(
                        (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed) - 0.5f) * 2f * Waver,
                        (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed + 1) - 0.5f) * 2f * Waver);
                    float cx = p.x - PinUnits * 0.5f, cy = p.y - PinUnits * 0.5f;
                    float along = cx * dx + cy * dy, across = -cx * dy + cy * dx;
                    // The swath: an ellipse along the scrape, its rim ragged.
                    float sa = (along - offAlong) / AbradeSwathAlong, sc = (across - offAcross) / AbradeSwathAcross;
                    float r = Mathf.Sqrt(sa * sa + sc * sc)
                        + (Noise.Value((along - offAlong) / AbradeRimGrain, (across - offAcross) / AbradeRimGrain, seed + 5) - 0.5f) * AbradeRimRagged;
                    float swath = 1f - Smoothstep(AbradeRimInner, 1f, r);
                    // Grooves: noise stretched along the scrape and about a pen across, so a streak
                    // is at least a pixel wide on the sheet.
                    float streak = AbradeCoarse * Noise.Value(along / (pen * AbradeCoarseAlong), across / (pen * AbradeCoarseAcross), seed + 11)
                        + (1f - AbradeCoarse) * Noise.Value(along / (pen * AbradeFineAlong), across / (pen * AbradeFineAcross), seed + 13);
                    float lift = Smoothstep(AbradeLiftFrom, AbradeLiftTo, streak) * swath;
                    float remain = AbradeRubbed - lift * (AbradeRubbed - AbradeGhost);
                    remain += (AbradeGroove - remain) * Smoothstep(AbradeGrooveFrom, AbradeGrooveTo, streak) * swath;
                    ink[i] = (byte)(ink[i] * remain + 0.5f);
                }
        }

        private static float Smoothstep(float from, float to, float x)
        {
            float t = Mathf.Clamp01((x - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        // Main thread or worker: any other icon (bosses, traders, other mods' pins), redrawn from
        // its sprite. alpha and luma are the sprite's pixels, w x h with rows running north as
        // textures do; the sprite fills the pin's box. Its silhouette becomes the body, with a pen
        // line along its edge, and clearly darker parts inside are inked too, so a boss head
        // keeps its features without turning into a black blob.
        public static PinMark Trace(byte[] alpha, byte[] luma, int w, int h, int size, byte solid)
        {
            float scale = size / PinUnits;
            int n = size;
            var inside = new float[n * n];
            var shade = new float[n * n];
            float fit = Math.Max(w, h);
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    // Pixel centre in sprite pixels, the sprite centred in the box at its aspect.
                    float sx = (x + 0.5f) / n * fit - (fit - w) * 0.5f - 0.5f;
                    float sy = (n - 1 - y + 0.5f) / n * fit - (fit - h) * 0.5f - 0.5f;
                    inside[y * n + x] = Bilinear(alpha, w, h, sx, sy) / 255f;
                    shade[y * n + x] = Bilinear(luma, w, h, sx, sy) / 255f;
                }

            float limit = solid / 255f;
            var distance = new byte[n * n];
            const float Steps = 16f;
            new DistanceField(n).Compute(i => inside[i] <= limit, distance, Steps);
            float pen = Pen * scale;
            float brightest = 0f, dark = 0f;
            int count = 0;
            for (int i = 0; i < n * n; i++)
            {
                if (inside[i] <= limit) continue;
                brightest = Math.Max(brightest, shade[i]);
                count++;
            }
            for (int i = 0; i < n * n; i++)
                if (inside[i] > limit && shade[i] < brightest * TraceDark) dark++;
            bool details = count > 0 && dark / count < TraceMostDark;

            var mark = new PinMark { m_size = n, m_ink = new byte[n * n], m_body = new byte[n * n] };
            for (int i = 0; i < n * n; i++)
            {
                if (inside[i] <= limit) continue;
                // Distance in pixels from the silhouette's edge, which lies half a cell out.
                float d = distance[i] / Steps - 0.5f;
                float line = Mathf.Clamp01(pen * 0.5f - d + 0.5f);
                float detail = details ? Mathf.Clamp01((brightest * TraceDark - shade[i]) / (brightest * 0.15f + 1e-3f)) : 0f;
                mark.m_ink[i] = (byte)(Math.Max(line, detail) * 255f + 0.5f);
                mark.m_body[i] = 255;
            }
            // The line's outer half lies past the silhouette; the body reaches the gap beyond it.
            var grown = new byte[n * n];
            new DistanceField(n).Compute(i => inside[i] > limit, grown, Steps);
            for (int i = 0; i < n * n; i++)
            {
                if (mark.m_body[i] != 0) continue;
                float d = grown[i] / Steps - 0.5f;
                mark.m_ink[i] = (byte)(Mathf.Clamp01(pen * 0.5f - d + 0.5f) * 255f + 0.5f);
                mark.m_body[i] = (byte)(Mathf.Clamp01(pen * 0.5f + Gap * scale - d + 0.5f) * 255f + 0.5f);
            }
            return mark;
        }

        private static float Bilinear(byte[] values, int w, int h, float x, float y)
        {
            if (x < -0.5f || y < -0.5f || x > w - 0.5f || y > h - 0.5f) return 0f;
            int x0 = Mathf.Clamp((int)Math.Floor(x), 0, w - 1), y0 = Mathf.Clamp((int)Math.Floor(y), 0, h - 1);
            int x1 = Math.Min(x0 + 1, w - 1), y1 = Math.Min(y0 + 1, h - 1);
            float tx = Mathf.Clamp01(x - x0), ty = Mathf.Clamp01(y - y0);
            float top = values[y0 * w + x0] + (values[y0 * w + x1] - values[y0 * w + x0]) * tx;
            float bottom = values[y1 * w + x0] + (values[y1 * w + x1] - values[y1 * w + x0]) * tx;
            return top + (bottom - top) * ty;
        }

        // A closed outline inked all round.
        private static void Loop(InkCanvas ink, IList<Vector2> points, float width)
        {
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 a = points[i], b = points[(i + 1) % points.Count];
                ink.Stroke(a.x, a.y, b.x, b.y, width);
            }
        }

        // A smooth curve through the points (Catmull-Rom), closed or open.
        private static List<Vector2> Curve(IList<Vector2> points, bool closed)
        {
            const int Steps = 6;
            var curve = new List<Vector2>();
            int count = points.Count, segments = closed ? count : count - 1;
            for (int i = 0; i < segments; i++)
            {
                Vector2 p0 = points[closed ? (i - 1 + count) % count : Math.Max(i - 1, 0)];
                Vector2 p1 = points[i];
                Vector2 p2 = points[(i + 1) % count];
                Vector2 p3 = points[closed ? (i + 2) % count : Math.Min(i + 2, count - 1)];
                for (int s = 0; s < Steps; s++)
                {
                    float t = s / (float)Steps, t2 = t * t, t3 = t2 * t;
                    curve.Add(0.5f * (2f * p1 + (p2 - p0) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (3f * p1 - p0 - 3f * p2 + p3) * t3));
                }
            }
            if (!closed) curve.Add(points[count - 1]);
            return curve;
        }
    }
}
