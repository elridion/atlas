using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The lone oak of open Meadows (the game's oak1_meadows, about one per two zones): a
    /// gnarled oak with a bowed trunk, a broken stub and a round billowy crown (the user's pick,
    /// F of a design round, at OakBig its size). The crown is the union of a core ellipse and
    /// round puffs traced as one outline (PuffOutline), so the billows meet in cusps as a pen
    /// draws a cloud-like oak. A representative scatter, not the game's real oaks. Grass tufts
    /// keep out from behind it (TuftByOak), since the ground fills are laid before any glyph.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // One candidate per OakStep (96 m) cell of a sheet grid, OakOdds kept, so about one oak
        // per 128 x 128 m of open Meadows, the game's rate once the woods, hills and water are
        // left out; a candidate gives way to a higher-priority neighbour within OakGap (sheet
        // pixels; the crown is about 12 wide), so lone oaks stay apart. A cell tries OakTries
        // places for its oak (the first its own spot), since the large crown fits few clearings.
        private const float OakStep = 96f / MetresPerPixel;
        private const float OakOdds = 0.56f;
        private const float OakJitter = 0.85f;
        private const float OakGap = 14f;
        private const int OakTries = 7;
        private const int OakSalt = 44;
        // The game's oaks grow on ground up to 20 degrees (maxTilt 20, tan 20), stricter than the
        // other Meadows trees' 30.
        private const float OakTilt = 0.364f;

        // The oak drawn this much larger than its numbers (the user's pick, 1.5x), and its
        // numbers a little larger than a broadleaf's. The core ellipse inside the billows, as a
        // share of the form's half width and top.
        private const float OakBig = 1.5f;
        private const float OakScale = 1.15f;
        private const float CoreX = 0.85f;
        private const float CoreTop = 0.8f;

        private readonly List<Vector2> m_oak = new List<Vector2>(96);
        private readonly List<Vector4> m_boles = new List<Vector4>(4);
        private readonly List<Vector2> m_boleHalves = new List<Vector2>(4);
        private readonly List<Vector3> m_puffs = new List<Vector3>(12);

        private struct OakForm
        {
            public float m_cx, m_cy, m_rx, m_top, m_bottom, m_trunk;
        }

        // The crown's core ellipse (half width, height above and below the middle) and the
        // visible trunk under it, by seed; s already holds OakBig.
        private static OakForm OakShape(float x, float y, float s, int seed)
        {
            s *= OakScale;
            float rx = 2.8f * s * Vary(seed, 130, 0.08f);
            float top = 2.5f * s * Vary(seed, 131, 0.1f);
            float bottom = 1.1f * s * Vary(seed, 132, 0.1f);
            float trunk = 2.4f * s * Vary(seed, 133, 0.12f);
            return new OakForm
            {
                m_cx = x + (Unit(seed, 134) - 0.5f) * 0.4f * s, m_cy = y - trunk - bottom,
                m_rx = rx, m_top = top, m_bottom = bottom, m_trunk = trunk,
            };
        }

        // The billows round the crown's top and sides into m_puffs (x, y, radius).
        private void OakPuffs(OakForm f, int seed)
        {
            m_puffs.Clear();
            const int n = 4;
            const float size = 0.42f, reach = 0.72f, side = 0.34f;
            for (int k = 0; k < n; k++)
            {
                float a = Mathf.PI * (1f + (k + 0.5f + (Unit(seed, 150 + k) - 0.5f) * 0.3f) / n);
                float r = size * f.m_rx * Vary(seed, 160 + k, 0.15f);
                m_puffs.Add(new Vector3(f.m_cx + Mathf.Cos(a) * f.m_rx * reach, f.m_cy + Mathf.Sin(a) * f.m_top * reach, r));
            }
            // A billow low on each flank, so the crown is broad down to its underside.
            for (int k = 0; k < 2; k++)
            {
                float r = side * f.m_rx * Vary(seed, 170 + k, 0.15f);
                float px = f.m_cx + (k == 0 ? -1f : 1f) * f.m_rx * 0.78f;
                // Its underside no lower than the core's, so the crown does not hang down.
                float py = Mathf.Min(f.m_cy - 0.1f * f.m_top, f.m_cy + f.m_bottom * 0.95f - r);
                m_puffs.Add(new Vector3(px, py, r));
            }
        }

        // The union of the core ellipse and the puffs, traced from the crown's middle: on each
        // ray the farthest boundary, so billows meet in cusps; a slight hand wobble on top.
        private void PuffOutline(OakForm f, int seed, bool overlap)
        {
            m_oak.Clear();
            const int steps = 84;
            float start = Unit(seed, 140) * Mathf.PI * 2f;
            int last = overlap ? steps + 2 : steps - 1;
            for (int i = 0; i <= last; i++)
            {
                float a = start + Mathf.PI * 2f * i / steps;
                float dx = Mathf.Cos(a), dy = Mathf.Sin(a);
                float ry = dy < 0f ? f.m_top * CoreTop : f.m_bottom;
                float rx = f.m_rx * CoreX;
                float t = 1f / Mathf.Sqrt(dx * dx / (rx * rx) + dy * dy / (ry * ry));
                foreach (Vector3 p in m_puffs)
                {
                    float vx = p.x - f.m_cx, vy = p.y - f.m_cy;
                    float b = dx * vx + dy * vy;
                    float h = vx * vx + vy * vy - b * b;
                    if (h < p.z * p.z) t = Mathf.Max(t, b + Mathf.Sqrt(p.z * p.z - h));
                }
                float bump = 1f + 0.35f * (CrownBump(seed, a, 1f) - 1f);
                if (i > steps) bump *= 0.97f;
                m_oak.Add(new Vector2(f.m_cx + dx * t * bump, f.m_cy + dy * t * bump));
            }
        }

        private void Oak(float x, float y, float s, int seed)
        {
            s *= OakBig;
            OakForm f = OakShape(x, y, s, seed);
            OakPuffs(f, seed);
            // Where the trunk meets the crown's underside; trunk and limbs are drawn after the
            // crown, so its scrape does not eat the short bole.
            float under = f.m_cy + f.m_bottom * 0.9f;

            // The crown: scraped, marked as a wood body, shaded, inked.
            PuffOutline(f, seed, false);
            m_canvas.EraseOutline(m_oak, 0.08f * f.m_rx + 0.6f * m_pen);
            BeginWood(x, y);
            m_canvas.EraseOutline(m_oak, BodyGrow * m_pen);
            m_canvas.m_markBody = BodyKind.None;
            OakShade(f, seed);
            PuffOutline(f, seed, true);
            m_canvas.Polyline(m_oak, Mathf.Lerp(1.05f, 1.25f, Unit(seed, 44)));

            // A bowed, gnarled bole with a broken stub, and roots splaying at the foot.
            m_limbs.Clear();
            m_boles.Clear();
            m_boleHalves.Clear();
            float bow = (Unit(seed, 137) < 0.5f ? -1f : 1f) * Mathf.Lerp(0.35f, 0.6f, Unit(seed, 138)) * s;
            var mid = new Vector2(x + bow, y - (y - under) * 0.5f);
            OakBole(x, y, mid.x, mid.y, 0.75f * s, 0.58f * s);
            OakBole(mid.x, mid.y, f.m_cx, under, 0.58f * s, 0.48f * s);
            m_limbs.Add(new Limb(mid, mid + new Vector2(-Mathf.Sign(bow) * 1.1f * s, -0.7f * s), 1.2f));
            m_limbs.Add(new Limb(new Vector2(x - 0.4f * s, y - 0.3f * s), new Vector2(x - Mathf.Lerp(1.1f, 1.4f, Unit(seed, 139)) * s, y + 0.2f * s), Pen));
            m_limbs.Add(new Limb(new Vector2(x + 0.4f * s, y - 0.3f * s), new Vector2(x + Mathf.Lerp(1f, 1.3f, Unit(seed, 141)) * s, y + 0.15f * s), Pen));
            // Everything scraped first, then inked, so no scrape cuts the ink of another part.
            foreach (Limb limb in m_limbs) m_canvas.Scrape(limb.m_a.x, limb.m_a.y, limb.m_b.x, limb.m_b.y, limb.m_width + 0.6f);
            for (int i = 0; i < m_boles.Count; i++) BoleQuad(m_boles[i], m_boleHalves[i], 0.6f * m_pen, true);
            for (int i = 0; i < m_boles.Count; i++) BoleQuad(m_boles[i], m_boleHalves[i], 0f, false);
            foreach (Limb limb in m_limbs) m_canvas.Stroke(limb.m_a.x, limb.m_a.y, limb.m_b.x, limb.m_b.y, limb.m_width);
        }

        // A tapering bole from (x0, y0) to (x1, y1), filled solid, its band scraped first.
        private void OakBole(float x0, float y0, float x1, float y1, float half0, float half1)
        {
            m_boles.Add(new Vector4(x0, y0, x1, y1));
            m_boleHalves.Add(new Vector2(half0, half1));
        }

        private void BoleQuad(Vector4 b, Vector2 half, float grow, bool erase)
        {
            var a = new Vector2(b.x, b.y);
            var c = new Vector2(b.z, b.w);
            Vector2 d = c - a;
            var n = new Vector2(-d.y, d.x).normalized;
            Poly(a + n * (half.x + grow), c + n * (half.y + grow), c - n * (half.y + grow), a - n * (half.x + grow));
            if (erase) m_canvas.Erase(m_points);
            else m_canvas.Fill(m_points);
        }

        // The crown's shade: the core's east side, as the broadleaf's Shade but narrower (the
        // round crown filled near solid at sheet size with the full span), and the east billows
        // each on their own east side, so the shade's edge is scalloped as the crown's is.
        private void OakShade(OakForm f, int seed)
        {
            const float k = 0.92f;
            m_points.Clear();
            float span = CrownShade * 0.7f * Mathf.Lerp(0.85f, 1.15f, Unit(seed, 45));
            float turn = (Unit(seed, 46) - 0.5f) * 0.4f;
            for (int i = 0; i <= 8; i++)
            {
                float a = turn + Mathf.Lerp(-span, span, i / 8f);
                float sin = Mathf.Sin(a);
                m_points.Add(new Vector2(f.m_cx + Mathf.Cos(a) * f.m_rx * k, f.m_cy + sin * (sin < 0f ? f.m_top * CoreTop : f.m_bottom) * k));
            }
            m_canvas.Fill(m_points);
            foreach (Vector3 p in m_puffs)
                if (p.x > f.m_cx + f.m_rx * 0.4f) PuffShade(p, 0.95f, 0.2f);
        }

        // A billow's east segment: span either side of east, tilted down by tilt.
        private void PuffShade(Vector3 p, float span, float tilt)
        {
            m_points.Clear();
            for (int i = 0; i <= 8; i++)
            {
                float a = tilt + Mathf.Lerp(-span, span, i / 8f);
                m_points.Add(new Vector2(p.x + Mathf.Cos(a) * p.z * 0.97f, p.y + Mathf.Sin(a) * p.z * 0.97f));
            }
            m_canvas.Fill(m_points);
        }

        // The ellipse the oak's crown fills, for HideCoveredCrowns, the pins' cover and the grass.
        private static Crown OakCrown(float x, float y, float s, int seed)
        {
            OakForm f = OakShape(x, y, s * OakBig, seed);
            float up = f.m_top * 1.25f;
            return new Crown
            {
                m_cx = f.m_cx, m_cy = f.m_cy - (up - f.m_bottom) * 0.5f, m_rx = f.m_rx * 1.12f, m_ry = (up + f.m_bottom) * 0.5f,
                m_wobble = 1f, m_seed = seed,
            };
        }

        // Lone oaks on open Meadows, after the Meadows' woods and copses (copses holds the
        // copses' trees' range in m_glyphs): a sheet grid, so the same oaks stand at every zoom,
        // the foot on open Meadows WoodClearance from woods and off ground over OakTilt, the crown
        // off woods, rivers and water, clear of the hills, pins and copses.
        private void PlaceOaks(int copseFirst, int copseEnd)
        {
            float height = 8f * m_t * OakBig;
            // The crown's half width and middle, which must keep off woods, rivers and water too.
            float half = 3.7f * m_t * OakBig, mid = 5f * m_t * OakBig;
            bool Fits(Spot s)
            {
                s.m_shore /= m_lod;
                s.m_woodDistance /= m_lod;
                if (!IsOpenLand(s, Land.Meadows, 4f)) return false;
                if (m_world.MostlySteep(s.m_worldX, s.m_worldZ, SteepWindow, OakTilt)) return false;
                if (IsWoodAt(s.m_x, s.m_y - height * 0.6f) || OnRiver(s.m_x, s.m_y) || OnRiver(s.m_x, s.m_y - height * 0.6f)) return false;
                if (!IsLandColumn(s.m_x, s.m_y, height)) return false;
                if (IsWoodAt(s.m_x - half, s.m_y - mid) || IsWoodAt(s.m_x + half, s.m_y - mid)
                    || !IsLandAt(s.m_x - half, s.m_y - mid) || !IsLandAt(s.m_x + half, s.m_y - mid)
                    || OnRiver(s.m_x - half, s.m_y - mid) || OnRiver(s.m_x + half, s.m_y - mid)) return false;
                var oak = new Glyph { m_x = s.m_x, m_y = s.m_y, m_size = m_t, m_kind = Kind.Oak, m_seed = s.m_seed };
                Vector4 box = GlyphBox(oak);
                for (int i = copseFirst; i < copseEnd; i++)
                    if (Overlap(box, GlyphBox(m_glyphs[i])) > 0f) return false;
                return ClearOfPlaced(oak);
            }
            Scatter(OakStep, OakStep, OakJitter, 0f, OakSalt, s =>
            {
                if (!(s.m_roll < OakOdds)) return;
                // The cell's places come from its hash, so every zoom tries the same ones and the
                // count stays near the game's rate.
                for (int k = 0; k < OakTries; k++)
                {
                    Spot at = s;
                    if (k > 0)
                    {
                        float x = (s.m_gx + 0.5f + (Unit(s.m_seed, 60 + k) - 0.5f) * OakJitter) * OakStep;
                        float y = (s.m_gy + 0.5f + (Unit(s.m_seed, 70 + k) - 0.5f) * OakJitter) * OakStep;
                        at = Look(x, y);
                        at.m_roll = s.m_roll;
                        at.m_seed = s.m_seed;
                        at.m_gx = s.m_gx;
                        at.m_gy = s.m_gy;
                        at.m_gridSeed = s.m_gridSeed;
                    }
                    if (OakCrowded(at) || !Fits(at)) continue;
                    Add(at, Kind.Oak, m_t);
                    return;
                }
            }, sheetGrid: true);
        }

        // Whether a neighbouring cell's oak of higher priority may stand within OakGap: every
        // place the neighbour may try, from hashes only, so the sheet and every close-up agree.
        private bool OakCrowded(Spot s)
        {
            float priority = Unit(s.m_seed, 30);
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    int gx = s.m_gx + dx, gy = s.m_gy + dy;
                    int seed = HashInt(gx, gy, s.m_gridSeed);
                    if (!(Unit(seed, 3) < OakOdds) || Unit(seed, 30) < priority) continue;
                    for (int k = 0; k < OakTries; k++)
                    {
                        float x = (gx + 0.5f + (Unit(seed, k == 0 ? 1 : 60 + k) - 0.5f) * OakJitter) * OakStep - s.m_x;
                        float y = (gy + 0.5f + (Unit(seed, k == 0 ? 2 : 70 + k) - 0.5f) * OakJitter) * OakStep - s.m_y;
                        if (x * x + y * y < OakGap * OakGap) return true;
                    }
                }
            }
            return false;
        }

        // The oaks placed at this level, which the Meadows' grass tufts are tested against.
        private readonly List<Glyph> m_oaks = new List<Glyph>();

        private void FindOaks()
        {
            m_oaks.Clear();
            foreach (Glyph g in m_glyphs)
                if (g.m_kind == Kind.Oak) m_oaks.Add(g);
        }

        // How a grass tuft at (x, y) stands to the oaks. The tufts are laid before any glyph, so
        // an oak scrapes whatever lies under it: a tuft behind it (foot north of the oak's) that
        // reaches its crown, trunk or roots would show as a stub cut by the outline, so it is left
        // out (Behind); one in front whose box meets the oak's is inked again once the glyphs are
        // drawn (Front), so it stands over the oak's foot as a tree in front would.
        private TuftOak TuftByOak(float x, float y)
        {
            TuftOak result = TuftOak.Clear;
            if (m_oaks.Count == 0) return result;
            var box = new Vector4(x - 1.6f * m_g, y - 2.8f * m_g, x + 1.2f * m_g, y);
            foreach (Glyph oak in m_oaks)
            {
                if (Overlap(box, GlyphBox(oak)) <= 0f) continue;
                if (y > oak.m_y)
                {
                    result = TuftOak.Front;
                    continue;
                }
                if (TuftTouchesOak(x, y, oak)) return TuftOak.Behind;
            }
            return result;
        }

        private enum TuftOak { Clear, Behind, Front }

        // Whether any point along a tuft's two strokes (Glyphs.Grass) comes within a pen or so of
        // the oak's crown (OakCrown's ellipse), its trunk or its roots.
        private bool TuftTouchesOak(float x, float y, Glyph oak)
        {
            float s = oak.m_size * OakBig, margin = 1.5f * m_pen;
            Crown c = OakCrown(oak.m_x, oak.m_y, oak.m_size, oak.m_seed);
            float rx = c.m_rx + margin, ry = c.m_ry + margin;
            for (int k = 0; k <= 3; k++)
            {
                float t = k / 3f;
                for (int side = 0; side < 2; side++)
                {
                    float px = side == 0 ? x - (1f + 0.6f * t) * m_g : x + (0.8f + 0.4f * t) * m_g;
                    float py = y - (side == 0 ? 2.6f : 2.8f) * t * m_g;
                    float ex = (px - c.m_cx) / rx, ey = (py - c.m_cy) / ry;
                    if (ex * ex + ey * ey < 1f) return true;
                    float dx = Mathf.Abs(px - oak.m_x);
                    // The bowed trunk, up to its crown.
                    if (dx < 1.4f * s + margin && py > c.m_cy && py < oak.m_y + margin) return true;
                    // The roots splaying at the foot.
                    if (dx < 1.5f * s + margin && py > oak.m_y - 0.6f * s - margin && py < oak.m_y + 0.3f * s + margin) return true;
                }
            }
            return false;
        }
    }
}
