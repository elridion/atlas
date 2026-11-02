using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// The edge of every wood: a pen line traced (marching squares) on the wood field just outside
    /// the wood, drawn before the trees. The outer crowns then scrape it away where they stand over
    /// it, so it shows only in the gaps between them and joins them into one bounded mass, the way a
    /// classic fantasy-novel map draws its forests.
    /// </summary>
    internal sealed partial class InkPainter
    {
        private void DrawWoodEdges()
        {
            int size = m_world.m_size;
            float half = size * 0.5f;
            float edgeSqr = EdgeRadius * EdgeRadius;
            DistanceField.Contour(size,
                (x, y) => m_world.m_wood[y * size + x],
                WoodOutline,
                (x, y) =>
                {
                    float wx = (x + 1f - half) * m_world.m_pixelSize;
                    float wz = (y + 1f - half) * m_world.m_pixelSize;
                    return wx * wx + wz * wz >= edgeSqr;
                },
                (a, b) =>
                {
                    Vector2 p = SourceToCanvas(a), q = SourceToCanvas(b);
                    // Where a wood runs into the water the coast is its edge.
                    if (!IsLandAt(p.x, p.y) || !IsLandAt(q.x, q.y)) return;
                    m_canvas.Stroke(p.x, p.y, q.x, q.y, WoodEdgePen);
                });
        }
    }
}
