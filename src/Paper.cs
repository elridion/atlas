using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
        // Where the drawing stops (the user's design): across the glimpse's band the cartographer
        // draws less and less, then stops, as on maps of the period where a coast simply ends in
        // blank paper; no tear, rim or shadow (a tear is damage to the sheet). The band's end is
        // moved in and out by a slow fBm every EdgeGrain fog pixels, EdgeWobble pixels either way
        // (bays and tongues, no fibres). From ThinFrom of the band on, trees and wave strokes are
        // left out one by one, more of them towards its end (EdgeThin, by a hash of their foot,
        // salt ThinSalt), and the lines (coast, rivers, ground marks) fade over its last
        // EdgeFade of it and half that past it.
        private const float EdgeGrain = 16f;
        private const float EdgeWobble = 3f;
        private const int EdgeSeed = 4127;
        private const float ThinFrom = 0.3f;
        private const float EdgeFade = 0.12f;
        private const int ThinSalt = 7919;
        // The glimpse past the explored land, in metres, and the distance field's steps a pixel
        // (its bytes reach 255 / GlimpseSteps pixels, well past the band).
        private const float GlimpseBand = 160f;
        private const float GlimpseSteps = 4f;
        // A sea illustration is drawn only when it lies wholly in the explored land's colour
        // (SeaMarkSeen): its box and SeaMarkMargin metres round it all explored, past the glimpse's
        // softening of the explored edge (about 21 m) and its wash's reach (the blue wedge runs up
        // to 8 sheet px, 42 m, under a figure's waterline), so neither the glimpse's parchment nor
        // the fog ever lies over part of it; otherwise it is left out (FogEdge.m_hiddenSea) and
        // its water stays as the fog lays it. The box is sampled every SeaMarkStep metres, finer
        // than vanilla's 12 m exploration grid.
        private const float SeaMarkMargin = 70f;
        private const float SeaMarkStep = 6f;
        // How deep past the depth where the wobble can no longer reach the fog counts as wholly
        // opaque (Fog), well over the noise's float rounding.
        private const float FullMargin = 0.01f;
        // The fog's and the ink's loops run on the workers in bands of rows (FogRows fog rows,
        // InkBlock sheet rows), not row by row, so a worker the game holds up stalls little.
        // The ink skips blocks of InkBlock sheet pixels wholly under opaque fog (FoggedBlocks).
        private const int FogRows = 32;
        internal const int InkBlock = 32;

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
        // The Deep North's lakes take this share of the water's strength.
        private const float NorthLakeWater = 0.55f;
        // The sea marks' washes (SeaWash, the user's "take 3"): a second glaze of a deeper indigo
        // in the wedge of water under a mark's waterline, the shadow it casts trailing east, and
        // an iron grey glazed over a figure's body above the water. The grey is neutral to cool:
        // over the indigo it comes out blue-grey, never toward green or ochre (land) or red
        // (pins). A glaze can only darken (Filter caps a lighter tint), so the body cannot be
        // paler than the sea.
        private static readonly Tint s_seaWedge = new Tint(72, 100, 160, 0.30f);
        private static readonly Tint s_seaBody = new Tint(92, 92, 96, 0.36f);
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
        // How much of the water's blue a swamp pool keeps at full wetness. Wetness runs from
        // WorldSource.SwampDry above the water to SwampShallow below it, so the swamp's mean
        // wetness is about 0.45; this keeps its mean glaze where it was (about 0.27) when the
        // ground was wet from the water line down.
        private const float SwampPoolGlaze = 0.6f;

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

        public static void WaterColor(float r, float g, float b, out float wr, out float wg, out float wb) =>
            WaterColor(r, g, b, s_water.m_strength, out wr, out wg, out wb);

        private static void WaterColor(float r, float g, float b, float s, out float wr, out float wg, out float wb)
        {
            wr = r * (1f - s + s * Filter(s_water.m_color.r, PaperColor.r));
            wg = g * (1f - s + s * Filter(s_water.m_color.g, PaperColor.g));
            wb = b * (1f - s + s * Filter(s_water.m_color.b, PaperColor.b));
        }

        // The water's colour over this parchment at a world point, lighter on the Deep North's
        // lakes (NorthLakeWater, the user's call: frozen-over water among the snow) and warmed by
        // the Ashlands' boiling sea there (AshSeaGlaze).
        public static void WaterColor(float r, float g, float b, WorldSource world, float wx, float wz, out float wr, out float wg, out float wb)
        {
            float lake = world.m_northLake != null ? world.Sample(world.m_northLake, wx, wz) : 0f;
            WaterColor(r, g, b, s_water.m_strength * (1f - lake * (1f - NorthLakeWater)), out wr, out wg, out wb);
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
            // The darker, worn rim inside the sheet's trimmed edge.
            SheetEdge.AddRim(blots, n, seed);
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
                    // same grey the peaks' bodies are filled with. The Mistlands are wood
                    // throughout, so their ground keeps the open wash and their crowns and crags
                    // stay a shade deeper than it (WoodTint, MistInk).
                    Land ground = world.m_biome[world.Index(wx, wz)];
                    if (world.Sample(world.m_wood, wx, wz) > 0.5f && ground != Land.DeepNorth && ground != Land.Mountain && ground != Land.Mistlands) s += WoodWash;
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
                    if (!world.HasRiverBed(w)) continue;
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

        // A tint glazed over a colour, amount (0 to 1) of its strength.
        private static void Glaze(Tint t, float r, float g, float b, float amount, out float sr, out float sg, out float sb)
        {
            float s = t.m_strength * amount;
            sr = r * (1f - s + s * Filter(t.m_color.r, PaperColor.r));
            sg = g * (1f - s + s * Filter(t.m_color.g, PaperColor.g));
            sb = b * (1f - s + s * Filter(t.m_color.b, PaperColor.b));
        }

        // A sea mark's washes glazed over a colour in close-ups: the body's grey (body, 0 to 1,
        // SeaWash.At), then the blue wedge over it (wedge, SeaWash.WedgeAt).
        public static void SeaMarkGlaze(float r, float g, float b, float body, float wedge, out float sr, out float sg, out float sb)
        {
            sr = r;
            sg = g;
            sb = b;
            if (body > 0f) Glaze(s_seaBody, sr, sg, sb, body, out sr, out sg, out sb);
            if (wedge > 0f) Glaze(s_seaWedge, sr, sg, sb, wedge, out sr, out sg, out sb);
        }

        // The sheet's ink texture lies over the paper by alpha, so it cannot glaze. Each sea mark
        // wash (the wedge's blue, or the body's grey) is laid in it as the colour and opacity
        // that, at full wash over clean water, give exactly the glaze close-ups lay
        // (SeaMarkGlaze): the opacity is the largest channel's loss, so that channel is a pure
        // glaze and stains under the wash still show through; the others come within a level or
        // two of it on stained paper.
        private static void SeaInk(bool wedge, out float alpha, out float r, out float g, out float b)
        {
            Color32 p = PaperColor;
            WaterColor(p.r, p.g, p.b, out float wr, out float wg, out float wb);
            Glaze(wedge ? s_seaWedge : s_seaBody, wr, wg, wb, 1f, out float sr, out float sg, out float sb);
            alpha = Math.Max(1f - sr / wr, Math.Max(1f - sg / wg, 1f - sb / wb));
            if (alpha <= 0f)
            {
                alpha = r = g = b = 0f;
                return;
            }
            r = wr - (wr - sr) / alpha;
            g = wg - (wg - sg) / alpha;
            b = wb - (wb - sb) / alpha;
        }

        // A spire's body on the sheet, as SeaInk lays the sea washes: the colour and opacity that,
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
        // The Ashlands' rock (the sea thorns) is laid opaque, as a peak's body is: laid as a
        // glaze over the open burnt ground or the water, the lava fields' vermilion or the sea's
        // indigo under it would show through.
        private static void WoodInk(Land land, out float alpha, out float r, out float g, out float b)
        {
            if (land == Land.Ashlands)
            {
                Color32 rock = Glazed(WoodTint(land));
                alpha = 1f;
                r = rock.r;
                g = rock.g;
                b = rock.b;
                return;
            }
            InkOver(Glazed(land), Glazed(WoodTint(land)), out alpha, out r, out g, out b);
        }

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
        // pins keep their red. The marks the band leaves out (edge, Fog's, or null for none) are
        // cleared from the ink and bodies at the sheet's own resolution (HiddenInk); the pins'
        // plane is never cleared. The planes are read in place, not copied out: each is 16 MB.
        // Levels are averaged premultiplied, so ink and body mix rightly. With an edge, blocks
        // lying wholly under its opaque fog (FoggedBlocks) are left clear: nothing of them shows,
        // so only the coarsest levels, whose texels reach past the fog's edge, see the difference.
        public static byte[] InkWithMips(byte[] sheet, IList<SeaMark> marks, byte[] glimpse, FogEdge edge = null) =>
            Lay(null, null, sheet, null, null, marks, glimpse, edge).m_data;

        // Worker thread. The ink InkWithMips lays, byte for byte, laid again only where it can
        // differ from the last lay handed back (ReturnInk), into that lay's own pixels. The sheet
        // is that lay's own, or fromSheet with only the changed rectangles (sheet pixels) drawn
        // again (InkPainter.Repaint, Recheck). With no lay handed back, one laid from another
        // sheet, or a glimpse where it had none (or none where it had one), it is laid whole.
        public static LaidInk LayInk(byte[] sheet, byte[] fromSheet, IList<RectInt> changed, IList<SeaMark> marks, byte[] glimpse, FogEdge edge) =>
            LayInkInto(null, sheet, fromSheet, changed, marks, glimpse, edge);

        // LayInk, laid whole into spare (a buffer of InkBytes nothing else uses, or null for a
        // new one) when no lay was handed back.
        public static LaidInk LayInkInto(byte[] spare, byte[] sheet, byte[] fromSheet, IList<RectInt> changed, IList<SeaMark> marks, byte[] glimpse, FogEdge edge)
        {
            LaidInk last = Interlocked.Exchange(ref s_spareInk, null);
            return Lay(last, last?.m_data ?? spare, sheet, fromSheet, changed, marks, glimpse, edge);
        }

        /// <summary>
        /// The ink texture's pixels as laid (Paper.LayInk), with what they were laid from: the
        /// sheet, glimpse, cleared ink (HiddenInk), fogged blocks, the sea illustrations shown and
        /// their washes, and the premultiplied level KeptLevel, from which the coarser levels are
        /// made. The stored levels are un-premultiplied, and averaging those rounds otherwise,
        /// so a lay that changes some blocks makes their levels up to KeptLevel from their own
        /// pixels and every coarser level from the kept one. Handed back once uploaded
        /// (ReturnInk), the next lay starts from it (about 89 MB of pixels, 16 MB of cleared ink).
        /// </summary>
        public sealed class LaidInk
        {
            public byte[] m_data;
            // Counts the lays; m_from is the serial of the lay this one changed, 0 when laid whole.
            public int m_serial;
            public int m_from;
            // 256 px tiles (TilesAcross square, rows from the south) laid again over that lay,
            // null when laid whole; and how many InkBlock blocks were.
            public bool[] m_tiles;
            public int m_blocksLaid;
            internal byte[] m_sheet;
            internal byte[] m_glimpse;
            internal byte[] m_hidden;
            internal bool[] m_fogged;
            internal byte[] m_coarse;
            internal List<SeaMark> m_washMarks;
            internal List<SeaWash> m_washes;
        }

        // The texture's tiles (InkPatch) are TilePixels square on the first level.
        public const int TilePixels = 256;
        public const int TilesAcross = InkPainter.CanvasSize / TilePixels;
        // The level whose premultiplied texels are kept with a lay: one texel per InkBlock block.
        internal const int KeptLevel = 5;
        private static int s_laySerial;

        // The bytes of a laid ink's pixels, every level.
        public static int InkBytes
        {
            get
            {
                int total = 0;
                for (int size = InkPainter.CanvasSize; size >= 1; size /= 2) total += size * size;
                return total * 4;
            }
        }

        // Worker thread. The ink InkWithMips lays, read back from the disk cache (LaidCache): its
        // pixels and premultiplied level KeptLevel, laid for these inputs, with what a lay keeps
        // of them worked out again (cleared ink, fogged blocks, sea washes), so the next lay goes
        // on from it as from one laid here. null when data or coarse is not a laid ink's size.
        public static LaidInk LoadedInk(byte[] data, byte[] coarse, byte[] sheet, IList<SeaMark> marks, byte[] glimpse, FogEdge edge)
        {
            int blocks = InkPainter.CanvasSize / InkBlock;
            if (data == null || data.Length != InkBytes || coarse == null || coarse.Length != blocks * blocks * 4) return null;
            LayInputs(null, marks, edge, out bool[] fogged, out byte[] hidden, out List<SeaMark> shown, out List<SeaWash> washes);
            return new LaidInk
            {
                m_data = data,
                m_serial = Interlocked.Increment(ref s_laySerial),
                m_sheet = sheet,
                m_glimpse = glimpse,
                m_hidden = hidden,
                m_fogged = fogged,
                m_washMarks = shown,
                m_washes = washes,
                m_coarse = coarse,
            };
        }

        // What a lay reads besides the sheet and the glimpse, worked out from the fog's decisions
        // (edge): the blocks under opaque fog, the cleared ink, and the sea illustrations shown
        // with their washes (last's where it showed the same mark).
        private static void LayInputs(LaidInk last, IList<SeaMark> marks, FogEdge edge, out bool[] fogged, out byte[] hidden,
            out List<SeaMark> shown, out List<SeaWash> washes)
        {
            fogged = FoggedBlocks(edge?.m_edge);
            hidden = HiddenSeaInk(HiddenInk(edge), edge);
            shown = new List<SeaMark>();
            washes = new List<SeaWash>();
            if (marks == null) return;
            foreach (SeaMark mark in marks)
            {
                if (edge != null && edge.HidesSea(mark)) continue;
                shown.Add(mark);
                washes.Add(KeptWash(last, mark) ?? SeaWash.Build(mark));
            }
        }

        private static LaidInk Lay(LaidInk last, byte[] data, byte[] sheet, byte[] fromSheet, IList<RectInt> changed, IList<SeaMark> marks, byte[] glimpse, FogEdge edge)
        {
            int n = InkPainter.CanvasSize;
            int blocks = n / InkBlock;
            int plane = n * n;
            // Every byte is written below when laid whole, so a returned buffer serves as a new one.
            if (data == null || data.Length != InkBytes) data = new byte[InkBytes];
            // The sea illustrations shown and their washes, each built once and kept with the lay.
            LayInputs(last, marks, edge, out bool[] fogged, out byte[] hidden, out List<SeaMark> shown, out List<SeaWash> washes);
            // The blocks to lay again (rows from the south), or null for all.
            bool[] dirty = data == last?.m_data ? Unlaid(last, sheet, fromSheet, changed, glimpse, fogged, hidden, shown, washes) : null;
            Color32 peak = BodyColor(BodyKind.Mountain), northPeak = BodyColor(BodyKind.North);
            MistInk(out float mistAlpha, out float mistR, out float mistG, out float mistB);
            // A sheet missing a plane has no body of that kind.
            int bodyAt = sheet.Length >= 2 * plane ? plane : -1;
            int northAt = sheet.Length >= 3 * plane ? 2 * plane : -1;
            int mistAt = sheet.Length >= 4 * plane ? 3 * plane : -1;
            int woodAt = sheet.Length >= 5 * plane ? 4 * plane : -1;
            int pinInkAt = sheet.Length >= 6 * plane ? 5 * plane : -1;
            // Each wood code's ink (InkCanvas.WoodCode), worked out once.
            int codes = InkCanvas.WoodCodes;
            var woodAlpha = new float[codes];
            var woodR = new float[codes];
            var woodG = new float[codes];
            var woodB = new float[codes];
            for (int code = 1; code < codes; code++)
            {
                Land land = InkCanvas.WoodLandOf(code);
                if (land != Land.Ocean) WoodInk(land, out woodAlpha[code], out woodR[code], out woodG[code], out woodB[code]);
            }
            byte[] ink = sheet;
            SeaInk(false, out float bodyAlpha, out float bodyR, out float bodyG, out float bodyB);
            SeaInk(true, out float wedgeAlpha, out float wedgeR, out float wedgeG, out float wedgeB);
            // Premultiplied RGBA of the first level, rows from the south, laid where it goes in
            // data; each level is then made from the one before and un-premultiplied in place.
            Parallel.For(0, blocks, Workers.LoopOptions(), by =>
            {
                if (dirty != null && Array.IndexOf(dirty, true, by * blocks, blocks) < 0) return;
                for (int y = by * InkBlock; y < (by + 1) * InkBlock; y++)
                {
                    int from = (n - 1 - y) * n;
                    for (int bx = 0; bx < blocks; bx++)
                    {
                        if (dirty != null && !dirty[by * blocks + bx]) continue;
                        int x0 = bx * InkBlock;
                        if (fogged != null && fogged[by * blocks + bx])
                        {
                            Array.Clear(data, (y * n + x0) * 4, InkBlock * 4);
                            continue;
                        }
                        for (int x = x0; x < x0 + InkBlock; x++)
                        {
                            int i = from + x;
                            float keep = hidden != null ? 1f - hidden[i] / 255f : 1f;
                            float a = ink[i] / 255f * keep;
                            float g = GlimpseAt(glimpse, x + 0.5f, y + 0.5f);
                            // Bodies fade out in the glimpse, leaving the parchment under them, and go
                            // with the marks left out.
                            float open = (1f - a) * (1f - g) * keep;
                            float m = bodyAt >= 0 ? sheet[bodyAt + i] / 255f : 0f;
                            float q = northAt >= 0 ? sheet[northAt + i] / 255f : 0f;
                            float k = mistAt >= 0 ? sheet[mistAt + i] / 255f : 0f;
                            byte wood = woodAt >= 0 ? sheet[woodAt + i] : (byte)0;
                            int code = InkCanvas.WoodCodeOf(wood);
                            float mu = m * open, qu = q * open, ku = k * mistAlpha * open;
                            float wu = InkCanvas.WoodCover(wood) * woodAlpha[code] * open;
                            // A pin's red ink lies under any black ink drawn over it later.
                            float pr = pinInkAt >= 0 ? sheet[pinInkAt + i] / 255f * (1f - a) : 0f;
                            float inkR = InkColor.r + (GlimpseInkColor.r - InkColor.r) * g;
                            float inkG = InkColor.g + (GlimpseInkColor.g - InkColor.g) * g;
                            float inkB = InkColor.b + (GlimpseInkColor.b - InkColor.b) * g;
                            int o = (y * n + x) * 4;
                            data[o] = ToByte(peak.r * mu + northPeak.r * qu + mistR * ku + woodR[code] * wu + PinInkColor.r * pr + inkR * a);
                            data[o + 1] = ToByte(peak.g * mu + northPeak.g * qu + mistG * ku + woodG[code] * wu + PinInkColor.g * pr + inkG * a);
                            data[o + 2] = ToByte(peak.b * mu + northPeak.b * qu + mistB * ku + woodB[code] * wu + PinInkColor.b * pr + inkB * a);
                            data[o + 3] = ToByte(Math.Min(1f, mu + qu + ku + wu + pr + a) * 255f);
                        }
                    }
                }
            });
            // The sea marks' washes under their ink: the body's grey, the blue wedge over it. They
            // stand in open water far from any peak, so only the ink lies over them.
            for (int k = 0; k < shown.Count; k++)
            {
                SeaWash wash = washes[k];
                int x0 = Math.Max(0, (int)wash.Left), x1 = Math.Min(n - 1, (int)Math.Ceiling(wash.Right));
                int y0 = Math.Max(0, (int)wash.Top), y1 = Math.Min(n - 1, (int)Math.Ceiling(wash.Bottom));
                for (int sy = y0; sy <= y1; sy++)
                {
                    int from = sy * n, y = n - 1 - sy;
                    for (int x = x0; x <= x1; x++)
                    {
                        if (dirty != null && !dirty[y / InkBlock * blocks + x / InkBlock]) continue;
                        if (fogged != null && fogged[y / InkBlock * blocks + x / InkBlock]) continue;
                        float bu = wash.At(x + 0.5f, sy + 0.5f) * bodyAlpha;
                        float wu = wash.WedgeAt(x + 0.5f, sy + 0.5f) * wedgeAlpha;
                        if (bu <= 0f && wu <= 0f) continue;
                        float a = ink[from + x] / 255f * (hidden != null ? 1f - hidden[from + x] / 255f : 1f);
                        float g = GlimpseAt(glimpse, x + 0.5f, y + 0.5f);
                        float keep = (1f - a) * (1f - g);
                        bu *= keep;
                        wu *= keep;
                        // Premultiplied: the wedge over the body, the ink over both.
                        float pr = wedgeR * wu + bodyR * bu * (1f - wu);
                        float pg = wedgeG * wu + bodyG * bu * (1f - wu);
                        float pb = wedgeB * wu + bodyB * bu * (1f - wu);
                        float su = wu + bu * (1f - wu);
                        // A pin's red (pins stand over the sea marks) lies over the washes and
                        // under black ink, as in the main pass; this pass rewrites the pixel.
                        float red = pinInkAt >= 0 ? sheet[pinInkAt + from + x] / 255f * (1f - a) : 0f;
                        float under = 1f - red;
                        float inkR = InkColor.r + (GlimpseInkColor.r - InkColor.r) * g;
                        float inkG = InkColor.g + (GlimpseInkColor.g - InkColor.g) * g;
                        float inkB = InkColor.b + (GlimpseInkColor.b - InkColor.b) * g;
                        int o = (y * n + x) * 4;
                        data[o] = ToByte(pr * under + PinInkColor.r * red + inkR * a);
                        data[o + 1] = ToByte(pg * under + PinInkColor.g * red + inkG * a);
                        data[o + 2] = ToByte(pb * under + PinInkColor.b * red + inkB * a);
                        data[o + 3] = ToByte(Math.Min(1f, su * under + red + a) * 255f);
                    }
                }
            }
            var laid = new LaidInk
            {
                m_data = data,
                m_serial = Interlocked.Increment(ref s_laySerial),
                m_sheet = sheet,
                m_glimpse = glimpse,
                m_hidden = hidden,
                m_fogged = fogged,
                m_washMarks = shown,
                m_washes = washes,
                m_coarse = last?.m_coarse != null && last.m_coarse.Length == blocks * blocks * 4 ? last.m_coarse : new byte[blocks * blocks * 4],
            };
            if (dirty == null) MipsWhole(data, laid.m_coarse);
            else
            {
                laid.m_from = last.m_serial;
                laid.m_tiles = new bool[TilesAcross * TilesAcross];
                var laidBlocks = new List<int>();
                int perTile = TilePixels / InkBlock;
                for (int b = 0; b < dirty.Length; b++)
                {
                    if (!dirty[b]) continue;
                    laidBlocks.Add(b);
                    laid.m_tiles[b / blocks / perTile * TilesAcross + b % blocks / perTile] = true;
                }
                laid.m_blocksLaid = laidBlocks.Count;
                if (laidBlocks.Count > 0) MipsOfBlocks(data, laid.m_coarse, laidBlocks);
            }
            return laid;
        }

        // Each level's offset in a laid ink's pixels (RGBA), the first level first.
        internal static int[] LevelOffsets()
        {
            int n = InkPainter.CanvasSize;
            var offsets = new List<int>();
            int offset = 0;
            for (int size = n; size >= 1; size /= 2)
            {
                offsets.Add(offset);
                offset += size * size * 4;
            }
            return offsets.ToArray();
        }

        // Makes every level from the first (premultiplied, in place) and un-premultiplies them all,
        // keeping a premultiplied copy of level KeptLevel in coarse.
        private static void MipsWhole(byte[] data, byte[] coarse)
        {
            int n = InkPainter.CanvasSize, kept = n >> KeptLevel;
            int offset = 0;
            for (int size = n; ; size /= 2)
            {
                int next = size / 2, at = offset, below = offset + size * size * 4;
                // The next level from this one while it is still premultiplied.
                if (size > 1) MipRows(next, (from, to) =>
                {
                    for (int y = from; y < to; y++)
                    {
                        for (int x = 0; x < next; x++)
                        {
                            int a = at + ((2 * y) * size + 2 * x) * 4, b = a + size * 4;
                            int o = below + (y * next + x) * 4;
                            for (int c = 0; c < 4; c++)
                                data[o + c] = (byte)((data[a + c] + data[a + 4 + c] + data[b + c] + data[b + 4 + c] + 2) / 4);
                        }
                    }
                });
                if (size == kept) Buffer.BlockCopy(data, at, coarse, 0, size * size * 4);
                MipRows(size, (from, to) =>
                {
                    for (int o = at + from * size * 4; o < at + to * size * 4; o += 4) Unpremultiply(data, o);
                });
                offset = below;
                if (size == 1) break;
            }
        }

        // Makes the levels again over the blocks laid again (their first level premultiplied, in
        // place): up to KeptLevel from each block's own texels, as MipsWhole averages the same
        // texels in the same way, then every coarser level whole from the kept premultiplied
        // level (coarse, updated for the blocks).
        private static void MipsOfBlocks(byte[] data, byte[] coarse, List<int> laidBlocks)
        {
            int n = InkPainter.CanvasSize, blocks = n / InkBlock;
            int[] offsets = LevelOffsets();
            Parallel.For(0, laidBlocks.Count, Workers.LoopOptions(), j =>
            {
                int bx = laidBlocks[j] % blocks, by = laidBlocks[j] / blocks;
                for (int k = 1; k <= KeptLevel; k++)
                {
                    int size = n >> k, above = n >> (k - 1), side = InkBlock >> k;
                    int at = offsets[k - 1], below = offsets[k];
                    for (int y = by * side; y < (by + 1) * side; y++)
                    {
                        for (int x = bx * side; x < (bx + 1) * side; x++)
                        {
                            int a = at + ((2 * y) * above + 2 * x) * 4, b = a + above * 4;
                            int o = below + (y * size + x) * 4;
                            for (int c = 0; c < 4; c++)
                                data[o + c] = (byte)((data[a + c] + data[a + 4 + c] + data[b + c] + data[b + 4 + c] + 2) / 4);
                        }
                    }
                }
                Buffer.BlockCopy(data, offsets[KeptLevel] + laidBlocks[j] * 4, coarse, laidBlocks[j] * 4, 4);
                for (int k = 0; k <= KeptLevel; k++)
                {
                    int size = n >> k, side = InkBlock >> k;
                    for (int y = by * side; y < (by + 1) * side; y++)
                        for (int x = bx * side; x < (bx + 1) * side; x++) Unpremultiply(data, offsets[k] + (y * size + x) * 4);
                }
            });
            byte[] level = coarse;
            for (int k = KeptLevel + 1; k < offsets.Length; k++)
            {
                int size = n >> k, above = size * 2;
                var next = new byte[size * size * 4];
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        int a = ((2 * y) * above + 2 * x) * 4, b = a + above * 4;
                        int o = (y * size + x) * 4;
                        for (int c = 0; c < 4; c++)
                            next[o + c] = (byte)((level[a + c] + level[a + 4 + c] + level[b + c] + level[b + 4 + c] + 2) / 4);
                    }
                }
                Buffer.BlockCopy(next, 0, data, offsets[k], next.Length);
                for (int o = 0; o < next.Length; o += 4) Unpremultiply(data, offsets[k] + o);
                level = next;
            }
        }

        // A premultiplied RGBA texel as the texture stores it, straight.
        private static void Unpremultiply(byte[] data, int o)
        {
            int alpha = data[o + 3];
            if (alpha == 0)
            {
                data[o] = data[o + 1] = data[o + 2] = 0;
                return;
            }
            data[o] = (byte)Math.Min(255, data[o] * 255 / alpha);
            data[o + 1] = (byte)Math.Min(255, data[o + 1] * 255 / alpha);
            data[o + 2] = (byte)Math.Min(255, data[o + 2] * 255 / alpha);
        }

        // The blocks (rows from the south) where a lay of sheet, glimpse, fogged, hidden and the
        // sea washes shown can differ from last's, or null when it is to be laid whole. A texel of
        // the first level reads only its own sheet pixel's planes and cleared ink, its block's
        // fog flag, the glimpse round it and the washes over it, so it is the same wherever all
        // of those are.
        private static bool[] Unlaid(LaidInk last, byte[] sheet, byte[] fromSheet, IList<RectInt> changed, byte[] glimpse, bool[] fogged, byte[] hidden,
            List<SeaMark> shown, List<SeaWash> washes)
        {
            if (last?.m_data == null || last.m_coarse == null || last.m_sheet == null || last.m_washMarks == null) return null;
            bool sameSheet = last.m_sheet == sheet;
            if (!sameSheet && (changed == null || last.m_sheet != fromSheet || fromSheet.Length != sheet.Length)) return null;
            if ((last.m_glimpse == null) != (glimpse == null)) return null;
            int n = InkPainter.CanvasSize, blocks = n / InkBlock;
            var dirty = new bool[blocks * blocks];
            // Sheet rectangles run from the north.
            if (!sameSheet)
                foreach (RectInt r in changed) MarkBlocks(dirty, r.xMin, n - r.yMax, r.xMax - 1, n - 1 - r.yMin);
            for (int i = 0; i < dirty.Length; i++)
                if ((last.m_fogged != null && last.m_fogged[i]) != (fogged != null && fogged[i])) dirty[i] = true;
            byte[] lastHidden = last.m_hidden, lastGlimpse = last.m_glimpse;
            bool hiddenChanged = lastHidden != hidden, glimpseChanged = lastGlimpse != glimpse;
            // A block reads the glimpse's fog pixels from one before its own to one after.
            int fogPerBlock = InkBlock * FogSize / n;
            if (hiddenChanged || glimpseChanged) Parallel.For(0, blocks, Workers.LoopOptions(), by =>
            {
                // Each iteration writes its own row of blocks only.
                if (hiddenChanged)
                {
                    for (int y = by * InkBlock; y < (by + 1) * InkBlock; y++)
                    {
                        int from = (n - 1 - y) * n;
                        for (int x = 0; x < n; x++)
                        {
                            byte was = lastHidden != null ? lastHidden[from + x] : (byte)0, now = hidden != null ? hidden[from + x] : (byte)0;
                            if (was == now) continue;
                            dirty[by * blocks + x / InkBlock] = true;
                            x = (x / InkBlock + 1) * InkBlock - 1;
                        }
                    }
                }
                if (glimpseChanged)
                {
                    int fy0 = Math.Max(0, by * fogPerBlock - 1), fy1 = Math.Min(FogSize - 1, (by + 1) * fogPerBlock);
                    for (int fy = fy0; fy <= fy1; fy++)
                    {
                        for (int fx = 0; fx < FogSize; fx++)
                        {
                            int a = (fy * FogSize + fx) * 4 + 3;
                            if (lastGlimpse[a] == glimpse[a]) continue;
                            int bx0 = Math.Max(0, (fx + fogPerBlock - 1) / fogPerBlock - 1), bx1 = Math.Min(blocks - 1, (fx + 1) / fogPerBlock);
                            for (int bx = bx0; bx <= bx1; bx++) dirty[by * blocks + bx] = true;
                        }
                    }
                }
            });
            // Washes shown in one lay and not the other. Those shown in both are the same washes
            // over the same pixels: sea illustrations stand far apart, so no two washes overlap.
            for (int k = 0; k < last.m_washMarks.Count; k++)
                if (IndexOf(shown, last.m_washMarks[k]) < 0) MarkWash(dirty, last.m_washes[k]);
            for (int k = 0; k < shown.Count; k++)
                if (IndexOf(last.m_washMarks, shown[k]) < 0) MarkWash(dirty, washes[k]);
            return dirty;
        }

        // Marks the blocks over the first level's texels x0 to x1, y0 to y1 (rows from the south).
        private static void MarkBlocks(bool[] dirty, int x0, int y0, int x1, int y1)
        {
            int n = InkPainter.CanvasSize, blocks = n / InkBlock;
            x0 = Math.Max(0, x0);
            y0 = Math.Max(0, y0);
            x1 = Math.Min(n - 1, x1);
            y1 = Math.Min(n - 1, y1);
            for (int by = y0 / InkBlock; by <= y1 / InkBlock && x0 <= x1; by++)
                for (int bx = x0 / InkBlock; bx <= x1 / InkBlock; bx++) dirty[by * blocks + bx] = true;
        }

        // Marks the blocks a sea wash is laid over (the pixels InkWithMips' sea pass visits).
        private static void MarkWash(bool[] dirty, SeaWash wash)
        {
            int n = InkPainter.CanvasSize;
            int x0 = Math.Max(0, (int)wash.Left), x1 = Math.Min(n - 1, (int)Math.Ceiling(wash.Right));
            int y0 = Math.Max(0, (int)wash.Top), y1 = Math.Min(n - 1, (int)Math.Ceiling(wash.Bottom));
            MarkBlocks(dirty, x0, n - 1 - y1, x1, n - 1 - y0);
        }

        // A sea illustration's wash as last laid it, or null when it showed no such mark.
        private static SeaWash KeptWash(LaidInk last, SeaMark mark)
        {
            if (last?.m_washMarks == null) return null;
            int k = IndexOf(last.m_washMarks, mark);
            return k < 0 ? null : last.m_washes[k];
        }

        private static int IndexOf(List<SeaMark> marks, SeaMark mark)
        {
            for (int k = 0; k < marks.Count; k++)
            {
                SeaMark m = marks[k];
                if (m.m_west == mark.m_west && m.m_east == mark.m_east && m.m_south == mark.m_south && m.m_north == mark.m_north && m.m_kind == mark.m_kind
                    && m.m_x == mark.m_x && m.m_y == mark.m_y && m.m_scale == mark.m_scale && m.m_mirror == mark.m_mirror) return k;
            }
            return -1;
        }

        // A lay handed back once uploaded, so the next lay fills its pixels again instead of
        // allocating about 89 MB and lays again only what changed; null lets a kept one go. The
        // caller must not touch it after.
        public static void ReturnInk(LaidInk laid) => Interlocked.Exchange(ref s_spareInk, laid);

        // A lay handed back late (once the disk cache has written it), kept only if no later lay
        // was handed back meanwhile. Any thread.
        public static void ReturnInkIfNone(LaidInk laid) => Interlocked.CompareExchange(ref s_spareInk, laid, null);

        private static LaidInk s_spareInk;

        // Runs body(from, to) over a mip level's rows: in bands on the workers for the large
        // levels, at once for the small ones, where a parallel loop costs more than it saves.
        private static void MipRows(int size, Action<int, int> body)
        {
            if (size < 256) body(0, size);
            else Bands(size, Math.Max(8, size / 64), body);
        }

        // The ink's blocks (InkBlock sheet pixels square, rows from the south) lying wholly under
        // opaque fog (opacity, FogEdge.m_edge), so nothing of them shows; null without a fog.
        private static bool[] FoggedBlocks(byte[] opacity)
        {
            if (opacity == null) return null;
            int blocks = InkPainter.CanvasSize / InkBlock;
            var fogged = new bool[blocks * blocks];
            Bands(blocks, 8, (from, to) =>
            {
                for (int by = from; by < to; by++)
                    for (int bx = 0; bx < blocks; bx++)
                        fogged[by * blocks + bx] = Opaque(opacity, bx * InkBlock, by * InkBlock, (bx + 1) * InkBlock - 1, (by + 1) * InkBlock - 1);
            });
            return fogged;
        }

        // Whether the fog's opacity is 255 wherever the sheet pixels x0 to x1, y0 to y1 (rows from
        // the south) can sample it: the fog pixels under them and one round, since a sheet pixel's
        // bilinear sample reaches into the fog pixel beside its own.
        private static bool Opaque(byte[] opacity, int x0, int y0, int x1, int y1)
        {
            int ratio = InkPainter.CanvasSize / FogSize, last = FogSize - 1;
            int fx0 = Math.Max(0, x0 / ratio - 1), fx1 = Math.Min(last, x1 / ratio + 1);
            int fy0 = Math.Max(0, y0 / ratio - 1), fy1 = Math.Min(last, y1 / ratio + 1);
            for (int y = fy0; y <= fy1; y++)
                for (int x = fx0; x <= fx1; x++)
                    if (opacity[y * FogSize + x] != 255) return false;
            return true;
        }

        // Whether all a hidden mark would clear (Clear's pixels) lies under opaque fog, where
        // clearing it changes nothing that shows.
        private static bool ClearedUnderFog(byte[] opacity, FogMark mark)
        {
            int n = InkPainter.CanvasSize;
            Vector4 b = mark.m_box;
            int x0 = Math.Max(0, (int)(b.x - HiddenGrow) - 1), x1 = Math.Min(n - 1, (int)(b.z + HiddenGrow) + 1);
            int y0 = Math.Max(0, (int)(b.y - HiddenGrow) - 1), y1 = Math.Min(n - 1, (int)(b.w + HiddenGrow) + 1);
            return Opaque(opacity, x0, n - 1 - y1, x1, n - 1 - y0);
        }

        // Fills the alpha of an RGBA fog buffer whose colours are the parchment: opaque where the
        // snapshot shows nothing explored, clear where it does, and between them the band where
        // the drawing thins out and stops. The band ends GlimpseBand metres out from the explored
        // land and round each of glimpses (world x, z: the game's marks out in the unexplored),
        // and glimpse (RGBA, the parchment's colours) gets that band, explored land excepted, as
        // opacity: laid over the paper's wash and under the ink, it shows the map there on bare,
        // untinted parchment, a glimpse past what was explored. marks are the sheet's sea
        // illustrations (null when not known): the FogEdge leaves out every one not lying wholly
        // in explored land (SeaMarkSeen), which InkWithMips and close-ups apply. sheetMarks are the
        // sheet's trees, glyphs and wave strokes (null for none): the returned FogEdge says
        // which are left out, which InkWithMips clears from the sheet's ink and close-ups leave
        // out, and relief the band's end would cut is shown whole by lifting the fog over it.
        // With glimpseBand false (AtlasPlugin.Glimpse off) the band's end lies on the explored
        // land's own edge, glimpses are ignored, and the glimpse only lies under the fog, so no
        // bare parchment or greyer ink shows past it.
        public static FogEdge Fog(byte[] rgba, byte[] glimpse, byte[] parchment, Snapshot snapshot, IList<SeaMark> marks, IList<Vector2> glimpses,
            SheetMarks sheetMarks = null, bool glimpseBand = true)
        {
            int n = FogSize;
            float metres = 2f * InkPainter.HalfExtent / n;
            var cover = new float[n * n];
            var inside = new bool[n * n];
            float edge = InkPainter.EdgeRadius - FrameBand;
            // Only fog pixels whose samples can reach the explored snapshot pixels read the
            // snapshot; every other is wholly unexplored.
            ExploredFogBounds(snapshot, n, metres, out int ex0, out int ey0, out int ex1, out int ey1);
            Bands(n, FogRows, (from, to) =>
            {
                for (int y = from; y < to; y++)
                {
                    for (int x = 0; x < n; x++)
                    {
                        float wx = (x + 0.5f) * metres - InkPainter.HalfExtent;
                        float wz = (y + 0.5f) * metres - InkPainter.HalfExtent;
                        if (wx * wx + wz * wz > edge * edge) continue;
                        inside[y * n + x] = true;
                        if (x < ex0 || x > ex1 || y < ey0 || y > ey1)
                        {
                            cover[y * n + x] = 1f;
                            continue;
                        }
                        int seen = 0;
                        for (int sy = -1; sy <= 1; sy++)
                            for (int sx = -1; sx <= 1; sx++)
                                if (snapshot.IsExplored(wx + sx * metres / 3f, wz + sy * metres / 3f)) seen++;
                        cover[y * n + x] = 1f - seen / 9f;
                    }
                }
            });
            // The glimpse: the explored land's own edge, softened, is where the bare parchment
            // starts; the band reaches GlimpseBand further out, and round every glimpse.
            var unexplored = (float[])cover.Clone();
            BlurParallel(unexplored, n);
            BlurParallel(unexplored, n);
            float[] depth = BandDepth(cover, inside, n, metres, glimpseBand ? glimpses : null, glimpseBand);
            BlurParallel(depth, n);
            BlurParallel(depth, n);
            float band = GlimpseBand / metres;
            // Past this depth no wobble of the fBm brings the band's end back (its noise is 0 to 1,
            // so it moves the edge at most EdgeWobble pixels in): the fog is opaque and every mark
            // thinned out, without working out the noise (about 1.27 of the band).
            float full = 1f + EdgeFade * 0.5f + EdgeWobble / band + FullMargin;
            var opacity = new byte[n * n];
            var thin = new byte[n * n];
            // The glimpse's bare parchment: wherever the land is not explored (softened at the
            // explored edge); the fog lies over all of it but the band.
            var open = new byte[n * n];
            Bands(n, FogRows, (from, to) =>
            {
                for (int i = from * n; i < to * n; i++)
                {
                    float a = 0f;
                    if (inside[i])
                    {
                        if (depth[i] >= full && cover[i] > 0f)
                        {
                            a = 1f;
                            thin[i] = 255;
                        }
                        else
                        {
                            int x = i % n, y = i / n;
                            float t = depth[i] + (Fbm(x / EdgeGrain, y / EdgeGrain, EdgeSeed) - 0.5f) * 2f * EdgeWobble / band;
                            a = Smooth(1f - EdgeFade, 1f + EdgeFade * 0.5f, t);
                            // Explored land is never covered, wherever the wobble takes the edge.
                            if (cover[i] <= 0f) a = 0f;
                            thin[i] = ToByte(Smooth(ThinFrom, 1f, t) * 255f);
                        }
                    }
                    opacity[i] = ToByte(a * 255f);
                    open[i] = inside[i] ? ToByte(unexplored[i] * 255f) : (byte)0;
                }
            });
            return FogOfBand(rgba, glimpse, parchment, new FogBase { m_opacity = opacity, m_thin = thin, m_unexplored = open, m_snapshot = snapshot },
                marks, sheetMarks, glimpseBand);
        }

        // Worker thread. The fog Fog lays from its band (laid: opacity, thinning and the glimpse's
        // opacity, a new FogBase with nothing else set but the snapshot), byte for byte: the
        // buffers' colours from the parchment, then the marks judged. The disk cache keeps only
        // the band (LaidCache), so a band read from it lays the same fog without working it out.
        public static FogEdge FogOfBand(byte[] rgba, byte[] glimpse, byte[] parchment, FogBase laid, IList<SeaMark> marks, SheetMarks sheetMarks,
            bool glimpseBand)
        {
            int n = FogSize;
            byte[] opacity = laid.m_opacity, open = laid.m_unexplored;
            byte[] beyond = glimpse ?? new byte[n * n * 4];
            Bands(n, FogRows, (from, to) =>
            {
                for (int i = from * n; i < to * n; i++)
                {
                    int o = i * 4, p = i * 3;
                    rgba[o] = parchment[p];
                    rgba[o + 1] = parchment[p + 1];
                    rgba[o + 2] = parchment[p + 2];
                    rgba[o + 3] = opacity[i];
                    beyond[o] = parchment[p];
                    beyond[o + 1] = parchment[p + 1];
                    beyond[o + 2] = parchment[p + 2];
                    beyond[o + 3] = open[i];
                }
            });
            // Kept for Redecide; without the glimpse its alpha is overwritten below, so the marks
            // are judged against a copy of it.
            laid.m_beyond = glimpseBand ? null : (byte[])beyond.Clone();
            FogEdge result = Judge(laid, rgba, marks, sheetMarks, beyond);
            // Without the glimpse it takes the band's opacity: wherever it would show, the fog
            // lies over it. Not the fog's own once relief is lifted out of it: through the lift
            // the unexplored ground's wash would show round the peak.
            if (!glimpseBand)
                for (int i = 0; i < n * n; i++) beyond[i * 4 + 3] = opacity[i];
            return result;
        }

        // Worker thread. The fog last laid (its FogEdge, from Fog or Redecide) judged again for
        // other sheet marks and sea illustrations, exactly as Fog would lay it for them with the
        // same snapshot, glimpses and Glimpse setting: the band itself does not depend on the
        // sheet, only which marks it leaves out and the relief lifted out of it (Decide). rgba is
        // the fog buffer Fog laid (its colour is kept, its alpha laid again with the new lifts);
        // glimpse the glimpse it laid (read only). About as cheap as Decide; Fog spends its time
        // on the band (the snapshot's samples, the distance field, the blurs and the noise).
        public static FogEdge Redecide(FogEdge last, byte[] rgba, byte[] glimpse, IList<SeaMark> marks, SheetMarks sheetMarks)
        {
            FogBase laid = last.m_base;
            int n = FogSize;
            Bands(n, FogRows, (from, to) =>
            {
                for (int i = from * n; i < to * n; i++) rgba[i * 4 + 3] = laid.m_opacity[i];
            });
            return Judge(laid, rgba, marks, sheetMarks, laid.m_beyond ?? glimpse);
        }

        // The band's decisions on the sea illustrations and the sheet's marks (lifting relief out
        // of rgba's alpha), against beyond, the glimpse as the band laid it.
        private static FogEdge Judge(FogBase laid, byte[] rgba, IList<SeaMark> marks, SheetMarks sheetMarks, byte[] beyond)
        {
            var result = new FogEdge { m_marks = sheetMarks, m_edge = laid.m_opacity, m_thin = laid.m_thin, m_base = laid };
            if (marks != null)
                foreach (SeaMark mark in marks)
                    if (laid.m_snapshot == null || !SeaMarkSeen(laid.m_snapshot, mark)) result.HideSea(mark);
            if (sheetMarks != null) Decide(result, rgba, beyond);
            return result;
        }

        // How deep each fog pixel lies in the band: 0 on the explored land, 1 at the band's end
        // (GlimpseBand out from the explored pixels and from glimpses, the game's marks out in the
        // unexplored). With glimpseBand false the band's end lies on the explored land's own edge:
        // the fade starts on the explored pixels and the fog closes about 30 m past them.
        private static float[] BandDepth(float[] cover, bool[] inside, int n, float metres, IList<Vector2> glimpses, bool glimpseBand)
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
            new DistanceField(n).ComputeParallel(i => seeds[i], distance, GlimpseSteps, CancellationToken.None);
            float band = GlimpseBand / metres;
            var depth = new float[n * n];
            for (int i = 0; i < n * n; i++)
            {
                if (!inside[i]) continue;
                float d = distance[i] / GlimpseSteps;
                depth[i] = glimpseBand ? d / band : 1f - EdgeFade + d / band;
            }
            return depth;
        }

        // The fog pixels (inclusive, rows from the south) whose snapshot samples (Fog's, a third
        // of a pixel either side) can reach an explored snapshot pixel, two pixels wider for
        // rounding; none (x0 > x1) without a snapshot or anything explored.
        private static void ExploredFogBounds(Snapshot snapshot, int n, float metres, out int x0, out int y0, out int x1, out int y1)
        {
            x0 = y0 = n;
            x1 = y1 = -1;
            if (snapshot?.m_explored == null) return;
            int size = snapshot.m_size, count = Math.Min(snapshot.m_explored.Length, size * size);
            var words = new int[(snapshot.m_explored.Length + 31) / 32];
            snapshot.m_explored.CopyTo(words, 0);
            int sx0 = size, sz0 = size, sx1 = -1, sz1 = -1;
            for (int w = 0; w < words.Length; w++)
            {
                if (words[w] == 0) continue;
                for (int bit = 0; bit < 32; bit++)
                {
                    int i = w * 32 + bit;
                    if (i >= count) break;
                    if ((words[w] & (1 << bit)) == 0) continue;
                    int sx = i % size, sz = i / size;
                    sx0 = Math.Min(sx0, sx);
                    sx1 = Math.Max(sx1, sx);
                    sz0 = Math.Min(sz0, sz);
                    sz1 = Math.Max(sz1, sz);
                }
            }
            if (sx1 < 0) return;
            // Snapshot pixel s covers world (s - size / 2) to (s + 1 - size / 2) pixel sizes.
            float ps = snapshot.m_pixelSize, reach = 1f / 3f;
            x0 = Math.Max(0, (int)Math.Floor(((sx0 - size / 2) * ps + InkPainter.HalfExtent) / metres - 0.5f - reach) - 2);
            x1 = Math.Min(n - 1, (int)Math.Ceiling(((sx1 + 1 - size / 2) * ps + InkPainter.HalfExtent) / metres - 0.5f + reach) + 2);
            y0 = Math.Max(0, (int)Math.Floor(((sz0 - size / 2) * ps + InkPainter.HalfExtent) / metres - 0.5f - reach) - 2);
            y1 = Math.Min(n - 1, (int)Math.Ceiling(((sz1 + 1 - size / 2) * ps + InkPainter.HalfExtent) / metres - 0.5f + reach) + 2);
        }

        // Runs body(from, to) over 0 to count in bands of band, in parallel on the workers.
        private static void Bands(int count, int band, Action<int, int> body) =>
            Parallel.For(0, (count + band - 1) / band, Workers.LoopOptions(), b => body(b * band, Math.Min(count, (b + 1) * band)));

        // Blur on the workers, byte for byte the serial one: rows in bands, then columns in bands,
        // each column band keeping the row above as it was before the column pass.
        private static void BlurParallel(float[] values, int n)
        {
            Bands(n, FogRows, (from, to) =>
            {
                var row = new float[n];
                for (int y = from; y < to; y++)
                {
                    for (int x = 0; x < n; x++)
                        row[x] = (values[y * n + Math.Max(0, x - 1)] + values[y * n + x] + values[y * n + Math.Min(n - 1, x + 1)]) / 3f;
                    Array.Copy(row, 0, values, y * n, n);
                }
            });
            Bands(n, FogRows, (from, to) =>
            {
                int w = to - from;
                var above = new float[w];
                var here = new float[w];
                Array.Copy(values, from, above, 0, w);
                for (int y = 0; y < n; y++)
                {
                    Array.Copy(values, y * n + from, here, 0, w);
                    int below = Math.Min(n - 1, y + 1) * n + from;
                    for (int k = 0; k < w; k++)
                        values[y * n + from + k] = (above[k] + here[k] + values[below + k]) / 3f;
                    float[] swap = above;
                    above = here;
                    here = swap;
                }
            });
        }

        // Which of the sheet's marks the band leaves out (FogEdge), against the glimpse (beyond:
        // where the land is not explored). Relief the band's end would cut is never thinned
        // (holes in a range read as damage) but drawn whole when its foot stands on the drawn
        // side or it reaches back into the explored land, as a mountain seen from afar is
        // sketched whole, the fog lifted over it; otherwise left out whole. Then trees, dead
        // trees and wave strokes standing beyond the explored land are left out when the fog as
        // laid, lifts included, would cut them or the thinning takes them (CutByFog): judged
        // before the lifts, the trees in front of a lifted peak showed in its opening.
        private static void Decide(FogEdge edge, byte[] fog, byte[] beyond)
        {
            SheetMarks marks = edge.m_marks;
            float canvas = InkPainter.CanvasSize;
            edge.m_trees = new bool[marks.m_trees.Count];
            edge.m_glyphs = new bool[marks.m_glyphs.Count];
            edge.m_waves = new bool[marks.m_waves.Count];
            var lifted = new List<FogMark>();
            for (int i = 0; i < marks.m_glyphs.Count; i++)
            {
                FogMark mark = marks.m_glyphs[i];
                if (mark.m_thinned || !SpansFog(edge.m_edge, mark.m_box)) continue;
                bool standsClear = PlaneAt(edge.m_edge, (mark.m_box.x + mark.m_box.z) * 0.5f, canvas - mark.m_box.w) < 0.5f;
                if (!standsClear && MostlyBeyond(beyond, mark.m_box)) edge.m_glyphs[i] = true;
                else lifted.Add(mark);
            }
            if (lifted.Count > 0)
            {
                foreach (FogMark mark in lifted) LiftFog(fog, mark);
                int n = FogSize;
                var laid = new byte[n * n];
                for (int i = 0; i < n * n; i++) laid[i] = fog[i * 4 + 3];
                edge.m_edge = laid;
            }
            for (int i = 0; i < marks.m_trees.Count; i++) edge.m_trees[i] = CutByFog(edge, beyond, marks.m_trees[i].m_box);
            for (int i = 0; i < marks.m_waves.Count; i++) edge.m_waves[i] = CutByFog(edge, beyond, marks.m_waves[i].m_box);
            for (int i = 0; i < marks.m_glyphs.Count; i++)
                if (marks.m_glyphs[i].m_thinned) edge.m_glyphs[i] = CutByFog(edge, beyond, marks.m_glyphs[i].m_box);
        }

        // Whether a footprint (left, top, right, bottom in sheet pixels) standing beyond the
        // explored land (glimpse, Fog's) is left out by the band (edge): the thinning takes it
        // (a hash of its foot against FogEdge.m_thin), or the band's end covers part of it but
        // not all. Close-ups judge their own trees by it (InkPainter.RunTile).
        public static bool CutByFog(FogEdge edge, byte[] glimpse, Vector4 box)
        {
            float canvas = InkPainter.CanvasSize;
            float footX = (box.x + box.z) * 0.5f;
            if (GlimpseAt(glimpse, footX, canvas - box.w) < 0.5f) return false;
            if (Noise.Hash(Mathf.FloorToInt(footX), Mathf.FloorToInt(box.w), ThinSalt) < PlaneAt(edge.m_thin, footX, canvas - box.w)) return true;
            return SpansFog(edge.m_edge, box);
        }

        // A mark is cut where the band's opacity over its footprint spans these.
        private const float CutLeast = 0.1f;
        private const float CutMost = 0.9f;

        // Whether the band's opacity (a FogSize plane) covers part of a footprint but not all.
        private static bool SpansFog(byte[] opacity, Vector4 box)
        {
            float canvas = InkPainter.CanvasSize;
            float least = 1f, most = 0f;
            const int Samples = 4;
            for (int sy = 0; sy < Samples; sy++)
            {
                float y = box.y + (box.w - box.y) * (sy + 0.5f) / Samples;
                for (int sx = 0; sx < Samples; sx++)
                {
                    float x = box.x + (box.z - box.x) * (sx + 0.5f) / Samples;
                    float a = PlaneAt(opacity, x, canvas - y);
                    least = Math.Min(least, a);
                    most = Math.Max(most, a);
                }
            }
            return most > CutLeast && least < CutMost;
        }

        // Whether most of a footprint lies beyond the explored land: relief reaching back into it
        // is drawn whole, since leaving out only its unexplored part would cut it in two.
        private static bool MostlyBeyond(byte[] glimpse, Vector4 box)
        {
            float canvas = InkPainter.CanvasSize;
            int beyond = 0;
            for (int sy = 0; sy < 3; sy++)
            {
                for (int sx = 0; sx < 3; sx++)
                {
                    float x = box.x + (box.z - box.x) * (sx + 0.5f) / 3f, y = box.y + (box.w - box.y) * (sy + 0.5f) / 3f;
                    if (GlimpseAt(glimpse, x, canvas - y) >= 0.5f) beyond++;
                }
            }
            return beyond >= 8;
        }

        // A FogSize plane (rows from the south) at a sheet point, bilinear, 0 to 1.
        private static float PlaneAt(byte[] plane, float x, float y)
        {
            float scale = FogSize / (float)InkPainter.CanvasSize;
            x = Mathf.Clamp(x * scale - 0.5f, 0f, FogSize - 1.001f);
            y = Mathf.Clamp(y * scale - 0.5f, 0f, FogSize - 1.001f);
            int x0 = (int)x, y0 = (int)y;
            float fx = x - x0, fy = y - y0;
            int i = y0 * FogSize + x0;
            float bottom = plane[i] + (plane[i + 1] - plane[i]) * fx;
            float top = plane[i + FogSize] + (plane[i + FogSize + 1] - plane[i + FogSize]) * fx;
            return (bottom + (top - bottom) * fy) / 255f;
        }

        // Opens the fog (RGBA) over a relief mark's shape, so it shows whole across the band's
        // end. At the fog's 10.5 m pixels the opening is soft by a pixel.
        private static void LiftFog(byte[] fog, FogMark mark)
        {
            int n = FogSize;
            float scale = n / (float)InkPainter.CanvasSize, canvas = InkPainter.CanvasSize;
            Vector4 box = mark.m_box;
            int x0 = Math.Max(0, (int)(box.x * scale) - 1), x1 = Math.Min(n - 1, (int)(box.z * scale) + 1);
            int y0 = Math.Max(0, (int)((canvas - box.w) * scale) - 1), y1 = Math.Min(n - 1, (int)((canvas - box.y) * scale) + 1);
            for (int y = y0; y <= y1; y++)
            {
                float sy = canvas - (y + 0.5f) / scale;
                for (int x = x0; x <= x1; x++)
                {
                    // The shape's depth in sheet pixels, two to a fog pixel.
                    float inside = ShapeDepth(mark, (x + 0.5f) / scale, sy, 1f);
                    float open = Mathf.Clamp01(inside * scale + 0.5f);
                    if (open <= 0f) continue;
                    int q = (y * n + x) * 4 + 3;
                    fog[q] = ToByte(fog[q] * (1f - open));
                }
            }
        }

        // How far a sheet point (x from the west, y from the north) lies inside a mark's shape
        // (MarkShape) grown by grow, in sheet pixels; negative outside.
        public static float ShapeDepth(FogMark mark, float x, float y, float grow)
        {
            Vector4 b = mark.m_box;
            float left = b.x - grow, right = b.z + grow, top = b.y - grow, bottom = b.w + grow;
            float inside = Math.Min(Math.Min(x - left, right - x), Math.Min(y - top, bottom - y));
            float cx = (left + right) * 0.5f, rx = Math.Max(0.5f, (right - left) * 0.5f), height = Math.Max(0.5f, bottom - top);
            switch (mark.m_shape)
            {
                case MarkShape.Crown:
                {
                    float ry = height * 0.5f, ex = (x - cx) / rx, ey = (y - (top + ry)) / ry;
                    return (1f - Mathf.Sqrt(ex * ex + ey * ey)) * Math.Min(rx, ry);
                }
                case MarkShape.Spire:
                {
                    // Under both flanks, the apex at the top middle.
                    float half = rx * (y - top) / height;
                    return Math.Min(inside, (half - Math.Abs(x - cx)) * height / Mathf.Sqrt(height * height + rx * rx));
                }
                case MarkShape.Dome:
                {
                    float ex = (x - cx) / rx, ey = (bottom - y) / height;
                    return Math.Min(inside, (1f - Mathf.Sqrt(ex * ex + ey * ey)) * Math.Min(rx, height));
                }
                default:
                    return inside;
            }
        }

        // A hidden mark's shape is grown by HiddenGrow sheet pixels, so its outline's pen goes
        // too; the kept marks near hidden ones are filed in HiddenCell squares.
        private const float HiddenGrow = 0.75f;
        private const int HiddenCell = 32;

        // Clears from the sheet's ink what the band leaves out (FogEdge): each such mark's shape,
        // at sheet resolution, but not where a mark kept and drawn after it (south of it) lies
        // over it, so the trees in front of a hidden one keep their crowns. A mark behind it
        // stays without the part the hidden one scraped away when the sheet was painted, which
        // only repainting could restore; close-ups leave the mark out exactly. Marks wholly under
        // opaque fog are not cleared: InkWithMips leaves those blocks clear. Returns coverage
        // (canvas order, rows from the north) or null when nothing is left out.
        private static byte[] HiddenInk(FogEdge edge)
        {
            if (edge?.m_marks == null || edge.m_trees == null) return null;
            SheetMarks marks = edge.m_marks;
            byte[] opacity = edge.m_edge;
            var hidden = new List<FogMark>();
            var kept = new List<FogMark>();
            var waves = new List<FogMark>();
            for (int i = 0; i < marks.m_trees.Count; i++)
            {
                if (!edge.m_trees[i]) kept.Add(marks.m_trees[i]);
                else if (opacity == null || !ClearedUnderFog(opacity, marks.m_trees[i])) hidden.Add(marks.m_trees[i]);
            }
            for (int i = 0; i < marks.m_glyphs.Count; i++)
            {
                if (!edge.m_glyphs[i]) kept.Add(marks.m_glyphs[i]);
                else if (opacity == null || !ClearedUnderFog(opacity, marks.m_glyphs[i])) hidden.Add(marks.m_glyphs[i]);
            }
            for (int i = 0; i < marks.m_waves.Count; i++)
                if (edge.m_waves[i] && (opacity == null || !ClearedUnderFog(opacity, marks.m_waves[i]))) waves.Add(marks.m_waves[i]);
            if (hidden.Count == 0 && waves.Count == 0) return null;
            int n = InkPainter.CanvasSize, cells = n / HiddenCell;
            // The kept marks near the hidden ones, filed by the cells their footprints touch.
            var filed = new Dictionary<int, List<int>>();
            var near = new HashSet<int>();
            foreach (FogMark mark in hidden) Touch(mark.m_box, cells, cell => near.Add(cell));
            for (int k = 0; k < kept.Count; k++)
            {
                int index = k;
                Touch(kept[k].m_box, cells, cell =>
                {
                    if (!near.Contains(cell)) return;
                    if (!filed.TryGetValue(cell, out List<int> list)) filed[cell] = list = new List<int>();
                    list.Add(index);
                });
            }
            var mask = new byte[n * n];
            var front = new List<FogMark>();
            var seen = new HashSet<int>();
            foreach (FogMark mark in hidden)
            {
                Vector4 b = mark.m_box;
                front.Clear();
                seen.Clear();
                Touch(b, cells, cell =>
                {
                    if (!filed.TryGetValue(cell, out List<int> list)) return;
                    foreach (int k in list)
                    {
                        FogMark other = kept[k];
                        if (other.m_order > mark.m_order && seen.Add(k) && Overlaps(other.m_box, b)) front.Add(other);
                    }
                });
                Clear(mask, mark, front);
            }
            // A wave stroke lies in open water, drawn after every glyph: nothing kept lies on it.
            front.Clear();
            foreach (FogMark mark in waves) Clear(mask, mark, front);
            return mask;
        }

        // Adds the sea illustrations the fog leaves out (FogEdge.m_hiddenSea) to the cleared ink
        // (mask, HiddenInk's, or null), their whole box and a pixel round it: nothing else is
        // inked within InkPainter.IllustrationInkGap of one, so the box clears that glyph alone.
        private static byte[] HiddenSeaInk(byte[] mask, FogEdge edge)
        {
            if (edge?.m_hiddenSea == null) return mask;
            int n = InkPainter.CanvasSize;
            if (mask == null) mask = new byte[n * n];
            foreach (Vector4 b in edge.m_hiddenSea)
            {
                int x0 = Math.Max(0, (int)b.x - 1), x1 = Math.Min(n - 1, (int)Math.Ceiling(b.z) + 1);
                int y0 = Math.Max(0, (int)b.y - 1), y1 = Math.Min(n - 1, (int)Math.Ceiling(b.w) + 1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++) mask[y * n + x] = 255;
            }
            return mask;
        }

        // Clears a hidden mark's shape in the mask (canvas order), less what the marks in front
        // of it cover.
        private static void Clear(byte[] mask, FogMark mark, List<FogMark> front)
        {
            int n = InkPainter.CanvasSize;
            Vector4 b = mark.m_box;
            int x0 = Math.Max(0, (int)(b.x - HiddenGrow) - 1), x1 = Math.Min(n - 1, (int)(b.z + HiddenGrow) + 1);
            int y0 = Math.Max(0, (int)(b.y - HiddenGrow) - 1), y1 = Math.Min(n - 1, (int)(b.w + HiddenGrow) + 1);
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float clear = Mathf.Clamp01(ShapeDepth(mark, px, py, HiddenGrow) + 0.5f);
                    if (clear <= 0f) continue;
                    foreach (FogMark other in front)
                    {
                        clear *= 1f - Mathf.Clamp01(ShapeDepth(other, px, py, 0f) + 0.5f);
                        if (clear <= 0f) break;
                    }
                    byte value = ToByte(clear * 255f);
                    int i = y * n + x;
                    if (value > mask[i]) mask[i] = value;
                }
            }
        }

        private static bool Overlaps(Vector4 a, Vector4 b) => a.x < b.z && b.x < a.z && a.y < b.w && b.y < a.w;

        // Calls visit with every HiddenCell cell a footprint touches.
        private static void Touch(Vector4 box, int cells, Action<int> visit)
        {
            int x0 = Mathf.Clamp((int)(box.x / HiddenCell), 0, cells - 1), x1 = Mathf.Clamp((int)(box.z / HiddenCell), 0, cells - 1);
            int y0 = Mathf.Clamp((int)(box.y / HiddenCell), 0, cells - 1), y1 = Mathf.Clamp((int)(box.w / HiddenCell), 0, cells - 1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) visit(y * cells + x);
        }

        private static float Smooth(float from, float to, float value)
        {
            float t = Mathf.Clamp01((value - from) / (to - from));
            return t * t * (3f - 2f * t);
        }

        // Whether a sea illustration lies wholly in the explored land's colour: its box and
        // SeaMarkMargin round it all explored. Only then is it drawn: one shown across the band
        // stood half on parchment or under the fog, and one partly explored but hidden laid the
        // fog over explored sea. Its box lies far out in open water, so leaving it out leaves
        // nothing but water.
        private static bool SeaMarkSeen(Snapshot snapshot, SeaMark mark)
        {
            float west = mark.m_west - SeaMarkMargin, east = mark.m_east + SeaMarkMargin;
            float south = mark.m_south - SeaMarkMargin, north = mark.m_north + SeaMarkMargin;
            for (float wz = south; wz <= north + SeaMarkStep * 0.5f; wz += SeaMarkStep)
            {
                float z = Math.Min(wz, north);
                float dz = Math.Max(0f, Math.Max(mark.m_south - z, z - mark.m_north));
                for (float wx = west; wx <= east + SeaMarkStep * 0.5f; wx += SeaMarkStep)
                {
                    float x = Math.Min(wx, east);
                    float dx = Math.Max(0f, Math.Max(mark.m_west - x, x - mark.m_east));
                    if (dx * dx + dz * dz > SeaMarkMargin * SeaMarkMargin) continue;
                    if (!snapshot.IsExplored(x, z)) return false;
                }
            }
            return true;
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
