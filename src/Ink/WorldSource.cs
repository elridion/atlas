using System;
using System.Collections.Generic;
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
    /// One river or stream as the world generator lays it out: a straight run from start to end,
    /// swung from side to side by a sum of sines (WorldGenerator.RenderRivers), in world metres
    /// (y is the world z axis).
    /// </summary>
    internal struct RiverPath
    {
        public Vector2 m_start;
        public Vector2 m_end;
        public float m_curveWidth;
        public float m_curveWavelength;
        // The channel's width in metres, the middle of the generator's range for it.
        public float m_width;
        public bool m_stream;

        // The course's point at distance t along it, the same curve vanilla carves.
        public Vector2 PointAt(float t)
        {
            Vector2 along = (m_end - m_start).normalized;
            var side = new Vector2(-along.y, along.x);
            double k = t / m_curveWavelength;
            float swing = (float)(Math.Sin(k) * Math.Sin(k * 0.634119987487793) * Math.Sin(k * 0.3341200053691864) * m_curveWidth);
            return m_start + along * t + side * swing;
        }
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

        // How far above the water a swamp's water is raised, so it reads as its ground, and how
        // deep swamp water joined to the sea must be to stay water (MarkSwampSea).
        public const float SwampFloor = 0.5f;
        public const float SwampShallow = 1.5f;

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
        // The woods as a smooth field, edge at 0.5 (Woods.Build).
        public float[] m_wood;
        // The Ashlands' lava fields as a smooth field, edge at 0.5 (Woods.BuildLava).
        public float[] m_lavaField;
        // The swamp's wetness (1 in its pools, 0 on its dry ground, from the heights before the
        // shallow pools are raised) and the swamp itself (1 inside, 0 outside, edge at 0.5), both
        // softened by SwampBlur passes, so marks gather round the real pools and thin out at the
        // swamp's edge.
        public float[] m_swampWet;
        public float[] m_swampField;
        // Swamp water joined to the sea, which is not raised (MarkSwampSea).
        public bool[] m_swampSea;
        private const int SwampBlur = 3;
        // Rivers and streams from the world generator. The minimap's 12 m pixels lose the narrow
        // ones (a stream's channel is about 20 m wide), so they are drawn from their courses.
        public readonly List<RiverPath> m_rivers = new List<RiverPath>();

        private Color32[] m_colors;
        private Color32[] m_mask;
        private ushort[] m_halfHeights;
        private float[] m_floatHeights;
        private PaletteEntry[] m_palette;

        // A plain struct, not a tuple: tuples need System.ValueTuple.dll on .NET Framework 4.6.2,
        // which Valheim does not ship, and the type then fails to load in game.
        private struct PaletteEntry
        {
            public Color32 m_color;
            public Land m_land;

            public PaletteEntry(Color color, Land land)
            {
                m_color = color;
                m_land = land;
            }
        }

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
            WorldGenerator generator = WorldGenerator.instance;
            if (generator != null)
            {
                AddRivers(source, generator.GetRivers(), stream: false);
                AddRivers(source, generator.GetStreams(), stream: true);
            }
            // Vanilla paints the ocean plain white (Minimap.GetPixelColor); the other colours come
            // from the minimap prefab, which overrides the code defaults.
            source.m_palette = new[]
            {
                new PaletteEntry(minimap.m_meadowsColor, Land.Meadows),
                new PaletteEntry(minimap.m_blackforestColor, Land.BlackForest),
                new PaletteEntry(minimap.m_swampColor, Land.Swamp),
                new PaletteEntry(minimap.m_heathColor, Land.Plains),
                new PaletteEntry(s_mistlandsColor(minimap), Land.Mistlands),
                new PaletteEntry(minimap.m_ashlandsColor, Land.Ashlands),
                new PaletteEntry(minimap.m_mountainColor, Land.Mountain),
                new PaletteEntry(minimap.m_deepnorthColor, Land.DeepNorth),
                new PaletteEntry(Color.white, Land.Ocean),
            };
            return source;
        }

        private static void AddRivers(WorldSource source, List<WorldGenerator.River> rivers, bool stream)
        {
            if (rivers == null) return;
            foreach (WorldGenerator.River river in rivers)
            {
                if (river == null || river.curveWavelength <= 0f) continue;
                source.m_rivers.Add(new RiverPath
                {
                    m_start = river.p0,
                    m_end = river.p1,
                    m_curveWidth = river.curveWidth,
                    m_curveWavelength = river.curveWavelength,
                    m_width = (river.widthMin + river.widthMax) * 0.5f,
                    m_stream = stream,
                });
            }
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
            m_swampWet = new float[count];
            m_swampField = new float[count];
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
                if (land == Land.Swamp)
                {
                    // How wet the swamp is here, from its real height before the pools are
                    // raised: 1 in a pool, 0 a metre and a half above the water.
                    m_swampWet[i] = Mathf.Clamp01((m_waterLevel + SwampShallow - h) / SwampShallow);
                    m_swampField[i] = 1f;
                }
            }
            MarkSwampSea();
            for (int i = 0; i < count; i++)
                if (m_biome[i] == Land.Swamp && m_height[i] <= m_waterLevel && !m_swampSea[i]) m_height[i] = m_waterLevel + SwampFloor;
            for (int pass = 0; pass < SwampBlur; pass++)
            {
                Paper.Blur(m_swampWet, m_size);
                Paper.Blur(m_swampField, m_size);
            }
            m_colors = null;
            m_mask = null;
            m_halfHeights = null;
            m_floatHeights = null;
            m_wood = Woods.Build(this);
            m_lavaField = Woods.BuildLava(this);
        }

        // A swamp's water counts as its ground (pools traced exactly at the minimap's detail broke
        // every swamp into a maze of islets, and the user wants no pools), except its
        // deep water (over SwampShallow) joined to the sea: raising that too turned the flooded
        // fringe of swamps beside the sea into land, on Velen 3,889 raised cells touching other
        // water. The sea is flooded four-way from the map's edge through the water that stays
        // water, so a deep pool joined to the sea only across shallows is still raised.
        private void MarkSwampSea()
        {
            int count = m_size * m_size;
            m_swampSea = new bool[count];
            var sea = new bool[count];
            var queue = new int[count];
            int head = 0, tail = 0;
            for (int k = 0; k < m_size; k++)
            {
                Seed(k);
                Seed((m_size - 1) * m_size + k);
                Seed(k * m_size);
                Seed(k * m_size + m_size - 1);
            }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % m_size, z = i / m_size;
                if (x > 0) Seed(i - 1);
                if (x < m_size - 1) Seed(i + 1);
                if (z > 0) Seed(i - m_size);
                if (z < m_size - 1) Seed(i + m_size);
            }
            for (int i = 0; i < count; i++) m_swampSea[i] = sea[i] && m_biome[i] == Land.Swamp;

            void Seed(int j)
            {
                if (sea[j] || m_height[j] > m_waterLevel) return;
                if (m_biome[j] == Land.Swamp && m_height[j] > m_waterLevel - SwampShallow) return;
                sea[j] = true;
                queue[tail++] = j;
            }
        }

        // Whether world point (wx, wz) is swamp water joined to the sea (MarkSwampSea), which
        // stays water; close-ups ask it of the generator's finer heights.
        public bool IsSwampSea(float wx, float wz) => m_swampSea != null && m_swampSea[Index(wx, wz)];

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
            Color32 best = m_palette[0].m_color;
            int bestDistance = int.MaxValue;
            foreach (PaletteEntry entry in m_palette)
            {
                Color32 color = entry.m_color;
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
            foreach (PaletteEntry entry in m_palette)
                if (entry.m_land == land) return SameColor(entry.m_color, color);
            return false;
        }

        private Land First(Color32 color)
        {
            foreach (PaletteEntry entry in m_palette)
                if (SameColor(entry.m_color, color)) return entry.m_land;
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
