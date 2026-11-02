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
        // 2048 over the sheet is 10.5 m a pixel, a little finer than vanilla's 12 m exploration grid.
        public const int FogSize = 2048;

        // Parchment and sepia ink.
        public static readonly Color32 PaperColor = new Color32(232, 218, 182, 255);
        public static readonly Color32 InkColor = new Color32(46, 33, 24, 255);

        // How strongly the paper is mottled and darkened towards its corners.
        private const float Mottle = 0.07f;
        private const float Vignette = 0.2f;
        // The mottling was tuned on a 1024 sheet; this keeps its grain the same size on the paper.
        private const float PaperScale = FogSize / 1024f;
        // Fog is lifted inside this band at the world edge, so the frame always shows.
        private const float FrameBand = 120f;

        // The biome wash: per biome (in Land order) a tint and how far the paper is moved towards
        // it, the extra share in the woods, and the blur passes (each about 10 m) that soften it.
        private struct Tint
        {
            public Color32 m_color;
            public float m_strength;

            public Tint(byte r, byte g, byte b, float strength)
            {
                m_color = new Color32(r, g, b, 255);
                m_strength = strength;
            }
        }

        private static readonly Tint[] s_tints =
        {
            new Tint(140, 170, 180, 0.18f), // Ocean (land vanilla files as ocean, rare)
            new Tint(150, 180, 90, 0.24f),  // Meadows
            new Tint(85, 110, 65, 0.26f),   // Black Forest
            new Tint(120, 105, 65, 0.28f),  // Swamp
            new Tint(205, 205, 215, 0.3f),  // Mountain
            new Tint(225, 185, 95, 0.24f),  // Plains
            new Tint(120, 105, 135, 0.26f), // Mistlands
            new Tint(165, 70, 50, 0.26f),   // Ashlands
            new Tint(242, 246, 250, 0.4f),  // Deep North
        };
        private static readonly Tint s_water = new Tint(150, 180, 185, 0.12f);
        private const float WoodWash = 0.1f;
        private const int WashBlur = 3;

        // Stains on the parchment, by kind: how many, their radius range (paper pixels, 10.5 m each),
        // how much they darken inside, and how dark the tide line at their rim is.
        private struct StainKind
        {
            public int m_count;
            public float m_minRadius, m_maxRadius, m_inside, m_rim;

            public StainKind(int count, float minRadius, float maxRadius, float inside, float rim)
            {
                m_count = count;
                m_minRadius = minRadius;
                m_maxRadius = maxRadius;
                m_inside = inside;
                m_rim = rim;
            }
        }

        private static readonly StainKind[] s_stains =
        {
            // Counts are for the whole 21.6 km sheet; the view shows only the explored part, so
            // there are enough that a few always fall in it.
            new StainKind(12, 120f, 300f, 0.06f, 0.1f),  // old water stains with faint tide lines
            new StainKind(45, 25f, 80f, 0.07f, 0.17f),   // spilt drops dried to rings
            new StainKind(500, 1.5f, 5f, 0.2f, 0f),      // foxing: small brown spots of age
        };
        // The tide line's place (share of the radius) and width, and how ragged a stain's edge is.
        private const float RimAt = 0.96f;
        private const float RimWidth = 0.035f;
        private const float Ragged = 0.45f;

        // How much each paper pixel is darkened by stains.
        private static float[] Stains(int seed)
        {
            int n = FogSize;
            var stain = new float[n * n];
            for (int kind = 0; kind < s_stains.Length; kind++)
            {
                StainKind k = s_stains[kind];
                for (int s = 0; s < k.m_count; s++)
                {
                    int hash = Noise.HashInt(s, kind, seed + 31);
                    float cx = Noise.Unit(hash, 1) * n, cy = Noise.Unit(hash, 2) * n;
                    float r = Mathf.Lerp(k.m_minRadius, k.m_maxRadius, Noise.Unit(hash, 3));
                    float strength = Mathf.Lerp(0.6f, 1f, Noise.Unit(hash, 4));
                    float reach = r * (1f + Ragged);
                    int x0 = Math.Max(0, (int)(cx - reach)), x1 = Math.Min(n - 1, (int)(cx + reach));
                    int y0 = Math.Max(0, (int)(cy - reach)), y1 = Math.Min(n - 1, (int)(cy + reach));
                    float grain = Math.Max(2f, r * 0.45f);
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float dx = x - cx, dy = y - cy;
                            // A ragged edge: the distance is stretched by noise around the stain.
                            float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                            if (d > 1f + Ragged) continue;
                            d *= 1f + (Noise.Value(x / grain, y / grain, hash) - 0.5f) * Ragged;
                            float inside = 1f - Mathf.Clamp01((d - 0.85f) / 0.15f);
                            float t = (d - RimAt) / RimWidth;
                            float rim = k.m_rim > 0f ? Mathf.Exp(-t * t) : 0f;
                            stain[y * n + x] += (k.m_inside * inside + k.m_rim * rim) * strength;
                        }
                    }
                }
            }
            return stain;
        }

        public static byte[] Parchment(int seed)
        {
            int n = FogSize;
            var rgb = new byte[n * n * 3];
            float[] stains = Stains(seed);
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float shade = 1f - Math.Min(stains[y * n + x], 0.35f)
                        + (Noise.Value(x / (90f * PaperScale), y / (90f * PaperScale), seed) - 0.5f) * Mottle * 2f
                        + (Noise.Value(x / (17f * PaperScale), y / (17f * PaperScale), seed + 1) - 0.5f) * Mottle
                        + (Noise.Value(x / (3f * PaperScale), y / (3f * PaperScale), seed + 2) - 0.5f) * Mottle * 0.5f;
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float corner = Mathf.Clamp01((Mathf.Sqrt(u * u + v * v) - 0.75f) / 0.66f);
                    shade *= 1f - Vignette * corner * corner;
                    int i = (y * n + x) * 3;
                    // Darker paper also turns browner, as old parchment does.
                    rgb[i] = ToByte(PaperColor.r * shade);
                    rgb[i + 1] = ToByte(PaperColor.g * Mathf.Pow(shade, 1.1f));
                    rgb[i + 2] = ToByte(PaperColor.b * Mathf.Pow(shade, 1.35f));
                }
            }
            return rgb;
        }

        // The parchment under a faint watercolour wash, one tint per biome (darker in the woods,
        // pale blue on water), so biomes read apart at every zoom while the ink stays black. The
        // tints are blurred, so they bleed softly across biome edges and the coast as a brush
        // would. The fog keeps the bare parchment, so unexplored land shows no colour either.
        public static byte[] Washed(byte[] parchment, WorldSource world)
        {
            int n = FogSize;
            float metres = 2f * InkPainter.HalfExtent / n;
            float edgeSqr = InkPainter.EdgeRadius * InkPainter.EdgeRadius;
            var red = new float[n * n];
            var green = new float[n * n];
            var blue = new float[n * n];
            var strength = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                    if (wx * wx + wz * wz >= edgeSqr) continue;
                    int i = y * n + x;
                    bool land = world.HeightAt(wx, wz) > world.m_waterLevel;
                    Tint tint = land ? s_tints[(int)world.m_biome[world.Index(wx, wz)]] : s_water;
                    float s = tint.m_strength;
                    if (land && world.Sample(world.m_wood, wx, wz) > 0.5f) s += WoodWash;
                    red[i] = tint.m_color.r * s;
                    green[i] = tint.m_color.g * s;
                    blue[i] = tint.m_color.b * s;
                    strength[i] = s;
                }
            }
            for (int pass = 0; pass < WashBlur; pass++)
            {
                Blur(red, n);
                Blur(green, n);
                Blur(blue, n);
                Blur(strength, n);
            }
            var rgb = new byte[parchment.Length];
            for (int i = 0; i < n * n; i++)
            {
                float keep = 1f - strength[i];
                int p = i * 3;
                rgb[p] = ToByte(parchment[p] * keep + red[i]);
                rgb[p + 1] = ToByte(parchment[p + 1] * keep + green[i]);
                rgb[p + 2] = ToByte(parchment[p + 2] * keep + blue[i]);
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

        // A 3x3 box blur, rows then columns.
        public static void Blur(float[] values, int n)
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
