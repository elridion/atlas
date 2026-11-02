using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The ground under one detail tile, on a regular grid a few screen pixels apart (the nearest
    /// of SampleCache's spacings): far finer than the minimap where the world generator is the
    /// source, and spaced by screen pixels, so a tile costs about the same at any zoom. Distances to water and to the nearest
    /// wood are kept for the glyph rules' margins. Built on a worker thread, read-only after.
    /// </summary>
    internal sealed class TileGrid
    {
        public readonly int m_columns;
        public readonly int m_rows;
        // The sheet position of sample (0, 0) and the sheet pixels between samples.
        public readonly float m_left;
        public readonly float m_top;
        public readonly float m_step;
        public readonly float m_waterLevel;

        public readonly float[] m_height;
        public readonly Land[] m_land;
        public readonly float[] m_wood;
        // Distances in samples, square grids of m_size (DistanceField works on squares).
        public byte[] m_shore;
        public byte[] m_woodDistance;
        public int m_size;

        private TileGrid(float left, float top, float step, int columns, int rows, float waterLevel)
        {
            m_left = left;
            m_top = top;
            m_step = step;
            m_columns = columns;
            m_rows = rows;
            m_waterLevel = waterLevel;
            m_height = new float[columns * rows];
            m_land = new Land[columns * rows];
            m_wood = new float[columns * rows];
        }

        // The ground under the sheet rectangle (left, top, right, bottom) at a cache level, with a
        // sample of margin all round so contours reach the tile's edges. Samples sit on the
        // world-anchored points of the cache (world x = index * spacing), so tiles share them.
        public static TileGrid Build(SampleCache cache, float waterLevel, float left, float top, float right, float bottom,
            int level, int threads, CancellationToken cancel)
        {
            float spacing = SampleCache.Spacing(level);
            float mpp = InkPainter.MetresPerPixel, half = InkPainter.HalfExtent;
            int x0 = (int)Math.Floor((left * mpp - half) / spacing) - 1;
            int x1 = (int)Math.Ceiling((right * mpp - half) / spacing) + 1;
            int z0 = (int)Math.Floor((half - bottom * mpp) / spacing) - 1;
            int z1 = (int)Math.Ceiling((half - top * mpp) / spacing) + 1;
            int columns = x1 - x0 + 1, rows = z1 - z0 + 1;
            // Rows run south as the sheet does: row 0 is the northern edge, z1.
            var grid = new TileGrid((x0 * spacing + half) / mpp, (half - z1 * spacing) / mpp, spacing / mpp, columns, rows, waterLevel);
            Dictionary<long, SampleCache.Chunk> chunks = cache.Fetch(level, x0, x1, z0, z1, threads, cancel);
            cancel.ThrowIfCancellationRequested();
            const int n = SampleCache.ChunkSamples;
            for (int row = 0; row < rows; row++)
            {
                int z = z1 - row;
                int cz = SampleCache.FloorDiv(z);
                for (int column = 0; column < columns; column++)
                {
                    int x = x0 + column;
                    SampleCache.Chunk chunk = chunks[SampleCache.ChunkKey(level, x, z)];
                    int from = (z - cz * n) * n + (x - SampleCache.FloorDiv(x) * n);
                    int i = row * columns + column;
                    grid.m_height[i] = chunk.m_height[from];
                    grid.m_land[i] = chunk.m_land[from];
                    grid.m_wood[i] = chunk.m_wood[from];
                }
            }
            grid.BuildDistances();
            return grid;
        }

        private void BuildDistances()
        {
            m_size = Math.Max(m_columns, m_rows);
            int n = m_size;
            var field = new DistanceField(n);
            m_shore = new byte[n * n];
            m_woodDistance = new byte[n * n];
            field.Compute(i => !IsLandCell(i % n, i / n), m_shore, 1f);
            field.Compute(i => IsWoodCell(i % n, i / n), m_woodDistance, 1f);
        }


        private bool IsLandCell(int x, int y) => x < m_columns && y < m_rows && m_height[y * m_columns + x] > m_waterLevel;

        private bool IsWoodCell(int x, int y) => x < m_columns && y < m_rows && m_wood[y * m_columns + x] > 0f;

        // Sheet point to grid coordinates.
        public float GridX(float x) => (x - m_left) / m_step;
        public float GridY(float y) => (y - m_top) / m_step;

        public int Nearest(float x, float y)
        {
            int gx = Mathf.Clamp((int)Math.Round(GridX(x)), 0, m_columns - 1);
            int gy = Mathf.Clamp((int)Math.Round(GridY(y)), 0, m_rows - 1);
            return gy * m_columns + gx;
        }

        // Bilinear sample of a grid-shaped array at a sheet point.
        public float Sample(float[] values, float x, float y)
        {
            float gx = Mathf.Clamp(GridX(x), 0f, m_columns - 1.001f);
            float gy = Mathf.Clamp(GridY(y), 0f, m_rows - 1.001f);
            int ix = (int)gx, iy = (int)gy;
            float fx = gx - ix, fy = gy - iy;
            int i = iy * m_columns + ix;
            float top = values[i] + (values[i + 1] - values[i]) * fx;
            float bottom = values[i + m_columns] + (values[i + m_columns + 1] - values[i + m_columns]) * fx;
            return top + (bottom - top) * fy;
        }

        // A distance array at the sample nearest a sheet point, in sheet pixels.
        public float Distance(byte[] distances, float x, float y)
        {
            int gx = Mathf.Clamp((int)Math.Round(GridX(x)), 0, m_columns - 1);
            int gy = Mathf.Clamp((int)Math.Round(GridY(y)), 0, m_rows - 1);
            return distances[gy * m_size + gx] * m_step;
        }

        public bool IsLand(float x, float y) => Sample(m_height, x, y) > m_waterLevel;

        public bool IsWood(float x, float y) => Sample(m_wood, x, y) > 0f;

        // Height change per metre at a sheet point, from neighbouring samples.
        public float Slope(float x, float y)
        {
            int i = Nearest(x, y);
            int gx = i % m_columns, gy = i / m_columns;
            if (gx < 1 || gy < 1 || gx >= m_columns - 1 || gy >= m_rows - 1) return 0f;
            float dx = m_height[i + 1] - m_height[i - 1];
            float dy = m_height[i + m_columns] - m_height[i - m_columns];
            return Mathf.Sqrt(dx * dx + dy * dy) / (2f * m_step * InkPainter.MetresPerPixel);
        }
    }
}
