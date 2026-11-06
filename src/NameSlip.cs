using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The slip of parchment the hovered pin's name is lettered on, as a label pasted onto a map:
    /// paler than the sheet, cut by hand with a slightly wandering edge and worn corners, browned
    /// along the cut, with a soft shadow falling south-east (the map's shade side) onto the sheet.
    /// Made in code for each slip size and kept for the session, since names of one width share
    /// a slip; the shadow lies in a margin round the slip, so the texture is larger than the slip.
    /// </summary>
    internal static class NameSlip
    {
        // Texels per canvas unit, and the margin round the slip that holds its shadow (canvas units).
        private const int Texels = 2;
        public const float Margin = 5f;
        // Sizes are rounded up to this many canvas units, so names of about one width share a slip.
        private const int SizeStep = 8;

        // The cut: corner radius, how far the edge wanders in, and how fine its wander is
        // (texels). Wandering more than a couple of texels reads as torn rather than cut.
        private const float CornerRadius = 4f * Texels;
        private const float Wander = 1.2f * Texels;
        private const float WanderScale = 0.09f;

        // The paper: a little paler than the sheet's (Paper.PaperColor), a fresh slip pasted on;
        // its grain, and the browned band along the cut and how brown it gets.
        private static readonly Color32 s_paper = new Color32(240, 226, 197, 255);
        private static readonly Color32 s_fringe = new Color32(190, 160, 118, 255);
        private const float Grain = 0.05f;
        private const float GrainScale = 0.35f;
        private const float FringeDepth = 2.5f * Texels;
        private const float FringeStrength = 0.55f;

        // The shadow: its offset south-east, its softness (box radius) in texels, and its opacity.
        private const int ShadowOffset = 2 * Texels;
        private const int ShadowBlur = 3 * Texels;
        private const float ShadowAlpha = 0.4f;

        private const int Salt = 0x51A7;
        private static readonly Dictionary<long, Texture2D> s_slips = new Dictionary<long, Texture2D>();

        // The slip's size for a name's own size (canvas units), rounded up to SizeStep.
        public static Vector2 Fit(Vector2 size) =>
            new Vector2(Mathf.Ceil(size.x / SizeStep) * SizeStep, Mathf.Ceil(size.y / SizeStep) * SizeStep);

        // Main thread. The texture for a slip of a Fit size, covering the slip and Margin round it.
        public static Texture2D For(Vector2 slip)
        {
            int w = Mathf.RoundToInt(slip.x), h = Mathf.RoundToInt(slip.y);
            long key = ((long)w << 32) | (uint)h;
            if (s_slips.TryGetValue(key, out Texture2D cached) && cached) return cached;
            Texture2D made = Make(w, h);
            s_slips[key] = made;
            return made;
        }

        private static Texture2D Make(int slipWidth, int slipHeight)
        {
            int margin = Mathf.RoundToInt(Margin * Texels);
            int w = slipWidth * Texels + 2 * margin, h = slipHeight * Texels + 2 * margin;
            int seed = Noise.HashInt(slipWidth, slipHeight, Salt);

            // The slip's coverage, anti-aliased over a texel: a rounded rectangle whose edge
            // wanders in by smooth noise.
            var cover = new float[w * h];
            var inside = new float[w * h];
            float cx = w * 0.5f, cy = h * 0.5f;
            float hx = slipWidth * Texels * 0.5f - CornerRadius, hy = slipHeight * Texels * 0.5f - CornerRadius;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float qx = Mathf.Abs(x + 0.5f - cx) - hx, qy = Mathf.Abs(y + 0.5f - cy) - hy;
                    float ox = Mathf.Max(qx, 0f), oy = Mathf.Max(qy, 0f);
                    float distance = -(Mathf.Sqrt(ox * ox + oy * oy) + Mathf.Min(Mathf.Max(qx, qy), 0f) - CornerRadius);
                    distance -= Noise.Value(x * WanderScale, y * WanderScale, seed) * Wander;
                    int i = y * w + x;
                    inside[i] = distance;
                    cover[i] = Mathf.Clamp01(distance + 0.5f);
                }

            float[] shadow = Blur(cover, w, h, ShadowBlur);
            var pixels = new Color32[w * h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    // Texture rows run north; the shadow falls east and south, so it is the
                    // coverage read from west and north of here.
                    int sx = x - ShadowOffset, sy = y + ShadowOffset;
                    float cast = sx >= 0 && sy < h ? shadow[sy * w + sx] * ShadowAlpha : 0f;
                    float c = cover[i];
                    float alpha = c + cast * (1f - c);
                    if (alpha <= 0f) continue;
                    float grain = 1f - Grain + 2f * Grain * (0.6f * Noise.Value(x * GrainScale, y * GrainScale, seed + 1)
                        + 0.4f * Noise.Value(x * GrainScale * 3f, y * GrainScale * 3f, seed + 2));
                    float brown = FringeStrength * (1f - Mathf.Clamp01(inside[i] / FringeDepth));
                    float r = Mathf.Lerp(s_paper.r, s_fringe.r, brown) * grain;
                    float g = Mathf.Lerp(s_paper.g, s_fringe.g, brown) * grain;
                    float b = Mathf.Lerp(s_paper.b, s_fringe.b, brown) * grain;
                    // The shadow is black, so the colour is the paper's share of the pixel.
                    float k = c / alpha;
                    pixels[i] = new Color32(ToByte(r * k), ToByte(g * k), ToByte(b * k), ToByte(alpha * 255f));
                }

            var texture = new Texture2D(w, h, TextureFormat.RGBA32, mipChain: true)
            {
                name = "Atlas name slip",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: true, makeNoLongerReadable: true);
            return texture;
        }

        // A separable box blur of the given radius.
        private static float[] Blur(float[] source, int w, int h, int radius)
        {
            var across = new float[w * h];
            var result = new float[w * h];
            float scale = 1f / (2 * radius + 1);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int xx = x + k;
                        if (xx >= 0 && xx < w) sum += source[y * w + xx];
                    }
                    across[y * w + x] = sum * scale;
                }
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float sum = 0f;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int yy = y + k;
                        if (yy >= 0 && yy < h) sum += across[yy * w + x];
                    }
                    result[y * w + x] = sum * scale;
                }
            return result;
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(Mathf.RoundToInt(value), 0, 255);
    }
}
