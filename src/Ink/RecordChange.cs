using System;
using System.Collections.Generic;
using UnityEngine;

namespace Atlas
{
    /// <summary>
    /// What a repaint for other pins (InkPainter.Repaint) changed for close-ups: everything a
    /// close-up reads from its record that differs between the old record and the new, as the
    /// sheet areas over which it is read. A close-up whose reads miss all of it draws the same
    /// from either record, so DetailTiles keeps it (NewRecord); the fog's decisions for the new
    /// marks are compared on their own when they come (FogChange). The one non-obvious part:
    /// a close-up replays every glyph group it picks whole and leaves out its own trees for the
    /// pins over them, so a change counts over the whole group and, for a pin, over the trees
    /// its box and the boxes chained to it can take out (PinAreas), not only where its ink lies.
    /// Made on the worker that painted; the main thread only tests tiles against it.
    /// </summary>
    internal sealed class RecordChange
    {
        // The record the change was made from: tiles drawn from any other are dropped regardless.
        public InkRecord m_from;
        private int m_across;
        // Index cells whose replayed calls, groups or feet differ (InkRecord.TileHashes), and
        // those whose river strokes do.
        private bool[] m_cells;
        private bool[] m_rivers;
        // The index cells (x, y the first, width and height the count) of every group with a
        // call in a changed cell that the other record has not alike: a close-up picking any of
        // its calls replays it all.
        private readonly List<RectInt> m_groups = new List<RectInt>();
        // Sheet areas over which a close-up's region reads a changed list entry (peaks, mist
        // banks, glyph footprints, waves, sea washes), already grown by its reach.
        private readonly List<Rect> m_reads = new List<Rect>();
        // Both records' pins, and per pin whether it was added, removed, moved, ticked, reordered
        // or given another order or relief cover (the other record has it not alike).
        private SheetPin[] m_pinsFrom, m_pinsTo;
        private bool[] m_changedFrom, m_changedTo;
        // Per close-up scale, the sheet areas a pin change reaches (PinAreas), made on the main
        // thread when a tile of that scale is first tested.
        private readonly Dictionary<int, List<Rect>> m_pinAreas = new Dictionary<int, List<Rect>>();

        public int ChangedPins { get; private set; }

        public int ChangedCells { get; private set; }

        // Worker thread. The change from the sealed record from to the sealed record to, or null
        // when they cannot be compared: then every close-up is drawn again.
        public static RecordChange Between(InkRecord from, InkRecord to)
        {
            if (from == null || to == null || from.CellsAcross == 0 || from.CellsAcross != to.CellsAcross) return null;
            int across = from.CellsAcross;
            var change = new RecordChange { m_from = from, m_across = across, m_cells = new bool[across * across], m_rivers = new bool[across * across] };
            ulong[] before = from.TileHashes(out ulong[] beforeRivers), after = to.TileHashes(out ulong[] afterRivers);
            var seenFrom = new HashSet<int>();
            var seenTo = new HashSet<int>();
            var groupsFrom = new List<int>();
            var groupsTo = new List<int>();
            for (int i = 0; i < before.Length; i++)
            {
                if (beforeRivers[i] != afterRivers[i]) change.m_rivers[i] = true;
                if (before[i] == after[i]) continue;
                change.m_cells[i] = true;
                change.ChangedCells++;
                from.GroupsNear(i % across, i / across, seenFrom, groupsFrom);
                to.GroupsNear(i % across, i / across, seenTo, groupsTo);
            }
            // The groups over the changed cells that are not the same in both records (their
            // calls, in order, and foot): a close-up picking one elsewhere replays its changed
            // part too. One alike in both is replayed alike wherever it is picked; the cells it
            // shares with others are compared above.
            var hashFrom = new ulong[groupsFrom.Count];
            var hashTo = new ulong[groupsTo.Count];
            for (int k = 0; k < hashFrom.Length; k++) hashFrom[k] = from.GroupHash(groupsFrom[k]);
            for (int k = 0; k < hashTo.Length; k++) hashTo[k] = to.GroupHash(groupsTo[k]);
            Diff(hashFrom.Length, hashTo.Length, false, (a, b) => hashFrom[a] == hashTo[b], a => hashFrom[a].GetHashCode(), b => hashTo[b].GetHashCode(),
                a => from.AddGroupCells(groupsFrom[a], change.m_groups), b => to.AddGroupCells(groupsTo[b], change.m_groups));
            if (!to.AddChangedLists(from, change.m_reads)) return null;
            change.FindChangedPins(from, to);
            return change;
        }

