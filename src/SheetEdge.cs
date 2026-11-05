using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The parchment's outer edge, trimmed by hand (the user's design): a slightly wandering cut
    /// with worn, rounded corners, a few small worn nicks and a browned cut line, in place of the
    /// square's ruled edge. The sheet's layers stay square; four strips along its sides, laid
    /// over everything in the window's backdrop colour (AtlasView's Edge), cover what lies
    /// outside the cut, so paper, ink, glimpse, close-ups, fog, markers and dirt are all cut alike
    /// without touching any of them. A UI Mask would cut every layer too, but only with a hard,
    /// aliased 1-bit edge (the stock UI shader's alpha clip), and per-layer alpha would leave each
    /// layer's edge at its own resolution. The strips can only stand in for a flat backdrop:
    /// a textured one would show the strips. The darker worn rim inside the cut is a stain laid
    /// into the dirt (AddRim). Built on the workers; coordinates are the ink canvas's: sheet
    /// pixels, x east, y south from the north edge.
    /// </summary>
    internal static class SheetEdge
    {
        private const float Size = InkPainter.CanvasSize;
        // The edge's own noise, apart from every other hashed rule on the sheet.
        private const int Salt = 5501;
        // Texels per sheet pixel: at the deepest zoom (4 screen px a sheet px) the cut stays as
        // sharp as the 4x close-ups under it; at 1 it blurred over 4 screen px.
        public const int Texels = 2;

        // How far the cut lies inside the square, in sheet pixels: never less than Inset, plus a
        // slow wander of the whole side (two octaves), a little unevenness and a fibre. At most
        // about 15 px, so it keeps about 44 px (230 m) of bare margin off the frame's ring where
        // the ring comes nearest, at the four compass points.
        private const float Inset = 4f;
        private const float Wander = 4f;
        private const float WanderPeriod = 700f;
        private const float Wander2 = 1.5f;
        private const float Wander2Period = 160f;
        private const float Uneven = 1.2f;
        private const float UnevenPeriod = 18f;
        private const float Fibre = 0.5f;
        private const float FibrePeriod = 2.5f;
        // A slow 2D wobble of the whole outline, corners included, so worn corners are not drawn
        // with a compass.
        private const float Grain = 1f;
        private const float GrainPeriod = 30f;
        // Worn corners, each its own radius.
        private const float CornerMin = 14f;
        private const float CornerMax = 34f;
        // Small worn nicks: the odds of one in each NickCell of edge, their depth and half width.
        // Rounded, never a V: a V notch reads as a tear, and a tear is damage to the sheet.
        private const float NickCell = 160f;
        private const float NickOdds = 0.35f;
        private const float NickDepth = 3f;
        private const float NickWidth = 5f;
        // The cut thins the vellum over Fringe px (varied by FringeVary along the edge), so the
        // backdrop shows a little through it; a browned cut line of Line opacity hugs it.
        private const float Fringe = 0.8f;
        private const float FringeVary = 0.3f;
        private const float Line = 0.35f;
        private const float LineWidth = 1.2f;
        // The sheet's soft shadow on the backdrop, cast south-east: darkening, reach, offset.
        private const float Shadow = 0.55f;
        private const float ShadowWidth = 9f;
        private const float ShadowOffset = 3f;
        // The worn rim, a stain in the dirt: density at the cut and how far in it reaches.
        private const float Rim = 0.6f;
        private const float RimWidth = 16f;

        private const float MaxInset = Inset + Wander + Wander2 + Uneven + Fibre + NickDepth + Grain;
        // The strips reach Depth into the sheet (every pixel past it is clean paper) and Pad
        // beyond it, for the shadow.
        public static readonly int Depth = Mathf.CeilToInt(MaxInset + Fringe * (1f + FringeVary) + 3f * LineWidth + 2f);
        public static readonly int Pad = Mathf.CeilToInt(ShadowOffset + ShadowWidth + 2f);

        /// <summary>
        /// The four strips: north and south the whole width, west and east between them. Each
        /// rect is in canvas pixels (x east, y south); its pixels are RGBA with their mip chain,
        /// rows from the south as textures run: RGB the shadow's factor on the backdrop colour
        /// (white: none), A how much of the backdrop covers the sheet there.
        /// </summary>
        internal sealed class Strips
        {
            public RectInt[] m_rects;
            public byte[][] m_pixels;
        }

        public static RectInt[] Rects()
        {
            int n = (int)Size;
            return new[]
            {
                new RectInt(-Pad, -Pad, n + 2 * Pad, Depth + Pad),
                new RectInt(-Pad, n - Depth, n + 2 * Pad, Depth + Pad),
                new RectInt(-Pad, Depth, Depth + Pad, n - 2 * Depth),
                new RectInt(n - Depth, Depth, Depth + Pad, n - 2 * Depth),
            };
        }

        // Worker thread.
        public static Strips Build(int worldSeed)
        {
            int seed = worldSeed + Salt;
            var strips = new Strips { m_rects = Rects(), m_pixels = new byte[4][] };
            for (int k = 0; k < 4; k++)
            {
                RectInt r = strips.m_rects[k];
                int w = r.width * Texels, h = r.height * Texels;
                var top = new byte[w * h * 4];
                const float texel = 1f / Texels;
                Parallel.For(0, h, Workers.LoopOptions(), row =>
                {
                    // Texture rows run from the south.
                    float y = r.y + r.height - (row + 0.5f) * texel;
                    for (int col = 0; col < w; col++)
                    {
                        float x = r.x + (col + 0.5f) * texel;
                        Texel(x, y, seed, texel, out float alpha, out float shade);
                        int i = (row * w + col) * 4;
                        byte v = ToByte(shade * 255f);
                        top[i] = top[i + 1] = top[i + 2] = v;
                        top[i + 3] = ToByte(alpha * 255f);
                    }
                });
                strips.m_pixels[k] = WithMips(top, w, h);
            }
            return strips;
        }

        // The mip count Unity gives a w x h texture with a full chain.
        public static int MipCount(int w, int h)
        {
            int levels = 1;
            for (int s = Math.Max(w, h); s > 1; s >>= 1) levels++;
            return levels;
        }

        // The level and its full mip chain, each level the alpha-weighted mean of the 2 x 2 texels
        // under it (odd edges clamped), so the backdrop's colour does not bleed grey into the cut.
        private static byte[] WithMips(byte[] top, int w, int h)
        {
            int levels = MipCount(w, h);
            long total = 0;
            for (int l = 0; l < levels; l++) total += (long)Math.Max(1, w >> l) * Math.Max(1, h >> l) * 4;
            var all = new byte[total];
            Buffer.BlockCopy(top, 0, all, 0, top.Length);
            int from = 0, pw = w, ph = h;
            int to = top.Length;
            for (int l = 1; l < levels; l++)
            {
                int cw = Math.Max(1, w >> l), ch = Math.Max(1, h >> l);
                for (int y = 0; y < ch; y++)
                    for (int x = 0; x < cw; x++)
                    {
                        float a = 0f, c = 0f;
                        for (int dy = 0; dy < 2; dy++)
                            for (int dx = 0; dx < 2; dx++)
                            {
                                int sx = Math.Min(pw - 1, 2 * x + dx), sy = Math.Min(ph - 1, 2 * y + dy);
                                int i = from + (sy * pw + sx) * 4;
                                float ta = all[i + 3];
                                a += ta;
                                c += all[i] * ta;
                            }
                        int o = to + (y * cw + x) * 4;
                        byte v = a > 0f ? ToByte(c / a) : (byte)255;
                        all[o] = all[o + 1] = all[o + 2] = v;
                        all[o + 3] = ToByte(a * 0.25f);
                    }
                from = to;
                to += cw * ch * 4;
                pw = cw;
                ph = ch;
            }
            return all;
        }

        // How far the cut lies inside side k (0 west, 1 east, 2 north, 3 south) at t along it.
        // smooth leaves out the unevenness, fibres and nicks, for the rim, which the dirt lays
        // coarser.
        private static float SideInset(int side, float t, int seed, bool smooth)
        {
            int salt = seed + side * 101;
            float inset = Inset + Wander * Noise.Value(t / WanderPeriod, 0.5f, salt + 1)
                + Wander2 * Noise.Value(t / Wander2Period, 1.5f, salt + 2);
            if (smooth) return inset;
            inset += Uneven * Noise.Value(t / UnevenPeriod, 2.5f, salt + 3) + Fibre * Noise.Value(t / FibrePeriod, 3.5f, salt + 4);
            int cell = Mathf.FloorToInt(t / NickCell);
            for (int c = cell - 1; c <= cell + 1; c++)
            {
                int h = Noise.HashInt(c, side, seed + 977);
                if (Noise.Unit(h, 1) >= NickOdds) continue;
                float at = (c + 0.2f + 0.6f * Noise.Unit(h, 2)) * NickCell;
                float width = NickWidth * Mathf.Lerp(0.6f, 1.4f, Noise.Unit(h, 3));
                float u = 1f - Math.Abs(t - at) / width;
                if (u <= 0f) continue;
                inset += NickDepth * Mathf.Lerp(0.5f, 1f, Noise.Unit(h, 4)) * u * u * (3f - 2f * u);
            }
            return inset;
        }

        // Signed distance from the cut, in sheet pixels, positive on the sheet. Each side has its
        // own cut; the two nearest are joined by a rounded intersection (Quilez), so corners are
        // worn round with no seam on the diagonal.
        private static float Distance(float x, float y, int seed, bool smooth)
        {
            float a = Math.Min(x - SideInset(0, y, seed, smooth), Size - x - SideInset(1, y, seed, smooth));
            float b = Math.Min(y - SideInset(2, x, seed, smooth), Size - y - SideInset(3, x, seed, smooth));
            int corner = (x < Size * 0.5f ? 0 : 1) + (y < Size * 0.5f ? 0 : 2);
            float r = Mathf.Lerp(CornerMin, CornerMax, Noise.Unit(seed + 5, corner));
            float ua = Math.Max(r - a, 0f), ub = Math.Max(r - b, 0f);
            float d = -(Math.Min(-r, Math.Max(-a, -b)) + (float)Math.Sqrt(ua * ua + ub * ub));
            return d - Grain * Noise.Value(x / GrainPeriod, y / GrainPeriod, seed + 61);
        }

        // The strips at a point: how much backdrop covers the sheet, and the shadow's factor on it.
        private static void Texel(float x, float y, int seed, float texel, out float alpha, out float shade)
        {
            float d = Distance(x, y, seed, false);
            float fringe = Fringe * (1f - FringeVary + 2f * FringeVary * Noise.Value(x / 37f, y / 37f, seed + 31));
            // The sheet's cover, anti-aliased over a texel and thinning over the fringe.
            float sheet = Mathf.Clamp01((d + 0.5f * texel) / (fringe + texel));
            float outside = 1f - sheet;
            float line = 0f;
            if (d > -texel) line = Line * Mathf.Exp(-Math.Max(d, 0f) / LineWidth) * Mathf.Lerp(0.5f, 1.2f, Noise.Value(x / 9f, y / 9f, seed + 41));
            line = Mathf.Clamp01(line) * sheet;
            float shadow = 0f;
            if (outside > 0f)
            {
                float t = Mathf.Clamp01(1f + Distance(x - ShadowOffset, y - ShadowOffset, seed, true) / ShadowWidth);
                shadow = Shadow * t * t;
            }
            alpha = outside + (1f - outside) * line;
            // The cut line takes the backdrop's own colour at its opacity.
            shade = alpha > 0f ? (outside * (1f - shadow) + (1f - outside) * line) / alpha : 1f;
        }

        // Worker thread. The worn rim: stain density rising toward the cut, mottled, added to the
        // dirt's blots (FogSize² floats, rows from the south), so it browns the paper and fades
        // the ink under it as the other stains do.
        public static void AddRim(float[] blots, int n, int worldSeed)
        {
            int seed = worldSeed + Salt;
            float px = Size / n;
            float reach = MaxInset + CornerMax + 4f * RimWidth;
            Parallel.For(0, n, Workers.LoopOptions(), row =>
            {
                float y = Size - (row + 0.5f) * px;
                bool rowNear = y < reach || y > Size - reach;
                for (int col = 0; col < n; col++)
                {
                    float x = (col + 0.5f) * px;
                    if (!rowNear && x > reach && x < Size - reach) continue;
                    float d = Distance(x, y, seed, true);
                    float mottle = Mathf.Lerp(0.35f, 1.5f, Noise.Value(x / 60f, y / 60f, seed + 51) * 0.7f + Noise.Value(x / 13f, y / 13f, seed + 52) * 0.3f);
                    blots[row * n + col] += Rim * mottle * Mathf.Exp(-Math.Max(d, 0f) / RimWidth);
                }
            });
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(value + 0.5f, 0f, 255f);
    }
}
