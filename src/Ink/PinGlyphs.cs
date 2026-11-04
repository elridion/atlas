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
        Checked,
        Traced,
    }

    // A pin drawn into the sheet itself (InkPainter), at its place in world metres. The
    // Sacrificial Stones are one too (StoneCircle), from the game's location icons.
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
        // The parts that are rock, filled with the peaks' wash on the sheet; null for none.
        public byte[] m_rock;
    }

    /// <summary>
    /// The pins drawn with the map's own pens: a pen line round a body of bare paper, as every
    /// glyph on the sheet is drawn, instead of a solid black icon on a parchment ring, which reads
    /// as a sticker on the hand-drawn map. The body is what keeps a pin legible
    /// on dark ground: inside it the paper shows with no terrain ink. Pins are laid out in a box of
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
        private const float MarkerLogWidth = 2.6f;
        private const float MarkerRunePen = 1.6f;
        [ThreadStatic] private static float s_pen;
        // While a glyph is drawn, the parts of it that are rock (the Sacrificial Stones), filled
        // with the peaks' wash on the sheet instead of the ground's.
        [ThreadStatic] private static InkCanvas s_rock;
        // A sheet pin's outline is half again the map's pen, as a peak's flanks are, so a symbol
        // outranks the terrain round it; its inner lines keep the map's pen.
        private const float SheetOutline = 1.5f;
        private static float Pen => s_pen > 0f ? s_pen * SheetOutline : MarkerPen;
        private static float FinePen => s_pen > 0f ? s_pen : MarkerFinePen;
        // The logs are solid bars rather than lines, a little heavier than the pen.
        private static float LogWidth => s_pen > 0f ? s_pen * 1.6f : MarkerLogWidth;
        private static float RunePen => s_pen > 0f ? s_pen * 1.2f : MarkerRunePen;
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
        // How far a traced icon's dark interior must fall below its brightest part to be inked,
        // and the share of the body above which the interior is left out (an all-dark icon).
        private const float TraceDark = 0.55f;
        private const float TraceMostDark = 0.6f;

        // How far a glyph is lifted off its pin's place, as a share of the pin's size: a pin drawn
        // in elevation stands on its place as a tree or a peak does, its foot (the ground line it
        // is drawn on, in pin units) on the spot; the open circle and icons traced from sprites
        // stay centred on it, and the stone circle has the middle of its ring there.
        public static float Lift(PinGlyph glyph) => (FootOf(glyph) - PinUnits * 0.5f) / PinUnits;

        private static float FootOf(PinGlyph glyph)
        {
            switch (glyph)
            {
                case PinGlyph.Fire: return 22.2f;
                case PinGlyph.House: return 21.6f;
                case PinGlyph.Hammer: return 22.4f;
                case PinGlyph.Portal: return 21.8f;
                case PinGlyph.Death: return 22f;
                case PinGlyph.Bed: return 21.6f;
                case PinGlyph.Boss: return 21.4f;
                case PinGlyph.Tomb: return 21.4f;
                case PinGlyph.Cavern: return 21.6f;
                case PinGlyph.Tower: return 21.8f;
                case PinGlyph.Trader: return 22.6f;
                case PinGlyph.Tent: return 21.6f;
                case PinGlyph.Cauldron: return 23.4f;
                case PinGlyph.Forge: return 22f;
                case PinGlyph.StoneCircle: return StonesCentreY;
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
                s_rock = null;
            }
        }

        private static PinMark DrawWith(PinGlyph glyph, int size, int seed)
        {
            float scale = size / PinUnits;
            var ink = new InkCanvas(size, size, 0f, 0f, scale);
            var body = new InkCanvas(size, size, 0f, 0f, scale);
            var rock = glyph == PinGlyph.StoneCircle ? new InkCanvas(size, size, 0f, 0f, scale) : null;
            s_rock = rock;
            if (seed != 0)
            {
                Func<Vector2, Vector2> waver = p => p + new Vector2(
                    (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed) - 0.5f) * 2f * Waver,
                    (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed + 1) - 0.5f) * 2f * Waver);
                ink.m_warp = waver;
                body.m_warp = waver;
                if (rock != null) rock.m_warp = waver;
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
                case PinGlyph.Checked: Checked(ink); break;
            }
            s_rock = null;
            var mark = new PinMark { m_size = size, m_ink = ink.m_ink, m_body = new byte[size * size], m_rock = rock?.m_ink };
            for (int i = 0; i < mark.m_body.Length; i++) mark.m_body[i] = (byte)(255 - body.m_ink[i]);
            if (glyph != PinGlyph.Checked) Widen(mark, Gap * scale);
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

        // A campfire: two crossed logs under a flame with a tongue inside it.
        private static void Fire(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> flame = Curve(new[]
            {
                new Vector2(13.4f, 2.5f), new Vector2(17.6f, 9.5f), new Vector2(17.2f, 14.6f), new Vector2(13f, 17.4f),
                new Vector2(8.8f, 14.8f), new Vector2(8.6f, 10.2f), new Vector2(11.2f, 7.4f),
            }, closed: true);
            Log(ink, body, 5.5f, 22.2f, 20.5f, 17.2f);
            Log(ink, body, 5.5f, 17.2f, 20.5f, 22.2f);
            body.EraseOutline(flame, Pen * 0.5f);
            ink.EraseOutline(flame, 0f);
            Loop(ink, flame, Pen);
            ink.Polyline(Curve(new[] { new Vector2(13f, 15.6f), new Vector2(11.6f, 12.4f), new Vector2(12.6f, 9.6f) }, closed: false), FinePen);
        }

        // A log as one thick stroke.
        private static void Log(InkCanvas ink, InkCanvas body, float x0, float y0, float x1, float y1)
        {
            body.Scrape(x0, y0, x1, y1, LogWidth);
            ink.Stroke(x0, y0, x1, y1, LogWidth);
        }

        // A longhouse: plain walls under a gable roof with crossed ridge ends, its eastern
        // roof plane hatched as the map shades every glyph's eastern side.
        private static void House(InkCanvas ink, InkCanvas body)
        {
            var outline = new List<Vector2>
            {
                new Vector2(3.6f, 13.2f), new Vector2(13f, 4.4f), new Vector2(22.4f, 13.2f), new Vector2(20.4f, 13.2f),
                new Vector2(20.4f, 21.6f), new Vector2(5.6f, 21.6f), new Vector2(5.6f, 13.2f),
            };
            body.EraseOutline(outline, Pen * 0.5f);
            ink.Polyline(new[] { new Vector2(3.6f, 13.2f), new Vector2(13f, 4.4f), new Vector2(22.4f, 13.2f), new Vector2(3.6f, 13.2f) }, Pen);
            ink.Polyline(new[] { new Vector2(5.6f, 13.2f), new Vector2(5.6f, 21.6f), new Vector2(20.4f, 21.6f), new Vector2(20.4f, 13.2f) }, Pen);
            ink.Stroke(13f, 4.4f, 11.2f, 2.2f, FinePen);
            ink.Stroke(13f, 4.4f, 14.8f, 2.2f, FinePen);
            for (float x = 15.2f; x < 21.5f; x += 2.1f) ink.Stroke(x, 4.4f + (x - 13f) * 0.936f + 0.9f, x, 12.6f, FinePen);
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

        // Inks a simple polygon, concave or not, solid: a scratch canvas starts all ink and has
        // the outline scraped from it in one pass, and what was scraped is the fill.
        private static void FillSolid(InkCanvas ink, IList<Vector2> outline)
        {
            var shape = new InkCanvas(ink.m_width, ink.m_height, 0f, 0f, ink.m_width / PinUnits) { m_warp = ink.m_warp };
            shape.Fill(new[] { new Vector2(-1f, -1f), new Vector2(PinUnits + 1f, -1f), new Vector2(PinUnits + 1f, PinUnits + 1f), new Vector2(-1f, PinUnits + 1f) });
            shape.EraseOutline(outline, 0f);
            for (int i = 0; i < ink.m_ink.Length; i++)
                ink.m_ink[i] = Math.Max(ink.m_ink[i], (byte)(255 - shape.m_ink[i]));
        }

        // An open circle; its body is the paper inside.
        private static void Point(InkCanvas ink, InkCanvas body)
        {
            body.EraseEllipse(13f, 13f, 5.8f, 5.8f);
            ink.Ring(13f, 13f, 4.8f, 4.8f, Pen);
        }

        // A dolmen, two standing stones under a capstone, for the rune icon players set on
        // portals; the gap between the stones stays open ground.
        private static void Portal(InkCanvas ink, InkCanvas body)
        {
            var cap = new List<Vector2> { new Vector2(4.6f, 7.4f), new Vector2(21.6f, 6.4f), new Vector2(21f, 10.4f), new Vector2(5f, 10.8f) };
            var west = new List<Vector2> { new Vector2(6.4f, 10.8f), new Vector2(10.2f, 10.7f), new Vector2(10.6f, 21.6f), new Vector2(6f, 21.6f) };
            var east = new List<Vector2> { new Vector2(15.8f, 10.6f), new Vector2(19.6f, 10.5f), new Vector2(20.2f, 21.6f), new Vector2(15.6f, 21.6f) };
            foreach (List<Vector2> stone in new[] { cap, west, east }) body.EraseOutline(stone, Pen * 0.5f);
            body.Scrape(3.6f, 21.8f, 22.4f, 21.8f, Pen);
            foreach (List<Vector2> stone in new[] { west, east, cap })
            {
                ink.EraseOutline(stone, 0f);
                Loop(ink, stone, Pen);
            }
            ink.Stroke(3.6f, 21.8f, 22.4f, 21.8f, Pen);
            for (float x = 17.6f; x < 19.6f; x += 1.5f) ink.Stroke(x, 12f, x + 0.3f, 20.4f, FinePen);
            for (float x = 8.6f; x < 10.2f; x += 1.5f) ink.Stroke(x, 12f, x + 0.2f, 20.4f, FinePen);
        }

        // Where a player died, their grave: a Norse memorial stone (bautasten) on a low mound,
        // carved with the yr rune, the Younger Futhark's rune of the yew and of death, not a
        // cross, which has no place in a Norse afterlife.
        private static void Death(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> stone = Curve(new[]
            {
                new Vector2(7.8f, 20.6f), new Vector2(7.6f, 11.6f), new Vector2(9.6f, 5.4f), new Vector2(13.4f, 3.2f),
                new Vector2(17f, 5.8f), new Vector2(18.6f, 12f), new Vector2(18.2f, 20.6f),
            }, closed: false);
            List<Vector2> mound = Curve(new[] { new Vector2(4.2f, 22f), new Vector2(8.6f, 19.6f), new Vector2(17.4f, 19.6f), new Vector2(21.8f, 22f) }, closed: false);
            body.EraseOutline(stone, Pen * 0.5f);
            body.EraseOutline(mound, Pen * 0.5f);
            ink.EraseOutline(stone, 0f);
            Loop(ink, stone, Pen);
            ink.Polyline(mound, Pen);
            ink.Stroke(4.2f, 22f, 21.8f, 22f, FinePen);
            ink.Stroke(13f, 7f, 13f, 15.4f, RunePen);
            ink.Stroke(13f, 11.2f, 10.6f, 14.9f, RunePen);
            ink.Stroke(13f, 11.2f, 15.4f, 14.9f, RunePen);
        }

        // The game's own pins, each a plain map sign for what the place is rather than a copy of
        // the game's icon.

        // The player's bed (their spawn point): a bed seen from the side, headboard to the west.
        private static void Bed(InkCanvas ink, InkCanvas body)
        {
            var outline = new List<Vector2>
            {
                new Vector2(4.4f, 7.6f), new Vector2(7f, 7.6f), new Vector2(7f, 12.6f), new Vector2(21.6f, 12.6f),
                new Vector2(21.6f, 21.6f), new Vector2(4.4f, 21.6f),
            };
            body.EraseOutline(outline, Pen * 0.5f);
            // Headboard, the mattress as one block, and the foot post: nothing more, to keep it
            // simple.
            ink.Stroke(5.6f, 7.6f, 5.6f, 21.6f, Pen);
            ink.Stroke(20.4f, 12.6f, 20.4f, 21.6f, Pen);
            ink.EraseOutline(new[] { new Vector2(5.6f, 13.2f), new Vector2(20.4f, 13.2f), new Vector2(20.4f, 17.6f), new Vector2(5.6f, 17.6f) }, 0f);
            Loop(ink, new[] { new Vector2(5.6f, 13.2f), new Vector2(20.4f, 13.2f), new Vector2(20.4f, 17.6f), new Vector2(5.6f, 17.6f) }, Pen);
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
            ink.Scrape(11.8f, 19f, 11.8f, 21.6f, 0.7f);
            ink.Scrape(14.2f, 19f, 14.2f, 21.6f, 0.7f);
        }

        // Hildir's first request, the Smouldering Tombs: a barrow with a dark doorway.
        private static void Tomb(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> mound = Curve(new[] { new Vector2(2.8f, 21.4f), new Vector2(7f, 12.6f), new Vector2(13f, 9.2f), new Vector2(19f, 12.6f), new Vector2(23.2f, 21.4f) }, closed: false);
            body.EraseOutline(mound, Pen * 0.5f);
            ink.Polyline(mound, Pen);
            ink.Stroke(2.8f, 21.4f, 23.2f, 21.4f, Pen);
            FillSolid(ink, new[] { new Vector2(10.8f, 21.4f), new Vector2(10.8f, 16.6f), new Vector2(13f, 15f), new Vector2(15.2f, 16.6f), new Vector2(15.2f, 21.4f) });
            ink.Stroke(9.6f, 15.6f, 16.4f, 15.6f, Pen);
            for (float x = 17.4f; x < 22f; x += 1.9f) ink.Stroke(x, 13.4f + (x - 17.4f) * 1.1f, x, 20.6f, FinePen);
        }

        // Hildir's second, the Howling Caverns: a rocky height with a cave mouth.
        private static void Cavern(InkCanvas ink, InkCanvas body)
        {
            var outline = new List<Vector2>
            {
                new Vector2(2.6f, 21.6f), new Vector2(6f, 13.4f), new Vector2(9.2f, 11.4f), new Vector2(12.4f, 5.4f),
                new Vector2(15.2f, 9.2f), new Vector2(18.2f, 7.8f), new Vector2(21f, 13.6f), new Vector2(23.4f, 21.6f),
            };
            body.EraseOutline(outline, Pen * 0.5f);
            ink.EraseOutline(outline, 0f);
            Loop(ink, outline, Pen);
            List<Vector2> mouth = Curve(new[] { new Vector2(8.8f, 21.6f), new Vector2(9.6f, 16.8f), new Vector2(13f, 14.6f), new Vector2(16.4f, 16.8f), new Vector2(17.2f, 21.6f) }, closed: false);
            FillSolid(ink, mouth);
            for (float x = 17.8f; x < 22.6f; x += 1.9f) ink.Stroke(x, 10.6f + (x - 17.8f) * 1.4f, x + 0.4f, 20.8f, FinePen);
        }

        // Hildir's third, the Sealed Towers: a plain flat-topped tower with a window slit and a
        // dark door; battlements are too fine to read at a pin's size.
        private static void Tower(InkCanvas ink, InkCanvas body)
        {
            var outline = new List<Vector2>
            {
                new Vector2(8.2f, 21.8f), new Vector2(8.8f, 4.4f), new Vector2(17.2f, 4.4f), new Vector2(17.8f, 21.8f),
            };
            body.EraseOutline(outline, Pen * 0.5f);
            ink.EraseOutline(outline, 0f);
            Loop(ink, outline, Pen);
            ink.Stroke(13f, 8.6f, 13f, 11.6f, Pen);
            FillSolid(ink, Curve(new[] { new Vector2(11.4f, 21.8f), new Vector2(11.4f, 18f), new Vector2(13f, 16.4f), new Vector2(14.6f, 18f), new Vector2(14.6f, 21.8f) }, closed: false));
        }

        // The Sacrificial Stones, where every Viking wakes: a ring of standing stones round a low
        // altar in the map's oblique view, as its peaks and trees are drawn. Each stone is a block
        // with a lit front, a solid east face and a thin top, drawn from the back of the ring to
        // the front, each scraping what lies behind it. Its place is the ring's middle.
        private const float StonesCentreY = 15f;
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
            // The stone itself takes the rock's wash, not the ground's (PinMark.m_rock).
            if (s_rock != null)
            {
                s_rock.Fill(new[] { bottomLeft, bottomRight, topRight, topLeft });
                s_rock.Fill(new[] { bottomRight, bottomRight + back, topRight + back, topRight });
                s_rock.Fill(new[] { topLeft, topRight, topRight + back, topLeft + back });
            }
            ink.EraseOutline(outline, 0f);
            ink.Fill(new[] { bottomRight, bottomRight + back, topRight + back, topRight });
            Loop(ink, outline, Pen);
            ink.Stroke(topLeft.x, topLeft.y, topRight.x, topRight.y, Pen);
            ink.Stroke(topRight.x, topRight.y, bottomRight.x, bottomRight.y, Pen);
        }

        // Haldor the trader: a plain tied bag.
        private static void Trader(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> sack = Curve(new[]
            {
                new Vector2(13f, 8.6f), new Vector2(17.4f, 10.4f), new Vector2(20.6f, 16f), new Vector2(18.8f, 21.2f),
                new Vector2(13f, 22.6f), new Vector2(7.2f, 21.2f), new Vector2(5.4f, 16f), new Vector2(8.6f, 10.4f),
            }, closed: true);
            // The cloth gathered above the tie, flaring out.
            var tuft = new List<Vector2> { new Vector2(9.6f, 4f), new Vector2(16.4f, 4f), new Vector2(14.2f, 8.8f), new Vector2(11.8f, 8.8f) };
            body.EraseOutline(sack, Pen * 0.5f);
            body.EraseOutline(tuft, Pen * 0.5f);
            ink.EraseOutline(sack, 0f);
            Loop(ink, sack, Pen);
            ink.EraseOutline(tuft, 0f);
            Loop(ink, tuft, Pen);
            ink.Stroke(10.2f, 9.4f, 15.8f, 9.4f, Pen);
        }

        // Hildir's camp: a tent with a pennant on its pole and a dark door.
        private static void Tent(InkCanvas ink, InkCanvas body)
        {
            var tent = new List<Vector2> { new Vector2(3.2f, 21.6f), new Vector2(13f, 6.4f), new Vector2(22.8f, 21.6f) };
            body.EraseOutline(tent, Pen * 0.5f);
            Loop(ink, tent, Pen);
            ink.Stroke(13f, 6.4f, 13f, 2f, FinePen * 1.3f);
            FillSolid(ink, new[] { new Vector2(13f, 2f), new Vector2(17.8f, 3.3f), new Vector2(13f, 4.6f) });
            FillSolid(ink, new[] { new Vector2(13f, 12.4f), new Vector2(16.2f, 21.6f), new Vector2(9.8f, 21.6f) });
            for (float x = 17.2f; x < 21.6f; x += 1.8f) ink.Stroke(x, 6.4f + (x - 13f) * 1.55f + 0.8f, x, 21f, FinePen);
        }

        // The Bog Witch: a cauldron on short legs with bubbles over its rim.
        private static void Cauldron(InkCanvas ink, InkCanvas body)
        {
            List<Vector2> pot = Curve(new[]
            {
                new Vector2(4.8f, 11.4f), new Vector2(5.6f, 16.6f), new Vector2(9f, 20.6f), new Vector2(13f, 21.4f),
                new Vector2(17f, 20.6f), new Vector2(20.4f, 16.6f), new Vector2(21.2f, 11.4f),
            }, closed: false);
            body.EraseOutline(pot, Pen * 0.5f);
            ink.EraseOutline(pot, 0f);
            ink.Polyline(pot, Pen);
            ink.Stroke(3.8f, 11.4f, 22.2f, 11.4f, Pen);
            ink.Stroke(8.6f, 20.2f, 7.2f, 23.4f, Pen);
            ink.Stroke(17.4f, 20.2f, 18.8f, 23.4f, Pen);
            ink.Ring(10.4f, 8f, 1.3f, 1.3f, FinePen);
            ink.Ring(14.2f, 5.4f, 1.1f, 1.1f, FinePen);
            ink.Ring(15.8f, 8.8f, 0.8f, 0.8f, FinePen);
            for (float x = 14.6f; x < 20f; x += 1.9f) ink.Stroke(x, 12.6f, x, 20f - (x - 14.6f) * 0.5f, FinePen);
        }

        // The Forge of Potential, whose map icon the game draws after its ward: a standing stone on
        // a low plinth under a wider cap, a gem set on top, its eastern face hatched as the map
        // shades every glyph's eastern side.
        private static void Forge(InkCanvas ink, InkCanvas body)
        {
            var plinth = new List<Vector2> { new Vector2(6.8f, 19.4f), new Vector2(19.2f, 19.4f), new Vector2(19.2f, 22f), new Vector2(6.8f, 22f) };
            var pillar = new List<Vector2> { new Vector2(9.6f, 19.4f), new Vector2(10.6f, 8.4f), new Vector2(15.4f, 8.4f), new Vector2(16.4f, 19.4f) };
            var cap = new List<Vector2> { new Vector2(8.4f, 6.6f), new Vector2(17.6f, 6.6f), new Vector2(17.6f, 8.4f), new Vector2(8.4f, 8.4f) };
            var gem = new List<Vector2> { new Vector2(13f, 1.8f), new Vector2(15f, 4.3f), new Vector2(13f, 6.6f), new Vector2(11f, 4.3f) };
            foreach (List<Vector2> part in new[] { plinth, pillar, cap, gem }) body.EraseOutline(part, Pen * 0.5f);
            foreach (List<Vector2> part in new[] { pillar, plinth, cap })
            {
                ink.EraseOutline(part, 0f);
                Loop(ink, part, Pen);
            }
            FillSolid(ink, gem);
            // A rune down the stone's face, and the hatching on its eastern side.
            ink.Stroke(13f, 10.6f, 13f, 16.4f, FinePen);
            ink.Stroke(13f, 12.2f, 11.6f, 13.8f, FinePen);
            ink.Stroke(13f, 12.2f, 14.4f, 13.8f, FinePen);
            for (float x = 15.2f; x < 16.4f; x += 1f) ink.Stroke(x, 9.6f, x + 0.4f, 18.6f, FinePen);
        }

        // A checked pin is struck through with two pen strokes, drawn over its glyph.
        private static void Checked(InkCanvas ink)
        {
            ink.Stroke(4.4f, 4.4f, 21.6f, 21.6f, Pen);
            ink.Stroke(21.6f, 4.4f, 4.4f, 21.6f, Pen);
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
