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
        // The tile coast's pen in screen pixels, a little heavier than the ripples beside it.
        private const float TileCoastPixels = 3.5f;

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
            PlaceTrees();
            m_glyphs.Sort((a, b) => a.m_y.CompareTo(b.m_y));
            foreach (Glyph glyph in m_glyphs) DrawGlyph(glyph);
            m_cancel.ThrowIfCancellationRequested();
            // The sheet's other glyphs go over the trees: the few that meet them (spires in the
            // Mistlands woods) stand in front of them.
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.Glyph, InkLayer.Mist), null);
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.Ripple, InkLayer.Floe, InkLayer.Frame), null);
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
