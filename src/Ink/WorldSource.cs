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
        // Inland water patches mostly in the Mistlands under this many map pixels (32: about
        // 4,600 m², a pool some 70 m across) are filled to their lowest rim, at least
        // MistPoolFloor above the water, so the Mistlands' hundreds of little pools do not
        // clutter it with shores (MarkMistPools). On Velen 1,472 of its 1,499 inland patches are
        // smaller; the 27 left read as lakes.
        public const int MistPoolCells = 32;
        public const float MistPoolFloor = 0.5f;
        // Vanilla's Mountain firs (ZoneSystem vegetation firtree_mountain and
        // firtree_small_mountain): at most this many metres above the water, on ground no steeper
        // than this many degrees (m_maxAltitude 280, m_maxTilt 30).
        public const float MountainFirAltitude = 280f;
        public const float MountainFirTilt = 30f;
        // A Mountain wood is where most land within this many map pixels (2: 60 m across) is
        // fir ground (MarkMountainForest).
        private const int MountainFirWindow = 2;

        public int m_size;
        public float m_pixelSize;
        public float m_waterLevel;
        public int m_seed;

        public float[] m_height;
        public Land[] m_biome;
        // Vanilla's forest mask: red is forest in Meadows, Plains and Black Forest, green the
        // Mistlands forest, blue lava in the Ashlands (and the Ashlands sea gradient under water).
        // Vanilla's mask leaves the Deep North and the Mountains blank, so their red is filled in
        // from the rules their trees grow by (MarkNorthForest, MarkMountainForest).
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
        // The Mistlands pools raised to land (MarkMistPools): 1 on a raised pool's pixel, 2 on
        // the land round it that touches no water left as water, where the generator's finer
        // heights may still dip under the water line at the pool's rim.
        public byte[] m_mistPool;
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

        // Vanilla's forest factor at a world point (WorldGenerator.GetForestFactor, the same in
        // every world). A field so the offline preview, which has no engine for the
        // Mathf.PerlinNoise underneath, can put its own in.
        internal static Func<float, float, float> s_forestFactor = (wx, wz) => WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz));

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
            MarkNorthForest();
            MarkMountainForest();
            for (int i = 0; i < count; i++)
                if (m_biome[i] == Land.Swamp && m_height[i] <= m_waterLevel && !m_swampSea[i]) m_height[i] = m_waterLevel + SwampFloor;
            MarkMistPools();
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

        // A swamp's water counts as its ground (pools traced exactly at the minimap's detail break
        // every swamp into a maze of islets), except its deep water (over SwampShallow) joined to
        // the sea: raising that too turns the flooded fringe of swamps beside the sea into land,
        // on Velen 3,889 raised cells touching other water. The sea is flooded four-way from the map's edge through the water that stays
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

        // The Mistlands' small inland pools become their ground, so the tiny pools do not clutter
        // the biome with little shores. Velen has 1,499 inland water patches mostly in the
        // Mistlands, 799 of two pixels or more against the Plains' 432 and the Black Forest's
        // 294, and all but 27 are under MistPoolCells; those few larger lakes stay. Water joined
        // to the sea stays the sea. The sea is flooded four-way from the map's edge as
        // InkPainter.MarkSea floods it, with a lone land pixel counted as water since the painter
        // drops it (RemoveSpecks), so no inlet or channel of the painted sea is filled.
        private void MarkMistPools()
        {
            int count = m_size * m_size;
            m_mistPool = new byte[count];
            var sea = new bool[count];
            var seen = new bool[count];
            var queue = new int[count];
            int head = 0, tail = 0;
            // The lowest land round the patch being flooded.
            float rim = float.MaxValue;
            for (int k = 0; k < m_size; k++)
            {
                Sea(k);
                Sea((m_size - 1) * m_size + k);
                Sea(k * m_size);
                Sea(k * m_size + m_size - 1);
            }
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % m_size, z = i / m_size;
                if (x > 0) Sea(i - 1);
                if (x < m_size - 1) Sea(i + 1);
                if (z > 0) Sea(i - m_size);
                if (z < m_size - 1) Sea(i + m_size);
            }
            for (int start = 0; start < count; start++)
            {
                if (seen[start] || sea[start] || !IsWet(start)) continue;
                head = 0;
                tail = 0;
                seen[start] = true;
                queue[tail++] = start;
                int water = 0, mist = 0;
                rim = float.MaxValue;
                while (head < tail)
                {
                    int i = queue[head++];
                    if (m_height[i] <= m_waterLevel)
                    {
                        water++;
                        if (m_biome[i] == Land.Mistlands) mist++;
                    }
                    int x = i % m_size, z = i / m_size;
                    if (x > 0) Pool(i - 1);
                    if (x < m_size - 1) Pool(i + 1);
                    if (z > 0) Pool(i - m_size);
                    if (z < m_size - 1) Pool(i + m_size);
                }
                if (water >= MistPoolCells || 2 * mist <= water) continue;
                // Filled to its lowest rim, never under MistPoolFloor, so the pool leaves no hollow
                // for the summits, crags and mist that read the heights to find.
                float floor = Math.Max(m_waterLevel + MistPoolFloor, rim);
                for (int k = 0; k < tail; k++)
                {
                    int i = queue[k];
                    if (m_height[i] > m_waterLevel) continue;
                    m_height[i] = floor;
                    m_mistPool[i] = 1;
                }
            }
            // The rim: land beside a raised pool, unless it also touches water that stays water,
            // whose edge the generator's samples there belong to.
            int last = m_size - 1;
            for (int i = 0; i < count; i++)
            {
                if (m_mistPool[i] != 0 || m_height[i] <= m_waterLevel) continue;
                int x = i % m_size, z = i / m_size;
                bool pool = false, water = false;
                for (int nz = Math.Max(0, z - 1); nz <= Math.Min(last, z + 1); nz++)
                {
                    for (int nx = Math.Max(0, x - 1); nx <= Math.Min(last, x + 1); nx++)
                    {
                        int j = nz * m_size + nx;
                        if (m_mistPool[j] == 1) pool = true;
                        else if (m_height[j] <= m_waterLevel) water = true;
                    }
                }
                if (pool && !water) m_mistPool[i] = 2;
            }

            void Sea(int j)
            {
                if (sea[j] || !IsWet(j)) return;
                sea[j] = true;
                queue[tail++] = j;
            }

            void Pool(int j)
            {
                if (!IsWet(j))
                {
                    rim = Math.Min(rim, m_height[j]);
                    return;
                }
                if (seen[j] || sea[j]) return;
                seen[j] = true;
                queue[tail++] = j;
            }
        }

        // Water, or a lone pixel of land with water on all four sides, which the painter drops.
        private bool IsWet(int i)
        {
            if (m_height[i] <= m_waterLevel) return true;
            int x = i % m_size, z = i / m_size;
            if (x > 0 && m_height[i - 1] > m_waterLevel) return false;
            if (x < m_size - 1 && m_height[i + 1] > m_waterLevel) return false;
            if (z > 0 && m_height[i - m_size] > m_waterLevel) return false;
            if (z < m_size - 1 && m_height[i + m_size] > m_waterLevel) return false;
            return true;
        }

        // Whether world point (wx, wz) lies on a raised Mistlands pool or its rim
        // (MarkMistPools); close-ups raise the generator's finer heights there too.
        public bool IsMistPool(float wx, float wz) => m_mistPool != null && m_mistPool[Index(wx, wz)] != 0;

        // Vanilla grows every Deep North tree (snowy firs and pines, ZoneSystem's vegetation from
        // _LocationList_DeepNorth) only where the forest factor is under 1.15, the Meadows' own
        // forest limit, yet its minimap mask (Minimap.GetMaskColor) marks no forest there. So the
        // Deep North's land gets the same test here, on the map pixel's centre.
        private void MarkNorthForest()
        {
            int half = m_size / 2;
            for (int i = 0; i < m_biome.Length; i++)
            {
                if (m_biome[i] != Land.DeepNorth || m_height[i] <= m_waterLevel) continue;
                float wx = (i % m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                float wz = (i / m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                m_forest[i] = s_forestFactor(wx, wz) < GeneratorSampler.DeepNorthForest ? (byte)255 : (byte)0;
            }
        }

        // Vanilla's mask marks no forest in the Mountains either, yet firs grow there. ZoneSystem's
        // vegetation (firtree_mountain, 5-20 a zone, and firtree_small_mountain, 30 a zone) places
        // them on any Mountain ground from 0-2 m up to MountainFirAltitude above the water and no
        // steeper than MountainFirTilt, with no forest factor (m_inForest is off), so the rule
        // here is the ground's height and slope. The slope is measured across the neighbouring
        // map pixels (24 m), coarser than the terrain mesh vanilla tests, so some broken ground
        // passes that has no trees in game. Peaks keep their own ground: InkPainter keeps the
        // trees off their bases.
        private void MarkMountainForest()
        {
            float maxSlope = Mathf.Tan(MountainFirTilt * Mathf.Deg2Rad);
            int last = m_size - 1;
            var fir = new bool[m_biome.Length];
            for (int i = 0; i < m_biome.Length; i++)
            {
                if (m_biome[i] != Land.Mountain || m_height[i] <= m_waterLevel) continue;
                int x = i % m_size, z = i / m_size;
                float dx = m_height[z * m_size + Math.Min(last, x + 1)] - m_height[z * m_size + Math.Max(0, x - 1)];
                float dz = m_height[Math.Min(last, z + 1) * m_size + x] - m_height[Math.Max(0, z - 1) * m_size + x];
                float slope = Mathf.Sqrt(dx * dx + dz * dz) / (2f * m_pixelSize);
                fir[i] = m_height[i] - m_waterLevel < MountainFirAltitude && slope <= maxSlope;
            }
            // Gentle and steep ground lie mixed pixel by pixel over most of the Mountains (on
            // Velen about 45% of its land is gentle enough, nowhere much more over 600 m), which
            // drew as specks of trees among the peaks. A wood is where most of the land within
            // MountainFirWindow pixels carries firs, so the woods follow the gentle stretches and
            // the broken ground between stays open.
            int r = MountainFirWindow;
            for (int i = 0; i < m_biome.Length; i++)
            {
                if (m_biome[i] != Land.Mountain || m_height[i] <= m_waterLevel) continue;
                int x = i % m_size, z = i / m_size;
                int land = 0, firs = 0;
                for (int nz = Math.Max(0, z - r); nz <= Math.Min(last, z + r); nz++)
                {
                    for (int nx = Math.Max(0, x - r); nx <= Math.Min(last, x + r); nx++)
                    {
                        int j = nz * m_size + nx;
                        if (m_height[j] <= m_waterLevel) continue;
                        land++;
                        if (fir[j]) firs++;
                    }
                }
                m_forest[i] = 2 * firs > land ? (byte)255 : (byte)0;
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

        public Land LandAt(float wx, float wz) => m_biome[Index(wx, wz)];

        // Water as the sheet washes it: swamp pools away from the sea and the Mistlands' raised
        // pools read as ground.
        public bool IsWaterAt(float wx, float wz)
        {
            if (HeightAt(wx, wz) > m_waterLevel || IsMistPool(wx, wz)) return false;
            return LandAt(wx, wz) != Land.Swamp || IsSwampSea(wx, wz);
        }

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
