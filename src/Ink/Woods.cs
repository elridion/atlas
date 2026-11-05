namespace Atlas
{
    /// <summary>
    /// Where the woods are, as one smooth field over vanilla's map pixels (1 deep in a wood, 0 in
    /// the open, the edge at 0.5), so the atlas can draw each wood as a shape with an edge rather
    /// than trees scattered point by point. It comes from vanilla's forest mask (red in Meadows,
    /// Black Forest and Plains, the forest factor in the Deep North and height and slope in the
    /// Mountains, which the mask leaves out; all Mistlands land, whose green is the mist area,
    /// not woods), cleaned of copses and clearings too small
    /// to draw, then softened so the traced edge runs as a curve instead of 12 m stairs.
    /// </summary>
    internal static class Woods
    {
        // Copses and clearings smaller than this many map pixels (12 m each) are dropped; 12 is a
        // patch about 40 m across.
        private const int MinWood = 12;
        private const int MinClearing = 16;
        private const int BlurPasses = 2;
        // Lava patches and the islands of ground between them under this many map pixels are
        // dropped, as small as the woods' copses: vanilla's lava is veined (a cellular crackle
        // over large fBm patches, WorldGenerator.GetAshlandsHeight), so at 110 cells and four
        // blurs the atlas kept only about 5% of the Ashlands as lava where the game has 18%.
        private const int MinLava = 12;
        private const int LavaBlurPasses = 1;
        // The mask's lava weight (0-255) from which ground is drawn as lava: 0.2, where the game
        // stops growing its Ashlands trees (tree_ashlands1-4 take only a vegetation mask up to
        // 0.2). It burns from 0.6 (Heightmap.IsLava), but the ground between is the lava fields'
        // crust and veins; at 0.5 and two blurs the atlas drew about 15% of demoo's Ashlands as
        // lava, where the game's lava ground is about a quarter.
        private const int LavaWeight = 51;
        // The large fields keep the old reading (over 0.5), so the crags stand where they did.
        private const int MajorLavaWeight = 127;
        // The large lava fields alone, which crags keep their bases off (InkPainter.MajorLavaAt):
        // with every pool counted, the Ashlands' high ground had little room left for a base.
        // Their bodies cover the pools under them (Glyphs.AshBody).
        private const int MajorLava = 110;
        private const int MajorLavaBlurPasses = 4;

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
                // The Mountains' red is their firs' ground (WorldSource.MarkMountainForest).
                if (biome == Land.Meadows || biome == Land.BlackForest || biome == Land.Plains || biome == Land.Mountain)
                    wood[i] = world.m_forest[i] > 127;
                // The Mistlands' green is the mist area, not woods; the Yggdrasil shoots grow over
                // all its land, most of them whatever the forest factor (ZoneSystem's vegetation).
                else if (biome == Land.Mistlands) wood[i] = true;
                // The Deep North's forest (WorldSource.MarkNorthForest), up its gentle uplands too.
                else if (biome == Land.DeepNorth) wood[i] = world.m_forest[i] > 127;
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
        public static float[] BuildLava(WorldSource world) => BuildLava(world, LavaWeight, MinLava, LavaBlurPasses);

        // The large lava fields only, as a field like BuildLava's.
        public static float[] BuildMajorLava(WorldSource world) => BuildLava(world, MajorLavaWeight, MajorLava, MajorLavaBlurPasses);

        private static float[] BuildLava(WorldSource world, int weight, int minCells, int blurPasses)
        {
            int size = world.m_size;
            var lava = new bool[size * size];
            var land = new bool[size * size];
            for (int i = 0; i < lava.Length; i++)
            {
                land[i] = world.m_height[i] > world.m_waterLevel;
                lava[i] = land[i] && world.m_biome[i] == Land.Ashlands && world.m_lava[i] > weight;
            }
            // Only specks and pinholes too small to draw are dropped, then blurred once, so the
            // traced edge runs as a curve instead of 12 m stairs while veins a pixel or two wide
            // survive (two blurs melted them below the edge).
            Clean(lava, land, size, value: true, minCells);
            Clean(lava, land, size, value: false, minCells);
            var field = new float[size * size];
            for (int i = 0; i < field.Length; i++) field[i] = lava[i] ? 1f : 0f;
            for (int pass = 0; pass < blurPasses; pass++) Paper.Blur(field, size);
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
