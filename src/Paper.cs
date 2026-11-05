using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Pixel data for the atlas textures, built on worker threads. Texture rows run from south (row
    /// 0) to north, while the ink canvas runs from north, so the ink is flipped here. The paper and
    /// the fog share the same parchment colours, so where the fog lies the sheet looks blank, not
    /// covered.
    /// </summary>
    internal static class Paper
    {
        // 2048 over the sheet is 10.5 m a pixel, a little finer than vanilla's 12 m exploration grid.
        public const int FogSize = 2048;

        // Aged calfskin parchment and aged iron-gall ink, turned brown as such ink does.
        public static readonly Color32 PaperColor = new Color32(230, 211, 177, 255);
        public static readonly Color32 InkColor = new Color32(46, 33, 24, 255);
        // The pins' ink: red, as places were rubricated on maps of the period, a vermilion dulled
        // by age, so a pin reads apart from the terrain's black ink at any zoom.
        public static readonly Color32 PinInkColor = new Color32(152, 44, 28, 255);
        // The terrain's ink in the glimpse past the explored land: a paler, greyer brown, as if
        // sketched rather than inked, so what is only glimpsed reads apart from what is known.
        public static readonly Color32 GlimpseInkColor = new Color32(92, 86, 78, 255);

        // The glimpse's opacity (Fog's glimpse, RGBA at FogSize, rows from the south) at a sheet
        // point (x from the west, y from the south, in sheet pixels), bilinear; 0 without one.
        public static float GlimpseAt(byte[] glimpse, float x, float y)
        {
            if (glimpse == null) return 0f;
            float scale = FogSize / (float)InkPainter.CanvasSize;
            x = Mathf.Clamp(x * scale - 0.5f, 0f, FogSize - 1.001f);
            y = Mathf.Clamp(y * scale - 0.5f, 0f, FogSize - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int i = (y0 * FogSize + x0) * 4 + 3;
            const int Right = 4, Down = FogSize * 4;
            float bottom = glimpse[i] + (glimpse[i + Right] - glimpse[i]) * fx;
            float top = glimpse[i + Down] + (glimpse[i + Down + Right] - glimpse[i + Down]) * fx;
            return (bottom + (top - bottom) * fy) / 255f;
        }

        // How strongly the paper is mottled and darkened towards its corners.
        private const float Mottle = 0.07f;
        private const float Vignette = 0.2f;
        // The mottling was tuned on a 1024 sheet; this keeps its grain the same size on the paper.
        private const float PaperScale = FogSize / 1024f;
        // Fog is lifted inside this band at the world edge, so the frame always shows.
        private const float FrameBand = 120f;
        // The fog's edge is a torn sheet, not a fade: the explored cover is blurred over RipBlur
        // 3x3 passes (about 5 fog pixels, 53 m, either side of the edge), and noise moves its
        // middle within that band: RipTear by a slow fBm every RipGrain pixels for bays and
        // tongues, RipFibres by fine noise every RipFibre pixels for the fibres. The edge is
        // crisp over RipEdge of the cover (about half a pixel), with a paler rim RipRim deep
        // (the torn paper's core), brightening the parchment's own hue by RipRimLight: white read as
        // a chalk line along the tear. No shadow: it read as a pencil stroke (the user's call).
        private const int RipBlur = 5;
        private const float RipGrain = 9f;
        private const float RipTear = 0.85f;
        private const float RipFibre = 1.3f;
        private const float RipFibres = 0.16f;
        private const float RipEdge = 0.04f;
        private const float RipRim = 0.12f;
        private const float RipRimLight = 0.08f;
        private const int RipSeed = 4127;
        // The glimpse past the explored land, in metres, and the distance field's steps a pixel
        // (its bytes reach 255 / GlimpseSteps pixels, past the band and the tear).
        private const float GlimpseBand = 160f;
        private const float GlimpseSteps = 4f;
        // A sea illustration is shown or hidden whole (SeaMarkFog): the fog is lifted over its box
        // when at least SeaMarkShown of the box is explored and laid over it when less is, out to
        // SeaMarkMargin metres round it, past the torn edge (which stays within RipBlur pixels,
        // about 53 m, of the explored edge), so that edge never crosses the glyph. The box is
        // sampled every SeaMarkStep metres, finer than vanilla's 12 m exploration grid.
        private const float SeaMarkShown = 0.5f;
        private const float SeaMarkMargin = 60f;
        private const float SeaMarkStep = 6f;

        // The biome wash: per biome (in Land order) a tint and how far the paper is moved towards
        // it, the extra share in the woods, and the blur passes (each about 10 m) that soften it.
        // Each tint is a thin glaze of a pigment an early-medieval northern illuminator could have
        // had (earths, ochres, verdigris, folium, chalk and soot, woad or indigo), so the sheet
        // reads as one muted palette.
        private struct Tint
        {
            public Color32 m_color;
            public float m_strength;

            public Tint(byte r, byte g, byte b, float strength)
            {
                m_color = new Color32(r, g, b, 255);
                m_strength = strength;
            }
        }

        private static readonly Tint[] s_tints =
        {
            new Tint(140, 170, 180, 0.18f), // Ocean (land vanilla files as ocean, rare)
            new Tint(148, 168, 104, 0.24f), // Meadows: green earth
            new Tint(80, 102, 72, 0.26f),   // Black Forest: verdigris dulled with indigo
            new Tint(116, 106, 70, 0.28f),  // Swamp: green earth with ochre and soot
            new Tint(198, 196, 196, 0.3f),  // Mountain: chalk greyed with soot
            new Tint(214, 176, 98, 0.24f),  // Plains: yellow ochre
            new Tint(128, 112, 138, 0.26f), // Mistlands: folium (turnsole)
            new Tint(140, 76, 52, 0.42f),   // Ashlands: burnt red ochre
            new Tint(238, 241, 242, 0.4f),  // Deep North: chalk white
        };
        // Water is one even indigo wash, so seas, lakes and rivers read as water. A shade by depth
        // would draw the sea floor more exactly than a hand-tinted map does.
        private static readonly Tint s_water = new Tint(100, 135, 190, 0.42f);
        // The sea illustrations' wash (SeaWash): a second glaze of a deeper indigo laid over the
        // water in their bodies, so they stand a little darker and bluer than the sea round them.
        private static readonly Tint s_seaWash = new Tint(72, 100, 160, 0.22f);
        // The Ashlands' boiling sea: a thin glaze of burnt red ochre over the water's indigo, as
        // vanilla's map warms the sea there, by the sea's heat (WorldSource.m_ashSea), so it fades
        // out with vanilla's own gradient instead of stopping at the biome's edge.
        private static readonly Tint s_ashSea = new Tint(170, 92, 52, 0.26f);
        // The water's edge is anti-aliased over one pixel by the local slope of the ground, so the
        // blue ends exactly at the water line; softening it by a fixed height spills it tens of
        // metres inland over flat beaches. This is the least slope assumed (metres a pixel).
        private const float MinShoreSlope = 0.05f;
        // Streams' water on the sheet: their channels are under the minimap's 12 m pixels, so a
        // band of this width (metres) is laid along their courses. Rivers show by their heights.
        private const float StreamWater = 8f;
        // Lava fields: vermilion, the bright red a northern illuminator had as a rare luxury.
        private static readonly Tint s_lava = new Tint(176, 48, 34, 0.55f);
        // Grey ash clouds over the Ashlands, in blotches of about AshScale metres.
        private static readonly Tint s_ash = new Tint(92, 86, 82, 0.45f);
        private const float AshScale = 700f;
        private const float WoodWash = 0.1f;
        private const int WashBlur = 3;
        // How much of the water's blue a swamp pool keeps at full wetness.
        private const float SwampPoolGlaze = 0.35f;

        /// <summary>
        /// The washed paper, in two parts: the sheet's paper with water tinted, and the land's wash
        /// alone, carried a little way out over the water, which close-ups lay their own,
        /// finer water over; plus where rivers and streams run (0 to 1 a paper pixel), since the
        /// minimap's heights miss narrow ones.
        /// </summary>
        public sealed class Wash
        {
            public byte[] m_sheet;
            public byte[] m_land;
            public byte[] m_parchment;
            public float[] m_rivers;
        }

        // Worker or main thread. The paper with no wash at all, for when the wash failed.
        public static Wash Bare(byte[] parchment) => new Wash { m_sheet = parchment, m_land = parchment, m_parchment = parchment };

        // How much of a pixel is water at the given depth (metres below the water line, negative on
        // land) and stream share, and the water colour over that parchment.
        // slope is how much the depth changes across one pixel there.
        public static float Waterness(float depth, float slope, float river) =>
            Math.Max(Mathf.Clamp01(0.5f + depth / Math.Max(slope, MinShoreSlope)), river);

        // Washes are glazes, as real watercolour is: the pigment filters the light, so the paper
        // under it, stains and all, shows through. A tint acts as its colour relative to clean paper
        // (capped a little above 1 for tints lighter than the paper); on clean paper the result is
        // the tint mixed in by its strength.
        private static float Filter(float tint, float paper) => Math.Min(tint / paper, 1.1f);

        public static void WaterColor(float r, float g, float b, out float wr, out float wg, out float wb)
        {
            float s = s_water.m_strength;
            wr = r * (1f - s + s * Filter(s_water.m_color.r, PaperColor.r));
            wg = g * (1f - s + s * Filter(s_water.m_color.g, PaperColor.g));
            wb = b * (1f - s + s * Filter(s_water.m_color.b, PaperColor.b));
        }

        // The water's colour over this parchment at a world point, warmed by the Ashlands' boiling
        // sea there (AshSeaGlaze).
        public static void WaterColor(float r, float g, float b, WorldSource world, float wx, float wz, out float wr, out float wg, out float wb)
        {
            WaterColor(r, g, b, out wr, out wg, out wb);
            if (world.m_ashSea == null) return;
            float heat = world.Sample(world.m_ashSea, wx, wz);
            if (heat > 0f) AshSeaGlaze(wr, wg, wb, heat, out wr, out wg, out wb);
        }

        // The boiling sea's glaze over a colour, amount (0 to 1, the sea's heat) of its strength.
        private static void AshSeaGlaze(float r, float g, float b, float amount, out float sr, out float sg, out float sb)
        {
            float s = s_ashSea.m_strength * amount;
            sr = r * (1f - s + s * Filter(s_ashSea.m_color.r, PaperColor.r));
            sg = g * (1f - s + s * Filter(s_ashSea.m_color.g, PaperColor.g));
            sb = b * (1f - s + s * Filter(s_ashSea.m_color.b, PaperColor.b));
        }

        // Stains on the parchment are optical density: each pixel's summed density D lets
        // pow(tint, D) of the paper's light through per channel, so stains turn yellow-brown (blue
        // drops fastest) rather than grey, overlapping stains darken where they cross, and D is
        // softly capped at MaxStain rather than clipped flat.
        // Blots by kind: how many on the whole 21.6 km sheet (the view shows only part, so enough
        // that a few always fall in it), radius range (paper pixels, 10.5 m each), the density of
        // the body and of the tide line, and how many fainter re-wetting rims lie inside.
        private struct StainKind
        {
            public int m_count;
            public float m_minRadius, m_maxRadius, m_inside, m_rim;
            public int m_rewets;

            public StainKind(int count, float minRadius, float maxRadius, float inside, float rim, int rewets)
            {
                m_count = count;
                m_minRadius = minRadius;
                m_maxRadius = maxRadius;
                m_inside = inside;
                m_rim = rim;
                m_rewets = rewets;
            }
        }

        private static readonly StainKind[] s_stains =
        {
            new StainKind(24, 120f, 380f, 0.1f, 0.35f, 2),  // old water stains, wetted more than once
            new StainKind(90, 25f, 90f, 0.12f, 0.5f, 0),    // spilt drops dried to rings
        };
        // Light let through per unit of density, per channel: water and coffee, and foxing (redder).
        private static readonly Vector3 s_stainTint = new Vector3(0.9f, 0.8f, 0.64f);
        private static readonly Vector3 s_foxTint = new Vector3(0.86f, 0.7f, 0.54f);
        private const float MaxStain = 1.2f;
        // A blot's edge is domain-warped by fBm (Quilez): Warp of its radius, at a grain of
        // WarpGrain radii; its tide line rises sharply at the edge and fades inward over RimWidth
        // of the radius, never under MinRim paper pixels, or it blurs when magnified.
        private const float Warp = 0.35f;
        private const float WarpGrain = 0.6f;
        private const float RimWidth = 0.06f;
        private const float MinRim = 1.5f;
        // Foxing: spots in clusters (Gaussian spread over ClusterSpread paper pixels), more of them
        // toward the sheet's edge, plus some alone; each a dark core with a soft halo.
        private const int FoxClusters = 80;
        private const int FoxAlone = 400;
        // Age: an uneven patina over the whole sheet, the paper browning where it was handled and
        // exposed, so no part of it looks fresh. Density up to AgeDensity in blotches of about
        // AgeScale paper pixels, domain-warped so they have no regular grain.
        private const float AgeDensity = 0.32f;
        private const float AgeScale = 160f;
        private const float ClusterSpread = 35f;
        private const float FoxCore = 0.4f;
        private const float FoxHalo = 0.12f;

        // Three octaves of value noise, 0 to 1.
        private static float Fbm(float x, float y, int seed) =>
            (Noise.Value(x, y, seed) * 0.57f + Noise.Value(x * 2f, y * 2f, seed + 1) * 0.29f + Noise.Value(x * 4f, y * 4f, seed + 2) * 0.14f);

        // Each paper pixel's stain density, per kind: blots (water and drops) and foxing.
        private static void Stains(int seed, out float[] blots, out float[] foxing)
        {
            int n = FogSize;
            blots = new float[n * n];
            foxing = new float[n * n];
            float[] target = blots;
            for (int kind = 0; kind < s_stains.Length; kind++)
            {
                StainKind k = s_stains[kind];
                for (int s = 0; s < k.m_count; s++)
                {
                    int hash = Noise.HashInt(s, kind, seed + 31);
                    float cx = Noise.Unit(hash, 1) * n, cy = Noise.Unit(hash, 2) * n;
                    float r = Mathf.Lerp(k.m_minRadius, k.m_maxRadius, Noise.Unit(hash, 3));
                    float strength = Mathf.Lerp(0.6f, 1f, Noise.Unit(hash, 4));
                    // An elongated, turned blot rather than a disc.
                    float angle = Noise.Unit(hash, 5) * Mathf.PI, aspect = Mathf.Lerp(0.7f, 1.3f, Noise.Unit(hash, 6));
                    float cos = Mathf.Cos(angle), sin = Mathf.Sin(angle);
                    float rim = Math.Max(RimWidth, MinRim / r);
                    float grain = Math.Max(2f, r * WarpGrain);
                    // The farthest a pixel can lie and still be warped into the blot: its longer
                    // axis, plus the warp's largest shift.
                    float reach = r / Math.Min(1f, aspect) + 1.42f * Warp * r;
                    int x0 = Math.Max(0, (int)(cx - reach)), x1 = Math.Min(n - 1, (int)(cx + reach));
                    int y0 = Math.Max(0, (int)(cy - reach)), y1 = Math.Min(n - 1, (int)(cy + reach));
                    // Rows are independent, so they run in parallel: the warp's noise for some
                    // hundred blots is most of the parchment's cost.
                    System.Threading.Tasks.Parallel.For(y0, y1 + 1, Workers.LoopOptions(), y =>
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float ux = x - cx, uy = y - cy;
                            if (ux * ux + uy * uy > reach * reach) continue;
                            float wx = x + (Fbm(x / grain, y / grain, hash) - 0.5f) * Warp * 2f * r;
                            float wy = y + (Fbm(x / grain, y / grain, hash + 7) - 0.5f) * Warp * 2f * r;
                            float dx = wx - cx, dy = wy - cy;
                            float u = dx * cos + dy * sin, v = (dy * cos - dx * sin) * aspect;
                            float d = Mathf.Sqrt(u * u + v * v) / r;
                            if (d >= 1f) continue;
                            // The deposit piles up where the drying edge was pinned: a sharp outer
                            // rise, a slow fade inward, over a faint flat body.
                            float e = 1f - d;
                            float density = k.m_rim * Mathf.Exp(-e / rim) * (1f - Mathf.Exp(-e / (0.15f * rim)))
                                + k.m_inside * Mathf.Clamp01(e / 0.3f);
                            for (int w = 1; w <= k.m_rewets; w++)
                            {
                                float at = 1f - 0.08f * w - 0.04f * Noise.Unit(hash, 7 + w);
                                if (d >= at) continue;
                                float ew = at - d;
                                density += 0.4f * k.m_rim * Mathf.Exp(-ew / rim) * (1f - Mathf.Exp(-ew / (0.15f * rim)));
                            }
                            target[y * n + x] += density * strength;
                        }
                    });
                }
            }
            // Foxing clusters, placed more often near the sheet's edge, as paper ages from it.
            for (int c = 0; c < FoxClusters; c++)
            {
                int hash = Noise.HashInt(c, 91, seed + 37);
                float a = Noise.Unit(hash, 1) * Mathf.PI * 2f, reachOut = Mathf.Sqrt(Noise.Unit(hash, 2));
                reachOut = Mathf.Lerp(reachOut, 1f, 0.4f) * 0.7f;
                float cx = n * (0.5f + Mathf.Cos(a) * reachOut), cy = n * (0.5f + Mathf.Sin(a) * reachOut);
                int spots = 8 + (int)(Noise.Unit(hash, 3) * 32f);
                float spread = ClusterSpread * Mathf.Lerp(0.5f, 1.6f, Noise.Unit(hash, 4));
                for (int s = 0; s < spots; s++)
                {
                    int spot = Noise.HashInt(c, s, seed + 41);
                    // Box-Muller from two hashes: a Gaussian spread round the cluster's centre.
                    float g1 = Math.Max(1e-4f, Noise.Unit(spot, 1)), g2 = Noise.Unit(spot, 2);
                    float rad = Mathf.Sqrt(-2f * Mathf.Log(g1)) * spread;
                    Fox(foxing, cx + Mathf.Cos(g2 * 6.283f) * rad, cy + Mathf.Sin(g2 * 6.283f) * rad, spot);
                }
            }
            for (int s = 0; s < FoxAlone; s++)
            {
                int spot = Noise.HashInt(s, 97, seed + 43);
                Fox(foxing, Noise.Unit(spot, 1) * n, Noise.Unit(spot, 2) * n, spot);
            }
        }

        // One foxing spot: an irregular dark core of 1 to 4 paper pixels and a soft halo round it.
        private static void Fox(float[] foxing, float cx, float cy, int hash)
        {
            int n = FogSize;
            float r = Mathf.Lerp(1f, 4f, Noise.Unit(hash, 5));
            float halo = r * 2.5f;
            int x0 = Math.Max(0, (int)(cx - halo * 2f)), x1 = Math.Min(n - 1, (int)(cx + halo * 2f));
            int y0 = Math.Max(0, (int)(cy - halo * 2f)), y1 = Math.Min(n - 1, (int)(cy + halo * 2f));
            float strength = Mathf.Lerp(0.6f, 1f, Noise.Unit(hash, 6));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = x - cx, dy = y - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy) / r;
                    float ragged = d * (1f + (Noise.Value(x / 1.5f, y / 1.5f, hash) - 0.5f) * 0.6f);
                    float core = Mathf.Clamp01((1f - ragged) / 0.4f);
                    float dh = Mathf.Sqrt(dx * dx + dy * dy) / halo;
                    foxing[y * n + x] += (FoxCore * core + FoxHalo * Mathf.Exp(-dh * dh)) * strength;
                }
            }
        }

        // The patina is broad, so it is worked out every AgeStep paper pixels and interpolated:
        // at every pixel its noise takes four times as long as the rest of the parchment.
        private const int AgeStep = 4;

        private static float[] Ages(int seed)
        {
            int m = FogSize / AgeStep + 1;
            var age = new float[m * m];
            for (int y = 0; y < m; y++)
                for (int x = 0; x < m; x++)
                    age[y * m + x] = Age(x * AgeStep, y * AgeStep, seed);
            return age;
        }

        private static float AgeAt(float[] age, int x, int y)
        {
            int m = FogSize / AgeStep + 1;
            int gx = x / AgeStep, gy = y / AgeStep;
            float fx = (x - gx * AgeStep) / (float)AgeStep, fy = (y - gy * AgeStep) / (float)AgeStep;
            int i = gy * m + gx;
            float top = age[i] + (age[i + 1] - age[i]) * fx;
            float bottom = age[i + m] + (age[i + m + 1] - age[i + m]) * fx;
            return top + (bottom - top) * fy;
        }

        // The patina's density at a paper pixel: warped fBm thresholded into soft blotches, with a
        // faint floor so even the cleanest parts are a little aged.
        private static float Age(int x, int y, int seed)
        {
            float u = x / AgeScale, v = y / AgeScale;
            float wu = u + (Fbm(u * 0.7f, v * 0.7f, seed + 61) - 0.5f) * 1.6f;
            float wv = v + (Fbm(u * 0.7f, v * 0.7f, seed + 67) - 0.5f) * 1.6f;
            float blotch = Fbm(wu, wv, seed + 71);
            float t = Mathf.Clamp01((blotch - 0.4f) / 0.35f);
            return AgeDensity * (0.15f + 0.85f * t * t * (3f - 2f * t));
        }

        // The light a stain of density d lets through, softly capped at MaxStain.
        private static float Through(float tint, float density) =>
            Mathf.Pow(tint, MaxStain * (1f - Mathf.Exp(-density / MaxStain)));

        // The parchment: mottled, darkened towards its corners and browned by an even patina. The
        // stains and foxing lie over everything drawn on it instead (Dirt).
        public static byte[] Parchment(int seed)
        {
            int n = FogSize;
            var rgb = new byte[n * n * 3];
            float[] age = Ages(seed);
            // Rows are independent, so they run in parallel.
            System.Threading.Tasks.Parallel.For(0, n, Workers.LoopOptions(), y =>
            {
                for (int x = 0; x < n; x++)
                {
                    float shade = 1f
                        + (Noise.Value(x / (90f * PaperScale), y / (90f * PaperScale), seed) - 0.5f) * Mottle * 2f
                        + (Noise.Value(x / (17f * PaperScale), y / (17f * PaperScale), seed + 1) - 0.5f) * Mottle
                        + (Noise.Value(x / (3f * PaperScale), y / (3f * PaperScale), seed + 2) - 0.5f) * Mottle * 0.5f;
                    float u = (x + 0.5f) / n * 2f - 1f, v = (y + 0.5f) / n * 2f - 1f;
                    float corner = Mathf.Clamp01((Mathf.Sqrt(u * u + v * v) - 0.75f) / 0.66f);
                    shade *= 1f - Vignette * corner * corner;
                    int i = (y * n + x) * 3;
                    float b = AgeAt(age, x, y);
                    // Darker paper also turns browner, as old parchment does.
                    rgb[i] = ToByte(PaperColor.r * shade * Through(s_stainTint.x, b));
                    rgb[i + 1] = ToByte(PaperColor.g * Mathf.Pow(shade, 1.1f) * Through(s_stainTint.y, b));
                    rgb[i + 2] = ToByte(PaperColor.b * Mathf.Pow(shade, 1.35f) * Through(s_stainTint.z, b));
                }
            });
            return rgb;
        }

        // The dirt of the sheet: its stains and foxing as RGBA over the parchment's square
        // (FogSize², rows as the parchment's), laid over everything drawn on it, ink, pins and names
        // included, as dirt lies on a real map. A stain filters the light, so over the paper it
        // multiplies by pow(tint, density) per channel (Through); an overlay can only blend, so each
        // pixel is the colour and opacity that give exactly that over clean paper, the opacity being
        // the largest channel's loss. Over ink it lifts the black a little towards the stain's
        // brown, as water stains fade ink. The even patina stays in the paper (Parchment): laid on
        // top, it would lift every ink line on the sheet.
        public static byte[] Dirt(int seed)
        {
            int n = FogSize;
            var rgba = new byte[n * n * 4];
            Stains(seed, out float[] blots, out float[] foxing);
            System.Threading.Tasks.Parallel.For(0, n, Workers.LoopOptions(), y =>
            {
                for (int x = 0; x < n; x++)
                {
                    int p = y * n + x, o = p * 4;
                    float b = blots[p], f = foxing[p];
                    float tr = Through(s_stainTint.x, b) * Through(s_foxTint.x, f);
                    float tg = Through(s_stainTint.y, b) * Through(s_foxTint.y, f);
                    float tb = Through(s_stainTint.z, b) * Through(s_foxTint.z, f);
                    float alpha = Math.Max(1f - tr, Math.Max(1f - tg, 1f - tb));
                    if (alpha <= 0f) continue;
                    rgba[o] = ToByte(PaperColor.r * (1f - (1f - tr) / alpha));
                    rgba[o + 1] = ToByte(PaperColor.g * (1f - (1f - tg) / alpha));
                    rgba[o + 2] = ToByte(PaperColor.b * (1f - (1f - tb) / alpha));
                    rgba[o + 3] = ToByte(alpha * 255f);
                }
            });
            return rgba;
        }

        // The parchment under a faint watercolour wash, one tint per biome (darker in the woods), so
        // biomes read apart at every zoom while the ink stays black, and water in one blue.
        // The land tints are blurred among land only, so they bleed softly across biome edges but
        // never out over the water, and the water's blue stops crisply at the shore. The fog keeps
        // the bare parchment, so unexplored land shows no colour either.
        public static Wash Washed(byte[] parchment, WorldSource world)
        {
            int n = FogSize;
            float metres = 2f * InkPainter.HalfExtent / n;
            float edgeSqr = InkPainter.EdgeRadius * InkPainter.EdgeRadius;
            float[] rivers = Rivers(world);
            var red = new float[n * n];
            var green = new float[n * n];
            var blue = new float[n * n];
            var strength = new float[n * n];
            var weight = new float[n * n];
            var depth = new float[n * n];
            var waters = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                    depth[y * n + x] = world.m_waterLevel - world.HeightAt(wx, wz);
                }
            }
            for (int y = 0; y < n; y++)
            {
                float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                    int i = y * n + x;
                    // Outside the frame is the bare margin of the sheet: neither land nor water.
                    if (wx * wx + wz * wz >= edgeSqr) continue;
                    float dx = depth[y * n + Math.Min(n - 1, x + 1)] - depth[y * n + Math.Max(0, x - 1)];
                    float dy = depth[Math.Min(n - 1, y + 1) * n + x] - depth[Math.Max(0, y - 1) * n + x];
                    waters[i] = Waterness(depth[i], 0.5f * Mathf.Sqrt(dx * dx + dy * dy), rivers[i]);
                    // Each land tint is weighted by how much of the pixel is land, so the blur
                    // below, divided back by the blurred weights, averages land only.
                    float land = 1f - waters[i];
                    if (land <= 0f) continue;
                    Tint tint = s_tints[(int)world.m_biome[world.Index(wx, wz)]];
                    // The Ashlands are burnt sienna mottled with grey ash clouds, darker and
                    // dirtier than the parchment, so they read as waste rather than sand.
                    if (tint.m_color.r == s_tints[(int)Land.Ashlands].m_color.r && world.m_biome[world.Index(wx, wz)] == Land.Ashlands)
                    {
                        float ash = Mathf.Clamp01((Noise.Value(wx / AshScale, wz / AshScale, world.m_seed + 91) - 0.45f) / 0.3f);
                        tint = new Tint(
                            (byte)Mathf.Lerp(tint.m_color.r, s_ash.m_color.r, ash * 0.6f),
                            (byte)Mathf.Lerp(tint.m_color.g, s_ash.m_color.g, ash * 0.6f),
                            (byte)Mathf.Lerp(tint.m_color.b, s_ash.m_color.b, ash * 0.6f),
                            Mathf.Lerp(tint.m_strength, s_ash.m_strength, ash * 0.6f));
                    }
                    float s = tint.m_strength;
                    // A wood is washed a little stronger, which darkens every biome's tint but the
                    // Deep North's chalk, which it would whiten; there the snow keeps one colour
                    // and the dark pines alone mark the wood. The Mountains' fir woods keep the
                    // mountain wash too, so the biome reads as mountains under its trees, the
                    // same grey the peaks' bodies are filled with.
                    Land ground = world.m_biome[world.Index(wx, wz)];
                    if (world.Sample(world.m_wood, wx, wz) > 0.5f && ground != Land.DeepNorth && ground != Land.Mountain) s += WoodWash;
                    red[i] = Filter(tint.m_color.r, PaperColor.r) * s * land;
                    green[i] = Filter(tint.m_color.g, PaperColor.g) * s * land;
                    blue[i] = Filter(tint.m_color.b, PaperColor.b) * s * land;
                    strength[i] = s * land;
                    weight[i] = land;
                }
            }
            for (int pass = 0; pass < WashBlur; pass++)
            {
                Blur(red, n);
                Blur(green, n);
                Blur(blue, n);
                Blur(strength, n);
                Blur(weight, n);
            }
            var sheet = new byte[parchment.Length];
            var landOnly = new byte[parchment.Length];
            for (int i = 0; i < n * n; i++)
            {
                int p = i * 3;
                float r = parchment[p], g = parchment[p + 1], b = parchment[p + 2];
                // The land's wash, also carried over the water near the shore, where weight is low
                // but not nil; close-ups lay their own water over it.
                float lr = r, lg = g, lb = b;
                if (weight[i] > 0.02f)
                {
                    float keep = 1f - strength[i] / weight[i];
                    lr = r * (keep + red[i] / weight[i]);
                    lg = g * (keep + green[i] / weight[i]);
                    lb = b * (keep + blue[i] / weight[i]);
                }
                landOnly[p] = ToByte(lr);
                landOnly[p + 1] = ToByte(lg);
                landOnly[p + 2] = ToByte(lb);
                // Lava: a darker vermilion glaze clipped to the field's edge, no halo around it.
                if (world.m_lavaField != null)
                {
                    float wxi = (i % n + 0.5f) * metres - InkPainter.HalfExtent, wzi = (i / n + 0.5f) * metres - InkPainter.HalfExtent;
                    float lava = Mathf.Clamp01((world.Sample(world.m_lavaField, wxi, wzi) - 0.47f) / 0.06f);
                    if (lava > 0f && world.m_biome[world.Index(wxi, wzi)] == Land.Ashlands)
                    {
                        float s = s_lava.m_strength * lava;
                        lr *= 1f - s + s * Filter(s_lava.m_color.r, PaperColor.r);
                        lg *= 1f - s + s * Filter(s_lava.m_color.g, PaperColor.g);
                        lb *= 1f - s + s * Filter(s_lava.m_color.b, PaperColor.b);
                        landOnly[p] = ToByte(lr);
                        landOnly[p + 1] = ToByte(lg);
                        landOnly[p + 2] = ToByte(lb);
                    }
                }
                // The swamp's wet ground and shallow pools, raised to land so the swamp reads as one
                // marsh, keep a faint glaze of the water's blue by how wet they are, so the pools
                // still show where they are; its cool tone also lets brown stains stand out there.
                if (world.m_swampWet != null)
                {
                    float wxi = (i % n + 0.5f) * metres - InkPainter.HalfExtent, wzi = (i / n + 0.5f) * metres - InkPainter.HalfExtent;
                    float wet = world.Sample(world.m_swampWet, wxi, wzi);
                    if (wet > 0f && world.m_biome[world.Index(wxi, wzi)] == Land.Swamp)
                    {
                        WaterColor(lr, lg, lb, out float pr, out float pg, out float pb);
                        float k = SwampPoolGlaze * Mathf.Clamp01(wet);
                        lr += (pr - lr) * k;
                        lg += (pg - lg) * k;
                        lb += (pb - lb) * k;
                        landOnly[p] = ToByte(lr);
                        landOnly[p + 1] = ToByte(lg);
                        landOnly[p + 2] = ToByte(lb);
                    }
                }
                float water = waters[i];
                float wxw = (i % n + 0.5f) * metres - InkPainter.HalfExtent, wzw = (i / n + 0.5f) * metres - InkPainter.HalfExtent;
                WaterColor(r, g, b, world, wxw, wzw, out float wr, out float wg, out float wb);
                sheet[p] = ToByte(lr + (wr - lr) * water);
                sheet[p + 1] = ToByte(lg + (wg - lg) * water);
                sheet[p + 2] = ToByte(lb + (wb - lb) * water);
            }
            return new Wash { m_sheet = sheet, m_land = landOnly, m_parchment = parchment, m_rivers = rivers };
        }

        // Where the generator's streams run, as coverage of each paper pixel: the minimap misses
        // channels narrower than its 12 m pixels, so a StreamWater band is laid along their
        // courses. Rivers are wide enough to show by the heights; the generator's width for them
        // includes their banks, and a band that wide spills blue over the land.
        private static float[] Rivers(WorldSource world)
        {
            int n = FogSize;
            float metres = 2f * InkPainter.HalfExtent / n;
            var rivers = new float[n * n];
            foreach (RiverPath river in world.m_rivers)
            {
                if (!river.m_stream) continue;
                float length = Vector2.Distance(river.m_start, river.m_end);
                if (length <= 0f) continue;
                float radius = StreamWater * 0.5f / metres;
                float step = Math.Max(1f, radius) * metres;
                for (float t = 0f; t <= length; t += step)
                {
                    Vector2 w = river.PointAt(t);
                    float cx = (w.x + InkPainter.HalfExtent) / metres - 0.5f, cy = (w.y + InkPainter.HalfExtent) / metres - 0.5f;
                    int x0 = Math.Max(0, (int)(cx - radius - 1f)), x1 = Math.Min(n - 1, (int)(cx + radius + 1f));
                    int y0 = Math.Max(0, (int)(cy - radius - 1f)), y1 = Math.Min(n - 1, (int)(cy + radius + 1f));
                    for (int y = y0; y <= y1; y++)
                    {
                        for (int x = x0; x <= x1; x++)
                        {
                            float dx = x - cx, dy = y - cy;
                            float cover = Mathf.Clamp01(radius + 0.5f - Mathf.Sqrt(dx * dx + dy * dy));
                            int i = y * n + x;
                            if (cover > rivers[i]) rivers[i] = cover;
                        }
                    }
                }
            }
            return rivers;
        }

        // The glaze a body of this kind is filled with: the mountains' or the Deep North's wash,
        // or for a Mistlands spire the Mistlands' own wash as its woods have it (WoodTint), the
        // ground most spires stand in, so the rock is that land's colour, a shade deeper than
        // its open ground. Bodies take it strictly, whatever biome they reach over.
        private static Tint BodyTint(BodyKind kind)
        {
            switch (kind)
            {
                case BodyKind.North: return s_tints[(int)Land.DeepNorth];
                case BodyKind.Mist: return WoodTint(Land.Mistlands);
                default: return s_tints[(int)Land.Mountain];
            }
        }

        // A body's glaze on clean paper.
        public static Color32 BodyColor(BodyKind kind) => Glazed(BodyTint(kind));

        // A pin's body: the wash of the biome it stands in on clean paper, as a peak's body takes
        // the mountains', or the water's on water.
        public static Color32 PinBodyColor(Land land, bool water)
        {
            if (!water) return Glazed(land);
            Color32 p = PaperColor;
            WaterColor(p.r, p.g, p.b, out float r, out float g, out float b);
            return new Color32(ToByte(r), ToByte(g), ToByte(b), 255);
        }

        // A pin body's colour browned by the paper's patina where the pin stands (world metres),
        // so a pin does not look cleaner than the paper round it.
        public static Color32 AgedPin(Color32 colour, float wx, float wz, int seed)
        {
            float b = Age((int)((wx + InkPainter.HalfExtent) / (2f * InkPainter.HalfExtent) * FogSize),
                (int)((wz + InkPainter.HalfExtent) / (2f * InkPainter.HalfExtent) * FogSize), seed);
            return new Color32(ToByte(colour.r * Through(s_stainTint.x, b)), ToByte(colour.g * Through(s_stainTint.y, b)),
                ToByte(colour.b * Through(s_stainTint.z, b)), colour.a);
        }

        // A biome's wash on clean paper.
        private static Color32 Glazed(Land land) => Glazed(s_tints[(int)land]);

        // A glaze on clean paper.
        private static Color32 Glazed(Tint t)
        {
            float s = t.m_strength;
            return new Color32(
                ToByte(PaperColor.r * (1f - s + s * Filter(t.m_color.r, PaperColor.r))),
                ToByte(PaperColor.g * (1f - s + s * Filter(t.m_color.g, PaperColor.g))),
                ToByte(PaperColor.b * (1f - s + s * Filter(t.m_color.b, PaperColor.b))), 255);
        }

        // The sea wash glazed over a colour, amount (0 to 1, SeaWash.At) of its strength.
        public static void SeaGlaze(float r, float g, float b, float amount, out float sr, out float sg, out float sb)
        {
            float s = s_seaWash.m_strength * amount;
            sr = r * (1f - s + s * Filter(s_seaWash.m_color.r, PaperColor.r));
            sg = g * (1f - s + s * Filter(s_seaWash.m_color.g, PaperColor.g));
            sb = b * (1f - s + s * Filter(s_seaWash.m_color.b, PaperColor.b));
        }

        // The sheet's ink texture lies over the paper by alpha, so it cannot glaze. The sea wash is
        // laid in it as the colour and opacity that, at full wash over clean water, give exactly
        // the glaze close-ups lay (SeaGlaze): the opacity is the largest channel's loss, so that
        // channel is a pure glaze and stains under the wash still show through; the others come
        // within a level or two of it on stained paper.
        private static void SeaInk(out float alpha, out float r, out float g, out float b)
        {
            Color32 p = PaperColor;
            WaterColor(p.r, p.g, p.b, out float wr, out float wg, out float wb);
            SeaGlaze(wr, wg, wb, 1f, out float sr, out float sg, out float sb);
            alpha = Math.Max(1f - sr / wr, Math.Max(1f - sg / wg, 1f - sb / wb));
            if (alpha <= 0f)
            {
                r = g = b = 0f;
                return;
            }
            r = wr - (wr - sr) / alpha;
            g = wg - (wg - sg) / alpha;
            b = wb - (wb - sb) / alpha;
        }

        // A spire's body on the sheet, as SeaInk lays the sea wash: the colour and opacity that,
        // over the Mistlands' ground wash on clean paper, give exactly the rock's glaze there
        // (BodyColor). Laid opaque as a peak's body is, the rock would be its clean-paper colour
        // on a sheet whose Mistlands are darkened by stains and patina, so it would read lighter
        // and lilac there while close-ups (BodyGlaze over the stained paper) show it deeper than
        // the ground; laid so, the sheet darkens the paper under it as the close-ups do. A peak's
        // body stays opaque: the ground round it is mostly the same mountain wash.
        private static void MistInk(out float alpha, out float r, out float g, out float b) =>
            InkOver(Glazed(Land.Mistlands), BodyColor(BodyKind.Mist), out alpha, out r, out g, out b);

        // The colour and opacity that, laid over ground, give target: the opacity is the largest
        // channel's loss, so that channel is a pure glaze. Nothing for a target no darker.
        private static void InkOver(Color32 ground, Color32 target, out float alpha, out float r, out float g, out float b)
        {
            alpha = Math.Max(1f - target.r / (float)ground.r, Math.Max(1f - target.g / (float)ground.g, 1f - target.b / (float)ground.b));
            if (alpha <= 0f)
            {
                alpha = r = g = b = 0f;
                return;
            }
            r = ground.r - (ground.r - target.r) / alpha;
            g = ground.g - (ground.g - target.g) / alpha;
            b = ground.b - (ground.b - target.b) / alpha;
        }

        // A tree's body (BodyKind.Wood): the wash its biome's ground takes in a wood (Washed),
        // strictly, as a peak's body takes the mountains'. The Mountains and the Deep North keep
        // their open wash under trees.
        private static Tint WoodTint(Land land)
        {
            Tint t = s_tints[(int)land];
            if (land != Land.DeepNorth && land != Land.Mountain) t.m_strength += WoodWash;
            return t;
        }

        // A tree's body on the sheet, as MistInk lays a spire's: the colour and opacity that,
        // over its biome's open ground wash on clean paper, give the wood wash. Inside a wood the
        // ground already has it, so crowns there come out a shade deeper than the floor; at a
        // wood's edge, where the blurred wash fades, they keep it.
        private static void WoodInk(Land land, out float alpha, out float r, out float g, out float b) =>
            InkOver(Glazed(land), Glazed(WoodTint(land)), out alpha, out r, out g, out b);

        // A tree's body in close-ups: its wood wash glazed over a parchment colour.
        public static void WoodGlaze(float r, float g, float b, Land land, out float pr, out float pg, out float pb)
        {
            Tint t = WoodTint(land);
            float s = t.m_strength;
            pr = r * (1f - s + s * Filter(t.m_color.r, PaperColor.r));
            pg = g * (1f - s + s * Filter(t.m_color.g, PaperColor.g));
            pb = b * (1f - s + s * Filter(t.m_color.b, PaperColor.b));
        }

        // A body's glaze over a parchment colour, for peak and spire bodies in close-ups.
        public static void BodyGlaze(float r, float g, float b, BodyKind kind, out float pr, out float pg, out float pb)
        {
            Tint t = BodyTint(kind);
            float s = t.m_strength;
            pr = r * (1f - s + s * Filter(t.m_color.r, PaperColor.r));
            pg = g * (1f - s + s * Filter(t.m_color.g, PaperColor.g));
            pb = b * (1f - s + s * Filter(t.m_color.b, PaperColor.b));
        }

        // RGBA ink for every mip level, as LoadRawTextureData expects it, from a painted sheet: ink
        // coverage, then the body masks in BodyKind order (InkPainter.Paint), each body filled
        // with its BodyColor under the ink (a spire's and a tree's as a glaze, MistInk and
        // WoodInk), with the wash of its
        // sea illustrations (marks, or null for none). In the glimpse past the explored land
        // (glimpse, Fog's, or null for none) the bodies and washes give way to the bare parchment
        // the Glimpse layer lays under this one, and the terrain's ink turns GlimpseInkColor; the
        // pins keep their red. The planes are read in place, not copied out: each is 16 MB.
        // Levels are averaged premultiplied, so ink and body mix rightly.
        public static byte[] InkWithMips(byte[] sheet, IList<SeaMark> marks, byte[] glimpse)
        {
            int n = InkPainter.CanvasSize;
            int plane = n * n;
            int total = 0;
            for (int size = n; size >= 1; size /= 2) total += size * size;
            var data = new byte[total * 4];
            Color32 peak = BodyColor(BodyKind.Mountain), northPeak = BodyColor(BodyKind.North);
            MistInk(out float mistAlpha, out float mistR, out float mistG, out float mistB);
            // A sheet missing a plane has no body of that kind.
            int bodyAt = sheet.Length >= 2 * plane ? plane : -1;
            int northAt = sheet.Length >= 3 * plane ? 2 * plane : -1;
            int mistAt = sheet.Length >= 4 * plane ? 3 * plane : -1;
            int woodAt = sheet.Length >= 5 * plane ? 4 * plane : -1;
            int pinInkAt = sheet.Length >= 6 * plane ? 5 * plane : -1;
            // Each wood code's ink (InkCanvas.WoodCode), worked out once.
            var woodAlpha = new float[8];
            var woodR = new float[8];
            var woodG = new float[8];
            var woodB = new float[8];
            for (int code = 1; code < 8; code++)
                WoodInk(InkCanvas.WoodLand((byte)(code << 5)), out woodAlpha[code], out woodR[code], out woodG[code], out woodB[code]);
            byte[] ink = sheet;
            SeaInk(out float seaAlpha, out float seaR, out float seaG, out float seaB);
            // Premultiplied RGBA of the current level, rows from the south.
            var level = new byte[n * n * 4];
            for (int y = 0; y < n; y++)
            {
                int from = (n - 1 - y) * n;
                for (int x = 0; x < n; x++)
                {
                    int i = from + x;
                    float a = ink[i] / 255f;
                    float g = GlimpseAt(glimpse, x + 0.5f, y + 0.5f);
                    // Bodies fade out in the glimpse, leaving the parchment under them.
                    float open = (1f - a) * (1f - g);
                    float m = bodyAt >= 0 ? sheet[bodyAt + i] / 255f : 0f;
                    float q = northAt >= 0 ? sheet[northAt + i] / 255f : 0f;
                    float k = mistAt >= 0 ? sheet[mistAt + i] / 255f : 0f;
                    byte wood = woodAt >= 0 ? sheet[woodAt + i] : (byte)0;
                    int code = wood >> 5;
                    float mu = m * open, qu = q * open, ku = k * mistAlpha * open;
                    float wu = InkCanvas.WoodCover(wood) * woodAlpha[code] * open;
                    // A pin's red ink lies under any black ink drawn over it later.
                    float pr = pinInkAt >= 0 ? sheet[pinInkAt + i] / 255f * (1f - a) : 0f;
                    float inkR = InkColor.r + (GlimpseInkColor.r - InkColor.r) * g;
                    float inkG = InkColor.g + (GlimpseInkColor.g - InkColor.g) * g;
                    float inkB = InkColor.b + (GlimpseInkColor.b - InkColor.b) * g;
                    int o = (y * n + x) * 4;
                    level[o] = ToByte(peak.r * mu + northPeak.r * qu + mistR * ku + woodR[code] * wu + PinInkColor.r * pr + inkR * a);
                    level[o + 1] = ToByte(peak.g * mu + northPeak.g * qu + mistG * ku + woodG[code] * wu + PinInkColor.g * pr + inkG * a);
                    level[o + 2] = ToByte(peak.b * mu + northPeak.b * qu + mistB * ku + woodB[code] * wu + PinInkColor.b * pr + inkB * a);
                    level[o + 3] = ToByte(Math.Min(1f, mu + qu + ku + wu + pr + a) * 255f);
                }
            }
            // The sea illustrations' wash under their ink. They stand in open water far from any
            // peak, so only the ink lies over it.
            if (marks != null && seaAlpha > 0f)
            {
                foreach (SeaMark mark in marks)
                {
                    SeaWash wash = SeaWash.Build(mark);
                    int x0 = Math.Max(0, (int)wash.Left), x1 = Math.Min(n - 1, (int)Math.Ceiling(wash.Right));
                    int y0 = Math.Max(0, (int)wash.Top), y1 = Math.Min(n - 1, (int)Math.Ceiling(wash.Bottom));
                    for (int sy = y0; sy <= y1; sy++)
                    {
                        int from = sy * n, y = n - 1 - sy;
                        for (int x = x0; x <= x1; x++)
                        {
                            float su = wash.At(x + 0.5f, sy + 0.5f) * seaAlpha;
                            if (su <= 0f) continue;
                            float a = ink[from + x] / 255f;
                            float g = GlimpseAt(glimpse, x + 0.5f, y + 0.5f);
                            su *= (1f - a) * (1f - g);
                            float inkR = InkColor.r + (GlimpseInkColor.r - InkColor.r) * g;
                            float inkG = InkColor.g + (GlimpseInkColor.g - InkColor.g) * g;
                            float inkB = InkColor.b + (GlimpseInkColor.b - InkColor.b) * g;
                            int o = (y * n + x) * 4;
                            level[o] = ToByte(seaR * su + inkR * a);
                            level[o + 1] = ToByte(seaG * su + inkG * a);
                            level[o + 2] = ToByte(seaB * su + inkB * a);
                            level[o + 3] = ToByte(Math.Min(1f, su + a) * 255f);
                        }
                    }
                }
            }
            int offset = 0;
            for (int size = n; ; size /= 2)
            {
                for (int i = 0; i < size * size; i++)
                {
                    int o = offset + i * 4, p = i * 4;
                    int alpha = level[p + 3];
                    data[o + 3] = (byte)alpha;
                    if (alpha == 0) continue;
                    data[o] = (byte)Math.Min(255, level[p] * 255 / alpha);
                    data[o + 1] = (byte)Math.Min(255, level[p + 1] * 255 / alpha);
                    data[o + 2] = (byte)Math.Min(255, level[p + 2] * 255 / alpha);
                }
                offset += size * size * 4;
                if (size == 1) break;
                int next = size / 2;
                var smaller = new byte[next * next * 4];
                for (int y = 0; y < next; y++)
                {
                    for (int x = 0; x < next; x++)
                    {
                        int a = ((2 * y) * size + 2 * x) * 4, b = a + size * 4;
                        int o = (y * next + x) * 4;
                        for (int c = 0; c < 4; c++)
                            smaller[o + c] = (byte)((level[a + c] + level[a + 4 + c] + level[b + c] + level[b + 4 + c] + 2) / 4);
                    }
                }
                level = smaller;
            }
            return data;
        }

        // Fills the alpha of an RGBA fog buffer whose colours are the parchment: opaque where the
        // snapshot shows nothing explored, clear where it does, with a torn edge between. The
        // edge lies GlimpseBand metres out from the explored land, and round each of glimpses
        // (world x, z: the game's marks out in the unexplored), and glimpse (RGBA, the
        // parchment's colours) gets that band, explored land excepted, as opacity: laid over the
        // paper's wash and under the ink, it shows the map there on bare, untinted parchment, a
        // glimpse past what was explored. marks are the sheet's sea illustrations, each shown or
        // hidden whole (null when not known); trees are the sheet's trees' footprints, and those
        // the torn edge would cut are hidden (HideCutTrees; null for none). With glimpseBand
        // false (AtlasPlugin.Glimpse off) the torn edge lies on the explored land's own edge,
        // glimpses are ignored, and the glimpse only lies under the fog, so no bare parchment or
        // greyer ink shows past it.
        public static void Fog(byte[] rgba, byte[] glimpse, byte[] parchment, Snapshot snapshot, IList<SeaMark> marks, IList<Vector2> glimpses,
            IList<Vector4> trees = null, bool glimpseBand = true)
        {
            int n = FogSize;
            float metres = 2f * InkPainter.HalfExtent / n;
            var cover = new float[n * n];
            var inside = new bool[n * n];
            float edge = InkPainter.EdgeRadius - FrameBand;
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                {
                    float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                    float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                    if (wx * wx + wz * wz > edge * edge) continue;
                    inside[y * n + x] = true;
                    int seen = 0;
                    for (int sy = -1; sy <= 1; sy++)
                        for (int sx = -1; sx <= 1; sx++)
                            if (snapshot != null && snapshot.IsExplored(wx + sx * metres / 3f, wz + sy * metres / 3f)) seen++;
                    cover[y * n + x] = 1f - seen / 9f;
                }
            }
            // The glimpse: the explored land's own edge, softened, is where the bare parchment
            // starts; the fog's torn edge lies GlimpseBand further out, and round every glimpse.
            var unexplored = (float[])cover.Clone();
            Blur(unexplored, n);
            Blur(unexplored, n);
            if (glimpseBand) Band(cover, inside, n, metres, glimpses);
            if (snapshot != null && marks != null)
                foreach (SeaMark mark in marks) SeaMarkFog(cover, n, metres, snapshot, mark);
            for (int pass = 0; pass < RipBlur; pass++) Blur(cover, n);
            // The blurred cover runs from 0 to 1 across about 2 * RipBlur pixels of the edge. Noise
            // on it moves the 0.5 crossing back and forth inside that band, so the edge tears in
            // ragged bays and tongues with fine fibres on them; nowhere else changes.
            for (int i = 0; i < n * n; i++)
            {
                float c = cover[i];
                if (c <= 0f || c >= 1f) continue;
                int x = i % n, y = i / n;
                float tear = (Fbm(x / RipGrain, y / RipGrain, RipSeed) - 0.5f) * RipTear
                    + (Noise.Value(x / RipFibre, y / RipFibre, RipSeed + 5) - 0.5f) * RipFibres;
                cover[i] = c + tear;
            }
            for (int i = 0; i < n * n; i++)
            {
                int o = i * 4, p = i * 3;
                int x = i % n, y = i / n;
                float c = cover[i];
                // The torn sheet, crisp over about half a pixel.
                float a = Smooth(0.5f - RipEdge, 0.5f + RipEdge, c);
                if (a > 0f)
                {
                    // Tearing pulls the paper's paler core out along the edge, as a fuzzy rim.
                    float rim = a * (1f - Smooth(0.5f, 0.5f + RipRim, c)) * RipRimLight;
                    rgba[o] = ToByte(parchment[p] * (1f + rim));
                    rgba[o + 1] = ToByte(parchment[p + 1] * (1f + rim));
                    rgba[o + 2] = ToByte(parchment[p + 2] * (1f + rim));
                    rgba[o + 3] = ToByte(a * 255f);
                    continue;
                }
                // Clear, but still paper-coloured: filtering blends a clear pixel's colour into
                // the edge beside it.
                rgba[o] = parchment[p];
                rgba[o + 1] = parchment[p + 1];
                rgba[o + 2] = parchment[p + 2];
                rgba[o + 3] = 0;
            }
            // The glimpse's bare parchment: wherever the land is not explored (softened at the
            // explored edge); the fog lies over all of it but the band. The trees need it too.
            byte[] beyond = glimpse ?? new byte[n * n * 4];
            for (int i = 0; i < n * n; i++)
            {
                int o = i * 4, p = i * 3;
                beyond[o] = parchment[p];
                beyond[o + 1] = parchment[p + 1];
                beyond[o + 2] = parchment[p + 2];
                beyond[o + 3] = inside[i] ? ToByte(unexplored[i] * 255f) : (byte)0;
            }
            if (trees != null) HideCutTrees(rgba, beyond, parchment, trees);
            // Without the glimpse it takes the fog's own opacity: wherever it would show, the fog
            // lies over it, and close-ups judge the trees the tear cuts by the fog alone.
            if (!glimpseBand)
                for (int i = 0; i < n * n; i++) beyond[i * 4 + 3] = rgba[i * 4 + 3];
        }

        // Moves the fog's edge (cover, 1 where nothing is explored) GlimpseBand metres out from
        // the explored land, and opens it round each of glimpses (world x, z): the glimpse's band.
        private static void Band(float[] cover, bool[] inside, int n, float metres, IList<Vector2> glimpses)
        {
            var seeds = new bool[n * n];
            for (int i = 0; i < n * n; i++) seeds[i] = inside[i] && cover[i] < 0.5f;
            if (glimpses != null)
            {
                foreach (Vector2 at in glimpses)
                {
                    int gx = Mathf.Clamp((int)((at.x + InkPainter.HalfExtent) / metres), 0, n - 1);
                    int gy = Mathf.Clamp((int)((at.y + InkPainter.HalfExtent) / metres), 0, n - 1);
                    seeds[gy * n + gx] = true;
                }
            }
            var distance = new byte[n * n];
            new DistanceField(n).Compute(i => seeds[i], distance, GlimpseSteps);
            float band = GlimpseBand / metres;
            for (int i = 0; i < n * n; i++)
            {
                if (!inside[i]) continue;
                cover[i] = Mathf.Clamp01(distance[i] / GlimpseSteps - band + 0.5f);
            }
        }

        // A tree the fog's torn edge would cut is hidden whole, as a sea illustration is: the fog's
        // parchment is laid over its footprint, over the glimpse's same bare parchment, so it reads
        // as never drawn. Only trees standing beyond the explored land are hidden.
        // A tree is cut where the fog's opacity over its footprint spans these.
        private const float TreeCutLeast = 0.1f;
        private const float TreeCutMost = 0.9f;
        // The patch laid over a hidden tree, as a share of the ellipse in its footprint.
        private const float TreeCutGrow = 0.9f;

        private static void HideCutTrees(byte[] fog, byte[] glimpse, byte[] parchment, IList<Vector4> trees)
        {
            // Every tree is judged against the torn edge alone, then the cut ones are covered:
            // judged against the patches too, each patch cut the trees round it in turn and a wood
            // in the glimpse was eaten away from the tear inwards.
            var cut = new List<Vector4>();
            foreach (Vector4 tree in trees)
                if (CutByFog(fog, glimpse, tree)) cut.Add(tree);
            foreach (Vector4 tree in cut) CoverTree(fog, parchment, tree);
        }

        // Whether the fog (Fog's RGBA) would cut a tree's footprint (left, top, right, bottom in
        // sheet pixels) standing beyond the explored land (glimpse, Fog's): it covers part of it
        // but not all. Close-ups leave such trees out (InkPainter.RunTile).
        public static bool CutByFog(byte[] fog, byte[] glimpse, Vector4 box)
        {
            float canvas = InkPainter.CanvasSize;
            float footX = (box.x + box.z) * 0.5f;
            if (GlimpseAt(glimpse, footX, canvas - box.w) < 0.5f) return false;
            float least = 1f, most = 0f;
            const int Samples = 4;
            for (int sy = 0; sy < Samples; sy++)
            {
                float y = box.y + (box.w - box.y) * (sy + 0.5f) / Samples;
                for (int sx = 0; sx < Samples; sx++)
                {
                    float x = box.x + (box.z - box.x) * (sx + 0.5f) / Samples;
                    float a = GlimpseAt(fog, x, canvas - y);
                    least = Math.Min(least, a);
                    most = Math.Max(most, a);
                }
            }
            return most > TreeCutLeast && least < TreeCutMost;
        }

        // Lays the fog's parchment, opaque, over the ellipse in a tree's footprint.
        private static void CoverTree(byte[] fog, byte[] parchment, Vector4 box)
        {
            int n = FogSize;
            float scale = n / (float)InkPainter.CanvasSize;
            float cx = (box.x + box.z) * 0.5f * scale, cy = (InkPainter.CanvasSize - (box.y + box.w) * 0.5f) * scale;
            float rx = (box.z - box.x) * 0.5f * scale * TreeCutGrow, ry = (box.w - box.y) * 0.5f * scale * TreeCutGrow;
            float soft = Math.Max(1f, Math.Min(rx, ry));
            int x0 = Math.Max(0, (int)(cx - rx - 1f)), x1 = Math.Min(n - 1, (int)(cx + rx + 1f));
            int y0 = Math.Max(0, (int)(cy - ry - 1f)), y1 = Math.Min(n - 1, (int)(cy + ry + 1f));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = (x + 0.5f - cx) / rx, dy = (y + 0.5f - cy) / ry;
                    float cover = Mathf.Clamp01((1f - Mathf.Sqrt(dx * dx + dy * dy)) * soft + 0.5f);
                    if (cover <= 0f) continue;
                    int o = (y * n + x) * 4, p = (y * n + x) * 3;
                    float a = fog[o + 3] / 255f;
                    float total = a + cover * (1f - a);
                    for (int c = 0; c < 3; c++)
                        fog[o + c] = ToByte((fog[o + c] * a + parchment[p + c] * cover * (1f - a)) / total);
                    fog[o + 3] = ToByte(total * 255f);
                }
            }
        }

        private static float Smooth(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        // Shows or hides one sea illustration whole, before the fog is blurred: the fog's own soft
        // edge across a monster reads as half a monster. Its box lies far out in open water, so
        // lifting the fog off a little unexplored sea, or laying it over a little explored sea,
        // shows or hides nothing but water.
        private static void SeaMarkFog(float[] cover, int n, float metres, Snapshot snapshot, SeaMark mark)
        {
            int seen = 0, total = 0;
            for (float wz = mark.m_south + SeaMarkStep * 0.5f; wz < mark.m_north; wz += SeaMarkStep)
            {
                for (float wx = mark.m_west + SeaMarkStep * 0.5f; wx < mark.m_east; wx += SeaMarkStep)
                {
                    total++;
                    if (snapshot.IsExplored(wx, wz)) seen++;
                }
            }
            if (total == 0) return;
            float value = seen >= SeaMarkShown * total ? 0f : 1f;
            int x0 = Mathf.Max(0, Mathf.FloorToInt((mark.m_west - SeaMarkMargin + InkPainter.HalfExtent) / metres));
            int x1 = Mathf.Min(n - 1, Mathf.CeilToInt((mark.m_east + SeaMarkMargin + InkPainter.HalfExtent) / metres));
            int y0 = Mathf.Max(0, Mathf.FloorToInt((mark.m_south - SeaMarkMargin + InkPainter.HalfExtent) / metres));
            int y1 = Mathf.Min(n - 1, Mathf.CeilToInt((mark.m_north + SeaMarkMargin + InkPainter.HalfExtent) / metres));
            for (int y = y0; y <= y1; y++)
            {
                float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                float dz = Math.Max(0f, Math.Max(mark.m_south - wz, wz - mark.m_north));
                for (int x = x0; x <= x1; x++)
                {
                    float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                    float dx = Math.Max(0f, Math.Max(mark.m_west - wx, wx - mark.m_east));
                    if (dx * dx + dz * dz <= SeaMarkMargin * SeaMarkMargin) cover[y * n + x] = value;
                }
            }
        }

        // A 3x3 box blur, rows then columns.
        public static void Blur(float[] values, int n)
        {
            var row = new float[n];
            for (int y = 0; y < n; y++)
            {
                for (int x = 0; x < n; x++)
                    row[x] = (values[y * n + Math.Max(0, x - 1)] + values[y * n + x] + values[y * n + Math.Min(n - 1, x + 1)]) / 3f;
                Array.Copy(row, 0, values, y * n, n);
            }
            for (int x = 0; x < n; x++)
            {
                for (int y = 0; y < n; y++)
                    row[y] = (values[Math.Max(0, y - 1) * n + x] + values[y * n + x] + values[Math.Min(n - 1, y + 1) * n + x]) / 3f;
                for (int y = 0; y < n; y++) values[y * n + x] = row[y];
            }
        }

        private static byte ToByte(float value) => (byte)Mathf.Clamp(value + 0.5f, 0f, 255f);
    }
}
