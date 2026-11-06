using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A sheet of ink coverage (0 bare parchment, 255 solid ink) with anti-aliased pens. Callers
    /// draw in sheet coordinates: continuous pixels of the whole 4096 sheet, y growing southward,
    /// pixel (x, y) centred on (x + 0.5, y + 0.5). A canvas may cover only part of the sheet at a
    /// magnification (a detail tile), so the same calls draw the same marks, sharper. Ink only ever
    /// darkens (the darker of old and new wins), and Erase scrapes back to bare parchment, which is
    /// how a glyph hides whatever lies behind it. A recorder, when set, keeps every call so it can
    /// be replayed into a detail tile later.
    /// </summary>
    // What a peak's, spire's or tree's body takes the wash of; each kind but None has its own
    // body mask.
    internal enum BodyKind : byte
    {
        None,
        Mountain,
        North,
        Mist,
        Wood,
        // Not a body: an erase that scrapes only the ink and leaves every body mask as it is
        // (Glyphs.ScrapeBase).
        Keep,
    }

    internal sealed class InkCanvas
    {
        public readonly int m_width;
        public readonly int m_height;
        public readonly byte[] m_ink;
        // Where a peak's body lies (0 to 255), which takes the mountains' wash whatever the
        // ground under it: peaks rise north from their feet over the land beyond a range.
        public readonly byte[] m_body;
        // The same for Deep North peaks, which take the Deep North's wash instead.
        public readonly byte[] m_bodyNorth;
        // The same for the Mistlands' spires, which take their rock's colour (Paper.BodyColor).
        // A plane of its own rather than a kind byte beside one shared mask: where two kinds meet
        // (a spire in front of a peak, a skirt over another body) a pixel holds a share of each,
        // which one mask and one kind could not keep.
        public readonly byte[] m_bodyMist;
        // The same for tree crowns, which take the wood wash of the biome the tree stands in
        // (Paper.WoodTint), and the Ashlands' sea thorns. Each byte packs that biome
        // (WoodCode, top four bits) with the coverage (bottom four bits), since a cached sheet is
        // shown before the world is read and so cannot look the biome up.
        public readonly byte[] m_bodyWood;
        // The pins' own ink, laid in red (Paper.PinInkColor, InkCanvas.Pin): a plane of its own,
        // so pins read apart from the terrain's black ink.
        public readonly byte[] m_pinInk;
        // While not None, an erase marks what it scrapes as that kind of body (and clears the
        // others); any other erase clears all, so the glyph in front decides. Polygon, outline and
        // ellipse erases mark; scraped strokes never do.
        public BodyKind m_markBody;
        // The biome a Wood body is marked with.
        public Land m_woodLand;
        // When set, every erase also keeps its coverage here (the most of any erase), so a glyph's
        // scraped silhouette can be read back: a sea illustration's wash follows it (SeaWash).
        public byte[] m_scraped;
        // While Tint runs: the mask it raises, and its fade (sheet y where it starts, its length).
        private BodyKind m_tintKind;
        private float m_tintTop, m_tintFade, m_tintDrop, m_tintCentre, m_tintHalf;

        private readonly float m_originX;
        private readonly float m_originY;
        private readonly float m_scale;
        private readonly List<Vector2> m_transformed = new List<Vector2>(16);
        private InkRecord m_record;

        // What the calls drawn from now on belong to, kept with them in the record.
        public InkLayer m_layer;
        // Pen and band widths are multiplied by this: a detail tile drawn at a finer level of detail
        // uses finer pens, so lines do not swell with the zoom.
        public float m_penScale = 1f;

        public InkCanvas(int size) : this(size, size, 0f, 0f, 1f)
        {
        }

        // A canvas of width x height pixels showing the sheet from (originX, originY) at scale
        // pixels per sheet pixel.
        public InkCanvas(int width, int height, float originX, float originY, float scale)
        {
            m_width = width;
            m_height = height;
            m_ink = new byte[width * height];
            m_body = new byte[width * height];
            m_bodyNorth = new byte[width * height];
            m_bodyMist = new byte[width * height];
            m_bodyWood = new byte[width * height];
            m_pinInk = new byte[width * height];
            m_originX = originX;
            m_originY = originY;
            m_scale = scale;
        }

        // The body mask a kind marks (None has none).
        public byte[] BodyMask(BodyKind kind)
        {
            switch (kind)
            {
                case BodyKind.Mountain: return m_body;
                case BodyKind.North: return m_bodyNorth;
                case BodyKind.Mist: return m_bodyMist;
                case BodyKind.Wood: return m_bodyWood;
                default: return null;
            }
        }

        // The biomes a Wood body can take, by code (0: none). The Ashlands have no woods: their
        // sea thorns take its wood wash as their rock (Glyphs.AshBody).
        private static readonly Land[] s_woodLands =
        {
            Land.Ocean, Land.Meadows, Land.BlackForest, Land.Swamp, Land.Mountain, Land.Plains, Land.Mistlands, Land.DeepNorth,
            Land.Ashlands,
        };
        // A Wood body byte is the code in its top four bits and the coverage in the bottom four:
        // eight biomes need a fourth code bit, and sixteen levels of a light glaze under the ink
        // do not show.
        private const int WoodShift = 4;
        private const int WoodLevels = 15;
        public const int WoodCodes = 1 << (8 - WoodShift);

        // A biome's code in a Wood body byte, or 0 for a biome whose trees take no wood wash.
        public static int WoodCode(Land land)
        {
            for (int i = 1; i < s_woodLands.Length; i++)
                if (s_woodLands[i] == land) return i;
            return 0;
        }

        public static int WoodCodeOf(byte packed) => packed >> WoodShift;

        public static float WoodCover(byte packed) => (packed & WoodLevels) / (float)WoodLevels;

        public static Land WoodLand(byte packed) => WoodLandOf(WoodCodeOf(packed));

        // The biome of a code, Ocean for none or an unused code.
        public static Land WoodLandOf(int code) => code < s_woodLands.Length ? s_woodLands[code] : Land.Ocean;

        private static byte PackWood(float cover, int code)
        {
            int level = (int)(Mathf.Clamp01(cover) * WoodLevels + 0.5f);
            return level == 0 ? (byte)0 : (byte)((code << WoodShift) | level);
        }

        public void Record(InkRecord record) => m_record = record;

        // Marks the calls from here to EndGroup as one glyph standing at footY, in the record.
        public void BeginGroup(float footY) => m_record?.BeginGroup(footY);

        public void EndGroup() => m_record?.EndGroup();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float X(float x) => (x - m_originX) * m_scale;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private float Y(float y) => (y - m_originY) * m_scale;

        // When set, every point drawn is moved by it first, and straight strokes and outlines are
        // cut into short pieces so they bend with it: the hand-drawn waver of a pin drawn into
        // the sheet (PinGlyphs). Not recorded; only the pins' own scratch canvases use it.
        public Func<Vector2, Vector2> m_warp;
        private const float WarpStep = 1.5f;

        private Vector2 Warp(float x, float y) => m_warp != null ? m_warp(new Vector2(x, y)) : new Vector2(x, y);

        private Vector2 Point(Vector2 p)
        {
            if (m_warp != null) p = m_warp(p);
            return new Vector2(X(p.x), Y(p.y));
        }

        // A straight pen stroke with round ends.
        public void Stroke(float x0, float y0, float x1, float y1, float width, float strength = 1f)
        {
            m_record?.Stroke(m_layer, x0, y0, x1, y1, width, strength, erase: false);
            Pieces(x0, y0, x1, y1, width * m_penScale * m_scale, strength, erase: false);
        }

        // Scrapes a band with round ends back to bare parchment.
        public void Scrape(float x0, float y0, float x1, float y1, float width)
        {
            m_record?.Stroke(m_layer, x0, y0, x1, y1, width, 1f, erase: true);
            Pieces(x0, y0, x1, y1, width * m_penScale * m_scale, 1f, erase: true);
        }

        private void Pieces(float x0, float y0, float x1, float y1, float width, float strength, bool erase)
        {
            if (m_warp == null)
            {
                Capsule(X(x0), Y(y0), X(x1), Y(y1), width, strength, erase);
                return;
            }
            int pieces = Math.Max(1, Mathf.CeilToInt(Mathf.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0)) / WarpStep));
            Vector2 from = Point(new Vector2(x0, y0));
            for (int k = 1; k <= pieces; k++)
            {
                float t = k / (float)pieces;
                Vector2 to = Point(new Vector2(Mathf.Lerp(x0, x1, t), Mathf.Lerp(y0, y1, t)));
                Capsule(from.x, from.y, to.x, to.y, width, strength, erase);
                from = to;
            }
        }

        public void Polyline(IList<Vector2> points, float width, float strength = 1f)
        {
            for (int i = 1; i < points.Count; i++)
                Stroke(points[i - 1].x, points[i - 1].y, points[i].x, points[i].y, width, strength);
        }

        public void Dot(float cx, float cy, float radius, float strength = 1f)
        {
            m_record?.Ellipse(m_layer, cx, cy, radius, radius, strength, erase: false);
            Vector2 c = Point(new Vector2(cx, cy));
            Ellipse(c.x, c.y, radius * m_scale, radius * m_scale, strength, erase: false);
        }

        public void EraseEllipse(float cx, float cy, float rx, float ry)
        {
            m_record?.Ellipse(m_layer, cx, cy, rx, ry, 1f, erase: true, m_markBody, m_woodLand);
            m_erasingPolygon = true;
            try
            {
                Vector2 c = Point(new Vector2(cx, cy));
                Ellipse(c.x, c.y, rx * m_scale, ry * m_scale, 1f, erase: true);
            }
            finally
            {
                m_erasingPolygon = false;
            }
        }

        public void Ring(float cx, float cy, float rx, float ry, float width, float strength = 1f)
        {
            m_record?.Ring(m_layer, cx, cy, rx, ry, width, strength);
            if (m_warp != null)
            {
                // Traced round as pen strokes, so the ring wavers with the rest of the pin.
                int pieces = Math.Max(8, Mathf.CeilToInt(2f * Mathf.PI * Math.Max(rx, ry) / WarpStep));
                for (int k = 0; k < pieces; k++)
                {
                    float a0 = 2f * Mathf.PI * k / pieces, a1 = 2f * Mathf.PI * (k + 1) / pieces;
                    Vector2 p = Point(new Vector2(cx + Mathf.Cos(a0) * rx, cy + Mathf.Sin(a0) * ry));
                    Vector2 q = Point(new Vector2(cx + Mathf.Cos(a1) * rx, cy + Mathf.Sin(a1) * ry));
                    Capsule(p.x, p.y, q.x, q.y, width * m_penScale * m_scale, strength, erase: false);
                }
                return;
            }
            RingPixels(X(cx), Y(cy), rx * m_scale, ry * m_scale, width * m_penScale * m_scale, strength);
        }

        // Convex polygons only, wound either way.
        public void Fill(IList<Vector2> polygon, float strength = 1f)
        {
            m_record?.Polygon(m_layer, polygon, strength, erase: false);
            Polygon(polygon, strength, erase: false);
        }

        // A pin glyph (PinGlyphs) in the box of size sheet pixels from (left, top): its body
        // scrapes the ink and every body mask, so the ground's own wash fills it, then its lines
        // are laid in the pins' red ink (m_pinInk), scraped away in part when checked
        // (PinGlyphs.Abrade). Drawn at this canvas's resolution, so a
        // close-up gets a sharp pin.
        public void Pin(PinGlyph glyph, float left, float top, float size, bool struck)
        {
            m_record?.Pin(m_layer, glyph, left, top, size, struck);
            // A close-up draws its pins smaller by its level of detail, as it draws its trees, so a
            // pin keeps its size beside the trees at every zoom; it stays on its place (its foot,
            // the Stones' centre: PinGlyphs.Lift).
            if (m_penScale != 1f)
            {
                float placeX = left + size * 0.5f, placeY = top + (0.5f + PinGlyphs.Lift(glyph)) * size;
                size *= m_penScale;
                left = placeX - size * 0.5f;
                top = placeY - (0.5f + PinGlyphs.Lift(glyph)) * size;
            }
            int pixels = Mathf.Max(4, Mathf.RoundToInt(size * m_scale));
            // The map's own pen (1 sheet pixel, thinned in close-ups as every line is) in pin
            // units, and a waver seeded by the pin's place, the same on the sheet and in tiles.
            float pen = m_penScale * PinGlyphs.PinUnits / size;
            int seed = Noise.HashInt(Mathf.RoundToInt(left * 4f), Mathf.RoundToInt(top * 4f), 7411) | 1;
            PinMark mark = PinGlyphs.Draw(glyph, pixels, pen, seed);
            if (struck) PinGlyphs.Abrade(mark, pen, seed);
            int x0 = Mathf.RoundToInt(X(left)), y0 = Mathf.RoundToInt(Y(top));
            for (int y = 0; y < pixels; y++)
            {
                int py = y0 + y;
                if (py < 0 || py >= m_height) continue;
                for (int x = 0; x < pixels; x++)
                {
                    int px = x0 + x;
                    if (px < 0 || px >= m_width) continue;
                    int i = py * m_width + px, j = y * pixels + x;
                    float keep = 1f - mark.m_body[j] / 255f;
                    if (keep < 1f)
                    {
                        m_ink[i] = (byte)(m_ink[i] * keep);
                        m_body[i] = (byte)(m_body[i] * keep);
                        m_bodyNorth[i] = (byte)(m_bodyNorth[i] * keep);
                        m_bodyMist[i] = (byte)(m_bodyMist[i] * keep);
                        byte wood = m_bodyWood[i];
                        if (wood != 0) m_bodyWood[i] = PackWood(WoodCover(wood) * keep, WoodCodeOf(wood));
                        if (m_scraped != null) m_scraped[i] = Math.Max(m_scraped[i], mark.m_body[j]);
                        m_pinInk[i] = (byte)(m_pinInk[i] * keep);
                    }
                    byte ink = mark.m_ink[j];
                    if (ink > m_pinInk[i]) m_pinInk[i] = ink;
                }
            }
        }

        public void Erase(IList<Vector2> polygon)
        {
            m_record?.Polygon(m_layer, polygon, 1f, erase: true, body: m_markBody, land: m_woodLand);
            Polygon(polygon, 1f, erase: true);
        }

        // Scrapes a simple polygon, concave or not, back to bare parchment in one pass, its edge
        // pushed out by grow sheet pixels; marks body as Erase does. A concave outline scraped as
        // a fan of convex triangles leaves a faint line along every shared edge: each triangle
        // covers a pixel on it only partly, and the masks keep the larger share while the ink
        // keeps the product of what is left, so neither comes out whole.
        public void EraseOutline(IList<Vector2> outline, float grow)
        {
            m_record?.Outline(m_layer, outline, grow, m_markBody, m_woodLand);
            m_erasingPolygon = true;
            try
            {
                OutlinePixels(outline, grow * m_scale);
            }
            finally
            {
                m_erasingPolygon = false;
                m_rampOn = false;
            }
        }

        // Coverage from the signed distance to the nearest edge segment, inside by the even-odd
        // rule, so it is exact for concave outlines too. Only pixels near an edge need that
        // distance: further in the coverage is full, further out there is none. So each row
        // counts its sorted edge crossings for inside, and a pixel measures only the edges filed
        // in its cell of a grid (each edge in every cell its box, grown by the band, touches).
        // Testing every edge at every pixel of the box took a long name's banner 0.35 s and more
        // (NameBanner); this takes about 10 ms. The arithmetic per edge and pixel must stay as it
        // is (no reordering, no inverse lengths): the sheet is cached, so a pixel that comes out a
        // bit different needs an InkCache.Version bump.
        private void OutlinePixels(IList<Vector2> sheetPolygon, float grow)
        {
            int count = sheetPolygon.Count;
            if (count < 3) return;
            m_transformed.Clear();
            for (int i = 0; i < count; i++)
            {
                Vector2 a = sheetPolygon[i];
                if (m_warp == null)
                {
                    m_transformed.Add(new Vector2(X(a.x), Y(a.y)));
                    continue;
                }
                // Cut into short pieces, so the outline bends with the warp as the strokes do.
                Vector2 b = sheetPolygon[(i + 1) % count];
                int pieces = Math.Max(1, Mathf.CeilToInt(Vector2.Distance(a, b) / WarpStep));
                for (int k = 0; k < pieces; k++) m_transformed.Add(Point(Vector2.Lerp(a, b, k / (float)pieces)));
            }
            count = m_transformed.Count;
            List<Vector2> polygon = m_transformed;
            float minXf = float.MaxValue, maxXf = float.MinValue, minYf = float.MaxValue, maxYf = float.MinValue;
            foreach (Vector2 p in polygon)
            {
                minXf = Math.Min(minXf, p.x);
                maxXf = Math.Max(maxXf, p.x);
                minYf = Math.Min(minYf, p.y);
                maxYf = Math.Max(maxYf, p.y);
            }
            float reach = Math.Max(grow, 0f) + 1f;
            int minX = Math.Max(0, (int)(minXf - reach)), maxX = Math.Min(m_width - 1, (int)(maxXf + reach));
            int minY = Math.Max(0, (int)(minYf - reach)), maxY = Math.Min(m_height - 1, (int)(maxYf + reach));
            if (minX > maxX || minY > maxY) return;
            LoadEdges(polygon);
            // Beyond this distance from every edge a pixel's coverage is at least 1 inside and at
            // most 0 outside, so its exact distance changes nothing (OutlineSlack keeps rounding
            // well clear of that).
            float band = Math.Abs(grow) + 0.5f + OutlineSlack;
            int cellsX = (maxX - minX) / OutlineCell + 1, cellsY = (maxY - minY) / OutlineCell + 1;
            FileEdges(count, band, minX, minY, cellsX, cellsY);
            float[] ax = m_edgeAx, ay = m_edgeAy, by = m_edgeBy, ex = m_edgeEx, ey = m_edgeEy, lengthSqr = m_edgeLengthSqr;
            int[] cellStart = m_cellStart, cellEdges = m_cellEdges;
            if (m_crossings.Length < count) m_crossings = new float[count];
            float[] crossings = m_crossings;
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                // The row's crossings, sorted: a pixel is inside when an odd number lie past it.
                int crossed = 0;
                for (int i = 0; i < count; i++)
                {
                    if ((ay[i] > py) == (by[i] > py)) continue;
                    float cross = ax[i] + (py - ay[i]) * ex[i] / ey[i];
                    // px < NaN is false: a NaN crossing never counts.
                    if (!float.IsNaN(cross)) crossings[crossed++] = cross;
                }
                if (crossed > 1) Array.Sort(crossings, 0, crossed);
                int passed = 0;
                int cellRow = (y - minY) / OutlineCell * cellsX;
                int row = y * m_width;
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f;
                    while (passed < crossed && !(px < crossings[passed])) passed++;
                    bool inside = ((crossed - passed) & 1) != 0;
                    int cell = cellRow + (x - minX) / OutlineCell;
                    int from = cellStart[cell], to = cellStart[cell + 1];
                    if (from == to)
                    {
                        if (inside) Lighten(row + x, 1f);
                        continue;
                    }
                    float nearest = float.MaxValue;
                    for (int k = from; k < to; k++)
                    {
                        int i = cellEdges[k];
                        float t = 0f;
                        if (lengthSqr[i] > 0f)
                        {
                            t = ((px - ax[i]) * ex[i] + (py - ay[i]) * ey[i]) / lengthSqr[i];
                            if (t < 0f) t = 0f;
                            else if (t > 1f) t = 1f;
                        }
                        float dx = px - (ax[i] + t * ex[i]), dy = py - (ay[i] + t * ey[i]);
                        float d = dx * dx + dy * dy;
                        // Math.Min's rule, NaN included.
                        if (!(nearest < d) && !float.IsNaN(nearest)) nearest = d;
                    }
                    float distance = Mathf.Sqrt(nearest);
                    Lighten(row + x, (inside ? distance : -distance) + grow + 0.5f);
                }
            }
        }

        // Grid cells of the outline's edge index, in pixels, and how far past the band of partial
        // coverage an edge is still filed.
        private const int OutlineCell = 8;
        private const float OutlineSlack = 2f;

        // A polygon's edges (corner i to corner i + 1, the last closing the loop) as arrays, so the
        // pixel loops read no list and take no modulo.
        private float[] m_edgeAx = new float[16], m_edgeAy = new float[16], m_edgeBx = new float[16], m_edgeBy = new float[16];
        private float[] m_edgeEx = new float[16], m_edgeEy = new float[16], m_edgeLengthSqr = new float[16];
        private float[] m_crossings = new float[16];
        private int[] m_cellStart = new int[16], m_cellEdges = new int[16];

        private void EnsureEdges(int count)
        {
            if (m_edgeAx.Length >= count) return;
            int size = Math.Max(count, m_edgeAx.Length * 2);
            m_edgeAx = new float[size];
            m_edgeAy = new float[size];
            m_edgeBx = new float[size];
            m_edgeBy = new float[size];
            m_edgeEx = new float[size];
            m_edgeEy = new float[size];
            m_edgeLengthSqr = new float[size];
        }

        private void LoadEdges(List<Vector2> polygon)
        {
            int count = polygon.Count;
            EnsureEdges(count);
            for (int i = 0; i < count; i++)
            {
                Vector2 a = polygon[i], b = polygon[i + 1 < count ? i + 1 : 0];
                float ex = b.x - a.x, ey = b.y - a.y;
                m_edgeAx[i] = a.x;
                m_edgeAy[i] = a.y;
                m_edgeBx[i] = b.x;
                m_edgeBy[i] = b.y;
                m_edgeEx[i] = ex;
                m_edgeEy[i] = ey;
                m_edgeLengthSqr[i] = ex * ex + ey * ey;
            }
        }

        // Files each edge in every cell its box grown by band touches (m_cellStart, m_cellEdges,
        // cells row by row from the pixel box's corner), so every edge within band of a pixel is
        // in that pixel's cell. An edge with a NaN or infinite end is filed everywhere, as the old
        // loop measured it everywhere.
        private void FileEdges(int count, float band, int minX, int minY, int cellsX, int cellsY)
        {
            int cells = cellsX * cellsY;
            if (m_cellStart.Length < cells + 1) m_cellStart = new int[Math.Max(cells + 1, m_cellStart.Length * 2)];
            int[] start = m_cellStart;
            Array.Clear(start, 0, cells + 1);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < count; i++)
                {
                    int c0, c1, r0, r1;
                    CellSpan(Math.Min(m_edgeAx[i], m_edgeBx[i]) - band, Math.Max(m_edgeAx[i], m_edgeBx[i]) + band, minX, cellsX, out c0, out c1);
                    CellSpan(Math.Min(m_edgeAy[i], m_edgeBy[i]) - band, Math.Max(m_edgeAy[i], m_edgeBy[i]) + band, minY, cellsY, out r0, out r1);
                    for (int r = r0; r <= r1; r++)
                        for (int c = c0; c <= c1; c++)
                        {
                            int cell = r * cellsX + c;
                            if (pass == 0) start[cell + 1]++;
                            else m_cellEdges[start[cell]++] = i;
                        }
                }
                if (pass == 0)
                {
                    for (int cell = 0; cell < cells; cell++) start[cell + 1] += start[cell];
                    int filed = start[cells];
                    if (m_cellEdges.Length < filed) m_cellEdges = new int[Math.Max(filed, m_cellEdges.Length * 2)];
                }
            }
            // The second pass moved each cell's start on to the next cell's: shift them back.
            for (int cell = cells; cell > 0; cell--) start[cell] = start[cell - 1];
            start[0] = 0;
        }

        // The cells from..to (pixels from origin) covers, empty (first > last) when it lies off the
        // grid; NaN spans the whole grid.
        private static void CellSpan(float from, float to, int origin, int cellCount, out int first, out int last)
        {
            float a = (from - origin) / OutlineCell, b = (to - origin) / OutlineCell;
            first = !(a >= 0f) ? 0 : a >= cellCount ? cellCount : (int)a;
            last = !(b < cellCount) ? cellCount - 1 : b < 0f ? -1 : (int)b;
        }

        // A peak, seen as a cone, meets the ground along the near half of its base ellipse, as
        // wide as the polygon (its bounding box): drop below sheet y = top (the foot line) under
        // its middle and depth above it at its ends. The body is full TintSoft above that arc and
        // fades to nothing at it. With kind Keep this only sets that fade for the next outline
        // erase, which marks the body (EraseOutline): the body is marked faded rather than marked
        // and then lowered, which would also lower the wash of a peak behind. With a body kind it
        // raises that body's mask over the polygon (from just above the foot line down) along the
        // same fade, the skirt under the
        // middle where the arc dips below the foot. Either way it only ever raises a mask.
        public void Tint(IList<Vector2> polygon, BodyKind kind, float top, float depth, float drop)
        {
            m_record?.Tint(m_layer, polygon, kind, top, depth, drop);
            float left = float.MaxValue, right = float.MinValue;
            foreach (Vector2 p in polygon)
            {
                left = Math.Min(left, p.x);
                right = Math.Max(right, p.x);
            }
            m_tintTop = top;
            m_tintFade = Math.Max(depth, 0f);
            m_tintDrop = Math.Max(drop, 0f);
            m_tintCentre = 0.5f * (left + right);
            m_tintHalf = Math.Max(0.5f * (right - left), 1e-3f);
            if (kind == BodyKind.Keep)
            {
                m_rampOn = true;
                return;
            }
            m_tintKind = kind;
            try
            {
                PolygonPixels(polygon, 1f, erase: false);
            }
            finally
            {
                m_tintKind = BodyKind.None;
            }
        }

        // Set by a Keep tint: the next outline erase marks its body faded towards its base arc.
        private bool m_rampOn;
        // How far (sheet pixels) above a cone's base arc its body fades in from the ground's wash,
        // the fade following the arc.
        internal const float TintSoft = 6f;

        // How much of a body is kept at a sheet point: nothing at the base arc and below it, all of
        // it TintSoft above, along a smoothstep.
        private float BaseKeep(int index)
        {
            float sheetX = (index % m_width + 0.5f) / m_scale + m_originX;
            float sheetY = (index / m_width + 0.5f) / m_scale + m_originY;
            float u = Math.Min(1f, Math.Abs(sheetX - m_tintCentre) / m_tintHalf);
            float arc = m_tintTop + m_tintDrop - (m_tintFade + m_tintDrop) * (1f - Mathf.Sqrt(1f - u * u));
            float keep = Mathf.Clamp01((arc - sheetY) / TintSoft);
            return keep * keep * (3f - 2f * keep);
        }

        private void TintPixel(int index, float coverage)
        {
            if (coverage <= 0f) return;
            int value = (int)(Math.Min(coverage, 1f) * BaseKeep(index) * 255f + 0.5f);
            byte[] mark = BodyMask(m_tintKind);
            if (value > mark[index]) mark[index] = (byte)value;
        }

        // How far (pixels) past the reach of a pen's partial coverage a row still evaluates its
        // pixels. The rows' spans are worked out in other arithmetic than the pixels' coverage,
        // so they keep this much clear of where rounding could matter; every pixel left out has
        // a coverage of 0 or less, which leaves it as it is.
        private const float SpanSlack = 1f;

        // True when v is neither NaN nor infinite. Spans are worked out only then: a NaN coverage
        // still changes a pixel (Lighten), so a shape with a NaN or infinite point keeps its
        // whole box.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Finite(float v) => v - v == 0f;

        private void Capsule(float x0, float y0, float x1, float y1, float width, float strength, bool erase)
        {
            float r = width * 0.5f;
            float dx = x1 - x0, dy = y1 - y0;
            float lengthSqr = dx * dx + dy * dy;
            int minX = Math.Max(0, (int)(Math.Min(x0, x1) - r - 1f));
            int maxX = Math.Min(m_width - 1, (int)(Math.Max(x0, x1) + r + 1f));
            int minY = Math.Max(0, (int)(Math.Min(y0, y1) - r - 1f));
            int maxY = Math.Min(m_height - 1, (int)(Math.Max(y0, y1) + r + 1f));
            // Each row evaluates only the pixels within reach of the part of the segment level with
            // it, not the whole box: a long slanting stroke's box is mostly empty. A negative or NaN
            // strength would ink a pixel of negative coverage, so it keeps the box.
            bool span = (erase || strength > 0f) && Finite(x0 + y0 + x1 + y1 + r);
            double reach = r + 0.5f + SpanSlack;
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                int from = minX, to = maxX;
                if (span)
                {
                    double lo, hi;
                    if (dy != 0f)
                    {
                        double t0 = (py - reach - y0) / dy, t1 = (py + reach - y0) / dy;
                        t0 = t0 < 0.0 ? 0.0 : t0 > 1.0 ? 1.0 : t0;
                        t1 = t1 < 0.0 ? 0.0 : t1 > 1.0 ? 1.0 : t1;
                        double xa = x0 + t0 * dx, xb = x0 + t1 * dx;
                        lo = Math.Min(xa, xb);
                        hi = Math.Max(xa, xb);
                    }
                    else
                    {
                        if (Math.Abs(py - (double)y0) > reach) continue;
                        lo = Math.Min(x0, x1);
                        hi = Math.Max(x0, x1);
                    }
                    if (!Clip(lo - reach - 0.5, hi + reach, minX, maxX, out from, out to)) continue;
                }
                int row = y * m_width;
                for (int x = from; x <= to; x++)
                {
                    float px = x + 0.5f;
                    // Mathf.Clamp01 and Mathf.Sqrt written out: Mono calls them per pixel.
                    float t = 0f;
                    if (lengthSqr > 0f)
                    {
                        t = ((px - x0) * dx + (py - y0) * dy) / lengthSqr;
                        if (t < 0f) t = 0f;
                        else if (t > 1f) t = 1f;
                    }
                    float ex = px - (x0 + t * dx), ey = py - (y0 + t * dy);
                    float coverage = r + 0.5f - (float)Math.Sqrt(ex * ex + ey * ey);
                    if (erase) Lighten(row + x, coverage);
                    else Darken(row + x, coverage * strength);
                }
            }
        }

        // The pixels from..to of minX..maxX that cover left..right (pixel indices, a pixel spare
        // on the right); false when none do.
        private static bool Clip(double left, double right, int minX, int maxX, out int from, out int to)
        {
            from = minX;
            to = maxX;
            if (!(left <= maxX) || !(right >= minX)) return false;
            if (left > minX) from = (int)left;
            if (right < maxX) to = Math.Min(maxX, (int)right + 1);
            return from <= to;
        }

        private void RingPixels(float cx, float cy, float rx, float ry, float width, float strength)
        {
            float r = width * 0.5f;
            int minX = Math.Max(0, (int)(cx - rx - r - 1f));
            int maxX = Math.Min(m_width - 1, (int)(cx + rx + r + 1f));
            int minY = Math.Max(0, (int)(cy - ry - r - 1f));
            int maxY = Math.Min(m_height - 1, (int)(cy + ry + r + 1f));
            float scale = Math.Min(rx, ry);
            // Each row evaluates only the pixels near the ring, where the normalised radius lies
            // within (r + 0.5 + SpanSlack) / scale of 1, not the whole box: the frame's rings
            // spanned the whole sheet.
            bool span = strength > 0f && rx > 0f && ry > 0f && Finite(cx + cy + rx + ry + r);
            double band = (r + 0.5f + SpanSlack) / (double)scale;
            double outer = 1.0 + band, inner = 1.0 - band;
            for (int y = minY; y <= maxY; y++)
            {
                float ey = (y + 0.5f - cy) / ry;
                if (!span)
                {
                    RingRow(y, minX, maxX, cx, rx, ey, r, scale, strength);
                    continue;
                }
                double eySqr = (double)ey * ey;
                if (eySqr >= outer * outer) continue;
                double far = Math.Sqrt(outer * outer - eySqr) * rx;
                double near = inner > 0.0 && inner * inner > eySqr ? Math.Sqrt(inner * inner - eySqr) * rx : 0.0;
                // Pixel centres within near of the centre lie inside the ring's band, beyond far
                // outside it.
                int from, to;
                if (near <= 1.0)
                {
                    if (Clip(cx - far - 1.0 - 0.5, cx + far + 1.0, minX, maxX, out from, out to)) RingRow(y, from, to, cx, rx, ey, r, scale, strength);
                    continue;
                }
                int leftEnd = minX - 1;
                if (Clip(cx - far - 1.0 - 0.5, cx - near + 1.0, minX, maxX, out from, out to))
                {
                    RingRow(y, from, to, cx, rx, ey, r, scale, strength);
                    leftEnd = to;
                }
                if (Clip(cx + near - 1.0 - 0.5, cx + far + 1.0, minX, maxX, out from, out to))
                {
                    if (from <= leftEnd) from = leftEnd + 1;
                    if (from <= to) RingRow(y, from, to, cx, rx, ey, r, scale, strength);
                }
            }
        }

        private void RingRow(int y, int from, int to, float cx, float rx, float ey, float r, float scale, float strength)
        {
            int row = y * m_width;
            for (int x = from; x <= to; x++)
            {
                float ex = (x + 0.5f - cx) / rx;
                // Distance to an ellipse, approximated by the normalised radius; exact for circles.
                float distance = Math.Abs((float)Math.Sqrt(ex * ex + ey * ey) - 1f) * scale;
                Darken(row + x, (r + 0.5f - distance) * strength);
            }
        }

        private void Ellipse(float cx, float cy, float rx, float ry, float strength, bool erase)
        {
            int minX = Math.Max(0, (int)(cx - rx - 1f));
            int maxX = Math.Min(m_width - 1, (int)(cx + rx + 1f));
            int minY = Math.Max(0, (int)(cy - ry - 1f));
            int maxY = Math.Min(m_height - 1, (int)(cy + ry + 1f));
            float scale = Math.Min(rx, ry);
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float ex = (x + 0.5f - cx) / rx, ey = (y + 0.5f - cy) / ry;
                    float coverage = (1f - Mathf.Sqrt(ex * ex + ey * ey)) * scale + 0.5f;
                    if (erase) Lighten(y * m_width + x, coverage);
                    else Darken(y * m_width + x, coverage * strength);
                }
            }
        }

        private void Polygon(IList<Vector2> sheetPolygon, float strength, bool erase)
        {
            m_erasingPolygon = erase;
            try
            {
                PolygonPixels(sheetPolygon, strength, erase);
            }
            finally
            {
                m_erasingPolygon = false;
            }
        }

        private void PolygonPixels(IList<Vector2> sheetPolygon, float strength, bool erase)
        {
            int count = sheetPolygon.Count;
            if (count < 3) return;
            m_transformed.Clear();
            // Convex polygons keep their corners only: pieces bent by the warp could break the
            // convexity the fill relies on.
            foreach (Vector2 p in sheetPolygon) m_transformed.Add(Point(p));
            List<Vector2> polygon = m_transformed;
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
            int minX = Math.Max(0, (int)(minXf - 1f)), maxX = Math.Min(m_width - 1, (int)(maxXf + 1f));
            int minY = Math.Max(0, (int)(minYf - 1f)), maxY = Math.Min(m_height - 1, (int)(maxYf + 1f));
            // The edges as arrays with their lengths (in the outline's squared-length array),
            // worked out once rather than per pixel; edges too short to have a direction are left
            // out. The division by the length stays a division: a multiplied inverse rounds
            // differently.
            EnsureEdges(count);
            float[] ax = m_edgeAx, ay = m_edgeAy, ex = m_edgeEx, ey = m_edgeEy, length = m_edgeLengthSqr;
            int edges = 0;
            bool finite = true;
            for (int i = 0; i < count; i++)
            {
                Vector2 a = polygon[i], b = polygon[i + 1 < count ? i + 1 : 0];
                float edgeX = b.x - a.x, edgeY = b.y - a.y;
                float edgeLength = (float)Math.Sqrt(edgeX * edgeX + edgeY * edgeY);
                if (edgeLength < 1e-5f) continue;
                ax[edges] = a.x;
                ay[edges] = a.y;
                ex[edges] = edgeX;
                ey[edges] = edgeY;
                length[edges] = edgeLength;
                finite &= Finite(a.x + a.y + edgeX + edgeY + edgeLength);
                edges++;
            }
            // Each row evaluates only the pixels where no edge line lies more than
            // 0.5 + SpanSlack outside them (an interval: the polygon grown is convex whatever the
            // outline), not the whole box. A negative or NaN strength keeps the box, as for strokes.
            bool span = finite && (erase || m_tintKind != BodyKind.None || strength > 0f);
            double edge = -0.5 - SpanSlack;
            for (int y = minY; y <= maxY; y++)
            {
                float py = y + 0.5f;
                int from = minX, to = maxX;
                if (span)
                {
                    // Along the row an edge's signed distance is d = c + s * px.
                    double lo = double.NegativeInfinity, hi = double.PositiveInfinity;
                    for (int i = 0; i < edges; i++)
                    {
                        double c = winding * ((double)ex[i] * (py - ay[i]) + (double)ey[i] * ax[i]) / length[i];
                        double s = -winding * (double)ey[i] / length[i];
                        if (s > 0.0) lo = Math.Max(lo, (edge - c) / s);
                        else if (s < 0.0) hi = Math.Min(hi, (edge - c) / s);
                        else if (c <= edge) hi = double.NegativeInfinity;
                    }
                    if (!(lo <= hi) || !Clip(lo - 0.5 - 1.0, hi + 1.0, minX, maxX, out from, out to)) continue;
                }
                int row = y * m_width;
                for (int x = from; x <= to; x++)
                {
                    float px = x + 0.5f;
                    // Signed distance to the nearest edge line, positive inside.
                    float inside = float.MaxValue;
                    for (int i = 0; i < edges; i++)
                    {
                        float d = winding * (ex[i] * (py - ay[i]) - ey[i] * (px - ax[i])) / length[i];
                        if (d < inside) inside = d;
                    }
                    float coverage = inside + 0.5f;
                    if (m_tintKind != BodyKind.None) TintPixel(row + x, coverage);
                    else if (erase) Lighten(row + x, coverage);
                    else Darken(row + x, coverage * strength);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Darken(int index, float coverage)
        {
            if (coverage <= 0f) return;
            int value = (int)(Math.Min(coverage, 1f) * 255f + 0.5f);
            if (value > m_ink[index]) m_ink[index] = (byte)value;
        }

        private void Lighten(int index, float coverage)
        {
            if (coverage <= 0f) return;
            float keep = 1f - Math.Min(coverage, 1f);
            BodyKind marking = m_erasingPolygon ? m_markBody : BodyKind.None;
            // A body marked faded towards its base arc takes only its faded share of what lies
            // behind it (other kinds' bodies, woods): cleared in full, the body behind showed a
            // hole of the ground's wash in the shape of the glyph in front.
            float ramp = m_rampOn && marking != BodyKind.None && marking != BodyKind.Keep ? BaseKeep(index) : 1f;
            float behind = 1f - Math.Min(coverage, 1f) * ramp;
            if (m_scraped != null)
            {
                int scraped = (int)(Math.Min(coverage, 1f) * 255f + 0.5f);
                if (scraped > m_scraped[index]) m_scraped[index] = (byte)scraped;
            }
            // A glyph in front scrapes a pin's red ink as it scrapes black ink.
            if (m_pinInk[index] != 0) m_pinInk[index] = (byte)(m_pinInk[index] * keep);
            if (marking == BodyKind.Keep)
            {
                m_ink[index] = coverage >= 1f ? (byte)0 : (byte)(m_ink[index] * (1f - coverage));
                return;
            }
            Fade(m_body, index, behind, marking == BodyKind.Mountain);
            Fade(m_bodyNorth, index, behind, marking == BodyKind.North);
            Fade(m_bodyMist, index, behind, marking == BodyKind.Mist);
            byte wood = m_bodyWood[index];
            if (marking == BodyKind.Wood)
            {
                int code = WoodCode(m_woodLand);
                float cover = Math.Max(WoodCover(wood), Math.Min(coverage, 1f));
                m_bodyWood[index] = code == 0 ? (byte)0 : PackWood(cover, code);
            }
            else if (wood != 0)
            {
                m_bodyWood[index] = PackWood(WoodCover(wood) * behind, WoodCodeOf(wood));
            }
            if (marking != BodyKind.None && marking != BodyKind.Wood)
            {
                int value = (int)(Math.Min(coverage, 1f) * ramp * 255f + 0.5f);
                byte[] mark = BodyMask(marking);
                if (value > mark[index]) mark[index] = (byte)value;
            }
            if (coverage >= 1f)
            {
                m_ink[index] = 0;
                return;
            }
            m_ink[index] = (byte)(m_ink[index] * (1f - coverage));
        }

        // Scrapes a body mask by keep, except the one the erase is marking, which it only raises.
        private static void Fade(byte[] mask, int index, float keep, bool marking)
        {
            if (!marking) mask[index] = (byte)(mask[index] * keep);
        }

        // Set while a polygon, outline or ellipse is erased, so only those erases mark body.
        private bool m_erasingPolygon;
    }
}
