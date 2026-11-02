using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// Detail tiles: the part of the sheet in view drawn again at the zoom it is shown at. The
    /// ground fills, trees and coast are drawn afresh from a grid sampled for the tile
    /// (TileGrid), at a level of detail that grows with the zoom, so zooming in shows more and
    /// finer trees and an exact coast instead of the sheet's marks made bigger. Everything else
    /// (peaks, hills, rivers, mist, ripples, the frame) is replayed from the sheet's record.
    /// </summary>
    internal sealed partial class InkPainter
    {
        // The farthest a tile-drawn glyph reaches from its foot (the Mistlands' giant trees, 15 T
        // tall), at level of detail 1, in sheet pixels.
        private const float GlyphReach = 16f * T;
        // The tile coast's and waves' pens in screen pixels, the coast heavier: the smaller waves
        // stay a hairline that does not compete with it.
        private const float TileCoastPixels = 2.2f;
        private const float TileRipplePixels = 1.3f;

        // How far beyond a tile its ground must be sampled, in sheet pixels: glyphs standing outside
        // it, plus the margins their rules test, so a tile's neighbours decide alike at the edge.
        public static float TileMargin(float lod) => (GlyphReach + 8f) / lod;

        // A tile's magnification, tile pixels per sheet pixel.
        private float m_tileScale = 1f;

        // Worker thread. Draws into canvas, which shows region (sheet pixels) at its magnification.
        public static void PaintTile(WorldSource world, InkRecord record, TileGrid grid, InkCanvas canvas, RectInt region,
            float lod, int scale, CancellationToken cancel)
        {
            var painter = new InkPainter(world, canvas, grid, lod, region, cancel) { m_tileScale = scale };
            painter.RunTile(record);
        }

        private void RunTile(InkRecord record)
        {
            m_canvas.m_layer = InkLayer.Background;
            DrawBackground();
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.Lava), null);
            // Rivers and the coast under the glyphs, as on the sheet, so crowns hide the lines
            // behind them. The tile's finer coast traces river channels the sheet saw as land, so
            // the sheet's river ink is kept only clear of the tile's water, or it runs along the
            // new banks.
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.River),
                (a, b) => ClearOfWater(a) && ClearOfWater(b));
            m_canvas.m_layer = InkLayer.Coast;
            DrawTileCoast();
            MarkTileRivers(record);
            m_canvas.m_layer = InkLayer.Tree;
            // The sheet's peaks near the tile, whose bases the Mountains' trees keep clear of as
            // on the sheet: the tile's trees stand up to GlyphReach beyond it, and a peak's base
            // reaches half its width (51 px at most) from its foot.
            var peaks = new List<Vector3>();
            float reach = GlyphReach + PeakCell;
            record.Peaks(m_left - reach, m_top - reach, m_right + reach, m_bottom + reach, peaks);
            foreach (Vector3 peak in peaks) AddPeakBase(peak);
            PlaceTrees();
            m_glyphs.Sort((a, b) => a.m_y.CompareTo(b.m_y));
            // The sheet's other glyphs (peaks, spires, hills) are sorted in with the tile's own
            // trees by their feet and drawn from north to south, so whichever stands in front
            // hides the other, as on the sheet; laid over all the trees, a spire behind a tree
            // covered it.
            List<InkRecord.Group> groups = record.Groups(InkLayer.Glyph, m_left, m_top, m_right, m_bottom);
            groups.Sort((a, b) => a.m_foot.CompareTo(b.m_foot));
            int next = 0;
            foreach (Glyph glyph in m_glyphs)
            {
                while (next < groups.Count && groups[next].m_foot <= glyph.m_y) record.Replay(m_canvas, groups[next++]);
                DrawGlyph(glyph);
            }
            while (next < groups.Count) record.Replay(m_canvas, groups[next++]);
            m_cancel.ThrowIfCancellationRequested();
            DrawTileWaves(record);
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.Floe, InkLayer.Frame), null);
        }

        private static int Layers(params InkLayer[] layers)
        {
            int mask = 0;
            foreach (InkLayer layer in layers) mask |= 1 << (int)layer;
            return mask;
        }

        // The coast on the tile's own heights, one pen for sea and inland water alike. Its width
        // is set in screen pixels, divided back out of the pen scale: the sheet's coast thinned
        // only by the level of detail stood 6 pixels wide at 4x.
        private void DrawTileCoast()
        {
            float width = TileCoastPixels / m_tileScale / m_pen;
            Contour(m_grid.m_height, m_grid.m_waterLevel, (p, q) => m_canvas.Stroke(p.x, p.y, q.x, q.y, width));
        }

        // How far either side of a wave's sheet anchor the tile looks for its own coast, in sheet
        // pixels, and the paper left between that coast and the wave, in screen pixels: enough to
        // clear the coast's pen so the wave starts right at it without touching.
        private const float TileWaveSearch = 4f;
        private const float TileWaveGapPixels = 2.6f;

        // The sheet's waves drawn again, at a pen of their own in screen pixels, finer than the
        // coast's (thinned by the level of detail alone they stood almost as heavy as it at 4x).
        // A wave that starts at the coast is moved to the tile's own coast, which runs up to a few
        // sheet pixels from the sheet's: replayed where the sheet drew them, they stood off it
        //. Its crests are then cut to the water ahead. An anchor outside the
        // tile's ground is left where it is, as the tile cannot see that coast. Floating waves
        // stay put and are left out where the tile has land.
        private void DrawTileWaves(InkRecord record)
        {
            float reach = (WaveLongCrests + 1) * WaveCrest * 1.1f + TileWaveSearch;
            var waves = new List<InkRecord.Wave>();
            record.Waves(m_left - reach, m_top - 2f, m_right + reach, m_bottom + 2f, waves);
            if (waves.Count == 0) return;
            TileGrid grid = m_grid;
            float gridRight = grid.m_left + (grid.m_columns - 1) * grid.m_step;
            float gap = TileWaveGapPixels / m_tileScale;
            float pen = m_canvas.m_penScale;
            m_canvas.m_penScale = TileRipplePixels / m_tileScale;
            foreach (InkRecord.Wave wave in waves)
            {
                float x = wave.m_x, y = wave.m_y, length = wave.m_length;
                int crests = wave.m_crests;
                int dir = wave.m_anchor;
                if (dir == CutAnchor)
                {
                    // An islet cluster's stroke, cut by the close-up's own finer islets. Its
                    // ground outside the grid repeats the edge, but a part that reaches into the
                    // tile from beyond the grid is longer than two crests either way.
                    Vector2 along = wave.m_dir == Vector2.zero ? Vector2.right : wave.m_dir;
                    WaterParts(new Vector2(x, y), along, length, crests, m_grid.IsLand, m_parts);
                    foreach (Vector2 part in m_parts) WaveStroke(new Vector2(x, y), along, length, crests, wave.m_phase, wave.m_lift, part.x, part.y);
                    continue;
                }
                if (dir != 0)
                {
                    // A lake's line (lift 0) starts on the coast's pen itself, touching it (user,
                    //); its room is measured from just off the shore.
                    bool lake = wave.m_lift == 0f;
                    float tip = dir > 0 ? x : x + length, from = 0f;
                    if (tip - TileWaveSearch > grid.m_left && tip + TileWaveSearch < gridRight && TileShore(tip, y, dir, out float shore))
                        tip = lake ? shore : shore + dir * gap;
                    if (lake) from = grid.m_step;
                    float crest = length / crests;
                    float room = TileRoom(tip + dir * from, y, dir, length, gap) + from;
                    // A lake's line keeps its three dashes, as on the sheet.
                    int least = lake ? LakeLeastDashes : 2;
                    while (crests > least && crests * crest > room) crests--;
                    if (crests * crest > room) continue;
                    length = crests * crest;
                    x = dir > 0 ? tip : tip - length;
                }
                else if (TileRoom(x, y, 1, length, 0f) < length)
                {
                    continue;
                }
                WaveStroke(new Vector2(x, y), Vector2.right, length, crests, wave.m_phase, wave.m_lift);
            }
            m_canvas.m_penScale = pen;
        }

        // The tile's coast on the row through y nearest x within TileWaveSearch, where land gives
        // way to water going dir.
        private bool TileShore(float x, float y, int dir, out float shore)
        {
            float step = Math.Min(0.25f, m_grid.m_step * 0.5f);
            shore = 0f;
            float best = float.MaxValue;
            bool before = m_grid.IsLand(x - dir * TileWaveSearch, y);
            for (float s = -TileWaveSearch + step; s <= TileWaveSearch; s += step)
            {
                float at = x + dir * s;
                bool land = m_grid.IsLand(at, y);
                if (before && !land && Math.Abs(s) < best)
                {
                    best = Math.Abs(s);
                    shore = at - dir * step * 0.5f;
                }
                before = land;
            }
            return best < float.MaxValue;
        }

        // Water ahead of x along dir on the row through y, up to length and the gap beyond it:
        // where land comes, the room ends the gap short of it.
        private float TileRoom(float x, float y, int dir, float length, float gap)
        {
            float step = Math.Min(0.25f, m_grid.m_step * 0.5f);
            for (float s = 0f; s <= length + gap; s += step)
                if (m_grid.IsLand(x + dir * s, y)) return Math.Max(0f, s - gap);
            return length;
        }

        // The tile's river courses, from the sheet's river strokes, which its trees keep off: a
        // grid over the tile and its glyph reach at RiverCell sheet pixels a cell.
        private const float RiverCell = 0.5f;
        private byte[] m_tileRivers;
        private int m_tileRiverColumns, m_tileRiverRows;
        private float m_tileRiverLeft, m_tileRiverTop;

        private void MarkTileRivers(InkRecord record)
        {
            float reach = GlyphReach / m_lod + 2f;
            m_tileRiverLeft = m_left - reach;
            m_tileRiverTop = m_top - reach;
            m_tileRiverColumns = Mathf.CeilToInt((m_right - m_left + 2f * reach) / RiverCell) + 1;
            m_tileRiverRows = Mathf.CeilToInt((m_bottom - m_top + 2f * reach) / RiverCell) + 1;
            m_tileRivers = new byte[m_tileRiverColumns * m_tileRiverRows];
            record.Strokes(InkLayer.River, m_tileRiverLeft, m_tileRiverTop, m_right + reach, m_bottom + reach, (a, b, width) =>
            {
                // The band the river scraped, as on the sheet's own river mask, thinned with the
                // tile's pens.
                float radius = ((width + RiverBand) * 0.5f + 1f) * m_pen;
                float length = Vector2.Distance(a, b);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length / RiverCell));
                for (int k = 0; k <= steps; k++) MarkTileRiver(Vector2.Lerp(a, b, k / (float)steps), radius);
            });
        }

        private void MarkTileRiver(Vector2 at, float radius)
        {
            int x0 = Mathf.Max(0, (int)((at.x - radius - m_tileRiverLeft) / RiverCell));
            int x1 = Mathf.Min(m_tileRiverColumns - 1, (int)((at.x + radius - m_tileRiverLeft) / RiverCell));
            int y0 = Mathf.Max(0, (int)((at.y - radius - m_tileRiverTop) / RiverCell));
            int y1 = Mathf.Min(m_tileRiverRows - 1, (int)((at.y + radius - m_tileRiverTop) / RiverCell));
            for (int y = y0; y <= y1; y++)
            {
                for (int x = x0; x <= x1; x++)
                {
                    float dx = m_tileRiverLeft + (x + 0.5f) * RiverCell - at.x, dy = m_tileRiverTop + (y + 0.5f) * RiverCell - at.y;
                    if (dx * dx + dy * dy <= radius * radius) m_tileRivers[y * m_tileRiverColumns + x] = 1;
                }
            }
        }

        private bool OnTileRiver(float x, float y)
        {
            if (m_tileRivers == null) return false;
            int gx = (int)((x - m_tileRiverLeft) / RiverCell), gy = (int)((y - m_tileRiverTop) / RiverCell);
            if (gx < 0 || gy < 0 || gx >= m_tileRiverColumns || gy >= m_tileRiverRows) return false;
            return m_tileRivers[gy * m_tileRiverColumns + gx] != 0;
        }

        private bool ClearOfWater(Vector2 p) => m_grid.IsLand(p.x, p.y) && m_grid.Distance(m_grid.m_shore, p.x, p.y) >= RiverShoreClear;

        // Marching squares over a grid-shaped array, segments in sheet coordinates, inside the
        // frame's ring only.
        private void Contour(float[] values, float level, System.Action<Vector2, Vector2> segment)
        {
            TileGrid grid = m_grid;
            int columns = grid.m_columns, rows = grid.m_rows;
            float edge = EdgeRadius / MetresPerPixel, centre = CanvasSize * 0.5f;
            DistanceField.Contour(Mathf.Max(columns, rows),
                (x, y) => values[Mathf.Min(y, rows - 1) * columns + Mathf.Min(x, columns - 1)],
                level,
                (x, y) =>
                {
                    if (x >= columns - 1 || y >= rows - 1) return true;
                    float dx = grid.m_left + (x + 0.5f) * grid.m_step - centre, dy = grid.m_top + (y + 0.5f) * grid.m_step - centre;
                    return dx * dx + dy * dy >= edge * edge;
                },
                (a, b) => segment(
                    new Vector2(grid.m_left + a.x * grid.m_step, grid.m_top + a.y * grid.m_step),
                    new Vector2(grid.m_left + b.x * grid.m_step, grid.m_top + b.y * grid.m_step)));
        }
    }
}
