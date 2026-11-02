using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// A part of the sheet inked again at the magnification it is shown at, so zoomed-in pen lines
    /// stay sharp instead of showing the sheet's pixels. The tile is opaque (ink laid on the
    /// parchment), so it hides the coarser sheet beneath it; the fog still lies on top.
    /// </summary>
    internal static class DetailTile
    {
        // Worker thread. Returns RGBA rows from south to north, as LoadRawTextureData expects, for
        // the sheet rectangle (left, top, width, height) at scale pixels per sheet pixel.
        public static byte[] Render(InkRecord record, byte[] parchment, int left, int top, int width, int height, int scale)
        {
            int w = width * scale, h = height * scale;
            var canvas = new InkCanvas(w, h, left, top, scale);
            record.Replay(canvas, left, top, left + width, top + height);

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
