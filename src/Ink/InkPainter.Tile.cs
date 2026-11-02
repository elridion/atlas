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

        // How far beyond a tile its ground must be sampled, in sheet pixels: glyphs standing outside
        // it, plus the margins their rules test, so a tile's neighbours decide alike at the edge.
        public static float TileMargin(float lod) => (GlyphReach + 8f) / lod;

        // Worker thread. Draws into canvas, which shows region (sheet pixels) at its magnification.
        public static void PaintTile(WorldSource world, InkRecord record, TileGrid grid, InkCanvas canvas, RectInt region,
            float lod, CancellationToken cancel)
        {
            var painter = new InkPainter(world, canvas, grid, lod, region, cancel);
            painter.RunTile(record);
        }

        private void RunTile(InkRecord record)
        {
            m_canvas.m_layer = InkLayer.Background;
            DrawBackground();
            m_canvas.m_layer = InkLayer.Tree;
            PlaceTrees();
            m_glyphs.Sort((a, b) => a.m_y.CompareTo(b.m_y));
            foreach (Glyph glyph in m_glyphs) DrawGlyph(glyph);
            m_cancel.ThrowIfCancellationRequested();
            // The sheet's other glyphs go over the trees: the few that meet them (spires in the
            // Mistlands woods) stand in front of them.
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.Glyph, InkLayer.Mist), null);
            // The tile's finer coast traces river channels the sheet saw as land, so the sheet's
            // river ink is kept only clear of the tile's water, or it runs along the new banks.
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.River),
                (a, b) => ClearOfWater(a) && ClearOfWater(b));
            m_canvas.m_layer = InkLayer.Coast;
            DrawTileCoast(record);
            record.Replay(m_canvas, m_left, m_top, m_right, m_bottom, Layers(InkLayer.Ripple, InkLayer.Floe, InkLayer.Frame), null);
        }

        private static int Layers(params InkLayer[] layers)
        {
            int mask = 0;
            foreach (InkLayer layer in layers) mask |= 1 << (int)layer;
            return mask;
        }

        // The coast on the tile's own heights. The sheet's sea pixels tell the heavy sea coast from
        // the light shore of inland water.
        private void DrawTileCoast(InkRecord record)
        {
            float water = m_grid.m_waterLevel;
            float[] height = m_grid.m_height;
            Contour(height, water, (p, q) =>
            {
                Vector2 mid = (p + q) * 0.5f;
                bool sea = record.IsSea(mid.x, mid.y) || record.IsSea(mid.x - 1.5f, mid.y) || record.IsSea(mid.x + 1.5f, mid.y)
                    || record.IsSea(mid.x, mid.y - 1.5f) || record.IsSea(mid.x, mid.y + 1.5f);
                m_canvas.Stroke(p.x, p.y, q.x, q.y, sea ? CoastWidth : LakeShore);
            });
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
