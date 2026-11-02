using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Hash-based randomness and value noise. Pure functions with no Unity calls, so they are
    /// safe on worker threads and give the same map for the same world seed on every machine.
    /// </summary>
    internal static class Noise
    {
        public static int HashInt(int x, int y, int seed)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695041);
                h = (h ^ (h >> 13)) * 1274126177u;
                return (int)(h ^ (h >> 16));
            }
        }

        // A random number in [0, 1) for slot k of a seed.
        public static float Unit(int seed, int k) => Hash(seed, k, 0x2545F491);

        public static float Hash(int x, int y, int seed) => (HashInt(x, y, seed) & 0xFFFFFF) / (float)0x1000000;

        // Smooth value noise in [0, 1].
        public static float Value(float x, float y, int seed)
        {
            int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
            float fx = x - x0, fy = y - y0;
            fx = fx * fx * (3f - 2f * fx);
            fy = fy * fy * (3f - 2f * fy);
            float a = Hash(x0, y0, seed), b = Hash(x0 + 1, y0, seed);
            float c = Hash(x0, y0 + 1, seed), d = Hash(x0 + 1, y0 + 1, seed);
            float top = a + (b - a) * fx;
            float bottom = c + (d - c) * fx;
            return top + (bottom - top) * fy;
        }
    }
}
