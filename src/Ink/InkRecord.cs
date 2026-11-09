using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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
        internal const int CellSize = 64;
        private const int MaxCells = 16;
        // How far past a call's Bounds the clipped replay and the cell hashes reach: Bounds keep a
        // pixel for anti-aliasing, and a stretched ellipse's coverage can reach a little further.
        private const float ReplaySlack = 2f;

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

        // Not readonly only so WithChecks can give its copy a list of its own.
        private List<Call> m_calls = new List<Call>(1 << 20);
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
        // Not readonly only so WithChecks can give its copy marks of its own.
        public SheetMarks m_marks = new SheetMarks();

        // The pins the sheet was drawn with, so close-ups keep the same clearings round them.
        public SheetPin[] m_pins = new SheetPin[0];
        // Per pin, in the same order: where it stands in the drawing order and how much of its
        // box the relief in front of it covers (InkPainter.OrderMarks), so close-ups give its
        // trees the same share.
        public float[] m_pinOrders;
        public bool[][] m_pinCovers;

        // The pin call drawing glyph in the box (left, top, size) exactly as recorded, or -1 when
        // there is none or more than one (two pins on one place, which InkPainter.Recheck leaves
        // to a full paint).
        public int PinCall(PinGlyph glyph, float left, float top, float size)
        {
            int found = -1;
            foreach (int i in Picked(left, top, left + size, top + size))
            {
                Call c = m_calls[i];
                if (c.m_op != Op.Pin || (int)c.m_d != (int)glyph || c.m_a != left || c.m_b != top || c.m_c != size) continue;
                if (found >= 0) return -1;
                found = i;
            }
            return found;
        }

        // A copy of this record for the same pins with only their ticks changed (InkPainter.Recheck):
        // the pin calls at calls drawn struck or not as struck says, m_pins the new list and the
        // marks keyed by it. Everything else is shared, as a record is read-only once sealed
        // (tiles still drawing from this one are not disturbed): only the call list and the marks
        // are copied, a few tens of MB, against a paint of the whole world.
        public InkRecord WithChecks(SheetPin[] pins, IList<int> calls, IList<bool> struck, long key)
        {
            var copy = (InkRecord)MemberwiseClone();
            copy.m_calls = new List<Call>(m_calls);
            for (int k = 0; k < calls.Count; k++)
            {
                Call c = copy.m_calls[calls[k]];
                c.m_e = struck[k] ? 1f : 0f;
                copy.m_calls[calls[k]] = c;
            }
            copy.m_pins = (SheetPin[])pins.Clone();
            // The ticked pins' calls changed, so their cells hash differently.
            copy.m_hashLock = new object();
            copy.m_cellHashes = null;
            copy.m_fades = null;
            copy.m_tileHashes = null;
            copy.m_riverHashes = null;
            // The fog's marks do not change with a tick (pins are not among them), only their key.
            copy.m_marks = new SheetMarks { m_key = key };
            copy.m_marks.m_trees.AddRange(m_marks.m_trees);
            copy.m_marks.m_glyphs.AddRange(m_marks.m_glyphs);
            copy.m_marks.m_waves.AddRange(m_marks.m_waves);
            return copy;
        }

        /// <summary>
        /// What a paint recorded up to the end of its pin-free stages (InkPainter's Startwork), so
        /// a later paint of the same world can start from it: the calls and points so far and the
        /// peaks and mist banks. Taken while no group is open and before any wave, glyph box, sea
        /// mark or fog mark is recorded, so nothing else needs keeping.
        /// </summary>
        internal sealed class Start
        {
            private Call[] m_calls;
            private Vector2[] m_points;
            private float[] m_groupFeet;
            public Vector4[] m_peaks;
            public Vector3[] m_mistBanks;

            public Start(InkRecord from)
            {
                m_calls = from.m_calls.ToArray();
                m_points = from.m_points.ToArray();
                m_groupFeet = from.m_groupFeet.ToArray();
                m_peaks = from.m_peaks.ToArray();
                m_mistBanks = from.m_mistBanks.ToArray();
            }

            // The calls kept, which every paint started from this begins with.
            public int Count => m_calls.Length;

            // Only into a fresh record, before anything is recorded.
            public void CopyInto(InkRecord to)
            {
                to.m_calls.AddRange(m_calls);
                to.m_points.AddRange(m_points);
                to.m_groupFeet.AddRange(m_groupFeet);
                to.m_peaks.AddRange(m_peaks);
                to.m_mistBanks.AddRange(m_mistBanks);
            }
        }

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
        // A record-only paint (InkPainter.Repaint) also seals what it has so far before the sea's
        // illustrations, which test for ink near them (InkIn); the calls after that are scanned
        // in full until the final seal indexes everything again.
        public void Seal(int sheetSize)
        {
            m_cellsAcross = (sheetSize + CellSize - 1) / CellSize;
            m_cells = new List<int>[m_cellsAcross * m_cellsAcross];
            m_wide.Clear();
            m_sealed = m_calls.Count;
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
            InkLayer was = canvas.m_layer;
            foreach (int i in Picked(left, top, right, bottom))
            {
                Call c = m_calls[i];
                if ((layers & (1 << (int)c.m_layer)) == 0) continue;
                if (keepStroke != null && (c.m_op == Op.Stroke || c.m_op == Op.Scrape)
                    && !keepStroke(new Vector2(c.m_a, c.m_b), new Vector2(c.m_c, c.m_d))) continue;
                // Each call on its own layer, as the sheet drew it: the canvas holds the later
                // layers' ink off the pins by it (InkCanvas.Shield).
                canvas.m_layer = c.m_layer;
                Draw(canvas, c, polygon);
            }
            canvas.m_layer = was;
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
            InkLayer was = canvas.m_layer;
            foreach (int i in group.m_calls)
            {
                canvas.m_layer = m_calls[i].m_layer;
                Draw(canvas, m_calls[i], polygon);
            }
            canvas.m_layer = was;
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

        // The calls indexed by the last Seal (m_cells and m_wide hold no later ones).
        private int m_sealed;
        // Per index cell, an order-sensitive hash of every call that can touch the cell's pixels
        // (CellHashes), and per body-marking erase drawn under a base fade the tint that set it.
        private ulong[] m_cellHashes;
        private Dictionary<int, int> m_fades;
        // Not readonly only so WithChecks can give its copy a lock of its own.
        private object m_hashLock = new object();

        public int CellsAcross => m_cellsAcross;

        // Per index cell (CellSize, rows of CellsAcross), a hash of the calls that can touch its
        // pixels, in order, with what they draw (group ids left out: they shift as glyphs are
        // added or left out elsewhere). Two sealed records whose hashes agree on a cell ink it
        // alike, so a repaint for other pins (InkPainter.Repaint) draws again only the cells that
        // differ. An erase that marks a body under a base fade also hashes the tint that set the
        // fade, as that tint may lie outside the cell. Worked out once per record; the record must
        // be sealed and is not changed after.
        public ulong[] CellHashes()
        {
            lock (m_hashLock)
            {
                if (m_cellHashes != null) return m_cellHashes;
                var hashes = new ulong[m_cellsAcross * m_cellsAcross];
                var fades = new Dictionary<int, int>();
                int tint = -1;
                bool ramp = false;
                for (int i = 0; i < m_calls.Count; i++)
                {
                    Call c = m_calls[i];
                    ulong h = CallHash(c);
                    if (MarksBody(c) && ramp)
                    {
                        fades[i] = tint;
                        h = Mix(h ^ (CallHash(m_calls[tint]) * 0xC2B2AE3D27D4EB4FUL));
                    }
                    if (c.m_op == Op.Tint)
                    {
                        tint = i;
                        if (c.m_body == BodyKind.Keep) ramp = true;
                    }
                    else if (c.m_op == Op.EraseOutline)
                    {
                        ramp = false;
                    }
                    Bounds(c, out float minX, out float minY, out float maxX, out float maxY);
                    int x0 = Cell(minX - ReplaySlack), x1 = Cell(maxX + ReplaySlack);
                    int y0 = Cell(minY - ReplaySlack), y1 = Cell(maxY + ReplaySlack);
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            int cell = y * m_cellsAcross + x;
                            hashes[cell] = (hashes[cell] ^ h) * 0x9E3779B97F4A7C15UL + 1UL;
                        }
                    }
                }
                m_fades = fades;
                m_cellHashes = hashes;
                return hashes;
            }
        }

        // The layers close-ups replay from the record (InkPainter.RunTile); they draw the ground
        // fills, trees, coast and waves afresh.
        private const int TileLayers = (1 << (int)InkLayer.Lava) | (1 << (int)InkLayer.River) | (1 << (int)InkLayer.Glyph)
            | (1 << (int)InkLayer.Floe) | (1 << (int)InkLayer.Illustration) | (1 << (int)InkLayer.Frame);
        private ulong[] m_tileHashes, m_riverHashes;

        // Per index cell, as CellHashes, a hash of what a close-up reads from the calls that can
        // touch the cell: the calls of the layers it replays and of every glyph group, in order,
        // each grouped call with its group's foot and whether it starts or ends the group (a
        // close-up sorts the groups by their feet in with its own trees and replays each whole).
        // rivers gets the same of the river strokes alone, which a close-up reads further out
        // (InkPainter.MarkTileRivers). Two sealed records whose hashes agree on the cells a
        // close-up picks give it the same calls, groups and feet there (RecordChange). Worked out
        // once per record.
        public ulong[] TileHashes(out ulong[] rivers)
        {
            lock (m_hashLock)
            {
                if (m_tileHashes == null)
                {
                    var hashes = new ulong[m_cellsAcross * m_cellsAcross];
                    var river = new ulong[hashes.Length];
                    int tint = -1;
                    bool ramp = false;
                    for (int i = 0; i < m_calls.Count; i++)
                    {
                        Call c = m_calls[i];
                        bool read = c.m_group >= 0 || (TileLayers & (1 << (int)c.m_layer)) != 0;
                        bool riverStroke = c.m_layer == InkLayer.River && c.m_op == Op.Stroke;
                        ulong h = 0UL;
                        if (read || riverStroke)
                        {
                            h = CallHash(c);
                            if (MarksBody(c) && ramp) h = Mix(h ^ (CallHash(m_calls[tint]) * 0xC2B2AE3D27D4EB4FUL));
                            if (c.m_group >= 0)
                            {
                                bool first = i == 0 || m_calls[i - 1].m_group != c.m_group;
                                bool last = i + 1 == m_calls.Count || m_calls[i + 1].m_group != c.m_group;
                                h = Mix(h ^ Bits(m_groupFeet[c.m_group], (first ? 1f : 0f) + (last ? 2f : 0f)));
                            }
                        }
                        if (c.m_op == Op.Tint)
                        {
                            tint = i;
                            if (c.m_body == BodyKind.Keep) ramp = true;
                        }
                        else if (c.m_op == Op.EraseOutline)
                        {
                            ramp = false;
                        }
                        if (!read && !riverStroke) continue;
                        Bounds(c, out float minX, out float minY, out float maxX, out float maxY);
                        int x0 = Cell(minX - ReplaySlack), x1 = Cell(maxX + ReplaySlack);
                        int y0 = Cell(minY - ReplaySlack), y1 = Cell(maxY + ReplaySlack);
                        for (int y = y0; y <= y1; y++)
                        {
                            for (int x = x0; x <= x1; x++)
                            {
                                int cell = y * m_cellsAcross + x;
                                if (read) hashes[cell] = (hashes[cell] ^ h) * 0x9E3779B97F4A7C15UL + 1UL;
                                if (riverStroke) river[cell] = (river[cell] ^ h) * 0x9E3779B97F4A7C15UL + 1UL;
                            }
                        }
                    }
                    m_riverHashes = river;
                    m_tileHashes = hashes;
                }
                rivers = m_riverHashes;
                return m_tileHashes;
            }
        }

        // Adds the first call of every group with a call that can touch the index cell (cx, cy),
        // each group once (seen). The record must be sealed.
        public void GroupsNear(int cx, int cy, HashSet<int> seen, List<int> into)
        {
            float left = cx * CellSize, top = cy * CellSize;
            foreach (int i in Picked(left - ReplaySlack, top - ReplaySlack, left + CellSize + ReplaySlack, top + CellSize + ReplaySlack))
            {
                int group = m_calls[i].m_group;
                if (group < 0 || !seen.Add(group)) continue;
                int first = i;
                while (first > 0 && m_calls[first - 1].m_group == group) first--;
                into.Add(first);
            }
        }

        // A hash of the group starting at call first as a close-up replays it: its calls in
        // order, with what they draw, and its foot.
        public ulong GroupHash(int first)
        {
            int group = m_calls[first].m_group;
            ulong h = Mix(Bits(m_groupFeet[group], 0f));
            for (int k = first; k < m_calls.Count && m_calls[k].m_group == group; k++) h = Mix(h ^ CallHash(m_calls[k]));
            return h;
        }

        // Adds the index cells (x, y the first, width and height the count) over which the group
        // starting at call first has calls: a close-up that picks any of them replays all of it.
        public void AddGroupCells(int first, List<RectInt> into)
        {
            int group = m_calls[first].m_group;
            int x0 = int.MaxValue, y0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue;
            for (int k = first; k < m_calls.Count && m_calls[k].m_group == group; k++)
            {
                Bounds(m_calls[k], out float minX, out float minY, out float maxX, out float maxY);
                // A few sea marks' strokes end at no point (NaN), which the index files in the
                // first cell (Cell clamps it); taken in with the rest they spanned the sheet's
                // corner to the mark.
                if (float.IsNaN(minX) || float.IsNaN(minY) || float.IsNaN(maxX) || float.IsNaN(maxY))
                {
                    int nx = Cell(minX), ny = Cell(minY);
                    into.Add(new RectInt(nx, ny, Cell(maxX) - nx + 1, Cell(maxY) - ny + 1));
                    continue;
                }
                x0 = Math.Min(x0, Cell(minX));
                y0 = Math.Min(y0, Cell(minY));
                x1 = Math.Max(x1, Cell(maxX));
                y1 = Math.Max(y1, Cell(maxY));
            }
            if (x1 >= x0) into.Add(new RectInt(x0, y0, x1 - x0 + 1, y1 - y0 + 1));
        }

        // Adds the sheet areas over which a close-up's region would read an entry of this
        // record's lists that from does not have alike (RecordChange): the peaks, mist banks and
        // glyph footprints it takes within its reach (InkPainter.TileGlyphReach, TileMistReach),
        // the waves it draws again (TileWaveReach) and the sea marks' washes it samples. False
        // when they cannot be compared (marks and washes not in step).
        public bool AddChangedLists(InkRecord from, List<Rect> into)
        {
            if (from.m_seaWashes.Count != from.m_seaMarks.Count || m_seaWashes.Count != m_seaMarks.Count) return false;
            float reach = InkPainter.TileGlyphReach, mist = InkPainter.TileMistReach, waves = InkPainter.TileWaveReach;
            // Peaks, banks and footprints are filed for tests, in any order; waves are drawn in
            // order, and so are the marks' washes laid.
            RecordChange.Diff(from.m_peaks.Count, m_peaks.Count, false, (a, b) => from.m_peaks[a].Equals(m_peaks[b]),
                a => from.m_peaks[a].GetHashCode(), b => m_peaks[b].GetHashCode(),
                a => into.Add(Around(from.m_peaks[a].x, from.m_peaks[a].y, reach)), b => into.Add(Around(m_peaks[b].x, m_peaks[b].y, reach)));
            RecordChange.Diff(from.m_mistBanks.Count, m_mistBanks.Count, false, (a, b) => from.m_mistBanks[a].Equals(m_mistBanks[b]),
                a => from.m_mistBanks[a].GetHashCode(), b => m_mistBanks[b].GetHashCode(),
                a => into.Add(Around(from.m_mistBanks[a].x, from.m_mistBanks[a].y, mist)), b => into.Add(Around(m_mistBanks[b].x, m_mistBanks[b].y, mist)));
            RecordChange.Diff(from.m_glyphBoxes.Count, m_glyphBoxes.Count, false, (a, b) => from.m_glyphBoxes[a].Equals(m_glyphBoxes[b]),
                a => from.m_glyphBoxes[a].GetHashCode(), b => m_glyphBoxes[b].GetHashCode(),
                a => into.Add(Grown(from.m_glyphBoxes[a], reach)), b => into.Add(Grown(m_glyphBoxes[b], reach)));
            RecordChange.Diff(from.m_waves.Count, m_waves.Count, true, (a, b) => SameWave(from.m_waves[a], m_waves[b]),
                a => WaveKey(from.m_waves[a]), b => WaveKey(m_waves[b]),
                a => into.Add(WaveArea(from.m_waves[a], waves)), b => into.Add(WaveArea(m_waves[b], waves)));
            // A mark's wash is drawn from the mark alone (SeaWash), so marks alike wash alike.
            RecordChange.Diff(from.m_seaMarks.Count, m_seaMarks.Count, true, (a, b) => SameSeaMark(from.m_seaMarks[a], m_seaMarks[b]),
                a => SeaMarkKey(from.m_seaMarks[a]), b => SeaMarkKey(m_seaMarks[b]),
                a => into.Add(WashArea(from.m_seaWashes[a])), b => into.Add(WashArea(m_seaWashes[b])));
            return true;
        }

        private static Rect Around(float x, float y, float reach) => Rect.MinMaxRect(x - reach, y - reach, x + reach, y + reach);

        private static Rect Grown(Vector4 box, float reach) => Rect.MinMaxRect(box.x - reach, box.y - reach, box.z + reach, box.w + reach);

        // The rows and columns over which a close-up takes a wave (Waves, as DrawTileWaves asks).
        private static Rect WaveArea(Wave wave, float reach) =>
            Rect.MinMaxRect(wave.m_x - reach, wave.m_y - 2f, wave.m_x + wave.m_length + reach, wave.m_y + 2f);

        private static Rect WashArea(SeaWash wash) => Rect.MinMaxRect(wash.Left, wash.Top, wash.Right, wash.Bottom);

        // Waves alike but for their index, which shifts as strokes are added before them.
        private static bool SameWave(Wave a, Wave b) =>
            a.m_x == b.m_x && a.m_y == b.m_y && a.m_length == b.m_length && a.m_phase == b.m_phase && a.m_lift == b.m_lift
            && a.m_crests == b.m_crests && a.m_anchor == b.m_anchor;

        private static int WaveKey(Wave w) => w.m_x.GetHashCode() * 31 + w.m_y.GetHashCode();

        internal static bool SameSeaMark(SeaMark a, SeaMark b) =>
            a.m_west == b.m_west && a.m_east == b.m_east && a.m_south == b.m_south && a.m_north == b.m_north && a.m_kind == b.m_kind
            && a.m_x == b.m_x && a.m_y == b.m_y && a.m_scale == b.m_scale && a.m_mirror == b.m_mirror;

        internal static int SeaMarkKey(SeaMark m) => m.m_x.GetHashCode() * 31 + m.m_y.GetHashCode();

        // Draws onto canvas every call that can touch the sheet pixels [left, right) x [top,
        // bottom), in order, so those pixels come out as a paint of the whole sheet left them:
        // canvas is blank and a window of the sheet (InkCanvas.Window), since a canvas with an
        // origin of its own rounds the shapes differently. Unlike Replay, it restores the base
        // fade each body-marking erase saw when painted, which a tint outside the area may set.
        public void ReplayClipped(InkCanvas canvas, int left, int top, int right, int bottom)
        {
            CellHashes();
            var polygon = new List<Vector2>(16);
            InkLayer was = canvas.m_layer;
            foreach (int i in Picked(left - ReplaySlack, top - ReplaySlack, right + ReplaySlack, bottom + ReplaySlack))
            {
                Call c = m_calls[i];
                if (!Touches(c, left, top, right, bottom)) continue;
                if (MarksBody(c))
                {
                    if (m_fades.TryGetValue(i, out int t))
                    {
                        Call tint = m_calls[t];
                        polygon.Clear();
                        for (int k = 0; k < (int)tint.m_b; k++) polygon.Add(m_points[(int)tint.m_a + k]);
                        canvas.RestoreFade(polygon, tint.m_c, tint.m_d, tint.m_f);
                    }
                    else
                    {
                        canvas.EndFade();
                    }
                }
                canvas.m_layer = c.m_layer;
                Draw(canvas, c, polygon);
            }
            canvas.m_layer = was;
        }

        // Whether any ink lies on the sheet pixels x0..x1, y0..y1 (inclusive) as the calls
        // recorded so far draw it: what a record-only paint (InkPainter.Repaint) reads instead
        // of its canvas. Replayed on a scratch canvas from the index of the last Seal and the
        // calls after it. The ink plane does not depend on the base fade, so none is restored.
        public bool InkIn(int x0, int y0, int x1, int y1)
        {
            int width = x1 + 1 - x0, height = y1 + 1 - y0;
            if (width <= 0 || height <= 0) return false;
            var canvas = InkCanvas.Window(m_cellsAcross * CellSize, x0, y0, width, height);
            List<int> picked = Picked(x0 - ReplaySlack, y0 - ReplaySlack, x1 + 1 + ReplaySlack, y1 + 1 + ReplaySlack);
            for (int i = m_sealed; i < m_calls.Count; i++) picked.Add(i);
            var polygon = new List<Vector2>(16);
            foreach (int i in picked)
            {
                Call c = m_calls[i];
                if (!Touches(c, x0, y0, x1 + 1, y1 + 1)) continue;
                canvas.m_layer = c.m_layer;
                Draw(canvas, c, polygon);
            }
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (canvas.m_ink[(y - y0) * width + x - x0] != 0) return true;
            return false;
        }

        // Per window (four ints each: x0, y0, x1, y1, inclusive, as InkIn takes them), an
        // order-sensitive hash of the calls from to to - 1 that InkIn would replay over it, with
        // what they draw. Two records whose calls before from are the same and whose hashes agree
        // on a window ink its pixels alike, so InkIn answers alike there (InkPainter's sea plan).
        // One pass over those calls, so the record need not be sealed.
        public ulong[] WindowHashes(int[] windows, int from, int to, int sheetSize)
        {
            int count = windows.Length / 4;
            var hashes = new ulong[count];
            if (count == 0) return hashes;
            int across = (sheetSize + CellSize - 1) / CellSize;
            int CellOf(float coordinate) => Mathf.Clamp((int)(coordinate / CellSize), 0, across - 1);
            // Each window filed under the cells its pixels and the pixel past them cover (the
            // reach of Touches), so a call can only touch the windows filed where its grown
            // bounds lie.
            var cells = new List<int>[across * across];
            for (int w = 0; w < count; w++)
            {
                int x0 = CellOf(windows[4 * w]), x1 = CellOf(windows[4 * w + 2] + 1);
                int y0 = CellOf(windows[4 * w + 1]), y1 = CellOf(windows[4 * w + 3] + 1);
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        int cell = y * across + x;
                        if (cells[cell] == null) cells[cell] = new List<int>(4);
                        cells[cell].Add(w);
                    }
                }
            }
            var last = new int[count];
            for (int w = 0; w < count; w++) last[w] = -1;
            for (int i = from; i < to; i++)
            {
                Call c = m_calls[i];
                Bounds(c, out float minX, out float minY, out float maxX, out float maxY);
                int x0 = CellOf(minX - ReplaySlack), x1 = CellOf(maxX + ReplaySlack);
                int y0 = CellOf(minY - ReplaySlack), y1 = CellOf(maxY + ReplaySlack);
                bool hashed = false;
                ulong h = 0UL;
                for (int y = y0; y <= y1; y++)
                {
                    for (int x = x0; x <= x1; x++)
                    {
                        List<int> filed = cells[y * across + x];
                        if (filed == null) continue;
                        foreach (int w in filed)
                        {
                            if (last[w] == i) continue;
                            last[w] = i;
                            // Touches, on the bounds already worked out.
                            if (maxX + ReplaySlack < windows[4 * w] || minX - ReplaySlack > windows[4 * w + 2] + 1
                                || maxY + ReplaySlack < windows[4 * w + 1] || minY - ReplaySlack > windows[4 * w + 3] + 1) continue;
                            if (!hashed)
                            {
                                h = CallHash(c);
                                hashed = true;
                            }
                            hashes[w] = (hashes[w] ^ h) * 0x9E3779B97F4A7C15UL + 1UL;
                        }
                    }
                }
            }
            return hashes;
        }

        // Whether a call's Bounds, grown by ReplaySlack, reach the pixels [left, right) x [top, bottom).
        private bool Touches(Call c, int left, int top, int right, int bottom)
        {
            Bounds(c, out float minX, out float minY, out float maxX, out float maxY);
            return maxX + ReplaySlack >= left && minX - ReplaySlack <= right && maxY + ReplaySlack >= top && minY - ReplaySlack <= bottom;
        }

        // An erase that marks a body, which a base fade set before it changes (InkCanvas.Lighten).
        private static bool MarksBody(Call c) =>
            (c.m_op == Op.Erase || c.m_op == Op.EraseEllipse || c.m_op == Op.EraseOutline)
            && c.m_body != BodyKind.None && c.m_body != BodyKind.Keep;

        private ulong CallHash(Call c)
        {
            ulong h = Mix(((ulong)c.m_op << 24) | ((ulong)c.m_layer << 16) | ((ulong)c.m_body << 8) | (ulong)c.m_land);
            bool points = c.m_op == Op.Fill || c.m_op == Op.Erase || c.m_op == Op.Tint || c.m_op == Op.EraseOutline;
            if (points)
            {
                int first = (int)c.m_a, count = (int)c.m_b;
                h = Mix(h ^ (ulong)count);
                for (int k = 0; k < count; k++)
                {
                    Vector2 p = m_points[first + k];
                    h = Mix(h ^ Bits(p.x, p.y));
                }
            }
            else
            {
                h = Mix(h ^ Bits(c.m_a, c.m_b));
                h = Mix(h ^ Bits(c.m_c, c.m_d));
            }
            h = Mix(h ^ Bits(points ? c.m_c : 0f, points ? c.m_d : 0f));
            return Mix(h ^ Bits(c.m_e, c.m_f));
        }

        private static ulong Bits(float a, float b) =>
            ((ulong)new FloatBits { m_float = a }.m_bits << 32) | new FloatBits { m_float = b }.m_bits;

        // A float's bits without allocating (BitConverter's bytes would, a million times a repaint).
        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float m_float;
            [FieldOffset(0)] public uint m_bits;
        }

        // SplitMix64's finalizer.
        private static ulong Mix(ulong z)
        {
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
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
