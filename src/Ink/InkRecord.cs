using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    // What a pen call drew. Detail tiles draw some layers afresh from the world (the ground
    // fills, trees and coast) and replay only the others from the record.
    internal enum InkLayer : byte
    {
        Background,
        Tree,
        Glyph,
        River,
        Coast,
        Ripple,
        Floe,
        Frame,
        Lava,
        // The sea's marks (SeaGlyphs: the figures and their stand-ins), one group each.
        Illustration,
    }

    // Where a sea mark (a figure or a stand-in) stands, in world metres: the box round its ink.
    // It is drawn only when the box lies wholly in explored land (Paper.SeaMarkSeen), so no
    // monster is shown half explored, on parchment or cut by the fog.
    // The glyph itself (SeaGlyphs kind, the sheet point it was drawn at, mirroring and scale; a
    // stand-in's shape comes from its point) is kept too, so its wash (SeaWash) can be laid
    // again from the cached sheet.
    internal struct SeaMark
    {
        public float m_west, m_east, m_south, m_north;
        public int m_kind;
        public float m_x, m_y, m_scale;
        public bool m_mirror;
    }

    /// <summary>
    /// Every pen call that drew the sheet, in order, with a coarse spatial index, so any part of the
    /// sheet can be inked again at a higher magnification (a detail tile) and come out sharp instead
    /// of magnified pixels. Replaying in the original order keeps erasing and inking exactly as on
    /// the sheet. Built on the painting thread, read-only afterwards, so detail tiles can be drawn
    /// on worker threads.
    /// </summary>
    internal sealed class InkRecord
    {
        // Index cells, in sheet pixels. Calls spanning more than MaxCells cells (the frame rings)
        // go in a short list checked for every region instead.
        private const int CellSize = 64;
        private const int MaxCells = 16;

        private enum Op : byte
        {
            Stroke,
            Scrape,
            Ellipse,
            EraseEllipse,
            Ring,
            Fill,
            Erase,
            Tint,
            EraseOutline,
            Pin,
        }

        // One call: for strokes a..d are the ends, e the width; for ellipses and rings a, b the
        // centre and c, d the radii (e the ring width); for polygons a is the first point's index
        // in m_points and b the point count. f is the ink strength.
        private struct Call
        {
            public Op m_op;
            public InkLayer m_layer;
            // The glyph the call belongs to (an index into m_groupFeet), or -1.
            public int m_group;
            // An erase that marks a peak's, spire's or tree's body (InkCanvas.m_markBody), or the
            // mask a tint raises.
            public BodyKind m_body;
            // The biome a Wood body takes (InkCanvas.m_woodLand).
            public Land m_land;
            public float m_a, m_b, m_c, m_d, m_e, m_f;
        }

        // Each glyph's foot (sheet y), so a detail tile can sort the sheet's glyphs in with the
        // trees it draws itself and draw them all from north to south, as the sheet does.
        private readonly List<float> m_groupFeet = new List<float>(65536);
        private int m_group = -1;

        public void BeginGroup(float footY)
        {
            m_group = m_groupFeet.Count;
            m_groupFeet.Add(footY);
        }

        public void EndGroup() => m_group = -1;

        // The group being recorded (BeginGroup), or -1.
        public int CurrentGroup => m_group;

        // One glyph of the sheet touching a tile: its group, foot and calls, in order.
        public sealed class Group
        {
            public int m_id;
            public float m_foot;
            public readonly List<int> m_calls = new List<int>();
        }

        private readonly List<Call> m_calls = new List<Call>(1 << 20);
        private readonly List<Vector2> m_points = new List<Vector2>(1 << 16);
        private List<int>[] m_cells;
        private readonly List<int> m_wide = new List<int>();
        private int m_cellsAcross;

        public int Count => m_calls.Count;

        public void Stroke(InkLayer layer, float x0, float y0, float x1, float y1, float width, float strength, bool erase) =>
            m_calls.Add(new Call { m_op = erase ? Op.Scrape : Op.Stroke, m_layer = layer, m_group = m_group, m_a = x0, m_b = y0, m_c = x1, m_d = y1, m_e = width, m_f = strength });

        public void Ellipse(InkLayer layer, float cx, float cy, float rx, float ry, float strength, bool erase, BodyKind body = BodyKind.None, Land land = Land.Ocean) =>
            m_calls.Add(new Call { m_op = erase ? Op.EraseEllipse : Op.Ellipse, m_layer = layer, m_group = m_group, m_body = body, m_land = land, m_a = cx, m_b = cy, m_c = rx, m_d = ry, m_f = strength });

        public void Ring(InkLayer layer, float cx, float cy, float rx, float ry, float width, float strength) =>
            m_calls.Add(new Call { m_op = Op.Ring, m_layer = layer, m_group = m_group, m_a = cx, m_b = cy, m_c = rx, m_d = ry, m_e = width, m_f = strength });

        public void Polygon(InkLayer layer, IList<Vector2> polygon, float strength, bool erase, BodyKind body = BodyKind.None, Land land = Land.Ocean)
        {
            m_calls.Add(new Call { m_op = erase ? Op.Erase : Op.Fill, m_layer = layer, m_group = m_group, m_body = body, m_land = land, m_a = m_points.Count, m_b = polygon.Count, m_f = strength });
            for (int i = 0; i < polygon.Count; i++) m_points.Add(polygon[i]);
        }

        // A body tint (InkCanvas.Tint): its fade's top and length in c and d.
        public void Tint(InkLayer layer, IList<Vector2> polygon, BodyKind body, float top, float fade, float strength)
        {
            m_calls.Add(new Call { m_op = Op.Tint, m_layer = layer, m_group = m_group, m_body = body, m_a = m_points.Count, m_b = polygon.Count, m_c = top, m_d = fade, m_f = strength });
            for (int i = 0; i < polygon.Count; i++) m_points.Add(polygon[i]);
        }

        // An outline scraped in one pass (InkCanvas.EraseOutline): how far its edge is pushed out in c.
        public void Outline(InkLayer layer, IList<Vector2> outline, float grow, BodyKind body, Land land)
        {
            m_calls.Add(new Call { m_op = Op.EraseOutline, m_layer = layer, m_group = m_group, m_body = body, m_land = land, m_a = m_points.Count, m_b = outline.Count, m_c = grow });
            for (int i = 0; i < outline.Count; i++) m_points.Add(outline[i]);
        }

        // A pin glyph (InkCanvas.Pin): its box's top left in a, b, its size in c, the glyph in d
        // and whether it is checked (scraped, PinGlyphs.Abrade) in e.
        public void Pin(InkLayer layer, PinGlyph glyph, float left, float top, float size, bool struck) =>
            m_calls.Add(new Call { m_op = Op.Pin, m_layer = layer, m_group = m_group, m_a = left, m_b = top, m_c = size, m_d = (int)glyph, m_e = struck ? 1f : 0f });

        // The marks the fog's edge shows or leaves out whole (Paper.Fog); kept in the disk cache.
        public readonly SheetMarks m_marks = new SheetMarks();

        // The pins the sheet was drawn with, so close-ups keep the same clearings round them.
        public SheetPin[] m_pins = new SheetPin[0];
        // Per pin, in the same order: where it stands in the drawing order and how much of its
        // box the relief in front of it covers (InkPainter.OrderMarks), so close-ups give its
        // trees the same share.
        public float[] m_pinOrders;
        public bool[][] m_pinCovers;

        // Calls visit with the ends and width of every stroke of a layer touching the sheet
        // rectangle (each once). Scrapes are left out.
        public void Strokes(InkLayer layer, float left, float top, float right, float bottom, Action<Vector2, Vector2, float> visit)
        {
            int x0 = Cell(left), x1 = Cell(right), y0 = Cell(top), y1 = Cell(bottom);
            var seen = new HashSet<int>();
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    List<int> cell = m_cells[y * m_cellsAcross + x];
                    if (cell == null) continue;
                    foreach (int i in cell)
                    {
                        Call c = m_calls[i];
                        if (c.m_layer != layer || c.m_op != Op.Stroke || !seen.Add(i)) continue;
                        visit(new Vector2(c.m_a, c.m_b), new Vector2(c.m_c, c.m_d), c.m_e);
                    }
                }
            }
        }

        // One level wave of the sheet: a stroke from (x, y), length long, with its crests, their
        // lift (a lake's lie lower than the sea's) and phase. anchor is +1 when its left end
        // starts at a coast west of it, -1 when its right end does, 0 when it floats, and +2 or
        // -2 for the same off an islet's shore (InkPainter.IsletAnchor). Close-ups draw the waves
        // again from these, moving each anchored one to their own finer coast, since a replayed
        // stroke stands off it.
        // m_index is its place among the sheet's waves, as in m_marks.m_waves, where the fog's
        // edge decides whether it is drawn (Paper.FogEdge.m_waves).
        public struct Wave
        {
            public float m_x, m_y, m_length, m_phase, m_lift;
            public int m_crests;
            public int m_anchor;
            public int m_index;
        }

        private readonly List<Wave> m_waves = new List<Wave>();
        // How far a wave stroke's ink reaches round its line: its crests' lift and a pen.
        private const float WaveReach = 2.5f;

        public void AddWave(Wave wave)
        {
            wave.m_index = m_waves.Count;
            m_waves.Add(wave);
            m_marks.m_waves.Add(new FogMark
            {
                m_box = new Vector4(wave.m_x - 1f, wave.m_y - wave.m_lift - WaveReach, wave.m_x + wave.m_length + 1f, wave.m_y + WaveReach),
                m_order = wave.m_y,
                m_shape = MarkShape.Box,
                m_group = -1,
            });
        }

        // The waves overlapping the sheet rectangle, in the order they were drawn.
        public void Waves(float left, float top, float right, float bottom, List<Wave> into)
        {
            foreach (Wave wave in m_waves)
            {
                if (wave.m_y >= top && wave.m_y <= bottom && wave.m_x <= right && wave.m_x + wave.m_length >= left)
                    into.Add(wave);
            }
        }

        // Every peak and ridge of the sheet: its foot (x, y), width (z) and height (w), so close-ups
        // keep the Mountains' trees off the bases and stand them on the lower slopes as the sheet
        // does (InkPainter.ClearOfPeakBases, SlopeOrder).
        private readonly List<Vector4> m_peaks = new List<Vector4>();

        public void AddPeak(float x, float y, float width, float height) => m_peaks.Add(new Vector4(x, y, width, height));

        // The sheet's sea illustrations, kept with the sheet in the disk cache for the fog.
        public readonly List<SeaMark> m_seaMarks = new List<SeaMark>();
        // Their washes, in the same order, which close-ups sample.
        public readonly List<SeaWash> m_seaWashes = new List<SeaWash>();

        // The sea washes that overlap the sheet rectangle.
        public void SeaWashes(float left, float top, float right, float bottom, List<SeaWash> into)
        {
            foreach (SeaWash wash in m_seaWashes)
                if (wash.Overlaps(left, top, right, bottom)) into.Add(wash);
        }

        // The peaks whose foot lies in the sheet rectangle.
        public void Peaks(float left, float top, float right, float bottom, List<Vector4> into)
        {
            foreach (Vector4 peak in m_peaks)
                if (peak.x >= left && peak.x <= right && peak.y >= top && peak.y <= bottom) into.Add(peak);
        }

        // Every Mistlands mist bank of the sheet: its middle line's centre (x, y) and half length
        // (z), so close-ups keep their scarps off the banks as the sheet does
        // (InkPainter.ClearOfMist).
        private readonly List<Vector3> m_mistBanks = new List<Vector3>();

        public void AddMist(float x, float y, float halfLength) => m_mistBanks.Add(new Vector3(x, y, halfLength));

        // The mist banks whose centre lies in the sheet rectangle.
        public void MistBanks(float left, float top, float right, float bottom, List<Vector3> into)
        {
            foreach (Vector3 bank in m_mistBanks)
                if (bank.x >= left && bank.x <= right && bank.y >= top && bank.y <= bottom) into.Add(bank);
        }

        // The drawn footprint (left, top, right, bottom) of every glyph of the sheet but its trees
        // and mist, so close-ups keep their scarps off the sheet's spires and peaks as the sheet
        // does (InkPainter.ClearOfGlyphs).
        private readonly List<Vector4> m_glyphBoxes = new List<Vector4>();

        public void AddGlyphBox(Vector4 box) => m_glyphBoxes.Add(box);

        // The footprints that overlap the sheet rectangle.
        public void GlyphBoxes(float left, float top, float right, float bottom, List<Vector4> into)
        {
            foreach (Vector4 box in m_glyphBoxes)
                if (box.x <= right && box.z >= left && box.y <= bottom && box.w >= top) into.Add(box);
        }

        // Builds the spatial index once painting is done.
        public void Seal(int sheetSize)
        {
            m_cellsAcross = (sheetSize + CellSize - 1) / CellSize;
            m_cells = new List<int>[m_cellsAcross * m_cellsAcross];
            for (int i = 0; i < m_calls.Count; i++)
            {
                Bounds(m_calls[i], out float minX, out float minY, out float maxX, out float maxY);
                int x0 = Cell(minX), x1 = Cell(maxX), y0 = Cell(minY), y1 = Cell(maxY);
                if ((x1 - x0 + 1) * (y1 - y0 + 1) > MaxCells)
                {
                    m_wide.Add(i);
                    continue;
                }
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = y * m_cellsAcross + x;
                        if (m_cells[cell] == null) m_cells[cell] = new List<int>(32);
                        m_cells[cell].Add(i);
                    }
                }
            }
        }

        // Draws every call touching the sheet rectangle into the canvas, in the original order.
        public void Replay(InkCanvas canvas, float left, float top, float right, float bottom) =>
            Replay(canvas, left, top, right, bottom, ~0, null);

        // As above, only the calls whose layer is in the mask (bit 1 << layer), and of strokes and
        // scrapes only those keepStroke accepts by their ends, when it is given.
        public void Replay(InkCanvas canvas, float left, float top, float right, float bottom, int layers,
            Func<Vector2, Vector2, bool> keepStroke)
        {
            var polygon = new List<Vector2>(16);
            foreach (int i in Picked(left, top, right, bottom))
            {
                Call c = m_calls[i];
                if ((layers & (1 << (int)c.m_layer)) == 0) continue;
                if (keepStroke != null && (c.m_op == Op.Stroke || c.m_op == Op.Scrape)
                    && !keepStroke(new Vector2(c.m_a, c.m_b), new Vector2(c.m_c, c.m_d))) continue;
                Draw(canvas, c, polygon);
            }
        }

        // The glyphs of a layer with any call touching the sheet rectangle, each with all its
        // calls in order, so a tile can draw them in turn with its own glyphs.
        public List<Group> Groups(InkLayer layer, float left, float top, float right, float bottom)
        {
            var byId = new Dictionary<int, Group>();
            var groups = new List<Group>();
            foreach (int i in Picked(left, top, right, bottom))
            {
                Call c = m_calls[i];
                if (c.m_layer != layer || c.m_group < 0 || byId.ContainsKey(c.m_group)) continue;
                var group = new Group { m_id = c.m_group, m_foot = m_groupFeet[c.m_group] };
                // A glyph's calls are consecutive; gather all of them, also those outside the tile.
                int first = i;
                while (first > 0 && m_calls[first - 1].m_group == c.m_group) first--;
                for (int k = first; k < m_calls.Count && m_calls[k].m_group == c.m_group; k++) group.m_calls.Add(k);
                byId[c.m_group] = group;
                groups.Add(group);
            }
            return groups;
        }

        public void Replay(InkCanvas canvas, Group group)
        {
            var polygon = new List<Vector2>(16);
            foreach (int i in group.m_calls) Draw(canvas, m_calls[i], polygon);
        }

        // The calls touching the sheet rectangle, once each, in their original order.
        private List<int> Picked(float left, float top, float right, float bottom)
        {
            var picked = new List<int>(4096);
            int x0 = Cell(left), x1 = Cell(right), y0 = Cell(top), y1 = Cell(bottom);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    List<int> cell = m_cells[y * m_cellsAcross + x];
                    if (cell != null) picked.AddRange(cell);
                }
            }
            foreach (int i in m_wide)
            {
                Bounds(m_calls[i], out float minX, out float minY, out float maxX, out float maxY);
                if (maxX >= left && minX <= right && maxY >= top && minY <= bottom) picked.Add(i);
            }
            picked.Sort();
            // A call in several cells is picked once per cell.
            int kept = 0;
            for (int k = 0; k < picked.Count; k++)
                if (kept == 0 || picked[k] != picked[kept - 1]) picked[kept++] = picked[k];
            picked.RemoveRange(kept, picked.Count - kept);
            return picked;
        }

        private void Draw(InkCanvas canvas, Call c, List<Vector2> polygon)
        {
            switch (c.m_op)
            {
                case Op.Stroke: canvas.Stroke(c.m_a, c.m_b, c.m_c, c.m_d, c.m_e, c.m_f); break;
                case Op.Scrape: canvas.Scrape(c.m_a, c.m_b, c.m_c, c.m_d, c.m_e); break;
                case Op.Ellipse: canvas.Dot(c.m_a, c.m_b, c.m_c, c.m_f); break;
                case Op.EraseEllipse:
                    canvas.m_markBody = c.m_body;
                    canvas.m_woodLand = c.m_land;
                    canvas.EraseEllipse(c.m_a, c.m_b, c.m_c, c.m_d);
                    canvas.m_markBody = BodyKind.None;
                    break;
                case Op.Ring: canvas.Ring(c.m_a, c.m_b, c.m_c, c.m_d, c.m_e, c.m_f); break;
                case Op.Fill:
                case Op.Erase:
                    polygon.Clear();
                    for (int k = 0; k < (int)c.m_b; k++) polygon.Add(m_points[(int)c.m_a + k]);
                    if (c.m_op == Op.Fill) canvas.Fill(polygon, c.m_f);
                    else
                    {
                        canvas.m_markBody = c.m_body;
                        canvas.m_woodLand = c.m_land;
                        canvas.Erase(polygon);
                        canvas.m_markBody = BodyKind.None;
                    }
                    break;
                case Op.Tint:
                    polygon.Clear();
                    for (int k = 0; k < (int)c.m_b; k++) polygon.Add(m_points[(int)c.m_a + k]);
                    canvas.Tint(polygon, c.m_body, c.m_c, c.m_d, c.m_f);
                    break;
                case Op.EraseOutline:
                    polygon.Clear();
                    for (int k = 0; k < (int)c.m_b; k++) polygon.Add(m_points[(int)c.m_a + k]);
                    canvas.m_markBody = c.m_body;
                    canvas.m_woodLand = c.m_land;
                    canvas.EraseOutline(polygon, c.m_c);
                    canvas.m_markBody = BodyKind.None;
                    break;
                case Op.Pin: canvas.Pin((PinGlyph)(int)c.m_d, c.m_a, c.m_b, c.m_c, c.m_e > 0f); break;
            }
        }

        private int Cell(float coordinate) => Mathf.Clamp((int)(coordinate / CellSize), 0, m_cellsAcross - 1);

        // The sheet area a call can touch, with a pixel's margin for anti-aliasing.
        private void Bounds(Call c, out float minX, out float minY, out float maxX, out float maxY)
        {
            switch (c.m_op)
            {
                case Op.Stroke:
                case Op.Scrape:
                {
                    float r = c.m_e * 0.5f + 1f;
                    minX = Math.Min(c.m_a, c.m_c) - r;
                    maxX = Math.Max(c.m_a, c.m_c) + r;
                    minY = Math.Min(c.m_b, c.m_d) - r;
                    maxY = Math.Max(c.m_b, c.m_d) + r;
                    return;
                }
                case Op.Fill:
                case Op.Erase:
                case Op.Tint:
                case Op.EraseOutline:
                {
                    minX = minY = float.MaxValue;
                    maxX = maxY = float.MinValue;
                    for (int k = 0; k < (int)c.m_b; k++)
                    {
                        Vector2 p = m_points[(int)c.m_a + k];
                        minX = Math.Min(minX, p.x);
                        maxX = Math.Max(maxX, p.x);
                        minY = Math.Min(minY, p.y);
                        maxY = Math.Max(maxY, p.y);
                    }
                    float margin = 1f + (c.m_op == Op.EraseOutline ? Math.Max(c.m_c, 0f) : 0f);
                    minX -= margin;
                    minY -= margin;
                    maxX += margin;
                    maxY += margin;
                    return;
                }
                case Op.Pin:
                    minX = c.m_a - 1f;
                    minY = c.m_b - 1f;
                    maxX = c.m_a + c.m_c + 1f;
                    maxY = c.m_b + c.m_c + 1f;
                    return;
                default:
                {
                    float grow = c.m_e * 0.5f + 1f;
                    minX = c.m_a - c.m_c - grow;
                    maxX = c.m_a + c.m_c + grow;
                    minY = c.m_b - c.m_d - grow;
                    maxY = c.m_b + c.m_d + grow;
                    return;
                }
            }
        }
    }
}
