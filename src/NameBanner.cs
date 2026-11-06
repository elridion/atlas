using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The hovered pin's name banner as a period pen drawing: a banderole whose face holds the
    /// name, each end turned back behind it at a diagonal crease, the back of the band showing in
    /// the turn as a small hatched wedge, and a swallowtail tail running out below. It is inked
    /// with the map's own rasterizer and pen (InkCanvas, the sheet pins' outline and inner pens
    /// and hand waver), so it reads as drawn by the hand that drew the pins. The face is the
    /// scraped body of the same outlines the pen traces, so the outline covers the mask's edge.
    /// Pixels is pure math (no Unity objects), so it can be rendered offline; For uploads them.
    /// </summary>
    internal static class NameBanner
    {
        // Texels per canvas unit.
        private const float Texels = 2f;

        // The pen: the outline is as wide as the coast is on screen at the view's zoom (the
        // user's call; the view passes it, AtlasView.ShoreWidth), so the banner is inked in the
        // map's own line at every zoom; the hatching is the map's base pen, the coast's width over
        // ShorePens (InkPainter.CoastWidth, in pens). Widths are rounded to PenQuantum canvas
        // units, so a zoom makes a new pair of textures only every quantum. SheetPen, a sheet
        // pixel where close-ups start (DetailTiles.DetailFrom 1.25), scales the hand below and
        // OutlinePen the corners' rounding, so the shape stays the same at every pen.
        private const float SheetPen = 1.25f;
        private const float OutlinePen = SheetPen * 1.5f;
        private const float ShorePens = InkPainter.CoastWidth;
        private const float PenQuantum = 0.25f;
        private const int PenSteps = 24;

        // A pen width as its step: at least one quantum, at most PenSteps.
        internal static int PenStep(float outline) => Mathf.Clamp(Mathf.RoundToInt(outline / PenQuantum), 1, PenSteps);

        // The pins' hand waver (PinGlyphs.Waver 0.45 pin units over WaverGrain 3.5), for a pin a
        // tree's size (InkPainter.PinSheetSize 16 sheet px in a box of PinUnits 26), in canvas
        // units: one pin unit is 16 / 26 sheet px, SheetPen canvas units each.
        private const float PinUnit = 16f / 26f * SheetPen;
        private const float Waver = 0.45f * PinUnit;
        private const float WaverGrain = 3.5f * PinUnit;
        // The pins' hand trace (PinGlyphs.HandTrace): edges cut into pieces of uneven length,
        // every point nudged a hair off the line, and corners rounded over HandRound of the
        // outline pen, so no edge reads as ruled and no corner as a mitre.
        private const float HandPieceMin = 2f * PinUnit;
        private const float HandPieceMax = 3.6f * PinUnit;
        private const float HandNudge = 0.09f * PinUnit;
        private const float HandRound = 0.45f;

        // The face reaches this far past the strip's ends: the strip already pads the name, so
        // only a little more before the turns (halved from 4, the user's call).
        private const float PadX = 2f;

        // Sizes are rounded up to SizeStep canvas units, so names of about one width share a
        // banner; small, as rounding up widens the padding beside the name.
        private const int SizeStep = 4;
        private static readonly Dictionary<long, Banner> s_banners = new Dictionary<long, Banner>();

        // A banner's two textures: Fill, white with the face as alpha, the mask the bare
        // parchment is laid through; Ink, the pen lines in the sheet's ink.
        internal sealed class Banner
        {
            public Texture2D m_fill;
            public Texture2D m_ink;
        }

        // One gentle swell and one dip along the band; small, since the lettering stays level
        // and a stronger wave would leave the text reading as laid on top.
        private const float WaveAmp = 1.1f;
        private const float PhaseRange = 0.5f;

        // The turns, as shares of the band's height: how far the crease runs back along the
        // face, how far the tail hangs below it, how far the tail runs out past the face and how
        // deep its swallowtail is cut. The back stays well under a fifth of the band's length.
        private const float FoldShare = 0.4f;
        private const float DropShare = 0.32f;
        private const float TailShare = 0.6f;
        private const float NotchShare = 0.3f;
        // The tails droop as they run out, by this share of the band's height at their tips
        // (growing with the square of the distance), so the band flows rather than reading as
        // ruled boxes.
        private const float DroopShare = 0.16f;
        // Each end's proportions vary by this share, so the two ends are no mirror image.
        private const float Vary = 0.12f;
        // The tail a little narrower than the face, as it turns away from the eye.
        private const float TailHeight = 0.86f;

        // Hatch lines in each turn's back: three or four, about this far apart (canvas units),
        // each wandering this share of its spacing off the even step.
        private const int HatchMin = 3;
        private const int HatchMax = 4;
        private const float HatchGap = 3.2f;
        private const float HatchJitter = 0.18f;

        private const int Salt = 0x6A11;

        /// <summary>How far the textures reach past the strip on each side, in canvas units.</summary>
        public static Vector2 Margin(Vector2 strip, float outline)
        {
            float band = strip.y;
            // The width as drawn (For rounds it), so the view's box matches the texture's.
            float edge = PenStep(outline) * PenQuantum * 0.5f + Waver + HandNudge + 1f;
            float x = PadX + band * TailShare * (1f + Vary) + edge;
            float y = WaveAmp + band * (DropShare * (1f + Vary) + DroopShare) + edge;
            return new Vector2(Mathf.Ceil(x), Mathf.Ceil(y));
        }

        // The strip's size for a name's own padded box (canvas units), rounded up to SizeStep.
        public static Vector2 Fit(Vector2 size) =>
            new Vector2(Mathf.Ceil(size.x / SizeStep) * SizeStep, Mathf.Ceil(size.y / SizeStep) * SizeStep);

        // Main thread. The textures for a strip of a Fit size, covering the strip and its Margin,
        // kept for the session; null when they could not be made.
        public static Banner For(Vector2 strip, float outline)
        {
            long key = Key(strip, outline);
            if (s_banners.TryGetValue(key, out Banner cached) && (cached == null || cached.m_fill && cached.m_ink)) return cached;
            Banner made = null;
            try
            {
                var size = new Vector2(Mathf.RoundToInt(strip.x), Mathf.RoundToInt(strip.y));
                Pixels(size, PenStep(outline) * PenQuantum, out Color32[] fill, out Color32[] ink, out int w, out int h);
                made = new Banner { m_fill = Upload(fill, w, h, "Atlas name banner fill"), m_ink = Upload(ink, w, h, "Atlas name banner ink") };
            }
            catch (Exception e)
            {
                Jotunn.Logger.LogWarning($"The atlas could not draw a name banner: {e.Message}");
            }
            s_banners[key] = made;
            return made;
        }

        private static long Key(Vector2 strip, float outline) =>
            ((long)Mathf.RoundToInt(strip.x) << 40) | ((long)Mathf.RoundToInt(strip.y) << 20) | (uint)PenStep(outline);

        private static Texture2D Upload(Color32[] pixels, int w, int h, string name)
        {
            var texture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
            {
                name = name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }

        /// <summary>
        /// Draws the banner for a strip (canvas units, the name's box). Rows run north.
        /// <paramref name="fill"/> is white with the banner's face as hard alpha,
        /// <paramref name="ink"/> the pen lines in <see cref="Paper.InkColor"/> with alpha coverage.
        /// </summary>
        internal static void Pixels(Vector2 strip, float outline, out Color32[] fill, out Color32[] ink, out int w, out int h)
        {
            Vector2 margin = Margin(strip, outline);
            w = Mathf.CeilToInt((strip.x + 2f * margin.x) * Texels);
            h = Mathf.CeilToInt((strip.y + 2f * margin.y) * Texels);
            int seed = Noise.HashInt(Mathf.RoundToInt(strip.x), Mathf.RoundToInt(strip.y), Salt) | 1;

            // The canvas runs south as the sheet does: the banner is drawn with y = -north, from
            // the texture's north-west corner.
            var pen = new InkCanvas(w, h, -margin.x, -(strip.y + margin.y), Texels);
            var body = new InkCanvas(w, h, -margin.x, -(strip.y + margin.y), Texels);
            Func<Vector2, Vector2> waver = p => p + new Vector2(
                (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed) - 0.5f) * 2f * Waver,
                (Noise.Value(p.x / WaverGrain, p.y / WaverGrain, seed + 1) - 0.5f) * 2f * Waver);
            pen.m_warp = waver;
            body.m_warp = waver;
            // As a pin's body: the canvas starts all ink and the banner's outlines are scraped
            // from it, so the face is what got scraped.
            body.Fill(new[]
            {
                new Vector2(-margin.x - 1f, -(strip.y + margin.y) - 1f), new Vector2(strip.x + margin.x + 1f, -(strip.y + margin.y) - 1f),
                new Vector2(strip.x + margin.x + 1f, margin.y + 1f), new Vector2(-margin.x - 1f, margin.y + 1f),
            });

            var band = new Band
            {
                m_start = -PadX,
                m_length = strip.x + 2f * PadX,
                m_phase = (Noise.Unit(seed, 1) - 0.5f) * PhaseRange,
                m_droop = strip.y * DroopShare,
                m_reach = strip.y * TailShare,
            };
            float west = -PadX, east = strip.x + PadX;
            End westEnd = MakeEnd(seed, 10, strip.y, west, -1f);
            End eastEnd = MakeEnd(seed, 20, strip.y, east, 1f);
            Vector2[] face =
            {
                new Vector2(west, 0f), new Vector2(east, 0f), new Vector2(east, strip.y), new Vector2(west, strip.y),
            };

            // Back to front, each part scraping what lies behind it: the tails, the turns' backs,
            // then the face over both.
            Part(pen, body, band, westEnd.m_tail, null, seed + 10, outline);
            Part(pen, body, band, eastEnd.m_tail, null, seed + 20, outline);
            Part(pen, body, band, westEnd.m_back, westEnd.m_hatch, seed + 11, outline);
            Part(pen, body, band, eastEnd.m_back, eastEnd.m_hatch, seed + 21, outline);
            Part(pen, body, band, face, null, seed + 30, outline);

            fill = new Color32[w * h];
            ink = new Color32[w * h];
            Color32 colour = Paper.InkColor;
            for (int y = 0; y < h; y++)
            {
                // Canvas row 0 is north; the textures' row 0 is south.
                int from = (h - 1 - y) * w, to = y * w;
                for (int x = 0; x < w; x++)
                {
                    fill[to + x] = new Color32(255, 255, 255, body.m_ink[from + x] < 128 ? (byte)255 : (byte)0);
                    ink[to + x] = new Color32(colour.r, colour.g, colour.b, pen.m_ink[from + x]);
                }
            }
        }

        // The band's gentle wave along its length, in north-up coordinates.
        private struct Band
        {
            public float m_start;
            public float m_length;
            public float m_phase;
            public float m_droop;
            public float m_reach;

            public float Rise(float x)
            {
                float wave = WaveAmp * Mathf.Sin(2f * Mathf.PI * (x - m_start) / m_length + m_phase);
                float past = Mathf.Max(m_start - x, x - (m_start + m_length), 0f) / m_reach;
                return wave - m_droop * past * past;
            }
        }

        // One end of the band, north-up: the tail behind, the back of the turn and its hatching.
        private struct End
        {
            public Vector2[] m_tail;
            public Vector2[] m_back;
            public List<Vector2[]> m_hatch;
        }

        // side is +1 for the east end, -1 for the west; end is the face's end x, the face running
        // from 0 (bottom) to band (top).
        private static End MakeEnd(int seed, int slot, float band, float end, float side)
        {
            float fold = band * FoldShare * VaryOf(seed, slot);
            float drop = band * DropShare * VaryOf(seed, slot + 1);
            float tail = band * TailShare * VaryOf(seed, slot + 2);
            float notch = band * NotchShare * VaryOf(seed, slot + 3);
            float tailBottom = -drop;
            float tailTop = tailBottom + band * TailHeight;
            float tailMid = (tailTop + tailBottom) * 0.5f;
            float outer = end + side * tail;

            var result = new End();
            result.m_tail = new[]
            {
                new Vector2(end, tailBottom), new Vector2(outer, tailBottom),
                new Vector2(outer - side * notch, tailMid), new Vector2(outer, tailTop),
                new Vector2(end, tailTop),
            };

            // The turn: the crease runs from the face's lower edge, fold back from its end, down
            // to the tail's lower corner; the band's back shows between crease and face.
            var crease = new Vector2(end - side * fold, 0f);
            var corner = new Vector2(end, 0f);
            var foot = new Vector2(end, tailBottom);
            result.m_back = new[] { crease, corner, foot };

            // Hatch lines across the back, parallel to the face's end, from the crease up to the
            // face's lower edge, as the period tones a turned-back band.
            int count = Mathf.Clamp(Mathf.RoundToInt(fold / HatchGap) - 1, HatchMin, HatchMax);
            float spacing = fold / (count + 1);
            result.m_hatch = new List<Vector2[]>();
            for (int k = 1; k <= count; k++)
            {
                float jitter = (Noise.Hash(slot, k, seed) - 0.5f) * 2f * HatchJitter * spacing;
                float x = crease.x + side * (spacing * k + jitter);
                float t = Mathf.Abs(x - crease.x) / fold;
                result.m_hatch.Add(new[] { new Vector2(x, tailBottom * t), new Vector2(x, 0f) });
            }
            return result;
        }

        private static float VaryOf(int seed, int slot) => 1f + (Noise.Unit(seed, slot) * 2f - 1f) * Vary;

        // One part of the banner in the hand: its outline rounded and traced, laid on the band's
        // wave and turned onto the south-running canvas; scraped from the ink behind it and from
        // the body (the face), then outlined, and its inner lines inked with the map's pen.
        private static void Part(InkCanvas pen, InkCanvas body, Band band, Vector2[] corners, List<Vector2[]> inner, int salt, float width)
        {
            List<Vector2> outline = HandTrace(RoundCorners(corners, OutlinePen * HandRound), salt);
            for (int i = 0; i < outline.Count; i++) outline[i] = OnCanvas(outline[i], band);
            body.EraseOutline(outline, 0f);
            pen.EraseOutline(outline, 0f);
            outline.Add(outline[0]);
            pen.Polyline(outline, width);
            if (inner == null) return;
            foreach (Vector2[] line in inner)
                pen.Stroke(OnCanvas(line[0], band).x, OnCanvas(line[0], band).y, OnCanvas(line[1], band).x, OnCanvas(line[1], band).y, width / ShorePens);
        }

        // A north-up point on the wave, as the canvas's south-running point.
        private static Vector2 OnCanvas(Vector2 p, Band band) => new Vector2(p.x, -(p.y + band.Rise(p.x)));

        // Rounds every corner of a closed outline with a quadratic curve from r back along one
        // edge to r along the next (less on short edges), so a tip stays a tip, only blunted, as
        // the pins' RoundCorners does.
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
        // up to HandNudge off the line, as the pins' HandTrace does; the wave and waver then bend
        // the pieces.
        private static List<Vector2> HandTrace(IList<Vector2> outline, int salt)
        {
            var traced = new List<Vector2>();
            int n = outline.Count, k = 0;
            for (int i = 0; i < n; i++)
            {
                Vector2 a = outline[i], b = outline[(i + 1) % n];
                Vector2 d = b - a;
                float len = d.magnitude;
                Vector2 normal = len > 1e-4f ? new Vector2(-d.y, d.x) / len : Vector2.zero;
                traced.Add(a + normal * (Noise.Hash(salt, k++, Salt) - 0.5f) * HandNudge);
                float s = HandPieceMin + (HandPieceMax - HandPieceMin) * Noise.Hash(salt + 7, k, Salt);
                while (s < len - HandPieceMin * 0.5f)
                {
                    traced.Add(a + d * (s / len) + normal * (Noise.Hash(salt, k++, Salt) - 0.5f) * 2f * HandNudge);
                    s += HandPieceMin + (HandPieceMax - HandPieceMin) * Noise.Hash(salt + 7, k, Salt);
                }
            }
            return traced;
        }
    }
}
