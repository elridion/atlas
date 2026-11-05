using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    // The rough shape of a mark's ink inside its footprint, which the sheet's ink is cleared over
    // when the fog's edge leaves the mark out (Paper.HiddenInk).
    internal enum MarkShape : byte
    {
        // A round crown on its trunk: the ellipse in the footprint.
        Crown,
        // A pine or a peak: the triangle on the footprint's base, apex at the top middle.
        Spire,
        // A hill: the half ellipse on the footprint's base.
        Dome,
        // The whole footprint.
        Box,
    }

    // One mark the fog's edge shows or leaves out whole (Paper.Fog): its footprint (left, top,
    // right, bottom in sheet pixels), its shape, and its place in the drawing order (its foot,
    // north first). m_thinned marks a glyph thinned out as the trees are (a dead tree) rather
    // than judged as relief. m_group is the record group that drew it, or -1 (trees, waves, and
    // marks read from the cache).
    internal struct FogMark
    {
        public Vector4 m_box;
        public float m_order;
        public MarkShape m_shape;
        public bool m_thinned;
        public int m_group;
    }

    /// <summary>
    /// The sheet's marks that the fog's edge shows or leaves out whole: its trees, its other
    /// glyphs but pins and mist (relief and dead trees, which close-ups replay from the record),
    /// and its wave strokes. Kept in the disk cache with the sheet, since the fog is laid over a
    /// cached sheet before the world is painted. Painting is deterministic, so the lists come in
    /// the same order from the record and the cache: what the fog decides for mark i a close-up
    /// applies to the record's mark i, once m_key (the pins the sheet was drawn with) matches.
    /// </summary>
    internal sealed class SheetMarks
    {
        public long m_key;
        public readonly List<FogMark> m_trees = new List<FogMark>(1 << 16);
        public readonly List<FogMark> m_glyphs = new List<FogMark>();
        public readonly List<FogMark> m_waves = new List<FogMark>();
    }

    /// <summary>
    /// Where the drawing stops, as Paper.Fog laid it: the band's own opacity and its thinning
    /// (FogSize planes, rows from the south), and per mark of m_marks whether it is left out.
    /// The sheet's ink is cleared over the marks left out (Paper.InkWithMips); close-ups judge
    /// their own trees by the planes (Paper.CutByFog) and leave out the sheet's glyphs and wave
    /// strokes by index, but only for a record drawn with the same pins (Matches). Read-only once
    /// laid, so workers share it.
    /// </summary>
    internal sealed class FogEdge
    {
        public SheetMarks m_marks;
        // The fog's opacity as laid (relief lifted out of it), and the share of trees and wave
        // strokes the thinning leaves out, 0 to 255.
        public byte[] m_edge;
        public byte[] m_thin;
        // Left out, per m_marks list; null when there were no marks.
        public bool[] m_trees, m_glyphs, m_waves;

        // Whether the record's marks are the ones judged here, so its indices hold.
        public bool Matches(InkRecord record) =>
            record != null && m_marks != null && m_glyphs != null && record.m_marks.m_key == m_marks.m_key
            && record.m_marks.m_glyphs.Count == m_glyphs.Length && record.m_marks.m_waves.Count == m_waves.Length;

        // The record groups (InkRecord.Group.m_id) of the glyphs left out, or null for none.
        public HashSet<int> HiddenGroups(InkRecord record)
        {
            if (!Matches(record)) return null;
            HashSet<int> groups = null;
            for (int i = 0; i < m_glyphs.Length; i++)
            {
                if (!m_glyphs[i]) continue;
                int group = record.m_marks.m_glyphs[i].m_group;
                if (group < 0) continue;
                if (groups == null) groups = new HashSet<int>();
                groups.Add(group);
            }
            return groups;
        }
    }
}
