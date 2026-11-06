using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Exact Euclidean distance transform (Felzenszwalb and Huttenlocher) and contour tracing on
    /// square grids. Distances are in grid cells to the centre of the nearest seed cell, so a
    /// boundary between seed and non-seed cells lies at about half a cell.
    /// </summary>
    internal sealed class DistanceField
    {
        private const float Infinity = 1e20f;

        // Columns handed to one parallel step together, so a worker writes whole cache lines.
        private const int ColumnBlock = 16;

        private readonly int m_size;
        private readonly float[] m_columns;
        private readonly Scratch m_scratch;

        // One line's working arrays: the input (m_f), the output (m_d) and the parabolas.
        private sealed class Scratch
        {
            public readonly float[] m_f;
            public readonly float[] m_d;
            public readonly double[] m_z;
            public readonly int[] m_v;

            public Scratch(int size)
            {
                m_f = new float[size];
                m_d = new float[size];
                m_z = new double[size + 1];
                m_v = new int[size];
            }
        }

        public DistanceField(int size)
        {
            m_size = size;
            m_columns = new float[size * size];
            m_scratch = new Scratch(size);
        }

        // Writes min(distance * scale, 255) for every cell, where distance is to the nearest seed.
        public void Compute(Func<int, bool> isSeed, byte[] output, float scale)
        {
            int n = m_size;
            for (int x = 0; x < n; x++) Column(isSeed, x, m_scratch);
            for (int y = 0; y < n; y++) Row(output, scale, y, m_scratch);
        }

        // The same on the atlas's workers, every column and then every row on its own, so the
        // result is the serial one byte for byte. Only from atlas work (a worker thread): isSeed
        // must be safe to call from several threads, and a caller off the workers would block on
        // them. A set cancel throws OperationCanceledException.
        public void ComputeParallel(Func<int, bool> isSeed, byte[] output, float scale, CancellationToken cancel)
        {
            int n = m_size;
            int blocks = (n + ColumnBlock - 1) / ColumnBlock;
            Parallel.For(0, blocks, Workers.LoopOptions(cancel), () => new Scratch(n), (block, state, scratch) =>
            {
                int end = Math.Min(n, (block + 1) * ColumnBlock);
                for (int x = block * ColumnBlock; x < end; x++) Column(isSeed, x, scratch);
                return scratch;
            }, scratch => { });
            Parallel.For(0, n, Workers.LoopOptions(cancel), () => new Scratch(n), (y, state, scratch) =>
            {
                Row(output, scale, y, scratch);
                return scratch;
            }, scratch => { });
        }

        private void Column(Func<int, bool> isSeed, int x, Scratch scratch)
        {
            int n = m_size;
            float[] f = scratch.m_f, d = scratch.m_d;
            for (int y = 0; y < n; y++) f[y] = isSeed(y * n + x) ? 0f : Infinity;
            Transform(n, scratch);
            for (int y = 0; y < n; y++) m_columns[y * n + x] = d[y];
        }

        private void Row(byte[] output, float scale, int y, Scratch scratch)
        {
            int n = m_size;
            int row = y * n;
            float[] d = scratch.m_d;
            Array.Copy(m_columns, row, scratch.m_f, 0, n);
            Transform(n, scratch);
            for (int x = 0; x < n; x++)
            {
                float value = Mathf.Sqrt(d[x]) * scale;
                output[row + x] = value >= 255f ? (byte)255 : (byte)(value + 0.5f);
            }
        }

        // One-dimensional squared distance transform of m_f into m_d.
        // Only cells with a finite value become parabolas, so cells far from every seed never
        // enter the arithmetic. Squares of 4096 exceed float precision, hence the doubles.
        private static void Transform(int n, Scratch scratch)
        {
            float[] f = scratch.m_f, d = scratch.m_d;
            double[] z = scratch.m_z;
            int[] v = scratch.m_v;
            int k = -1;
            for (int q = 0; q < n; q++)
            {
                if (f[q] >= Infinity) continue;
                double s = 0.0;
                while (k >= 0)
                {
                    int p = v[k];
                    s = ((f[q] + (double)q * q) - (f[p] + (double)p * p)) / (2.0 * (q - p));
                    if (s > z[k]) break;
                    k--;
                }
                k++;
                v[k] = q;
                z[k] = k == 0 ? double.NegativeInfinity : s;
                z[k + 1] = double.PositiveInfinity;
            }
            if (k < 0)
            {
                for (int q = 0; q < n; q++) d[q] = Infinity;
                return;
            }
            k = 0;
            for (int q = 0; q < n; q++)
            {
                while (z[k + 1] < q) k++;
                int p = v[k];
                d[q] = (float)((double)(q - p) * (q - p) + f[p]);
            }
        }

        /// <summary>
        /// Marching squares: the line where value(x, y) crosses level, as segments between cell
        /// centres, in grid coordinates with cell (x, y) centred on (x, y). Cells for which skip
        /// returns true are left out.
        /// </summary>
        public static void Contour(int size, Func<int, int, float> value, float level, Func<int, int, bool> skip,
            Action<Vector2, Vector2> segment) =>
            Contour(size, value, level, skip, (x, y, a, b) => segment(a, b));

        // As above, also telling each segment's cell.
        public static void Contour(int size, Func<int, int, float> value, float level, Func<int, int, bool> skip,
            Action<int, int, Vector2, Vector2> segment)
        {
            var points = new Vector2[4];
            for (int y = 0; y < size - 1; y++)
            {
                for (int x = 0; x < size - 1; x++)
                {
                    if (skip != null && skip(x, y)) continue;
                    float a = value(x, y) - level;         // top left
                    float b = value(x + 1, y) - level;     // top right
                    float c = value(x + 1, y + 1) - level; // bottom right
                    float d = value(x, y + 1) - level;     // bottom left
                    int count = 0;
                    if ((a < 0f) != (b < 0f)) points[count++] = new Vector2(x + a / (a - b), y);
                    if ((b < 0f) != (c < 0f)) points[count++] = new Vector2(x + 1, y + b / (b - c));
                    if ((c < 0f) != (d < 0f)) points[count++] = new Vector2(x + 1 - c / (c - d), y + 1);
                    if ((d < 0f) != (a < 0f)) points[count++] = new Vector2(x, y + 1 - d / (d - a));
                    if (count == 2)
                    {
                        segment(x, y, points[0], points[1]);
                    }
                    else if (count == 4)
                    {
                        // Saddle: a and c share a side. When the cell's average is on that side too,
                        // a and c join through the middle, so the lines cut off the b and d corners;
                        // otherwise they cut off a and c.
                        if ((a + b + c + d) * 0.25f < 0f == a < 0f)
                        {
                            segment(x, y, points[0], points[1]);
                            segment(x, y, points[2], points[3]);
                        }
                        else
                        {
                            segment(x, y, points[0], points[3]);
                            segment(x, y, points[1], points[2]);
                        }
                    }
                }
            }
        }
    }
}
