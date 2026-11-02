using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
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
        }

        // One call: for strokes a..d are the ends, e the width; for ellipses and rings a, b the
        // centre and c, d the radii (e the ring width); for polygons a is the first point's index
        // in m_points and b the point count. f is the ink strength.
        private struct Call
        {
            public Op m_op;
            public float m_a, m_b, m_c, m_d, m_e, m_f;
        }

        private readonly List<Call> m_calls = new List<Call>(1 << 20);
        private readonly List<Vector2> m_points = new List<Vector2>(1 << 16);
        private List<int>[] m_cells;
        private readonly List<int> m_wide = new List<int>();
        private int m_cellsAcross;

        public int Count => m_calls.Count;

        public void Stroke(float x0, float y0, float x1, float y1, float width, float strength, bool erase) =>
            m_calls.Add(new Call { m_op = erase ? Op.Scrape : Op.Stroke, m_a = x0, m_b = y0, m_c = x1, m_d = y1, m_e = width, m_f = strength });

        public void Ellipse(float cx, float cy, float rx, float ry, float strength, bool erase) =>
            m_calls.Add(new Call { m_op = erase ? Op.EraseEllipse : Op.Ellipse, m_a = cx, m_b = cy, m_c = rx, m_d = ry, m_f = strength });

        public void Ring(float cx, float cy, float rx, float ry, float width, float strength) =>
            m_calls.Add(new Call { m_op = Op.Ring, m_a = cx, m_b = cy, m_c = rx, m_d = ry, m_e = width, m_f = strength });

        public void Polygon(IList<Vector2> polygon, float strength, bool erase)
        {
            m_calls.Add(new Call { m_op = erase ? Op.Erase : Op.Fill, m_a = m_points.Count, m_b = polygon.Count, m_f = strength });
            for (int i = 0; i < polygon.Count; i++) m_points.Add(polygon[i]);
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
        public void Replay(InkCanvas canvas, float left, float top, float right, float bottom)
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

            var polygon = new List<Vector2>(16);
            int previous = -1;
            foreach (int i in picked)
            {
                // A call in several cells is picked once per cell.
                if (i == previous) continue;
                previous = i;
                Call c = m_calls[i];
                switch (c.m_op)
                {
                    case Op.Stroke: canvas.Stroke(c.m_a, c.m_b, c.m_c, c.m_d, c.m_e, c.m_f); break;
                    case Op.Scrape: canvas.Scrape(c.m_a, c.m_b, c.m_c, c.m_d, c.m_e); break;
                    case Op.Ellipse: canvas.Dot(c.m_a, c.m_b, c.m_c, c.m_f); break;
                    case Op.EraseEllipse: canvas.EraseEllipse(c.m_a, c.m_b, c.m_c, c.m_d); break;
                    case Op.Ring: canvas.Ring(c.m_a, c.m_b, c.m_c, c.m_d, c.m_e, c.m_f); break;
                    case Op.Fill:
                    case Op.Erase:
                        polygon.Clear();
                        for (int k = 0; k < (int)c.m_b; k++) polygon.Add(m_points[(int)c.m_a + k]);
                        if (c.m_op == Op.Fill) canvas.Fill(polygon, c.m_f);
                        else canvas.Erase(polygon);
                        break;
                }
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
                    minX -= 1f;
                    minY -= 1f;
                    maxX += 1f;
                    maxY += 1f;
                    return;
                }
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