        // A pin as a close-up takes it: its place, glyph and tick, and its order and relief cover
        // from the record (InkPainter.FindMarks, TakeMarkOrders).
        private void FindChangedPins(InkRecord from, InkRecord to)
        {
            SheetPin[] a = from.m_pins ?? new SheetPin[0], b = to.m_pins ?? new SheetPin[0];
            m_pinsFrom = a;
            m_pinsTo = b;
            m_changedFrom = new bool[a.Length];
            m_changedTo = new bool[b.Length];
            // In order: the marks share the trees they limit (LimitMarkCover), pin by pin, so
            // pins alike keep their order among themselves.
            Diff(a.Length, b.Length, true, (i, j) => SamePin(a[i], b[j]) && SameOrder(from, i, to, j),
                i => PinKey(a[i]), j => PinKey(b[j]),
                i => m_changedFrom[i] = true, j => m_changedTo[j] = true);
            foreach (bool c in m_changedFrom) if (c) ChangedPins++;
            foreach (bool c in m_changedTo) if (c) ChangedPins++;
        }

        // The sheet areas where a close-up at scale can draw otherwise for the changed pins. A
        // close-up's pins reach its pixels only through the trees and grass tufts over their
        // boxes (InkPainter.MarkBox at its level of detail): LimitMarkCover and
        // DropHiddenBehindMarks leave out trees whose footprint meets a box, OnAMark and
        // DrawGrassOverMarks ink the tufts over one again. LimitMarkCover takes the pins in turn
        // and a tree one leaves out is no candidate for the next, so a pin's choice depends on
        // every pin whose box a tree can meet with its own, and theirs on others, down a chain of
        // boxes nearer than a tree's footprint (TilePinLink): the whole chain round a changed pin,
        // in either record, can leave out other trees. A changed pin's sheet box (among the glyph
        // footprints, with the dead trees it changed) also changes trees before any pin limits
        // them, so the pins those can meet join its chain (TilePinCascade); what those trees draw
        // themselves the glyph footprints' reach covers (InkRecord.AddChangedLists). Pins alike
        // outside such a chain decide alike from the same trees: they keep their order among
        // themselves (Diff), and their scatter covers the same trees in either record. Each box of
        // a chain counts, grown by the reach of a tree meeting it and of the ground marks round
        // that tree (TilePinReach). The rest of a close-up does not read the pins: ScatterArea
        // widens its scatter round pins near it, but every glyph that can show in it is decided
        // from its own hashes and the ground within TileMargin, as for tiles with no pin near.
        private List<Rect> PinAreas(int scale)
        {
            if (m_pinAreas.TryGetValue(scale, out List<Rect> areas)) return areas;
            areas = new List<Rect>();
            float lod = DetailTile.Lod(scale);
            AddChains(m_pinsFrom, m_changedFrom, lod, areas);
            AddChains(m_pinsTo, m_changedTo, lod, areas);
            m_pinAreas[scale] = areas;
            return areas;
        }

        private static void AddChains(SheetPin[] pins, bool[] changed, float lod, List<Rect> into)
        {
            int n = pins.Length;
            var boxes = new Rect[n];
            for (int i = 0; i < n; i++) boxes[i] = InkPainter.MarkBox(pins[i], lod);
            float link = InkPainter.TilePinLink / lod, reach = InkPainter.TilePinReach / lod, cascade = InkPainter.TilePinCascade;
            var inChain = new bool[n];
            var open = new Stack<int>();
            for (int i = 0; i < n; i++)
            {
                if (!changed[i]) continue;
                inChain[i] = true;
                open.Push(i);
            }
            // A changed pin's sheet box also changes trees before any pin limits them, through
            // what keeps off it (TilePinCascade): the pins near enough to meet those trees join.
            for (int i = 0; i < n; i++)
            {
                if (!changed[i]) continue;
                Rect a = InkPainter.MarkBox(pins[i], 1f);
                for (int j = 0; j < n; j++)
                {
                    if (inChain[j] || Gap(a, boxes[j]) >= cascade) continue;
                    inChain[j] = true;
                    open.Push(j);
                }
            }
            while (open.Count > 0)
            {
                Rect a = boxes[open.Pop()];
                for (int j = 0; j < n; j++)
                {
                    // A tree's footprint under link across and high can meet both boxes.
                    if (inChain[j] || Gap(a, boxes[j]) >= link) continue;
                    inChain[j] = true;
                    open.Push(j);
                }
            }
            for (int i = 0; i < n; i++)
            {
                if (!inChain[i]) continue;
                Rect b = boxes[i];
                into.Add(Rect.MinMaxRect(b.xMin - reach, b.yMin - reach, b.xMax + reach, b.yMax + reach));
            }
        }

        // The larger of the gaps between two rectangles across and down (negative when they
        // overlap that way): a box under it across and high can meet both.
        private static float Gap(Rect a, Rect b) =>
            Math.Max(Math.Max(b.xMin - a.xMax, a.xMin - b.xMax), Math.Max(b.yMin - a.yMax, a.yMin - b.yMax));

