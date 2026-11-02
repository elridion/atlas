namespace Atlas
{
    /// <summary>
    /// Where the woods are, as one smooth field over vanilla's map pixels (1 deep in a wood, 0 in
    /// the open, the edge at 0.5), so the atlas can draw each wood as a shape with an edge rather
    /// than trees scattered point by point. It comes from vanilla's forest mask (red in Meadows,
    /// Black Forest and Plains, green in the Mistlands), cleaned of copses and clearings too small
    /// to draw, then softened so the traced edge runs as a curve instead of 12 m stairs.
    /// </summary>
    internal static class Woods
    {
        // Copses and clearings smaller than this many map pixels (12 m each) are dropped; 12 is a
        // patch about 40 m across.
        private const int MinWood = 12;
        private const int MinClearing = 16;
        private const int BlurPasses = 2;
        private const int MinLava = 110;
        private const int LavaBlurPasses = 4;

        // Worker thread, after WorldSource.Decode.
        public static float[] Build(WorldSource world)
        {
            int size = world.m_size;
            float water = world.m_waterLevel;
            var wood = new bool[size * size];
            var land = new bool[size * size];
            for (int i = 0; i < wood.Length; i++)
            {
                land[i] = world.m_height[i] > water;
                if (!land[i]) continue;
                Land biome = world.m_biome[i];
                if (biome == Land.Meadows || biome == Land.BlackForest || biome == Land.Plains) wood[i] = world.m_forest[i] > 127;
                else if (biome == Land.Mistlands) wood[i] = world.m_mist[i] > 127;
            }
            Clean(wood, land, size, value: true, MinWood);
            Clean(wood, land, size, value: false, MinClearing);

            var field = new float[size * size];
            for (int i = 0; i < field.Length; i++) field[i] = wood[i] ? 1f : 0f;
            for (int pass = 0; pass < BlurPasses; pass++) Paper.Blur(field, size);
            return field;
        }

        // The Ashlands' lava fields as a smooth field like the woods' (edge at 0.5), from vanilla's
        // lava mask (blue on Ashlands land), so they can be outlined and filled as shapes.
        public static float[] BuildLava(WorldSource world)
        {
            int size = world.m_size;
            var lava = new bool[size * size];
            var land = new bool[size * size];
            for (int i = 0; i < lava.Length; i++)
            {
                land[i] = world.m_height[i] > world.m_waterLevel;
                lava[i] = land[i] && world.m_biome[i] == Land.Ashlands && world.m_lava[i] > 127;
            }
            // Vanilla's lava is patchy, so fields are kept only from MinLava cells and blurred
            // more than woods, which joins near patches into one flow.
            Clean(lava, land, size, value: true, MinLava);
            Clean(lava, land, size, value: false, MinLava);
            var field = new float[size * size];
            for (int i = 0; i < field.Length; i++) field[i] = lava[i] ? 1f : 0f;
            for (int pass = 0; pass < LavaBlurPasses; pass++) Paper.Blur(field, size);
            return field;
        }

        // Flips connected patches of cells equal to value smaller than minCells. A clearing is only
        // filled on land, so a pond in a wood stays water; patches touching the map's edge stay.
        private static void Clean(bool[] wood, bool[] land, int size, bool value, int minCells)
        {
            var seen = new bool[size * size];
            var queue = new int[size * size];
            for (int start = 0; start < seen.Length; start++)
            {
                if (seen[start] || wood[start] != value) continue;
                int head = 0, tail = 0;
                bool edge = false;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int i = queue[head++];
                    int x = i % size, z = i / size;
                    if (x == 0 || z == 0 || x == size - 1 || z == size - 1) edge = true;
                    if (x > 0) Visit(i - 1);
                    if (x < size - 1) Visit(i + 1);
                    if (z > 0) Visit(i - size);
                    if (z < size - 1) Visit(i + size);
                }
                if (tail >= minCells || edge) continue;
                for (int k = 0; k < tail; k++)
                    if (value || land[queue[k]]) wood[queue[k]] = !value;

                void Visit(int j)
                {
                    if (seen[j] || wood[j] != value) return;
                    seen[j] = true;
                    queue[tail++] = j;
                }
            }
        }
    }
}
