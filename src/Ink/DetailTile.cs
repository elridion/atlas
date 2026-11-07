using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A part of the sheet drawn again at the magnification it is shown at (InkPainter.PaintTile),
    /// so zooming in shows finer detail instead of the sheet's pixels. The tile is opaque (ink
    /// laid on the paper), so it hides the coarser sheet beneath it; the fog still lies on top.
    /// </summary>
    internal static class DetailTile
    {
        // The widest scatter grid a close-up's trees stand on, in sheet pixels (the oaks' 96 m,
        // the giants' steep grid): a tile visits a cell or two of it past its scatter area.
        private const float WidestScatterCell = 20f;

        // The sheet rectangle over which a close-up of region at scale reads the fog's planes
        // (FogChange.Affects): its own pixels for the glimpse, and every tree it scatters, judged
        // by Paper.CutByFog over its footprint, whose foot stands up to its reach and two scatter
        // cells past the scatter area (InkPainter.Scatter) and whose crown rises a reach above it;
        // the scarps, stumps and grass that keep off those trees stand within the same margins.
        // Generous on purpose: a close-up kept wrongly would show the old fog until it is drawn
        // again.
        public static Rect FogReach(InkRecord record, RectInt region, int scale)
        {
            float margin = InkPainter.TileMargin(Lod(scale));
            Rect scatter = InkPainter.ScatterArea(record, region, margin);
            float grow = 2f * margin + 2f * WidestScatterCell;
            return Rect.MinMaxRect(scatter.xMin - grow, scatter.yMin - grow, scatter.xMax + grow, scatter.yMax + grow);
        }

        internal static float Lod(int scale) => Mathf.Sqrt(Mathf.Max(1f, scale * 0.5f));

        // Worker thread. Returns RGBA rows from south to north, as LoadRawTextureData expects, for
        // the sheet rectangle region at scale pixels per sheet pixel. The ground is sampled every
        // gridPixels tile pixels on up to threads threads. The level of detail grows with the
        // square root of the zoom past 2x (where the tile draws what the sheet draws, only
        // sharper): trees true to scale at 8x pack into a uniform texture, so glyphs are let to
        // grow a little on screen instead. glimpse and edge are Paper.Fog's, hiddenGroups the
        // record groups edge leaves out (FogEdge.HiddenGroups); null for none.
        public static byte[] Render(InkRecord record, Paper.Wash wash, WorldSource world, SampleCache cache, RectInt region,
            int scale, float gridPixels, int threads, CancellationToken cancel, byte[] glimpse = null, FogEdge edge = null,
            HashSet<int> hiddenGroups = null)
        {
            int left = region.x, top = region.y, width = region.width, height = region.height;
            int w = width * scale, h = height * scale;
            float lod = Lod(scale);
            // The cache spacing nearest gridPixels tile pixels.
            float metres = gridPixels / scale * InkPainter.MetresPerPixel;
            int level = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(metres, 2f)), 0, SampleCache.Levels - 1);
            float margin = InkPainter.TileMargin(lod);
            // Round a pin near the tile, the ground is sampled far enough for every tree that may
            // stand in front of it, so each tile decides its trees alike.
            Rect scatter = InkPainter.ScatterArea(record, region, margin);
            TileGrid grid = TileGrid.Build(cache, world.m_waterLevel, scatter.xMin - margin, scatter.yMin - margin, scatter.xMax + margin, scatter.yMax + margin,
                level, threads, cancel);
            var canvas = new InkCanvas(w, h, left, top, scale);
            InkPainter.PaintTile(world, record, grid, canvas, region, scatter, lod, scale, cancel, glimpse, edge, hiddenGroups);
            cancel.ThrowIfCancellationRequested();

            int paper = Paper.FogSize;
            float perSheet = paper / (float)InkPainter.CanvasSize;
            Color32 ink = Paper.InkColor, pinInk = Paper.PinInkColor, glimpseInk = Paper.GlimpseInkColor;
            // The frame's radius in sheet pixels: beyond it is the sheet's bare margin, not water.
            float frame = InkPainter.EdgeRadius / InkPainter.MetresPerPixel;
            var rgba = new byte[w * h * 4];
            var seaWashes = new List<SeaWash>();
            record.SeaWashes(left, top, left + width, top + height, seaWashes);
            // The washes of the sea illustrations the fog leaves out go with them.
            if (edge != null) seaWashes.RemoveAll(sea => edge.HidesSea(sea.Left, sea.Top, sea.Right, sea.Bottom));
            // The paper's bilinear weights depend on the column and the row alone, so they are
            // worked out once each, and every paper sample of a pixel shares them; the parchment
            // under a pixel is read once, when first needed, instead of up to five times over (the
            // arithmetic, and so every pixel, is unchanged). The colour pass was about half of a
            // close-up's time in game.
            float limit = paper - 1.001f;
            var columnCell = new int[w];
            var columnFrac = new float[w];
            for (int tx = 0; tx < w; tx++)
            {
                float px = Mathf.Clamp((left + (tx + 0.5f) / scale) * perSheet - 0.5f, 0f, limit);
                columnCell[tx] = (int)px;
                columnFrac[tx] = px - (int)px;
            }
            float step = 1f / scale;
            byte[] land = wash.m_land, parchment = wash.m_parchment;
            for (int ty = 0; ty < h; ty++)
            {
                // A tile the view has left is cancelled (DetailTiles.Cancel); the colour pass is
                // about half its time, so it stops here too.
                if ((ty & 31) == 0) cancel.ThrowIfCancellationRequested();
                // Parchment rows run from the south, the sheet's from the north.
                float py = Mathf.Clamp((InkPainter.CanvasSize - (top + (ty + 0.5f) / scale)) * perSheet - 0.5f, 0f, limit);
                int y0 = (int)py;
                float fy = py - y0;
                int row = h - 1 - ty;
                float sy = top + (ty + 0.5f) / scale;
                for (int tx = 0; tx < w; tx++)
                {
                    float sx = left + (tx + 0.5f) / scale;
                    int x0 = columnCell[tx];
                    float fx = columnFrac[tx];
                    int cell = y0 * paper + x0;
                    int p = ty * w + tx;
                    float a = canvas.m_ink[p] / 255f;
                    int o = (row * w + tx) * 4;
                    // The land's wash, with the tile's own water over it: its fine heights put the
                    // blue exactly where its coast runs, which the paper's 10 m pixels cannot.
                    float ground = grid.Sample(grid.m_height, sx, sy);
                    float depth = world.m_waterLevel - ground;
                    float ex = sx - InkPainter.CanvasSize * 0.5f, ey = sy - InkPainter.CanvasSize * 0.5f;
                    if (ex * ex + ey * ey >= frame * frame) depth = float.NegativeInfinity;
                    // The depth's change over one tile pixel sets the edge's one-pixel ramp. Streams
                    // show in the tile's own heights; the paper's stream band is for the sheet.
                    float slope = Mathf.Max(Mathf.Abs(grid.Sample(grid.m_height, sx + step, sy) - ground),
                        Mathf.Abs(grid.Sample(grid.m_height, sx, sy + step) - ground));
                    float water = Paper.Waterness(depth, slope, 0f);
                    float lr = Bilinear(land, cell * 3, 3, paper * 3, fx, fy);
                    float lg = Bilinear(land, cell * 3 + 1, 3, paper * 3, fx, fy);
                    float lb = Bilinear(land, cell * 3 + 2, 3, paper * 3, fx, fy);
                    bool haveParchment = false;
                    float pr = 0f, pg = 0f, pb = 0f;
                    if (water > 0f)
                    {
                        Parchment(parchment, cell, paper, fx, fy, ref haveParchment, ref pr, ref pg, ref pb);
                        Paper.WaterColor(pr, pg, pb, world, sx * InkPainter.MetresPerPixel - InkPainter.HalfExtent,
                            InkPainter.HalfExtent - sy * InkPainter.MetresPerPixel, out float wr, out float wg, out float wb);
                        lr += (wr - lr) * water;
                        lg += (wg - lg) * water;
                        lb += (wb - lb) * water;
                    }
                    // The glimpse round the explored land: bare parchment over the ground's wash,
                    // the water and the glyphs' bodies, with the terrain's ink greyer, as the sheet
                    // lays it (Paper.InkWithMips).
                    float g = glimpse != null ? Bilinear(glimpse, cell * 4 + 3, 4, paper * 4, fx, fy) / 255f : 0f;
                    float open = 1f - g;
                    if (g > 0f)
                    {
                        Parchment(parchment, cell, paper, fx, fy, ref haveParchment, ref pr, ref pg, ref pb);
                        lr += (pr - lr) * g;
                        lg += (pg - lg) * g;
                        lb += (pb - lb) * g;
                    }
                    // A peak's or spire's body takes its kind's glaze strictly, over the stained
                    // paper (Paper.BodyGlaze), as the sheet fills it with BodyColor.
                    for (var kind = BodyKind.Mountain; kind <= BodyKind.Mist; kind++)
                    {
                        float body = canvas.BodyMask(kind)[p] / 255f * open;
                        if (body <= 0f) continue;
                        Parchment(parchment, cell, paper, fx, fy, ref haveParchment, ref pr, ref pg, ref pb);
                        Paper.BodyGlaze(pr, pg, pb, kind, out float br, out float bg, out float bb);
                        lr += (br - lr) * body;
                        lg += (bg - lg) * body;
                        lb += (bb - lb) * body;
                    }
                    // A tree's crown takes its biome's wood wash the same way (Paper.WoodGlaze).
                    byte wood = canvas.m_bodyWood[p];
                    if (wood != 0)
                    {
                        float body = InkCanvas.WoodCover(wood) * open;
                        Parchment(parchment, cell, paper, fx, fy, ref haveParchment, ref pr, ref pg, ref pb);
                        Paper.WoodGlaze(pr, pg, pb, InkCanvas.WoodLand(wood), out float br, out float bg, out float bb);
                        lr += (br - lr) * body;
                        lg += (bg - lg) * body;
                        lb += (bb - lb) * body;
                    }
                    // A sea mark's washes (the body's grey, the blue wedge), sampled from the same
                    // masks the sheet lays.
                    foreach (SeaWash sea in seaWashes)
                    {
                        float amount = sea.At(sx, sy) * open, wedge = sea.WedgeAt(sx, sy) * open;
                        if (amount > 0f || wedge > 0f) Paper.SeaMarkGlaze(lr, lg, lb, amount, wedge, out lr, out lg, out lb);
                    }
                    // A pin's red ink, under any black ink drawn over it later.
                    float red = canvas.m_pinInk[p] / 255f;
                    if (red > 0f)
                    {
                        lr += (pinInk.r - lr) * red;
                        lg += (pinInk.g - lg) * red;
                        lb += (pinInk.b - lb) * red;
                    }
                    float ir = ink.r + (glimpseInk.r - ink.r) * g;
                    float ig = ink.g + (glimpseInk.g - ink.g) * g;
                    float ib = ink.b + (glimpseInk.b - ink.b) * g;
                    rgba[o] = (byte)(lr + (ir - lr) * a + 0.5f);
                    rgba[o + 1] = (byte)(lg + (ig - lg) * a + 0.5f);
                    rgba[o + 2] = (byte)(lb + (ib - lb) * a + 0.5f);
                    rgba[o + 3] = 255;
                }
            }
            return rgba;
        }


        // A bilinear sample of one channel at index i (the cell's lower left), right and down
        // apart from its neighbours, with the cell's weights.
        private static float Bilinear(byte[] values, int i, int right, int down, float fx, float fy)
        {
            float bottom = values[i] + (values[i + right] - values[i]) * fx;
            float topRow = values[i + down] + (values[i + down + right] - values[i + down]) * fx;
            return bottom + (topRow - bottom) * fy;
        }

        // The parchment under a pixel, read on first use, so the paper's grain stays smooth at any
        // magnification.
        private static void Parchment(byte[] parchment, int cell, int size, float fx, float fy, ref bool have, ref float r, ref float g, ref float b)
        {
            if (have) return;
            have = true;
            r = Bilinear(parchment, cell * 3, 3, size * 3, fx, fy);
            g = Bilinear(parchment, cell * 3 + 1, 3, size * 3, fx, fy);
            b = Bilinear(parchment, cell * 3 + 2, 3, size * 3, fx, fy);
        }    }
}
