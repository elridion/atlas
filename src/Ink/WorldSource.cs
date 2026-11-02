using System;
using HarmonyLib;
using UnityEngine;

namespace Atlas
{
    internal enum Land : byte
    {
        Ocean,
        Meadows,
        BlackForest,
        Swamp,
        Mountain,
        Plains,
        Mistlands,
        Ashlands,
        DeepNorth,
    }

    /// <summary>
    /// The world as vanilla's minimap already sampled it: biome colour, height and forest mask for
    /// every map pixel, kept in plain arrays so the atlas can be drawn on a worker thread. Vanilla
    /// builds these textures (or loads them from its cache in the world folder) when the world
    /// loads, so the atlas never samples the world generator itself. They are created by script and
    /// stay readable, so reading them back gets the full data, not a copy reduced by texture quality.
    /// </summary>
    internal sealed class WorldSource
    {
        private static readonly AccessTools.FieldRef<Minimap, bool> s_hasGenerated =
            AccessTools.FieldRefAccess<Minimap, bool>("m_hasGenerated");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> s_heightTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_heightTexture");
        private static readonly AccessTools.FieldRef<Minimap, Texture2D> s_forestMaskTexture =
            AccessTools.FieldRefAccess<Minimap, Texture2D>("m_forestMaskTexture");
        private static readonly AccessTools.FieldRef<Minimap, Color> s_mistlandsColor =
            AccessTools.FieldRefAccess<Minimap, Color>("m_mistlandsColor");

        public int m_size;
        public float m_pixelSize;
        public float m_waterLevel;
        public int m_seed;

        public float[] m_height;
        public Land[] m_biome;
        // Vanilla's forest mask: red is forest in Meadows, Plains and Black Forest, green the
        // Mistlands forest, blue lava in the Ashlands (and the Ashlands sea gradient under water).
        public byte[] m_forest;
        public byte[] m_mist;
        public byte[] m_lava;

        private Color32[] m_colors;
        private Color32[] m_mask;
        private ushort[] m_halfHeights;
        private float[] m_floatHeights;
        private (Color32 color, Land land)[] m_palette;

        public static bool IsReady(Minimap minimap) =>
            minimap && s_hasGenerated(minimap) && minimap.m_mapTexture && s_heightTexture(minimap) && s_forestMaskTexture(minimap);

        // Main thread: copies the texture data. Decode finishes the work on a worker thread.
        public static WorldSource Capture(Minimap minimap)
        {
            Texture2D heights = s_heightTexture(minimap);
            var source = new WorldSource
            {
                m_size = minimap.m_textureSize,
                m_pixelSize = minimap.m_pixelSize,
                m_waterLevel = ZoneSystem.instance ? ZoneSystem.instance.m_waterLevel : 30f,
                m_seed = ZNet.World != null ? ZNet.World.m_seed : 0,
                m_colors = minimap.m_mapTexture.GetPixels32(),
                m_mask = s_forestMaskTexture(minimap).GetPixels32(),
            };
            if (heights.format == TextureFormat.RHalf)
            {
                source.m_halfHeights = heights.GetPixelData<ushort>(0).ToArray();
            }
            else
            {
                Color[] pixels = heights.GetPixels();
                source.m_floatHeights = new float[pixels.Length];
                for (int i = 0; i < pixels.Length; i++) source.m_floatHeights[i] = pixels[i].r;
            }
            // Vanilla paints the ocean plain white (Minimap.GetPixelColor); the other colours come
            // from the minimap prefab, which overrides the code defaults.
            source.m_palette = new[]
            {
                ((Color32)minimap.m_meadowsColor, Land.Meadows),
                ((Color32)minimap.m_blackforestColor, Land.BlackForest),
                ((Color32)minimap.m_swampColor, Land.Swamp),
                ((Color32)minimap.m_heathColor, Land.Plains),
                ((Color32)s_mistlandsColor(minimap), Land.Mistlands),
                ((Color32)minimap.m_ashlandsColor, Land.Ashlands),
                ((Color32)minimap.m_mountainColor, Land.Mountain),
                ((Color32)minimap.m_deepnorthColor, Land.DeepNorth),
                ((Color32)Color.white, Land.Ocean),
            };
            return source;
        }

