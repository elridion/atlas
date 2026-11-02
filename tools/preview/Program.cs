using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using UnityEngine;

// Offline preview of the Atlas drawing, fed from vanilla's cached minimap files.
// Usage: Preview <worldDir> <outDir> view ... [--nowash] [--parchment] [--find] [--layers]
//   overview                       the whole sheet at 1/4
//   name:wx:wz:widthPx:scale[:sheet] a square of widthPx sheet pixels (5.27 m) around world (wx, wz),
//                                  as a detail tile at scale, or magnified from the sheet with :sheet
//   tilesNAME:wx:wz:scale          3x3 separately rendered 256 px tiles at scale, stitched
// --layers also writes the biome map, land wash and body masks of the whole sheet.
internal static class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly s_atlas;

    static int Main(string[] args)
    {
        s_atlas = Assembly.LoadFrom(Environment.GetEnvironmentVariable("ATLAS_DLL") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Atlas.dll"));
        if (args[0] == "--seacat") return SeaCatalogue(args[1]);
        string dir = args[0], outDir = args[1];
        Directory.CreateDirectory(outDir);
        var sw = Stopwatch.StartNew();

        byte[] biome = Gunzip(Path.Combine(dir, "cacheMinimapBiome"));
        byte[] mask = Gunzip(Path.Combine(dir, "cacheMinimapMask"));
        byte[] height = Gunzip(Path.Combine(dir, "cacheMinimapHeight"));
        int size = 2048;
        var colors = new Color32[size * size];
        var masks = new Color32[size * size];
        for (int i = 0; i < colors.Length; i++)
        {
            colors[i] = new Color32(biome[i * 4], biome[i * 4 + 1], biome[i * 4 + 2], 255);
            masks[i] = new Color32(mask[i * 4], mask[i * 4 + 1], mask[i * 4 + 2], 255);
        }
        var halves = new ushort[size * size];
        Buffer.BlockCopy(height, 0, halves, 0, height.Length);

        Type wsType = T("Atlas.WorldSource");
        object source = Activator.CreateInstance(wsType, true);
        Set(source, "m_size", size);
        Set(source, "m_pixelSize", 12f);
        Set(source, "m_waterLevel", 30f);
        Set(source, "m_seed", 12345);
        Set(source, "m_colors", colors);
        Set(source, "m_mask", masks);
        Set(source, "m_halfHeights", halves);
        Type entry = wsType.GetNestedType("PaletteEntry", All);
        Type land = T("Atlas.Land");
        var palette = Array.CreateInstance(entry, 9);
        void P(int i, int r, int g, int b, string name) =>
            palette.SetValue(Activator.CreateInstance(entry, new object[] { new UnityEngine.Color(r / 255f, g / 255f, b / 255f), Enum.Parse(land, name) }), i);
        P(0, 146, 167, 92, "Meadows");
        P(1, 107, 116, 63, "BlackForest");
        P(2, 163, 114, 88, "Swamp");
        P(3, 231, 171, 120, "Plains");
        P(4, 51, 51, 51, "Mistlands");
        P(5, 123, 32, 32, "Ashlands");
        P(6, 255, 255, 255, "Mountain");
        P(7, 255, 255, 255, "DeepNorth");
        P(8, 255, 255, 255, "Ocean");
        Set(source, "m_palette", palette);

        // The game's forest factor runs on Mathf.PerlinNoise, which needs the engine; the preview
        // stands in Ken Perlin's improved noise (z = 0) mapped to 0..1, an approximation.
        wsType.GetField("s_forestFactor", All)?.SetValue(null, new Func<float, float, float>(ForestFactor));
        var rawHalves = (ushort[])halves.Clone();
        wsType.GetMethod("Decode", All).Invoke(source, null);
        if (Array.IndexOf(args, "--forestcheck") >= 0)
        for (s_variant = 0; s_variant <= 4; s_variant++)
        {
            System.Console.WriteLine($"variant {s_variant}");
            // How well the stand-in forest factor matches vanilla's own Meadows forest mask
            // (WorldGenerator.InForest, the same < 1.15 test the Deep North uses).
            var biomes = (Array)Get(source, "m_biome");
            var heights = (float[])Get(source, "m_height");
            int meadows = Convert.ToInt32(Enum.Parse(land, "Meadows")), north = Convert.ToInt32(Enum.Parse(land, "DeepNorth"));
            int agree = 0, total = 0, forest = 0, northLand = 0, northForest = 0;
            for (int i = 0; i < size * size; i++)
            {
                if (heights[i] <= 30f) continue;
                float wx = (i % size - size / 2) * 12f + 6f, wz = (i / size - size / 2) * 12f + 6f;
                int b = Convert.ToInt32(biomes.GetValue(i));
                bool mine = ForestFactor(wx, wz) < 1.15f;
                if (b == north) { northLand++; if (mine) northForest++; }
                if (b != meadows) continue;
                total++;
                bool real = masks[i].r > 127;
                if (real) forest++;
                if (real == mine) agree++;
            }
            System.Console.WriteLine($"MEADOWS {total} land cells, vanilla forest {100f * forest / total:F1}%, stand-in agrees on {100f * agree / total:F1}%");
            System.Console.WriteLine($"DEEPNORTH {northLand} land cells, stand-in forest {100f * northForest / northLand:F1}%");
            if (s_variant == 4) return 0;
        }
        if (Array.IndexOf(args, "--swampcheck") >= 0)
        {
            // Swamp cells under water before the raise, by depth, and how many of them join
            // water that is not swamp (the sea or a lake beside the swamp).
            MethodInfo h2f = wsType.GetMethod("HalfToFloat", All);
            var biomes = (Array)Get(source, "m_biome");
            int swamp = Convert.ToInt32(Enum.Parse(land, "Swamp"));
            var raw = new float[size * size];
            for (int i = 0; i < raw.Length; i++) raw[i] = (float)h2f.Invoke(null, new object[] { rawHalves[i] });
            var bins = new int[8];
            int edge = 0, under = 0;
            for (int i = 0; i < raw.Length; i++)
            {
                if (Convert.ToInt32(biomes.GetValue(i)) != swamp || raw[i] > 30f) continue;
                under++;
                float depth = 30f - raw[i];
                bins[Math.Min(7, (int)(depth / 0.5f))]++;
                int x = i % size, z = i / size;
                bool joins = false;
                foreach (int j in new[] { i - 1, i + 1, i - size, i + size })
                    if (j >= 0 && j < raw.Length && raw[j] <= 30f && Convert.ToInt32(biomes.GetValue(j)) != swamp) joins = true;
                if (joins) edge++;
            }
            System.Console.WriteLine($"SWAMP under water {under}, beside non-swamp water {edge}");
            for (int b = 0; b < 8; b++) System.Console.WriteLine($"  depth {b * 0.5f:F1}{(b == 7 ? "+" : "-" + ((b + 1) * 0.5f).ToString("F1"))} m: {bins[b]}");
            return 0;
        }
        if (Array.IndexOf(args, "--find") >= 0)
        {
            // For each biome, the 600 m window (50 map cells) where it covers the most land.
            var biomes = (Array)Get(source, "m_biome");
            var heights = (float[])Get(source, "m_height");
            foreach (object landValue in Enum.GetValues(land))
            {
                int want = Convert.ToInt32(landValue), best = -1, bx = 0, bz = 0;
                for (int z = 40; z < size - 40; z += 20)
                    for (int x = 40; x < size - 40; x += 20)
                    {
                        float wx0 = (x - size / 2) * 12f, wz0 = (z - size / 2) * 12f;
                        if (wx0 * wx0 + wz0 * wz0 > 9800f * 9800f) continue;
                        int count = 0;
                        for (int dz = -25; dz <= 25; dz += 5)
                            for (int dx = -25; dx <= 25; dx += 5)
                            {
                                int i = (z + dz) * size + x + dx;
                                bool water = heights[i] <= 30f;
                                if (Convert.ToInt32(biomes.GetValue(i)) == want && (want == 0 ? water : !water)) count++;
                            }
                        if (count > best) { best = count; bx = x; bz = z; }
                    }
                System.Console.WriteLine($"FIND {landValue} {(bx - size / 2) * 12} {(bz - size / 2) * 12} {best}/121");
            }
            return 0;
        }
        System.Console.WriteLine($"decode {sw.ElapsedMilliseconds} ms");
        sw.Restart();

        Type paper = T("Atlas.Paper");
        byte[] parchment = (byte[])paper.GetMethod("Parchment", All).Invoke(null, new object[] { 12345 });
        object wash = paper.GetMethod("Washed", All).Invoke(null, new object[] { parchment, source });
        byte[] washed = Array.IndexOf(args, "--nowash") >= 0 ? parchment : (byte[])wash.GetType().GetField("m_sheet").GetValue(wash);
        System.Console.WriteLine($"wash {sw.ElapsedMilliseconds} ms");
        int fog = (int)Math.Round(Math.Sqrt(parchment.Length / 3));
        if (Array.IndexOf(args, "--parchment") >= 0)
        {
            byte[] landWash = (byte[])wash.GetType().GetField("m_land").GetValue(wash);
            SaveRgb(Path.Combine(outDir, "parchment.png"), parchment, fog);
            SaveRgb(Path.Combine(outDir, "landwash.png"), landWash, fog);
            SaveRgb(Path.Combine(outDir, "sheetwash.png"), washed, fog);
            return 0;
        }
        sw.Restart();

        object record = Activator.CreateInstance(T("Atlas.InkRecord"), true);
        byte[] sheet = (byte[])T("Atlas.InkPainter").GetMethod("Paint", All).Invoke(null, new object[] { source, CancellationToken.None, record });
        System.Console.WriteLine($"paint {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        // The game's own composite: the sheet's ink and its sea illustrations' wash (when this
        // Atlas has it; older builds take the sheet alone, or the marks are left out with --nosea).
        MethodInfo withMarks = Array.Find(paper.GetMethods(All), m => m.Name == "InkWithMips" && m.GetParameters().Length == 2);
        byte[] inkMips = withMarks != null
            ? (byte[])withMarks.Invoke(null, new object[] { sheet, Array.IndexOf(args, "--nosea") >= 0 ? null : Get(record, "m_seaMarks") })
            : (byte[])paper.GetMethod("InkWithMips", All, null, new[] { typeof(byte[]) }, null).Invoke(null, new object[] { sheet });
        object minimap = Activator.CreateInstance(T("Atlas.MinimapSampler"), All, null, new object[] { source }, null);
        object sampler = Activator.CreateInstance(T("Atlas.SampleCache"), All, null, new object[] { minimap }, null);
        const int canvas = 4096;
        if (Array.IndexOf(args, "--layers") >= 0)
        {
            int n = canvas * canvas;
            var gray = new byte[n * 4];
            void Layer(string name, int offset)
            {
                // Sheet rows run south from the north edge; flip into texture order for Save.
                for (int y = 0; y < canvas; y++)
                    for (int x = 0; x < canvas; x++)
                    {
                        byte v = sheet[offset + (canvas - 1 - y) * canvas + x];
                        int o = (y * canvas + x) * 4;
                        gray[o] = gray[o + 1] = gray[o + 2] = (byte)(255 - v);
                        gray[o + 3] = 255;
                    }
                Save(Path.Combine(outDir, name), gray, canvas, canvas, 2);
            }
            Layer("layer-ink.png", 0);
            Layer("layer-body.png", n);
            Layer("layer-north.png", 2 * n);
            byte[] landWash = (byte[])wash.GetType().GetField("m_land").GetValue(wash);
            SaveRgb(Path.Combine(outDir, "layer-landwash.png"), landWash, fog);
            SaveRgb(Path.Combine(outDir, "layer-sheetwash.png"), washed, fog);
        }

        MethodInfo render = T("Atlas.DetailTile").GetMethod("Render", All);
        const float half = 10800f, mpp = 2f * half / canvas;
        for (int a = 2; a < args.Length; a++)
        {
            if (args[a].StartsWith("--")) continue;
            string[] p = args[a].Split(':');
            sw.Restart();
            if (p[0].StartsWith("tiles"))
            {
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]);
                int scale = int.Parse(p[3]), tileSize = 256 / scale;
                int tx0 = (int)((wx + half) / mpp) / tileSize - 1, ty0 = (int)((half - wz) / mpp) / tileSize - 1;
                var all = new byte[768 * 768 * 4];
                for (int ty = 0; ty < 3; ty++)
                    for (int tx = 0; tx < 3; tx++)
                    {
                        var tile = (byte[])render.Invoke(null, new object[] { record, wash, source, sampler, new RectInt((tx0 + tx) * tileSize, (ty0 + ty) * tileSize, tileSize, tileSize), scale, 3f, 1, CancellationToken.None });
                        for (int y = 0; y < 256; y++)
                            Buffer.BlockCopy(tile, y * 1024, all, (((2 - ty) * 256 + y) * 768 + tx * 256) * 4, 1024);
                    }
                Save(Path.Combine(outDir, p[0] + ".png"), all, 768, 768, 1);
            }
            else if (p[0] == "biomes")
            {
                // biomes:wx:wz:radiusMetres prints land cell counts per biome and the height range.
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]), radius = float.Parse(p[3]);
                var biomes = (Array)Get(source, "m_biome");
                var heights = (float[])Get(source, "m_height");
                var counts = new System.Collections.Generic.SortedDictionary<string, string>();
                foreach (object landValue in Enum.GetValues(land))
                {
                    int count = 0;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int z = 0; z < size; z++)
                        for (int x = 0; x < size; x++)
                        {
                            float cx = (x - size / 2 + 0.5f) * 12f, cz = (z - size / 2 + 0.5f) * 12f;
                            if ((cx - wx) * (cx - wx) + (cz - wz) * (cz - wz) > radius * radius) continue;
                            int i = z * size + x;
                            if (Convert.ToInt32(biomes.GetValue(i)) != Convert.ToInt32(landValue) || heights[i] <= 30f) continue;
                            count++;
                            lo = Math.Min(lo, heights[i] - 30f);
                            hi = Math.Max(hi, heights[i] - 30f);
                        }
                    if (count > 0) System.Console.WriteLine($"BIOME {landValue} {count} cells, {lo:F0}-{hi:F0} m above water");
                }
            }
            else if (p[0].StartsWith("fog"))
            {
                // fogNAME:wx:wz:widthPx:scale:dz:halfWidth lays the game's fog over the sheet square,
                // for a snapshot that has explored only a level strip halfWidth metres either side
                // of wz + dz, with the sheet's sea marks shown or hidden whole.
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]), dz = float.Parse(p[5]), halfWidth = float.Parse(p[6]);
                int w = int.Parse(p[3]), scale = int.Parse(p[4]);
                int left = (int)((wx + half) / mpp) - w / 2, top = (int)((half - wz) / mpp) - w / 2;
                object snapshot = Activator.CreateInstance(T("Atlas.Snapshot"), true);
                Set(snapshot, "m_size", size);
                Set(snapshot, "m_pixelSize", 12f);
                var bits = new System.Collections.BitArray(size * size);
                for (int z = 0; z < size; z++)
                {
                    float cz = (z - size / 2 + 0.5f) * 12f;
                    if (Math.Abs(cz - (wz + dz)) > halfWidth) continue;
                    for (int x = 0; x < size; x++) bits[z * size + x] = true;
                }
                Set(snapshot, "m_explored", bits);
                object marks = p[0].EndsWith("plain") ? null : Get(record, "m_seaMarks");
                var fogRgba = new byte[fog * fog * 4];
                paper.GetMethod("Fog", All).Invoke(null, new object[] { fogRgba, parchment, snapshot, marks });
                byte[] rgba = Sheet(inkMips, washed, fog, left, top, w, scale);
                int ow = w * scale;
                var texel = new float[4];
                for (int oy = 0; oy < ow; oy++)
                {
                    for (int ox = 0; ox < ow; ox++)
                    {
                        // Fog rows run from the south, as the output's do; bilinear as the game samples it.
                        float fx = (left + (ox + 0.5f) / scale) * fog / canvas - 0.5f, fy = (canvas - (top + w - (oy + 0.5f) / scale)) * fog / canvas - 0.5f;
                        int x0 = Mathf.Clamp((int)Math.Floor(fx), 0, fog - 2), y0 = Mathf.Clamp((int)Math.Floor(fy), 0, fog - 2);
                        float tx = Mathf.Clamp01(fx - x0), ty = Mathf.Clamp01(fy - y0);
                        int o = (oy * ow + ox) * 4;
                        for (int c = 0; c < 4; c++)
                        {
                            float a00 = fogRgba[(y0 * fog + x0) * 4 + c], a10 = fogRgba[(y0 * fog + x0 + 1) * 4 + c];
                            float a01 = fogRgba[((y0 + 1) * fog + x0) * 4 + c], a11 = fogRgba[((y0 + 1) * fog + x0 + 1) * 4 + c];
                            texel[c] = Lerp(Lerp(a00, a10, tx), Lerp(a01, a11, tx), ty);
                        }
                        float alpha = texel[3] / 255f;
                        for (int c = 0; c < 3; c++) rgba[o + c] = (byte)(rgba[o + c] * (1f - alpha) + texel[c] * alpha + 0.5f);
                    }
                }
                Save(Path.Combine(outDir, p[0] + ".png"), rgba, ow, ow, 1);
            }
            else if (p[0] == "seamarks")
            {
                // Lists the sea illustrations' boxes: centre in world metres and size in sheet px.
                foreach (object m in (System.Collections.IList)Get(record, "m_seaMarks"))
                {
                    float we = (float)Get(m, "m_west"), ea = (float)Get(m, "m_east"), so = (float)Get(m, "m_south"), no = (float)Get(m, "m_north");
                    System.Console.WriteLine($"SEAMARK {(we + ea) / 2:F0} {(so + no) / 2:F0} size {(ea - we) / mpp:F0}x{(no - so) / mpp:F0}");
                }
            }
            else if (p[0] == "washmasks")
            {
                // Each sea illustration's wash mask (SeaWash, black = full wash), 8x, for checking its shape.
                int k = 0;
                foreach (object wsh in (System.Collections.IList)Get(record, "m_seaWashes"))
                {
                    int ww = (int)Get(wsh, "m_width"), wh = (int)Get(wsh, "m_height");
                    var msk = (float[])Get(wsh, "m_mask");
                    var px = new byte[ww * wh * 4];
                    for (int y = 0; y < wh; y++)
                        for (int x = 0; x < ww; x++)
                        {
                            byte v = (byte)(255 - Mathf.Clamp01(msk[(wh - 1 - y) * ww + x]) * 255f);
                            int o = (y * ww + x) * 4;
                            px[o] = px[o + 1] = px[o + 2] = v;
                            px[o + 3] = 255;
                        }
                    var big = new byte[ww * 4 * wh * 4 * 4];
                    for (int y = 0; y < wh * 4; y++)
                        for (int x = 0; x < ww * 4; x++)
                            Buffer.BlockCopy(px, ((y / 4) * ww + x / 4) * 4, big, (y * ww * 4 + x) * 4, 4);
                    Save(Path.Combine(outDir, $"washmask{k++}.png"), big, ww * 4, wh * 4, 1);
                }
            }
            else if (p[0] == "islets")
            {
                // Lists islet cluster strokes (anchor 2) as world positions, one line per stroke.
                var waves = (System.Collections.IList)Get(record, "m_waves");
                foreach (object wv in waves)
                {
                    if ((int)Get(wv, "m_anchor") != 2) continue;
                    float x = (float)Get(wv, "m_x"), y = (float)Get(wv, "m_y"), l = (float)Get(wv, "m_length");
                    System.Console.WriteLine($"ISLET {(x + l / 2) * mpp - half:F0} {half - y * mpp:F0} len {l:F1} crests {Get(wv, "m_crests")}");
                }
            }
            else if (p[0] == "overview")
            {
                var rgba = Sheet(inkMips, washed, fog, 0, 0, canvas, 1);
                Save(Path.Combine(outDir, "overview.png"), rgba, canvas, canvas, 4);
            }
            else
            {
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]);
                int w = int.Parse(p[3]), scale = int.Parse(p[4]);
                int left = (int)((wx + half) / mpp) - w / 2, top = (int)((half - wz) / mpp) - w / 2;
                var rgba = p.Length > 5 && p[5] == "sheet" ? Sheet(inkMips, washed, fog, left, top, w, scale)
                    : (byte[])render.Invoke(null, new object[] { record, wash, source, sampler, new RectInt(left, top, w, w), scale, 3f, 4, CancellationToken.None });
                Save(Path.Combine(outDir, p[0] + ".png"), rgba, w * scale, w * scale, 1);
            }
            System.Console.WriteLine($"{p[0]} {sw.ElapsedMilliseconds} ms");
        }
        return 0;
    }

    // The sheet as the game shows it zoomed out: the ink texture's top level (straight alpha,
    // rows from the south) over the washed paper (fog-sized, rows from the south), for the square
    // of w sheet pixels at (left, top), magnified by scale. Output rows run from the south.
    static byte[] Sheet(byte[] ink, byte[] paper, int fog, int left, int top, int w, int scale)
    {
        const int canvas = 4096;
        int ow = w * scale;
        var rgba = new byte[ow * ow * 4];
        for (int oy = 0; oy < ow; oy++)
        {
            // Output row oy counts from the south edge of the square.
            int sy = top + w - 1 - oy / scale;
            int texY = canvas - 1 - Mathf.Clamp(sy, 0, canvas - 1);
            for (int ox = 0; ox < ow; ox++)
            {
                int sx = Mathf.Clamp(left + ox / scale, 0, canvas - 1);
                int i = (texY * canvas + sx) * 4;
                int py = texY * fog / canvas, px = sx * fog / canvas;
                int q = (py * fog + px) * 3;
                float a = ink[i + 3] / 255f;
                int o = (oy * ow + ox) * 4;
                for (int c = 0; c < 3; c++) rgba[o + c] = (byte)(paper[q + c] * (1f - a) + ink[i + c] * a + 0.5f);
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    static float ForestFactor(float wx, float wz)
    {
        float x = wx * 0.004f, y = wz * 0.004f, sum = 0f, amp = 1f;
        for (int i = 0; i < 3; i++)
        {
            sum += amp * Perlin(x, y);
            amp *= 0.7f;
            x *= 1.6f;
            y *= 1.6f;
        }
        return sum;
    }

    static readonly int[] s_p = BuildPerm();

    static int[] BuildPerm()
    {
        int[] p = { 151,160,137,91,90,15,131,13,201,95,96,53,194,233,7,225,140,36,103,30,69,142,8,99,37,240,21,10,23,
            190,6,148,247,120,234,75,0,26,197,62,94,252,219,203,117,35,11,32,57,177,33,88,237,149,56,87,174,20,125,136,171,168,68,175,
            74,165,71,134,139,48,27,166,77,146,158,231,83,111,229,122,60,211,133,230,220,105,92,41,55,46,245,40,244,102,143,54,65,25,63,
            161,1,216,80,73,209,76,132,187,208,89,18,169,200,196,135,130,116,188,159,86,164,100,109,198,173,186,3,64,52,217,226,250,124,
            123,5,202,38,147,118,126,255,82,85,212,207,206,59,227,47,16,58,17,182,189,28,42,223,183,170,213,119,248,152,2,44,154,163,70,
            221,153,101,155,167,43,172,9,129,22,39,253,19,98,108,110,79,113,224,232,178,185,112,104,218,246,97,228,251,34,242,193,238,
            210,144,12,191,179,162,241,81,51,145,235,249,14,239,107,49,192,214,31,181,199,106,157,184,84,204,176,115,121,50,45,127,4,
            150,254,138,236,205,93,222,114,67,29,24,72,243,141,128,195,78,66,215,61,156,180 };
        var q = new int[512];
        for (int i = 0; i < 512; i++) q[i] = p[i & 255];
        return q;
    }

    static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

    static float Grad(int hash, float x, float y)
    {
        int h = hash & 15;
        float u = h < 8 ? x : y, v = h < 4 ? y : (h == 12 || h == 14 ? x : 0f);
        return ((h & 1) == 0 ? u : -u) + ((h & 2) == 0 ? v : -v);
    }

    static int s_variant;

    static float Perlin(float x, float y)
    {
        if (s_variant > 0) return PerlinVariant(x, y);
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        float xf = x - xi, yf = y - yi;
        xi &= 255; yi &= 255;
        float u = Fade(xf), v = Fade(yf);
        int aa = s_p[s_p[s_p[xi] + yi]], ab = s_p[s_p[s_p[xi] + yi + 1]];
        int ba = s_p[s_p[s_p[xi + 1] + yi]], bb = s_p[s_p[s_p[xi + 1] + yi + 1]];
        float x1 = Lerp(Grad(aa, xf, yf), Grad(ba, xf - 1f, yf), u);
        float x2 = Lerp(Grad(ab, xf, yf - 1f), Grad(bb, xf - 1f, yf - 1f), u);
        return (Lerp(x1, x2, v) + 1f) * 0.5f;
    }

    static readonly float[] s_gx8 = { 1f, -1f, 0f, 0f, 0.7071f, -0.7071f, 0.7071f, -0.7071f };
    static readonly float[] s_gy8 = { 0f, 0f, 1f, -1f, 0.7071f, 0.7071f, -0.7071f, -0.7071f };

    // Variants: 1 two-level hash with 3D grads; 2 two-level hash, 8 unit grads; 3 three-level, 8 grads;
    // 4 two-level, 4 diagonal grads (+-1,+-1).
    static float PerlinVariant(float x, float y)
    {
        int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
        float xf = x - xi, yf = y - yi;
        xi &= 255; yi &= 255;
        float u = Fade(xf), v = Fade(yf);
        int H(int a, int b) => s_variant == 3 ? s_p[s_p[s_p[a] + b]] : s_p[s_p[a] + b];
        float G(int h, float dx, float dy)
        {
            if (s_variant == 1) return Grad(h, dx, dy);
            if (s_variant == 4) return ((h & 1) == 0 ? dx : -dx) + ((h & 2) == 0 ? dy : -dy);
            return s_gx8[h & 7] * dx + s_gy8[h & 7] * dy;
        }
        float x1 = Lerp(G(H(xi, yi), xf, yf), G(H(xi + 1, yi), xf - 1f, yf), u);
        float x2 = Lerp(G(H(xi, yi + 1), xf, yf - 1f), G(H(xi + 1, yi + 1), xf - 1f, yf - 1f), u);
        return (Lerp(x1, x2, v) + 1f) * 0.5f;
    }

    static float Lerp(float a, float b, float t) => a + (b - a) * t;

    static Type T(string name) => s_atlas.GetType(name, true);

    static void Set(object o, string field, object value) => o.GetType().GetField(field, All).SetValue(o, value);

    static object Get(object o, string field) => o.GetType().GetField(field, All).GetValue(o);

    static byte[] Gunzip(string path)
    {
        using (var file = File.OpenRead(path))
        using (var zip = new GZipStream(file, CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            zip.CopyTo(ms);
            return ms.ToArray();
        }
    }

    static void SaveRgb(string path, byte[] rgb, int n)
    {
        var rgba = new byte[n * n * 4];
        for (int i = 0; i < n * n; i++)
        {
            rgba[i * 4] = rgb[i * 3];
            rgba[i * 4 + 1] = rgb[i * 3 + 1];
            rgba[i * 4 + 2] = rgb[i * 3 + 2];
            rgba[i * 4 + 3] = 255;
        }
        Save(path, rgba, n, n, 1);
    }

    // The sea illustration catalogue (Atlas.SeaGlyphs): every candidate at 4x with its name, and
    // the same row at the opening zoom (1x) beneath, ink on the sea's glazed parchment.
    static int SeaCatalogue(string path)
    {
        Type sea = T("Atlas.SeaGlyphs");
        var names = (string[])sea.GetField("Names", All).GetValue(null);
        const int columns = 5;
        const float cell = 60f;
        int rows = (names.Length + columns - 1) / columns;
        System.Drawing.Color water = System.Drawing.Color.FromArgb(175, 179, 182), inkColor = System.Drawing.Color.FromArgb(46, 33, 24);
        object big = sea.GetMethod("Catalogue", All).Invoke(null, new object[] { columns, cell, 4f });
        object small = sea.GetMethod("Catalogue", All).Invoke(null, new object[] { columns, cell, 1f });
        int bw = (int)Get(big, "m_width"), bh = (int)Get(big, "m_height");
        int sw = (int)Get(small, "m_width"), sh = (int)Get(small, "m_height");
        const int label = 26, gap = 30;
        using (var bmp = new Bitmap(bw, label * rows + bh + gap + sh + label))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        using (var font = new Font("Segoe UI", 11f))
        {
            g.Clear(System.Drawing.Color.White);
            Bitmap bigImage = Ink((byte[])Get(big, "m_ink"), bw, bh, water, inkColor);
            Bitmap smallImage = Ink((byte[])Get(small, "m_ink"), sw, sh, water, inkColor);
            int cellPx = (int)(cell * 4f);
            for (int r = 0; r < rows; r++)
            {
                g.DrawImage(bigImage, new Rectangle(0, r * (cellPx + label) + label, bw, cellPx), new Rectangle(0, r * cellPx, bw, cellPx), GraphicsUnit.Pixel);
                for (int c = 0; c < columns && r * columns + c < names.Length; c++)
                    g.DrawString($"{r * columns + c + 1}. {names[r * columns + c]}", font, Brushes.Black, c * cellPx + 6, r * (cellPx + label) + 4);
            }
            int y0 = rows * (cellPx + label) + gap;
            g.DrawString("The same at the opening zoom (1x):", font, Brushes.Black, 6, y0 - 24);
            g.DrawImage(smallImage, 6, y0, sw, sh);
            bmp.Save(path, ImageFormat.Png);
            bigImage.Dispose();
            smallImage.Dispose();
        }
        System.Console.WriteLine(path);
        return 0;
    }

    static Bitmap Ink(byte[] ink, int w, int h, System.Drawing.Color paper, System.Drawing.Color pen)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        var row = new byte[data.Stride];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float a = ink[y * w + x] / 255f;
                row[x * 3] = (byte)(paper.B + (pen.B - paper.B) * a);
                row[x * 3 + 1] = (byte)(paper.G + (pen.G - paper.G) * a);
                row[x * 3 + 2] = (byte)(paper.R + (pen.R - paper.R) * a);
            }
            Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
        }
        bmp.UnlockBits(data);
        return bmp;
    }

    // rgba rows run south first; down is a box downscale factor.
    static void Save(string path, byte[] rgba, int w, int h, int down)
    {
        int ow = w / down, oh = h / down;
        using (var bmp = new Bitmap(ow, oh, PixelFormat.Format24bppRgb))
        {
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, ow, oh), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            var row = new byte[data.Stride];
            for (int y = 0; y < oh; y++)
            {
                for (int x = 0; x < ow; x++)
                {
                    int r = 0, g = 0, b = 0;
                    for (int dy = 0; dy < down; dy++)
                        for (int dx = 0; dx < down; dx++)
                        {
                            int sy = h - 1 - (y * down + dy);
                            int i = (sy * w + x * down + dx) * 4;
                            r += rgba[i]; g += rgba[i + 1]; b += rgba[i + 2];
                        }
                    int n = down * down;
                    row[x * 3] = (byte)(b / n);
                    row[x * 3 + 1] = (byte)(g / n);
                    row[x * 3 + 2] = (byte)(r / n);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
            bmp.UnlockBits(data);
            bmp.Save(path, ImageFormat.Png);
        }
    }
}
