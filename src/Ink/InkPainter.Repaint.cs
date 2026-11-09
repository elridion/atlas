using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A repaint for other pins that rasterizes only where the drawing changes. The painter runs
    /// from the kept pin-free start (Startwork) with a canvas that only records, so every
    /// placement decision is made as in a full paint and the new record comes out the same; the
    /// one decision that read pixels, the sea illustrations' clearance (InkNear), asks the record
    /// instead. The record's 64 px index cells whose calls differ from the last sheet's record
    /// (InkRecord.CellHashes) are the only pixels that can differ, so a copy of the last sheet
    /// has just those cells drawn again from the new record (InkRecord.ReplayClipped). Effects
    /// that are not local (a glyph sorted elsewhere, a figure moved) still show up as changed
    /// cells; they only make the region bigger.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // A repaint draws again at most this share of the sheet's index cells; with more, a full
        // paint is not much slower and needs no old sheet.
        private const float RepaintMostCells = 0.25f;

        // Worker thread. The sheet Paint would draw for pins, made from sheet and sheetRecord (the
        // last paint's sheet and its sealed record, for any pins), with the new record filled in
        // as Paint fills it; changed, when given, gets the sheet rectangles drawn again. Null when
        // it cannot (no kept groundwork and start for this world, or too much changed): paint
        // instead, with a fresh record. Neither the sheet nor the record given is changed.
        public static byte[] Repaint(WorldSource world, CancellationToken cancel, byte[] sheet, InkRecord sheetRecord,
            InkRecord record, IList<SheetPin> pins, List<RectInt> changed)
        {
            if (sheet == null || sheetRecord == null || record == null || record.Count != 0) return null;
            var painter = new InkPainter(world, cancel, recordOnly: true);
            painter.FindMarks(pins);
            painter.m_canvas.Record(record);
            if (pins != null) record.m_pins = new List<SheetPin>(pins).ToArray();
            record.m_marks.m_key = SheetPin.Key(record.m_pins);
            painter.m_record = record;
            if (!painter.TakeGroundwork() || !painter.TakeStartwork()) return null;
            painter.RunFromPins();
            record.Seal(CanvasSize);
            cancel.ThrowIfCancellationRequested();

            ulong[] before = sheetRecord.CellHashes(), after = record.CellHashes();
            if (before.Length != after.Length) return null;
            var dirty = new bool[after.Length];
            int count = 0;
            for (int i = 0; i < after.Length; i++)
            {
                if (before[i] == after[i]) continue;
                dirty[i] = true;
                count++;
            }
            if (count > after.Length * RepaintMostCells) return null;
            List<RectInt> rects = DirtyRects(dirty, record.CellsAcross);
            cancel.ThrowIfCancellationRequested();

            var ink = (byte[])sheet.Clone();
            int n = CanvasSize;
            Parallel.For(0, rects.Count, Workers.LoopOptions(cancel), k =>
            {
                RectInt r = rects[k];
                InkCanvas canvas = InkCanvas.Window(n, r.xMin, r.yMin, r.width, r.height);
                record.ReplayClipped(canvas, r.xMin, r.yMin, r.xMax, r.yMax);
                byte[][] planes = { canvas.m_ink, canvas.m_body, canvas.m_bodyNorth, canvas.m_bodyMist, canvas.m_bodyWood, canvas.m_pinInk };
                for (int p = 0; p < planes.Length; p++)
                    for (int y = 0; y < r.height; y++)
                        Buffer.BlockCopy(planes[p], y * r.width, ink, p * n * n + (r.yMin + y) * n + r.xMin, r.width);
            });
            changed?.AddRange(rects);
            return ink;
        }

        // The changed cells as sheet rectangles: runs along each row, a run joined to the one
        // above it when they span the same cells.
        private static List<RectInt> DirtyRects(bool[] dirty, int across)
        {
            var rects = new List<RectInt>();
            var open = new List<RectInt>();
            for (int cy = 0; cy < across; cy++)
            {
                var next = new List<RectInt>();
                int cx = 0;
                while (cx < across)
                {
                    if (!dirty[cy * across + cx])
                    {
                        cx++;
                        continue;
                    }
                    int from = cx;
                    while (cx < across && dirty[cy * across + cx]) cx++;
                    int width = cx - from;
                    int above = open.FindIndex(r => r.x == from && r.width == width);
                    if (above >= 0)
                    {
                        RectInt grown = open[above];
                        open.RemoveAt(above);
                        grown.height++;
                        next.Add(grown);
                    }
                    else
                    {
                        next.Add(new RectInt(from, cy, width, 1));
                    }
                }
                rects.AddRange(open);
                open = next;
            }
            rects.AddRange(open);
            int cell = InkRecord.CellSize;
            for (int k = 0; k < rects.Count; k++)
            {
                RectInt r = rects[k];
                int x = r.x * cell, y = r.y * cell;
                rects[k] = new RectInt(x, y, Math.Min(r.width * cell, CanvasSize - x), Math.Min(r.height * cell, CanvasSize - y));
            }
            return rects;
        }
    }
}