        // Worker thread.
        public void Decode()
        {
            int count = m_size * m_size;
            m_height = new float[count];
            m_biome = new Land[count];
            m_forest = new byte[count];
            m_mist = new byte[count];
            m_lava = new byte[count];
            int half = m_size / 2;
            for (int i = 0; i < count; i++)
            {
                float h = m_halfHeights != null ? HalfToFloat(m_halfHeights[i]) : m_floatHeights[i];
                m_height[i] = h;
                Color32 mask = m_mask[i];
                m_forest[i] = mask.r;
                m_mist[i] = mask.g;
                m_lava[i] = mask.b;

                Color32 match = Nearest(m_colors[i]);
                bool ocean = Shares(match, Land.Ocean);
                bool mountain = Shares(match, Land.Mountain);
                bool north = Shares(match, Land.DeepNorth);
                Land land;
                // Colours several biomes share (white for all three by default) are told apart by
                // height and by the world generator's own Deep North test.
                if (ocean && (h < m_waterLevel || (!mountain && !north)))
                {
                    land = Land.Ocean;
                }
                else if (mountain && north)
                {
                    float wx = (i % m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                    float wz = (i / m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                    land = WorldGenerator.IsDeepnorth(wx, wz) ? Land.DeepNorth : Land.Mountain;
                }
                else if (mountain) land = Land.Mountain;
                else if (north) land = Land.DeepNorth;
                else land = First(match);
                m_biome[i] = land;
            }
            m_colors = null;
            m_mask = null;
            m_halfHeights = null;
            m_floatHeights = null;
        }

        // IEEE half to float in plain C#: Mathf.HalfToFloat is a native call, not promised to be
        // safe off the main thread.
        private static float HalfToFloat(ushort half)
        {
            int sign = (half >> 15) & 1;
            int exponent = (half >> 10) & 0x1F;
            int mantissa = half & 0x3FF;
            float value;
            if (exponent == 0) value = mantissa / 1024f / 16384f;
            else if (exponent == 31) value = mantissa == 0 ? float.PositiveInfinity : float.NaN;
            else value = (1f + mantissa / 1024f) * (float)Math.Pow(2, exponent - 15);
            return sign == 1 ? -value : value;
        }

        // The palette colour closest to a map pixel.
        private Color32 Nearest(Color32 c)
        {
            Color32 best = m_palette[0].color;
            int bestDistance = int.MaxValue;
            foreach (var (color, _) in m_palette)
            {
                int dr = c.r - color.r, dg = c.g - color.g, db = c.b - color.b;
                int distance = dr * dr + dg * dg + db * db;
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = color;
            }
            return best;
        }

        private bool Shares(Color32 color, Land land)
        {
            foreach (var (entry, owner) in m_palette)
                if (owner == land) return SameColor(entry, color);
            return false;
        }

        private Land First(Color32 color)
        {
            foreach (var (entry, land) in m_palette)
                if (SameColor(entry, color)) return land;
            return Land.Ocean;
        }

        private static bool SameColor(Color32 a, Color32 b) => a.r == b.r && a.g == b.g && a.b == b.b;

        // World position to source pixel coordinates, pixel centres on whole numbers.
        public float ToSourceX(float wx) => wx / m_pixelSize + m_size / 2 - 0.5f;
        public float ToSourceZ(float wz) => wz / m_pixelSize + m_size / 2 - 0.5f;

        public int Index(float wx, float wz)
        {
            int x = Mathf.Clamp((int)Math.Round(ToSourceX(wx)), 0, m_size - 1);
            int z = Mathf.Clamp((int)Math.Round(ToSourceZ(wz)), 0, m_size - 1);
            return z * m_size + x;
        }

        public float HeightAt(float wx, float wz) => Sample(m_height, wx, wz);

        // Bilinear sample of any grid laid out like the source maps.
        public float Sample(float[] grid, float wx, float wz)
        {
            float sx = Mathf.Clamp(ToSourceX(wx), 0f, m_size - 1.001f);
            float sz = Mathf.Clamp(ToSourceZ(wz), 0f, m_size - 1.001f);
            int x = (int)sx, z = (int)sz;
            float fx = sx - x, fz = sz - z;
            int i = z * m_size + x;
            float bottom = grid[i] + (grid[i + 1] - grid[i]) * fx;
            float top = grid[i + m_size] + (grid[i + m_size + 1] - grid[i + m_size]) * fx;
            return bottom + (top - bottom) * fz;
        }

        // Height change per metre, from neighbouring map pixels.
        public float SlopeAt(float wx, float wz)
        {
            int i = Index(wx, wz);
            int x = i % m_size, z = i / m_size;
            if (x < 1 || z < 1 || x >= m_size - 1 || z >= m_size - 1) return 0f;
            float dx = m_height[i + 1] - m_height[i - 1];
            float dz = m_height[i + m_size] - m_height[i - m_size];
            return Mathf.Sqrt(dx * dx + dz * dz) / (2f * m_pixelSize);
        }
    }
}
