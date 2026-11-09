using System.Collections.Generic;
using System.Threading.Tasks;
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
    /// strokes by index, but only for a record drawn with the same pins (Matches). It also leaves
    /// out every sea illustration not lying wholly in explored land (m_hiddenSea), on the sheet
    /// and in close-ups. Read-only once laid, so workers share it.
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
        // The sea illustrations left out (any not wholly in explored land, Paper.SeaMarkSeen): their
        // boxes in sheet pixels (left, top, right, bottom) and the sheet y each was drawn at, its
        // record group's foot; null when none is. They stand far apart (IllustrationSpacing), so
        // a box overlapping one names it.
        public List<Vector4> m_hiddenSea;
        private List<float> m_hiddenSeaFeet;
        // The band this was judged on, so other marks can be judged on it (Paper.Redecide).
        public FogBase m_base;

        public void HideSea(SeaMark mark)
        {
            if (m_hiddenSea == null)
            {
                m_hiddenSea = new List<Vector4>();
                m_hiddenSeaFeet = new List<float>();
            }
            m_hiddenSea.Add(SeaBox(mark));
            m_hiddenSeaFeet.Add(mark.m_y);
        }

        public bool HidesSea(SeaMark mark)
        {
            Vector4 b = SeaBox(mark);
            return HidesSea(b.x, b.y, b.z, b.w);
        }

        // Whether a sheet rectangle (a sea wash's area) overlaps a sea illustration left out.
        public bool HidesSea(float left, float top, float right, float bottom)
        {
            if (m_hiddenSea == null) return false;
            foreach (Vector4 b in m_hiddenSea)
                if (b.x <= right && left <= b.z && b.y <= bottom && top <= b.w) return true;
            return false;
        }

        // Whether an Illustration record group, taken for the sheet rectangle (a close-up's
        // area), drew a sea mark left out: its foot is the mark's and the mark's box reaches the
        // rectangle. The stand-ins sit on OpenCell rows, so two in one row share a foot; they
        // stand StandInSpacing apart, so only one of them reaches a close-up.
        public bool HidesSeaGroup(InkRecord.Group group, float left, float top, float right, float bottom)
        {
            if (m_hiddenSeaFeet == null) return false;
            for (int i = 0; i < m_hiddenSeaFeet.Count; i++)
            {
                Vector4 b = m_hiddenSea[i];
                if (System.Math.Abs(m_hiddenSeaFeet[i] - group.m_foot) < 0.01f
                    && b.x - 1f <= right && left <= b.z + 1f && b.y - 1f <= bottom && top <= b.w + 1f) return true;
            }
            return false;
        }

        internal static Vector4 SeaBox(SeaMark mark) => new Vector4(
            (mark.m_west + InkPainter.HalfExtent) / InkPainter.MetresPerPixel,
            (InkPainter.HalfExtent - mark.m_north) / InkPainter.MetresPerPixel,
            (mark.m_east + InkPainter.HalfExtent) / InkPainter.MetresPerPixel,
            (InkPainter.HalfExtent - mark.m_south) / InkPainter.MetresPerPixel);

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

    /// <summary>
    /// The band Paper.Fog laid before judging any mark: its opacity before relief is lifted out
    /// of it, its thinning, the glimpse's opacity (the unexplored land softened at the explored
    /// edge, before the Glimpse setting is applied), the snapshot the sea illustrations are judged
    /// on and, with the Glimpse setting off, the glimpse as the marks are judged against it (with
    /// it on, that is the laid glimpse itself). None of it depends on the sheet, so a new sheet's
    /// marks are judged on it again (Paper.Redecide) instead of laying the band again, and the
    /// three planes are all the disk cache keeps of a fog (LaidCache). Read-only once laid.
    /// </summary>
    internal sealed class FogBase
    {
        public byte[] m_opacity;
        public byte[] m_thin;
        public byte[] m_unexplored;
        public Snapshot m_snapshot;
        public byte[] m_beyond;
    }

    /// <summary>
    /// What a new fog changed for close-ups against the fog laid before it (Between), on the
    /// same sheet marks or a new sheet's after a pin change: where the planes a close-up reads
    /// differ (the glimpse's opacity, the band's opacity and its thinning), as blocks of
    /// BlockPixels fog pixels, and the sheet boxes of the marks whose decision changed (glyphs
    /// and wave strokes by index on the same marks, matched by footprint on others; sea
    /// illustrations by their boxes).
    /// A close-up whose reads miss all of it draws the same under either fog, so it is kept
    /// (DetailTiles.SetGlimpse). Made on the worker that laid the fog, as comparing the planes
    /// reads 16 MB; the main thread only tests tiles against it.
    /// </summary>
    internal sealed class FogChange
    {
        // Fog pixels to a block side: a 256Â² grid of flags, so a change dirties at most 8 fog
        // pixels (16 sheet pixels) round itself.
        private const int BlockPixels = 8;
        private const int Blocks = Paper.FogSize / BlockPixels;
        // How far a mark's ink in a close-up reaches past its FogMark box, in sheet pixels: a
        // glyph's outline and the paper its body scrapes round itself; a wave stroke moved onto
        // the close-up's own coast (InkPainter.TileWaveSearch and its gap, more off an islet);
        // a sea illustration's washes (SeaWash: Reach and the stand-ins' wedge below the box).
        private const float GlyphGrow = 12f;
        private const float WaveGrow = 12f;
        private const float SeaGrow = 24f;

        // The glimpse and edge it was compared against: a close-up drawn under any other is
        // dropped regardless.
        public byte[] m_fromGlimpse;
        public FogEdge m_fromEdge;
        // Blocks where a plane changed, rows from the south.
        private bool[] m_blocks;
        private readonly List<Rect> m_marks = new List<Rect>();

        // Worker thread. The change from the fog laid as oldGlimpse and oldEdge to glimpse and
        // edge, or null when they cannot be compared (no earlier fog or marks): then every
        // close-up is drawn again. oldSea and sea are the sea illustrations each was laid for.
        // Edges judged for other sheet marks (a new sheet after a pin change) are compared mark
        // by mark: a glyph or wave stroke is the same mark in both when its footprint, order,
        // shape and kind agree (indices shift as marks are added or left out elsewhere), and one
        // without a match, or judged otherwise, counts as changed; so does a sea illustration in
        // one list and not the other.
        public static FogChange Between(byte[] oldGlimpse, FogEdge oldEdge, byte[] glimpse, FogEdge edge, IList<SeaMark> oldSea, IList<SeaMark> sea)
        {
            if (oldGlimpse == null || oldEdge == null || glimpse == null || edge == null) return null;
            if (oldEdge.m_edge == null || oldEdge.m_thin == null) return null;
            bool sameMarks = oldEdge.m_marks == edge.m_marks;
            if (!sameMarks && (oldEdge.m_marks == null || edge.m_marks == null)) return null;
            if (sameMarks && (!SameLength(oldEdge.m_glyphs, edge.m_glyphs) || !SameLength(oldEdge.m_waves, edge.m_waves))) return null;
            int n = Paper.FogSize;
            if (oldGlimpse.Length != n * n * 4 || glimpse.Length != n * n * 4) return null;
            var change = new FogChange { m_fromGlimpse = oldGlimpse, m_fromEdge = oldEdge, m_blocks = new bool[Blocks * Blocks] };
            byte[] oldOpacity = oldEdge.m_edge, opacity = edge.m_edge, oldThin = oldEdge.m_thin, thin = edge.m_thin;
            bool[] blocks = change.m_blocks;
            // Each block row is written by its own iteration only.
            Parallel.For(0, Blocks, Workers.LoopOptions(), by =>
            {
                for (int y = by * BlockPixels; y < (by + 1) * BlockPixels; y++)
                {
                    int row = y * n;
                    for (int x = 0; x < n; x++)
                    {
                        int i = row + x;
                        if (oldOpacity[i] != opacity[i] || oldThin[i] != thin[i] || oldGlimpse[i * 4 + 3] != glimpse[i * 4 + 3])
                            blocks[by * Blocks + x / BlockPixels] = true;
                    }
                }
            });
            SheetMarks marks = edge.m_marks;
            if (sameMarks)
            {
                if (edge.m_glyphs != null)
                {
                    for (int i = 0; i < edge.m_glyphs.Length; i++)
                        if (edge.m_glyphs[i] != oldEdge.m_glyphs[i]) change.m_marks.Add(Grown(marks.m_glyphs[i].m_box, GlyphGrow));
                    for (int i = 0; i < edge.m_waves.Length; i++)
                        if (edge.m_waves[i] != oldEdge.m_waves[i]) change.m_marks.Add(Grown(marks.m_waves[i].m_box, WaveGrow));
                }
            }
            else
            {
                Matched(oldEdge.m_marks.m_glyphs, oldEdge.m_glyphs, marks.m_glyphs, edge.m_glyphs, GlyphGrow, change.m_marks);
                Matched(oldEdge.m_marks.m_waves, oldEdge.m_waves, marks.m_waves, edge.m_waves, WaveGrow, change.m_marks);
            }
            AddMissing(edge.m_hiddenSea, oldEdge.m_hiddenSea, change.m_marks);
            AddMissing(oldEdge.m_hiddenSea, edge.m_hiddenSea, change.m_marks);
            // A sea illustration placed in one fog's sheet and not the other's is judged anew.
            if (oldSea != sea)
            {
                int a = oldSea?.Count ?? 0, b = sea?.Count ?? 0;
                RecordChange.Diff(a, b, false, (i, j) => InkRecord.SameSeaMark(oldSea[i], sea[j]),
                    i => InkRecord.SeaMarkKey(oldSea[i]), j => InkRecord.SeaMarkKey(sea[j]),
                    i => change.m_marks.Add(Grown(FogEdge.SeaBox(oldSea[i]), SeaGrow)), j => change.m_marks.Add(Grown(FogEdge.SeaBox(sea[j]), SeaGrow)));
            }
            return change;
        }

        private static bool SameLength(bool[] a, bool[] b) => a == null ? b == null : b != null && a.Length == b.Length;

        // Adds the grown boxes of the marks of either list without a match in the other, or
        // judged otherwise there (left out, per list, as FogEdge keeps it; null for none).
        private static void Matched(List<FogMark> oldMarks, bool[] oldHidden, List<FogMark> marks, bool[] hidden, float grow, List<Rect> into)
        {
            RecordChange.Diff(oldMarks.Count, marks.Count, false,
                (i, j) => SameMark(oldMarks[i], marks[j]) && Hidden(oldHidden, i) == Hidden(hidden, j),
                i => oldMarks[i].m_box.GetHashCode(), j => marks[j].m_box.GetHashCode(),
                i => into.Add(Grown(oldMarks[i].m_box, grow)), j => into.Add(Grown(marks[j].m_box, grow)));
        }

        private static bool Hidden(bool[] hidden, int i) => hidden != null && i < hidden.Length && hidden[i];

        // The same mark of two sheets, its record group aside (group ids shift).
        private static bool SameMark(FogMark a, FogMark b) =>
            a.m_box.Equals(b.m_box) && a.m_order == b.m_order && a.m_shape == b.m_shape && a.m_thinned == b.m_thinned;

        private static Rect Grown(Vector4 box, float grow) => Rect.MinMaxRect(box.x - grow, box.y - grow, box.z + grow, box.w + grow);

        // The sea illustrations hidden in one fog and not the other.
        private static void AddMissing(List<Vector4> boxes, List<Vector4> other, List<Rect> into)
        {
            if (boxes == null) return;
            foreach (Vector4 box in boxes)
                if (other == null || !other.Contains(box)) into.Add(Grown(box, SeaGrow));
        }

        // Whether a close-up of region (sheet pixels) that reads the fog's planes over reads
        // (DetailTile.FogReach) draws differently under the new fog.
        public bool Affects(RectInt region, Rect reads)
        {
            foreach (Rect mark in m_marks)
                if (mark.xMin <= region.xMax && region.xMin <= mark.xMax && mark.yMin <= region.yMax && region.yMin <= mark.yMax) return true;
            // A plane is sampled bilinearly (Paper.PlaneAt, GlimpseAt): a sheet point x reads fog
            // pixels floor(x * scale - 0.5) and the one after, rows from the south.
            float scale = Paper.FogSize / (float)InkPainter.CanvasSize, canvas = InkPainter.CanvasSize;
            int x0 = Mathf.Clamp(Mathf.FloorToInt(reads.xMin * scale - 1.5f), 0, Paper.FogSize - 1);
            int x1 = Mathf.Clamp(Mathf.CeilToInt(reads.xMax * scale + 0.5f), 0, Paper.FogSize - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((canvas - reads.yMax) * scale - 1.5f), 0, Paper.FogSize - 1);
            int y1 = Mathf.Clamp(Mathf.CeilToInt((canvas - reads.yMin) * scale + 0.5f), 0, Paper.FogSize - 1);
            for (int by = y0 / BlockPixels; by <= y1 / BlockPixels; by++)
                for (int bx = x0 / BlockPixels; bx <= x1 / BlockPixels; bx++)
                    if (m_blocks[by * Blocks + bx]) return true;
            return false;
        }
    }
}
