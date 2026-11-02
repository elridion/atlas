using System;

namespace Atlas
{
    /// <summary>
    /// How much ink lies under any rectangle of the sheet, for placing pin names on clear ground
    /// as cartographers do (Imhof): the sheet's ink coverage averaged over Cell x Cell blocks, kept
    /// as a summed-area table so a rectangle of any size costs four reads. Built on a worker from
    /// the sheet before it is uploaded (the uploaded texture cannot be read back).
    /// </summary>
    internal sealed class InkDensity
    {
        private const int Cell = 4;
        private readonly int m_cells;
        private readonly int[] m_sums;

        private InkDensity(int cells, int[] sums)
        {
            m_cells = cells;
            m_sums = sums;
        }

        // sheet: the painted sheet, ink coverage first (CanvasSize squared bytes, rows running south).
        public static InkDensity FromSheet(byte[] sheet)
        {
            int size = InkPainter.CanvasSize, cells = size / Cell;
            var sums = new int[(cells + 1) * (cells + 1)];
            for (int cy = 0; cy < cells; cy++)
            {
                int row = 0;
                for (int cx = 0; cx < cells; cx++)
                {
                    int total = 0;
                    for (int y = cy * Cell; y < cy * Cell + Cell; y++)
                        for (int x = cx * Cell; x < cx * Cell + Cell; x++)
                            total += sheet[y * size + x];
                    row += total / (Cell * Cell);
                    sums[(cy + 1) * (cells + 1) + cx + 1] = sums[cy * (cells + 1) + cx + 1] + row;
                }
            }
            return new InkDensity(cells, sums);
        }

        // Mean ink coverage (0 bare to 1 solid) over a rectangle in sheet pixels, y running south.
        public float Mean(float left, float top, float right, float bottom)
        {
            int x0 = Clamp((int)Math.Floor(left / Cell)), x1 = Clamp((int)Math.Ceiling(right / Cell));
            int y0 = Clamp((int)Math.Floor(top / Cell)), y1 = Clamp((int)Math.Ceiling(bottom / Cell));
            if (x1 <= x0 || y1 <= y0) return 0f;
            int stride = m_cells + 1;
            long total = (long)m_sums[y1 * stride + x1] - m_sums[y0 * stride + x1] - m_sums[y1 * stride + x0] + m_sums[y0 * stride + x0];
            return total / (255f * (x1 - x0) * (y1 - y0));
        }

        private int Clamp(int cell) => Math.Max(0, Math.Min(m_cells, cell));
    }
}
