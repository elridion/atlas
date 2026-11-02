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
    /// wood field follows vanilla's forest mask (Minimap.GetMaskColor) as a continuous value.
    /// </summary>
    internal sealed class GeneratorSampler : IWorldSampler
    {
        // Vanilla's forest limits on its forest factor: Meadows woods under 1.15
        // (WorldGenerator.InForest), Plains under 0.8, Mistlands where the mask's smoothstep from
        // 1.1 to 1.3 is below half.
        private const float MeadowsForest = 1.15f;
        private const float PlainsForest = 0.8f;
        private const float MistlandsForest = 1.2f;

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
                case Land.BlackForest: wood = 1f; break;
                case Land.Meadows: wood = MeadowsForest - Factor(wx, wz); break;
                case Land.Plains: wood = PlainsForest - Factor(wx, wz); break;
                case Land.Mistlands: wood = MistlandsForest - Factor(wx, wz); break;
            }
        }

        private static float Factor(float wx, float wz) => WorldGenerator.GetForestFactor(new Vector3(wx, 0f, wz));

        private static Land ToLand(Heightmap.Biome biome)
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
        }
    }
}
