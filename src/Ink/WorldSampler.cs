using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The ground at any world point, for detail tiles: height, biome and a wood field (positive
    /// inside a wood, the edge at 0). Called from worker threads.
    /// </summary>
    internal interface IWorldSampler
    {
        void Sample(float wx, float wz, out float height, out Land land, out float wood);
    }

    /// <summary>
    /// Samples the world generator itself, so a detail tile is as fine as the tile needs, not
    /// limited to the minimap's 12 m pixels. These are the calls vanilla's minimap is built from
    /// (Minimap.GenerateWorldMap), and vanilla makes them off the main thread too, from its
    /// terrain build thread (HeightmapBuilder); the river cache they read is behind a lock. The
    /// wood field follows vanilla's forest mask (Minimap.GetMaskColor) as a continuous value, and
    /// in the Deep North, which the mask leaves out, the forest factor its trees grow by; in the
    /// Mountains, which it leaves out too, the sheet's own wood field.
    /// </summary>
    internal sealed class GeneratorSampler : IWorldSampler
    {
        // Vanilla's forest limits on its forest factor: Meadows woods under 1.15
        // (WorldGenerator.InForest), Plains under 0.8. The Mistlands' mask (a smoothstep from 1.1
        // to 1.3) is the mist area, not woods, so all their land is wood.
        private const float MeadowsForest = 1.15f;
        private const float PlainsForest = 0.8f;
        // Every Deep North tree grows under this forest factor (ZoneSystem's vegetation from
        // _LocationList_DeepNorth: snowy firs and pines, forest threshold 0 to 1.15), though
        // vanilla's mask marks no forest there. Its woods stop below the peaks' ground, falling
        // by NorthHeightFall a metre towards it, so the edge lies where the sheet's does.
        public const float DeepNorthForest = 1.15f;
        private const float NorthHeightFall = 0.01f;

        private readonly WorldGenerator m_generator;
        private readonly float m_waterLevel;
        private readonly WorldSource m_world;

        // Main thread.
        public GeneratorSampler(WorldGenerator generator, WorldSource world)
        {
            m_generator = generator;
            m_world = world;
            m_waterLevel = world.m_waterLevel;
        }

        public void Sample(float wx, float wz, out float height, out Land land, out float wood)
        {
            Heightmap.Biome biome = m_generator.GetBiome(wx, wz);
            height = m_generator.GetBiomeHeight(biome, wx, wz, out Color _);
            land = ToLand(biome);
            // As on the sheet (WorldSource.MarkSwampSea): a swamp's water is its ground, except deep
            // water joined to the sea.
            if (land == Land.Swamp && height <= m_waterLevel && !(height <= m_waterLevel - WorldSource.SwampShallow && m_world.IsSwampSea(wx, wz)))
                height = m_waterLevel + WorldSource.SwampFloor;
            wood = -1f;
            if (height <= m_waterLevel) return;
            switch (land)
            {
                // A felled Black Forest is open land, as on the sheet (InkPainter.BuildFields).
                case Land.BlackForest: wood = m_world.GroveAt(wx, wz, land) == Grove.ClearCut ? -1f : 1f; break;
                case Land.Meadows: wood = MeadowsForest - Factor(wx, wz); break;
                case Land.Plains: wood = PlainsForest - Factor(wx, wz); break;
                // All Mistlands land is wood, as on the sheet (Woods.Build).
                case Land.Mistlands: wood = 1f; break;
                case Land.DeepNorth:
                    float below = InkPainter.DeepNorthPeakHeight - (height - m_waterLevel);
                    wood = Mathf.Min(DeepNorthForest - Factor(wx, wz), below * NorthHeightFall);
                    break;
                // The Mountains' firs follow the ground's slope (WorldSource.MarkMountainForest),
                // which one generator sample cannot tell; worked out here on the generator's
                // finer heights it would break the woods into the copses and clearings the sheet
                // dropped. So close-ups take the sheet's own field, and their edge lies where the
                // sheet's does.
                case Land.Mountain: wood = m_world.Sample(m_world.m_wood, wx, wz) - 0.5f; break;
            }
        }

        private static float Factor(float wx, float wz) => WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz));

        internal static Land ToLand(Heightmap.Biome biome)
        {
            switch (biome)
            {
                case Heightmap.Biome.Meadows: return Land.Meadows;
                case Heightmap.Biome.BlackForest: return Land.BlackForest;
                case Heightmap.Biome.Swamp: return Land.Swamp;
                case Heightmap.Biome.Mountain: return Land.Mountain;
                case Heightmap.Biome.Plains: return Land.Plains;
                case Heightmap.Biome.Mistlands: return Land.Mistlands;
                case Heightmap.Biome.AshLands: return Land.Ashlands;
                case Heightmap.Biome.DeepNorth: return Land.DeepNorth;
                default: return Land.Ocean;
            }
        }
    }

    /// <summary>
    /// Samples the decoded minimap (12 m pixels, interpolated): the fallback when the generator
    /// cannot be used, and what the offline preview runs on.
    /// </summary>
    internal sealed class MinimapSampler : IWorldSampler
    {
        private readonly WorldSource m_world;

        public MinimapSampler(WorldSource world) => m_world = world;

        public void Sample(float wx, float wz, out float height, out Land land, out float wood)
        {
            height = m_world.HeightAt(wx, wz);
            land = m_world.m_biome[m_world.Index(wx, wz)];
            wood = height > m_world.m_waterLevel ? m_world.Sample(m_world.m_wood, wx, wz) - 0.5f : -1f;
            if (land == Land.BlackForest && m_world.GroveAt(wx, wz, land) == Grove.ClearCut) wood = -1f;
        }
    }
}
