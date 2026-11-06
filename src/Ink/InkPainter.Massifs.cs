using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The Mountains drawn as the game builds them: most mountain land is one broad massif whose
    /// rough heights carry a bump every hundred metres or so, so a peak on every local summit
    /// broke one mountain into a field of them. Each massif (connected Mountain ground over
    /// PeakHeight) gets one dominant peak on its highest point, sized to the massif; a further
    /// peak only where a summit stands out by real topographic prominence (its height over the
    /// saddle joining it to higher ground) and lies well away from the peaks already placed; the
    /// rest of its high ground carries ridges (Kind.Ridge), stretches of wall with no summit of
    /// their own, so the massif reads as one mass without a peak competing with its top. Placed
    /// on the sheet alone from the world's heights; close-ups replay the sheet's glyphs.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // A massif's dominant peak: MassifPeakShare of the massif's span (the side of a square of
        // its area), between MassifPeakMin and MassifPeakMax G wide. Its seed is drawn again
        // until its shape is at least MassifPeakTall of the tallest a peak can be for its width,
        // so the top of a massif never comes out broad and low beside the ridges.
        private const float MassifPeakShare = 0.6f;
        private const float MassifPeakMin = 16f;
        private const float MassifPeakMax = 56f;
        private const float MassifPeakTall = 0.5f;
        // A summit other than the top gets a peak when it rises SecondProminence metres over the
        // saddle joining it to higher ground and stands SecondIsolation metres from every peak
        // already placed on its massif; it is 22 to 34 G wide by prominence (up to
        // SecondProminenceFull over the threshold) and never over SecondShare of the top's width.
        private const float SecondProminence = 35f;
        private const float SecondProminenceFull = 100f;
        private const float SecondIsolation = 400f;
        private const float SecondShare = 0.75f;
        // Ridges: staggered rows RidgeStepX by RidgeStepY G over the massifs' high ground,
        // RidgeOdds of the cells taken, RidgeMin to RidgeMax G wide by height over PeakHeight (up to
        // RidgeHeightFull metres more), never over RidgeShare of the massif's top.
        private const float RidgeStepX = 30f;
        private const float RidgeStepY = 12f;
        private const float RidgeOdds = 0.9f;
        private const float RidgeMin = 26f;
        private const float RidgeMax = 40f;
        private const float RidgeHeightFull = 160f;
        private const float RidgeShare = 0.85f;
        private const int RidgeSalt = 65;
        // A ridge in front of a summit's peak may hide at most its lower RidgeShown.
        private const float RidgeShown = 0.3f;
        // A ridge is moved this share of the way from its grid spot towards its massif's middle.
        private const float RidgePull = 0.12f;

        // Each map pixel's massif (index into m_massifPeaks), -1 off the Mountains' high ground.
        private int[] m_massifOf;
        // Each massif's top's peak width, for the ridges round it.
        private readonly List<float> m_massifPeaks = new List<float>();

        private void PlaceMassifs()
        {
            int size = m_world.m_size;
            float[] h = m_world.m_height;
            float floor = m_world.m_waterLevel + PeakHeight;
            // The massifs are grouped over all the Mountains' land, not only its high ground: the
            // game's mountain is one rise whose rough top dips under PeakHeight here and there,
            // and grouped by the high ground alone it fell apart into many small massifs, each
            // with a dominant peak of its own.
            var high = new List<int>();
            for (int i = 0; i < h.Length; i++)
                if (m_world.m_biome[i] == Land.Mountain && h[i] > m_world.m_waterLevel) high.Add(i);
            if (high.Count == 0) return;
            // Highest first, ties by index, so the result never depends on the sort's order.
            high.Sort((a, b) => h[a] != h[b] ? h[b].CompareTo(h[a]) : a.CompareTo(b));

            // Union-find, highest first: a pixel joins the ground already visited round it; where
            // it joins two groups, it is the saddle between them, and the lower group's top has
            // that much prominence. Each root keeps its group's top.
            var parent = new Dictionary<int, int>(high.Count);
            var top = new Dictionary<int, int>(high.Count);
            var count = new Dictionary<int, int>(high.Count);
            var prominence = new Dictionary<int, float>();
            int Find(int i)
            {
                int root = i;
                while (parent[root] != root) root = parent[root];
                while (parent[i] != root)
                {
                    int next = parent[i];
                    parent[i] = root;
                    i = next;
                }
                return root;
            }
            foreach (int i in high)
            {
                parent[i] = i;
                top[i] = i;
                count[i] = 1;
                int x = i % size, z = i / size;
                for (int dz = -1; dz <= 1; dz++)
                {
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if ((dx == 0 && dz == 0) || x + dx < 0 || x + dx >= size || z + dz < 0 || z + dz >= size) continue;
                        int n = (z + dz) * size + x + dx;
                        if (!parent.ContainsKey(n)) continue;
                        int a = Find(i), b = Find(n);
                        if (a == b) continue;
                        int ta = top[a], tb = top[b];
                        bool aHigher = h[ta] > h[tb] || (h[ta] == h[tb] && ta < tb);
                        int lower = aHigher ? tb : ta;
                        if (lower != i) prominence[lower] = h[lower] - h[i];
                        int keep = aHigher ? a : b, join = aHigher ? b : a;
                        parent[join] = keep;
                        count[keep] += count[join];
                    }
                }
            }

            // The massifs: each root, its top and its area.
            m_massifOf = new int[h.Length];
            for (int i = 0; i < m_massifOf.Length; i++) m_massifOf[i] = -1;
            var roots = new Dictionary<int, int>();
            var tops = new List<int>();
            var areas = new List<int>();
            var sums = new List<Vector2>();
            foreach (int i in high)
            {
                int root = Find(i);
                if (!roots.TryGetValue(root, out int massif))
                {
                    roots[root] = massif = tops.Count;
                    tops.Add(top[root]);
                    areas.Add(count[root]);
                    sums.Add(Vector2.zero);
                    m_massifPeaks.Add(0f);
                }
                m_massifOf[i] = massif;
                sums[massif] += new Vector2(i % size, i / size);
            }
            // Each massif's middle on the canvas, which its ridges lean towards.
            var middles = new List<Vector2>(sums.Count);
            for (int m = 0; m < sums.Count; m++)
            {
                Vector2 cell = sums[m] / areas[m];
                float wx = (cell.x - size / 2 + 0.5f) * m_world.m_pixelSize, wz = (cell.y - size / 2 + 0.5f) * m_world.m_pixelSize;
                middles.Add(new Vector2((wx + HalfExtent) / MetresPerPixel, (HalfExtent - wz) / MetresPerPixel));
            }

            // The summits that stand out, highest first: the tops, then the prominent others.
            var summits = new List<int>();
            foreach (int t in tops)
                if (h[t] >= floor) summits.Add(t);
            foreach (KeyValuePair<int, float> p in prominence)
                if (p.Value >= SecondProminence && h[p.Key] >= floor) summits.Add(p.Key);
            summits.Sort((a, b) => h[a] != h[b] ? h[b].CompareTo(h[a]) : a.CompareTo(b));
            var placed = new List<Vector3>();
            foreach (int i in summits)
            {
                int massif = m_massifOf[i];
                bool isTop = tops[massif] == i;
                int x = i % size, z = i / size;
                float wx = (x - size / 2 + 0.5f) * m_world.m_pixelSize;
                float wz = (z - size / 2 + 0.5f) * m_world.m_pixelSize;
                float cx = (wx + HalfExtent) / MetresPerPixel, cy = (HalfExtent - wz) / MetresPerPixel;
                float dx = cx - CanvasSize * 0.5f, dy = cy - CanvasSize * 0.5f;
                float edge = EdgeRadius / MetresPerPixel * EdgeFraction;
                if (dx * dx + dy * dy > edge * edge || !IsLandAt(cx, cy)) continue;
                float width;
                int seed = HashInt(x, z, m_world.m_seed + 77);
                if (isTop)
                {
                    float span = Mathf.Sqrt(areas[massif]) * m_world.m_pixelSize / MetresPerPixel;
                    width = Mathf.Clamp(span * MassifPeakShare, MassifPeakMin * G, MassifPeakMax * G);
                    for (int k = 1; k < 16 && Unit(seed, 10) < MassifPeakTall; k++) seed = HashInt(x, z, m_world.m_seed + 77 + k);
                }
                else
                {
                    float top0 = m_massifPeaks[massif];
                    if (top0 <= 0f) continue;
                    bool crowded = false;
                    foreach (Vector3 other in placed)
                    {
                        if ((int)other.z != massif) continue;
                        float ox = other.x - wx, oz = other.y - wz;
                        if (ox * ox + oz * oz < SecondIsolation * SecondIsolation) crowded = true;
                    }
                    if (crowded) continue;
                    width = Mathf.Lerp(22f, 34f, Mathf.Clamp01((prominence[i] - SecondProminence) / SecondProminenceFull)) * G;
                    width = Mathf.Min(width, top0 * SecondShare);
                }
                int before = m_glyphs.Count;
                PlaceSummitPeak(cx, cy, width, seed);
                if (m_glyphs.Count == before) continue;
                if (isTop) m_massifPeaks[massif] = m_glyphs[m_glyphs.Count - 1].m_size;
                placed.Add(new Vector3(wx, wz, massif));
            }

            // Ridges over the rest of the massifs' high ground, clear of the summits' peaks.
            Scatter(RidgeStepX * G, RidgeStepY * G, 0.6f, 0.5f, RidgeSalt, s =>
            {
                if (!s.m_isLand || !(s.m_roll < RidgeOdds)) return;
                if (s.m_land != Land.Mountain || s.m_above < PeakHeight) return;
                int massif = m_massifOf[m_world.Index(s.m_worldX, s.m_worldZ)];
                float width = Mathf.Lerp(RidgeMin, RidgeMax, Mathf.Clamp01((s.m_above - PeakHeight) / RidgeHeightFull)) * G;
                if (massif >= 0 && m_massifPeaks[massif] > 0f) width = Mathf.Min(width, m_massifPeaks[massif] * RidgeShare);
                // Drawn a little towards the massif's middle (the user's call), so its ridges close
                // up into one range instead of fraying out to its edges.
                if (massif >= 0)
                {
                    s.m_x += (middles[massif].x - s.m_x) * RidgePull;
                    s.m_y += (middles[massif].y - s.m_y) * RidgePull;
                }
                float height = RidgeHeightOf(width, s.m_seed);
                if (s.m_shore < width * 0.45f || !PeakOnLand(s.m_x, s.m_y, width, height)) return;
                if (RidgeHidesSummit(s.m_x, s.m_y, width, height)) return;
                Add(s, Kind.Ridge, width);
            });
            m_massifOf = null;
        }

        // Whether a ridge with this foot, width and height would hide part of a summit's peak
        // that must show: standing in front of it anywhere along the ridge's length and rising
        // over the peak's lower RidgeShown, or standing behind and rising over its apex. A ridge
        // is wide, so its whole length counts, not only its middle (NearSummit's rule for peaks).
        private bool RidgeHidesSummit(float x, float y, float width, float height)
        {
            foreach (Vector4 summit in m_summits)
            {
                if (Mathf.Abs(x - summit.x) >= summit.z * 0.45f + width * 0.5f) continue;
                float apex = y - height;
                if (apex >= summit.y || y <= summit.y - summit.w) continue;
                if (y >= summit.y ? apex < summit.y - summit.w * RidgeShown : apex < summit.y - summit.w) return true;
            }
            return false;
        }
    }
}