        private static bool SamePin(SheetPin a, SheetPin b) =>
            a.m_x == b.m_x && a.m_z == b.m_z && a.m_glyph == b.m_glyph && a.m_checked == b.m_checked;

        // The order and cover a close-up takes for pin i, alike in both records (TakeMarkOrders
        // takes them only when there is one per pin).
        private static bool SameOrder(InkRecord a, int i, InkRecord b, int j)
        {
            bool hasA = a.m_pinOrders != null && a.m_pinCovers != null && a.m_pinOrders.Length == a.m_pins.Length;
            bool hasB = b.m_pinOrders != null && b.m_pinCovers != null && b.m_pinOrders.Length == b.m_pins.Length;
            if (hasA != hasB) return false;
            if (!hasA) return true;
            if (a.m_pinOrders[i] != b.m_pinOrders[j]) return false;
            bool[] ca = a.m_pinCovers[i], cb = b.m_pinCovers[j];
            if (ca == null || cb == null) return ca == cb;
            if (ca.Length != cb.Length) return false;
            for (int k = 0; k < ca.Length; k++)
                if (ca[k] != cb[k]) return false;
            return true;
        }

        private static int PinKey(SheetPin pin) => pin.m_x.GetHashCode() * 31 + pin.m_z.GetHashCode();

        // Compares two lists by index (counts a and b; same tells entries alike, keyA and keyB
        // hash them) and calls changedA and changedB for the entries of either that the other
        // has not alike, matched as multisets, duplicates one for one. With ordered, an entry
        // matched out of the order of the others counts as changed too (in both lists). Lists
        // alike entry for entry cost one pass.
        internal static void Diff(int a, int b, bool ordered, Func<int, int, bool> same, Func<int, int> keyA, Func<int, int> keyB,
            Action<int> changedA, Action<int> changedB)
        {
            if (a == b)
            {
                bool all = true;
                for (int i = 0; i < a && all; i++) all = same(i, i);
                if (all) return;
            }
            var buckets = new Dictionary<int, List<int>>();
            for (int i = 0; i < a; i++)
            {
                int key = keyA(i);
                if (!buckets.TryGetValue(key, out List<int> bucket)) buckets[key] = bucket = new List<int>(1);
                bucket.Add(i);
            }
            var match = new int[a];
            for (int i = 0; i < a; i++) match[i] = -1;
            for (int j = 0; j < b; j++)
            {
                int found = -1;
                if (buckets.TryGetValue(keyB(j), out List<int> bucket))
                {
                    for (int k = 0; k < bucket.Count; k++)
                    {
                        if (!same(bucket[k], j)) continue;
                        found = bucket[k];
                        bucket.RemoveAt(k);
                        break;
                    }
                }
                if (found < 0) changedB(j);
                else match[found] = j;
            }
            int last = -1;
            for (int i = 0; i < a; i++)
            {
                if (match[i] < 0)
                {
                    changedA(i);
                    continue;
                }
                if (ordered && match[i] < last)
                {
                    changedA(i);
                    changedB(match[i]);
                }
                else
                {
                    last = match[i];
                }
            }
        }

        private int CellOf(float coordinate) => Mathf.Clamp((int)(coordinate / InkRecord.CellSize), 0, m_across - 1);

        // Whether a close-up of region (sheet pixels) at scale, drawn from m_from, can differ
        // from one drawn from the new record (record, which the test no longer needs to read).
        // The cells it picks are those InkRecord's queries over its region pick (its edges
        // inclusive), wider for the river strokes; a changed pin counts where it can take out
        // trees drawn in the region (PinAreas).
        public bool Affects(InkRecord record, RectInt region, int scale)
        {
            int x0 = CellOf(region.xMin), x1 = CellOf(region.xMax), y0 = CellOf(region.yMin), y1 = CellOf(region.yMax);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (m_cells[y * m_across + x]) return true;
            foreach (RectInt g in m_groups)
                if (g.xMin <= x1 && x0 <= g.xMax - 1 && g.yMin <= y1 && y0 <= g.yMax - 1) return true;
            float river = InkPainter.TileRiverReach;
            int rx0 = CellOf(region.xMin - river), rx1 = CellOf(region.xMax + river), ry0 = CellOf(region.yMin - river), ry1 = CellOf(region.yMax + river);
            for (int y = ry0; y <= ry1; y++)
                for (int x = rx0; x <= rx1; x++)
                    if (m_rivers[y * m_across + x]) return true;
            foreach (Rect r in m_reads)
                if (r.xMin <= region.xMax && region.xMin <= r.xMax && r.yMin <= region.yMax && region.yMin <= r.yMax) return true;
            if (ChangedPins == 0) return false;
            foreach (Rect r in PinAreas(scale))
                if (r.xMin <= region.xMax && region.xMin <= r.xMax && r.yMin <= region.yMax && region.yMin <= r.yMax) return true;
            return false;
        }
    }
}
