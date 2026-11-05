using System;
using System.Collections.Generic;

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
        // Copses: a Meadows patch dropped by MinWood is still real trees. The Meadows' red is
        // WorldGenerator.InForest (forest factor under 1.15) at the pixel's centre, where the
        // game's beeches and birches grow (about ten small beeches and a big tree or two on a
        // 12 m pixel), so each keeps one tree, or two from CopsePair pixels, on its own pixels
        // (WorldSource.m_copses, drawn by InkPainter.PlaceCopses), on its pixels inland (Inland).
        // Most of these patches are beach, so few trees come of them. Two trees stand on the pair of
        // its pixels nearest CopseGap pixels apart (about a crown's width, so they read as one
        // clump) with their middle nearest the patch's centre.
        private const int CopsePair = 3;
        private const float CopseGap = 2f;

        // Worker thread, after WorldSource.Decode. Also fills world.m_copses.
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
            var copses = new List<int[]>();
            Clean(wood, land, size, value: true, MinWood, (patch, count) =>
            {
                int meadows = 0;
                for (int k = 0; k < count; k++)
                    if (world.m_biome[patch[k]] == Land.Meadows) meadows++;
                if (meadows * 2 <= count) return;
                var cells = new int[count];
                Array.Copy(patch, cells, count);
                copses.Add(cells);
            });
            Clean(wood, land, size, value: false, MinClearing);

            var field = new float[size * size];
            for (int i = 0; i < field.Length; i++) field[i] = wood[i] ? 1f : 0f;
            for (int pass = 0; pass < BlurPasses; pass++) Paper.Blur(field, size);
            world.m_copses = CopseTrees(world, copses, field);
            return field;
        }

        // The map pixels the copses' trees stand on: their Meadows pixels not under the woods'
        // field (a copse in a clearing the cleanup filled, or touching a wood's softened edge, is
        // drawn by that wood's own trees), one per copse, two from CopsePair pixels.
        private static int[] CopseTrees(WorldSource world, List<int[]> copses, float[] field)
        {
            int size = world.m_size;
            var trees = new List<int>();
            var open = new List<int>();
            foreach (int[] patch in copses)
            {
                float cx = 0f, cz = 0f;
                foreach (int i in patch)
                {
                    cx += i % size;
                    cz += i / size;
                }
                cx /= patch.Length;
                cz /= patch.Length;
                open.Clear();
                foreach (int i in patch)
                    if (world.m_biome[i] == Land.Meadows && field[i] <= 0.5f && Inland(world, i)) open.Add(i);
                if (open.Count == 0) continue;
                if (patch.Length < CopsePair || open.Count < 2)
                {
                    int best = open[0];
                    float nearest = float.MaxValue;
                    foreach (int i in open)
                    {
                        float dx = i % size - cx, dz = i / size - cz, d = dx * dx + dz * dz;
                        if (d < nearest)
                        {
                            nearest = d;
                            best = i;
                        }
                    }
                    trees.Add(best);
                    continue;
                }
                int first = open[0], second = open[1];
                float cost = float.MaxValue;
                for (int a = 0; a < open.Count; a++)
                {
                    for (int b = a + 1; b < open.Count; b++)
                    {
                        int ax = open[a] % size, az = open[a] / size, bx = open[b] % size, bz = open[b] / size;
                        float gap = (float)Math.Sqrt((ax - bx) * (ax - bx) + (az - bz) * (az - bz));
                        float mx = 0.5f * (ax + bx) - cx, mz = 0.5f * (az + bz) - cz;
                        float c = Math.Abs(gap - CopseGap) + 0.5f * (float)Math.Sqrt(mx * mx + mz * mz);
                        if (c < cost)
                        {
                            cost = c;
                            first = open[a];
                            second = open[b];
                        }
                    }
                }
                trees.Add(first);
                trees.Add(second);
            }
            return trees.ToArray();
        }

        // Whether a map pixel's eight neighbours are all land. Most dropped patches are a pixel or
        // two of beach (0-2 m above the water), where a tree would stand on the coast's line; the
        // woods' own trees keep three sheet pixels off it too.
        private static bool Inland(WorldSource world, int i)
        {
            int size = world.m_size, x = i % size, z = i / size;
            if (x == 0 || z == 0 || x == size - 1 || z == size - 1) return false;
            for (int dz = -1; dz <= 1; dz++)
                for (int dx = -1; dx <= 1; dx++)
                    if (!(world.m_height[i + dz * size + dx] > world.m_waterLevel)) return false;
            return true;
        }

        // The Ashlands' lava fields as a smooth field like the woods' (edge at 0.5), from vanilla's
        // lava mask (blue on Ashlands land), so they can be outlined and filled as shapes.
        public static float[] BuildLava(WorldSource world) => BuildLava(world, LavaWeight, MinLava, LavaBlurPasses);

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
        // Each patch flipped is passed to dropped (its cells, then how many) before it flips.
        private static void Clean(bool[] wood, bool[] land, int size, bool value, int minCells, Action<int[], int> dropped = null)
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
                dropped?.Invoke(queue, tail);
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
