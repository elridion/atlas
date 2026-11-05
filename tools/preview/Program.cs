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
//   fogNAME:wx:wz:widthPx:scale:dz:halfWidth[:gx:gz]  the sheet under the fog of an explored strip
//   edgeNAME:sheet:wx:wz:widthPx:scale[:down], edgeNAME:tiles:wx:wz:scale  the sheet or close-ups
//                                  under the fog of --explore ex:ez:radius blobs (repeatable),
//                                  with --glimpse gx:gz glimpses; --noglimpse as the setting off
// --layers also writes the biome map, land wash and body masks of the whole sheet.
// --alt NAME:wx:wz:radiusMetres forces an alt biome's effect on the woods there (ForceAltBiomes).
// --temple wx:wz places the Sacrificial Stones there; --pin GLYPH:wx:wz[:checked] (repeatable)
// draws a pin into the sheet.
// Preview --pincat <png> and Preview --seacat <png> render the pin and sea mark boards alone.
internal static class Program
{
    const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Assembly s_atlas;

    static int Main(string[] args)
    {
        s_atlas = Assembly.LoadFrom(Environment.GetEnvironmentVariable("ATLAS_DLL") ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Atlas.dll"));
        if (args[0] == "--seacat") return SeaCatalogue(args[1]);
        if (args[0] == "--pincat") return PinCatalogue(args[1]);
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
        ForceAltBiomes(args, source, wsType, land, size);
        // --temple wx:wz stands in for the Sacrificial Stones the game's location icons give, and
        // --pin GLYPH:wx:wz[:checked] (repeatable) for a pin drawn into the sheet.
        Type pinType = T("Atlas.SheetPin"), glyphType = T("Atlas.PinGlyph");
        var sheetPins = new System.Collections.Generic.List<object>();
        for (int a = 2; a < args.Length - 1; a++)
        {
            if (args[a] != "--temple" && args[a] != "--pin") continue;
            string[] t = args[a + 1].Split(':');
            string glyphName = args[a] == "--temple" ? "StoneCircle" : t[0];
            int at = args[a] == "--temple" ? 0 : 1;
            object pin = Activator.CreateInstance(pinType);
            pinType.GetField("m_x").SetValue(pin, float.Parse(t[at]));
            pinType.GetField("m_z").SetValue(pin, float.Parse(t[at + 1]));
            pinType.GetField("m_glyph").SetValue(pin, Enum.Parse(glyphType, glyphName));
            pinType.GetField("m_checked").SetValue(pin, t.Length > at + 2);
            sheetPins.Add(pin);
        }
        Array pinArray = Array.CreateInstance(pinType, sheetPins.Count);
        for (int k = 0; k < sheetPins.Count; k++) pinArray.SetValue(sheetPins[k], k);
        System.Console.WriteLine($"decode {sw.ElapsedMilliseconds} ms");
        sw.Restart();

        Type paper = T("Atlas.Paper");
        byte[] parchment = (byte[])paper.GetMethod("Parchment", All).Invoke(null, new object[] { 12345 });
        // The sheet's stains and patina, laid over every view last as the atlas window lays them
        // over everything, pins and names included (when this Atlas has them; --nodirt leaves them out).
        MethodInfo dirtOf = paper.GetMethod("Dirt", All);
        byte[] dirt = dirtOf == null || Array.IndexOf(args, "--nodirt") >= 0 ? null : (byte[])dirtOf.Invoke(null, new object[] { 12345 });
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
        byte[] sheet = (byte[])T("Atlas.InkPainter").GetMethod("Paint", All).Invoke(null, new object[] { source, CancellationToken.None, record, pinArray });
        System.Console.WriteLine($"paint {sw.ElapsedMilliseconds} ms");
        sw.Restart();
        // The game's own composite: the sheet's ink and its sea illustrations' wash (when this
        // Atlas has it; older builds take the sheet alone, or the marks are left out with --nosea).
        MethodInfo inkWithMips = paper.GetMethod("InkWithMips", All);
        object seaMarks = Array.IndexOf(args, "--nosea") >= 0 ? null : Get(record, "m_seaMarks");
        // The sheet's ink as the game lays it, with the glimpse and the fog's edge (Paper.Fog's
        // FogEdge, which marks are left out) when there is a fog (null: none).
        Func<byte[], object, byte[]> inkWith = (glimpse, edge) => (byte[])inkWithMips.Invoke(null, new object[] { sheet, seaMarks, glimpse, edge });
        var plainInk = Stopwatch.StartNew();
        byte[] inkMips = inkWith(null, null);
        System.Console.WriteLine($"ink laid in {plainInk.ElapsedMilliseconds} ms");
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
            if (sheet.Length >= 4 * n) Layer("layer-mist.png", 3 * n);
            byte[] landWash = (byte[])wash.GetType().GetField("m_land").GetValue(wash);
            SaveRgb(Path.Combine(outDir, "layer-landwash.png"), landWash, fog);
            SaveRgb(Path.Combine(outDir, "layer-sheetwash.png"), washed, fog);
        }

        MethodInfo render = T("Atlas.DetailTile").GetMethod("Render", All);
        const float half = 10800f, mpp = 2f * half / canvas;
        // The edge views' fog, laid once a run, the ink under it and the record groups it leaves out.
        FogLayer edgeLayer = null;
        byte[] edgeInk = null;
        object edgeGroups = null;
        for (int a = 2; a < args.Length; a++)
        {
            if (args[a].StartsWith("--") || args[a - 1] == "--alt" || args[a - 1] == "--temple" || args[a - 1] == "--pin"
                || args[a - 1] == "--explore" || args[a - 1] == "--glimpse") continue;
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
                        var tile = (byte[])render.Invoke(null, new object[] { record, wash, source, sampler, new RectInt((tx0 + tx) * tileSize, (ty0 + ty) * tileSize, tileSize, tileSize), scale, 3f, 1, CancellationToken.None, null, null, null });
                        for (int y = 0; y < 256; y++)
                            Buffer.BlockCopy(tile, y * 1024, all, (((2 - ty) * 256 + y) * 768 + tx * 256) * 4, 1024);
                    }
                Dirt(all, 768, tx0 * tileSize, ty0 * tileSize, 3 * tileSize, scale, dirt, fog);
                Save(Path.Combine(outDir, p[0] + ".png"), all, 768, 768, 1);
            }
            else if (p[0] == "biomes")
            {
                // biomes:wx:wz:radiusMetres prints land cell counts per biome, how many are wood, and
                // the height range.
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]), radius = float.Parse(p[3]);
                var biomes = (Array)Get(source, "m_biome");
                var heights = (float[])Get(source, "m_height");
                var woods = (float[])Get(source, "m_wood");
                var counts = new System.Collections.Generic.SortedDictionary<string, string>();
                foreach (object landValue in Enum.GetValues(land))
                {
                    int count = 0, wooded = 0;
                    float lo = float.MaxValue, hi = float.MinValue;
                    for (int z = 0; z < size; z++)
                        for (int x = 0; x < size; x++)
                        {
                            float cx = (x - size / 2 + 0.5f) * 12f, cz = (z - size / 2 + 0.5f) * 12f;
                            if ((cx - wx) * (cx - wx) + (cz - wz) * (cz - wz) > radius * radius) continue;
                            int i = z * size + x;
                            if (Convert.ToInt32(biomes.GetValue(i)) != Convert.ToInt32(landValue) || heights[i] <= 30f) continue;
                            count++;
                            if (woods[i] > 0.5f) wooded++;
                            lo = Math.Min(lo, heights[i] - 30f);
                            hi = Math.Max(hi, heights[i] - 30f);
                        }
                    if (count > 0) System.Console.WriteLine($"BIOME {landValue} {count} cells ({wooded} wood), {lo:F0}-{hi:F0} m above water");
                }
            }
            else if (p[0].StartsWith("fog"))
            {
                // fogNAME:wx:wz:widthPx:scale:dz:halfWidth[:gx:gz] lays the game's fog over the
                // sheet square, for a snapshot that has explored only a level strip halfWidth
                // metres either side of wz + dz, with the sheet's sea marks shown or hidden whole
                // (fogNAMEplain without them) and a glimpse round a game mark at gx, gz.
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]), dz = float.Parse(p[5]), halfWidth = float.Parse(p[6]);
                int w = int.Parse(p[3]), scale = int.Parse(p[4]);
                int left = (int)((wx + half) / mpp) - w / 2, top = (int)((half - wz) / mpp) - w / 2;
                var bits = new System.Collections.BitArray(size * size);
                for (int z = 0; z < size; z++)
                {
                    float cz = (z - size / 2 + 0.5f) * 12f;
                    if (Math.Abs(cz - (wz + dz)) > halfWidth) continue;
                    for (int x = 0; x < size; x++) bits[z * size + x] = true;
                }
                Vector2[] glimpses = p.Length >= 9 ? new[] { new Vector2(float.Parse(p[7]), float.Parse(p[8])) } : new Vector2[0];
                FogLayer layer = LayFog(paper, record, bits, size, parchment, washed, fog, p[0].EndsWith("plain"), glimpses, Array.IndexOf(args, "--noglimpse") < 0);
                byte[] rgba = Sheet(inkWith(layer.m_glimpse, layer.m_edge), layer.m_glimpsed, fog, left, top, w, scale);
                OverlayFog(rgba, w * scale, left, top, w, scale, layer.m_fog, fog);
                Dirt(rgba, w * scale, left, top, w, scale, dirt, fog);
                Save(Path.Combine(outDir, p[0] + ".png"), rgba, w * scale, w * scale, 1);
            }
            else if (p[0].StartsWith("edge"))
            {
                // edgeNAME:sheet:wx:wz:widthPx:scale[:down] and edgeNAME:tiles:wx:wz:scale: the
                // sheet square (shrunk by down) or 3x3 close-up tiles under the fog of a snapshot
                // that explored ragged blobs (--explore ex:ez:radius, repeatable), with glimpses
                // round --glimpse gx:gz and --noglimpse for the Glimpse setting off. The fog is
                // laid once a run.
                if (edgeLayer == null)
                {
                    edgeLayer = LayFog(paper, record, ExploredBlobs(args, size), size, parchment, washed, fog, false, Glimpses(args), Array.IndexOf(args, "--noglimpse") < 0);
                    var inkTime = Stopwatch.StartNew();
                    edgeInk = inkWith(edgeLayer.m_glimpse, edgeLayer.m_edge);
                    System.Console.WriteLine($"ink laid with the fog's edge in {inkTime.ElapsedMilliseconds} ms");
                    edgeGroups = edgeLayer.m_edge.GetType().GetMethod("HiddenGroups", All).Invoke(edgeLayer.m_edge, new[] { record });
                }
                float wx = float.Parse(p[2]), wz = float.Parse(p[3]);
                byte[] rgba;
                int left, top, w, scale, down = 1;
                if (p[1] == "tiles")
                {
                    scale = int.Parse(p[4]);
                    int tileSize = 256 / scale;
                    int tx0 = (int)((wx + half) / mpp) / tileSize - 1, ty0 = (int)((half - wz) / mpp) / tileSize - 1;
                    rgba = new byte[768 * 768 * 4];
                    for (int ty = 0; ty < 3; ty++)
                        for (int tx = 0; tx < 3; tx++)
                        {
                            var tile = (byte[])render.Invoke(null, new object[] { record, wash, source, sampler, new RectInt((tx0 + tx) * tileSize, (ty0 + ty) * tileSize, tileSize, tileSize), scale, 3f, 1, CancellationToken.None, edgeLayer.m_glimpse, edgeLayer.m_edge, edgeGroups });
                            for (int y = 0; y < 256; y++)
                                Buffer.BlockCopy(tile, y * 1024, rgba, (((2 - ty) * 256 + y) * 768 + tx * 256) * 4, 1024);
                        }
                    left = tx0 * tileSize;
                    top = ty0 * tileSize;
                    w = 3 * tileSize;
                }
                else
                {
                    w = int.Parse(p[4]);
                    scale = int.Parse(p[5]);
                    if (p.Length > 6) down = int.Parse(p[6]);
                    left = (int)((wx + half) / mpp) - w / 2;
                    top = (int)((half - wz) / mpp) - w / 2;
                    rgba = Sheet(edgeInk, edgeLayer.m_glimpsed, fog, left, top, w, scale);
                }
                int ow = w * scale;
                OverlayFog(rgba, ow, left, top, w, scale, edgeLayer.m_fog, fog);
                Dirt(rgba, ow, left, top, w, scale, dirt, fog);
                Save(Path.Combine(outDir, p[0] + ".png"), rgba, ow, ow, down);
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
            else if (p[0].StartsWith("pins"))
            {
                // pinsNAME:wx:wz:scale renders 3x3 tiles as tilesNAME does and lays a fixed set of
                // test pins and names over them as the atlas window does: pen glyphs with bodies in
                // the biome's wash, names placed by PinLayout on the clearest ground. One output pixel is one
                // canvas unit. A stand-in serif font replaces Valheim's. pinsbareNAME leaves out the
                // gap round the names' letters.
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]);
                int scale = int.Parse(p[3]), tileSize = 256 / scale;
                int tx0 = (int)((wx + half) / mpp) / tileSize - 1, ty0 = (int)((half - wz) / mpp) / tileSize - 1;
                var all = new byte[768 * 768 * 4];
                for (int ty = 0; ty < 3; ty++)
                    for (int tx = 0; tx < 3; tx++)
                    {
                        var tile = (byte[])render.Invoke(null, new object[] { record, wash, source, sampler, new RectInt((tx0 + tx) * tileSize, (ty0 + ty) * tileSize, tileSize, tileSize), scale, 3f, 1, CancellationToken.None, null, null, null });
                        for (int y = 0; y < 256; y++)
                            Buffer.BlockCopy(tile, y * 1024, all, (((2 - ty) * 256 + y) * 768 + tx * 256) * 4, 1024);
                    }
                DrawPins(all, 768, tx0 * tileSize, ty0 * tileSize + 3 * tileSize, scale, source, sheet, !p[0].StartsWith("pinsbare"), washed, fog);
                Dirt(all, 768, tx0 * tileSize, ty0 * tileSize, 3 * tileSize, scale, dirt, fog);
                Save(Path.Combine(outDir, p[0] + ".png"), all, 768, 768, 1);
            }
            else if (p[0] == "overview")
            {
                var rgba = Sheet(inkMips, washed, fog, 0, 0, canvas, 1);
                Dirt(rgba, canvas, 0, 0, canvas, 1, dirt, fog);
                Save(Path.Combine(outDir, "overview.png"), rgba, canvas, canvas, 4);
            }
            else
            {
                float wx = float.Parse(p[1]), wz = float.Parse(p[2]);
                int w = int.Parse(p[3]), scale = int.Parse(p[4]);
                int left = (int)((wx + half) / mpp) - w / 2, top = (int)((half - wz) / mpp) - w / 2;
                var rgba = p.Length > 5 && p[5] == "sheet" ? Sheet(inkMips, washed, fog, left, top, w, scale)
                    : (byte[])render.Invoke(null, new object[] { record, wash, source, sampler, new RectInt(left, top, w, w), scale, 3f, 4, CancellationToken.None, null, null, null });
                Dirt(rgba, w * scale, left, top, w, scale, dirt, fog);
                Save(Path.Combine(outDir, p[0] + ".png"), rgba, w * scale, w * scale, 1);
            }
            System.Console.WriteLine($"{p[0]} {sw.ElapsedMilliseconds} ms");
        }
        return 0;
    }

    // --pincat <png>: every pen-drawn pin as the map draws it (InkCanvas.Pin), as a table. Each
    // row is one glyph, with its vanilla pin and its box on the sheet, the glyph as a 4x close-up
    // draws it on paper, plain and checked (scraped, PinGlyphs.Abrade), then as a 2x close-up draws it in every biome, over a dense hatching
    // that stands for woods, so the body's fill and its break in the terrain ink show. Both with
    // the map's own pen (thinned by the close-up's level of detail, as every line) and the
    // hand-drawn waver. A last row notes what is drawn otherwise.
    static int PinCatalogue(string path)
    {
        const int units = 26, over = 4, cell = 56, nameWidth = 230, bigScale = 4, smallScale = 2;
        Type kind = T("Atlas.PinGlyph"), land = T("Atlas.Land");
        MethodInfo draw = T("Atlas.PinGlyphs").GetMethod("Draw", All);
        MethodInfo sheetSize = T("Atlas.InkPainter").GetMethod("PinSheetSize", All);
        MethodInfo bodyColor = T("Atlas.Paper").GetMethod("PinBodyColor", All);
        int largest = 26, bigCell = largest * bigScale + 16;
        var rows = new[]
        {
            new[] { "Fire", "Fire (Icon0)", "campfire" },
            new[] { "House", "House (Icon1)", "longhouse" },
            new[] { "Hammer", "Hammer (Icon2)", "Mjolnir pendant" },
            new[] { "Point", "Circle (Icon3)", "dot" },
            new[] { "Portal", "Portal (Icon4)", "stone portal" },
            new[] { "Death", "Death (tombstone)", "two steles on a plinth" },
            new[] { "Bed", "Bed (spawn point)", "game's pin: bed" },
            new[] { "Boss", "Boss (Vegvisir)", "game's pin: horned skull" },
            new[] { "Tomb", "Hildir 1", "Smouldering Tombs: barrow" },
            new[] { "Cavern", "Hildir 2", "Howling Caverns: cave mouth" },
            new[] { "Tower", "Hildir 3", "Sealed Towers: tower" },
            new[] { "StoneCircle", "Sacrificial Stones", "location: stone circle" },
            new[] { "Trader", "Haldor", "location: bag" },
            new[] { "Tent", "Hildir's camp", "location: tent" },
            new[] { "Cauldron", "The Bog Witch", "location: cauldron" },
            new[] { "Forge", "Forge of Potential", "location: crowned pillar" },
        };
        string[] lands = { "Meadows", "BlackForest", "Swamp", "Mountain", "Plains", "Mistlands", "Ashlands", "DeepNorth", "Ocean" };
        string[] headers = { "Meadows", "Black F.", "Swamp", "Mountain", "Plains", "Mistlands", "Ashlands", "Deep N.", "Water" };
        // The biome columns start after the two close-ups, plain and checked.
        int biomes = nameWidth + 2 * bigCell;
        int width = biomes + cell * lands.Length + 16, header = 34, rowHeight = bigCell;
        int height = header + rowHeight * rows.Length + 70;
        var paperColor = System.Drawing.Color.FromArgb(230, 211, 177);
        var inkColor = System.Drawing.Color.FromArgb(46, 33, 24);
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        using (var font = new System.Drawing.Font("Palatino Linotype", 15f, GraphicsUnit.Pixel))
        using (var small = new System.Drawing.Font("Palatino Linotype", 12f, System.Drawing.FontStyle.Italic, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(inkColor))
        {
            g.Clear(paperColor);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString("pin", font, ink, 8, 8);
            g.DrawString("close-up, 4x", font, ink, nameWidth + 4, 8);
            g.DrawString("checked", font, ink, nameWidth + bigCell + 4, 8);
            for (int c = 0; c < headers.Length; c++)
                g.DrawString(headers[c], small, ink, biomes + c * cell, 12);

            var pinInk = (Color32)T("Atlas.Paper").GetField("PinInkColor", All).GetValue(null);

            // The pin's box on the sheet, in sheet pixels (InkPainter.PinSheetSize).
            float Box(string glyph) => (float)sheetSize.Invoke(null, new object[] { Enum.Parse(kind, glyph) });

            // Blends a glyph into the bitmap with its box centred on (cx, cy), as a close-up at
            // scale draws it (InkCanvas.Pin): box sheet pixels at scale, every line the map's pen
            // thinned by the level of detail; fill gives the body colour, null for none.
            MethodInfo abrade = T("Atlas.PinGlyphs").GetMethod("Abrade", All);
            void Stamp(string glyph, float box, int cx, int cy, int scale, System.Drawing.Color? fill, bool scraped)
            {
                // A close-up draws its pins smaller by its level of detail, as its trees.
                float lod = Mathf.Max(1f, Mathf.Sqrt(scale / 2f));
                int size = Mathf.RoundToInt(box * scale / lod);
                int x = cx - size / 2, y = cy - size / 2;
                int pixels = size * over;
                float pen = units / box;
                object mark = draw.Invoke(null, new object[] { Enum.Parse(kind, glyph), pixels, pen, 4127 });
                // A checked pin, as InkCanvas.Pin scrapes it.
                if (scraped) abrade.Invoke(null, new object[] { mark, pen, 4127 });
                var inkMask = (byte[])Get(mark, "m_ink");
                var body = (byte[])Get(mark, "m_body");
                for (int j = 0; j < size; j++)
                    for (int i = 0; i < size; i++)
                    {
                        float a = 0f, b = 0f;
                        for (int v = 0; v < over; v++)
                            for (int u = 0; u < over; u++)
                            {
                                int k = (j * over + v) * pixels + i * over + u;
                                a += inkMask[k];
                                b += body[k];
                            }
                        a /= 255f * over * over;
                        b /= 255f * over * over;
                        System.Drawing.Color under = bitmap.GetPixel(x + i, y + j);
                        float r = under.R, gr = under.G, bl = under.B;
                        if (fill.HasValue && b > 0f)
                        {
                            r += (fill.Value.R - r) * b;
                            gr += (fill.Value.G - gr) * b;
                            bl += (fill.Value.B - bl) * b;
                        }
                        // The pins' red ink (Paper.PinInkColor).
                        r += (pinInk.r - r) * a;
                        gr += (pinInk.g - gr) * a;
                        bl += (pinInk.b - bl) * a;
                        bitmap.SetPixel(x + i, y + j, System.Drawing.Color.FromArgb((int)(r + 0.5f), (int)(gr + 0.5f), (int)(bl + 0.5f)));
                    }
            }

            for (int row = 0; row < rows.Length; row++)
            {
                int top = header + row * rowHeight;
                string glyph = rows[row][0];
                float box = Box(glyph);
                g.DrawString(rows[row][1], font, ink, 8, top + rowHeight / 2 - 26);
                g.DrawString(rows[row][2], small, ink, 8, top + rowHeight / 2 - 6);
                g.DrawString($"{box:0} sheet px box", small, ink, 8, top + rowHeight / 2 + 10);
                Stamp(glyph, box, nameWidth + bigCell / 2, top + rowHeight / 2, bigScale, paperColor, false);
                // The same close-up checked, its ink scraped away in part.
                Stamp(glyph, box, nameWidth + bigCell + bigCell / 2, top + rowHeight / 2, bigScale, paperColor, true);
                // The game's own pins stand on land: beds, altars, dungeons, traders and the
                // Stones never lie in water. The player's pins and deaths can.
                bool onWater = row < 6;
                for (int c = 0; c < lands.Length; c++)
                {
                    bool water = lands[c] == "Ocean";
                    if (water && !onWater) continue;
                    var colour = (Color32)bodyColor.Invoke(null, new object[] { Enum.Parse(land, lands[c]), water });
                    var fill = System.Drawing.Color.FromArgb(colour.r, colour.g, colour.b);
                    int x0 = biomes + c * cell, y0 = top + (rowHeight - cell) / 2;
                    using (var ground = new SolidBrush(fill)) g.FillRectangle(ground, x0, y0, cell - 4, cell - 4);
                    // Woods: dense short strokes, as a pine wood's ink would lie under the pin.
                    using (var pen = new Pen(inkColor, 1.4f))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.SetClip(new System.Drawing.Rectangle(x0, y0, cell - 4, cell - 4));
                        for (int s = 0; s < 2 * cell; s += 5) g.DrawLine(pen, x0 + s, y0, x0 + s - cell, y0 + cell);
                        g.ResetClip();
                    }
                    Stamp(glyph, box, x0 + (cell - 4) / 2, y0 + (cell - 4) / 2, smallScale, fill, false);
                }
            }
            int foot = header + rowHeight * rows.Length + 10;
            g.DrawString("Locations other mods mark (vanilla marks only the five above) and other mods' pins:", small, ink, 8, foot);
            g.DrawString("traced from their own sprites in game, as markers over the map (not shown offline).", small, ink, 8, foot + 18);
            bitmap.Save(path, ImageFormat.Png);
        }
        return 0;
    }

    // Test pins round the view's centre: offset in canvas units (y up), glyph, name, checked, and
    // the biome the name belongs to (null for any land). Each is moved to the nearest land of its
    // biome in the view (SnapPins), as a player would set it, so the samples never show a pin in
    // open sea or silver outside the Mountains whatever the view.
    static readonly object[][] s_testPins =
    {
        new object[] { -210, 150, "Fire", "Camp", false, null },
        new object[] { 70, 210, "House", "Home", false, "Meadows" },
        new object[] { 230, 40, "Hammer", "Copper mine", false, "BlackForest" },
        new object[] { -120, -60, "Point", "Silver", false, "Mountain" },
        new object[] { 10, 10, "Portal", "Portal to the swamp", false, null },
        new object[] { 190, -210, "Death", "Died here", false, null },
        new object[] { -260, -220, "Point", "Raspberries", true, "Meadows" },
        new object[] { -40, -250, "House", "Old outpost", false, null },
    };

    // Where each test pin stands in the view (output pixels, y up): the nearest point to its
    // offset that is land of its biome, ShoreClear pixels off water each way and PinSpacing from
    // the pins placed before; failing that any such land; failing that its offset.
    static Vector2[] SnapPins(int n, int left, int bottom, int scale, object source)
    {
        const int ShoreClear = 10, PinSpacing = 56, Margin = 40, Reach = 360, Step = 3;
        MethodInfo landAt = source.GetType().GetMethod("LandAt", All), waterAt = source.GetType().GetMethod("IsWaterAt", All);
        float mpp = 2f * 10800f / 4096f;
        bool Water(float x, float y) => (bool)waterAt.Invoke(source, new object[] { (left + x / scale) * mpp - 10800f, 10800f - (bottom - y / scale) * mpp });
        string Land(float x, float y) => landAt.Invoke(source, new object[] { (left + x / scale) * mpp - 10800f, 10800f - (bottom - y / scale) * mpp }).ToString();
        var spots = new Vector2[s_testPins.Length];
        for (int p = 0; p < s_testPins.Length; p++)
        {
            object[] pin = s_testPins[p];
            var nominal = new Vector2(n / 2 + (int)pin[0], n / 2 + (int)pin[1]);
            bool Fits(Vector2 at, string want)
            {
                if (at.x < Margin || at.y < Margin || at.x > n - Margin || at.y > n - Margin) return false;
                if (Water(at.x, at.y) || Water(at.x - ShoreClear, at.y) || Water(at.x + ShoreClear, at.y) || Water(at.x, at.y - ShoreClear) || Water(at.x, at.y + ShoreClear)) return false;
                if (want != null && Land(at.x, at.y) != want) return false;
                for (int q = 0; q < p; q++) if (Vector2.Distance(spots[q], at) < PinSpacing) return false;
                return true;
            }
            spots[p] = nominal;
            bool found = false;
            foreach (string want in new[] { (string)pin[5], null })
            {
                for (int r = 0; r <= Reach && !found; r += Step)
                {
                    int around = Math.Max(1, (int)(2f * Mathf.PI * r / Step));
                    for (int k = 0; k < around; k++)
                    {
                        float a = 2f * Mathf.PI * k / around;
                        var at = nominal + new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * r;
                        if (!Fits(at, want)) continue;
                        spots[p] = new Vector2(Mathf.Round(at.x), Mathf.Round(at.y));
                        found = true;
                        break;
                    }
                }
                if (found || pin[5] == null) break;
            }
        }
        return spots;
    }

    // Lays the test pins over rgba (n x n, rows from the south) as the atlas window does. left is
    // the sheet x of the output's west edge, bottom the sheet y (running south) of its south edge.
    // With gap, each name also scrapes the terrain ink round its letters (their shapes widened by
    // a little), showing the paper there, as an engraver breaks lines round lettering.
    static void DrawPins(byte[] rgba, int n, int left, int bottom, int scale, object source, byte[] sheet, bool gap, byte[] paper, int fog)
    {
        const int canvas = 4096, size = 26, over = 2;
        Type kind = T("Atlas.PinGlyph"), layout = T("Atlas.PinLayout");
        MethodInfo draw = T("Atlas.PinGlyphs").GetMethod("Draw", All);
        MethodInfo choose = layout.GetMethod("Choose", All), area = layout.GetMethod("Area", All), align = layout.GetMethod("Alignment", All);
        object density = T("Atlas.InkDensity").GetMethod("FromSheet", All).Invoke(null, new object[] { sheet });
        MethodInfo mean = density.GetType().GetMethod("Mean", All);

        MethodInfo landAt = source.GetType().GetMethod("LandAt", All), waterAt = source.GetType().GetMethod("IsWaterAt", All);
        MethodInfo bodyColor = T("Atlas.Paper").GetMethod("PinBodyColor", All), aged = T("Atlas.Paper").GetMethod("AgedPin", All);
        MethodInfo abrade = T("Atlas.PinGlyphs").GetMethod("Abrade", All);
        MethodInfo sheetSize = T("Atlas.InkPainter").GetMethod("PinSheetSize", All);
        void Glyph(string name, int cx, int cy, bool body, bool scraped)
        {
            // The body's fill: the wash of the biome at the pin, as the atlas window lays it.
            float pwx = (left + cx / (float)scale) * 2f * 10800f / canvas - 10800f, pwz = 10800f - (bottom - cy / (float)scale) * 2f * 10800f / canvas;
            var fill = (Color32)bodyColor.Invoke(null, new object[] { landAt.Invoke(source, new object[] { pwx, pwz }), waterAt.Invoke(source, new object[] { pwx, pwz }) });
            if (aged != null) fill = (Color32)aged.Invoke(null, new object[] { fill, pwx, pwz, 12345 });
            object mark = draw.Invoke(null, new object[] { Enum.Parse(kind, name), size * over, 0f, 0 });
            // A checked marker, as AtlasView scrapes it: the pen of a sheet pin of its size.
            if (scraped) abrade.Invoke(null, new object[] { mark, 26f / (float)sheetSize.Invoke(null, new object[] { Enum.Parse(kind, name) }), 4127 });
            var ink = (byte[])Get(mark, "m_ink");
            var shape = (byte[])Get(mark, "m_body");
            int m = size * over;
            for (int j = 0; j < size; j++)
                for (int i = 0; i < size; i++)
                {
                    float a = 0f, b = 0f;
                    for (int v = 0; v < over; v++)
                        for (int u = 0; u < over; u++)
                        {
                            a += ink[(j * over + v) * m + i * over + u];
                            b += shape[(j * over + v) * m + i * over + u];
                        }
                    a /= 255f * over * over;
                    b /= 255f * over * over;
                    int ox = cx - size / 2 + i, oy = cy + size / 2 - 1 - j;
                    if (ox < 0 || oy < 0 || ox >= n || oy >= n) continue;
                    int o = (oy * n + ox) * 4;
                    if (body && b > 0f)
                    {
                        rgba[o] = (byte)(rgba[o] * (1f - b) + fill.r * b + 0.5f);
                        rgba[o + 1] = (byte)(rgba[o + 1] * (1f - b) + fill.g * b + 0.5f);
                        rgba[o + 2] = (byte)(rgba[o + 2] * (1f - b) + fill.b * b + 0.5f);
                    }
                    rgba[o] = (byte)(rgba[o] * (1f - a) + 46 * a + 0.5f);
                    rgba[o + 1] = (byte)(rgba[o + 1] * (1f - a) + 33 * a + 0.5f);
                    rgba[o + 2] = (byte)(rgba[o + 2] * (1f - a) + 24 * a + 0.5f);
                }
        }

        // A pin's glyph stands on its place as the atlas window draws it (PinGlyphs.Lift), when
        // this Atlas has it; rows run north here, so the lift is added.
        MethodInfo liftOf = T("Atlas.PinGlyphs").GetMethod("Lift", All);
        int Lift(string name) => liftOf == null ? 0 : (int)Math.Round((float)liftOf.Invoke(null, new object[] { Enum.Parse(kind, name) }) * size);
        var placed = new System.Collections.Generic.List<Rect>();
        Vector2[] spots = SnapPins(n, left, bottom, scale, source);
        for (int p = 0; p < s_testPins.Length; p++)
        {
            object[] pin = s_testPins[p];
            int cx = (int)spots[p].x, cy = (int)spots[p].y + Lift((string)pin[2]);
            placed.Add(new Rect(cx - size * 0.5f, cy - size * 0.5f, size, size));
            Glyph((string)pin[2], cx, cy, true, (bool)pin[4]);
        }

        // Names: bare ink, measured and drawn with System.Drawing on a bitmap of the output.
        using (var bitmap = new Bitmap(n, n, PixelFormat.Format32bppArgb))
        {
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int o = ((n - 1 - y) * n + x) * 4;
                    bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, rgba[o], rgba[o + 1], rgba[o + 2]));
                }
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
            using (var font = new System.Drawing.Font("Palatino Linotype", 16f, System.Drawing.FontStyle.Regular, GraphicsUnit.Pixel))
            using (var pen = new SolidBrush(System.Drawing.Color.FromArgb(46, 33, 24)))
            {
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                StringFormat format = StringFormat.GenericTypographic;
                Func<Rect, float> inkUnder = r => (float)mean.Invoke(density, new object[] { left + r.xMin / scale, bottom - r.yMax / scale, left + r.xMax / scale, bottom - r.yMin / scale });
                float icon = size * 0.5f + 4f;
                for (int p = 0; p < s_testPins.Length; p++)
                {
                    object[] pin = s_testPins[p];
                    var at = new Vector2(spots[p].x, spots[p].y + Lift((string)pin[2]));
                    SizeF measured = g.MeasureString((string)pin[3], font, 1000, format);
                    var extent = new Vector2(measured.Width + 2f, font.Height);
                    int place = (int)choose.Invoke(null, new object[] { at, extent, -1, icon, 2f, placed, inkUnder });
                    if (place < 0) continue;
                    var rect = (Rect)area.Invoke(null, new object[] { at, extent, place, icon });
                    placed.Add(rect);
                    int side = (int)align.Invoke(null, new object[] { place });
                    var box = new RectangleF(rect.xMin, n - rect.yMax, rect.width, rect.height);
                    var aligned = new StringFormat(format) { Alignment = side < 0 ? StringAlignment.Near : side > 0 ? StringAlignment.Far : StringAlignment.Center };
                    if (gap)
                    {
                        using (var path = new System.Drawing.Drawing2D.GraphicsPath())
                        using (var letters = new System.Drawing.Drawing2D.GraphicsPath())
                        using (var widen = new Pen(System.Drawing.Color.Black, 3.2f) { LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
                        using (var shape = new Bitmap(n, n, PixelFormat.Format32bppArgb))
                        using (var sg = System.Drawing.Graphics.FromImage(shape))
                        {
                            path.AddString((string)pin[3], font.FontFamily, (int)font.Style, font.Size, box, aligned);
                            sg.FillPath(Brushes.Black, path);
                            sg.DrawPath(widen, path);
                            int x0 = Math.Max(0, (int)box.Left - 4), x1 = Math.Min(n, (int)box.Right + 4);
                            int y0 = Math.Max(0, (int)box.Top - 4), y1 = Math.Min(n, (int)box.Bottom + 4);
                            for (int y = y0; y < y1; y++)
                                for (int x = x0; x < x1; x++)
                                {
                                    if (shape.GetPixel(x, y).A < 8) continue;
                                    float sx = left + (x + 0.5f) / scale, sy = bottom - (n - 1 - y + 0.5f) / scale;
                                    int fx = Mathf.Clamp((int)(sx * fog / canvas), 0, fog - 1), fy = Mathf.Clamp(fog - 1 - (int)(sy * fog / canvas), 0, fog - 1);
                                    int q = (fy * fog + fx) * 3;
                                    bitmap.SetPixel(x, y, System.Drawing.Color.FromArgb(255, paper[q], paper[q + 1], paper[q + 2]));
                                }
                            // The letters from the same path, so they sit exactly in their gap.
                            letters.AddString((string)pin[3], font.FontFamily, (int)font.Style, font.Size, box, aligned);
                            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                            g.FillPath(pen, letters);
                        }
                        continue;
                    }
                    g.DrawString((string)pin[3], font, pen, box, aligned);
                }
            }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    System.Drawing.Color c = bitmap.GetPixel(x, y);
                    int o = ((n - 1 - y) * n + x) * 4;
                    rgba[o] = c.R;
                    rgba[o + 1] = c.G;
                    rgba[o + 2] = c.B;
                }
        }
    }

    // The sheet as the game shows it zoomed out: the ink texture's top level (straight alpha,
    // rows from the south) over the washed paper (fog-sized, rows from the south), for the square
    // of w sheet pixels at (left, top), magnified by scale. Output rows run from the south.
    // The fog the game lays for a snapshot (Paper.Fog), timed, with the glimpse's bare parchment
    // laid over the wash as the game lays it under the ink.
    sealed class FogLayer
    {
        public byte[] m_fog, m_glimpse, m_glimpsed;
        public object m_edge;
    }

    static FogLayer LayFog(Type paper, object record, System.Collections.BitArray explored, int size, byte[] parchment, byte[] washed, int fog,
        bool plain, Vector2[] glimpses, bool glimpseBand)
    {
        object snapshot = Activator.CreateInstance(T("Atlas.Snapshot"), true);
        Set(snapshot, "m_size", size);
        Set(snapshot, "m_pixelSize", 12f);
        Set(snapshot, "m_explored", explored);
        var layer = new FogLayer { m_fog = new byte[fog * fog * 4], m_glimpse = new byte[fog * fog * 4] };
        var time = Stopwatch.StartNew();
        layer.m_edge = paper.GetMethod("Fog", All).Invoke(null, new object[]
        {
            layer.m_fog, layer.m_glimpse, parchment, snapshot, plain ? null : Get(record, "m_seaMarks"), glimpses, Get(record, "m_marks"), glimpseBand,
        });
        object marks = Get(record, "m_marks");
        System.Console.WriteLine($"fog laid in {time.ElapsedMilliseconds} ms over {((System.Collections.ICollection)Get(marks, "m_trees")).Count} trees, "
            + $"{((System.Collections.ICollection)Get(marks, "m_glyphs")).Count} glyphs, {((System.Collections.ICollection)Get(marks, "m_waves")).Count} wave strokes");
        layer.m_glimpsed = (byte[])washed.Clone();
        for (int i = 0; i < fog * fog; i++)
        {
            float g = layer.m_glimpse[i * 4 + 3] / 255f;
            for (int c = 0; c < 3; c++) layer.m_glimpsed[i * 3 + c] = (byte)(washed[i * 3 + c] + (layer.m_glimpse[i * 4 + c] - washed[i * 3 + c]) * g + 0.5f);
        }
        return layer;
    }

    // --explore ex:ez:radius (repeatable): the snapshot's explored land as ragged blobs, a few
    // lobes and a wobble of a quarter of the radius, standing in for the paths a player takes.
    static System.Collections.BitArray ExploredBlobs(string[] args, int size)
    {
        var blobs = new System.Collections.Generic.List<Vector3>();
        for (int a = 2; a < args.Length - 1; a++)
        {
            if (args[a] != "--explore") continue;
            string[] q = args[a + 1].Split(':');
            blobs.Add(new Vector3(float.Parse(q[0]), float.Parse(q[1]), float.Parse(q[2])));
        }
        MethodInfo value = T("Atlas.Noise").GetMethod("Value", All);
        var bits = new System.Collections.BitArray(size * size);
        for (int z = 0; z < size; z++)
        {
            float cz = (z - size / 2 + 0.5f) * 12f;
            for (int x = 0; x < size; x++)
            {
                float cx = (x - size / 2 + 0.5f) * 12f;
                for (int b = 0; b < blobs.Count; b++)
                {
                    Vector3 blob = blobs[b];
                    float dx = cx - blob.x, dz = cz - blob.y, r = blob.z;
                    if (Math.Abs(dx) > r * 1.6f || Math.Abs(dz) > r * 1.6f) continue;
                    float th = (float)Math.Atan2(dz, dx);
                    float reach = r * (1f + 0.16f * (float)Math.Sin(3 * th + b * 1.7f) + 0.09f * (float)Math.Sin(5 * th + b * 2.9f) + 0.05f * (float)Math.Sin(11 * th + b))
                        + ((float)value.Invoke(null, new object[] { cx / 260f, cz / 260f, 77 + b }) - 0.5f) * 0.25f * r;
                    if (dx * dx + dz * dz > reach * reach) continue;
                    bits[z * size + x] = true;
                    break;
                }
            }
        }
        return bits;
    }

    // --glimpse gx:gz (repeatable): game marks out in the unexplored land, glimpsed.
    static Vector2[] Glimpses(string[] args)
    {
        var glimpses = new System.Collections.Generic.List<Vector2>();
        for (int a = 2; a < args.Length - 1; a++)
        {
            if (args[a] != "--glimpse") continue;
            string[] q = args[a + 1].Split(':');
            glimpses.Add(new Vector2(float.Parse(q[0]), float.Parse(q[1])));
        }
        return glimpses.ToArray();
    }

    // Lays the fog (fog x fog RGBA, rows from the south) over an output as Dirt lays the dirt.
    static void OverlayFog(byte[] rgba, int n, float left, float top, int w, int scale, byte[] fogRgba, int fog)
    {
        const int canvas = 4096;
        var texel = new float[4];
        for (int oy = 0; oy < n; oy++)
        {
            float fy = (canvas - (top + w - (oy + 0.5f) / scale)) * fog / canvas - 0.5f;
            int y0 = Mathf.Clamp((int)Math.Floor(fy), 0, fog - 2);
            float ty = Mathf.Clamp01(fy - y0);
            for (int ox = 0; ox < n; ox++)
            {
                float fx = (left + (ox + 0.5f) / scale) * fog / canvas - 0.5f;
                int x0 = Mathf.Clamp((int)Math.Floor(fx), 0, fog - 2);
                float tx = Mathf.Clamp01(fx - x0);
                for (int c = 0; c < 4; c++)
                {
                    float a00 = fogRgba[(y0 * fog + x0) * 4 + c], a10 = fogRgba[(y0 * fog + x0 + 1) * 4 + c];
                    float a01 = fogRgba[((y0 + 1) * fog + x0) * 4 + c], a11 = fogRgba[((y0 + 1) * fog + x0 + 1) * 4 + c];
                    texel[c] = Lerp(Lerp(a00, a10, tx), Lerp(a01, a11, tx), ty);
                }
                float alpha = texel[3] / 255f;
                if (alpha <= 0f) continue;
                int o = (oy * n + ox) * 4;
                for (int c = 0; c < 3; c++) rgba[o + c] = (byte)(rgba[o + c] * (1f - alpha) + texel[c] * alpha + 0.5f);
            }
        }
    }

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

    // --alt NAME:wx:wz:radiusMetres (repeatable) lays vanilla's alt biome NAME (AltBiome.m_name,
    // such as "Kalhygge Black Forest") on the map cells of its own biome within the radius. The
    // game picks alt biomes with UnityEngine.Random, which the preview cannot run, so without it
    // the preview draws every wood plain.
    static void ForceAltBiomes(string[] args, object source, Type wsType, Type land, int size)
    {
        MethodInfo groveOf = wsType.GetMethod("GroveOf", All);
        Array groves = null;
        var biomes = (Array)Get(source, "m_biome");
        for (int a = 2; a < args.Length - 1; a++)
        {
            if (args[a] != "--alt") continue;
            string[] q = args[a + 1].Split(':');
            string name = string.Join(":", q, 0, q.Length - 3);
            float ax = float.Parse(q[q.Length - 3]), az = float.Parse(q[q.Length - 2]), radius = float.Parse(q[q.Length - 1]);
            var call = new object[] { name, null, null };
            if (!(bool)groveOf.Invoke(null, call))
            {
                System.Console.WriteLine($"ALT '{name}' does not change the woods");
                continue;
            }
            if (groves == null) groves = Array.CreateInstance(T("Atlas.Grove"), size * size);
            int want = Convert.ToInt32(call[1]), cells = 0;
            int x0 = Math.Max(0, (int)((ax - radius) / 12f) + size / 2 - 1), x1 = Math.Min(size - 1, (int)((ax + radius) / 12f) + size / 2 + 1);
            int z0 = Math.Max(0, (int)((az - radius) / 12f) + size / 2 - 1), z1 = Math.Min(size - 1, (int)((az + radius) / 12f) + size / 2 + 1);
            for (int z = z0; z <= z1; z++)
                for (int x = x0; x <= x1; x++)
                {
                    float cx = (x - size / 2 + 0.5f) * 12f, cz = (z - size / 2 + 0.5f) * 12f;
                    int i = z * size + x;
                    if ((cx - ax) * (cx - ax) + (cz - az) * (cz - az) > radius * radius || Convert.ToInt32(biomes.GetValue(i)) != want) continue;
                    groves.SetValue(call[2], i);
                    cells++;
                }
            System.Console.WriteLine($"ALT '{name}' {call[2]} on {cells} {Enum.ToObject(land, want)} cells");
        }
        if (groves != null) wsType.GetMethod("SetGroves", All).Invoke(source, new object[] { groves });
    }

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

    // Lays the sheet's dirt (Paper.Dirt, fog x fog RGBA, rows from the south) over an output of
    // n x n pixels whose rows also run from the south, covering the sheet square of width w from
    // (left, top) at scale output pixels per sheet pixel; bilinear as the game samples it.
    static void Dirt(byte[] rgba, int n, float left, float top, int w, int scale, byte[] dirt, int fog)
    {
        if (dirt == null) return;
        const int canvas = 4096;
        var texel = new float[4];
        for (int oy = 0; oy < n; oy++)
        {
            float fy = (canvas - (top + w - (oy + 0.5f) / scale)) * fog / canvas - 0.5f;
            int y0 = Mathf.Clamp((int)Math.Floor(fy), 0, fog - 2);
            float ty = Mathf.Clamp01(fy - y0);
            for (int ox = 0; ox < n; ox++)
            {
                float fx = (left + (ox + 0.5f) / scale) * fog / canvas - 0.5f;
                int x0 = Mathf.Clamp((int)Math.Floor(fx), 0, fog - 2);
                float tx = Mathf.Clamp01(fx - x0);
                for (int c = 0; c < 4; c++)
                {
                    float a00 = dirt[(y0 * fog + x0) * 4 + c], a10 = dirt[(y0 * fog + x0 + 1) * 4 + c];
                    float a01 = dirt[((y0 + 1) * fog + x0) * 4 + c], a11 = dirt[((y0 + 1) * fog + x0 + 1) * 4 + c];
                    texel[c] = Lerp(Lerp(a00, a10, tx), Lerp(a01, a11, tx), ty);
                }
                float alpha = texel[3] / 255f;
                if (alpha <= 0f) continue;
                int o = (oy * n + ox) * 4;
                for (int c = 0; c < 3; c++) rgba[o + c] = (byte)(rgba[o + c] * (1f - alpha) + texel[c] * alpha + 0.5f);
            }
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

    // --seacat <png>: the sea's decorative marks as the map draws them, as a board. Each sea
    // illustration (SeaGlyphs) lies on the water it is placed in (the open ocean's blue; the
    // bonemaw on the Ashlands' boiling sea, at full heat) with its darker wash (SeaWash), built
    // from its mark as the sheet builds it: at sheet size, the wash laid as the ink texture lays
    // it (Paper.SeaInk), and as a 4x close-up draws it, the pen thinned by the close-up's level
    // of detail and the wash glazed over the water (Paper.SeaGlaze); plain and mirrored, at
    // scale 1 and centred as DrawIllustrations centres its box. Then the Ashlands' simmer marks
    // (InkPainter.Simmer) for a row of seeds, and a sea wave group and the two lake hands' line
    // groups (InkPainter.DrawWave, rows by RowY, crests by Crests) off a straight stand-in shore
    // facing either way: in game their places come from the traced coast, which needs a world.
    // Seeds are fixed, so the board changes only when the drawing does.
    static int SeaCatalogue(string path)
    {
        const int big = 4, pad = 14, nameWidth = 230, header = 34, margin = 10;
        Type painterType = T("Atlas.InkPainter"), canvasType = T("Atlas.InkCanvas"), sea = T("Atlas.SeaGlyphs");
        Type paperType = T("Atlas.Paper"), washType = T("Atlas.SeaWash"), wsType = T("Atlas.WorldSource"), noise = T("Atlas.Noise");
        var names = (string[])sea.GetField("Names", All).GetValue(null);
        int bonemaw = (int)sea.GetField("BonemawSerpent", All).GetValue(null), leviathan = (int)sea.GetField("Leviathan", All).GetValue(null);
        MethodInfo drawGlyph = sea.GetMethod("Draw", All);
        MethodInfo boxOf = painterType.GetMethod("IllustrationBox", All);
        MethodInfo buildWash = washType.GetMethod("Build", All), washAt = washType.GetMethod("At", All);
        MethodInfo simmer = painterType.GetMethod("Simmer", All), drawWave = painterType.GetMethod("DrawWave", All);
        MethodInfo rowY = painterType.GetMethod("RowY", All), crestsOf = painterType.GetMethod("Crests", All);
        MethodInfo hashInt = noise.GetMethod("HashInt", All), unit = noise.GetMethod("Unit", All);
        MethodInfo stroke = canvasType.GetMethod("Stroke", All);
        float Const(string name) => Convert.ToSingle(painterType.GetField(name, All).GetValue(null));
        float mpp = Const("MetresPerPixel"), half = Const("HalfExtent");
        Type byRef = typeof(float).MakeByRefType();
        MethodInfo waterColor = paperType.GetMethod("WaterColor", All, null, new[] { typeof(float), typeof(float), typeof(float), byRef, byRef, byRef }, null);
        MethodInfo ashGlaze = paperType.GetMethod("AshSeaGlaze", All), seaGlaze = paperType.GetMethod("SeaGlaze", All);
        var paper = (Color32)paperType.GetField("PaperColor", All).GetValue(null);
        var inkColour = (Color32)paperType.GetField("InkColor", All).GetValue(null);
        var seaInk = new object[] { 0f, 0f, 0f, 0f };
        paperType.GetMethod("SeaInk", All).Invoke(null, seaInk);
        float seaAlpha = (float)seaInk[0];
        var seaColour = new Vector3((float)seaInk[1], (float)seaInk[2], (float)seaInk[3]);

        // The water over clean parchment (Paper.WaterColor), warmed by the boiling sea at full
        // heat where ash is set (Paper.AshSeaGlaze), as the wash under the sheet lays it.
        Vector3 Water(bool ash)
        {
            var args = new object[] { (float)paper.r, (float)paper.g, (float)paper.b, 0f, 0f, 0f };
            waterColor.Invoke(null, args);
            if (!ash) return new Vector3((float)args[3], (float)args[4], (float)args[5]);
            var warm = new object[] { args[3], args[4], args[5], 1f, 0f, 0f, 0f };
            ashGlaze.Invoke(null, warm);
            return new Vector3((float)warm[4], (float)warm[5], (float)warm[6]);
        }

        // A close-up draws at lod sqrt(scale / 2) past 2x (DetailTile.Render), its pens 1 / lod
        // as wide (the tile painter sets InkCanvas.m_penScale); the sheet at 1.
        float Lod(int scale) => Mathf.Sqrt(Mathf.Max(1f, scale * 0.5f));
        ConstructorInfo canvasNew = canvasType.GetConstructor(new[] { typeof(int), typeof(int), typeof(float), typeof(float), typeof(float) });
        ConstructorInfo painterNew = painterType.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { wsType, canvasType, T("Atlas.TileGrid"), typeof(float), typeof(RectInt), typeof(CancellationToken) }, null);
        object world = Activator.CreateInstance(wsType, true);
        object NewCanvas(int w, int h, float left, float top, int scale)
        {
            object canvas = canvasNew.Invoke(new object[] { w * scale, h * scale, left, top, (float)scale });
            Set(canvas, "m_penScale", 1f / Lod(scale));
            return canvas;
        }
        // A tile painter on the canvas: it draws the simmer marks and waves in the canvas's pen.
        object Painter(object canvas, int scale) =>
            painterNew.Invoke(new object[] { world, canvas, null, Lod(scale), new RectInt(0, 0, 0, 0), CancellationToken.None });

        // The patch of w x h sheet pixels from (left, top) at scale as the map shows it: the water
        // (parchment on the stand-in shore's land side, land -1 west or 1 east of shore), the sea
        // wash where wash is given, then the ink.
        Bitmap Patch(object canvas, int w, int h, float left, float top, int scale, bool ash, object wash, float shore, int land)
        {
            int pw = w * scale, ph = h * scale;
            var ink = (byte[])Get(canvas, "m_ink");
            Vector3 water = Water(ash), clean = new Vector3(paper.r, paper.g, paper.b), pen = new Vector3(inkColour.r, inkColour.g, inkColour.b);
            var bitmap = new Bitmap(pw, ph, PixelFormat.Format24bppRgb);
            BitmapData data = bitmap.LockBits(new Rectangle(0, 0, pw, ph), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            var row = new byte[data.Stride];
            for (int y = 0; y < ph; y++)
            {
                float sy = top + (y + 0.5f) / scale;
                for (int x = 0; x < pw; x++)
                {
                    float sx = left + (x + 0.5f) / scale;
                    float a = ink[y * pw + x] / 255f;
                    bool dry = land != 0 && (sx - shore) * land > 0f;
                    Vector3 c = dry ? clean : water;
                    float amount = wash == null || dry ? 0f : (float)washAt.Invoke(wash, new object[] { sx, sy });
                    if (scale == 1)
                    {
                        // The sheet's ink texture: the wash's colour at its opacity under the ink,
                        // laid over the paper (Paper.InkWithMips).
                        float su = amount * seaAlpha * (1f - a);
                        c = c * (1f - Mathf.Min(1f, su + a)) + seaColour * su + pen * a;
                    }
                    else
                    {
                        if (amount > 0f)
                        {
                            var args = new object[] { c.x, c.y, c.z, amount, 0f, 0f, 0f };
                            seaGlaze.Invoke(null, args);
                            c = new Vector3((float)args[4], (float)args[5], (float)args[6]);
                        }
                        c += (pen - c) * a;
                    }
                    row[x * 3] = (byte)(c.z + 0.5f);
                    row[x * 3 + 1] = (byte)(c.y + 0.5f);
                    row[x * 3 + 2] = (byte)(c.x + 0.5f);
                }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
            bitmap.UnlockBits(data);
            return bitmap;
        }

        // Each illustration's box at scale 1, plain and mirrored (InkPainter.IllustrationBox).
        Rect Box(int kind, bool mirror)
        {
            object box = boxOf.Invoke(null, new object[] { kind, mirror, 1f });
            float l = (float)Get(box, "m_left"), t = (float)Get(box, "m_top");
            return new Rect(l, t, (float)Get(box, "m_right") - l, (float)Get(box, "m_bottom") - t);
        }

        // The patch round one illustration: the glyph centred on a spot as DrawIllustrations
        // centres it, its wash built from the mark the sheet would keep (SeaWash.Build). Each
        // kind has a spot of its own, so its wash is mottled as one placed there.
        Bitmap Illustration(int kind, bool mirror, int scale, int w, int h)
        {
            var at = new Vector2(1024.5f + 512f * kind, 1536.5f);
            Rect box = Box(kind, mirror);
            float x = at.x - box.center.x, y = at.y - box.center.y;
            object mark = Activator.CreateInstance(T("Atlas.SeaMark"));
            Set(mark, "m_west", (x + box.xMin) * mpp - half);
            Set(mark, "m_east", (x + box.xMax) * mpp - half);
            Set(mark, "m_north", half - (y + box.yMin) * mpp);
            Set(mark, "m_south", half - (y + box.yMax) * mpp);
            Set(mark, "m_kind", kind);
            Set(mark, "m_x", x);
            Set(mark, "m_y", y);
            Set(mark, "m_mirror", mirror);
            Set(mark, "m_scale", 1f);
            object wash = buildWash.Invoke(null, new[] { mark });
            float left = Mathf.Floor(at.x - w * 0.5f), top = Mathf.Floor(at.y - h * 0.5f);
            object canvas = NewCanvas(w, h, left, top, scale);
            drawGlyph.Invoke(null, new object[] { canvas, kind, x, y, mirror, 1f });
            return Patch(canvas, w, h, left, top, scale, kind == bonemaw, wash, 0f, 0);
        }

        // A row of simmer marks, one per seed, as DrawBoiling seeds them from its grid.
        const int boils = 6, boilStep = 26, boilHeight = 30;
        Bitmap Boiling(int scale)
        {
            int w = boils * boilStep + 2 * margin;
            const float left = 2000f, top = 3000f;
            object canvas = NewCanvas(w, boilHeight, left, top, scale);
            object painter = Painter(canvas, scale);
            int salt = unchecked(12345 + 62 * 7919);
            for (int k = 0; k < boils; k++)
            {
                int seed = (int)hashInt.Invoke(null, new object[] { k, 0, salt });
                simmer.Invoke(painter, new object[] { left + margin + (k + 0.5f) * boilStep, top + 18f, seed });
            }
            return Patch(canvas, w, boilHeight, left, top, scale, true, null, 0f, 0);
        }

        // One group of a hand's strokes off a straight shore at x = shore with the water on its
        // dir side: the sea's on rows from the middle down (PlaceGroup, a coast facing east or
        // west), a lake's as one stack from the middle row out, below and above in turn
        // (LakeStack), long and short rows in turn, each started as Anchored starts it.
        const int waveWidth = 34, waveHeight = 30;
        float rowPitch = Const("WaveRowPitch"), shoreGap = Const("WaveShoreGap"), overlap = Const("LakeOverlap");
        Bitmap Waves(string hand, int count, int dir, int scale)
        {
            float left = 2400f, top = 3000f + 40f * count;
            float shore = dir > 0 ? left + 6f : left + waveWidth - 6f;
            object canvas = NewCanvas(waveWidth, waveHeight, left, top, scale);
            object painter = Painter(canvas, scale);
            object style = painterType.GetField(hand, All).GetValue(null);
            Set(painter, "m_hand", style);
            bool lake = (bool)Get(style, "m_lake");
            float crest = (float)Get(style, "m_unit"), lift = (float)Get(style, "m_lift");
            // The stand-in shore, in the coast's pen: CoastWidth on the sheet, the tile's Pen in
            // a close-up (DrawTileCoast), thinned by the canvas's pen scale.
            stroke.Invoke(canvas, new object[] { shore, top, shore, top + waveHeight, scale == 1 ? Const("CoastWidth") : 1f, 1f });
            int salt = unchecked(12345 + 9 * 7919);
            int mid = Mathf.FloorToInt((top + waveHeight * 0.5f) / rowPitch);
            var rows = new System.Collections.Generic.List<int>();
            var longRows = new System.Collections.Generic.List<bool>();
            if (lake)
            {
                rows.Add(mid);
                longRows.Add(true);
                for (int k = 1; rows.Count < count; k++)
                {
                    rows.Add(mid + k);
                    longRows.Add((k & 1) == 0);
                    if (rows.Count == count) break;
                    rows.Add(mid - k);
                    longRows.Add((k & 1) == 0);
                }
            }
            else
            {
                for (int k = 0; k < count; k++)
                {
                    rows.Add(mid - count / 2 + k);
                    longRows.Add((k & 1) == 0);
                }
            }
            for (int i = 0; i < rows.Count; i++)
            {
                int r = rows[i];
                float y = (float)rowY.Invoke(painter, new object[] { r, salt });
                // A sea stroke starts WaveShoreGap off the shore, a lake's at the shore itself.
                float start = lake ? shore : shore + dir * shoreGap;
                int seed = (int)hashInt.Invoke(null, new object[] { r, Mathf.RoundToInt(start) * dir, salt + 7 });
                int crests = (int)crestsOf.Invoke(painter, new object[] { seed, longRows[i] });
                float length = crests * crest * (1f + ((float)unit.Invoke(null, new object[] { seed, 3 }) - 0.5f) * 0.2f);
                float x0 = dir > 0 ? start : start - length;
                if (lake)
                {
                    length += overlap;
                    if (dir > 0) x0 -= overlap;
                }
                drawWave.Invoke(painter, new object[] { x0, y, length, crests, lift, seed, salt, dir });
            }
            return Patch(canvas, waveWidth, waveHeight, left, top, scale, false, null, shore, -dir);
        }

        // Every illustration's patch: its larger box, plain or mirrored, and a margin round it.
        var patchW = new int[names.Length];
        var patchH = new int[names.Length];
        for (int k = 0; k < names.Length; k++)
        {
            Rect a = Box(k, false), b = Box(k, true);
            patchW[k] = Mathf.CeilToInt(Mathf.Max(a.width, b.width)) + 2 * margin;
            patchH[k] = Mathf.CeilToInt(Mathf.Max(a.height, b.height)) + 2 * margin;
        }
        int widest = Mathf.Max(Mathf.Max(patchW), waveWidth);
        int sheetColumn = widest + pad, bigColumn = widest * big + pad;
        int c1 = nameWidth, c2 = c1 + sheetColumn, c3 = c2 + sheetColumn, c4 = c3 + bigColumn;
        // The boil marks' close-up strip runs on across both close-up columns.
        int width = Mathf.Max(c4 + bigColumn, c3 + (boils * boilStep + 2 * margin) * big) + pad;
        var heights = new System.Collections.Generic.List<int>();
        for (int k = 0; k < names.Length; k++) heights.Add(patchH[k] * big + pad);
        heights.Add(boilHeight * big + pad);
        for (int k = 0; k < 3; k++) heights.Add(waveHeight * big + pad);
        int height = header + 40;
        foreach (int h in heights) height += h;

        var paperColor = System.Drawing.Color.FromArgb(paper.r, paper.g, paper.b);
        using (var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb))
        using (var g = System.Drawing.Graphics.FromImage(bitmap))
        using (var font = new System.Drawing.Font("Palatino Linotype", 15f, GraphicsUnit.Pixel))
        using (var small = new System.Drawing.Font("Palatino Linotype", 12f, System.Drawing.FontStyle.Italic, GraphicsUnit.Pixel))
        using (var ink = new SolidBrush(System.Drawing.Color.FromArgb(inkColour.r, inkColour.g, inkColour.b)))
        {
            g.Clear(paperColor);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.DrawString("sea mark", font, ink, 8, 8);
            g.DrawString("sheet", font, ink, c1 + 4, 8);
            g.DrawString("mirrored", font, ink, c2 + 4, 8);
            g.DrawString("close-up, 4x", font, ink, c3 + 4, 8);
            g.DrawString("mirrored", font, ink, c4 + 4, 8);

            // Lays a patch centred in its column's cell of the row at top.
            void Place(Bitmap patch, int column, int top, int rowHeight)
            {
                int x = column, y = top + (rowHeight - patch.Height) / 2;
                g.DrawImage(patch, new Rectangle(x, y, patch.Width, patch.Height));
                patch.Dispose();
            }
            void Label(int top, int rowHeight, string name, params string[] notes)
            {
                int y = top + rowHeight / 2 - 10 - 8 * notes.Length;
                g.DrawString(name, font, ink, 8, y);
                for (int i = 0; i < notes.Length; i++) g.DrawString(notes[i], small, ink, 8, y + 20 + 16 * i);
            }

            int rowTop = header, next = 0;
            for (int k = 0; k < names.Length; k++)
            {
                int rowHeight = heights[next++];
                Rect box = Box(k, false);
                Label(rowTop, rowHeight, names[k],
                    k == bonemaw ? "the Ashlands' boiling sea" : "the open ocean",
                    $"{box.width:0} x {box.height:0} sheet px box",
                    k == leviathan ? "mirrored: shell flipped, shade kept east" : "with its sea wash (SeaWash)");
                Place(Illustration(k, false, 1, patchW[k], patchH[k]), c1, rowTop, rowHeight);
                Place(Illustration(k, true, 1, patchW[k], patchH[k]), c2, rowTop, rowHeight);
                Place(Illustration(k, false, big, patchW[k], patchH[k]), c3, rowTop, rowHeight);
                Place(Illustration(k, true, big, patchW[k], patchH[k]), c4, rowTop, rowHeight);
                rowTop += rowHeight;
            }

            int boilRow = heights[next++];
            Label(rowTop, boilRow, "Boil marks", "the Ashlands' boiling sea", $"{boils} seeds (Simmer)", "sheet, then close-up 4x");
            Place(Boiling(1), c1, rowTop, boilRow);
            Place(Boiling(big), c3, rowTop, boilRow);
            rowTop += boilRow;

            var waves = new[]
            {
                new object[] { "s_seaHand", 5, "Sea waves", "a coast's group of 5" },
                new object[] { "s_lakeHand", 5, "Lake lines, large lake", "a group of 5" },
                new object[] { "s_smallLakeHand", 4, "Lake lines, small lake", "a group of 4" },
            };
            foreach (object[] wave in waves)
            {
                int rowHeight = heights[next++];
                Label(rowTop, rowHeight, (string)wave[2], (string)wave[3], "off a straight stand-in shore", "facing east; mirrored: west");
                Place(Waves((string)wave[0], (int)wave[1], 1, 1), c1, rowTop, rowHeight);
                Place(Waves((string)wave[0], (int)wave[1], -1, 1), c2, rowTop, rowHeight);
                Place(Waves((string)wave[0], (int)wave[1], 1, big), c3, rowTop, rowHeight);
                Place(Waves((string)wave[0], (int)wave[1], -1, big), c4, rowTop, rowHeight);
                rowTop += rowHeight;
            }
            g.DrawString("Waves and lake lines are placed at the traced coast's bends in game (it needs a world); here only their rows, crests and strokes are the mod's.", small, ink, 8, rowTop + 8);
            bitmap.Save(path, ImageFormat.Png);
        }
        System.Console.WriteLine(path);
        return 0;
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
