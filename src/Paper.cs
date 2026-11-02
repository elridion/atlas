using System;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Pixel data for the atlas textures, built on worker threads. Texture rows run from south (row
    /// 0) to north, while the ink canvas runs from north, so the ink is flipped here. The paper and
    /// the fog share the same parchment colours, so where the fog lies the sheet looks blank, not
    /// covered.
    /// </summary>
    internal static class Paper
    {
        public const int FogSize = 1024;

        // Parchment and sepia ink.
        private static readonly Color32 s_paper = new Color32(232, 218, 182, 255);
        public static readonly Color32 InkColor = new Color32(46, 33, 24, 255);

        // How strongly the paper is mottled and darkened towards its corners.
        private const float Mottle = 0.07f;
        private const float Vignette = 0.2f;
        // Fog is lifted inside this band at the world edge, so the frame always shows.
        private const float FrameBand = 120f;

        public static byte[] Parchment(int seed)
        {
            int n = FogSize;
            var rgb = new byte[n * n * 3];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float shade = 1f
                        + (Noise.Value(x / 90f, y / 90f, seed) - 0.5f) * Mottle * 2f
                        + (Noise.Value(x / 17f, y / 17f, seed + 1) - 0.5f) * Mottle
                        + (Noise.Value(x / 3f, y / 3f, seed + 2) - 0.5f) * Mottle * 0.5f;
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float corner = Mathf.Clamp01((Mathf.Sqrt(u * u + v * v) - 0.75f) / 0.66f);
                    shade *= 1f - Vignette * corner * corner;
                    int i = (y * n + x) * 3;
                    // Darker paper also turns browner, as old parchment does.
                    rgb[i] = ToByte(s_paper.r * shade);
                    rgb[i + 1] = ToByte(s_paper.g * Mathf.Pow(shade, 1.1f));
                    rgb[i + 2] = ToByte(s_paper.b * Mathf.Pow(shade, 1.35f));
                }
            }
            return rgb;
        }

        // RGBA ink for every mip level, as LoadRawTextureData expects it.
        public static byte[] InkWithMips(byte[] ink)
        {
            int n = InkPainter.CanvasSize;
            int total = 0;
            for (int size = n; size >= 1; size /= 2) total += size * size;
            var data = new byte[total * 4];
            byte[] level = new byte[n * n];
            for (int y = 0; y < n; y++) Array.Copy(ink, (n - 1 - y) * n, level, y * n, n);

            int offset = 0;
            for (int size = n; ; size /= 2)
            {
                for (int i = 0; i < size * size; i++)
                {
                    int o = offset + i * 4;
                    data[o] = InkColor.r;
                    data[o + 1] = InkColor.g;
                    data[o + 2] = InkColor.b;
                    data[o + 3] = level[i];
                }
                offset += size * size * 4;
                if (size == 1) break;
                int next = size / 2;
                var smaller = new byte[next * next];
                for (int y = 0; y < next; y++)
                {
                    for (int x = 0; x < next; x++)
                    {
                        int a = (2 * y) * size + 2 * x;
                        smaller[y * next + x] = (byte)((level[a] + level[a + 1] + level[a + size] + level[a + size + 1] + 2) / 4);
                    }
                }
                level = smaller;
            }
            return data;
        }

        // Fills the alpha of an RGBA fog buffer whose colours are the parchment: opaque where the
        // snapshot shows nothing explored, clear where it does, with a soft edge between.
        public static void Fog(byte[] rgba, byte[] parchment, Snapshot snapshot)
        {
            int n = FogSize;
            float metres = 2f * InkPainter.HalfExtent / n;
            var cover = new float[n * n];
            float edge = InkPainter.EdgeRadius - FrameBand;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                    float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                    if (wx * wx + wz * wz > edge * edge) continue;
                    int seen = 0;
                    for (int sy = -1; sy <= 1; sy++)
                        for (int sx = -1; sx <= 1; sx++)
                            if (snapshot != null && snapshot.IsExplored(wx + sx * metres / 3f, wz + sy * metres / 3f)) seen++;
                    cover[y * n + x] = 1f - seen / 9f;
                }
            }
            Blur(cover, n);
            Blur(cover, n);
            for (int i = 0; i < n * n; i++)
            {
                int o = i * 4, p = i * 3;
                rgba[o] = parchment[p];
                rgba[o + 1] = parchment[p + 1];
                rgba[o + 2] = parchment[p + 2];
                float c = cover[i];
                rgba[o + 3] = ToByte(c * c * (3f - 2f * c) * 255f);
            }
        }

        private static void Blur(float[] values, int n)
        {
            var row = new float[n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                    row[x] = (values[y * n + Math.Max(0, x - 1)] + values[y * n + x] + values[y * n + Math.Min(n - 1, x + 1)]) / 3f;
                Array.Copy(row, 0, values, y * n, n);
            }
            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                    row[y] = (values[Math.Max(0, y - 1) * n + x] + values[y * n + x] + values[Math.Min(n - 1, y + 1) * n + x]) / 3f;
                for (int y = 0; y < n; y++) values[y * n + x] = row[y];
            }
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(value + 0.5f, 0f, 255f);
    }
}
