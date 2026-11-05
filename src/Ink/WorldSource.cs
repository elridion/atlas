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
    /// What a world's alt biome (Valheim 1.0's variants of one connected biome patch, chosen once
    /// per world from the seed) does to that patch's woods on the atlas. Vanilla's variants add or
    /// block vegetation only; they never change the terrain, the ground texture or the minimap, so
    /// the wood shapes stay and only the trees in them thin out, thicken or go.
    /// </summary>
    internal enum Grove : byte
    {
        None,
        // Felled: no trees, stumps instead (Kalhygge Black Forest).
        ClearCut,
        // Open boulder ground: the big trees blocked, big boulders and a few lone pines instead
        // (Rock Black Forest).
        Rocky,
        // A third of the trees (vanilla swaps the patch's big trees for fewer others).
        Thin,
        // Open grazing land, about a third of the trees (Lox Plains; the user's call, sparser
        // than vanilla, which blocks one of the Plains' two birches).
        Grazed,
        // Half the trees (vanilla blocks or swaps the patch's big trees).
        Sparse,
        // Three quarters of the trees (vanilla blocks one of the patch's big trees).
        Light,
        // As many trees, most of them small (vanilla swaps the big beeches for small ones).
        Smalltree,
        // More trees (vanilla adds trees to the patch). Kept last: SetGroves sizes by it.
        Dense,
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
        // How far above the water a swamp's ground is dry (m_swampWet 0; it is 1 at SwampShallow
        // below). The marsh surface tops out about 4 m up and two thirds of it lie under water,
        // so wetness from the water line up left it at 1 over most of the swamp.
        public const float SwampDry = 1f;
        // The swamp's ancient trees (swamptree1 and 2, about 50-60 a zone) grow from this many
        // metres below the water up (their minimum altitude), so the atlas's dead trees stand on
        // ground from there up (m_swampGround).
        public const float SwampTreeLine = -0.5f;
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
        // The map pixels the Meadows' copses too small for the field stand a tree on (Woods.Build).
        public int[] m_copses;
        // The Ashlands' lava fields as a smooth field, edge at 0.5 (Woods.BuildLava).
        public float[] m_lavaField;
        // How hot the Ashlands' boiling sea is (0 to 1): vanilla's own ocean gradient, which its
        // mask keeps in blue under water and which tints vanilla's map there, averaged over water
        // only and softened (BuildAshSea), so it fades out smoothly at the biome's edge and never
        // takes a lava field's blue from the land beside it.
        public float[] m_ashSea;
        private const int AshSeaBlur = 6;
        // The swamp's wetness (1 in its pools, 0 on its dry ground, from the heights before the
        // shallow pools are raised) and the swamp itself (1 inside, 0 outside, edge at 0.5), both
        // softened by SwampBlur passes, so marks gather round the real pools and thin out at the
        // swamp's edge.
        public float[] m_swampWet;
        public float[] m_swampField;
        // Swamp ground the ancient trees grow on (SwampTreeLine and up, from the heights before
        // the pools are raised), where the swamp's dead trees stand (InkPainter.PlaceDeadTrees).
        public bool[] m_swampGround;
        // Swamp water joined to the sea, which is not raised (MarkSwampSea).
        public bool[] m_swampSea;
        // The Deep North's own water, where the game sets its ice (MarkIce), grown by IceReach map
        // pixels so a floe may lie half over the open sea beside a narrow fringe.
        public bool[] m_iceWater;
        // The Deep North's lakes (water not joined to the map's edge), 1 in a lake, 0 elsewhere,
        // which take a lighter water glaze (Paper.WaterColor).
        public float[] m_northLake;
        // Vanilla's GetBiome gives the Ocean before the Deep North wherever the base height is low,
        // so the open northern sea is Ocean, at most about 4 m above the sea floor's zero, and the
        // Deep North's own water (its shallow coast, the north side of the border moat, its lakes)
        // stands at 23 m and more before CreateDeepNorthGap pulls the moat under; nothing lies
        // between, so IceFloor (metres, absolute) splits them on the height over the gap.
        private const float IceFloor = 14f;
        // The Deep North's summits and hilltops in sheet canvas points, filled once on first use
        // by InkPainter.NorthTops (under a lock on this object).
        public List<Vector2> m_northTops;
        private const int IceReach = 4;
        private const int SwampBlur = 3;
        // Rivers and streams from the world generator. The minimap's 12 m pixels lose the narrow
        // ones (a stream's channel is about 20 m wide), so they are drawn from their courses.
        public readonly List<RiverPath> m_rivers = new List<RiverPath>();
        // Per map pixel, what its biome patch's alt biome does to its trees (GroveOf); null when
        // vanilla's alt-biome data was not ready at capture, which draws every wood as vanilla's
        // plain biome.
        public Grove[] m_grove;
        private bool[] m_hasGrove;

        // The world generator whose biome sectors are read on the worker (ReadGroves), set only
        // once its alt-biome data is built.
        private WorldGenerator m_altBiomes;
        private static bool s_altBiomesMissing;

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
            source.m_altBiomes = AltBiomesReady(generator) ? generator : null;
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

        // Vanilla builds the alt biomes when the world loads (AltBiomeWorldData.VerifyBiomeData, on
        // the server's world load and on a client's connect), before the minimap is drawn; until
        // then GetBiomeSector answers with empty placeholder sectors. Main thread.
        private static bool AltBiomesReady(WorldGenerator generator)
        {
            AltBiomeWorldData data = generator?.m_world?.m_biomeData;
            if (data != null && data.IsReady) return true;
            if (!s_altBiomesMissing)
            {
                s_altBiomesMissing = true;
                Jotunn.Logger.LogWarning("The atlas found no alt-biome data for this world; its woods are drawn without their variants.");
            }
            return false;
        }

        private static void AddRivers(WorldSource source, List<WorldGenerator.River> rivers, bool stream)
        {
            if (rivers == null) return;
            foreach (WorldGenerator.River river in rivers)
            {
                if (river == null || river.curveWavelength <= 0f) continue;
                // Vanilla carves its streams with RenderRivers(..., SkipDeepNorth), which skips every
                // stream whose mouth (p0) lies in the Deep North, yet GetStreams still returns them;
                // the Deep North's carved streams come from a second pass whose list is not kept.
                // So a stream with a Deep North mouth has no bed. Rivers are carved everywhere.
                if (stream && WorldGenerator.IsDeepnorth(river.p0.x, river.p0.y)) continue;
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
            m_swampGround = new bool[count];
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
                    // raised: 1 in water SwampShallow deep, 0 SwampDry above the water.
                    m_swampWet[i] = Mathf.Clamp01((m_waterLevel + SwampDry - h) / (SwampDry + SwampShallow));
                    m_swampField[i] = 1f;
                    m_swampGround[i] = h >= m_waterLevel + SwampTreeLine;
                }
            }
            if (m_altBiomes != null) ReadGroves();
            MarkSwampSea();
            BuildAshSea();
            MarkNorthForest();
            MarkMountainForest();
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

        // The boiling sea's heat (m_ashSea). Only the Ashlands' and the open ocean's water count:
        // the gradient runs on a little past the Ashlands into the ocean, while the odd swamp or
        // forest pool with blue in its mask would leave a red speck. Water and value are blurred
        // apart and divided, so the shore takes the water's heat, not the lava's.
        private void BuildAshSea()
        {
            int count = m_size * m_size;
            var heat = new float[count];
            var water = new float[count];
            for (int i = 0; i < count; i++)
            {
                if (m_height[i] > m_waterLevel) continue;
                water[i] = 1f;
                if (m_biome[i] == Land.Ashlands || m_biome[i] == Land.Ocean) heat[i] = m_lava[i] / 255f;
            }
            for (int pass = 0; pass < AshSeaBlur; pass++)
            {
                Paper.Blur(heat, m_size);
                Paper.Blur(water, m_size);
            }
            for (int i = 0; i < count; i++) heat[i] = water[i] > 0.01f ? Mathf.Clamp01(heat[i] / water[i]) : 0f;
            m_ashSea = heat;
        }

        // Each map pixel's biome sector, read once per world. Vanilla's sector grid is the minimap's
        // (2048 pixels of 12 m, centres at (i - 1024) * 12 + 6), so the pixel centre names its
        // sector. Reading it off the main thread is safe: GetBiomeSector only reads the sector
        // array and each sector's AltBiomes list, both built at world load and left alone after.
        // Sectors are connected patches, so the last one's grove is reused while it repeats.
        private void ReadGroves()
        {
            var groves = new Grove[m_biome.Length];
            int half = m_size / 2;
            BiomeSector last = null;
            Grove lastGrove = Grove.None;
            for (int i = 0; i < groves.Length; i++)
            {
                float wx = (i % m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                float wz = (i / m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                BiomeSector sector = m_altBiomes.GetBiomeSector(wx, wz);
                if (sector != last)
                {
                    last = sector;
                    lastGrove = Grove.None;
                    if (sector != null)
                    {
                        foreach (AltBiome alt in sector.AltBiomes)
                        {
                            // Felling outranks boulders, boulders thinning, thinning small trees,
                            // small trees thickening; vanilla's
                            // incompatibilities keep the others from meeting in one patch.
                            if (alt == null || !GroveOf(alt.m_name, out Land land, out Grove grove)) continue;
                            if (land != GeneratorSampler.ToLand(sector.Biome)) continue;
                            if (lastGrove == Grove.None || grove < lastGrove) lastGrove = grove;
                        }
                    }
                }
                // The decoded biome decides: a pixel decoded as another biome than its sector's
                // (the minimap's colours and heights against the generator's own biome) keeps
                // plain woods, so no grove applies outside its biome.
                if (lastGrove != Grove.None && m_biome[i] == GeneratorSampler.ToLand(sector.Biome)) groves[i] = lastGrove;
            }
            SetGroves(groves);
            m_altBiomes = null;
        }

        // Keeps the groves and notes which kinds occur, so a world without them skips their extra
        // scatter passes. The offline preview calls it with groves of its own (--alt).
        internal void SetGroves(Grove[] groves)
        {
            var has = new bool[(int)Grove.Dense + 1];
            foreach (Grove grove in groves) has[(int)grove] = true;
            m_grove = groves;
            m_hasGrove = has;
        }

        public bool HasGrove(Grove grove) => m_hasGrove != null && m_hasGrove[(int)grove];

        // The alt biomes that change their patch's trees, by vanilla's AltBiome.m_name, with the
        // biome they belong to; the rest (creatures, locations, small plants) leave the atlas
        // as it is.
        public static bool GroveOf(string name, out Land land, out Grove grove)
        {
            switch (name)
            {
                // A felled forest (kalhygge, a clear-cut).
                case "Kalhygge Black Forest": land = Land.BlackForest; grove = Grove.ClearCut; return true;
                case "Pinetree Black Forest": land = Land.BlackForest; grove = Grove.Sparse; return true;
                // Vanilla blocks firtree_many_blackforest, Pinetree_01_blackforest and
                // firtree_blackforest (about 105 big trees a zone down to firtree_blackforest2's
                // 1-2) and adds rock_4_blackforest 30, rock_3_blackforest 15 (scale 3-6) and
                // rock4_blackforest 3 a zone: a boulder field with saplings, not a wood.
                case "Rock Black Forest": land = Land.BlackForest; grove = Grove.Rocky; return true;
                // Vanilla blocks beech1_meadows (40 a zone), birch1 and birch2 and adds 100
                // beech_small3/4_meadows and bushes, keeping only beech2_meadows (5-10) and the oaks
                // of the big trees: as many trees, about a sixth of them big.
                case "Smalltree Meadows": land = Land.Meadows; grove = Grove.Smalltree; return true;
                // Vanilla blocks beech1_meadows and adds birch1/2_meadows2 (10 + 5-9),
                // beech5_meadows (10) and oak2_meadows (1-2): about three quarters of the big
                // trees. Its birches also grow on ground the minimap shows open; not drawn.
                case "Birch Meadows": land = Land.Meadows; grove = Grove.Light; return true;
                // Vanilla blocks the big firtree_mountain; the small firs stay.
                case "Drake Mountain":
                case "Fortress Mountain": land = Land.Mountain; grove = Grove.Sparse; return true;
                // Adds 10-20 firtree_mountain2 a zone.
                case "Wolf Mountain": land = Land.Mountain; grove = Grove.Dense; return true;
                // Vanilla blocks birch2_aut_heath (10 a zone); birch1_aut_plains (30) stays.
                case "Death Plains": land = Land.Plains; grove = Grove.Light; return true;
                // The same in vanilla, drawn sparser as open grazing land (the user's call).
                case "Lox Plains": land = Land.Plains; grove = Grove.Grazed; return true;
                // Vanilla blocks swamptree1 and 2 (about 55 ancient trees a zone) and adds
                // swamptree5 and 6 (about 18) and another 60 small dead firs: a third of the
                // ancient trees.
                case "Bog Swamp": land = Land.Swamp; grove = Grove.Thin; return true;
                // Vanilla blocks swamptree1 and 2 and adds swamptree3 (20) and 4 (7): about half.
                case "Hut Swamp": land = Land.Swamp; grove = Grove.Sparse; return true;
                case "Trees Mistlands": land = Land.Mistlands; grove = Grove.Dense; return true;
                default: land = Land.Ocean; grove = Grove.None; return false;
            }
        }

        // What the alt biome does to the trees at world point (wx, wz) of the given biome. Close-ups
        // sample the generator's finer biome border, which runs up to a map pixel off the
        // minimap's, so a point whose nearest pixel is another biome takes the nearest pixel of
        // its own biome round it; otherwise a fringe of plain woods would line a clear-cut.
        public Grove GroveAt(float wx, float wz, Land land)
        {
            if (m_grove == null) return Grove.None;
            float sx = ToSourceX(wx), sz = ToSourceZ(wz);
            int x = Mathf.Clamp((int)Math.Round(sx), 0, m_size - 1);
            int z = Mathf.Clamp((int)Math.Round(sz), 0, m_size - 1);
            int i = z * m_size + x;
            if (m_biome[i] == land) return m_grove[i];
            Grove found = Grove.None;
            float best = float.MaxValue;
            for (int nz = Math.Max(0, z - 1); nz <= Math.Min(m_size - 1, z + 1); nz++)
            {
                for (int nx = Math.Max(0, x - 1); nx <= Math.Min(m_size - 1, x + 1); nx++)
                {
                    int j = nz * m_size + nx;
                    if (m_biome[j] != land) continue;
                    float d = (nx - sx) * (nx - sx) + (nz - sz) * (nz - sz);
                    if (d >= best) continue;
                    best = d;
                    found = m_grove[j];
                }
            }
            return found;
        }

        // Whether a grove leaves its wood open land: a felled Black Forest or a boulder field, whose
        // big trees the game blocks, keeps the wood's shape and wash but no canvas wood.
        public static bool IsOpen(Grove grove) => grove == Grove.ClearCut || grove == Grove.Rocky;

        // Whether world point (wx, wz) lies in an open grove (IsOpen), which counts as open land.
        public bool IsOpenGrove(float wx, float wz) => m_grove != null && IsOpen(m_grove[Index(wx, wz)]);

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
            MarkIce(sea);

            void Seed(int j)
            {
                if (sea[j] || m_height[j] > m_waterLevel) return;
                if (m_biome[j] == Land.Swamp && m_height[j] > m_waterLevel - SwampShallow) return;
                sea[j] = true;
                queue[tail++] = j;
            }
        }

        // The Deep North's ice water and lakes (m_iceWater, m_northLake), from the heights before
        // any swamp is raised and the water flooded from the map's edge (sea). The game's ice
        // (ice1_deepnorth, ice_rock1_deepnorth) is Deep North vegetation only; the open northern
        // sea is Ocean and has none.
        private void MarkIce(bool[] sea)
        {
            int count = m_size * m_size, half = m_size / 2;
            var ice = new bool[count];
            m_northLake = new float[count];
            for (int i = 0; i < count; i++)
            {
                float h = m_height[i];
                if (h > m_waterLevel) continue;
                float wx = (i % m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                float wz = (i / m_size - half) * m_pixelSize + m_pixelSize * 0.5f;
                if (!WorldGenerator.IsDeepnorth(wx, wz)) continue;
                if (!sea[i]) m_northLake[i] = 1f;
                // Right on the border the gap pulls both kinds of water to nothing.
                double gap = WorldGenerator.CreateDeepNorthGap(wx, wz);
                if (gap > 1e-3 && h / gap >= IceFloor) ice[i] = sea[i];
            }
            // Grown a square of IceReach either way, rows then columns.
            var rows = new bool[count];
            m_iceWater = new bool[count];
            for (int z = 0; z < m_size; z++)
                for (int x = 0; x < m_size; x++)
                    for (int d = -IceReach; d <= IceReach; d++)
                    {
                        int xx = x + d;
                        if (xx >= 0 && xx < m_size && ice[z * m_size + xx]) { rows[z * m_size + x] = true; break; }
                    }
            for (int z = 0; z < m_size; z++)
                for (int x = 0; x < m_size; x++)
                    for (int d = -IceReach; d <= IceReach; d++)
                    {
                        int zz = z + d;
                        if (zz >= 0 && zz < m_size && rows[zz * m_size + x]) { m_iceWater[z * m_size + x] = true; break; }
                    }
        }

        public bool IsIceWater(float wx, float wz) => m_iceWater != null && m_iceWater[Index(wx, wz)];

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

        // Vanilla carves rivers and streams into every biome's heights but the Ashlands'
        // (WorldGenerator.GetAshlandsHeight never calls AddRivers), so a course that crosses the
        // Ashlands has no bed there and is not drawn. Takes world metres, y the world z axis.
        public bool HasRiverBed(Vector2 world) => LandAt(world.x, world.y) != Land.Ashlands;

        // Water as the sheet washes it: swamp pools away from the sea read as ground.
        public bool IsWaterAt(float wx, float wz)
        {
            if (HeightAt(wx, wz) > m_waterLevel) return false;
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

        // The lowest ground within radius map pixels (a disc), never below the water: a pool's bed
        // is no floor for the ground round it.
        public float FloorAt(float wx, float wz, int radius)
        {
            int i = Index(wx, wz);
            int x0 = i % m_size, z0 = i / m_size;
            float floor = float.MaxValue;
            for (int dz = -radius; dz <= radius; dz++)
            {
                int z = z0 + dz;
                if (z < 0 || z >= m_size) continue;
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int x = x0 + dx;
                    if (x < 0 || x >= m_size || dx * dx + dz * dz > radius * radius) continue;
                    floor = Math.Min(floor, m_height[z * m_size + x]);
                }
            }
            return Math.Max(floor, m_waterLevel);
        }

        // Whether most land map pixels within radius (a square) round the point are at least
        // maxSlope steep: broken ground, not a lone steep pixel or a valley's thin bank.
        public bool MostlySteep(float wx, float wz, int radius, float maxSlope)
        {
            int i = Index(wx, wz);
            int x0 = i % m_size, z0 = i / m_size, land = 0, steep = 0;
            for (int z = Math.Max(1, z0 - radius); z <= Math.Min(m_size - 2, z0 + radius); z++)
                for (int x = Math.Max(1, x0 - radius); x <= Math.Min(m_size - 2, x0 + radius); x++)
                {
                    int j = z * m_size + x;
                    if (m_height[j] <= m_waterLevel) continue;
                    land++;
                    float dx = m_height[j + 1] - m_height[j - 1], dz = m_height[j + m_size] - m_height[j - m_size];
                    if (Mathf.Sqrt(dx * dx + dz * dz) / (2f * m_pixelSize) >= maxSlope) steep++;
                }
            return steep * 2 > land;
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
