using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Atlas
{
    /// <summary>
    /// The ground sampled for detail tiles, kept in world-anchored chunks of ChunkSamples squared
    /// samples at a few fixed spacings (1, 2, 4, 8, 16 m), so panning and zooming back reuse what
    /// earlier tiles sampled instead of asking the world generator again; only chunks never seen
    /// are sampled, in parallel. Shared by tile tasks, hence the lock; chunks are immutable once in.
    /// </summary>
    internal sealed class SampleCache
    {
        public const int ChunkSamples = 32;
        public const int Levels = 5;
        // About 37 KB a chunk; the least recently used go once the cache holds more than this.
        private const int Capacity = 1200;

        public sealed class Chunk
        {
            public readonly float[] m_height = new float[ChunkSamples * ChunkSamples];
            public readonly Land[] m_land = new Land[ChunkSamples * ChunkSamples];
            public readonly float[] m_wood = new float[ChunkSamples * ChunkSamples];
            public long m_used;
        }

        private readonly IWorldSampler m_sampler;
        private readonly Dictionary<long, Chunk> m_chunks = new Dictionary<long, Chunk>();
        private readonly object m_lock = new object();
        private long m_clock;

        public SampleCache(IWorldSampler sampler) => m_sampler = sampler;

        public IWorldSampler Sampler => m_sampler;

        // Metres between samples at a level.
        public static float Spacing(int level) => 1 << level;

        private static long Key(int level, int cx, int cz) =>
            ((long)level << 48) | ((long)(cx + 0x800000) << 24) | (long)(cz + 0x800000);

        // Fetches the chunks covering sample indices x0..x1, z0..z1 (world x = index * spacing),
        // sampling the missing ones first. Chunk (cx, cz) holds indices cx * ChunkSamples onward.
        public Dictionary<long, Chunk> Fetch(int level, int x0, int x1, int z0, int z1, int threads, CancellationToken cancel)
        {
            int cx0 = FloorDiv(x0), cx1 = FloorDiv(x1), cz0 = FloorDiv(z0), cz1 = FloorDiv(z1);
            var found = new Dictionary<long, Chunk>();
            var missing = new List<long>();
            var missingAt = new List<int>();
            lock (m_lock)
            {
                long now = ++m_clock;
                for (int cz = cz0; cz <= cz1; cz++)
                {
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        long key = Key(level, cx, cz);
                        if (m_chunks.TryGetValue(key, out Chunk chunk))
                        {
                            chunk.m_used = now;
                            found[key] = chunk;
                        }
                        else
                        {
                            missing.Add(key);
                            missingAt.Add(cx);
                            missingAt.Add(cz);
                        }
                    }
                }
            }
            if (missing.Count == 0) return found;
            var made = new Chunk[missing.Count];
            float spacing = Spacing(level);
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, threads), CancellationToken = cancel };
            Parallel.For(0, missing.Count, options, k =>
            {
                var chunk = new Chunk();
                int cx = missingAt[k * 2], cz = missingAt[k * 2 + 1];
                for (int z = 0; z < ChunkSamples; z++)
                {
                    float wz = (cz * ChunkSamples + z) * spacing;
                    for (int x = 0; x < ChunkSamples; x++)
                    {
                        float wx = (cx * ChunkSamples + x) * spacing;
                        int i = z * ChunkSamples + x;
                        m_sampler.Sample(wx, wz, out chunk.m_height[i], out chunk.m_land[i], out chunk.m_wood[i]);
                    }
                }
                made[k] = chunk;
            });
            lock (m_lock)
            {
                long now = ++m_clock;
                for (int k = 0; k < missing.Count; k++)
                {
                    made[k].m_used = now;
                    m_chunks[missing[k]] = made[k];
                    found[missing[k]] = made[k];
                }
                if (m_chunks.Count > Capacity) Trim();
            }
            return found;
        }

        // Drops the least recently used quarter. Under the lock.
        private void Trim()
        {
            var ages = new List<long>(m_chunks.Count);
            foreach (Chunk chunk in m_chunks.Values) ages.Add(chunk.m_used);
            ages.Sort();
            long cutoff = ages[ages.Count / 4];
            var old = new List<long>();
            foreach (KeyValuePair<long, Chunk> entry in m_chunks)
                if (entry.Value.m_used <= cutoff) old.Add(entry.Key);
            foreach (long key in old) m_chunks.Remove(key);
        }

        public static int FloorDiv(int index) => index >= 0 ? index / ChunkSamples : (index + 1) / ChunkSamples - 1;

        public static long ChunkKey(int level, int index, int zIndex) => Key(level, FloorDiv(index), FloorDiv(zIndex));
    }
}
