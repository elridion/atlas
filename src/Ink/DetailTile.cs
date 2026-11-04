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
        // Worker thread. Returns RGBA rows from south to north, as LoadRawTextureData expects, for
        // the sheet rectangle region at scale pixels per sheet pixel. The ground is sampled every
        // gridPixels tile pixels on up to threads threads. The level of detail grows with the
        // square root of the zoom past 2x (where the tile draws what the sheet draws, only
        // sharper): trees true to scale at 8x pack into a uniform texture, so glyphs are let to
        // grow a little on screen instead.
        public static byte[] Render(InkRecord record, Paper.Wash wash, WorldSource world, SampleCache cache, RectInt region,
            int scale, float gridPixels, int threads, CancellationToken cancel, byte[] glimpse = null)
        {
            int left = region.x, top = region.y, width = region.width, height = region.height;
            int w = width * scale, h = height * scale;
            float lod = Mathf.Sqrt(Mathf.Max(1f, scale * 0.5f));
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
            InkPainter.PaintTile(world, record, grid, canvas, region, scatter, lod, scale, cancel);
            cancel.ThrowIfCancellationRequested();

            int paper = Paper.FogSize;
            float perSheet = paper / (float)InkPainter.CanvasSize;
            Color32 ink = Paper.InkColor, pinInk = Paper.PinInkColor, glimpseInk = Paper.GlimpseInkColor;
            // The frame's radius in sheet pixels: beyond it is the sheet's bare margin, not water.
            float frame = InkPainter.EdgeRadius / InkPainter.MetresPerPixel;
            var rgba = new byte[w * h * 4];
            var seaWashes = new List<SeaWash>();
            record.SeaWashes(left, top, left + width, top + height, seaWashes);
            for (int ty = 0; ty < h; ty++)
            {
                // Parchment rows run from the south, the sheet's from the north.
                float py = (InkPainter.CanvasSize - (top + (ty + 0.5f) / scale)) * perSheet - 0.5f;
                int row = h - 1 - ty;
                float sy = top + (ty + 0.5f) / scale;
                for (int tx = 0; tx < w; tx++)
                {
                    float sx = left + (tx + 0.5f) / scale;
                    float px = sx * perSheet - 0.5f;
                    float a = canvas.m_ink[ty * w + tx] / 255f;
                    int o = (row * w + tx) * 4;
                    // The land's wash, with the tile's own water over it: its fine heights put the
                    // blue exactly where its coast runs, which the paper's 10 m pixels cannot.
                    float depth = world.m_waterLevel - grid.Sample(grid.m_height, sx, sy);
                    float ex = sx - InkPainter.CanvasSize * 0.5f, ey = sy - InkPainter.CanvasSize * 0.5f;
                    if (ex * ex + ey * ey >= frame * frame) depth = float.NegativeInfinity;
                    // The depth's change over one tile pixel sets the edge's one-pixel ramp. Streams
                    // show in the tile's own heights; the paper's stream band is for the sheet.
                    float step = 1f / scale;
                    float slope = Mathf.Max(Mathf.Abs(grid.Sample(grid.m_height, sx + step, sy) - grid.Sample(grid.m_height, sx, sy)),
                        Mathf.Abs(grid.Sample(grid.m_height, sx, sy + step) - grid.Sample(grid.m_height, sx, sy)));
                    float water = Paper.Waterness(depth, slope, 0f);
                    float lr = PaperAt(wash.m_land, paper, px, py, 0), lg = PaperAt(wash.m_land, paper, px, py, 1), lb = PaperAt(wash.m_land, paper, px, py, 2);
                    if (water > 0f)
                    {
                        Paper.WaterColor(PaperAt(wash.m_parchment, paper, px, py, 0), PaperAt(wash.m_parchment, paper, px, py, 1),
                            PaperAt(wash.m_parchment, paper, px, py, 2), out float wr, out float wg, out float wb);
                        lr += (wr - lr) * water;
                        lg += (wg - lg) * water;
                        lb += (wb - lb) * water;
                    }
                    // The glimpse round the explored land: bare parchment over the ground's wash,
                    // the water and the glyphs' bodies, with the terrain's ink greyer, as the sheet
                    // lays it (Paper.InkWithMips).
                    float g = glimpse != null ? GlimpseAt(glimpse, paper, px, py) : 0f;
                    float open = 1f - g;
                    if (g > 0f)
                    {
                        lr += (PaperAt(wash.m_parchment, paper, px, py, 0) - lr) * g;
                        lg += (PaperAt(wash.m_parchment, paper, px, py, 1) - lg) * g;
                        lb += (PaperAt(wash.m_parchment, paper, px, py, 2) - lb) * g;
                    }
                    // A peak's or spire's body takes its kind's glaze strictly, over the stained
                    // paper (Paper.BodyGlaze), as the sheet fills it with BodyColor.
                    for (var kind = BodyKind.Mountain; kind <= BodyKind.Mist; kind++)
                    {
                        float body = canvas.BodyMask(kind)[ty * w + tx] / 255f * open;
                        if (body <= 0f) continue;
                        Paper.BodyGlaze(PaperAt(wash.m_parchment, paper, px, py, 0), PaperAt(wash.m_parchment, paper, px, py, 1),
                            PaperAt(wash.m_parchment, paper, px, py, 2), kind, out float br, out float bg, out float bb);
                        lr += (br - lr) * body;
                        lg += (bg - lg) * body;
                        lb += (bb - lb) * body;
                    }
                    // A tree's crown takes its biome's wood wash the same way (Paper.WoodGlaze).
                    byte wood = canvas.m_bodyWood[ty * w + tx];
                    if (wood != 0)
                    {
                        float body = InkCanvas.WoodCover(wood) * open;
                        Paper.WoodGlaze(PaperAt(wash.m_parchment, paper, px, py, 0), PaperAt(wash.m_parchment, paper, px, py, 1),
                            PaperAt(wash.m_parchment, paper, px, py, 2), InkCanvas.WoodLand(wood), out float br, out float bg, out float bb);
                        lr += (br - lr) * body;
                        lg += (bg - lg) * body;
                        lb += (bb - lb) * body;
                    }
                    // A sea illustration's darker wash, sampled from the same mask the sheet lays.
                    foreach (SeaWash sea in seaWashes)
                    {
                        float amount = sea.At(sx, sy) * open;
                        if (amount > 0f) Paper.SeaGlaze(lr, lg, lb, amount, out lr, out lg, out lb);
                    }
                    // A pin's red ink, under any black ink drawn over it later.
                    float red = canvas.m_pinInk[ty * w + tx] / 255f;
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


        // Bilinear sample of the glimpse's opacity (RGBA rows from the south, as the parchment's).
        private static float GlimpseAt(byte[] glimpse, int size, float x, float y)
        {
            x = Mathf.Clamp(x, 0f, size - 1.001f);
            y = Mathf.Clamp(y, 0f, size - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int i = (y0 * size + x0) * 4 + 3;
            int right = 4, down = size * 4;
            float bottom = glimpse[i] + (glimpse[i + right] - glimpse[i]) * fx;
            float topRow = glimpse[i + down] + (glimpse[i + down + right] - glimpse[i + down]) * fx;
            return (bottom + (topRow - bottom) * fy) / 255f;
        }

        // Bilinear parchment sample, so the paper's grain stays smooth at any magnification.
        private static float PaperAt(byte[] parchment, int size, float x, float y, int channel)
        {
            x = Mathf.Clamp(x, 0f, size - 1.001f);
            y = Mathf.Clamp(y, 0f, size - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int i = (y0 * size + x0) * 3 + channel;
            int right = 3, down = size * 3;
            float bottom = parchment[i] + (parchment[i + right] - parchment[i]) * fx;
            float topRow = parchment[i + down] + (parchment[i + down + right] - parchment[i + down]) * fx;
            return bottom + (topRow - bottom) * fy;
        }
    }
}
