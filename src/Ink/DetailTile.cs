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
        // sharper): trees true to scale at 8x packed into a uniform texture, so glyphs are let to
        // grow a little on screen instead.
        public static byte[] Render(InkRecord record, byte[] parchment, WorldSource world, SampleCache cache, RectInt region,
            int scale, float gridPixels, int threads, CancellationToken cancel)
        {
            int left = region.x, top = region.y, width = region.width, height = region.height;
            int w = width * scale, h = height * scale;
            float lod = Mathf.Sqrt(Mathf.Max(1f, scale * 0.5f));
            // The cache spacing nearest gridPixels tile pixels.
            float metres = gridPixels / scale * InkPainter.MetresPerPixel;
            int level = Mathf.Clamp(Mathf.RoundToInt(Mathf.Log(metres, 2f)), 0, SampleCache.Levels - 1);
            float margin = InkPainter.TileMargin(lod);
            TileGrid grid = TileGrid.Build(cache, world.m_waterLevel, left - margin, top - margin, left + width + margin, top + height + margin,
                level, threads, cancel);
            var canvas = new InkCanvas(w, h, left, top, scale);
            InkPainter.PaintTile(world, record, grid, canvas, region, lod, cancel);
            cancel.ThrowIfCancellationRequested();

            int paper = Paper.FogSize;
            float perSheet = paper / (float)InkPainter.CanvasSize;
            Color32 ink = Paper.InkColor;
            var rgba = new byte[w * h * 4];
            for (int ty = 0; ty < h; ty++)
            {
                // Parchment rows run from the south, the sheet's from the north.
                float py = (InkPainter.CanvasSize - (top + (ty + 0.5f) / scale)) * perSheet - 0.5f;
                int row = h - 1 - ty;
                for (int tx = 0; tx < w; tx++)
                {
                    float px = (left + (tx + 0.5f) / scale) * perSheet - 0.5f;
                    float a = canvas.m_ink[ty * w + tx] / 255f;
                    int o = (row * w + tx) * 4;
                    for (int channel = 0; channel < 3; channel++)
                    {
                        float back = PaperAt(parchment, paper, px, py, channel);
                        float front = channel == 0 ? ink.r : channel == 1 ? ink.g : ink.b;
                        rgba[o + channel] = (byte)(back + (front - back) * a + 0.5f);
                    }
                    rgba[o + 3] = 255;
                }
            }
            return rgba;
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
